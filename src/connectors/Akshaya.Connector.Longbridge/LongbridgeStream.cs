using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Longbridge's live feed, on two sockets this facet owns: quote and depth pushes from the quote gateway, and
/// order-change pushes from the trade gateway's <c>private</c> topic.
///
/// THE DESIRED SUBSCRIPTION SET IS THE SOURCE OF TRUTH. Subscriptions belong to a connection, so a reconnect
/// starts with none. <see cref="_desired"/> is replayed every time the quote socket comes back, and nothing on
/// the push path ever adds to it.
///
/// EITHER SOCKET DROPPING RECONNECTS BOTH. A feed with prices but no order updates is worse than a short gap in
/// both: it looks healthy while the blotter goes stale. The exception is a trade socket that cannot be opened
/// at all — an account without trade access — which leaves prices running and the state Degraded.
///
/// ORDER PUSHES ARE THIS LOGIN'S. The trade socket authenticates with the link's own token, so the gateway sends
/// only this login's orders. A push carries no time in force, so an update reads as Day until the book is next
/// read; and it carries no trade id, so fills are reconciled from the executions routes, never invented here.
/// </summary>
public sealed class LongbridgeStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>Ticks are conflated downstream; when this fills, the oldest event is the one dropped.</summary>
    private const int EventBufferCapacity = 4096;

    private readonly LongbridgeOptions _options;
    private readonly BrokerSession _session;
    private readonly LongbridgeErrorMapper _errors;
    private readonly LongbridgeInstrumentCache _cache;
    private readonly LongbridgeInstrumentResolver _resolver;
    private readonly Func<LongbridgeCredentials?, LongbridgeApi> _apiFactory;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>The subscriptions we WANT, keyed by Longbridge symbol. Replayed after every reconnect.</summary>
    private readonly Dictionary<string, Subscription> _desired = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _stateGate = new();

    private LongbridgeSocket? _quote;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    internal LongbridgeStream(
        LongbridgeOptions options,
        BrokerSession session,
        LongbridgeErrorMapper errors,
        LongbridgeInstrumentCache cache,
        LongbridgeInstrumentResolver resolver,
        Func<LongbridgeCredentials?, LongbridgeApi> apiFactory,
        IClock clock,
        ILogger logger)
    {
        _options = options;
        _session = session;
        _errors = errors;
        _cache = cache;
        _resolver = resolver;
        _apiFactory = apiFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>Securities currently wanted. Exposed so a conformance suite can prove no leak.</summary>
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
            // A supervisor calls Connect speculatively; a running pump must not get a second pair of sockets.
            if (_pump is { IsCompleted: false })
            {
                return Result.Success();
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _pump = Task.Run(() => RunAsync(firstConnect, cts.Token), CancellationToken.None);
        }

        // Report the FIRST attempt honestly: a fire-and-forget connect would say success while the gateway was
        // still refusing the token.
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

        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        var wanted = new List<Subscription>(instruments.Count);
        foreach (var key in instruments)
        {
            var target = LongbridgeTarget.For(key, _cache);
            if (target.IsFailure)
            {
                return Result.Failure(target.Error);
            }

            wanted.Add(new Subscription(key, target.Value.Native, target.Value.Currency, mode));
        }

        var previous = new Dictionary<string, Subscription?>(StringComparer.OrdinalIgnoreCase);
        LongbridgeSocket? socket;

        lock (_stateGate)
        {
            foreach (var subscription in wanted)
            {
                previous.TryAdd(subscription.Native, _desired.GetValueOrDefault(subscription.Native));
            }

            var projected = _desired.Count + previous.Count(p => p.Value is null);
            if (projected > _options.MaxSubscriptions)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"That would subscribe {projected} securities; Longbridge allows {_options.MaxSubscriptions} per account. "
                    + "The fan-out layer must drop a subscription first."));
            }

            foreach (var subscription in wanted)
            {
                _desired[subscription.Native] = subscription;
            }

            socket = _quote;
        }

        if (socket is not { IsOpen: true })
        {
            // Recorded, and replayed the moment the connection is up; not an error.
            return Result.Success();
        }

        var sent = await SendAsync(socket, wanted, subscribe: true, ct).ConfigureAwait(false);
        if (sent.IsSuccess)
        {
            // A security moved down from Full no longer wants its book pushed.
            var downgraded = wanted
                .Where(w => w.Mode != StreamMode.Full && previous.GetValueOrDefault(w.Native) is { Mode: StreamMode.Full })
                .Select(w => w.Native)
                .ToList();

            if (downgraded.Count > 0)
            {
                _ = await socket
                    .RequestAsync(LongbridgeCommand.Unsubscribe, LbSubscription.Unsubscribe(downgraded, [LongbridgeMaps.SubTypeDepth]), ct)
                    .ConfigureAwait(false);
            }

            return sent;
        }

        // Refused — typically a missing OpenAPI quote permission. Put the desired set back exactly as it was, so
        // the next reconnect does not replay a subscription Longbridge has already said no to.
        lock (_stateGate)
        {
            foreach (var (native, before) in previous)
            {
                if (before is null)
                {
                    _desired.Remove(native);
                }
                else
                {
                    _desired[native] = before;
                }
            }
        }

        return sent;
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(IReadOnlyCollection<InstrumentKey> instruments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var removed = new List<Subscription>();
        LongbridgeSocket? socket;

        lock (_stateGate)
        {
            foreach (var key in instruments)
            {
                if (LongbridgeTarget.For(key, _cache) is { IsSuccess: true } target
                    && _desired.Remove(target.Value.Native, out var subscription))
                {
                    removed.Add(subscription);
                }
            }

            socket = _quote;
        }

        if (removed.Count == 0 || socket is not { IsOpen: true })
        {
            return Result.Success();
        }

        var sent = await SendAsync(socket, removed, subscribe: false, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            // The caller's intent is recorded either way: a refused release costs subscription quota until the
            // next reconnect, not correctness, because pushes for symbols no longer wanted are dropped.
            _logger.LogWarning(
                "{ConnectorId}: Longbridge refused to unsubscribe {Count} securities; they are released when the stream reconnects. {Error}",
                LongbridgeAuth.ConnectorId,
                removed.Count,
                sent.Error.ToString());
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default) => _events.Reader.ReadAllAsync(ct);

    // --- the pump ------------------------------------------------------------------------------------------------

    private async Task RunAsync(TaskCompletionSource<Result> firstConnect, CancellationToken ct)
    {
        var account = LongbridgeAccount.FromSession(_session);
        if (account.IsFailure)
        {
            firstConnect.TrySetResult(Result.Failure(account.Error));
            SetState(StreamState.Disconnected, account.Error.Message);
            return;
        }

        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            SetState(attempt == 0 ? StreamState.Connecting : StreamState.Reconnecting, null);

            var opened = await OpenAsync(account.Value.Credentials, ct).ConfigureAwait(false);

            if (opened.IsSuccess)
            {
                var connection = opened.Value;
                attempt = 0;

                try
                {
                    lock (_stateGate)
                    {
                        _quote = connection.Quote;
                    }

                    SetState(StreamState.Connected, null);
                    firstConnect.TrySetResult(Result.Success());

                    if (connection.Degraded is { } degraded)
                    {
                        SetState(StreamState.Degraded, degraded);
                    }

                    await ResubscribeAsync(connection.Quote, ct).ConfigureAwait(false);
                    await PumpAsync(connection, ct).ConfigureAwait(false);

                    if (!ct.IsCancellationRequested)
                    {
                        SetState(StreamState.Reconnecting, CloseReason(connection));
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
                        _quote = null;
                    }

                    if (connection.Trade is not null)
                    {
                        await connection.Trade.DisposeAsync().ConfigureAwait(false);
                    }

                    await connection.Quote.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                firstConnect.TrySetResult(Result.Failure(opened.Error));

                if (opened.Error.Code is ConnectorErrorCodes.SessionExpired
                    or ConnectorErrorCodes.ReauthRequired
                    or ConnectorErrorCodes.InvalidCredentials)
                {
                    // A token Longbridge has refused is refused on the next attempt too; retrying would only spend
                    // the rate limit. The session monitor refreshes the link and the host starts a new stream.
                    SetState(StreamState.Disconnected, opened.Error.Message);
                    break;
                }

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
            ConnectorErrorCodes.BrokerUnavailable,
            "The Longbridge stream stopped before it connected.")));

        SetState(StreamState.Disconnected, null);
    }

    /// <summary>Opens the quote socket, then the trade socket and its private topic. Only the quote socket is required.</summary>
    private async Task<Result<Connection>> OpenAsync(LongbridgeCredentials credentials, CancellationToken ct)
    {
        await using var api = _apiFactory(credentials);

        var quote = await LongbridgeSocket
            .OpenAsync(_options.QuoteSocketUrl, api, _options, _errors, _logger, receivePushes: true, ct)
            .ConfigureAwait(false);

        if (quote.IsFailure)
        {
            return Result<Connection>.Failure(quote.Error);
        }

        try
        {
            var trade = await LongbridgeSocket
                .OpenAsync(_options.TradeSocketUrl, api, _options, _errors, _logger, receivePushes: true, ct)
                .ConfigureAwait(false);

            if (trade.IsFailure)
            {
                return new Connection(quote.Value, null, $"Prices are live, but order updates are not being pushed: {trade.Error.Message}");
            }

            var subscribed = await trade.Value
                .RequestAsync(LongbridgeCommand.TradeSubscribe, LbTradeSub.Encode([LbTradeSub.PrivateTopic]), ct)
                .ConfigureAwait(false);

            if (subscribed.IsFailure)
            {
                await trade.Value.DisposeAsync().ConfigureAwait(false);
                return new Connection(quote.Value, null, $"Prices are live, but order updates are not being pushed: {subscribed.Error.Message}");
            }

            return new Connection(quote.Value, trade.Value, null);
        }
        catch
        {
            await quote.Value.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpAsync(Connection connection, CancellationToken ct)
    {
        var pumps = new List<Task> { PumpQuotesAsync(connection.Quote, ct) };
        if (connection.Trade is not null)
        {
            pumps.Add(PumpTradesAsync(connection.Quote, connection.Trade, ct));
        }

        // Either socket ending ends this attempt; the caller disposes both, which completes the other pump.
        await Task.WhenAny(pumps).ConfigureAwait(false);
    }

    private async Task PumpQuotesAsync(LongbridgeSocket quote, CancellationToken ct)
    {
        if (quote.Pushes is not { } pushes)
        {
            return;
        }

        try
        {
            await foreach (var push in pushes.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    switch (push.Command)
                    {
                        case LongbridgeCommand.PushQuoteData:
                            PublishTick(LbPushQuote.Decode(push.Body));
                            break;

                        case LongbridgeCommand.PushDepthData:
                            PublishDepth(LbDepth.DecodePush(push.Body));
                            break;

                        default:
                            break;
                    }
                }
                catch (InvalidDataException ex)
                {
                    // One unreadable push costs one event, not the feed.
                    _logger.LogWarning(ex, "{ConnectorId}: could not read a {Command} push.", LongbridgeAuth.ConnectorId, LongbridgeCommand.Name(push.Command));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task PumpTradesAsync(LongbridgeSocket quote, LongbridgeSocket trade, CancellationToken ct)
    {
        if (trade.Pushes is not { } pushes)
        {
            return;
        }

        try
        {
            await foreach (var push in pushes.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (push.Command != LongbridgeCommand.TradeNotify)
                {
                    continue;
                }

                try
                {
                    var notification = LbNotification.Decode(push.Body);
                    if (!string.Equals(notification.Topic, LbTradeSub.PrivateTopic, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var evt = JsonSerializer.Deserialize<LbPushEvent>(notification.Data.Span, LongbridgeJson.Options);
                    if (evt is { Event: LbPushEvent.OrderChanged, Data: { } order })
                    {
                        await PublishOrderAsync(quote, order, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    _logger.LogWarning(ex, "{ConnectorId}: could not read a trade push.", LongbridgeAuth.ConnectorId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private void PublishTick(LbPushQuote quote)
    {
        if (!TryFind(quote.Symbol, out var subscription)
            || LongbridgeNumber.Decimal(quote.LastDone) is not { } last
            || last <= 0m)
        {
            return;
        }

        var currency = subscription.Currency;

        Publish(new StreamEvent.TickReceived(new Tick
        {
            Instrument = subscription.Key,
            LastPrice = new Money(last, currency),
            LastQuantity = quote.CurrentVolume > 0 ? new Quantity(quote.CurrentVolume) : null,
            Volume = quote.Volume > 0 ? quote.Volume : null,
            Open = LongbridgeMarketData.Positive(quote.Open, currency),
            High = LongbridgeMarketData.Positive(quote.High, currency),
            Low = LongbridgeMarketData.Positive(quote.Low, currency),
            Timestamp = LongbridgeTime.FromUnixSeconds(quote.Timestamp) ?? _clock.UtcNow,
        }));
    }

    private void PublishDepth(LbDepth depth)
    {
        if (TryFind(depth.Symbol, out var subscription))
        {
            Publish(new StreamEvent.DepthReceived(
                LongbridgeMarketData.ToDepth(subscription.Key, depth, subscription.Currency, _clock.UtcNow)));
        }
    }

    private async Task PublishOrderAsync(LongbridgeSocket quote, LbOrder order, CancellationToken ct)
    {
        var ensured = await _resolver.EnsureAsync(quote, [order.Symbol ?? string.Empty], ct).ConfigureAwait(false);

        var mapped = ensured.IsSuccess
            ? LongbridgeOrderMapper.MapOrder(order, _cache, _clock, lenient: true)
            : Result<BrokerOrder>.Failure(ensured.Error);

        if (mapped.IsSuccess)
        {
            Publish(new StreamEvent.OrderUpdated(mapped.Value));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{ConnectorId}: an order push was not published: {Error}", LongbridgeAuth.ConnectorId, mapped.Error.ToString());
        }
    }

    private async Task ResubscribeAsync(LongbridgeSocket quote, CancellationToken ct)
    {
        List<Subscription> snapshot;
        lock (_stateGate)
        {
            snapshot = [.. _desired.Values];
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var sent = await SendAsync(quote, snapshot, subscribe: true, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            // Connected but not fully subscribed is exactly what Degraded means.
            SetState(StreamState.Degraded, $"Connected, but re-subscribing failed: {sent.Error.Message}");
        }
    }

    private async Task<Result> SendAsync(
        LongbridgeSocket socket,
        IReadOnlyCollection<Subscription> subscriptions,
        bool subscribe,
        CancellationToken ct)
    {
        foreach (var group in subscriptions.GroupBy(s => s.Mode == StreamMode.Full))
        {
            var subTypes = LongbridgeMaps.ToNativeSubTypes(group.Key ? StreamMode.Full : StreamMode.Quote);

            foreach (var chunk in group.Select(s => s.Native).Chunk(Math.Max(1, _options.MaxSymbolsPerRequest)))
            {
                var response = await socket
                    .RequestAsync(
                        subscribe ? LongbridgeCommand.Subscribe : LongbridgeCommand.Unsubscribe,
                        subscribe ? LbSubscription.Subscribe(chunk, subTypes, firstPush: true) : LbSubscription.Unsubscribe(chunk, subTypes),
                        ct)
                    .ConfigureAwait(false);

                if (response.IsFailure)
                {
                    return Result.Failure(response.Error);
                }
            }
        }

        return Result.Success();
    }

    private bool TryFind(string symbol, [NotNullWhen(true)] out Subscription? subscription)
    {
        lock (_stateGate)
        {
            // A first push for something unsubscribed a moment ago is not the caller's any more.
            return _desired.TryGetValue(symbol.Trim(), out subscription);
        }
    }

    private static string CloseReason(Connection connection) =>
        connection.Quote.Closed.IsCompleted
            ? connection.Quote.Closed.Result.Message
            : connection.Trade is { Closed.IsCompleted: true } trade
                ? trade.Closed.Result.Message
                : "A Longbridge connection ended.";

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
    }

    /// <summary>One wanted subscription.</summary>
    private sealed record Subscription(InstrumentKey Key, string Native, Currency Currency, StreamMode Mode);

    /// <summary>One attempt's sockets. <see cref="Trade"/> is null when order updates could not be connected.</summary>
    private sealed record Connection(LongbridgeSocket Quote, LongbridgeSocket? Trade, string? Degraded);
}
