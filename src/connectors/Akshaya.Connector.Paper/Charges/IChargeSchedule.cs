using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper.Charges;

/// <summary>
/// One trade, described in the terms every fee schedule in the world needs. Deliberately not
/// a <see cref="PlaceOrderRequest"/>: charges are levied on what EXECUTED, not on what was
/// asked for, and the engine calls this once per fill.
/// </summary>
public sealed record ChargeContext
{
    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    /// <summary>Execution price per unit. Carries the currency the charges come back in.</summary>
    public required Money Price { get; init; }

    /// <summary>
    /// Delivery versus intraday changes the tax rate in India by a factor of four, and
    /// changes which side of the trade is taxed at all. It is not a cosmetic field.
    /// </summary>
    public required PositionEffect PositionEffect { get; init; }

    /// <summary>Contract multiplier: 1 for cash equity, the contract size for derivatives.</summary>
    public decimal Multiplier { get; init; } = 1m;

    /// <summary>
    /// Executed orders this charge covers. Per-order brokerage is flat in several markets, so
    /// a schedule cannot derive it from turnover alone.
    /// </summary>
    public int OrderCount { get; init; } = 1;

    /// <summary>Traded value: price x quantity x multiplier, in the price's currency.</summary>
    public Money Turnover => new(Price.Amount * Quantity.Value * Multiplier, Price.Currency);

    /// <summary>True for a position effect that settles into the depository rather than netting off intraday.</summary>
    public bool IsDelivery =>
        PositionEffect.HasFlag(PositionEffect.Delivery) || PositionEffect.HasFlag(PositionEffect.CarryForward);

    /// <summary>True for the F&amp;O side of the tax code, which has its own rates everywhere.</summary>
    public bool IsDerivative => Instrument.IsDerivative;
}

/// <summary>
/// A jurisdiction's transaction-cost model.
///
/// Costs are per-market, not per-broker, in everything but the brokerage line: STT, stamp
/// duty, SGX clearing fees and the SEC fee are set by a regulator or an exchange and apply
/// identically whoever routed the order. Modelling them per jurisdiction rather than per
/// connector means the Paper connector charges an NSE trade what NSE charges, and a strategy
/// backtested here is not quietly subsidised.
///
/// EVERY rate in every implementation is a named const with a "REVIEW" note. These change —
/// India revised STT and exchange transaction charges in 2024, Singapore's GST moved to 9%,
/// the SEC fee rate is reset by the Commission at least annually. A rate silently going stale
/// is a backtest that is wrong in a direction nobody notices.
/// </summary>
public interface IChargeSchedule
{
    /// <summary>Human-readable name, shown in the UI beside the itemised breakdown.</summary>
    string Name { get; }

    /// <summary>Currency every line this schedule produces is denominated in.</summary>
    Currency Currency { get; }

    /// <summary>True when this schedule models the venue. The first match wins.</summary>
    bool Handles(Venue venue);

    /// <summary>
    /// Itemised charges for one execution. Fails rather than guessing when the trade is in a
    /// currency the schedule does not levy in — a rupee fee on a US trade is worse than no
    /// estimate at all.
    /// </summary>
    Result<ChargesEstimate> Estimate(ChargeContext context);
}

/// <summary>
/// Accumulates charge lines and totals them in one currency.
///
/// Zero-valued lines are kept, not dropped: a trader looking at an intraday sell wants to see
/// that STT was charged and a buy wants to see that it was not, and a disappearing line reads
/// as a missing calculation rather than a deliberate zero.
/// </summary>
internal sealed class ChargeBuilder(Currency currency)
{
    private readonly List<ChargeLine> _lines = [];
    private decimal _total;

    /// <summary>Rounds to the currency's minor unit and accumulates.</summary>
    public void Add(string name, decimal amount, string? note = null)
    {
        var rounded = Math.Round(amount, 2, MidpointRounding.ToEven);
        _lines.Add(new ChargeLine(name, new Money(rounded, currency), note));
        _total += rounded;
    }

    /// <summary>Sum of the lines added so far, for schedules whose later lines are levied on earlier ones (GST).</summary>
    public decimal RunningTotal => _total;

    /// <summary>Sum of the named lines only. GST bases differ by jurisdiction and are never the whole total.</summary>
    public decimal SumOf(params ReadOnlySpan<string> names)
    {
        var sum = 0m;
        foreach (var line in _lines)
        {
            foreach (var name in names)
            {
                if (string.Equals(line.Name, name, StringComparison.Ordinal))
                {
                    sum += line.Amount.Amount;
                    break;
                }
            }
        }

        return sum;
    }

    public ChargesEstimate Build() => new()
    {
        Lines = _lines,
        Total = new Money(Math.Round(_total, 2, MidpointRounding.ToEven), currency),
    };
}
