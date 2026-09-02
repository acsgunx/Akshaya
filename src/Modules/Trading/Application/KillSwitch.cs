using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// The per-tenant global trading block.
///
/// Checked by <see cref="Domain.Rules.KillSwitchRule"/> as the FIRST pre-trade rule, flipped by
/// the risk endpoint, and audited on every flip.
///
/// Three properties matter more than anything else about this class:
///
///  1. IT FAILS CLOSED. If the store cannot be read, the switch reports ENGAGED. Continuing to
///     trade because we could not check whether we were supposed to stop is precisely the
///     failure this exists to prevent, and "the risk database was down" is not a defence
///     anyone has ever accepted after the fact.
///  2. EVERY FLIP IS ATTRIBUTED. Who, when, why. During an incident the first question is
///     always "who stopped it and do they know something I do not" — and the second, hours
///     later, is "who turned it back on".
///  3. RE-ENGAGING IS FREE. Engaging an already-engaged switch is not an error; in a panic
///     nobody should be reading an error message about idempotency.
/// </summary>
public sealed class KillSwitch(
    IKillSwitchStore store,
    IEventBus events,
    IAuditSink audit,
    IClock clock,
    ILogger<KillSwitch> logger) : IKillSwitch
{
    public async ValueTask<bool> IsEngagedAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        try
        {
            var state = await store.GetAsync(tenantId, ct);

            // A tenant that has never touched the switch has no row. That is NOT the failure
            // case — it is the normal case — and it means trading is permitted.
            return state?.IsEngaged ?? false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Kill-switch store is unreadable for tenant {TenantId}; failing CLOSED and halting trading.",
                tenantId);

            return true;
        }
    }

    public async ValueTask<KillSwitchState> GetAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var state = await store.GetAsync(tenantId, ct);
        return state ?? new KillSwitchState { TenantId = tenantId, IsEngaged = false };
    }

    public Task EngageAsync(string tenantId, string actor, string reason, CancellationToken ct = default) =>
        SetAsync(tenantId, engaged: true, actor, reason, ct);

    public Task DisengageAsync(string tenantId, string actor, string reason, CancellationToken ct = default) =>
        SetAsync(tenantId, engaged: false, actor, reason, ct);

    private async Task SetAsync(string tenantId, bool engaged, string actor, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var at = clock.UtcNow;
        var state = new KillSwitchState
        {
            TenantId = tenantId,
            IsEngaged = engaged,
            Reason = reason,
            ChangedBy = actor,
            ChangedAt = at,
        };

        // Persist FIRST. An engage that is announced but not stored would be undone by the next
        // process restart, silently, at the worst possible moment.
        await store.SaveAsync(state, ct);

        logger.LogWarning(
            "KILL SWITCH {Action} for tenant {TenantId} by {Actor}: {Reason}",
            engaged ? "ENGAGED" : "RELEASED",
            tenantId,
            actor,
            reason);

        await audit.RecordAsync(
            new AuditRecord
            {
                At = at,
                TenantId = tenantId,
                Actor = actor,
                Action = engaged ? "killswitch.engage" : "killswitch.disengage",
                Subject = tenantId,
                Detail = reason,
            },
            ct);

        await events.PublishAsync(new KillSwitchToggled(tenantId, engaged, actor, reason, at), ct);
    }
}
