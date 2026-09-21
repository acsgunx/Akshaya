using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Kite's live feed: binary market data and JSON order postbacks on one WebSocket.
///
/// This is the facet that most distinguishes Kite from the other Indian brokers in this
/// repository. Its wire protocol is fully published — packet lengths, byte offsets, the lot — so
/// the feed can be implemented against the documentation rather than against a vendor SDK, and
/// order updates arrive on the same socket seconds before the order book would show them.
///
/// Three things here are worth understanding before changing anything.
///
/// OFFSETS ARE NAMED BYTE CONSTANTS, not arithmetic on a word index. The layout in
/// <see cref="Layout"/> is a transcription of the documented table and can be read against it line
/// by line. That is deliberate: an index-times-four scheme reads plausibly while being wrong, and
/// a decoder that runs off the end of a packet throws somewhere the pump does not catch, which
/// kills the feed silently rather than loudly.
///
/// THE DESIRED SUBSCRIPTION SET IS THE SOURCE OF TRUTH. Kite keeps no server-side subscription
/// state across a reconnect, so <see cref="_desired"/> is replayed verbatim every time the socket
/// comes back. Nothing is ever added to it by the receive path, which is what keeps a reconnect
/// from accumulating subscriptions the connector no longer tracks — a leak that stays invisible
/// until the account crosses Kite's three-thousand cap days later, mid-session.
///
/// INDEX PACKETS ARE A DIFFERENT SHAPE. NIFTY 50 and SENSEX have no traded volume and no book, so
/// their packets are 28 or 32 bytes with the OHLC block in a different order. They are told apart
/// by LENGTH, and reading one as a tradable instrument's packet yields a plausible, wrong price.
/// </summary>
public sealed class ZerodhaStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>
    /// Ticks are conflated by the fan-out layer anyway, so a deep buffer buys nothing but latency.
    /// When this fills, the oldest tick — the least useful one — is dropped.
    /// </summary>
    private const int EventBufferCapacity = 4096;

    /// <summary>Indian equity prices arrive as integer paise.</summary>
    private const decimal PaiseDivisor = 100m;

    private readonly ZerodhaOptions _options;
    private readonly BrokerSession _session;
    private readonly IZerodhaInstrumentLookup _instruments;
    private readonly Func<CancellationToken, Task<Result>>? _ensureInstruments;
    private readonly ISymbolTranslator _symbols;
    private readonly ZerodhaOrderTagIndex _tags;
    private readonly IClock _clock;
    private readonly ILogger _logger;

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
    private long _unresolvedTicks;
    private bool _disposed;

    /// <summary>Creates the streaming facet.</summary>
    internal ZerodhaStream(
        ZerodhaOptions options,
        BrokerSession session,
        IZerodhaInstrumentLookup instruments,
        ISymbolTranslator symbols,
        ZerodhaOrderTagIndex tags,
        IClock clock,
        ILogger logger,
        Func<CancellationToken, Task<Result>>? ensureInstruments = null)
    {
        _options = options;
        _session = session;
        _instruments = instruments;
        _ensureInstruments = ensureInstruments;
        _symbols = symbols;
        _tags = tags;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>
    /// Ticks discarded because their instrument token is not in the master. Non-zero means the
    /// master is stale relative to what the socket is sending, which is an ingest problem rather
    /// than a reason to invent an instrument.
    /// </summary>
    public long UnresolvedTicks => Interlocked.Read(ref _unresolvedTicks);

    /// <summary>Instruments currently subscribed. Exposed so a conformance suite can prove no leak.</summary>
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
        lock (_stateGate)
        {
            // A supervisor calls Connect speculatively. Returning success for an already-running
            // pump is what stops a second socket being opened alongside the first.
            if (_pump is { IsCompleted: false })
            {
                return Result.Success();
            }
        }

        var cts = new CancellationTokenSource();
        var firstConnect = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_stateGate)
        {
            _cts = cts;
            _pump = Task.Run(() => RunAsync(firstConnect, cts.Token), CancellationToken.None);
        }

        // Report the FIRST connection attempt honestly to the caller: the link wizard and the
        // health check both need to know whether the socket came up, and a fire-and-forget connect
        // would report success while the socket was still failing its handshake.
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

            // A disconnect releases what it took. Leaving the desired set populated would have the
            // next Connect silently re-subscribe to instruments the caller had already let go.
            _desired.Clear();
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
                // Caller gave up waiting for a clean shutdown. The pump observes its own token and
                // will finish regardless; there is nothing to report.
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

        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        // A token miss on a cold process means the master is not loaded yet, not that the
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
                // Kite subscribes by numeric token and by nothing else, so this is a hard stop
                // rather than a degradation.
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"Kite does not list {instrument.Symbol}, so there is no live price for it.",
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
            // Count what the set WOULD become before committing to it, so a rejected subscribe
            // leaves the desired set exactly as it was rather than half applied.
            var projected = _desired.Count;
            foreach (var token in tokens)
            {
                if (!_desired.ContainsKey(token))
                {
                    projected++;
                }
            }

            if (projected > _options.MaxStreamSubscriptions)
            {
                // Overshooting Kite's cap does not fail loudly at the broker — it silently stops
                // delivering, which is the worst possible failure mode for a price feed.
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"That would take the Kite subscription set to {projected}, past the "
                    + $"{_options.MaxStreamSubscriptions} allowed on one socket. The fan-out layer must "
                    + "open another connection or drop a subscription."));
            }

            foreach (var token in tokens)
            {
                _desired[token] = mode;
            }
        }

        return await SendSubscriptionAsync(tokens, mode, subscribe: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

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

    // --- the pump ----------------------------------------------------------------------------

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
                SetState(StreamState.Connected, null);

                if (!reported)
                {
                    reported = true;
                    firstConnect.TrySetResult(Result.Success());
                }

                // Everything we were subscribed to before the drop has to be asked for again.
                // Kite keeps no server-side subscription state across a reconnect.
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
                        "Could not open the Kite streaming socket.",
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
            "The Kite streaming socket stopped before it connected.")));

        SetState(StreamState.Disconnected, null);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // Kite sends a one-byte heartbeat every couple of seconds when it has nothing else to
            // send, so silence really does mean the connection is gone rather than that the market
            // is quiet. That is what makes an idle timeout safe here.
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
                    SetState(StreamState.Reconnecting, "Kite closed the socket.");
                    return;
                }

                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

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
    /// The jitter is not decoration. Every socket in the fleet drops at the same instant when Kite
    /// restarts, and an un-jittered backoff has them all retry in lockstep forever — a
    /// self-inflicted thundering herd against a broker that is already struggling, and one that
    /// counts against an api_key limited to three connections.
    /// </summary>
    private TimeSpan BackoffDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 16);
        var baseMs = _options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(baseMs, _options.MaxReconnectDelay.TotalMilliseconds);

        var jitterRange = cappedMs * _options.ReconnectJitter;
        var jitter = ((Random.Shared.NextDouble() * 2d) - 1d) * jitterRange;

        return TimeSpan.FromMilliseconds(Math.Max(100d, cappedMs + jitter));
    }

    /// <summary>
    /// The socket authenticates with query parameters rather than a header, which is the one place
    /// Kite's WebSocket differs from its REST surface.
    /// </summary>
    private Uri BuildStreamUri()
    {
        var apiKey = _session.Extras.GetValueOrDefault(ZerodhaSessionKeys.ApiKey) ?? string.Empty;

        return new UriBuilder(_options.StreamUrl)
        {
            Query = $"api_key={Uri.EscapeDataString(apiKey)}"
                    + $"&access_token={Uri.EscapeDataString(_session.AccessToken)}",
        }.Uri;
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
                // Connected but not fully subscribed is exactly what Degraded is for. The UI shows
                // a stale-data banner rather than pretending the feed is healthy.
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
            // Not an error: the desired set has been recorded and will be replayed the moment the
            // socket comes back. Failing here would make a mid-reconnect subscribe look like a
            // permanent failure to the caller.
            return Result.Success();
        }

        var action = subscribe ? "subscribe" : "unsubscribe";
        var messages = new List<string>(2) { BuildActionMessage(action, tokens) };

        if (subscribe)
        {
            // Kite defaults a new subscription to quote mode, so the mode message is what makes
            // ltp and full actually mean anything.
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
                "Could not send a subscription message to Kite.",
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
        $$"""{"a":"mode","v":["{{ZerodhaMaps.ToNativeStreamMode(mode)}}",[{{string.Join(',', tokens)}}]]}""";

    // --- frame decoding ------------------------------------------------------------------------

    /// <summary>
    /// Text frames carry postbacks and control messages on the same socket as the binary ticks.
    ///
    /// Order updates in particular MUST be handled here: they are how a fill is learned about
    /// seconds before the order book would show it, and they are the reason this connector's
    /// stream is worth having beyond prices.
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
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "order" when root.TryGetProperty("data", out var orderData):
                    PublishOrderUpdate(orderData);
                    break;

                case "error":
                    // A socket-level error is degraded rather than disconnected: Kite keeps the
                    // connection open and the ticks keep coming, so reporting it as down would
                    // trigger a reconnect that fixes nothing.
                    SetState(StreamState.Degraded, ReadDataString(root) ?? "Kite reported a stream error.");
                    break;

                case "message":
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "{ConnectorId}: broker message on the stream: {Message}",
                            ZerodhaAuth.ConnectorId,
                            ReadDataString(root) ?? "(empty)");
                    }

                    break;

                default:
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{ConnectorId}: could not parse a text frame from the stream.",
                ZerodhaAuth.ConnectorId);
        }
    }

    private void PublishOrderUpdate(JsonElement data)
    {
        KiteOrder? order;
        try
        {
            order = data.Deserialize<KiteOrder>(ZerodhaJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{ConnectorId}: an order postback could not be read.",
                ZerodhaAuth.ConnectorId);
            return;
        }

        if (order is null)
        {
            return;
        }

        // Out-of-scope segments are dropped quietly: a Kite account that also trades MCX pushes
        // those postbacks down the same socket, and they are not this connector's to report.
        if (ZerodhaMaps.ToCanonicalVenue(order.Exchange).IsFailure)
        {
            return;
        }

        // Lenient, like the order book: an unmapped status must not stop a fill being reported.
        var mapped = ZerodhaOrderMapper.MapOrder(
            order,
            _symbols,
            _clock,
            _tags,
            "websocket/order",
            lenient: true);

        if (mapped.IsFailure)
        {
            _logger.LogWarning(
                "{ConnectorId}: an order postback could not be mapped: {Error}",
                ZerodhaAuth.ConnectorId,
                mapped.Error.ToString());
            return;
        }

        Publish(new StreamEvent.OrderUpdated(mapped.Value));
    }

    private static string? ReadDataString(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;

    /// <summary>
    /// A binary frame is a count of packets, then each packet prefixed with its own length. All
    /// integers are big-endian, which is the network order the documentation implies and the one
    /// every Kite client library uses.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can feed it recorded frames and read the resulting
    /// events off <see cref="Events"/> without standing up a socket. The packet decoder is the
    /// single highest-risk piece of code in this connector — a wrong byte offset is a wrong price,
    /// and it fails silently — so being able to assert on it directly is worth the widened
    /// accessibility.
    /// </remarks>
    internal void DispatchBinary(ReadOnlySpan<byte> frame)
    {
        // A one-byte frame is the heartbeat Kite sends when there is nothing to report. Kite's own
        // documentation says it "can be safely ignored", and a two-byte read on it would be an
        // out-of-range throw on the quietest possible market.
        if (frame.Length < 4)
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
                // A truncated frame is dropped whole rather than partially decoded. Half a packet
                // is not half a price; it is a different price.
                return;
            }

            DecodePacket(frame.Slice(offset, length));
            offset += length;
        }
    }

    private void DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Layout.LtpPacket)
        {
            return;
        }

        var token = (uint)BinaryPrimitives.ReadInt32BigEndian(packet);
        if (!_instruments.TryGetByToken(token, out var instrument))
        {
            // Counted, not guessed. A token we do not know means a stale instrument master, and
            // the ingest job alarms on this counter rather than us inventing an instrument.
            Interlocked.Increment(ref _unresolvedTicks);
            return;
        }

        var now = _clock.UtcNow;
        var last = Price(ReadInt(packet, Layout.LastPrice));

        var tick = packet.Length switch
        {
            // 8 bytes: token and last price. The LTP mode most watchlists use.
            Layout.LtpPacket => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                Timestamp = now,
            },

            // 28 or 32 bytes: an INDEX packet. Indices have no traded volume and no book, and
            // their OHLC block is in a different order from a tradable instrument's — high, low,
            // open, close rather than open, high, low, close. Reading one as the other yields a
            // plausible and wrong price, which is the worst kind.
            Layout.IndexQuotePacket or Layout.IndexFullPacket => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                High = Price(ReadInt(packet, Layout.IndexHigh)),
                Low = Price(ReadInt(packet, Layout.IndexLow)),
                Open = Price(ReadInt(packet, Layout.IndexOpen)),
                PreviousClose = Price(ReadInt(packet, Layout.IndexClose)),
                Timestamp = packet.Length >= Layout.IndexFullPacket
                    ? Timestamp(ReadInt(packet, Layout.IndexExchangeTimestamp), now)
                    : now,
            },

            // 44 bytes and up: a tradable instrument's quote packet, and 184 bytes is the same
            // thing with the five-level book appended.
            >= Layout.QuotePacket => new Tick
            {
                Instrument = instrument,
                LastPrice = last,
                LastQuantity = new Quantity(ReadInt(packet, Layout.LastQuantity)),
                Volume = ReadInt(packet, Layout.Volume),
                Open = Price(ReadInt(packet, Layout.Open)),
                High = Price(ReadInt(packet, Layout.High)),
                Low = Price(ReadInt(packet, Layout.Low)),
                PreviousClose = Price(ReadInt(packet, Layout.Close)),
                OpenInterest = packet.Length >= Layout.FullPacket
                    ? ReadInt(packet, Layout.OpenInterest)
                    : null,
                BidPrice = packet.Length >= Layout.FullPacket
                    ? Price(ReadInt(packet, Layout.DepthOffset + Layout.EntryPrice))
                    : null,
                AskPrice = packet.Length >= Layout.FullPacket
                    ? Price(ReadInt(packet, Layout.AskOffset + Layout.EntryPrice))
                    : null,
                Timestamp = packet.Length >= Layout.FullPacket
                    ? Timestamp(ReadInt(packet, Layout.ExchangeTimestamp), now)
                    : now,
            },

            _ => null,
        };

        if (tick is null)
        {
            return;
        }

        Publish(new StreamEvent.TickReceived(tick));

        if (packet.Length >= Layout.FullPacket)
        {
            Publish(new StreamEvent.DepthReceived(DecodeDepth(instrument, packet, tick.Timestamp)));
        }
    }

    /// <summary>
    /// The five-level book that follows the quote block in a 184-byte packet: ten entries of
    /// twelve bytes each — quantity (int32), price (int32), order count (int16), two bytes of
    /// padding — bids first, then asks.
    /// </summary>
    private static MarketDepth DecodeDepth(
        InstrumentKey instrument,
        ReadOnlySpan<byte> packet,
        DateTimeOffset timestamp)
    {
        var bids = new List<DepthLevel>(Layout.DepthLevels);
        var asks = new List<DepthLevel>(Layout.DepthLevels);

        for (var i = 0; i < Layout.DepthLevels * 2; i++)
        {
            var start = Layout.DepthOffset + (i * Layout.EntrySize);
            if (start + Layout.EntrySize > packet.Length)
            {
                break;
            }

            var entry = packet.Slice(start, Layout.EntrySize);
            var quantity = BinaryPrimitives.ReadInt32BigEndian(entry);
            var price = BinaryPrimitives.ReadInt32BigEndian(entry[Layout.EntryPrice..]);
            var orders = BinaryPrimitives.ReadInt16BigEndian(entry[Layout.EntryOrders..]);

            // Kite pads the book to five levels with zero-price rows when there is less depth than
            // that. A zero-priced level is not a level — and dropping it here is what keeps the
            // socket's book identical to the one the REST quote route returns for the same
            // instrument, which is the whole point of both producing a MarketDepth.
            if (price <= 0)
            {
                continue;
            }

            var level = new DepthLevel(
                new Money(price / PaiseDivisor, Currency.Inr),
                new Quantity(quantity),
                orders);

            if (i < Layout.DepthLevels)
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

    /// <summary>
    /// Reads a big-endian int32 at a BYTE offset.
    ///
    /// A byte offset rather than a word index on purpose: every constant in <see cref="Layout"/>
    /// is then the same number the documentation prints, and a reviewer can check the two against
    /// each other without doing arithmetic. An index-times-four scheme reads just as plausibly
    /// while being wrong, and a wrong offset here is a wrong price.
    /// </summary>
    private static int ReadInt(ReadOnlySpan<byte> packet, int byteOffset) =>
        BinaryPrimitives.ReadInt32BigEndian(packet[byteOffset..]);

    private static Money Price(int paise) => new(paise / PaiseDivisor, Currency.Inr);

    /// <summary>
    /// A Unix epoch from the wire, falling back when it is absent. Kite writes 0 rather than
    /// omitting the field outside market hours, and 1970 on a tick is worse than "now".
    /// </summary>
    private static DateTimeOffset Timestamp(int epochSeconds, DateTimeOffset fallback) =>
        epochSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds) : fallback;

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
            // The UI turns this into the stale-data banner, so it is published even when the
            // reason is null — the transition itself is the information.
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

    /// <summary>
    /// The binary packet layout, transcribed from Kite's WebSocket documentation.
    ///
    /// Every constant is a BYTE offset or a BYTE length, so each line can be checked directly
    /// against the table at https://kite.trade/docs/connect/v3/websocket/. Nothing here is derived
    /// from anything else; that is what makes it reviewable.
    /// </summary>
    private static class Layout
    {
        // Packet lengths, which are also how the mode and the instrument kind are told apart.
        public const int LtpPacket = 8;
        public const int IndexQuotePacket = 28;
        public const int IndexFullPacket = 32;
        public const int QuotePacket = 44;
        public const int FullPacket = 184;

        // Tradable instrument fields.
        public const int LastPrice = 4;
        public const int LastQuantity = 8;
        public const int AveragePrice = 12;
        public const int Volume = 16;
        public const int TotalBuyQuantity = 20;
        public const int TotalSellQuantity = 24;
        public const int Open = 28;
        public const int High = 32;
        public const int Low = 36;
        public const int Close = 40;
        public const int LastTradeTime = 44;
        public const int OpenInterest = 48;
        public const int OpenInterestDayHigh = 52;
        public const int OpenInterestDayLow = 56;
        public const int ExchangeTimestamp = 60;

        // Index fields. Note the ORDER: high, low, open, close — not the OHLC above.
        public const int IndexHigh = 8;
        public const int IndexLow = 12;
        public const int IndexOpen = 16;
        public const int IndexClose = 20;
        public const int IndexPriceChange = 24;
        public const int IndexExchangeTimestamp = 28;

        // Market depth: ten entries of twelve bytes, five bids then five asks.
        public const int DepthOffset = 64;
        public const int EntrySize = 12;
        public const int DepthLevels = 5;

        /// <summary>First ask entry: five bid entries after the depth block begins.</summary>
        public const int AskOffset = DepthOffset + (DepthLevels * EntrySize);

        // Offsets WITHIN one depth entry.
        public const int EntryQuantity = 0;
        public const int EntryPrice = 4;
        public const int EntryOrders = 8;
    }
}
