using System.Collections.Concurrent;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Infrastructure.InMemory;

/// <summary>
/// DEVELOPMENT ONLY. Kill-switch state in a dictionary.
///
/// PHASE 5 replaces this with a replicated row that every API instance reads. Until then note
/// the sharp edge: state is PER PROCESS, so engaging the switch on one instance does not stop
/// trading on another. Never rely on it in a multi-instance deployment.
/// </summary>
public sealed class InMemoryKillSwitchStore : IKillSwitchStore
{
    private readonly ConcurrentDictionary<string, KillSwitchState> _states = new(StringComparer.Ordinal);

    public ValueTask<KillSwitchState?> GetAsync(string tenantId, CancellationToken ct = default) =>
        ValueTask.FromResult(_states.TryGetValue(tenantId, out var state) ? state : null);

    public Task SaveAsync(KillSwitchState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        _states[state.TenantId] = state;
        return Task.CompletedTask;
    }
}

/// <summary>
/// DEVELOPMENT ONLY. Risk policies in a dictionary.
///
/// PHASE 5 replaces this with a versioned, audited table — a risk policy is a document whose
/// HISTORY matters, because "what were the limits when this order was accepted" is the first
/// question after any incident, and this store cannot answer it.
///
/// It never returns null: an unknown tenant gets <see cref="RiskPolicy.DefaultFor"/>, because
/// "no policy configured" must never quietly mean "no limits enforced".
/// </summary>
public sealed class InMemoryRiskPolicyStore(Currency defaultNormalisationCurrency) : IRiskPolicyStore
{
    private readonly ConcurrentDictionary<string, RiskPolicy> _policies = new(StringComparer.Ordinal);

    public Task<RiskPolicy> GetAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return Task.FromResult(_policies.GetOrAdd(
            tenantId,
            id => RiskPolicy.DefaultFor(id, defaultNormalisationCurrency)));
    }

    public Task SaveAsync(RiskPolicy policy, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _policies[policy.TenantId] = policy;
        return Task.CompletedTask;
    }
}

/// <summary>
/// DEVELOPMENT ONLY. Broker links, sessions and all, in a dictionary.
///
/// PHASE 5 moves this into the BrokerLink module with envelope encryption, per-tenant keys and
/// rotation. Until then the sharpest edge in this file: SESSIONS ARE HELD IN PLAIN MEMORY.
/// Access tokens sit unencrypted in the process heap and appear in a memory dump. Acceptable
/// against a sandbox, never against a live brokerage account.
/// </summary>
public sealed class InMemoryBrokerLinkStore : IBrokerLinkStore
{
    private readonly ConcurrentDictionary<string, BrokerLink> _links = new(StringComparer.Ordinal);

    public Task<BrokerLink?> GetAsync(string linkId, CancellationToken ct = default) =>
        Task.FromResult(_links.TryGetValue(linkId, out var link) ? link : null);

    public Task<IReadOnlyList<BrokerLink>> ListAsync(string tenantId, string? userId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        IReadOnlyList<BrokerLink> result =
        [
            .. _links.Values
                .Where(l => string.Equals(l.TenantId, tenantId, StringComparison.Ordinal)
                            && (userId is null || string.Equals(l.UserId, userId, StringComparison.Ordinal)))
                .OrderBy(l => l.CreatedAt),
        ];

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<BrokerLink>> ListActiveAsync(CancellationToken ct = default)
    {
        IReadOnlyList<BrokerLink> result = [.. _links.Values.Where(l => l.IsActive)];
        return Task.FromResult(result);
    }

    public Task SaveAsync(BrokerLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        _links[link.Id] = link;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string linkId, CancellationToken ct = default)
    {
        _links.TryRemove(linkId, out _);
        return Task.CompletedTask;
    }
}
