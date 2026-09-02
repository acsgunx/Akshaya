using System.Collections.Concurrent;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;

namespace Akshaya.Modules.Trading.Infrastructure.InMemory;

/// <summary>
/// DEVELOPMENT ONLY. An order store in a dictionary, so the API runs with no database.
///
/// PHASE 5 replaces this with an EF Core repository against PostgreSQL. Until then, understand
/// exactly what this is not:
///
///  * NOT DURABLE. Everything is lost on restart, which defeats the entire point of persisting
///    an order before sending it. Never run this against a real broker.
///  * NOT TRANSACTIONAL. <see cref="SaveAsync"/> returns before anything is on disk, because
///    there is no disk.
///  * ALIASED. It hands back the same mutable <see cref="Order"/> instance it stores, so a
///    caller's mutations are visible to everyone immediately, with or without a save. The EF
///    implementation will not behave this way, so do not write code that relies on it.
///  * UNBOUNDED. It grows until the process ends.
/// </summary>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _byId = new();
    private readonly ConcurrentDictionary<Guid, Guid> _byClientOrderId = new();
    private readonly ConcurrentDictionary<string, Guid> _byBrokerOrderId = new(StringComparer.Ordinal);

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(orderId, out var order) ? order : null);

    public Task<Order?> GetByClientOrderIdAsync(Guid clientOrderId, CancellationToken ct = default) =>
        Task.FromResult(
            _byClientOrderId.TryGetValue(clientOrderId, out var id) && _byId.TryGetValue(id, out var order)
                ? order
                : null);

    public Task<Order?> GetByBrokerOrderIdAsync(string brokerLinkId, string brokerOrderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);

        // Broker ids are unique only within a link, so the key is the pair — the same mistake
        // in a real schema would let one broker's ids collide with another's.
        return Task.FromResult(
            _byBrokerOrderId.TryGetValue(BrokerKey(brokerLinkId, brokerOrderId), out var id)
            && _byId.TryGetValue(id, out var order)
                ? order
                : null);
    }

    public Task<IReadOnlyList<Order>> ListAsync(OrderFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _byId.Values.Where(o => string.Equals(o.TenantId, filter.TenantId, StringComparison.Ordinal));

        if (filter.UserId is { Length: > 0 } userId)
        {
            query = query.Where(o => string.Equals(o.UserId, userId, StringComparison.Ordinal));
        }

        if (filter.BrokerLinkId is { Length: > 0 } linkId)
        {
            query = query.Where(o => string.Equals(o.BrokerLinkId, linkId, StringComparison.Ordinal));
        }

        if (filter.Instrument is { } instrument)
        {
            query = query.Where(o => o.Instrument == instrument);
        }

        if (filter.OpenOnly)
        {
            query = query.Where(o => o.State.IsWorking());
        }

        if (filter.UnresolvedOnly)
        {
            query = query.Where(o => o.State == OrderState.Unknown);
        }

        if (filter.From is { } from)
        {
            query = query.Where(o => o.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(o => o.CreatedAt <= to);
        }

        IReadOnlyList<Order> result =
        [
            .. query
                .OrderByDescending(o => o.CreatedAt)
                .Take(filter.Limit <= 0 ? 200 : filter.Limit),
        ];

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Order>> ListReconcilableAsync(string brokerLinkId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);

        // Everything that is not settled from our point of view: working orders, orders we have
        // not sent yet, and orders whose fate we do not know. Terminal orders are excluded
        // because a broker cannot change them — except by the conflict path, which reconciliation
        // finds through the broker-only sweep instead.
        IReadOnlyList<Order> result =
        [
            .. _byId.Values.Where(o =>
                string.Equals(o.BrokerLinkId, brokerLinkId, StringComparison.Ordinal)
                && !o.IsTerminal),
        ];

        return Task.FromResult(result);
    }

    public Task SaveAsync(Order order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        _byId[order.Id] = order;
        _byClientOrderId[order.ClientOrderId] = order.Id;

        if (order.BrokerOrderId is { Length: > 0 } brokerOrderId)
        {
            _byBrokerOrderId[BrokerKey(order.BrokerLinkId, brokerOrderId)] = order.Id;
        }

        return Task.CompletedTask;
    }

    // Joined with an ASCII unit separator, which no broker id or link id can contain, so that
    // ("ab", "c") and ("a", "bc") cannot collide into one key.
    private static string BrokerKey(string brokerLinkId, string brokerOrderId) =>
        $"{brokerLinkId}{brokerOrderId}";
}
