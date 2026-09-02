using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Refuses a fractional quantity when the connector's manifest declares
/// <c>fractionalQuantity: false</c>, and refuses a quantity that is not a whole multiple of the
/// instrument's lot size when reference data gives us one.
///
/// PREVENTS: the truncation surprise. <see cref="Quantity"/> is decimal precisely because
/// fractional shares are real, but most venues and most brokers outside the US are whole-unit
/// only. Sent 0.5 lots, such a broker does one of three things — rejects it, rounds it to 0
/// (nothing happens and the trader does not notice for hours), or rounds it to 1 (twice the
/// intended exposure). Rounding at OUR layer would be the worst of the three, because we would
/// be inventing a position size the trader never asked for. So we refuse and say why.
///
/// The lot-size half of this rule is what stops a derivatives order for 150 units on a
/// contract that trades in lots of 50 being partially accepted as 100.
/// </summary>
public sealed class FractionalQuantityRule : IRiskRule
{
    public string Name => RiskRuleNames.FractionalQuantityAllowed;

    public int Order => 40;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var allowsFractions = context.Manifest.Orders.FractionalQuantity;

        if (!allowsFractions && request.Quantity.IsFractional)
        {
            return Deny(
                $"This broker only accepts whole units; {request.Quantity} is fractional.",
                request.Quantity.Value);
        }

        if (!allowsFractions && request.DisclosedQuantity is { IsFractional: true } disclosed)
        {
            return Deny(
                $"This broker only accepts whole units; the disclosed quantity {disclosed} is fractional.",
                disclosed.Value);
        }

        // Lot size is reference data and is frequently absent for instruments we have not
        // ingested yet. Absent means "we do not know", and we do not invent a constraint we
        // cannot substantiate — the broker will enforce its own and we surface its answer.
        if (context.Instrument is { LotSize: > 0m } definition && definition.LotSize != 1m)
        {
            var lots = request.Quantity.Value / definition.LotSize;
            if (lots != Math.Truncate(lots))
            {
                return Deny(
                    $"{request.Instrument.Symbol} trades in lots of "
                    + $"{definition.LotSize.ToString(CultureInfo.InvariantCulture)}; "
                    + $"{request.Quantity} is not a whole number of lots.",
                    request.Quantity.Value);
            }
        }

        return Task.FromResult(RiskDecision.Allow());
    }

    private Task<RiskDecision> Deny(string reason, decimal quantity) =>
        Task.FromResult(RiskDecision.Deny(
            Name,
            reason,
            ConnectorErrorCodes.InvalidRequest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            }));
}
