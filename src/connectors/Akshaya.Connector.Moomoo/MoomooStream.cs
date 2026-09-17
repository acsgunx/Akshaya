using System.Text.Json;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// moomoo's live feed: quote and order-book pushes, order and fill pushes, and OpenD's own status
/// notifications, all on one long-lived OpenD connection that belongs to this facet alone.
///
/// Four things are worth understanding before changing anything.
///
/// THE DESIRED SUBSCRIPTION SET IS THE SOURCE OF TRUTH. Subscriptions belong to a connection, so a
/// reconnect starts with none. <see cref="_desired"/> is replayed verbatim every time the connection comes
/// back, and nothing on the push path ever adds to it — which is what keeps a reconnect from accumulating
/// subscriptions nobody tracks.
///
/// THE QUOTA IS COUNTED HERE. moomoo gives an account a fixed subscription quota (100 units on the
/// smallest accounts) where one security's basic quote is one unit and its order book another. OpenD
/// refuses past it; this facet refuses first, with an explanation, so the fan-out layer can decide what
/// to drop.
///
/// UNSUBSCRIBING CAN BE REFUSED FOR A MINUTE. OpenD will not release a subscription within a minute of
/// making it. A refused unsubscribe is retried once the minute has passed rather than reported as a
/// failure: the caller's intent is recorded, and the quota is only borrowed.
///
/// ORDER PUSHES ARE FILTERED BY ACCOUNT. OpenD pushes updates for every account subscribed on the
/// connection, and this facet subscribes exactly one; the header check is what stops a second tool on the
/// same OpenD from putting its orders in this trader's blotter.
/// </summary>
public sealed class MoomooStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>Ticks are conflated downstream; when this fills, the oldest event is the one dropped.</summary>
    private const int EventBufferCapacity = 4096;

    private readonly MoomooOptions _options;
    private readonly GatewayAddress _gateway;
    private readonly BrokerSession _session;
    private readonly MoomooErrorMapper _errors;
    private readonly MoomooInstrumentCache _cache;
    private readonly MoomooInstrumentResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>The subscriptions we WANT, keyed by native symbol. Replayed after every reconnect.</summary>
    private readonly Dictionary<string, Subscription> _desired = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _stateGate = new();

    private MoomooConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    internal MoomooStream(
        MoomooOptions options,
        GatewayAddress gateway,
        BrokerSession session,
        MoomooErrorMapper errors,
        MoomooInstrumentCache cache,
        MoomooInstrumentResolver resolver,
        IClock clock,
        ILogger logger)
    {
        _options = options;
        _gateway = gateway;
        _session = session;
        _errors = errors;
        _cache = cache;
        _resolver = resolver;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>Subscription quota units in use. Exposed so a conformance suite can prove no leak.</summary>
    public int QuotaInUse
    {
        get
        {
            lock (_stateGate)
            {
                return _desired.Values.Sum(s => s.QuotaUnits);
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        var firstConnect = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_stateGate)
        {
            // A supervisor calls Connect speculatively; a running pump must not get a second connection.
            if (_pump is { IsCompleted: false })
            {
                return Result.Success();
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _pump = Task.Run(() => RunAsync(firstConnect, cts.Token), CancellationToken.None);
        }

        // Report the FIRST attempt honestly: a fire-and-forget connect would say success while OpenD was
        // still refusing the connection.
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
            var target = TargetFor(key);
            if (target.IsFailure)
            {
                return Result.Failure(target.Error);
            }

            wanted.Add(new Subscription(key, target.Value.Market, target.Value.Code, mode));
        }

        var previous = new Dictionary<string, Subscription?>(StringComparer.OrdinalIgnoreCase);
        MoomooConnection? connection;

        lock (_stateGate)
        {
            var projected = _desired.Values.Sum(s => s.QuotaUnits);
            foreach (var subscription in wanted)
            {
                if (!previous.ContainsKey(subscription.Native))
                {
                    projected -= _desired.GetValueOrDefault(subscription.Native)?.QuotaUnits ?? 0;
                    projected += subscription.QuotaUnits;
                    previous[subscription.Native] = _desired.GetValueOrDefault(subscription.Native);
                }
            }

            if (projected > _options.SubscriptionQuota)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"That would use {projected} of moomoo's {_options.SubscriptionQuota} subscription units. A security's quote "
                    + "is one unit and its order book (Full mode) another; larger accounts get a larger quota. The fan-out "
                    + "layer must drop a subscription first."));
            }

            foreach (var subscription in wanted)
            {
                _desired[subscription.Native] = subscription;
            }

            connection = _connection;
        }

        if (connection is not { IsOpen: true })
        {
            // Recorded, and replayed the moment the connection is up; not an error.
            return Result.Success();
        }

        var sent = await SendSubscriptionAsync(connection, wanted, subscribe: true, ct).ConfigureAwait(false);
        if (sent.IsSuccess)
        {
            return sent;
        }

        // Refused — typically a missing quote right. Put the desired set back exactly as it was, so the
        // next reconnect does not replay a subscription OpenD has already said no to.
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
        MoomooConnection? connection;
        CancellationToken lifetime;

        lock (_stateGate)
        {
            foreach (var key in instruments)
            {
                if (TargetFor(key) is { IsSuccess: true } target
                    && _desired.Remove(MoomooNative.Qualify(target.Value.Market, target.Value.Code), out var subscription))
                {
                    removed.Add(subscription);
                }
            }

            connection = _connection;
            lifetime = _cts?.Token ?? CancellationToken.None;
        }

        if (removed.Count == 0 || connection is not { IsOpen: true })
        {
            return Result.Success();
        }

        var sent = await SendSubscriptionAsync(connection, removed, subscribe: false, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            _ = Task.Run(() => RetryUnsubscribeAsync(connection, removed, lifetime), CancellationToken.None);
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default) => _events.Reader.ReadAllAsync(ct);

    // --- the pump ------------------------------------------------------------------------------------------

    private async Task RunAsync(TaskCompletionSource<Result> firstConnect, CancellationToken ct)
    {
        var account = MoomooAccount.FromSession(_session);
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

            var opened = await MoomooConnection
                .OpenAsync(_gateway, _options, _errors, _clock, _logger, receivePushes: true, ct)
                .ConfigureAwait(false);

            if (opened.IsSuccess)
            {
                var connection = opened.Value;
                attempt = 0;

                try
                {
                    lock (_stateGate)
                    {
                        _connection = connection;
                    }

                    var accountPushes = await connection.RequestAsync<OpenDSubAccPushC2S, OpenDEmpty>(
                        MoomooProtoId.TrdSubAccPush,
                        new OpenDSubAccPushC2S { AccIdList = [account.Value.AccId] },
                        ct).ConfigureAwait(false);

                    SetState(StreamState.Connected, null);
                    firstConnect.TrySetResult(Result.Success());

                    if (accountPushes.IsFailure)
                    {
                        SetState(StreamState.Degraded, $"Prices are live but order updates are not being pushed: {accountPushes.Error.Message}");
                    }

                    await ResubscribeAsync(connection, ct).ConfigureAwait(false);
                    await PumpAsync(connection, account.Value, ct).ConfigureAwait(false);

                    if (!ct.IsCancellationRequested)
                    {
                        var reason = connection.Closed.IsCompleted ? connection.Closed.Result.Message : "The OpenD connection ended.";
                        SetState(StreamState.Reconnecting, reason);
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
                        _connection = null;
                    }

                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                firstConnect.TrySetResult(Result.Failure(opened.Error));
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
            "The moomoo stream stopped before it connected.")));

        SetState(StreamState.Disconnected, null);
    }

    private async Task PumpAsync(MoomooConnection connection, MoomooAccount account, CancellationToken ct)
    {
        if (connection.Pushes is not { } pushes)
        {
            return;
        }

        await foreach (var push in pushes.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await DispatchAsync(connection, account, push, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // One unreadable push costs one event, not the feed.
                _logger.LogWarning(ex, "{ConnectorId}: could not read a {Protocol} push.", MoomooAuth.ConnectorId, MoomooProtoId.Name(push.ProtoId));
            }
        }
    }

    private async Task DispatchAsync(MoomooConnection connection, MoomooAccount account, MoomooPush push, CancellationToken ct)
    {
        switch (push.ProtoId)
        {
            case MoomooProtoId.QotUpdateBasicQot:
                {
                    foreach (var quote in Read<OpenDUpdateBasicQotS2C>(push)?.BasicQotList ?? [])
                    {
                        PublishTick(quote);
                    }

                    break;
                }

            case MoomooProtoId.QotUpdateOrderBook:
                {
                    if (Read<OpenDOrderBookS2C>(push) is { } book)
                    {
                        PublishDepth(book);
                    }

                    break;
                }

            case MoomooProtoId.TrdUpdateOrder:
                {
                    if (Read<OpenDUpdateOrderS2C>(push) is { Header: { } header, Order: { } order } && header.AccId == account.AccId)
                    {
                        await PublishOrderAsync(connection, header, order, ct).ConfigureAwait(false);
                    }

                    break;
                }

            case MoomooProtoId.TrdUpdateOrderFill:
                {
                    if (Read<OpenDUpdateOrderFillS2C>(push) is { Header: { } header, OrderFill: { } fill } && header.AccId == account.AccId)
                    {
                        await PublishFillAsync(connection, header, fill, ct).ConfigureAwait(false);
                    }

                    break;
                }

            case MoomooProtoId.Notify:
                {
                    if (Read<OpenDNotifyS2C>(push) is { } notify)
                    {
                        HandleNotify(notify);
                    }

                    break;
                }

            default:
                break;
        }
    }

    private void PublishTick(OpenDBasicQot quote)
    {
        if (!TryFindSubscription(quote.Security, out var subscription) || MoomooNumber.Decimal(quote.CurPrice) is not { } last)
        {
            return;
        }

        var currency = subscription.Market.Currency;

        Publish(new StreamEvent.TickReceived(new Tick
        {
            Instrument = subscription.Key,
            LastPrice = new Money(last, currency),
            Volume = quote.Volume,
            Open = MoneyOrNull(quote.OpenPrice, currency),
            High = MoneyOrNull(quote.HighPrice, currency),
            Low = MoneyOrNull(quote.LowPrice, currency),
            PreviousClose = MoneyOrNull(quote.LastClosePrice, currency),
            OpenInterest = quote.OptionExData?.OpenInterest,
            Timestamp = MoomooTime.Best(quote.UpdateTimestamp, quote.UpdateTime, subscription.Market.Zone) ?? _clock.UtcNow,
        }));
    }

    private void PublishDepth(OpenDOrderBookS2C book)
    {
        if (TryFindSubscription(book.Security, out var subscription))
        {
            Publish(new StreamEvent.DepthReceived(
                MoomooMarketData.ToDepth(subscription.Key, book, subscription.Market, _clock.UtcNow)));
        }
    }

    private async Task PublishOrderAsync(MoomooConnection connection, OpenDTrdHeader header, OpenDOrder order, CancellationToken ct)
    {
        if (MoomooMaps.MarketForTrdMarket(header.TrdMarket) is not { IsSuccess: true } headerMarket)
        {
            return;
        }

        var market = MoomooOrderMapper.MarketOf(order.SecMarket, order.TrdMarket, headerMarket.Value);
        var ensured = await _resolver.EnsureAsync(
            connection,
            [new OpenDSecurity { Market = market.QotMarket, Code = order.Code?.Trim().ToUpperInvariant() ?? string.Empty }],
            ct).ConfigureAwait(false);

        var mapped = ensured.IsSuccess
            ? MoomooOrderMapper.MapOrder(order, market, _cache, _clock, lenient: true)
            : Result<BrokerOrder>.Failure(ensured.Error);

        if (mapped.IsSuccess)
        {
            Publish(new StreamEvent.OrderUpdated(mapped.Value));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{ConnectorId}: an order push was not published: {Error}", MoomooAuth.ConnectorId, mapped.Error.ToString());
        }
    }

    private async Task PublishFillAsync(MoomooConnection connection, OpenDTrdHeader header, OpenDOrderFill fill, CancellationToken ct)
    {
        if (fill.Status == MoomooMaps.FillStatusCancelled
            || MoomooMaps.MarketForTrdMarket(header.TrdMarket) is not { IsSuccess: true } headerMarket)
        {
            return;
        }

        var market = MoomooOrderMapper.MarketOf(fill.SecMarket, fill.TrdMarket, headerMarket.Value);
        var ensured = await _resolver.EnsureAsync(
            connection,
            [new OpenDSecurity { Market = market.QotMarket, Code = fill.Code?.Trim().ToUpperInvariant() ?? string.Empty }],
            ct).ConfigureAwait(false);

        var mapped = ensured.IsSuccess
            ? MoomooOrderMapper.MapTrade(fill, market, _cache, _clock)
            : Result<BrokerTrade>.Failure(ensured.Error);

        if (mapped.IsSuccess)
        {
            Publish(new StreamEvent.TradeExecuted(mapped.Value));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{ConnectorId}: a fill push was not published: {Error}", MoomooAuth.ConnectorId, mapped.Error.ToString());
        }
    }

    /// <summary>
    /// OpenD's own state, surfaced as the stream state the UI turns into its stale-data banner. A signed-out
    /// OpenD keeps the socket open and simply stops sending prices, so without this the feed would look
    /// healthy while frozen.
    /// </summary>
    private void HandleNotify(OpenDNotifyS2C notify)
    {
        switch (notify.Type)
        {
            case MoomooMaps.NotifyProgramStatus when notify.ProgramStatus?.ProgramStatus is { IsReady: false } status:
                SetState(StreamState.Degraded, $"OpenD reports status {status.TypeText}: {status.Description ?? "not ready"}.");
                break;

            case MoomooMaps.NotifyProgramStatus when State == StreamState.Degraded:
                SetState(StreamState.Connected, "OpenD is ready again.");
                break;

            case MoomooMaps.NotifyConnectionStatus when notify.ConnectStatus is { } connection:
                if (!connection.QotLogined || !connection.TrdLogined)
                {
                    SetState(StreamState.Degraded, "OpenD lost its connection to moomoo's servers; prices and order updates are stale.");
                }
                else if (State == StreamState.Degraded)
                {
                    SetState(StreamState.Connected, "OpenD reconnected to moomoo's servers.");
                }

                break;

            case MoomooMaps.NotifyGatewayEvent when notify.Event is { EventType: > 0 } gatewayEvent:
                SetState(StreamState.Degraded, gatewayEvent.Description ?? $"OpenD raised event {gatewayEvent.EventType}.");
                break;

            default:
                break;
        }
    }

    private async Task ResubscribeAsync(MoomooConnection connection, CancellationToken ct)
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

        var sent = await SendSubscriptionAsync(connection, snapshot, subscribe: true, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            // Connected but not fully subscribed is exactly what Degraded means.
            SetState(StreamState.Degraded, $"Connected, but re-subscribing failed: {sent.Error.Message}");
        }
    }

    private static async Task<Result> SendSubscriptionAsync(
        MoomooConnection connection,
        IReadOnlyCollection<Subscription> subscriptions,
        bool subscribe,
        CancellationToken ct)
    {
        foreach (var group in subscriptions.GroupBy(s => s.Mode == StreamMode.Full))
        {
            var response = await connection.RequestAsync<OpenDSubC2S, OpenDEmpty>(
                MoomooProtoId.QotSub,
                new OpenDSubC2S
                {
                    SecurityList = [.. group.Select(s => s.Security)],
                    SubTypeList = [.. MoomooMaps.ToNativeSubTypes(group.Key ? StreamMode.Full : StreamMode.Quote)],
                    IsSubOrUnSub = subscribe,
                    IsRegOrUnRegPush = subscribe,
                    IsFirstPush = subscribe ? true : null,
                },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result.Failure(response.Error);
            }
        }

        return Result.Success();
    }

    private async Task RetryUnsubscribeAsync(MoomooConnection connection, IReadOnlyList<Subscription> removed, CancellationToken lifetime)
    {
        try
        {
            await Task.Delay(_options.MinimumSubscriptionHold, lifetime).ConfigureAwait(false);

            List<Subscription> stillUnwanted;
            lock (_stateGate)
            {
                stillUnwanted = [.. removed.Where(s => !_desired.ContainsKey(s.Native))];
            }

            if (stillUnwanted.Count == 0 || !connection.IsOpen)
            {
                return;
            }

            var sent = await SendSubscriptionAsync(connection, stillUnwanted, subscribe: false, lifetime).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                _logger.LogWarning(
                    "{ConnectorId}: OpenD still refused to unsubscribe {Count} securities; their quota returns when the stream reconnects. {Error}",
                    MoomooAuth.ConnectorId,
                    stillUnwanted.Count,
                    sent.Error.ToString());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The stream stopped; its connection's subscriptions went with it.
        }
    }

    private Result<(MoomooMarket Market, string Code)> TargetFor(InstrumentKey key)
    {
        if (key.AssetClass != AssetClass.Option)
        {
            return MoomooNative.ForCashKey(key);
        }

        if (_cache.TryGetByKey(key, out var record)
            || (key is { Expiry: { } expiry, Strike: { } strike, Right: { } right }
                && MoomooMaps.MarketForVenue(key.Venue) is { IsSuccess: true } market
                && _cache.TryFindOption(market.Value, MoomooNative.CanonicalSymbol(market.Value, key.Symbol), expiry, strike, right, out record)))
        {
            return Result<(MoomooMarket, string)>.Success((record.Market, record.Code));
        }

        return Result<(MoomooMarket, string)>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"moomoo streams an option by its contract code, and none is known for {key}. Load the option chain for "
            + "that expiry before subscribing.",
            VendorCode: null,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["instrument"] = key.ToString() }));
    }

    private bool TryFindSubscription(OpenDSecurity? security, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Subscription? subscription)
    {
        subscription = null;

        if (security is null || MoomooMaps.MarketForQotMarket(security.Market) is not { IsSuccess: true } market)
        {
            return false;
        }

        var native = MoomooNative.Qualify(market.Value, security.Code.Trim().ToUpperInvariant());

        lock (_stateGate)
        {
            // A first push for something unsubscribed a moment ago is not the caller's any more.
            return _desired.TryGetValue(native, out subscription);
        }
    }

    private static T? Read<T>(MoomooPush push) where T : class =>
        JsonSerializer.Deserialize<MoomooResponse<T>>(push.Body, MoomooJson.Options)?.S2C;

    /// <summary>Exponential backoff with jitter, so every stream on a restarted OpenD does not return in the same millisecond.</summary>
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

    private static Money? MoneyOrNull(double? value, Currency currency) =>
        MoomooNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;

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
    private sealed record Subscription(InstrumentKey Key, MoomooMarket Market, string Code, StreamMode Mode)
    {
        public string Native => MoomooNative.Qualify(Market, Code);

        public OpenDSecurity Security => new() { Market = Market.QotMarket, Code = Code };

        public int QuotaUnits => MoomooMaps.ToNativeSubTypes(Mode).Count;
    }
}
