using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.AspNetCore.SignalR;

namespace Akshaya.Api.Hubs;

/// <summary>
/// The bookkeeping behind <see cref="MarketDataHub"/>: which connection wants which instrument
/// on which broker link, one live upstream connector per link no matter how many clients are
/// watching it, and the conflation that keeps a busy tape from drowning a browser tab.
///
/// ═══════════════════════ WHY ONE STREAM PER LINK, NOT PER CONNECTION ═══════════════════════
/// A broker link has exactly one upstream socket regardless of how many browser tabs are
/// watching it — that is the whole point of sharing a connector across users watching the same
/// account, and it is also a hard constraint: <see cref="ConnectorManifest.MarketData"/>'s
/// <c>MaxStreamSubscriptions</c> caps how many instruments the BROKER will accept on one
/// connection, so opening a second socket per browser tab would blow through that cap the moment
/// two people watch the same five stocks.
///
/// ═══════════════════════════════ WHY CONFLATE AT ALL ═══════════════════════════════════════
/// <see cref="IConnectorStream.Events"/>'s own doc comment says it: "back-pressure here would
/// stall ingest for every user on the connection." A single slow browser tab must never be able
/// to make the tape lag for every other trader sharing this link's socket. Conflating to at most
/// four pushes per second per instrument, and dropping every intermediate tick in between, means
/// a stalled client only ever misses PRICE HISTORY it did not need — the next flush still carries
/// the latest price — and never causes the upstream reader to block.
/// ═══════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class SubscriptionRegistry : IDisposable
{
    private const int FlushesPerSecond = 4;
    private static readonly TimeSpan ConflationInterval = TimeSpan.FromMilliseconds(1000d / FlushesPerSecond);

    private readonly IBrokerLinkStore _links;
    private readonly IConnectorFactory _connectors;
    private readonly IHubContext<MarketDataHub> _hub;
    private readonly ILogger<SubscriptionRegistry> _logger;
    private readonly IDisposable _orderSubscription;

    /// <summary>One entry per broker link with at least one live subscriber, anywhere.</summary>
    private readonly ConcurrentDictionary<string, LinkStream> _linkStreams = new(StringComparer.Ordinal);

    /// <summary>Serialises every mutation to one link's subscriber set, including its own creation and teardown.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Reverse index: which links a connection touched, so a disconnect can clean up without being told.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionLinks =
        new(StringComparer.Ordinal);

    public SubscriptionRegistry(
        IBrokerLinkStore links,
        IConnectorFactory connectors,
        IHubContext<MarketDataHub> hub,
        IEventBus events,
        ILogger<SubscriptionRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);

        _links = links;
        _connectors = connectors;
        _hub = hub;
        _logger = logger;

        // Order and execution pushes ride this same channel but come from the platform's own
        // event bus, never from a connector's raw stream: OrderStateChanged already carries the
        // tenant and user to address the push to, which a bare BrokerOrder from the wire does
        // not. One subscription here serves every link and every user.
        _orderSubscription = events.Subscribe<OrderStateChanged>(OnOrderStateChangedAsync);
    }

    /// <summary>The group a trader's own connections join, for order and execution pushes.</summary>
    public static string UserGroup(string tenantId, string userId) => $"user:{tenantId}:{userId}";

    private static string InstrumentGroup(string brokerLinkId, InstrumentKey instrument) =>
        $"md:{brokerLinkId}:{instrument}";

    /// <summary>
    /// Adds one connection's interest in a set of instruments on one link, activating the
    /// link's upstream stream on first use.
    /// </summary>
    public async Task<Result> SubscribeAsync(
        string connectionId,
        string tenantId,
        string brokerLinkId,
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentNullException.ThrowIfNull(instruments);

        if (instruments.Count == 0)
        {
            return Result.Success();
        }

        var gate = _gates.GetOrAdd(brokerLinkId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!_linkStreams.TryGetValue(brokerLinkId, out var stream))
            {
                var created = await CreateLinkStreamAsync(tenantId, brokerLinkId, ct);
                if (created.IsFailure)
                {
                    return Result.Failure(created.Error);
                }

                stream = created.Value;
                _linkStreams[brokerLinkId] = stream;
            }
            else if (!string.Equals(stream.TenantId, tenantId, StringComparison.Ordinal))
            {
                // The link id belongs to someone else. Reported the same way BrokerLinkResolver
                // reports it — as "does not exist" — never as "forbidden".
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"No broker link '{brokerLinkId}' exists for this account."));
            }

            var newlySubscribed = new List<InstrumentKey>(instruments.Count);

            // The group joins are started together rather than awaited one at a time. Against
            // the in-process group manager each is already complete when it returns, but a
            // deployment with a backplane makes every one of them a round trip — and a trader
            // loading a fifty-symbol watchlist would then wait for fifty of them in series
            // before their first tick could possibly arrive.
            var joins = new List<Task>(instruments.Count);

            foreach (var instrument in instruments)
            {
                var subscribers = stream.Subscribers.GetOrAdd(
                    instrument,
                    _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

                if (subscribers.TryAdd(connectionId, 1) && subscribers.Count == 1)
                {
                    newlySubscribed.Add(instrument);
                }

                joins.Add(_hub.Groups.AddToGroupAsync(connectionId, InstrumentGroup(brokerLinkId, instrument), ct));
            }

            await Task.WhenAll(joins);

            if (newlySubscribed.Count > 0 && stream.Connector.Stream is { } upstream)
            {
                var subscribeResult = await upstream.SubscribeAsync(newlySubscribed, StreamMode.Quote, ct);
                if (subscribeResult.IsFailure)
                {
                    _logger.LogWarning(
                        "Upstream subscribe for link {LinkId} failed for {Count} instrument(s): {Error}",
                        brokerLinkId,
                        newlySubscribed.Count,
                        subscribeResult.Error);
                }
            }

            TrackConnectionLink(connectionId, brokerLinkId);
            return Result.Success();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes one connection's interest in a set of instruments, tearing the link down once nobody is left.</summary>
    public async Task UnsubscribeAsync(
        string connectionId,
        string brokerLinkId,
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentNullException.ThrowIfNull(instruments);

        if (!_gates.TryGetValue(brokerLinkId, out var gate))
        {
            return;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (!_linkStreams.TryGetValue(brokerLinkId, out var stream))
            {
                return;
            }

            var newlyEmpty = new List<InstrumentKey>(instruments.Count);
            var leaves = new List<Task>(instruments.Count);

            foreach (var instrument in instruments)
            {
                if (stream.Subscribers.TryGetValue(instrument, out var subscribers))
                {
                    subscribers.TryRemove(connectionId, out _);
                    if (subscribers.IsEmpty)
                    {
                        newlyEmpty.Add(instrument);
                    }
                }

                leaves.Add(_hub.Groups.RemoveFromGroupAsync(connectionId, InstrumentGroup(brokerLinkId, instrument), ct));
            }

            // Concurrently, for the reason given in SubscribeAsync. Leaving matters just as
            // much: a watchlist switch is an unsubscribe followed by a subscribe, and the user
            // is waiting on both.
            await Task.WhenAll(leaves);

            if (newlyEmpty.Count > 0 && stream.Connector.Stream is { } upstream)
            {
                await upstream.UnsubscribeAsync(newlyEmpty, ct);
            }

            await TeardownIfIdleAsync(brokerLinkId, stream);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Cleans up everything a closed connection was subscribed to, across every link it touched.</summary>
    public async Task HandleDisconnectedAsync(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (!_connectionLinks.TryRemove(connectionId, out var links))
        {
            return;
        }

        foreach (var brokerLinkId in links.Keys)
        {
            if (!_gates.TryGetValue(brokerLinkId, out var gate))
            {
                continue;
            }

            await gate.WaitAsync();
            try
            {
                if (!_linkStreams.TryGetValue(brokerLinkId, out var stream))
                {
                    continue;
                }

                var newlyEmpty = new List<InstrumentKey>();
                foreach (var (instrument, subscribers) in stream.Subscribers)
                {
                    if (subscribers.TryRemove(connectionId, out _) && subscribers.IsEmpty)
                    {
                        newlyEmpty.Add(instrument);
                    }
                }

                if (newlyEmpty.Count > 0 && stream.Connector.Stream is { } upstream)
                {
                    await upstream.UnsubscribeAsync(newlyEmpty, CancellationToken.None);
                }

                await TeardownIfIdleAsync(brokerLinkId, stream);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Caller must already hold <paramref name="brokerLinkId"/>'s gate.</summary>
    private async Task<Result<LinkStream>> CreateLinkStreamAsync(string tenantId, string brokerLinkId, CancellationToken ct)
    {
        var link = await _links.GetAsync(brokerLinkId, ct);
        if (link is null || !string.Equals(link.TenantId, tenantId, StringComparison.Ordinal))
        {
            return new Error(ConnectorErrorCodes.InvalidRequest, $"No broker link '{brokerLinkId}' exists for this account.");
        }

        if (link.Session is not { } session)
        {
            return Result<LinkStream>.Failure(ConnectorErrors.ReauthRequired(link.ConnectorId));
        }

        var connectorResult = await _connectors.CreateAsync(link.ConnectorId, session, ct);
        if (connectorResult.IsFailure)
        {
            return Result<LinkStream>.Failure(connectorResult.Error);
        }

        var connector = connectorResult.Value;
        if (connector.Stream is not { } upstream)
        {
            await connector.DisposeAsync();
            return Result<LinkStream>.Failure(ConnectorErrors.NotSupported("live market data streaming"));
        }

        var connectResult = await upstream.ConnectAsync(ct);
        if (connectResult.IsFailure)
        {
            await connector.DisposeAsync();
            return Result<LinkStream>.Failure(connectResult.Error);
        }

        var stream = new LinkStream(tenantId, connector);
        stream.PumpTask = PumpAsync(brokerLinkId, stream, stream.Cts.Token);
        stream.ConflationTimer = new Timer(
            callback: state => _ = FlushConflatedAsync(brokerLinkId, (LinkStream)state!),
            state: stream,
            dueTime: ConflationInterval,
            period: ConflationInterval);

        return stream;
    }

    /// <summary>Caller must already hold <paramref name="brokerLinkId"/>'s gate.</summary>
    private async Task TeardownIfIdleAsync(string brokerLinkId, LinkStream stream)
    {
        if (stream.Subscribers.Any(kv => !kv.Value.IsEmpty))
        {
            return;
        }

        _linkStreams.TryRemove(brokerLinkId, out _);
        stream.ConflationTimer.Dispose();
        stream.Cts.Cancel();

        try
        {
            await stream.PumpTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: this is exactly what cancelling the pump above causes.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market-data pump for link {LinkId} faulted during teardown.", brokerLinkId);
        }

        if (stream.Connector.Stream is { } upstream)
        {
            await upstream.DisconnectAsync(CancellationToken.None);
        }

        await stream.Connector.DisposeAsync();
        stream.Cts.Dispose();
    }

    /// <summary>Reads one link's upstream events for as long as anyone is subscribed to it.</summary>
    private async Task PumpAsync(string brokerLinkId, LinkStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var streamEvent in stream.Connector.Stream!.Events(ct))
            {
                switch (streamEvent)
                {
                    case StreamEvent.TickReceived tickReceived:
                        // Overwriting rather than queuing IS the conflation: only the latest
                        // tick per instrument survives until the next flush, and every
                        // intermediate one is deliberately dropped.
                        stream.Pending[tickReceived.Tick.Instrument] = tickReceived.Tick;
                        break;

                    case StreamEvent.ConnectionChanged connectionChanged:
                        await BroadcastStreamStateAsync(brokerLinkId, stream, connectionChanged, ct);
                        break;

                    // Depth has no subscriber in this hub yet, and order/trade events are
                    // pushed from OrderStateChanged on the event bus instead — see the
                    // constructor for why that is the correct source for those.
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown: the link has no subscribers left.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Market-data pump for link {LinkId} crashed; its subscribers will see no more ticks until they resubscribe.",
                brokerLinkId);
        }
    }

    private async Task BroadcastStreamStateAsync(
        string brokerLinkId,
        LinkStream stream,
        StreamEvent.ConnectionChanged evt,
        CancellationToken ct)
    {
        var push = new StreamStatePush(brokerLinkId, evt.State.ToString(), evt.Reason);

        // A snapshot: sending must never run inside the same enumeration a concurrent
        // subscribe/unsubscribe is mutating. The sends themselves go out together — this runs
        // on the upstream pump, and walking a large watchlist one group at a time would hold
        // up every tick behind the reconnect notice that triggered it.
        var sends = stream.Subscribers.Keys
            .ToArray()
            .Select(instrument => SendStreamStateAsync(brokerLinkId, instrument, push, ct));

        await Task.WhenAll(sends);
    }

    private async Task SendStreamStateAsync(
        string brokerLinkId,
        InstrumentKey instrument,
        StreamStatePush push,
        CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Group(InstrumentGroup(brokerLinkId, instrument)).SendAsync("streamState", push, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast stream state for {LinkId}/{Instrument}.", brokerLinkId, instrument);
        }
    }

    /// <summary>
    /// The conflation flush: at most once per <see cref="ConflationInterval"/>, per instrument, per link.
    ///
    /// ═══════════════════ TWO THINGS HERE ARE LOAD-BEARING FOR LATENCY ═══════════════════
    ///
    /// NON-REENTRANT. The timer fires every 250ms whether or not the previous flush has
    /// finished, and the callback that starts it is fire-and-forget. A watchlist big enough
    /// that a flush takes longer than the interval would therefore start a second flush on top
    /// of the first, then a third, until the overlapping flushes were competing for the same
    /// Pending dictionary and the tape fell steadily further behind — the classic timer
    /// pile-up, and it gets worse exactly when the market is busiest. The gate below drops a
    /// tick that arrives while a flush is running, which is CORRECT rather than merely
    /// acceptable: conflation already drops intermediate ticks by design, and the next flush
    /// carries the latest price regardless.
    ///
    /// CONCURRENT SENDS. Each instrument goes to its own SignalR group, and those groups are
    /// independent. Awaiting them one after another made a hundred-symbol watchlist a hundred
    /// sequential round trips through the hub four times a second, so the last symbol in the
    /// dictionary updated visibly later than the first. Started together they cost one.
    /// ═══════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private async Task FlushConflatedAsync(string brokerLinkId, LinkStream stream)
    {
        if (stream.Pending.IsEmpty || !stream.TryBeginFlush())
        {
            return;
        }

        try
        {
            List<Task>? sends = null;

            foreach (var instrument in stream.Pending.Keys.ToArray())
            {
                if (!stream.Pending.TryRemove(instrument, out var tick))
                {
                    continue;
                }

                (sends ??= []).Add(SendTickAsync(brokerLinkId, stream, instrument, tick));
            }

            if (sends is not null)
            {
                await Task.WhenAll(sends);
            }
        }
        finally
        {
            stream.EndFlush();
        }
    }

    /// <summary>
    /// One instrument's push, with its failure swallowed rather than thrown.
    ///
    /// Because the sends now run together, an exception escaping here would fault the whole
    /// <c>Task.WhenAll</c> — and that task is awaited inside a fire-and-forget flush, so a
    /// single wedged browser tab would take down the tape for every other subscriber on the
    /// link. The failure is logged against the instrument it belongs to and nothing else stops.
    /// </summary>
    private async Task SendTickAsync(string brokerLinkId, LinkStream stream, InstrumentKey instrument, Tick tick)
    {
        try
        {
            await _hub.Clients
                .Group(InstrumentGroup(brokerLinkId, instrument))
                .SendAsync("tick", MarketTickPush.From(tick), stream.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The link was torn down mid-flush; nothing further to send.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push a conflated tick for {Instrument} on link {LinkId}.", instrument, brokerLinkId);
        }
    }

    private Task OnOrderStateChangedAsync(OrderStateChanged evt, CancellationToken ct) =>
        _hub.Clients.Group(UserGroup(evt.TenantId, evt.UserId)).SendAsync("orderUpdate", OrderUpdatePush.From(evt), ct);

    private void TrackConnectionLink(string connectionId, string brokerLinkId)
    {
        var set = _connectionLinks.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        set[brokerLinkId] = 1;
    }

    public void Dispose()
    {
        _orderSubscription.Dispose();

        // Best-effort on shutdown: cancel every pump and let the process exit reclaim the rest
        // rather than blocking shutdown on cooperative cancellation completing.
        foreach (var stream in _linkStreams.Values)
        {
            stream.ConflationTimer?.Dispose();
            stream.Cts.Cancel();
        }

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    /// <summary>Everything the registry holds for one broker link with at least one live subscriber.</summary>
    private sealed class LinkStream(string tenantId, IBrokerConnector connector)
    {
        public string TenantId { get; } = tenantId;

        public IBrokerConnector Connector { get; } = connector;

        /// <summary>Instrument -> the connection ids currently watching it.</summary>
        public ConcurrentDictionary<InstrumentKey, ConcurrentDictionary<string, byte>> Subscribers { get; } =
            new();

        /// <summary>The latest, not-yet-flushed tick per instrument. See <see cref="FlushConflatedAsync"/>.</summary>
        public ConcurrentDictionary<InstrumentKey, Tick> Pending { get; } = new();

        public CancellationTokenSource Cts { get; } = new();

        public Task PumpTask { get; set; } = Task.CompletedTask;

        public Timer ConflationTimer { get; set; } = null!;

        /// <summary>0 = idle, 1 = a flush is running. See <see cref="FlushConflatedAsync"/>.</summary>
        private int _flushing;

        /// <summary>True when this caller won the right to flush; false when one is already running.</summary>
        public bool TryBeginFlush() => Interlocked.CompareExchange(ref _flushing, 1, 0) == 0;

        public void EndFlush() => Interlocked.Exchange(ref _flushing, 0);
    }
}

/// <summary>Wire shape of one tick push. Deliberately thinner than <see cref="Tick"/>: a chart needs a price, not the whole quote.</summary>
public sealed record MarketTickPush(
    InstrumentKey Instrument,
    Money LastPrice,
    Quantity? LastQuantity,
    long? Volume,
    Money? BidPrice,
    Money? AskPrice,
    DateTimeOffset Timestamp)
{
    public static MarketTickPush From(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        return new MarketTickPush(
            tick.Instrument,
            tick.LastPrice,
            tick.LastQuantity,
            tick.Volume,
            tick.BidPrice,
            tick.AskPrice,
            tick.Timestamp);
    }
}

/// <summary>Wire shape of one order/execution push, projected from <see cref="OrderStateChanged"/>.</summary>
public sealed record OrderUpdatePush(
    Guid OrderId,
    Guid ClientOrderId,
    string BrokerLinkId,
    InstrumentKey Instrument,
    OrderState State,
    OrderStatus Status,
    Quantity FilledQuantity,
    Money? AveragePrice,
    string? Message,
    DateTimeOffset At)
{
    public static OrderUpdatePush From(OrderStateChanged evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return new OrderUpdatePush(
            evt.OrderId,
            evt.ClientOrderId,
            evt.BrokerLinkId,
            evt.Instrument,
            evt.State,
            evt.Status,
            evt.FilledQuantity,
            evt.AveragePrice,
            evt.Message,
            evt.At);
    }
}

/// <summary>Pushed when a link's upstream connection state changes, so the UI can show a stale-data banner.</summary>
public sealed record StreamStatePush(string BrokerLinkId, string State, string? Reason);
