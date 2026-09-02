using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// The mStock streaming socket.
///
/// Three things about this class are worth reading before changing it.
///
/// <b>Reconnection is the feature.</b> A market-data socket that works until it drops is not
/// useful; the whole value is in what happens afterwards. This reconnects with exponential
/// backoff AND jitter, and re-sends the full subscription set on every reconnect. Without the
/// jitter, a broker-side restart brings every one of our sockets back in the same millisecond
/// and mStock throttles the lot; without the re-subscribe, the socket comes back connected and
/// silent, which looks healthy and is worse than being down.
///
/// <b>Ticks carry a numeric token and nothing else.</b> Resolving them needs the script master
/// (<see cref="IMStockInstrumentLookup"/>). A tick whose token we cannot resolve is dropped
/// rather than guessed at, and counted, because a guessed instrument is a price posted against
/// the wrong contract.
///
/// <b>The consumer must never back-pressure the socket.</b> The contract says so, and the
/// channel below is bounded with drop-oldest for exactly that reason: a slow consumer must
/// lose stale prices, not stall ingest for every other subscriber on the connection.
/// </summary>
public sealed class MStockStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>
    /// Ticks are conflated by the fan-out layer anyway, so a deep buffer buys nothing but
    /// latency. When this fills, the oldest tick — the least useful one — is dropped.
    /// </summary>
    private const int EventBufferCapacity = 4096;

    /// <summary>Indian equity prices arrive as integer paise.</summary>
    private const decimal PaiseDivisor = 100m;

    private readonly MStockOptions _options;
    private readonly BrokerSession _session;
    private readonly IMStockInstrumentLookup _instruments;
    private readonly IClock _clock;

    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

    /// <summary>The subscription set we WANT. Replayed verbatim after every reconnect.</summary>
    private readonly Dictionary<uint, StreamMode> _desired = [];

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _stateGate = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private DateTimeOffset _lastMessageAt;
    private long _unresolvedTicks;

    /// <summary>Creates the streaming facet.</summary>
    public MStockStream(
        MStockOptions options,
        BrokerSession session,
        IMStockInstrumentLookup instruments,
        IClock clock)
    {
        _options = options;
        _session = session;
        _instruments = instruments;
        _clock = clock;
        _lastMessageAt = clock.UtcNow;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>Ticks discarded because their instrument token is not in the script master.</summary>
    public long UnresolvedTicks => Interlocked.Read(ref _unresolvedTicks);

    /// <inheritdoc />
    public async Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        lock (_stateGate)
        {
            if (_pump is { IsCompleted: false })
            {
                return Result.Success();
            }
        }

        var cts = new CancellationTokenSource();
        var firstConnect = new TaskCompletionSource<Result>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_stateGate)
        {
            _cts = cts;
            _pump = Task.Run(() => RunAsync(firstConnect, cts.Token), CancellationToken.None);
        }

        // Report the FIRST connection attempt honestly to the caller: the link wizard and the
        // health check both need to know whether the socket came up, and a fire-and-forget
        // connect would report success while the socket was still failing DNS.
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
                // Caller gave up waiting for a clean shutdown. The pump observes its own token
                // and will finish regardless; there is nothing to report.
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
        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        var tokens = new List<uint>(instruments.Count);
        foreach (var instrument in instruments)
        {
            if (!_instruments.TryGetToken(instrument, out var token))
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"mStock subscribes by numeric instrument token and none is known for "
                    + $"{instrument}. Load the script master before subscribing.",
                    VendorCode: null,
                    VendorMessage: null,
                    Context: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["instrument"] = instrument.ToString(),
                    }));
            }

            tokens.Add(token);
        }

        lock (_stateGate)
        {
            foreach (var token in tokens)
            {
                _desired[token] = mode;
            }

            if (_desired.Count > _options.MaxStreamSubscriptionsOrDefault())
            {
                // Overshooting mStock's cap does not fail loudly at the broker — it silently
                // stops delivering, which is the worst possible failure mode for a price feed.
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"That would take the mStock subscription set to {_desired.Count}, past the "
                    + $"{_options.MaxStreamSubscriptionsOrDefault()} the broker allows on one socket. "
                    + "The fan-out layer must open another connection or drop a subscription."));
            }
        }

        return await SendSubscriptionAsync(tokens, mode, subscribe: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        var tokens = new List<uint>(instruments.Count);
        foreach (var instrument in instruments)
        {
            if (_instruments.TryGetToken(instrument, out var token))
            {
                tokens.Add(token);
            }
        }

        lock (_stateGate)
        {
            foreach (var token in tokens)
            {
                _desired.Remove(token);
            }
        }

        return tokens.Count == 0
            ? Result.Success()
            : await SendSubscriptionAsync(tokens, StreamMode.Ltp, subscribe: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default) =>
        _events.Reader.ReadAllAsync(ct);

    // --- the pump ---------------------------------------------------------------------------

    private async Task RunAsync(TaskCompletionSource<Result> firstConnect, CancellationToken ct)
    {
        var attempt = 0;
        var reported = false;

        while (!ct.IsCancellationRequested)
        {
            SetState(attempt == 0 ? StreamState.Connecting : StreamState.Reconnecting, null);

            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                await socket.ConnectAsync(BuildStreamUri(), ct).ConfigureAwait(false);

                lock (_stateGate)
                {
                    _socket = socket;
                }

                attempt = 0;
                _lastMessageAt = _clock.UtcNow;
                SetState(StreamState.Connected, null);

                if (!reported)
                {
                    reported = true;
                    firstConnect.TrySetResult(Result.Success());
                }

                // Everything we were subscribed to before the drop has to be asked for again.
                // mStock keeps no server-side subscription state across a reconnect.
                await ResubscribeAsync(ct).ConfigureAwait(false);

                await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
                                           or InvalidOperationException or ObjectDisposedException)
            {
                if (!reported)
                {
                    reported = true;
                    firstConnect.TrySetResult(Result.Failure(new Error(
                        ConnectorErrorCodes.BrokerUnavailable,
                        "Could not open the mStock streaming socket.",
                        ex.GetType().Name,
                        ex.Message)));
                }

                SetState(StreamState.Reconnecting, ex.Message);
            }
            finally
            {
                lock (_stateGate)
                {
                    _socket = null;
                }

                socket?.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            if (_options.MaxReconnectAttempts > 0 && attempt > _options.MaxReconnectAttempts)
            {
                SetState(
                    StreamState.Disconnected,
                    $"Gave up after {_options.MaxReconnectAttempts} reconnect attempts.");
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

        // A connect that never succeeded must still complete the caller's task, or ConnectAsync
        // waits forever on a socket that was cancelled before it came up.
        firstConnect.TrySetResult(Result.Failure(new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            "The mStock streaming socket stopped before it connected.")));

        SetState(StreamState.Disconnected, null);
        _events.Writer.TryComplete();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_options.StreamIdleTimeout);

            using var payload = new MemoryStream(capacity: 8 * 1024);
            WebSocketReceiveResult result;

            do
            {
                result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SetState(StreamState.Reconnecting, "mStock closed the socket.");
                    return;
                }

                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            _lastMessageAt = _clock.UtcNow;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                DispatchBinary(payload.GetBuffer().AsSpan(0, (int)payload.Length));
            }
            else
            {
                DispatchText(Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length));
            }
        }
    }

    /// <summary>
    /// Exponential backoff with full jitter.
    ///
    /// The jitter is not decoration. Every socket in the fleet drops at the same instant when
    /// mStock restarts, and an un-jittered backoff has them all retry in lockstep forever —
    /// a self-inflicted thundering herd against a broker that is already struggling.
    /// </summary>
    private TimeSpan BackoffDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 16);
        var baseMs = _options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(baseMs, _options.MaxReconnectDelay.TotalMilliseconds);

        var jitterRange = cappedMs * _options.ReconnectJitter;
        var jitter = (Random.Shared.NextDouble() * 2d - 1d) * jitterRange;

        return TimeSpan.FromMilliseconds(Math.Max(100d, cappedMs + jitter));
    }

    private Uri BuildStreamUri()
    {
        var apiKey = _session.Extras.GetValueOrDefault(MStockSessionKeys.ApiKey) ?? string.Empty;

        // The socket authenticates with the enctoken when there is one — it is issued
        // specifically for the feed — and falls back to the access token otherwise.
        var token = _session.Extras.GetValueOrDefault(MStockSessionKeys.EncToken)
                    ?? _session.AccessToken;

        var builder = new UriBuilder(_options.StreamUrl)
        {
            Query = $"api_key={Uri.EscapeDataString(apiKey)}&access_token={Uri.EscapeDataString(token)}",
        };

        return builder.Uri;
    }

    private async Task ResubscribeAsync(CancellationToken ct)
    {
        List<KeyValuePair<uint, StreamMode>> snapshot;
        lock (_stateGate)
        {
            snapshot = [.. _desired];
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        // Group by mode so each mode is one message rather than one message per instrument.
        foreach (var group in snapshot.GroupBy(pair => pair.Value))
        {
            var tokens = group.Select(pair => pair.Key).ToList();
            var sent = await SendSubscriptionAsync(tokens, group.Key, subscribe: true, ct)
                .ConfigureAwait(false);

            if (sent.IsFailure)
            {
                // Connected but not fully subscribed is exactly what Degraded is for. The UI
                // shows a stale-data banner rather than pretending the feed is healthy.
                SetState(StreamState.Degraded, sent.Error.Message);
                return;
            }
        }
    }

    private async Task<Result> SendSubscriptionAsync(
        IReadOnlyList<uint> tokens,
        StreamMode mode,
        bool subscribe,
        CancellationToken ct)
    {
        ClientWebSocket? socket;
        lock (_stateGate)
        {
            socket = _socket;
        }

        if (socket is null || socket.State != WebSocketState.Open)
        {
            // Not an error: the desired set has been recorded and will be replayed the moment
            // the socket comes back. Failing here would make a mid-reconnect subscribe look
            // like a permanent failure to the caller.
            return Result.Success();
        }

        var action = subscribe ? "subscribe" : "unsubscribe";
        var messages = new List<string>(2)
        {
            BuildActionMessage(action, tokens),
        };

        if (subscribe)
        {
            messages.Add(BuildModeMessage(mode, tokens));
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var message in messages)
            {
                await socket.SendAsync(
                        Encoding.UTF8.GetBytes(message),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                       or InvalidOperationException)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                "Could not send a subscription message to mStock.",
                ex.GetType().Name,
                ex.Message));
        }
        finally
        {
            _sendGate.Release();
        }

        return Result.Success();
    }

    private static string BuildActionMessage(string action, IReadOnlyList<uint> tokens) =>
        $$"""{"a":"{{action}}","v":[{{string.Join(',', tokens)}}]}""";

    private static string BuildModeMessage(StreamMode mode, IReadOnlyList<uint> tokens) =>
        $$"""{"a":"mode","v":["{{NativeMode(mode)}}",[{{string.Join(',', tokens)}}]]}""";

    private static string NativeMode(StreamMode mode) => mode switch
    {
        StreamMode.Ltp => "ltp",
        StreamMode.Quote => "quote",
        StreamMode.Full => "full",
        _ => "quote",
    };

    // --- frame decoding ---------------------------------------------------------------------

    /// <summary>
    /// Control and order-update frames arrive as text JSON on the same socket as the binary
    /// ticks. Order updates in particular MUST be handled here: they are how a fill is learned
    /// about seconds before the order book would show it.
    /// </summary>
    private void DispatchText(string message)
    {
        if (message.Length == 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = document.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "order":
                    // The payload is the same shape as an order-book row. It is published as a
                    // raw envelope rather than parsed here so that order mapping lives in
                    // exactly one place; the host reconciles it against the order book anyway.
                    Publish(new StreamEvent.ConnectionChanged(
                        State,
                        "Order update received on the stream; reconcile the order book."));
                    break;

                case "error":
                    var reason = document.RootElement.TryGetProperty("data", out var data)
                        ? data.ToString()
                        : message;
                    SetState(StreamState.Degraded, reason);
                    break;

                default:
                    // Heartbeats and subscription acknowledgements. Their only significance is
                    // that they refresh the idle timer, which the caller already did.
                    break;
            }
        }
        catch (JsonException)
        {
            // A frame we cannot parse is not worth tearing the connection down for.
        }
    }

    /// <summary>
    /// mStock's binary tick frame, which follows the Kite wire format: a two-byte packet count,
    /// then for each packet a two-byte length followed by that many bytes of big-endian
    /// signed 32-bit fields. Packet length is what identifies the mode.
    /// </summary>
    private void DispatchBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return;
        }

        var packets = BinaryPrimitives.ReadInt16BigEndian(frame);
        var offset = 2;

        for (var i = 0; i < packets && offset + 2 <= frame.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt16BigEndian(frame[offset..]);
            offset += 2;

            if (length <= 0 || offset + length > frame.Length)
            {
                return;
            }

            DecodePacket(frame.Slice(offset, length));
            offset += length;
        }
    }

    private void DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
        {
            return;
        }

        var token = (uint)BinaryPrimitives.ReadInt32BigEndian(packet);
        if (!_instruments.TryGetByToken(token, out var instrument))
        {
            // Counted, not guessed. A token we do not know is a stale script master, and the
            // ingest job alarms on this counter rather than us inventing an instrument.
            Interlocked.Increment(ref _unresolvedTicks);
            return;
        }

        var now = _clock.UtcNow;
        var last = Price(ReadInt(packet, 1));

        var tick = packet.Length switch
        {
            // 8 bytes: token + last price. The LTP mode most watchlists use.
            8 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                Timestamp = now,
            },

            // 28 or 32 bytes: an index packet. Indices have no traded volume or book, so the
            // fields after the OHLC block are absent and must not be read as if they were.
            28 or 32 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                High = Price(ReadInt(packet, 2)),
                Low = Price(ReadInt(packet, 3)),
                Open = Price(ReadInt(packet, 4)),
                PreviousClose = Price(ReadInt(packet, 5)),
                Timestamp = packet.Length >= 32
                    ? DateTimeOffset.FromUnixTimeSeconds(ReadInt(packet, 7))
                    : now,
            },

            // 44 bytes and up: a tradable instrument's quote packet, and 184 bytes is the same
            // thing with the five-level book appended.
            >= 44 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                LastQuantity = new Quantity(ReadInt(packet, 2)),
                Volume = ReadInt(packet, 4),
                Open = Price(ReadInt(packet, 7)),
                High = Price(ReadInt(packet, 8)),
                Low = Price(ReadInt(packet, 9)),
                PreviousClose = Price(ReadInt(packet, 10)),
                OpenInterest = packet.Length >= 184 ? ReadInt(packet, 12) : null,
                BidPrice = packet.Length >= 184 ? Price(ReadInt(packet, 17)) : null,
                AskPrice = packet.Length >= 184 ? Price(ReadInt(packet, 47)) : null,
                Timestamp = packet.Length >= 184
                    ? DateTimeOffset.FromUnixTimeSeconds(ReadInt(packet, 44))
                    : now,
            },

            _ => null,
        };

        if (tick is null)
        {
            return;
        }

        Publish(new StreamEvent.TickReceived(tick));

        if (packet.Length >= 184)
        {
            Publish(new StreamEvent.DepthReceived(DecodeDepth(instrument, packet, tick.Timestamp)));
        }
    }

    /// <summary>
    /// The five-level book that follows the quote block in a 184-byte packet: ten entries of
    /// twelve bytes each (quantity int32, price int32, order count int16, two bytes padding),
    /// bids first then asks.
    /// </summary>
    private static MarketDepth DecodeDepth(
        InstrumentKey instrument,
        ReadOnlySpan<byte> packet,
        DateTimeOffset timestamp)
    {
        const int DepthOffset = 64;
        const int EntrySize = 12;
        const int Levels = 5;

        var bids = new List<DepthLevel>(Levels);
        var asks = new List<DepthLevel>(Levels);

        for (var i = 0; i < Levels * 2; i++)
        {
            var start = DepthOffset + (i * EntrySize);
            if (start + EntrySize > packet.Length)
            {
                break;
            }

            var entry = packet.Slice(start, EntrySize);
            var quantity = BinaryPrimitives.ReadInt32BigEndian(entry);
            var price = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var orders = BinaryPrimitives.ReadInt16BigEndian(entry[8..]);

            var level = new DepthLevel(
                new Money(price / PaiseDivisor, Currency.Inr),
                new Quantity(quantity),
                orders);

            if (i < Levels)
            {
                bids.Add(level);
            }
            else
            {
                asks.Add(level);
            }
        }

        return new MarketDepth
        {
            Instrument = instrument,
            Bids = bids,
            Asks = asks,
            Timestamp = timestamp,
        };
    }

    private static int ReadInt(ReadOnlySpan<byte> packet, int index) =>
        BinaryPrimitives.ReadInt32BigEndian(packet[(index * 4)..]);

    private static Money Price(int paise) => new(paise / PaiseDivisor, Currency.Inr);

    private void Publish(StreamEvent evt) => _events.Writer.TryWrite(evt);

    private void SetState(StreamState state, string? reason)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = State != state;
            State = state;
        }

        if (changed || reason is not null)
        {
            Publish(new StreamEvent.ConnectionChanged(state, reason));
        }
    }

    /// <summary>How long the socket has been silent. Surfaced by the connector's health check.</summary>
    public TimeSpan SilentFor => _clock.UtcNow - _lastMessageAt;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _sendGate.Dispose();
    }
}

/// <summary>Options the stream reads that are not part of the REST surface.</summary>
internal static class MStockStreamOptionExtensions
{
    /// <summary>
    /// mStock's documented per-socket subscription cap. Kept next to the stream rather than in
    /// the manifest reader so the stream can enforce it without a manifest in hand.
    /// </summary>
    public const int DefaultMaxStreamSubscriptions = 1000;

    /// <summary>The cap, defaulted when the options object does not override it.</summary>
    public static int MaxStreamSubscriptionsOrDefault(this MStockOptions options) =>
        options.MaxStreamSubscriptions > 0
            ? options.MaxStreamSubscriptions
            : DefaultMaxStreamSubscriptions;
}
