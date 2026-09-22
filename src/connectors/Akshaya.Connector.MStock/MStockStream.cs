using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly Func<CancellationToken, Task<Result>>? _ensureInstruments;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Packet lengths already logged once — see <see cref="DecodePacket"/>. Unlocked: only the
    /// receive loop touches it, and there is one receive loop at a time.
    /// </summary>
    private readonly HashSet<int> _seenPacketLengths = [];

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
    /// <param name="options">Endpoint and limits.</param>
    /// <param name="session">The session the socket authenticates with.</param>
    /// <param name="instruments">The script master: the socket speaks numeric tokens only.</param>
    /// <param name="clock">Stamps ticks and drives the staleness watchdog.</param>
    /// <param name="ensureInstruments">Loads the script master if this process has not yet.
    /// Without it, the first subscription after a restart could never resolve a token.</param>
    /// <param name="logger">State changes, broker text frames and first-seen packet shapes —
    /// what it takes to tell "mStock refused us" from "the market is quiet".</param>
    public MStockStream(
        MStockOptions options,
        BrokerSession session,
        IMStockInstrumentLookup instruments,
        IClock clock,
        Func<CancellationToken, Task<Result>>? ensureInstruments = null,
        ILogger? logger = null)
    {
        _options = options;
        _session = session;
        _instruments = instruments;
        _ensureInstruments = ensureInstruments;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
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

        // A token miss on a cold process means the script master is not loaded yet, not that the
        // instrument does not exist. Load it (once, process-wide) before deciding.
        if (_ensureInstruments is not null
            && instruments.Any(instrument => !_instruments.TryGetToken(instrument, out _)))
        {
            var loaded = await _ensureInstruments(ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                return loaded;
            }
        }

        var tokens = new List<uint>(instruments.Count);
        foreach (var instrument in instruments)
        {
            if (!_instruments.TryGetToken(instrument, out var token))
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"mStock does not list {instrument.Symbol}, so there is no live price for it.",
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

                // Ping/pong liveness, as mStock's own SDK does (a ping every 2.5s, dropped when
                // no pong comes back). Without it a half-open TCP connection — a NAT timeout, a
                // laptop waking from sleep — reads as a quiet market until the idle timeout.
                socket.Options.KeepAliveInterval = _options.StreamKeepAlive;
                socket.Options.KeepAliveTimeout = _options.StreamKeepAlive * 2;

                // The handshake the working clients send. Python's websocket-client (under both
                // mStock's SDK and OpenAlgo) adds an Origin of the socket's own host; .NET sends
                // none and no User-Agent. mStock's servers drop a handshake they will not accept,
                // and their AWS load balancer reports that as a bare 502, so nothing short of
                // matching a known-good client says which detail they object to.
                socket.Options.SetRequestHeader("Origin", $"https://{_options.StreamUrl.Host}");
                socket.Options.SetRequestHeader("User-Agent", "Akshaya");
                socket.Options.CollectHttpResponseDetails = true;

                try
                {
                    await socket.ConnectAsync(BuildStreamUri(), ct).ConfigureAwait(false);
                }
                catch (WebSocketException) when (socket.HttpStatusCode != 0)
                {
                    // The refused handshake's status and server are the only diagnostics mStock
                    // gives. Logged here; the reconnect path below reports the failure itself.
                    _logger.LogWarning(
                        "mStock refused the stream handshake: HTTP {Status}, server {Server}.",
                        (int)socket.HttpStatusCode,
                        socket.HttpResponseHeaders?.TryGetValue("Server", out var server) == true
                            ? string.Join(',', server)
                            : "-");
                    throw;
                }

                // mStock drops the session "shortly after" connecting unless this is the first
                // thing sent (Market Data docs, "Connection Maintenance"; the SDK's
                // send_login_after_connect). Sent BEFORE the socket is published below, so a
                // concurrent SubscribeAsync cannot get a message in ahead of it.
                await SendTextAsync(socket, $"LOGIN:{_session.AccessToken}", ct).ConfigureAwait(false);

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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // EVERY other failure reconnects, including the receive loop's idle timeout. This
                // used to list the exception types it expected, and anything else — that idle
                // timeout, then an OperationCanceledException, among them — escaped the loop and
                // faulted the pump: no reconnect, no final state, the event channel never
                // completed, and a feed that still read "Connected" while delivering nothing. A
                // price feed that stops silently is the one failure this class exists to prevent.
                var reason = ex is TimeoutException or WebSocketException
                    ? ex.Message
                    : $"{ex.GetType().Name}: {ex.Message}";

                if (!reported)
                {
                    reported = true;
                    firstConnect.TrySetResult(Result.Failure(new Error(
                        ConnectorErrorCodes.BrokerUnavailable,
                        "Could not open the mStock streaming socket.",
                        ex.GetType().Name,
                        ex.Message)));
                }

                SetState(StreamState.Reconnecting, reason);
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
                try
                {
                    result = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Named, so the reconnect below says what actually happened: the socket went
                    // quiet. Every other failure reports its own message.
                    throw new TimeoutException(
                        $"No data from mStock for {_options.StreamIdleTimeout.TotalSeconds:0}s; reconnecting.");
                }

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

    /// <summary>
    /// <c>wss://ws.mstock.trade?ACCESS_TOKEN=…&amp;API_KEY=…</c> — upper-case names, and the
    /// session's ACCESS token (Market Data docs; the SDK's <c>MTicker</c>). This used to send
    /// Kite's lower-case <c>api_key</c>/<c>access_token</c> and prefer the session's
    /// <c>enctoken</c>, and mStock rejects that handshake.
    ///
    /// The values go in RAW, as the SDK's plain string format puts them, not percent-encoded:
    /// a key containing <c>+</c>, <c>/</c> or <c>=</c> would otherwise reach mStock as something
    /// it never issued. <see cref="Uri"/> still escapes anything that cannot appear in a URL.
    /// </summary>
    private Uri BuildStreamUri()
    {
        var apiKey = _session.Extras.GetValueOrDefault(MStockSessionKeys.ApiKey) ?? string.Empty;
        var root = _options.StreamUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return new Uri($"{root}?ACCESS_TOKEN={_session.AccessToken}&API_KEY={apiKey}");
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

        try
        {
            foreach (var message in messages)
            {
                await SendTextAsync(socket, message, ct).ConfigureAwait(false);
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

        return Result.Success();
    }

    /// <summary>
    /// One text frame. Serialised: a WebSocket allows only one send in flight at a time.
    ///
    /// <paramref name="ct"/> bounds only the wait for the gate, never the send itself. Cancelling
    /// a <see cref="ClientWebSocket"/> send ABORTS THE SOCKET, and the tokens that reach here
    /// belong to whoever asked for a subscription — a page that was navigated away from, a
    /// connector timeout. Passing one through let any one caller giving up tear down the feed
    /// for every subscriber on the link. A send on a dead socket still ends: the keepalive or
    /// the idle timeout aborts it, and disconnecting disposes it.
    /// </summary>
    private async Task SendTextAsync(ClientWebSocket socket, string message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                    Encoding.UTF8.GetBytes(message),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
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

        // The broker's replies to LOGIN and to subscriptions are how a refused session shows
        // itself, and not all of them are JSON. The token is redacted in case one echoes it.
        _logger.LogInformation(
            "mStock stream text frame: {Message}",
            Truncate(message.Replace(_session.AccessToken, "***", StringComparison.Ordinal), 300));

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
    /// mStock's binary tick frame, which follows the Kite framing: a two-byte packet count,
    /// then for each packet a two-byte length followed by that many bytes of big-endian
    /// 32-bit fields. Packet length is what identifies the layout — see
    /// <see cref="DecodePacket"/>, where mStock's layouts depart from Kite's. Frames under two
    /// bytes are heartbeats.
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
            if (Interlocked.Increment(ref _unresolvedTicks) == 1)
            {
                _logger.LogWarning(
                    "mStock stream: tick for token {Token} is not in the script master; dropping it (and counting any more).",
                    token);
            }

            return;
        }

        if (_seenPacketLengths.Add(packet.Length))
        {
            _logger.LogInformation(
                "mStock stream: first {Length}-byte packet (token {Token}, {Instrument}).",
                packet.Length,
                token,
                instrument);
        }

        var now = _clock.UtcNow;
        var last = Price(ReadInt(packet, 1));

        // Packet length is the only thing that says which layout this is. The layouts below
        // are mStock's own SDK parser (MiraeAsset-mStock/pytradingapi-typeA, mticker.py), which
        // is what runs against the live socket, checked field by field against the Market
        // Data docs. `ReadInt(packet, n)` reads the n-th FOUR-BYTE field, i.e. bytes 4n..4n+4.
        var tick = packet.Length switch
        {
            // 8 bytes: token + last price. LTP mode.
            8 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                Timestamp = now,
            },

            // 28 or 32 bytes: the Kite-shaped index packet (32 adds the exchange time).
            28 or 32 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                High = Price(ReadInt(packet, 2)),
                Low = Price(ReadInt(packet, 3)),
                Open = Price(ReadInt(packet, 4)),
                PreviousClose = Price(ReadInt(packet, 5)),
                Timestamp = packet.Length >= 32 ? ExchangeTime(ReadInt(packet, 7), now) : now,
            },

            // 48 bytes: mStock's own index packet. It has to be matched BEFORE the `>= 44`
            // arm below, which used to swallow it and read an index's circuit limits and
            // 52-week range as its open, high, low and close. OHLC order is the SDK's (open,
            // high, low, close); the docs page lists high, low, open, close, but the SDK is
            // the code that is run against the live socket. Bytes 28-32 are the exchange time
            // in both.
            48 => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                Open = Price(ReadInt(packet, 2)),
                High = Price(ReadInt(packet, 3)),
                Low = Price(ReadInt(packet, 4)),
                PreviousClose = Price(ReadInt(packet, 5)),
                Timestamp = ExchangeTime(ReadInt(packet, 7), now),
            },

            // 44 bytes: a tradable instrument's quote. 184 appends the five-level book from
            // byte 64, and 200 appends circuit limits and the 52-week range after that.
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

                // Best bid and ask: the price of the first bid entry (byte 68) and of the first
                // ask entry (byte 128). The ask used to be read from byte 188 — the lower
                // circuit limit on a 200-byte packet, past the end on a 184-byte one.
                BidPrice = packet.Length >= 184 ? Price(ReadInt(packet, 17)) : null,
                AskPrice = packet.Length >= 184 ? Price(ReadInt(packet, 32)) : null,

                // The exchange time is field 15 (bytes 60-64). This used to read field 44 —
                // byte 176, an ask quantity — as a Unix time, so every full tick was stamped
                // in 1970 and looked hours stale the moment it arrived.
                Timestamp = packet.Length >= 184 ? ExchangeTime(ReadInt(packet, 15), now) : now,
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

    /// <summary>
    /// mStock's tick times count seconds from 1980-01-01 UTC, NOT the Unix epoch — the SDK's
    /// <c>convert_from_unix_timestamp(..., year=1980)</c>, despite its name. Read as Unix
    /// time, every tick lands ten years in the past and every staleness check calls a live
    /// feed dead. Zero means the exchange sent no time, so the receive time stands in.
    /// </summary>
    private static DateTimeOffset ExchangeTime(int seconds, DateTimeOffset fallback) =>
        seconds > 0 ? MStockEpoch.AddSeconds(seconds) : fallback;

    private static readonly DateTimeOffset MStockEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Money Price(int paise) => new(paise / PaiseDivisor, Currency.Inr);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

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
            _logger.LogInformation("mStock stream {State}: {Reason}", state, reason ?? "-");
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
