using Akshaya.Modules.Trading.Ports;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Refuses every new order for a tenant whose kill switch is engaged.
///
/// PREVENTS: the 2012 Knight Capital incident, in its general shape. A deployment put a
/// mis-configured strategy live and it sent millions of unintended orders in forty-five
/// minutes; the firm lost roughly USD 440m and its independence. What was missing was not
/// cleverness, it was a big red button that stopped everything instantly without needing a
/// deploy, a database migration or a conversation.
///
/// It runs FIRST and does no network I/O, so that flipping the switch takes effect on the very
/// next order rather than on the next order that happens to survive an FX lookup.
/// </summary>
public sealed class KillSwitchRule(IKillSwitch killSwitch) : IRiskRule
{
    private readonly IKillSwitch _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));

    public string Name => RiskRuleNames.KillSwitch;

    public int Order => 10;

    public async Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // IKillSwitch is contracted to fail closed on an unavailable store, so a false here
        // genuinely means "not engaged" rather than "could not tell".
        var engaged = await _killSwitch.IsEngagedAsync(context.TenantId, ct);
        if (!engaged)
        {
            return RiskDecision.Allow();
        }

        var state = await _killSwitch.GetAsync(context.TenantId, ct);

        return RiskDecision.Deny(
            Name,
            state.Reason is { Length: > 0 } reason
                ? $"Trading is halted for this account: {reason}"
                : "Trading is halted for this account by the kill switch.",
            context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engagedBy"] = state.ChangedBy ?? "unknown",
                ["engagedAt"] = state.ChangedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            });
    }
}
