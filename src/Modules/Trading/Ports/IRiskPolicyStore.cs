using Akshaya.Modules.Trading.Domain;

namespace Akshaya.Modules.Trading.Ports;

/// <summary>
/// Storage for per-tenant <see cref="RiskPolicy"/>.
///
/// <see cref="GetAsync"/> is on the order path and must never return null: a tenant with no
/// saved policy gets the conservative default rather than an unguarded one. "No policy
/// configured" must never mean "no limits enforced".
/// </summary>
public interface IRiskPolicyStore
{
    Task<RiskPolicy> GetAsync(string tenantId, CancellationToken ct = default);

    Task SaveAsync(RiskPolicy policy, string actor, CancellationToken ct = default);
}
