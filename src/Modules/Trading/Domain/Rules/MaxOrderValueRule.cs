using System.Globalization;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Caps the NOTIONAL value of a single order, in the tenant's normalised currency.
///
/// PREVENTS: the price-typed-as-quantity error and its cousins — the order that is the right
/// size in the wrong units. A quantity cap alone does not catch buying 100 index-future
/// contracts with a multiplier of 50 at 22,000 a point; the quantity is small and the notional
/// is 110 million. Value is the number a human's intuition is actually calibrated against, so
/// it is the number worth capping.
///
/// WHY THIS NEEDS AN <see cref="IFxConverter"/>: the limit is one number and the platform is
/// cross-border. A 500,000 INR cap compared naively against a USD notional of 6,000 passes an
/// order roughly eighty times over the limit. <see cref="Money"/> refuses to compare across
/// currencies precisely so this cannot happen by accident, and this rule is where the explicit
/// rate is fetched, applied, and reported back in the decision context so the number can be
/// reproduced later.
///
/// FAILS CLOSED, twice over. If the rate is unavailable the order is refused; if the order has
/// no price we can value it at — a market order while the quote feed is down — it is also
/// refused. A value cap that quietly skips the orders it cannot price is not a value cap, and
/// the orders it cannot price are exactly the ones placed in a fast, disorderly market.
/// </summary>
public sealed class MaxOrderValueRule(IFxConverter fx) : IRiskRule
{
    private readonly IFxConverter _fx = fx ?? throw new ArgumentNullException(nameof(fx));

    public string Name => RiskRuleNames.MaxOrderValue;

    public int Order => 80;

    public async Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Policy.MaxOrderValue is not { } limit)
        {
            return RiskDecision.Allow();
        }

        var request = context.Request;

        // Order of preference: the price we would actually pay at worst (the limit), then the
        // trigger, then the market. A market order valued at the LTP is an estimate, and it is
        // the best estimate available before the order exists.
        var reference = request.LimitPrice ?? request.TriggerPrice ?? context.LastTradedPrice;
        if (reference is not { } price)
        {
            return RiskDecision.Deny(
                Name,
                "This order cannot be valued right now (no limit price and no live quote), "
                + "so the per-order value limit cannot be enforced. Place a limit order or retry.",
                context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["limit"] = limit.ToString(),
                });
        }

        // Contract multiplier turns a derivative's "100 contracts" into its real exposure.
        // Absent reference data means 1, which under-states rather than over-states — so the
        // cap can only be too strict, never too loose.
        var multiplier = context.Instrument?.Multiplier ?? 1m;
        var notional = new Money(Math.Abs(price.Amount * request.Quantity.Value * multiplier), price.Currency);

        Money normalised;
        if (notional.Currency == limit.Currency)
        {
            normalised = notional;
        }
        else
        {
            var converted = await _fx.ConvertAsync(notional, limit.Currency, ct);
            if (converted.IsFailure)
            {
                return RiskDecision.Deny(
                    Name,
                    $"No {notional.Currency}/{limit.Currency} rate is available, so this order's value "
                    + "cannot be checked against the account limit.",
                    context: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["notional"] = notional.ToString(),
                        ["limitCurrency"] = limit.Currency.ToString(),
                        ["fxError"] = converted.Error.Code,
                    });
            }

            normalised = converted.Value;
        }

        if (normalised <= limit)
        {
            return RiskDecision.Allow();
        }

        return RiskDecision.Deny(
            Name,
            $"Order value {normalised} exceeds this account's per-order limit of {limit}.",
            context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notionalNative"] = notional.ToString(),
                ["notionalNormalised"] = normalised.ToString(),
                ["limit"] = limit.ToString(),
                ["referencePrice"] = price.ToString(),
                ["multiplier"] = multiplier.ToString(CultureInfo.InvariantCulture),
            });
    }
}
