namespace Akshaya.Modules.Trading.Ports;

/// <summary>The kill switch's current position for one tenant, and who put it there.</summary>
public sealed record KillSwitchState
{
    public required string TenantId { get; init; }

    /// <summary>True means no new order may be placed for this tenant, by anyone, for any reason.</summary>
    public required bool IsEngaged { get; init; }

    public string? Reason { get; init; }

    /// <summary>Who last flipped it. Always recorded — a kill switch with no name attached is a mystery during an incident.</summary>
    public string? ChangedBy { get; init; }

    public DateTimeOffset? ChangedAt { get; init; }
}

/// <summary>
/// The per-tenant global trading block.
///
/// It is the last thing anyone reaches for and the first thing they want to work. The risk gate
/// checks it FIRST, before any I/O, so that engaging it takes effect on the very next order
/// rather than after a quote fetch and an FX lookup.
/// </summary>
public interface IKillSwitch
{
    /// <summary>Hot path. Must be cheap and must never throw; an unavailable store fails CLOSED.</summary>
    ValueTask<bool> IsEngagedAsync(string tenantId, CancellationToken ct = default);

    ValueTask<KillSwitchState> GetAsync(string tenantId, CancellationToken ct = default);

    Task EngageAsync(string tenantId, string actor, string reason, CancellationToken ct = default);

    Task DisengageAsync(string tenantId, string actor, string reason, CancellationToken ct = default);
}

/// <summary>
/// Durable storage behind the kill switch.
///
/// Separated from <see cref="IKillSwitch"/> because the SERVICE owns the auditing and the event
/// publication — behaviour that must happen exactly once per flip — while the STORE owns
/// persistence, which in production is a replicated row and in development is a dictionary.
///
/// The store's read must fail CLOSED. If it cannot answer, the switch reads as ENGAGED: a
/// platform that keeps trading because it could not check whether it was supposed to stop is
/// the exact failure the kill switch exists to prevent.
/// </summary>
public interface IKillSwitchStore
{
    ValueTask<KillSwitchState?> GetAsync(string tenantId, CancellationToken ct = default);

    Task SaveAsync(KillSwitchState state, CancellationToken ct = default);
}
