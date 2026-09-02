using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain;

/// <summary>Raised whenever an order's state changes, whatever caused the change.</summary>
/// <param name="OrderId">Our surrogate id.</param>
/// <param name="ClientOrderId">The idempotency key, and the join key to the audit trail.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">Owning user; the realtime hub fans out on this.</param>
/// <param name="BrokerLinkId">Which linked account.</param>
/// <param name="Instrument">Canonical instrument.</param>
/// <param name="State">The state just entered.</param>
/// <param name="Status">Canonical projection of <paramref name="State"/>.</param>
/// <param name="FilledQuantity">Cumulative fill.</param>
/// <param name="AveragePrice">Average fill price, when known.</param>
/// <param name="Message">Broker or platform text, shown verbatim.</param>
/// <param name="At">When.</param>
public sealed record OrderStateChanged(
    Guid OrderId,
    Guid ClientOrderId,
    string TenantId,
    string UserId,
    string BrokerLinkId,
    InstrumentKey Instrument,
    OrderState State,
    OrderStatus Status,
    Quantity FilledQuantity,
    Money? AveragePrice,
    string? Message,
    DateTimeOffset At);

/// <summary>
/// Raised when our copy of an order disagreed with the broker's order book and was corrected.
///
/// THE BROKER WON. That is not a policy choice, it is the only defensible one: the broker's
/// book is what the venue acted on and what settlement will use. Our copy is a cache with
/// opinions.
///
/// Drift is not necessarily a bug — a fill that arrived while we were deploying will show up
/// here — but a sustained stream of these is the earliest signal that something in the order
/// pipeline is losing updates, and it is the event operators should alert on.
/// </summary>
/// <param name="OrderId">Our surrogate id.</param>
/// <param name="ClientOrderId">The idempotency key.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">Owning user.</param>
/// <param name="BrokerLinkId">Which linked account drifted.</param>
/// <param name="LocalState">What we believed.</param>
/// <param name="BrokerState">What the broker said, and what we now hold.</param>
/// <param name="MatchedBy">How the two records were paired — see <see cref="OrderMatchMethod"/>.</param>
/// <param name="Detail">Human-readable summary for the operator.</param>
/// <param name="At">When the correction was applied.</param>
public sealed record OrderDrifted(
    Guid OrderId,
    Guid ClientOrderId,
    string TenantId,
    string UserId,
    string BrokerLinkId,
    OrderState LocalState,
    OrderState BrokerState,
    OrderMatchMethod MatchedBy,
    string Detail,
    DateTimeOffset At);

/// <summary>How reconciliation paired a local order with a broker order.</summary>
public enum OrderMatchMethod
{
    /// <summary>On ClientOrderId. Exact, and the reason we generate one before sending.</summary>
    ClientOrderId,

    /// <summary>On the broker's own id, which we recorded when it acknowledged.</summary>
    BrokerOrderId,

    /// <summary>
    /// On (instrument, side, quantity) inside a timestamp window. A heuristic, used only for
    /// brokers that drop client ids, and always reported as such so nobody mistakes it for
    /// certainty.
    /// </summary>
    Heuristic,

    /// <summary>The broker has an order we have never seen. Placed outside the platform, or by an older instance.</summary>
    BrokerOnly,

    /// <summary>We have an order the broker does not. The dangerous direction — see the reconciliation service.</summary>
    LocalOnly,
}

/// <summary>
/// Raised when a local order could not be found at the broker at all.
///
/// This is the direction that costs money. It means either the order never arrived (and the
/// trader believes they are positioned when they are not) or it arrived and the broker's book
/// has not caught up. Reconciliation does not guess between those two; it marks the order
/// Unknown, raises this, and leaves the decision to a human.
/// </summary>
/// <param name="OrderId">Our surrogate id.</param>
/// <param name="ClientOrderId">The idempotency key to quote to the broker.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">Owning user.</param>
/// <param name="BrokerLinkId">Which linked account.</param>
/// <param name="Age">How long the order has been unaccounted for.</param>
/// <param name="At">When it was noticed.</param>
public sealed record OrderUnaccountedFor(
    Guid OrderId,
    Guid ClientOrderId,
    string TenantId,
    string UserId,
    string BrokerLinkId,
    TimeSpan Age,
    DateTimeOffset At);

/// <summary>Raised when the kill switch is flipped, so the UI and any alerting react immediately.</summary>
/// <param name="TenantId">Whose trading changed.</param>
/// <param name="IsEngaged">True when trading is now halted.</param>
/// <param name="Actor">Who flipped it.</param>
/// <param name="Reason">Why.</param>
/// <param name="At">When.</param>
public sealed record KillSwitchToggled(
    string TenantId,
    bool IsEngaged,
    string Actor,
    string Reason,
    DateTimeOffset At);
