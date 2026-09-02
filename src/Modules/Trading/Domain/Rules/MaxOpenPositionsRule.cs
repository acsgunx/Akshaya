using System.Globalization;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Caps how many distinct positions an account may hold open at once.
///
/// PREVENTS: the runaway strategy that diversifies itself into a mess. The failure mode is an
/// automation whose entry condition is looser than its author believed: it does not place one
/// catastrophic order, it places four hundred small ones across four hundred instruments. Each
/// passes every per-order check — the quantity is small, the notional is small, the price is
/// sane — and the account ends the day with concentration risk in nothing and operational risk
/// in everything, unclosable inside one session.
///
/// It applies only to orders that OPEN exposure. Blocking a close because the position count is
/// already at the cap would trap the account in exactly the state the cap exists to prevent —
/// this is the single most important line in the rule.
/// </summary>
public sealed class MaxOpenPositionsRule : IRiskRule
{
    public string Name => RiskRuleNames.MaxOpenPositions;

    public int Order => 70;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Policy.MaxOpenPositions is not { } max)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        // Never block the exit.
        if (context.IsReducingExposure)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        var open = context.Snapshot.OpenPositionCount;
        if (open < max)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        return Task.FromResult(RiskDecision.Deny(
            Name,
            $"This account already holds {open.ToString(CultureInfo.InvariantCulture)} open positions, "
            + $"which is its limit of {max.ToString(CultureInfo.InvariantCulture)}. "
            + "Close a position before opening another.",
            context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["openPositions"] = open.ToString(CultureInfo.InvariantCulture),
                ["limit"] = max.ToString(CultureInfo.InvariantCulture),
            }));
    }
}
