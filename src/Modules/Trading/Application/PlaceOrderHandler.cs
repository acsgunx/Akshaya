using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Domain.Rules;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// THE CRITICAL PATH.
///
/// Everything between a trader pressing "buy" and a broker receiving the order happens here, in
/// this order, for these reasons:
///
///  1. VALIDATE THE REQUEST. Structural problems are the caller's to fix and cost nothing to
///     detect. Doing this first means a malformed order never consumes a rate-limit permit, a
///     quote, or an FX lookup.
///
///  2. RESOLVE THE CONNECTOR. Also proves the link exists, belongs to this tenant and has a
///     live session. Doing it before the risk gate is what lets step 3 read the manifest, and
///     it fails fast on the most common operational problem — an expired session — with the one
///     error the user can actually act on.
///
///  3. CHECK MANIFEST CAPABILITY. Before any market data call, because an order type this
///     broker cannot place is refusable for free. It runs the SAME rule object the risk gate
///     uses, not a copy: two implementations of "can this broker do this" would eventually
///     disagree, and the disagreement would surface as an order the UI offered and the broker
///     rejected.
///
///  4. RUN THE RISK GATE. After the cheap checks, before anything irreversible. This is the
///     last moment at which refusing the order is free.
///
///  5. PERSIST AS PendingSubmit, WITH ITS ClientOrderId, BEFORE THE PLACE CALL. The single most
///     important line in this file. If the process dies between the write and the broker's
///     answer, reconciliation finds a local order with an idempotency key and can ask the
///     broker what became of it. Without the write there is nothing to reconcile: the order may
///     be live at the venue and no part of the platform knows it exists.
///
///  6. CALL THE CONNECTOR.
///
///  7. ON SUCCESS, transition to Submitted and then to Acknowledged when the broker says the
///     order is live at the venue. Two steps, not one, because "the broker took my HTTP
///     request" and "the order is working at the exchange" are different facts and a trader
///     needs to know which one they have.
///
///  8. ON TIMEOUT, DO NOT RETRY. Mark the order Unknown and hand it to reconciliation. A retry
///     is a coin flip between a recovered order and a DUPLICATE one, and the duplicate is
///     unbounded in cost — it is a second real position the trader never asked for. The
///     resilience decorator in the connector host already refuses to retry writes for exactly
///     this reason; this handler must not undo that at a higher level.
/// </summary>
public sealed class PlaceOrderHandler(
    IValidator<PlaceOrderCommand> validator,
    BrokerLinkResolver linkResolver,
    RiskGate riskGate,
    CapabilitySupportedRule capabilityRule,
    IRiskPolicyStore policies,
    IRiskSnapshotProvider snapshots,
    IOrderRepository orders,
    IEventBus events,
    IAuditSink audit,
    IClock clock,
    ILogger<PlaceOrderHandler> logger)
{
    private readonly IValidator<PlaceOrderCommand> _validator = validator;
    private readonly BrokerLinkResolver _linkResolver = linkResolver;
    private readonly RiskGate _riskGate = riskGate;
    private readonly CapabilitySupportedRule _capabilityRule = capabilityRule;
    private readonly IRiskPolicyStore _policies = policies;
    private readonly IRiskSnapshotProvider _snapshots = snapshots;
    private readonly IOrderRepository _orders = orders;
    private readonly IEventBus _events = events;
    private readonly IAuditSink _audit = audit;
    private readonly IClock _clock = clock;
    private readonly ILogger<PlaceOrderHandler> _logger = logger;

    public async Task<Result<PlaceOrderResult>> HandleAsync(PlaceOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ── 1. Validate ────────────────────────────────────────────────────────────────────
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var clientOrderId = command.ClientOrderId ?? Guid.CreateVersion7();

        // Idempotency. A client that retries the same intent with the same key must get the
        // original order back, not a second one. This is the reason ClientOrderId is the
        // caller's to choose.
        var existing = await _orders.GetByClientOrderIdAsync(clientOrderId, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Order with client id {ClientOrderId} already exists ({OrderId}); returning it unchanged.",
                clientOrderId,
                existing.Id);

            return Describe(existing);
        }

        var request = command.ToRequest(clientOrderId);

        // ── 2. Resolve the connector ───────────────────────────────────────────────────────
        var linkResult = await _linkResolver.GetLinkAsync(command.TenantId, command.BrokerLinkId, ct);
        if (linkResult.IsFailure)
        {
            return Result<PlaceOrderResult>.Failure(linkResult.Error);
        }

        var link = linkResult.Value;

        var connectorResult = await _linkResolver.ConnectAsync(link, ct);
        if (connectorResult.IsFailure)
        {
            return Result<PlaceOrderResult>.Failure(connectorResult.Error);
        }

        await using var connector = connectorResult.Value;
        var manifest = connector.Manifest;
        var policy = await _policies.GetAsync(command.TenantId, ct);
        var now = _clock.UtcNow;

        // ── 3. Manifest capability ─────────────────────────────────────────────────────────
        // Deliberately the same rule instance the gate holds. No market data yet.
        var capabilityContext = new RiskEvaluationContext
        {
            TenantId = command.TenantId,
            UserId = command.UserId,
            BrokerLinkId = link.Id,
            ConnectorId = link.ConnectorId,
            Manifest = manifest,
            Request = request,
            Policy = policy,
            At = now,
        };

        var capability = await _capabilityRule.EvaluateAsync(capabilityContext, ct);
        if (!capability.IsAllowed)
        {
            return await RefuseAsync(command, link, request, capability, now, ct);
        }

        // ── 4. Risk gate ───────────────────────────────────────────────────────────────────
        // Market data and account state are fetched once, here, and shared by every rule so
        // that ten rules do not make ten calls and every rule judges the same instant.
        var lastTradedPrice = await TryGetLastPriceAsync(connector, request.Instrument, ct);
        var instrument = await TryResolveInstrumentAsync(connector, request.Instrument, ct);
        var snapshot = await _snapshots.GetAsync(command.TenantId, command.UserId, link.Id, ct);

        var riskContext = capabilityContext with
        {
            Instrument = instrument,
            LastTradedPrice = lastTradedPrice,
            Snapshot = snapshot,
            IsReducingExposure = IsReducingExposure(snapshot, request),
        };

        var decision = await _riskGate.EvaluateAsync(riskContext, ct);
        if (!decision.IsAllowed)
        {
            return await RefuseAsync(command, link, request, decision, now, ct);
        }

        // ── 5. Persist BEFORE the network call ─────────────────────────────────────────────
        var order = Order.Create(
            command.TenantId,
            command.UserId,
            link.Id,
            link.ConnectorId,
            request,
            now,
            command.Actor);

        order.MarkRiskChecked(now, $"Passed {_riskGate.RuleNames.Count} pre-trade rule(s).");

        // If this write fails, nothing has been sent and the caller gets a clean failure. If it
        // succeeds and everything after it fails, reconciliation has something to find.
        await _orders.SaveAsync(order, ct);
        await PublishStateAsync(order, ct);

        await _audit.RecordAsync(
            new AuditRecord
            {
                At = now,
                TenantId = command.TenantId,
                Actor = command.Actor,
                Action = "order.place.submitting",
                Subject = order.Id.ToString(),
                Detail = $"{request.Side} {request.Quantity} {request.Instrument} as {request.OrderType}.",
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientOrderId"] = clientOrderId.ToString(),
                    ["brokerLinkId"] = link.Id,
                    ["connectorId"] = link.ConnectorId,
                },
            },
            ct);

        // ── 6. Send ────────────────────────────────────────────────────────────────────────
        Result<OrderAck> ack;
        try
        {
            ack = await connector.Orders.PlaceAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The connector's own timeout surfaced as a cancellation rather than a Result.
            // Treated exactly like a Timeout error: we do not know, so we say we do not know.
            return await AmbiguousAsync(order, "The broker did not answer in time.", ct);
        }

        if (ack.IsFailure)
        {
            // ── 8. Ambiguity is not failure ────────────────────────────────────────────────
            if (IsAmbiguous(ack.Error))
            {
                return await AmbiguousAsync(
                    order,
                    $"The broker did not give a usable answer ({ack.Error.Code}); the order may or may not exist.",
                    ct);
            }

            var sendTime = _clock.UtcNow;
            order.MarkRejected(sendTime, ack.Error.Message, OrderActors.Broker, ack.Error.VendorMessage);
            await _orders.SaveAsync(order, ct);
            await PublishStateAsync(order, ct);
            await AuditOutcomeAsync(order, "order.place.rejected", false, ack.Error.ToString(), ct);

            return Result<PlaceOrderResult>.Failure(ack.Error);
        }

        // ── 7. Success ─────────────────────────────────────────────────────────────────────
        var acknowledged = ack.Value;
        order.MarkSubmitted(acknowledged.AcknowledgedAt, acknowledged.BrokerOrderId, acknowledged.Message);

        // Only claim the order is live at the venue when the broker actually said so. Anything
        // else stays Submitted and reconciliation confirms it moments later.
        var brokerState = acknowledged.Status.ToOrderState();
        if (brokerState is OrderState.Acknowledged or OrderState.PartiallyFilled or OrderState.Filled
            or OrderState.Cancelled or OrderState.Rejected or OrderState.Expired)
        {
            ApplyBrokerState(order, brokerState, acknowledged);
        }

        await _orders.SaveAsync(order, ct);
        await PublishStateAsync(order, ct);
        await AuditOutcomeAsync(order, "order.place.accepted", true, acknowledged.Message, ct);

        return Describe(order);
    }

    /// <summary>
    /// Errors after which the order's existence at the broker is genuinely unknown.
    ///
    /// Timeout is the obvious one. Unknown is included because a connector that cannot classify
    /// its own failure has, by definition, not told us whether the write landed. Everything
    /// else — invalid request, insufficient funds, market closed — is a definite refusal and is
    /// recorded as a rejection.
    /// </summary>
    private static bool IsAmbiguous(Error error) =>
        error.Code is ConnectorErrorCodes.Timeout or ConnectorErrorCodes.Unknown;

    private async Task<Result<PlaceOrderResult>> AmbiguousAsync(Order order, string reason, CancellationToken ct)
    {
        var at = _clock.UtcNow;
        order.MarkUnknown(at, reason);
        await _orders.SaveAsync(order, ct);
        await PublishStateAsync(order, ct);

        await _events.PublishAsync(
            new OrderUnaccountedFor(
                order.Id,
                order.ClientOrderId,
                order.TenantId,
                order.UserId,
                order.BrokerLinkId,
                TimeSpan.Zero,
                at),
            ct);

        await AuditOutcomeAsync(order, "order.place.unknown", false, reason, ct);

        _logger.LogWarning(
            "Order {OrderId} (client {ClientOrderId}) is UNKNOWN after send: {Reason}. "
            + "Not retrying; reconciliation will resolve it against the broker's order book.",
            order.Id,
            order.ClientOrderId,
            reason);

        // 504 to the caller, with the order id, so the UI can show "checking with your broker"
        // rather than either "failed" or "placed" — both of which would be a guess.
        return new Error(
            ConnectorErrorCodes.Timeout,
            reason + " The order is being reconciled with your broker; do not resubmit.",
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["orderId"] = order.Id.ToString(),
                ["clientOrderId"] = order.ClientOrderId.ToString(),
            });
    }

    /// <summary>
    /// Persists a refused order before returning the failure.
    ///
    /// A rejected order is still an order that a human tried to place, and an incident review
    /// that cannot see the orders the platform BLOCKED is missing half the story — including
    /// the case where a rule is misconfigured and is blocking everything.
    /// </summary>
    private async Task<Result<PlaceOrderResult>> RefuseAsync(
        PlaceOrderCommand command,
        BrokerLink link,
        PlaceOrderRequest request,
        RiskDecision decision,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var order = Order.Create(
            command.TenantId,
            command.UserId,
            link.Id,
            link.ConnectorId,
            request,
            at,
            command.Actor);

        order.MarkRejected(at, decision.Reason ?? "Blocked by a pre-trade risk rule.", OrderActors.RiskGate);

        await _orders.SaveAsync(order, ct);
        await PublishStateAsync(order, ct);

        await _audit.RecordAsync(
            new AuditRecord
            {
                At = at,
                TenantId = command.TenantId,
                Actor = command.Actor,
                Action = "order.place.blocked",
                Subject = order.Id.ToString(),
                Succeeded = false,
                Detail = decision.Reason,
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["riskRule"] = decision.RuleName ?? "unknown",
                    ["clientOrderId"] = request.ClientOrderId.ToString(),
                    ["brokerLinkId"] = link.Id,
                },
            },
            ct);

        return Result<PlaceOrderResult>.Failure(decision.ToError());
    }

    private static void ApplyBrokerState(Order order, OrderState state, OrderAck ack)
    {
        switch (state)
        {
            case OrderState.Acknowledged:
                order.MarkAcknowledged(ack.AcknowledgedAt, ack.BrokerOrderId, ack.Message);
                break;
            case OrderState.PartiallyFilled:
                // OrderAck carries no fill quantity — it is deliberately thin, because the
                // order book is the truth. Keep what we have and let reconciliation fill it in
                // rather than inventing a zero that would read as "nothing has filled".
                order.MarkPartiallyFilled(ack.AcknowledgedAt, order.FilledQuantity, null, ack.Message);
                break;
            case OrderState.Filled:
                order.MarkFilled(ack.AcknowledgedAt, order.Request.Quantity, null, ack.Message);
                break;
            case OrderState.Cancelled:
                order.MarkCancelled(ack.AcknowledgedAt, ack.Message);
                break;
            case OrderState.Rejected:
                order.MarkRejected(
                    ack.AcknowledgedAt,
                    ack.Message ?? "The broker rejected the order without giving a reason.",
                    OrderActors.Broker,
                    ack.Message);
                break;
            case OrderState.Expired:
                order.MarkExpired(ack.AcknowledgedAt, ack.Message);
                break;
            default:
                // Submitted, PendingSubmit, RiskChecked and Unknown need no further action here.
                break;
        }
    }

    /// <summary>
    /// True when this order reduces existing exposure rather than opening more.
    ///
    /// Deliberately conservative: it only says "reducing" when there is a position on the other
    /// side. Mis-classifying an opening order as a close would let it past the position-count
    /// and daily-loss rules, which is the failure that costs money; mis-classifying a close as
    /// an open merely applies a rule that was going to pass anyway.
    /// </summary>
    private static bool IsReducingExposure(RiskSnapshot snapshot, PlaceOrderRequest request)
    {
        if (!snapshot.NetPositions.TryGetValue(request.Instrument.ToString(), out var net) || net == 0m)
        {
            return false;
        }

        return net > 0m ? request.Side == Side.Sell : request.Side == Side.Buy;
    }

    private static async Task<Money?> TryGetLastPriceAsync(
        IBrokerConnector connector,
        InstrumentKey instrument,
        CancellationToken ct)
    {
        // Best effort. A missing quote is handled by each rule on its own terms — the price
        // band lets it through by default, the value cap does not — so a failure here is data,
        // not an error.
        try
        {
            var quote = await connector.MarketData.GetQuoteAsync(instrument, ct);
            return quote.IsSuccess ? quote.Value.LastPrice : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<InstrumentDefinition?> TryResolveInstrumentAsync(
        IBrokerConnector connector,
        InstrumentKey instrument,
        CancellationToken ct)
    {
        try
        {
            var definition = await connector.Reference.ResolveAsync(instrument, ct);
            return definition.IsSuccess ? definition.Value : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task PublishStateAsync(Order order, CancellationToken ct) =>
        _events.PublishAsync(
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
                order.UpdatedAt),
            ct);

    private ValueTask AuditOutcomeAsync(Order order, string action, bool succeeded, string? detail, CancellationToken ct) =>
        _audit.RecordAsync(
            new AuditRecord
            {
                At = _clock.UtcNow,
                TenantId = order.TenantId,
                Actor = OrderActors.Broker,
                Action = action,
                Subject = order.Id.ToString(),
                Succeeded = succeeded,
                Detail = detail,
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientOrderId"] = order.ClientOrderId.ToString(),
                    ["brokerOrderId"] = order.BrokerOrderId ?? string.Empty,
                    ["state"] = order.State.ToString(),
                },
            },
            ct);

    private static PlaceOrderResult Describe(Order order) => new(
        order.Id,
        order.ClientOrderId,
        order.BrokerOrderId,
        order.State,
        order.Status,
        order.StatusMessage);
}
