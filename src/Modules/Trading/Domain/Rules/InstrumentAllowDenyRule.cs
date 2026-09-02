namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Enforces the tenant's instrument allow-list and deny-list.
///
/// PREVENTS: trading a name the account is not permitted to touch. The recurring real-world
/// cases are an employee dealing in a restricted-list security their employer is advising on,
/// and a fund breaching its own mandate by buying an instrument class it told investors it
/// would not hold. Both are discovered days later by compliance, both are unwindable only at
/// a loss, and both are stopped by one set lookup before the order leaves.
///
/// DENY ALWAYS BEATS ALLOW. If an instrument is on both lists it is refused: a restriction
/// added in a hurry during an incident must not be defeated by a stale permission added
/// months earlier.
/// </summary>
public sealed class InstrumentAllowDenyRule : IRiskRule
{
    public string Name => RiskRuleNames.InstrumentAllowDenyList;

    public int Order => 20;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Policy;
        var key = context.Request.Instrument.ToString();

        if (policy.DeniedInstruments.Contains(key))
        {
            return Task.FromResult(RiskDecision.Deny(
                Name,
                $"{key} is on this account's restricted list and cannot be traded.",
                context: Detail(key, "denied")));
        }

        // An empty allow-list means "no allow-list configured", not "nothing is allowed".
        // The other reading would silently freeze every existing tenant the moment the feature
        // shipped.
        if (policy.AllowedInstruments.Count > 0 && !policy.AllowedInstruments.Contains(key))
        {
            return Task.FromResult(RiskDecision.Deny(
                Name,
                $"{key} is not on this account's permitted instrument list.",
                context: Detail(key, "not-allow-listed")));
        }

        return Task.FromResult(RiskDecision.Allow());
    }

    private static Dictionary<string, string> Detail(string key, string outcome) =>
        new(StringComparer.Ordinal)
        {
            ["instrument"] = key,
            ["outcome"] = outcome,
        };
}
