using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain;

/// <summary>
/// Stable actor names for <see cref="OrderEvent.Actor"/>.
///
/// Strings rather than an enum because the set grows (a new automation, a new operator role)
/// and because audit rows outlive the enum that produced them.
/// </summary>
public static class OrderActors
{
    /// <summary>A human, acting through the API.</summary>
    public const string User = "user";

    /// <summary>The pre-trade risk gate.</summary>
    public const string RiskGate = "risk-gate";

    /// <summary>A broker answer, arriving on a request response.</summary>
    public const string Broker = "broker";

    /// <summary>A broker answer, arriving on the live stream.</summary>
    public const string BrokerStream = "broker-stream";

    /// <summary>The reconciliation loop, correcting us against the broker's order book.</summary>
    public const string Reconciliation = "reconciliation";

    /// <summary>The platform itself, with no external cause.</summary>
    public const string System = "system";
}

/// <summary>
/// One immutable step in an order's life.
///
/// Every transition appends one of these and none is ever mutated or removed. This list IS
/// the answer to "what happened to my order", and it is the artefact a regulator, a support
/// engineer and an angry trader all end up reading. It therefore carries the broker's RAW
/// payload verbatim: a normalised status tells you what we concluded, and the raw payload
/// tells you what we concluded it from.
/// </summary>
public sealed record OrderEvent
{
    public required DateTimeOffset At { get; init; }

    /// <summary>One of <see cref="OrderActors"/>.</summary>
    public required string Actor { get; init; }

    /// <summary>The lifecycle state the order entered (or re-affirmed) at this step.</summary>
    public required OrderState State { get; init; }

    /// <summary>The canonical status the outside world sees for <see cref="State"/>.</summary>
    public required OrderStatus Status { get; init; }

    /// <summary>
    /// Exactly what the broker sent, unparsed. Null when the step had no broker involvement
    /// (a risk decision, a local timeout). Never summarised, never pretty-printed: a summary
    /// is a second implementation of the parser and it will disagree with the first.
    /// </summary>
    public string? RawBrokerPayload { get; init; }

    /// <summary>Our own explanation of the step, when one helps.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The platform's own order aggregate.
///
/// It is NOT a copy of the broker's order. It is the record of what WE asked for, what we
/// checked before asking, when we asked, and everything we have since been told. The broker's
/// order book remains the source of truth about execution — see
/// <see cref="Application.ReconciliationService"/> — but this aggregate is the source of truth
/// about intent, and about the fact that we intended it before any network call happened.
///
/// State changes go through <see cref="OrderStateMachine"/> and nothing else. There is no
/// public setter for <see cref="State"/>; if a caller needs a transition that is not offered
/// here, the answer is a new method with a state-machine call in it, not a back door.
/// </summary>
public sealed class Order
{
    private readonly List<OrderEvent> _events;

    private Order(
        Guid id,
        string tenantId,
        string userId,
        string brokerLinkId,
        string connectorId,
        PlaceOrderRequest request,
        DateTimeOffset createdAt,
        OrderState state,
        List<OrderEvent> events)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        BrokerLinkId = brokerLinkId;
        ConnectorId = connectorId;
        Request = request;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        State = state;
        FilledQuantity = Quantity.Zero;
        _events = events;
    }

    /// <summary>Our surrogate id. Distinct from <see cref="ClientOrderId"/> so the wire identity can be rotated.</summary>
    public Guid Id { get; }

    /// <summary>
    /// The idempotency key we generate and persist BEFORE any network call, and send to the
    /// broker where the broker supports it. On a timeout this is what we match on when we
    /// re-read the order book — which is the difference between a recovered order and a
    /// duplicate one.
    /// </summary>
    public Guid ClientOrderId => Request.ClientOrderId;

    public string TenantId { get; }

    public string UserId { get; }

    /// <summary>Which linked broker account this order belongs to.</summary>
    public string BrokerLinkId { get; }

    /// <summary>Which connector serves that link. An opaque id — never switched on.</summary>
    public string ConnectorId { get; }

    public InstrumentKey Instrument => Request.Instrument;

    /// <summary>The full request as submitted, kept verbatim so the order can be replayed and audited.</summary>
    public PlaceOrderRequest Request { get; }

    public OrderState State { get; private set; }

    /// <summary>Canonical status for the API and the UI.</summary>
    public OrderStatus Status => State.ToCanonicalStatus();

    /// <summary>Null until the broker gives us one. Its absence is exactly why reconciliation matches on ClientOrderId first.</summary>
    public string? BrokerOrderId { get; private set; }

    public Quantity FilledQuantity { get; private set; }

    public Quantity PendingQuantity => new(Request.Quantity.Value - FilledQuantity.Value);

    public Money? AveragePrice { get; private set; }

    /// <summary>The broker's own rejection or status text. Shown to the trader verbatim.</summary>
    public string? StatusMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Append-only. See <see cref="OrderEvent"/>.</summary>
    public IReadOnlyList<OrderEvent> Events => _events;

    public bool IsTerminal => State.IsTerminal();

    /// <summary>
    /// Creates an order in <see cref="OrderState.PendingSubmit"/>.
    ///
    /// It is created — and persisted by the caller — before the risk gate runs and long before
    /// anything is sent. A crash between here and the broker call leaves a PendingSubmit row
    /// that reconciliation can resolve; a crash with no row at all leaves an order that may or
    /// may not exist at the broker and that nothing will ever look for.
    /// </summary>
    public static Order Create(
        string tenantId,
        string userId,
        string brokerLinkId,
        string connectorId,
        PlaceOrderRequest request,
        DateTimeOffset at,
        string actor = OrderActors.User)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentNullException.ThrowIfNull(request);

        var order = new Order(
            Guid.CreateVersion7(),
            tenantId,
            userId,
            brokerLinkId,
            connectorId,
            request,
            at,
            OrderState.PendingSubmit,
            []);

        order._events.Add(new OrderEvent
        {
            At = at,
            Actor = actor,
            State = OrderState.PendingSubmit,
            Status = OrderStatus.PendingSubmit,
            Note = "Order accepted and persisted locally; nothing sent yet.",
        });

        return order;
    }

    /// <summary>
    /// Rebuilds an aggregate from storage WITHOUT re-running the state machine.
    ///
    /// Only a repository may call this. It exists because replaying transitions on load would
    /// make a historical row un-loadable the day the legal graph changes — and the graph will
    /// change, while the history must not.
    /// </summary>
    public static Order Rehydrate(
        Guid id,
        string tenantId,
        string userId,
        string brokerLinkId,
        string connectorId,
        PlaceOrderRequest request,
        OrderState state,
        string? brokerOrderId,
        Quantity filledQuantity,
        Money? averagePrice,
        string? statusMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IEnumerable<OrderEvent> events)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);

        return new Order(id, tenantId, userId, brokerLinkId, connectorId, request, createdAt, state, [.. events])
        {
            BrokerOrderId = brokerOrderId,
            FilledQuantity = filledQuantity,
            AveragePrice = averagePrice,
            StatusMessage = statusMessage,
            UpdatedAt = updatedAt,
        };
    }

    // ─────────────────────────── transitions ───────────────────────────
    // Each one names the state machine explicitly. Read OrderStateMachine.Allowed alongside
    // this section; the table is the specification and these are its only callers.

    /// <summary>The pre-trade gate passed. Still local; nothing has been sent.</summary>
    public void MarkRiskChecked(DateTimeOffset at, string? note = null) =>
        Apply(OrderStateMachine.Transition(State, OrderState.RiskChecked), at, OrderActors.RiskGate, null, note);

    /// <summary>Handed to the connector. Called AFTER the network call returned an acknowledgement.</summary>
    public void MarkSubmitted(DateTimeOffset at, string? brokerOrderId, string? rawBrokerPayload)
    {
        BrokerOrderId ??= brokerOrderId;
        Apply(OrderStateMachine.Transition(State, OrderState.Submitted), at, OrderActors.Broker, rawBrokerPayload, null);
    }

    /// <summary>The broker confirmed the order is live at the venue.</summary>
    public void MarkAcknowledged(DateTimeOffset at, string? brokerOrderId, string? rawBrokerPayload, string actor = OrderActors.Broker)
    {
        BrokerOrderId ??= brokerOrderId;
        Apply(OrderStateMachine.Transition(State, OrderState.Acknowledged), at, actor, rawBrokerPayload, null);
    }

    /// <summary>A partial fill. Legal repeatedly — every subsequent partial lands here.</summary>
    public void MarkPartiallyFilled(
        DateTimeOffset at,
        Quantity filledQuantity,
        Money? averagePrice,
        string? rawBrokerPayload,
        string actor = OrderActors.Broker)
    {
        FilledQuantity = filledQuantity;
        AveragePrice = averagePrice ?? AveragePrice;
        Apply(OrderStateMachine.Transition(State, OrderState.PartiallyFilled), at, actor, rawBrokerPayload, null);
    }

    public void MarkFilled(
        DateTimeOffset at,
        Quantity filledQuantity,
        Money? averagePrice,
        string? rawBrokerPayload,
        string actor = OrderActors.Broker)
    {
        FilledQuantity = filledQuantity;
        AveragePrice = averagePrice ?? AveragePrice;
        Apply(OrderStateMachine.Transition(State, OrderState.Filled), at, actor, rawBrokerPayload, null);
    }

    public void MarkCancelled(DateTimeOffset at, string? rawBrokerPayload, string actor = OrderActors.Broker, string? note = null) =>
        Apply(OrderStateMachine.Transition(State, OrderState.Cancelled), at, actor, rawBrokerPayload, note);

    /// <summary>
    /// Rejected — by the risk gate before sending, or by the broker or venue after.
    /// <paramref name="reason"/> is shown to the trader verbatim; never paraphrase a rejection.
    /// </summary>
    public void MarkRejected(DateTimeOffset at, string reason, string actor, string? rawBrokerPayload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        StatusMessage = reason;
        Apply(OrderStateMachine.Transition(State, OrderState.Rejected), at, actor, rawBrokerPayload, reason);
    }

    public void MarkExpired(DateTimeOffset at, string? rawBrokerPayload, string actor = OrderActors.Broker) =>
        Apply(OrderStateMachine.Transition(State, OrderState.Expired), at, actor, rawBrokerPayload, "Order expired at the venue.");

    /// <summary>
    /// We do not know what happened. The ONLY correct response to a send timeout.
    ///
    /// Retrying instead would risk a duplicate order; assuming failure would risk an
    /// unmonitored live order. Both have cost real money at real firms. Admitting ignorance
    /// and handing the order to reconciliation costs a few seconds of uncertainty.
    /// </summary>
    public void MarkUnknown(DateTimeOffset at, string reason, string actor = OrderActors.System)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        StatusMessage = reason;
        Apply(OrderStateMachine.Transition(State, OrderState.Unknown), at, actor, null, reason);
    }

    /// <summary>
    /// Records something that happened to the order WITHOUT changing its state — an accepted
    /// amendment, a corroborating poll, a rejected cancel.
    ///
    /// It exists because a modify is not a lifecycle transition: an acknowledged order that has
    /// its price changed is still acknowledged. Inventing a state for it would double the size
    /// of the legal graph to express something the event log already says perfectly well.
    /// </summary>
    public void RecordAmendment(DateTimeOffset at, string detail, string actor, string? rawBrokerPayload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        UpdatedAt = at;
        _events.Add(new OrderEvent
        {
            At = at,
            Actor = actor,
            State = State,
            Status = Status,
            RawBrokerPayload = rawBrokerPayload,
            Note = detail,
        });
    }

    /// <summary>
    /// Adopts the broker's version of this order. THE BROKER WINS, always.
    ///
    /// Returns true when something actually changed, so the caller can decide whether to raise
    /// an <see cref="OrderDrifted"/> event. Re-affirming the current state appends a
    /// corroborating event but reports no change: a reconciliation pass that agrees with us
    /// should be visible in the audit trail and invisible in the alerting.
    /// </summary>
    public bool ReconcileWith(BrokerOrder brokerOrder, DateTimeOffset at, string? rawBrokerPayload)
    {
        ArgumentNullException.ThrowIfNull(brokerOrder);

        var target = brokerOrder.Status.ToOrderState();
        var changed = target != State
                      || FilledQuantity != brokerOrder.FilledQuantity
                      || (BrokerOrderId is null && brokerOrder.BrokerOrderId is not null);

        BrokerOrderId ??= brokerOrder.BrokerOrderId;
        FilledQuantity = brokerOrder.FilledQuantity;
        AveragePrice = brokerOrder.AveragePrice ?? AveragePrice;
        StatusMessage = brokerOrder.StatusMessage ?? StatusMessage;

        // Reconcile, not Transition: the broker may legitimately tell us something our own
        // code would never be allowed to assert. See OrderStateMachine's class remarks.
        Apply(
            OrderStateMachine.Reconcile(State, target),
            at,
            OrderActors.Reconciliation,
            rawBrokerPayload,
            changed ? $"Corrected from {State} to {target} against the broker's order book." : "Broker agrees.");

        return changed;
    }

    private void Apply(OrderState next, DateTimeOffset at, string actor, string? rawBrokerPayload, string? note)
    {
        State = next;
        UpdatedAt = at;
        _events.Add(new OrderEvent
        {
            At = at,
            Actor = actor,
            State = next,
            Status = next.ToCanonicalStatus(),
            RawBrokerPayload = rawBrokerPayload,
            Note = note,
        });
    }
}
