using System.Globalization;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// THE FAT-FINGER GUARD. Refuses a limit or trigger price more than X% away from the last
/// traded price.
///
/// PREVENTS: the decimal point in the wrong place, and the price typed into the quantity box's
/// neighbour. The canonical case is Mizuho's J-Com order in 2005 — 610,000 shares at 1 yen
/// instead of 1 share at 610,000 yen — and the London whale-sized version happens somewhere
/// every year. A buy limit ten times above the market executes instantly against every resting
/// offer; a sell limit at a hundredth of the market does the same on the other side. Both are
/// irreversible within seconds, and both are stopped by comparing one number to the last trade.
///
/// WHY IT ONLY GUARDS PRICED ORDERS: a market order has no price to sanity-check. Its
/// protection is <see cref="MaxOrderValueRule"/>, which values it at the LTP.
///
/// WHY THE MISSING-QUOTE CASE IS CONFIGURABLE: refusing every limit order whenever the quote
/// feed hiccups would block trading precisely when a trader most wants to act, so the default
/// (<see cref="RiskPolicy.RejectWhenPriceUnavailable"/> = false) lets the order through with
/// the other guards still applying. A tenant that prefers the stricter reading flips one flag.
/// </summary>
public sealed class PriceBandSanityRule : IRiskRule
{
    public string Name => RiskRuleNames.PriceBandSanity;

    public int Order => 100;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Policy.PriceBandPercent is not { } bandPercent || bandPercent <= 0m)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        var request = context.Request;
        if (request.LimitPrice is null && request.TriggerPrice is null)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        if (context.LastTradedPrice is not { } ltp || ltp.Amount <= 0m)
        {
            return Task.FromResult(context.Policy.RejectWhenPriceUnavailable
                ? RiskDecision.Deny(
                    Name,
                    "No live price is available for this instrument, so the price-band check could "
                    + "not be performed and the order was not sent.")
                : RiskDecision.Allow());
        }

        foreach (var (label, candidate) in Priced(request.LimitPrice, request.TriggerPrice))
        {
            // Comparing across currencies is meaningless and Money would throw. This normally
            // means a caller built the request with a price in the wrong currency, which is
            // itself an order worth refusing.
            if (candidate.Currency != ltp.Currency)
            {
                return Task.FromResult(RiskDecision.Deny(
                    Name,
                    $"The {label} is in {candidate.Currency} but {request.Instrument.Symbol} trades in "
                    + $"{ltp.Currency}; the price-band check cannot be performed.",
                    context: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["price"] = candidate.ToString(),
                        ["lastTradedPrice"] = ltp.ToString(),
                    }));
            }

            var deviation = Math.Abs(candidate.Amount - ltp.Amount) / ltp.Amount * 100m;
            if (deviation <= bandPercent)
            {
                continue;
            }

            return Task.FromResult(RiskDecision.Deny(
                Name,
                $"The {label} of {candidate} is {deviation.ToString("N2", CultureInfo.InvariantCulture)}% "
                + $"away from the last traded price of {ltp}, beyond this account's "
                + $"{bandPercent.ToString("N2", CultureInfo.InvariantCulture)}% band. "
                + "Check the price before resubmitting.",
                context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["field"] = label,
                    ["price"] = candidate.ToString(),
                    ["lastTradedPrice"] = ltp.ToString(),
                    ["deviationPercent"] = deviation.ToString("N4", CultureInfo.InvariantCulture),
                    ["bandPercent"] = bandPercent.ToString("N4", CultureInfo.InvariantCulture),
                }));
        }

        return Task.FromResult(RiskDecision.Allow());
    }

    private static IEnumerable<(string Label, Money Price)> Priced(Money? limitPrice, Money? triggerPrice)
    {
        if (limitPrice is { } limit)
        {
            yield return ("limit price", limit);
        }

        if (triggerPrice is { } trigger)
        {
            yield return ("trigger price", trigger);
        }
    }
}
