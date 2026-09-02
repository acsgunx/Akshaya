using Akshaya.Modules.Trading.Domain;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Ports;

/// <summary>Query shape for listing orders. All fields optional; unset means "do not filter on this".</summary>
public sealed record OrderFilter
{
    public required string TenantId { get; init; }

    public string? UserId { get; init; }

    public string? BrokerLinkId { get; init; }

    public InstrumentKey? Instrument { get; init; }

    /// <summary>Only orders that can still execute. What an order blotter shows by default.</summary>
    public bool OpenOnly { get; init; }

    /// <summary>
    /// Only orders whose state the platform cannot vouch for. This is the reconciliation
    /// work-list and the first thing to look at after an outage.
    /// </summary>
    public bool UnresolvedOnly { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int Limit { get; init; } = 200;
}

/// <summary>
/// Durable storage for the order aggregate.
///
/// The critical guarantee, on which the whole timeout story depends: <see cref="SaveAsync"/>
/// must have COMMITTED before <see cref="Application.PlaceOrderHandler"/> makes its network
/// call. An order that exists at the broker but not in our store is invisible to
/// reconciliation, and invisible is the one thing an order must never be.
///
/// <see cref="GetByClientOrderIdAsync"/> is the lookup that makes timeout recovery possible and
/// must be backed by a unique index — two orders sharing a ClientOrderId would make the
/// idempotency key meaningless.
/// </summary>
public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid orderId, CancellationToken ct = default);

    Task<Order?> GetByClientOrderIdAsync(Guid clientOrderId, CancellationToken ct = default);

    /// <summary>Broker ids are unique only within a broker link, never globally.</summary>
    Task<Order?> GetByBrokerOrderIdAsync(string brokerLinkId, string brokerOrderId, CancellationToken ct = default);

    Task<IReadOnlyList<Order>> ListAsync(OrderFilter filter, CancellationToken ct = default);

    /// <summary>Every order on a link that reconciliation still has to account for.</summary>
    Task<IReadOnlyList<Order>> ListReconcilableAsync(string brokerLinkId, CancellationToken ct = default);

    /// <summary>Insert or update. Must be atomic and must have committed before it returns.</summary>
    Task SaveAsync(Order order, CancellationToken ct = default);
}
