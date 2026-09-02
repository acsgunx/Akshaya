using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Stops an account opening new exposure once it has lost more than its daily limit.
///
/// PREVENTS: revenge trading and the death spiral. The pattern is documented in every
/// retail-broker post-mortem and every prop-desk blow-up: a trader (or an automation with a
/// martingale in it) takes a loss, increases size to win it back, and turns a bad morning into
/// a wiped account by lunchtime. The limit is not there to catch a mistake, it is there to
/// impose a pause that the person losing money is, by then, least able to impose on themselves.
///
/// TWO CAREFUL CHOICES:
///
///  * Losses are summed PER CURRENCY and converted explicitly. An account down 5,000 USD and
///    up 200,000 INR is not "up 195,000 of something"; adding those without a rate is
///    meaningless, so each leg is converted through <see cref="IFxConverter"/> and the rate is
///    reported back in the decision.
///  * Closing trades are always allowed. A loss limit that blocks the exit turns a bad day into
///    an unmanageable position, which is the opposite of risk control.
///
/// FAILS CLOSED on an incomplete snapshot: a loss limit computed from partial data is not a
/// loss limit. This one denial is worth the false positive, because the alternative is a limit
/// that stops working exactly when a broker is having a bad day.
/// </summary>
public sealed class DailyLossLimitRule(IFxConverter fx) : IRiskRule
{
    private readonly IFxConverter _fx = fx ?? throw new ArgumentNullException(nameof(fx));

    public string Name => RiskRuleNames.DailyLossLimit;

    public int Order => 90;

    public async Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Policy.DailyLossLimit is not { } limit)
        {
            return RiskDecision.Allow();
        }

        if (context.IsReducingExposure)
        {
            return RiskDecision.Allow();
        }

        if (context.Snapshot.IsPartial)
        {
            return RiskDecision.Deny(
                Name,
                "Today's realised P&L could not be established in full, so the daily loss limit "
                + "cannot be enforced. New positions are blocked until it can.",
                context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["limit"] = limit.ToString(),
                });
        }

        var target = limit.Currency;
        var total = Money.Zero(target);
        var breakdown = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var leg in context.Snapshot.RealisedPnlToday)
        {
            if (leg.Currency == target)
            {
                total += leg;
                breakdown[$"pnl.{leg.Currency}"] = leg.ToString();
                continue;
            }

            var converted = await _fx.ConvertAsync(leg, target, ct);
            if (converted.IsFailure)
            {
                return RiskDecision.Deny(
                    Name,
                    $"No {leg.Currency}/{target} rate is available, so today's P&L cannot be totalled "
                    + "and the daily loss limit cannot be enforced.",
                    context: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["leg"] = leg.ToString(),
                        ["fxError"] = converted.Error.Code,
                    });
            }

            total += converted.Value;
            breakdown[$"pnl.{leg.Currency}"] = $"{leg} -> {converted.Value}";
        }

        // The limit is stored as a positive magnitude; a loss is a negative P&L. Compare
        // magnitudes rather than signs so a policy saved with either convention behaves.
        var lossMagnitude = new Money(Math.Abs(Math.Min(total.Amount, 0m)), target);
        var threshold = new Money(Math.Abs(limit.Amount), target);

        if (lossMagnitude < threshold)
        {
            return RiskDecision.Allow();
        }

        breakdown["realisedPnl"] = total.ToString();
        breakdown["limit"] = threshold.ToString();

        return RiskDecision.Deny(
            Name,
            $"Today's realised loss of {lossMagnitude} has reached this account's daily limit of "
            + $"{threshold}. Closing orders are still permitted.",
            context: breakdown);
    }
}
