using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// IBKR's live prices, on the gateway's websocket.
///
/// A subscription is a text message, <c>smd+CONID+{"fields":[…]}</c>, and each push carries only the fields that
/// changed. The latest value of every field is therefore kept per conid, and each push publishes a whole tick.
///
/// THE DESIRED SUBSCRIPTION SET IS THE SOURCE OF TRUTH. Subscriptions belong to a connection, so a reconnect starts
/// with none; <see cref="_desired"/> is replayed every time the socket comes back.
///
/// THE SESSION IS KEPT ALIVE HERE TOO. The gateway drops a quiet websocket, so <c>tic</c> is sent on
/// <see cref="IbkrOptions.StreamHeartbeat"/>, and an <c>sts</c> message saying the brokerage session signed out
/// turns the state Degraded, which is what the UI's stale-data banner shows.
///
/// ORDER UPDATES ARE NOT STREAMED. The gateway's order topic is not described in the documentation this connector
/// was written from; orders are read from the book, and reconciliation catches changes.
/// </summary>
public sealed class IbkrStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>Ticks are conflated downstream; when this fills, the oldest event is the one dropped.</summary>
    private const int EventBufferCapacity = 4096;

    private readonly IbkrOptions _options;
    private readonly IbkrChannel _channel;
    private readonly IbkrInstrumentResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>The subscriptions we WANT, keyed by conid. Replayed after every reconnect.</summary>
    private readonly Dictionary<long, Subscription> _desired = [];

    /// <summary>The latest value of each field per conid, because a push carries only what changed.</summary>
    private readonly Dictionary<long, Dictionary<string, string?>> _latest = [];

    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    internal IbkrStream(
        IbkrOptions options,
        IbkrChannel channel,
        IbkrInstrumentResolver resolver,
        IClock clock,
        ILogger logger)
    {
        _options = options;
        _channel = channel;
        _resolver = resolver;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>Conids currently wanted. Exposed so a conformance suite can prove no leak.</summary>
    public int SubscriptionCount
    {
        get
        {
            lock (_stateGate)
            {
                return _desired.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        var firstConnect = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_stateGate)
        {
            // A supervisor calls Connect speculatively; a running pump must not get a second socket.
            if (_pump is { IsCompleted: false })
            {
                return Result.Success();
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _pump = Task.Run(() => RunAsync(firstConnect, cts.Token), CancellationToken.None);
        }

        using var registration = ct.Register(() => firstConnect.TrySetResult(
            Result.Failure(new Error(ConnectorErrorCodes.Timeout, "The stream connect was cancelled."))));

        return await firstConnect.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Task? pump;

        lock (_stateGate)
        {
            cts = _cts;
            pump = _pump;
            _cts = null;
            _pump = null;

            // A disconnect releases what it took, so the next Connect does not silently re-subscribe.
            _desired.Clear();
            _latest.Clear();
        }

        if (cts is null)
        {
            return Result.Success();
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pump observes its own token and finishes regardless.
            }
        }

        cts.Dispose();
        SetState(StreamState.Disconnected, "Disconnected by the platform.");
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        if (mode == StreamMode.Full)
        {
            return Result.Failure(ConnectorErrors.NotSupported("Full mode on the IBKR stream; the book is not streamed"));
        }

        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        var wanted = new List<Subscription>(instruments.Count);
        foreach (var key in instruments)
        {
            var record = await _resolver.ForKeyAsync(_channel, key, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                return Result.Failure(record.Error);
            }

            if (!IbkrInstrumentResolver.Matches(key, record.Value.Definition.Key))
            {
                return Result.Failure(ConnectorErrors.InstrumentNotFound(key));
            }

            wanted.Add(new Subscription(key, record.Value.Conid, record.Value.Definition.Currency, mode));
        }

        ClientWebSocket? socket;

        lock (_stateGate)
        {
            var projected = _desired.Count + wanted.Select(w => w.Conid).Distinct().Count(c => !_desired.ContainsKey(c));
            if (projected > _options.MaxStreamSubscriptions)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"That would stream {projected} contracts; this IBKR username is configured for {_options.MaxStreamSubscriptions} market "
                    + "data lines. The fan-out layer must drop a subscription first."));
            }

            foreach (var subscription in wanted)
            {
                _desired[subscription.Conid] = subscription;
            }

            socket = _socket;
        }

        if (socket is not { State: WebSocketState.Open })
        {
            // Recorded, and replayed the moment the connection is up; not an error.
            return Result.Success();
        }

        foreach (var conid in wanted.Select(w => w.Conid).Distinct())
        {
            var sent = await SendAsync(socket, MarketDataMessage(conid), ct).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                // Still recorded: the reconnect this failure leads to replays it.
                return sent;
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(IReadOnlyCollection<InstrumentKey> instruments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var removed = new List<long>();
        ClientWebSocket? socket;

        lock (_stateGate)
        {
            foreach (var key in instruments)
            {
                var conid = _desired.Values.FirstOrDefault(s => s.Key == key)?.Conid;
                if (conid is { } id && _desired.Remove(id))
                {
                    _latest.Remove(id);
                    removed.Add(id);
                }
            }

            socket = _socket;
        }

        if (removed.Count == 0 || socket is not { State: WebSocketState.Open })
        {
            return Result.Success();
        }

        foreach (var conid in removed)
        {
            var sent = await SendAsync(socket, $"umd+{conid.ToString(CultureInfo.InvariantCulture)}+{{}}", ct).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                // The caller's intent is recorded; pushes for a conid no longer wanted are dropped, and the line is
                // released when the stream reconnects.
                _logger.LogWarning("{ConnectorId}: could not unsubscribe conid {Conid}: {Error}", IbkrAuth.ConnectorId, conid, sent.Error.ToString());
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default) => _events.Reader.ReadAllAsync(ct);

    // --- the pump ------------------------------------------------------------------------------------------------

    private async Task RunAsync(TaskCompletionSource<Result> firstConnect, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            SetState(attempt == 0 ? StreamState.Connecting : StreamState.Reconnecting, null);

            var opened = await OpenAsync(ct).ConfigureAwait(false);

            if (opened.IsSuccess)
            {
                var socket = opened.Value;
                attempt = 0;

                try
                {
                    lock (_stateGate)
                    {
                        _socket = socket;
                    }

                    SetState(StreamState.Connected, null);
                    firstConnect.TrySetResult(Result.Success());

                    await ResubscribeAsync(socket, ct).ConfigureAwait(false);
                    await PumpAsync(socket, ct).ConfigureAwait(false);

                    if (!ct.IsCancellationRequested)
                    {
                        SetState(StreamState.Reconnecting, socket.CloseStatusDescription ?? "The gateway's websocket closed.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    lock (_stateGate)
                    {
                        _socket = null;
                        _latest.Clear();
                    }

                    await CloseAsync(socket).ConfigureAwait(false);
                }
            }
            else
            {
                firstConnect.TrySetResult(Result.Failure(opened.Error));

                // A signed-out gateway can be signed in again by the operator, so this keeps trying, slowly.
                SetState(StreamState.Reconnecting, opened.Error.Message);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            if (_options.MaxReconnectAttempts > 0 && attempt > _options.MaxReconnectAttempts)
            {
                SetState(StreamState.Disconnected, $"Gave up after {_options.MaxReconnectAttempts} reconnect attempts.");
                break;
            }

            try
            {
                await Task.Delay(BackoffDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        firstConnect.TrySetResult(Result.Failure(new Error(
            ConnectorErrorCodes.GatewayUnavailable,
            "The IBKR stream stopped before it connected.")));

        SetState(StreamState.Disconnected, null);
    }

    private async Task<Result<ClientWebSocket>> OpenAsync(CancellationToken ct)
    {
        if (_channel.Gateway is not { } gateway)
        {
            return Result<ClientWebSocket>.Failure(IbkrErrors.NoGatewayAddress());
        }

        var api = _channel.Api();
        if (api.IsFailure)
        {
            return Result<ClientWebSocket>.Failure(api.Error);
        }

        var tickle = await api.Value.PostAsync<IbkrTickle>("tickle", body: null, ct).ConfigureAwait(false);
        if (tickle.IsFailure)
        {
            return Result<ClientWebSocket>.Failure(tickle.Error);
        }

        if (tickle.Value.Server?.AuthStatus is not { Authenticated: true })
        {
            return Result<ClientWebSocket>.Failure(IbkrErrors.SignedOut(gateway));
        }

        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = IbkrCertificates.Validator(IbkrCertificates.Trusts(gateway, _options));
        socket.Options.SetRequestHeader("User-Agent", "Akshaya/1.0");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        if (tickle.Value.Session is { Length: > 0 } session)
        {
            socket.Options.SetRequestHeader("Cookie", $"api={session}");
        }

        var uri = new Uri($"wss://{IbkrCertificates.Authority(gateway)}/{_options.BasePath.Trim('/')}/ws");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.ConnectTimeout);

        try
        {
            await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            socket.Dispose();
            return Result<ClientWebSocket>.Failure(IbkrErrorMapper.MapException(ex, "the stream connection"));
        }

        return socket;
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(socket, beat.Token);
        var buffer = new byte[16 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;

                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, received.Count);
                    if (message.Length > _options.MaxFrameBytes)
                    {
                        throw new InvalidDataException($"An IBKR stream message exceeded {_options.MaxFrameBytes} bytes.");
                    }
                }
                while (!received.EndOfMessage);

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
        {
            _logger.LogWarning(ex, "{ConnectorId}: the IBKR stream failed.", IbkrAuth.ConnectorId);
        }
        finally
        {
            await beat.CancelAsync().ConfigureAwait(false);

            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.StreamHeartbeat);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if ((await SendAsync(socket, "tic", ct).ConfigureAwait(false)).IsFailure)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Heartbeat echoes and other plain-text frames.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var topic = IbkrApi.Text(root, "topic") ?? string.Empty;

            if (topic.StartsWith("smd+", StringComparison.Ordinal))
            {
                var row = IbkrFieldRow.From(root);
                if ((row.Conid ?? IbkrNumber.Integer(topic[4..])) is { } conid)
                {
                    PublishTick(conid, row);
                }
            }
            else if (string.Equals(topic, "sts", StringComparison.Ordinal)
                     && root.TryGetProperty("args", out var args)
                     && args.ValueKind == JsonValueKind.Object
                     && args.TryGetProperty("authenticated", out var authenticated))
            {
                if (authenticated.ValueKind == JsonValueKind.False)
                {
                    SetState(StreamState.Degraded, "The Client Portal Gateway's brokerage session signed out; prices have stopped.");
                }
                else if (authenticated.ValueKind == JsonValueKind.True && State == StreamState.Degraded)
                {
                    SetState(StreamState.Connected, "The gateway's brokerage session is signed in again.");
                }
            }
        }
    }

    private void PublishTick(long conid, IbkrFieldRow row)
    {
        Subscription? subscription;
        Dictionary<string, string?> snapshot;

        lock (_stateGate)
        {
            // A push for something unsubscribed a moment ago is not the caller's any more.
            if (!_desired.TryGetValue(conid, out subscription))
            {
                return;
            }

            if (!_latest.TryGetValue(conid, out var latest))
            {
                latest = new Dictionary<string, string?>(StringComparer.Ordinal);
                _latest[conid] = latest;
            }

            foreach (var field in IbkrMaps.StreamFields.Append("_updated"))
            {
                if (row.Has(field))
                {
                    latest[field] = row[field];
                }
            }

            snapshot = new Dictionary<string, string?>(latest, StringComparer.Ordinal);
        }

        if (IbkrMarketData.ToTick(subscription.Key, subscription.Currency, field => snapshot.GetValueOrDefault(field), _clock.UtcNow) is { } tick)
        {
            Publish(new StreamEvent.TickReceived(tick));
        }
    }

    private async Task ResubscribeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        List<long> conids;
        lock (_stateGate)
        {
            conids = [.. _desired.Keys];
        }

        foreach (var conid in conids)
        {
            var sent = await SendAsync(socket, MarketDataMessage(conid), ct).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                // Connected but not fully subscribed is exactly what Degraded means.
                SetState(StreamState.Degraded, $"Connected, but re-subscribing failed: {sent.Error.Message}");
                return;
            }
        }
    }

    private async Task<Result> SendAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        try
        {
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            return Result.Failure(IbkrErrorMapper.MapException(ex, "the stream"));
        }

        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return Result.Failure(IbkrErrorMapper.MapException(ex, "the stream"));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task CloseAsync(ClientWebSocket socket)
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort: the connection is going away regardless.
            }
        }

        socket.Dispose();
    }

    private static string MarketDataMessage(long conid) =>
        $"smd+{conid.ToString(CultureInfo.InvariantCulture)}+{{\"fields\":[{string.Join(",", IbkrMaps.StreamFields.Select(f => $"\"{f}\""))}]}}";

    /// <summary>Exponential backoff with jitter, so every stream on a restarted gateway does not return in the same millisecond.</summary>
    private TimeSpan BackoffDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 16);
        var baseMs = _options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(baseMs, _options.MaxReconnectDelay.TotalMilliseconds);
        var jitter = ((Random.Shared.NextDouble() * 2d) - 1d) * cappedMs * _options.ReconnectJitter;

        return TimeSpan.FromMilliseconds(Math.Max(100d, cappedMs + jitter));
    }

    private void Publish(StreamEvent evt) => _events.Writer.TryWrite(evt);

    private void SetState(StreamState state, string? reason)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = State != state;
            State = state;
        }

        if (changed)
        {
            Publish(new StreamEvent.ConnectionChanged(state, reason));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _events.Writer.TryComplete();
        _sendGate.Dispose();
    }

    /// <summary>One wanted subscription.</summary>
    private sealed record Subscription(InstrumentKey Key, long Conid, Currency Currency, StreamMode Mode);
}
