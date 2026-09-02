using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain;

/// <summary>
/// The platform's own order lifecycle.
///
/// It is deliberately RICHER than <see cref="OrderStatus"/>, which is the canonical vocabulary
/// connectors speak. Two of these states have no broker equivalent and exist because the
/// platform needs to know things the broker does not:
///
///  * <see cref="RiskChecked"/> — the pre-trade gate passed but nothing has left the building
///    yet. Separating it from <see cref="PendingSubmit"/> is what makes "we rejected it" and
///    "we never got that far" distinguishable after an incident.
///  * <see cref="Acknowledged"/> — the broker confirmed the order is live at the venue, as
///    opposed to merely having accepted our HTTP request. Collapsing the two is how a trader
///    ends up believing an order is working when it was never routed.
/// </summary>
public enum OrderState
{
    /// <summary>Persisted locally with its ClientOrderId; not yet risk checked, not yet sent.</summary>
    PendingSubmit,

    /// <summary>Passed the risk gate. Still ours; nothing has been sent.</summary>
    RiskChecked,

    /// <summary>Handed to the connector. No acknowledgement yet.</summary>
    Submitted,

    /// <summary>The broker confirmed it is live at the venue.</summary>
    Acknowledged,

    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired,

    /// <summary>
    /// We do not know. Reached on a send timeout and never on a clean broker answer.
    /// Exists because pretending to know is how phantom and duplicate orders are created;
    /// reconciliation resolves it against the broker's order book.
    /// </summary>
    Unknown,
}

public static class OrderStateExtensions
{
    public static bool IsTerminal(this OrderState state) => state is
        OrderState.Filled or OrderState.Cancelled or OrderState.Rejected or OrderState.Expired;

    /// <summary>True once the order can actually execute at the venue.</summary>
    public static bool IsWorking(this OrderState state) => state is
        OrderState.Submitted or OrderState.Acknowledged or OrderState.PartiallyFilled;

    /// <summary>
    /// Projection onto the canonical vocabulary that connectors and the API speak.
    /// <see cref="OrderState.RiskChecked"/> maps back to PendingSubmit because, as far as the
    /// outside world is concerned, an order that has not been sent has not been submitted.
    /// </summary>
    public static OrderStatus ToCanonicalStatus(this OrderState state) => state switch
    {
        OrderState.PendingSubmit => OrderStatus.PendingSubmit,
        OrderState.RiskChecked => OrderStatus.PendingSubmit,
        OrderState.Submitted => OrderStatus.Submitted,
        OrderState.Acknowledged => OrderStatus.Open,
        OrderState.PartiallyFilled => OrderStatus.PartiallyFilled,
        OrderState.Filled => OrderStatus.Filled,
        OrderState.Cancelled => OrderStatus.Cancelled,
        OrderState.Rejected => OrderStatus.Rejected,
        OrderState.Expired => OrderStatus.Expired,
        OrderState.Unknown => OrderStatus.Unknown,
        _ => OrderStatus.Unknown,
    };

    /// <summary>
    /// Lifts a broker-reported canonical status into our lifecycle. Used only by
    /// reconciliation and by stream-driven updates — the broker never tells us
    /// <see cref="OrderState.RiskChecked"/>, because that is a fact about us, not about it.
    /// </summary>
    public static OrderState ToOrderState(this OrderStatus status) => status switch
    {
        OrderStatus.PendingSubmit => OrderState.PendingSubmit,
        OrderStatus.Submitted => OrderState.Submitted,
        OrderStatus.Open => OrderState.Acknowledged,
        OrderStatus.PartiallyFilled => OrderState.PartiallyFilled,
        OrderStatus.Filled => OrderState.Filled,
        OrderStatus.Cancelled => OrderState.Cancelled,
        OrderStatus.Rejected => OrderState.Rejected,
        OrderStatus.Expired => OrderState.Expired,
        _ => OrderState.Unknown,
    };
}

/// <summary>
/// The legal order lifecycle, as data.
///
/// WHY A TABLE AND NOT A SWITCH: the whole legal graph has to be readable in one place, by a
/// reviewer, in under a minute. A state machine spread across nine methods is a state machine
/// nobody audits, and an unaudited order lifecycle is where "cancelled orders that later
/// filled" come from.
///
/// TWO MODES, because there are two kinds of transition:
///
///  * <see cref="Allowed"/> — what OUR code may do. Violating it is programmer error and
///    throws <see cref="InvalidOperationException"/>. It is not a broker outcome; a broker
///    saying something surprising is data, a caller calling MarkFilled on a cancelled order
///    is a bug.
///  * <see cref="ReconciliationAllowed"/> — what the BROKER may tell us. Wider, because the
///    broker is the source of truth and our local state can be wrong in ways our own code
///    would never produce: a stream event we mis-sequenced, a fill we recorded from a partial
///    payload, a cancel that raced a fill. Reconciliation may therefore correct a terminal
///    state, and that correction is exactly what the OrderDrifted event reports.
/// </summary>
public static class OrderStateMachine
{
    /// <summary>
    /// The happy path, plus every legitimate early exit.
    ///
    ///   PendingSubmit → RiskChecked → Submitted → Acknowledged → (PartiallyFilled) →
    ///       Filled | Cancelled | Rejected | Expired
    ///
    /// Notes on the less obvious rows:
    ///  * PendingSubmit → Rejected: the risk gate refused. The order still exists and is still
    ///    auditable; it just never left.
    ///  * Submitted → Filled directly: fast markets and market orders acknowledge and fill in
    ///    one payload. Forcing an Acknowledged hop we never observed would be a lie.
    ///  * PartiallyFilled → PartiallyFilled: every subsequent partial fill. Self-transitions
    ///    are legal here and nowhere else.
    ///  * anything non-terminal → Unknown: a send or a poll timed out. Always legal, because
    ///    admitting ignorance must never be blocked by a state machine.
    ///  * Unknown → anything: reconciliation resolved it.
    /// </summary>
    public static readonly IReadOnlyDictionary<OrderState, IReadOnlySet<OrderState>> Allowed =
        new Dictionary<OrderState, IReadOnlySet<OrderState>>
        {
            [OrderState.PendingSubmit] = Set(
                OrderState.RiskChecked,
                OrderState.Rejected,
                OrderState.Cancelled,
                OrderState.Unknown),

            [OrderState.RiskChecked] = Set(
                OrderState.Submitted,
                OrderState.Rejected,
                OrderState.Cancelled,
                OrderState.Unknown),

            [OrderState.Submitted] = Set(
                OrderState.Acknowledged,
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.Cancelled,
                OrderState.Rejected,
                OrderState.Expired,
                OrderState.Unknown),

            [OrderState.Acknowledged] = Set(
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.Cancelled,
                OrderState.Rejected,
                OrderState.Expired,
                OrderState.Unknown),

            [OrderState.PartiallyFilled] = Set(
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.Cancelled,
                OrderState.Expired,
                OrderState.Unknown),

            [OrderState.Unknown] = Set(
                OrderState.Submitted,
                OrderState.Acknowledged,
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.Cancelled,
                OrderState.Rejected,
                OrderState.Expired),

            // Terminal states have no outgoing edges under normal operation. Deliberately
            // present as empty sets rather than absent, so a missing key is a bug in this
            // table rather than an accidentally-permissive default.
            [OrderState.Filled] = Set(),
            [OrderState.Cancelled] = Set(),
            [OrderState.Rejected] = Set(),
            [OrderState.Expired] = Set(),
        };

    public static bool CanTransition(OrderState from, OrderState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// True when reconciliation may move <paramref name="from"/> to <paramref name="to"/>.
    ///
    /// Almost everything is permitted, because the broker's order book beats our copy every
    /// single time and our copy can be wrong in ways our own code would never produce — a
    /// stream event mis-sequenced, a fill recorded from a partial payload, a cancel that raced
    /// a fill. A terminal state we reached in error is still an error.
    ///
    /// The ONE refusal is resurrecting a FILLED order into a working state. A fill is a real
    /// event at a real venue with a real counterparty; it does not un-happen. If a broker
    /// reports one of our filled orders as working, the mismatch is an identity problem — we
    /// matched the wrong two records — and quietly adopting it would corrupt the position.
    /// That case must surface as drift for a human, not as a state change.
    /// </summary>
    public static bool CanReconcile(OrderState from, OrderState to) =>
        !(from == OrderState.Filled && to.IsWorking());

    /// <summary>
    /// Validates a transition our own code is about to make.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transition is not in the table. This is programmer error and is thrown rather than
    /// returned as a <see cref="Result"/>: a caller that cancels a filled order has a bug that
    /// must surface loudly in test, not a broker outcome to be handled gracefully.
    /// </exception>
    public static OrderState Transition(OrderState from, OrderState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Illegal order transition {from} -> {to}. Legal targets from {from}: "
                + $"{Describe(from)}.");
        }

        return to;
    }

    /// <summary>Validates a broker-driven correction. Same throw semantics, far wider rule.</summary>
    /// <exception cref="InvalidOperationException">
    /// The correction would resurrect a filled order. The caller must report drift instead of
    /// applying it — see <see cref="CanReconcile"/>.
    /// </exception>
    public static OrderState Reconcile(OrderState from, OrderState to)
    {
        if (!CanReconcile(from, to))
        {
            throw new InvalidOperationException(
                $"Illegal reconciliation {from} -> {to}. A filled order cannot return to a working state; "
                + "this almost always means two records were matched that are not the same order.");
        }

        return to;
    }

    /// <summary>Human-readable legal targets, for error messages and for the API's docs.</summary>
    public static string Describe(OrderState from) =>
        Allowed.TryGetValue(from, out var targets) && targets.Count > 0
            ? string.Join(", ", targets.Select(t => t.ToString()).Order(StringComparer.Ordinal))
            : "(none — terminal)";

    private static IReadOnlySet<OrderState> Set(params OrderState[] states) => new HashSet<OrderState>(states);
}
