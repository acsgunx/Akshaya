using System.Globalization;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Caps the quantity of a single order.
///
/// PREVENTS: the classic fat finger, in its quantity form. In December 2005 a Mizuho trader
/// meant to sell one share of J-Com at 610,000 yen and instead sold 610,000 shares at 1 yen —
/// more shares than existed. The loss was around USD 340m. Every variant of that mistake is a
/// digit typed twice, and a quantity cap catches all of them before the order exists.
///
/// Cheap, local, and checked before anything that needs a price, because a quantity that is
/// obviously wrong should not cost a quote fetch to reject.
/// </summary>
public sealed class MaxQuantityRule : IRiskRule
{
    public string Name => RiskRuleNames.MaxQuantity;

    public int Order => 50;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Policy.MaxQuantity is not { } max)
        {
            // No cap configured is not the same as a cap of zero.
            return Task.FromResult(RiskDecision.Allow());
        }

        var quantity = Math.Abs(context.Request.Quantity.Value);
        if (quantity <= max)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        return Task.FromResult(RiskDecision.Deny(
            Name,
            $"Order quantity {quantity.ToString(CultureInfo.InvariantCulture)} exceeds this account's "
            + $"per-order limit of {max.ToString(CultureInfo.InvariantCulture)}.",
            context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
                ["limit"] = max.ToString(CultureInfo.InvariantCulture),
            }));
    }
}
