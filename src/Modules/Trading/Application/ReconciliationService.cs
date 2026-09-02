using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>Tuning for <see cref="ReconciliationService"/>.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>
    /// How often every active link is polled. Thirty seconds is a compromise: shorter burns a
    /// broker's data rate limit that the trader's own quotes need, longer leaves a mis-sequenced
    /// fill on screen for an uncomfortable time. Streams carry the fast path; this is the safety net.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How close in time two records must be to be paired heuristically. Sixty seconds is wide
    /// enough for a slow acknowledgement and narrow enough that two genuinely separate orders
    /// for the same instrument and size are unlikely to fall inside it.
    /// </summary>
    public TimeSpan MatchWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a local order may go unmatched at the broker before it is declared unaccounted
    /// for. Must be comfortably longer than a broker's own order-book propagation delay, or
    /// every freshly placed order raises an alarm.
    /// </summary>
    public TimeSpan UnaccountedAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether orders found at the broker that the platform has never seen are adopted into the
    /// local blotter. On by default: a user who places an order in their broker's own app and
    /// then looks at our position screen must not see a lie.
    /// </summary>
    public bool AdoptBrokerOnlyOrders { get; set; } = true;
}

/// <summary>What one reconciliation pass found. Returned for tests, logs and the health endpoint.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="LocalExamined">Local orders considered.</param>
/// <param name="BrokerExamined">Broker orders considered.</param>
/// <param name="Corrected">Local orders the broker disagreed with, and which were corrected.</param>
/// <param name="Adopted">Broker orders the platform had never seen and took ownership of.</param>
/// <param name="Unaccounted">Local orders the broker has no record of.</param>
/// <param name="Conflicts">Pairs that could not be reconciled at all and need a human.</param>
public sealed record ReconciliationReport(
    string BrokerLinkId,
    int LocalExamined,
    int BrokerExamined,
    int Corrected,
    int Adopted,
    int Unaccounted,
    int Conflicts);

/// <summary>
/// Keeps the platform's order book honest against every broker's.
///
/// ═══════════════════════ THE BROKER IS THE SOURCE OF TRUTH ═══════════════════════
/// Our order aggregate is a cache with opinions. The broker's order book is what the venue
/// acted on, what the counterparty saw, and what settlement will use. Wherever the two
/// disagree, the broker wins — unconditionally, with no heuristic about which looks more
/// plausible, and with the correction recorded as an <see cref="OrderDrifted"/> event so that
/// a human can see the platform was wrong. A reconciler that sometimes prefers the local copy
/// is a reconciler that hides the bug it exists to expose.
/// ═════════════════════════════════════════════════════════════════════════════════
///
/// It runs on two triggers, and both are necessary:
///
///  * ON AN INTERVAL, because streams drop updates and because an order placed during a
///    deployment has nobody listening for its fill.
///  * AFTER EVERY STREAM RECONNECT, because the gap between disconnect and reconnect is
///    exactly the window in which updates were missed, and the reconnect is the only moment we
///    know that window closed. Polling alone would leave a trader looking at a stale blotter
///    for up to a full interval after the socket came back.
///
/// MATCHING, in order of confidence:
///  1. ClientOrderId — exact. This is why we generate one and persist it before sending.
///  2. BrokerOrderId — exact, once the broker has told us one.
///  3. (instrument, side, quantity) inside a timestamp window — a HEURISTIC, used only for
///     brokers that do not round-trip a client id. It is reported as a heuristic on every
///     event it produces, and it refuses to match when two candidates fit, because a wrong
///     match corrupts two orders instead of leaving one uncertain.
///
/// Shaped like a BackgroundService but deliberately NOT derived from one: this assembly does
/// not depend on Microsoft.Extensions.Hosting, so the API wraps <see cref="ExecuteAsync"/> in a
/// hosted service and tests call <see cref="ReconcileLinkAsync"/> directly with no host at all.
/// </summary>
public sealed class ReconciliationService(
    IBrokerLinkStore links,
    BrokerLinkResolver linkResolver,
    IOrderRepository orders,
    IEventBus events,
    IClock clock,
    ReconciliationOptions options,
    ILogger<ReconciliationService> logger)
{
    private readonly ReconciliationOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The polling loop. Runs until cancelled and never throws out of itself.</summary>
    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Reconciliation loop started; polling every {Interval}.",
            _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval);

        // One pass immediately on startup. The most likely reason this process is starting is
        // that the previous one stopped, and everything that happened in between was missed.
        await SafePassAsync(stoppingToken);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            await SafePassAsync(stoppingToken);
        }

        logger.LogInformation("Reconciliation loop stopped.");
    }

    /// <summary>
    /// Reconciles one link immediately. Call this from the stream supervisor the moment a
    /// connection is re-established — see the class remarks for why the interval alone is not
    /// enough.
    /// </summary>
    public async Task<Result<ReconciliationReport>> ReconcileAfterReconnectAsync(
        string brokerLinkId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);

        var link = await links.GetAsync(brokerLinkId, ct);
        if (link is null)
        {
            return new Error(ConnectorErrorCodes.InvalidRequest, $"No broker link '{brokerLinkId}'.");
        }

        logger.LogInformation("Reconciling link {LinkId} after a stream reconnect.", brokerLinkId);
        return await ReconcileLinkAsync(link, "stream-reconnect", ct);
    }

    /// <summary>One pass over every active link.</summary>
    public async Task<IReadOnlyList<ReconciliationReport>> ReconcileAllAsync(CancellationToken ct = default)
    {
        var active = await links.ListActiveAsync(ct);
        var reports = new List<ReconciliationReport>(active.Count);

        foreach (var link in active.Where(l => l.IsUsable))
        {
            ct.ThrowIfCancellationRequested();

            var report = await ReconcileLinkAsync(link, "interval", ct);
            if (report.IsSuccess)
            {
                reports.Add(report.Value);
            }
            else
            {
                // One unreachable broker must not stop the others being reconciled. The failure
                // is logged and the loop continues; a link that stays unreachable shows up in
                // its own health check, not by starving every other link of reconciliation.
                logger.LogWarning(
                    "Reconciliation of link {LinkId} failed: {Error}",
                    link.Id,
                    report.Error);
            }
        }

        return reports;
    }

    /// <summary>
    /// Fetches the broker's order book for one link and reconciles the local orders against it.
    /// </summary>
    public async Task<Result<ReconciliationReport>> ReconcileLinkAsync(
        BrokerLink link,
        string trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        var connectorResult = await linkResolver.ConnectAsync(link, ct);
        if (connectorResult.IsFailure)
        {
            return Result<ReconciliationReport>.Failure(connectorResult.Error);
        }

        await using var connector = connectorResult.Value;

        // The WHOLE day's book, not just the open orders: an order that filled or was cancelled
        // since the last pass is precisely the one we are looking for, and OpenOnly would hide it.
        var bookResult = await connector.Orders.GetOrdersAsync(new OrderQuery { OpenOnly = false }, ct);
        if (bookResult.IsFailure)
        {
            return Result<ReconciliationReport>.Failure(bookResult.Error);
        }

        var book = bookResult.Value;
        var local = await orders.ListReconcilableAsync(link.Id, ct);
        var now = clock.UtcNow;

        var byClientOrderId = book
            .Where(o => o.ClientOrderId is not null)
            .GroupBy(o => o.ClientOrderId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var byBrokerOrderId = book
            .GroupBy(o => o.BrokerOrderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var corrected = 0;
        var unaccounted = 0;
        var conflicts = 0;

        foreach (var order in local)
        {
            var match = Match(order, byClientOrderId, byBrokerOrderId, book, claimed);

            if (match is null)
            {
                if (await HandleUnaccountedAsync(order, now, ct))
                {
                    unaccounted++;
                }

                continue;
            }

            var (brokerOrder, method) = match.Value;
            claimed.Add(brokerOrder.BrokerOrderId);

            var before = order.State;
            bool changed;
            try
            {
                changed = order.ReconcileWith(brokerOrder, now, Render(brokerOrder));
            }
            catch (InvalidOperationException ex)
            {
                // The only way here is a filled order the broker reports as working, which means
                // the match itself is wrong. Never guess past it — leave both records alone and
                // raise it for a human.
                conflicts++;
                logger.LogError(
                    ex,
                    "Irreconcilable conflict on order {OrderId} (local {LocalState}, broker {BrokerStatus}) "
                    + "matched by {Method}. Left unchanged for manual review.",
                    order.Id,
                    order.State,
                    brokerOrder.Status,
                    method);

                await events.PublishAsync(
                    new OrderDrifted(
                        order.Id,
                        order.ClientOrderId,
                        order.TenantId,
                        order.UserId,
                        order.BrokerLinkId,
                        order.State,
                        brokerOrder.Status.ToOrderState(),
                        method,
                        "Irreconcilable: a filled order cannot return to a working state. Records may be mismatched.",
                        now),
                    ct);

                continue;
            }

            if (!changed)
            {
                continue;
            }

            corrected++;
            await orders.SaveAsync(order, ct);

            logger.LogInformation(
                "Order {OrderId} drifted: local {Before} -> broker {After} (matched by {Method}, trigger {Trigger}).",
                order.Id,
                before,
                order.State,
                method,
                trigger);

            await events.PublishAsync(
                new OrderDrifted(
                    order.Id,
                    order.ClientOrderId,
                    order.TenantId,
                    order.UserId,
                    order.BrokerLinkId,
                    before,
                    order.State,
                    method,
                    $"Corrected {before} -> {order.State} against the broker's order book.",
                    now),
                ct);

            await PublishStateAsync(order, now, ct);
        }

        var adopted = 0;
        if (_options.AdoptBrokerOnlyOrders)
        {
            foreach (var brokerOrder in book.Where(o => !claimed.Contains(o.BrokerOrderId)))
            {
                if (await AdoptAsync(link, brokerOrder, now, ct))
                {
                    adopted++;
                }
            }
        }

        return new ReconciliationReport(link.Id, local.Count, book.Count, corrected, adopted, unaccounted, conflicts);
    }

    /// <summary>
    /// Pairs a local order with a broker order, in descending order of confidence.
    ///
    /// The heuristic leg refuses to match when more than one candidate fits. Two orders for the
    /// same instrument, side and size within a minute of each other is exactly the shape of an
    /// order that was accidentally sent twice — the situation where a wrong match does the most
    /// damage, because it would silently conceal the duplicate.
    /// </summary>
    private (BrokerOrder Order, OrderMatchMethod Method)? Match(
        Order order,
        IReadOnlyDictionary<Guid, BrokerOrder> byClientOrderId,
        IReadOnlyDictionary<string, BrokerOrder> byBrokerOrderId,
        IReadOnlyList<BrokerOrder> book,
        IReadOnlySet<string> claimed)
    {
        if (byClientOrderId.TryGetValue(order.ClientOrderId, out var byClient))
        {
            return (byClient, OrderMatchMethod.ClientOrderId);
        }

        if (order.BrokerOrderId is { Length: > 0 } brokerOrderId
            && byBrokerOrderId.TryGetValue(brokerOrderId, out var byBroker))
        {
            return (byBroker, OrderMatchMethod.BrokerOrderId);
        }

        var candidates = book
            .Where(b => !claimed.Contains(b.BrokerOrderId)
                        && b.ClientOrderId is null
                        && b.Instrument == order.Instrument
                        && b.Side == order.Request.Side
                        && b.Quantity == order.Request.Quantity
                        && Within(b.PlacedAt, order.CreatedAt, _options.MatchWindow))
            .Take(2)
            .ToArray();

        if (candidates.Length == 1)
        {
            return (candidates[0], OrderMatchMethod.Heuristic);
        }

        if (candidates.Length > 1)
        {
            logger.LogWarning(
                "Order {OrderId} has {Count} equally plausible matches at the broker; refusing to guess.",
                order.Id,
                candidates.Length);
        }

        return null;
    }

    /// <summary>
    /// Handles a local order the broker has no record of. THE DANGEROUS DIRECTION.
    ///
    /// It means either the order never arrived — and the trader believes they are positioned
    /// when they are not — or the broker's book has not caught up. We do not guess between
    /// those. Inside the grace period we say nothing; past it we mark the order Unknown and
    /// raise <see cref="OrderUnaccountedFor"/> for a human. What we never do is quietly mark it
    /// cancelled, which would tell the trader a comforting thing that might be false.
    /// </summary>
    private async Task<bool> HandleUnaccountedAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        var age = now - order.CreatedAt;
        if (age < _options.UnaccountedAfter)
        {
            return false;
        }

        if (order.State != OrderState.Unknown)
        {
            order.MarkUnknown(
                now,
                "The broker's order book has no record of this order.",
                OrderActors.Reconciliation);

            await orders.SaveAsync(order, ct);
            await PublishStateAsync(order, now, ct);
        }

        await events.PublishAsync(
            new OrderUnaccountedFor(
                order.Id,
                order.ClientOrderId,
                order.TenantId,
                order.UserId,
                order.BrokerLinkId,
                age,
                now),
            ct);

        logger.LogError(
            "Order {OrderId} (client {ClientOrderId}) has been unaccounted for at the broker for {Age}.",
            order.Id,
            order.ClientOrderId,
            age);

        return true;
    }

    /// <summary>
    /// Takes ownership of an order that exists at the broker and not here — placed in the
    /// broker's own app, by another platform instance, or before this account was linked.
    ///
    /// Adopting it is the broker-wins principle applied to existence itself. A blotter that
    /// hides orders the user really has is worse than one that shows an order it did not place.
    /// The adopted order enters at <see cref="OrderState.Unknown"/> and is immediately corrected
    /// to the broker's state, so its event log honestly records that we discovered it rather
    /// than pretending we submitted it.
    /// </summary>
    private async Task<bool> AdoptAsync(BrokerLink link, BrokerOrder brokerOrder, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await orders.GetByBrokerOrderIdAsync(link.Id, brokerOrder.BrokerOrderId, ct);
        if (existing is not null)
        {
            return false;
        }

        var request = new PlaceOrderRequest
        {
            ClientOrderId = brokerOrder.ClientOrderId ?? Guid.CreateVersion7(),
            Instrument = brokerOrder.Instrument,
            Side = brokerOrder.Side,
            Quantity = brokerOrder.Quantity,
            OrderType = brokerOrder.OrderType,
            PositionEffect = brokerOrder.PositionEffect,
            TimeInForce = brokerOrder.TimeInForce,
            Variety = brokerOrder.Variety,
            LimitPrice = brokerOrder.LimitPrice,
            TriggerPrice = brokerOrder.TriggerPrice,
            Tag = "adopted",
        };

        var order = Order.Create(
            link.TenantId,
            link.UserId,
            link.Id,
            link.ConnectorId,
            request,
            brokerOrder.PlacedAt,
            OrderActors.Reconciliation);

        order.MarkUnknown(
            now,
            "Discovered in the broker's order book; this order was not placed through the platform.",
            OrderActors.Reconciliation);

        order.ReconcileWith(brokerOrder, now, Render(brokerOrder));
        await orders.SaveAsync(order, ct);

        logger.LogInformation(
            "Adopted broker order {BrokerOrderId} on link {LinkId} as {OrderId}.",
            brokerOrder.BrokerOrderId,
            link.Id,
            order.Id);

        await events.PublishAsync(
            new OrderDrifted(
                order.Id,
                order.ClientOrderId,
                order.TenantId,
                order.UserId,
                order.BrokerLinkId,
                OrderState.Unknown,
                order.State,
                OrderMatchMethod.BrokerOnly,
                "Order existed at the broker but not on the platform; adopted.",
                now),
            ct);

        await PublishStateAsync(order, now, ct);
        return true;
    }

    private Task PublishStateAsync(Order order, DateTimeOffset at, CancellationToken ct) =>
        events.PublishAsync(
            new OrderStateChanged(
                order.Id,
                order.ClientOrderId,
                order.TenantId,
                order.UserId,
                order.BrokerLinkId,
                order.Instrument,
                order.State,
                order.Status,
                order.FilledQuantity,
                order.AveragePrice,
                order.StatusMessage,
                at),
            ct);

    private static bool Within(DateTimeOffset a, DateTimeOffset b, TimeSpan window) =>
        (a - b).Duration() <= window;

    /// <summary>
    /// A stable rendering of the broker's order, stored on the event as its raw payload.
    ///
    /// CONTRACT NOTE: <see cref="IConnectorOrders"/> returns a typed <see cref="BrokerOrder"/>,
    /// not the vendor's original JSON, so this is the most faithful "raw" the core can see. The
    /// true wire payload is available in the connector's own audit trail, joined on
    /// ClientOrderId.
    /// </summary>
    private static string Render(BrokerOrder order) => string.Create(
        CultureInfo.InvariantCulture,
        $"brokerOrderId={order.BrokerOrderId}; status={order.Status}; qty={order.Quantity}; filled={order.FilledQuantity}; avg={order.AveragePrice}; updated={order.UpdatedAt:o}; message={order.StatusMessage}");

    private async Task SafePassAsync(CancellationToken ct)
    {
        try
        {
            await ReconcileAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // The reconciliation loop must outlive any single failure. A loop that dies on an
            // unexpected exception stops correcting every order on the platform, silently.
            logger.LogError(ex, "Reconciliation pass failed; the loop continues.");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
