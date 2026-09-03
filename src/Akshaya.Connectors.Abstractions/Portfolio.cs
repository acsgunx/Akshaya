using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

/// <summary>
/// An open position at one broker. P&amp;L is computed in the instrument's own currency here;
/// blending across brokers and converting to a display currency happens in the Portfolio
/// module, which is the only place that knows about FX rates.
/// </summary>
public sealed record BrokerPosition
{
    public required InstrumentKey Instrument { get; init; }

    /// <summary>Signed: negative is short. Decimal because fractional positions exist.</summary>
    public required Quantity NetQuantity { get; init; }

    public required PositionEffect PositionEffect { get; init; }

    public required Money AveragePrice { get; init; }

    public Money? LastPrice { get; init; }

    public Money? UnrealisedPnl { get; init; }

    public Money? RealisedPnl { get; init; }

    public Quantity BuyQuantity { get; init; } = Quantity.Zero;

    public Quantity SellQuantity { get; init; } = Quantity.Zero;

    public bool IsShort => NetQuantity.Value < 0;

    public bool IsFlat => NetQuantity.Value == 0;
}

/// <summary>A settled long-term holding, as distinct from an intraday position.</summary>
public sealed record BrokerHolding
{
    public required InstrumentKey Instrument { get; init; }

    public required Quantity Quantity { get; init; }

    public required Money AveragePrice { get; init; }

    public Money? LastPrice { get; init; }

    public Money? UnrealisedPnl { get; init; }

    /// <summary>Quantity sold today but not yet settled — cannot be sold again.</summary>
    public Quantity PledgedQuantity { get; init; } = Quantity.Zero;

    public string? Isin { get; init; }

    public Money? CurrentValue => LastPrice is { } price
        ? new Money(price.Amount * Quantity.Value, price.Currency)
        : null;
}

/// <summary>
/// A balance in ONE currency. Returned as a list, never a single number: a Moomoo or IBKR
/// account routinely holds SGD, USD and HKD at once, and collapsing them into one figure at
/// the connector layer destroys information the UI needs.
/// </summary>
public sealed record BrokerBalance
{
    public required Currency Currency { get; init; }

    /// <summary>Cash actually available to place new orders with.</summary>
    public required Money AvailableToTrade { get; init; }

    public Money? CashBalance { get; init; }

    public Money? UsedMargin { get; init; }

    public Money? AvailableMargin { get; init; }

    public Money? Collateral { get; init; }

    public Money? RealisedPnl { get; init; }

    public Money? UnrealisedPnl { get; init; }
}

/// <summary>
/// Moves an open position from one margin product to another — the intraday-to-delivery
/// rescue, most often, when a trader decides not to be squared off at the session's end.
///
/// This is NOT an order. Nothing trades, the position does not change size, and no fill is
/// generated; only the settlement basis under it changes, which changes how much margin the
/// broker blocks. Modelling it as an order would put a phantom fill in the blotter and double
/// the position in every P&amp;L that reads from trades.
/// </summary>
public sealed record ConvertPositionRequest
{
    public required InstrumentKey Instrument { get; init; }

    /// <summary>
    /// The side of the position being converted — BUY for a long, SELL for a short. Brokers
    /// key the conversion on it because a hedged account can hold both at once.
    /// </summary>
    public required Side Side { get; init; }

    /// <summary>
    /// How much to convert. Partial conversion is allowed and common: a trader may take
    /// delivery of part of an intraday position and let the rest square off.
    /// </summary>
    public required Quantity Quantity { get; init; }

    /// <summary>The product the position is held under now.</summary>
    public required PositionEffect From { get; init; }

    /// <summary>The product to move it to.</summary>
    public required PositionEffect To { get; init; }
}

public interface IConnectorPortfolio
{
    Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default);

    Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default);

    Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default);

    /// <summary>
    /// Converts an open position between margin products. Connectors that cannot do this
    /// return <c>NotSupported</c> and declare <c>positionConversion: false</c> in their
    /// manifest, so the UI hides the action rather than offering a button that always fails.
    /// </summary>
    Task<Result> ConvertPositionAsync(ConvertPositionRequest request, CancellationToken ct = default);
}
