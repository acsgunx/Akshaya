using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Portfolio.Models;

/// <summary>How two positions at different brokers were proved to be the same position.</summary>
public enum PositionGrouping
{
    /// <summary>Same ISIN. The strongest evidence available and the reason the identity resolver exists.</summary>
    Isin,

    /// <summary>Same FIGI.</summary>
    Figi,

    /// <summary>
    /// Same canonical instrument key. Correct, but it cannot merge a cross-listing — the same
    /// company on two venues stays two rows. The conservative fallback.
    /// </summary>
    InstrumentKey,
}

/// <summary>
/// The outcome of fetching from ONE broker link.
///
/// This is the type that keeps the dashboard alive when a broker is down. It is exposed on the
/// snapshot rather than swallowed, because the difference between "you hold nothing at this
/// broker" and "we could not reach this broker" is the difference between a calm user and a
/// panicking one — and hiding it is how a platform teaches people not to trust its numbers.
/// </summary>
public sealed record PortfolioSourceStatus
{
    public required string BrokerLinkId { get; init; }

    /// <summary>Opaque connector id. Shown as a label; never compared to a literal.</summary>
    public required string ConnectorId { get; init; }

    public required string DisplayName { get; init; }

    public bool PositionsOk { get; init; }

    public bool HoldingsOk { get; init; }

    public bool BalancesOk { get; init; }

    /// <summary>The first failure, verbatim, so the UI can show the user what their broker said.</summary>
    public Error? Error { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsComplete => PositionsOk && HoldingsOk && BalancesOk;
}

/// <summary>What one broker contributes to a blended position.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="ConnectorId">Opaque connector id.</param>
/// <param name="DisplayName">User-facing account label.</param>
/// <param name="NetQuantity">Signed; negative is short.</param>
/// <param name="AveragePrice">This broker's average entry price, in its own currency.</param>
/// <param name="LastPrice">This broker's last price, when it reported one.</param>
/// <param name="MarketValue">Quantity times last price, in the native currency.</param>
/// <param name="UnrealisedPnl">Native, as the broker computed it.</param>
/// <param name="RealisedPnl">Native, as the broker computed it.</param>
/// <param name="PositionEffect">Intraday, delivery, margin — the leg's product type.</param>
public sealed record BrokerPositionLeg(
    string BrokerLinkId,
    string ConnectorId,
    string DisplayName,
    Quantity NetQuantity,
    Money AveragePrice,
    Money? LastPrice,
    Money? MarketValue,
    Money? UnrealisedPnl,
    Money? RealisedPnl,
    PositionEffect PositionEffect);

/// <summary>
/// One economic position, assembled from every broker that holds part of it.
///
/// ONE CURRENCY PER ROW, ALWAYS. A blended position is keyed by (identity, currency), not by
/// identity alone. If the same ISIN is held in USD at one broker and SGD at another, that is two
/// rows, each internally consistent, and the display total converts them explicitly. Merging
/// them into a single row would require an FX rate to even state the quantity's value, and
/// burying that rate inside an aggregation is exactly the invisible assumption this module
/// exists to eliminate.
/// </summary>
public sealed record BlendedPosition
{
    /// <summary>ISIN, FIGI or canonical key, depending on <see cref="GroupedBy"/>.</summary>
    public required string GroupKey { get; init; }

    public required PositionGrouping GroupedBy { get; init; }

    /// <summary>A representative canonical key — the first leg's. Legs may differ by venue.</summary>
    public required InstrumentKey Instrument { get; init; }

    public string? Isin { get; init; }

    public string? Figi { get; init; }

    /// <summary>The native currency of every leg in this row. There is exactly one.</summary>
    public required Currency Currency { get; init; }

    /// <summary>Sum of the legs' signed quantities.</summary>
    public required Quantity NetQuantity { get; init; }

    /// <summary>Quantity-weighted average entry price across the legs, in <see cref="Currency"/>.</summary>
    public required Money AveragePrice { get; init; }

    /// <summary>Last price, taken from the legs that reported one.</summary>
    public Money? LastPrice { get; init; }

    public Money? MarketValue { get; init; }

    public Money? UnrealisedPnl { get; init; }

    public Money? RealisedPnl { get; init; }

    /// <summary>Per-broker breakdown. The UI shows this when the user expands a row.</summary>
    public required IReadOnlyList<BrokerPositionLeg> Legs { get; init; }

    public bool IsShort => NetQuantity.Value < 0m;

    /// <summary>True when more than one broker holds part of this position — the whole point of blending.</summary>
    public bool IsSplitAcrossBrokers => Legs.Count > 1;
}

/// <summary>What one broker contributes to a blended holding.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="ConnectorId">Opaque connector id.</param>
/// <param name="DisplayName">User-facing account label.</param>
/// <param name="Quantity">Settled quantity held.</param>
/// <param name="AveragePrice">Native average cost.</param>
/// <param name="LastPrice">Native last price, when reported.</param>
/// <param name="CurrentValue">Native market value.</param>
/// <param name="UnrealisedPnl">Native unrealised P&amp;L.</param>
/// <param name="PledgedQuantity">Quantity that cannot currently be sold.</param>
public sealed record BrokerHoldingLeg(
    string BrokerLinkId,
    string ConnectorId,
    string DisplayName,
    Quantity Quantity,
    Money AveragePrice,
    Money? LastPrice,
    Money? CurrentValue,
    Money? UnrealisedPnl,
    Quantity PledgedQuantity);

/// <summary>A settled holding blended across brokers. Same one-currency-per-row rule as <see cref="BlendedPosition"/>.</summary>
public sealed record BlendedHolding
{
    public required string GroupKey { get; init; }

    public required PositionGrouping GroupedBy { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public string? Isin { get; init; }

    public string? Figi { get; init; }

    public required Currency Currency { get; init; }

    public required Quantity Quantity { get; init; }

    public required Money AveragePrice { get; init; }

    public Money? LastPrice { get; init; }

    public Money? CurrentValue { get; init; }

    public Money? UnrealisedPnl { get; init; }

    public Quantity PledgedQuantity { get; init; } = Quantity.Zero;

    public required IReadOnlyList<BrokerHoldingLeg> Legs { get; init; }
}

/// <summary>What one broker contributes to a currency's balance.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="ConnectorId">Opaque connector id.</param>
/// <param name="DisplayName">User-facing account label.</param>
/// <param name="AvailableToTrade">Cash usable for new orders.</param>
/// <param name="CashBalance">Total cash, when reported.</param>
/// <param name="UsedMargin">Margin consumed, when reported.</param>
/// <param name="AvailableMargin">Margin headroom, when reported.</param>
public sealed record BrokerBalanceLeg(
    string BrokerLinkId,
    string ConnectorId,
    string DisplayName,
    Money AvailableToTrade,
    Money? CashBalance,
    Money? UsedMargin,
    Money? AvailableMargin);

/// <summary>
/// Every broker's balance in ONE currency, added together.
///
/// A list of these — never a single number — because a Singapore or US account routinely holds
/// SGD, USD and HKD at once. Collapsing them at this layer destroys the only information a
/// trader needs to answer "can I place this order", which is denominated in one specific
/// currency and cannot be paid for out of another without a conversion the broker has to make.
/// </summary>
public sealed record CurrencyBalance
{
    public required Currency Currency { get; init; }

    public required Money AvailableToTrade { get; init; }

    public Money? CashBalance { get; init; }

    public Money? UsedMargin { get; init; }

    public Money? AvailableMargin { get; init; }

    public Money? Collateral { get; init; }

    public Money? RealisedPnl { get; init; }

    public Money? UnrealisedPnl { get; init; }

    public required IReadOnlyList<BrokerBalanceLeg> Legs { get; init; }
}

/// <summary>A rate that was actually applied, recorded so any converted figure can be reproduced.</summary>
/// <param name="From">Source currency.</param>
/// <param name="To">Display currency.</param>
/// <param name="Rate">The multiplier used.</param>
/// <param name="AsOf">When the rate was observed.</param>
public sealed record AppliedFxRate(Currency From, Currency To, decimal Rate, DateTimeOffset AsOf);

/// <summary>
/// P&amp;L, twice: natively per currency, and converted for display.
///
/// THE NATIVE FIGURES ARE THE TRUTH AND ARE ALWAYS PRESENT. The converted total is a
/// convenience that depends on rates we may not have, so it is nullable and it always ships
/// with the rates that produced it. A user comparing our USD total to their broker's own screen
/// must be able to see which rate we used and when we took it; without that the difference
/// between two plausible numbers is unexplainable and the user concludes, reasonably, that one
/// of them is broken.
/// </summary>
public sealed record PnlSummary
{
    public required Currency DisplayCurrency { get; init; }

    /// <summary>Unrealised P&amp;L per currency, exactly as the brokers reported it.</summary>
    public required IReadOnlyList<Money> UnrealisedNative { get; init; }

    /// <summary>Realised P&amp;L per currency, exactly as the brokers reported it.</summary>
    public required IReadOnlyList<Money> RealisedNative { get; init; }

    /// <summary>Null when at least one leg could not be converted. Never a partial total presented as whole.</summary>
    public Money? UnrealisedConverted { get; init; }

    public Money? RealisedConverted { get; init; }

    /// <summary>Every rate applied, with its timestamp.</summary>
    public IReadOnlyList<AppliedFxRate> RatesUsed { get; init; } = [];

    /// <summary>Why a converted figure is missing or incomplete. Shown to the user, not swallowed.</summary>
    public IReadOnlyList<string> ConversionWarnings { get; init; } = [];

    public bool IsFullyConverted => ConversionWarnings.Count == 0;
}

/// <summary>
/// The whole portfolio at one instant, blended across every linked broker.
///
/// <see cref="IsPartial"/> is the single most important field for the UI: it is how the
/// dashboard knows to show "1 of 3 accounts unavailable" instead of a total that quietly
/// excludes a third of the user's money.
/// </summary>
public sealed record PortfolioSnapshot
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required Currency DisplayCurrency { get; init; }

    public required IReadOnlyList<BlendedPosition> Positions { get; init; }

    public required IReadOnlyList<BlendedHolding> Holdings { get; init; }

    public required IReadOnlyList<CurrencyBalance> Balances { get; init; }

    public required PnlSummary Pnl { get; init; }

    /// <summary>One entry per broker link, successful or not. Never filtered.</summary>
    public required IReadOnlyList<PortfolioSourceStatus> Sources { get; init; }

    /// <summary>True when any source failed. The dashboard must say so prominently.</summary>
    public bool IsPartial => Sources.Any(s => !s.IsComplete);

    /// <summary>Links that could not be fully read. For the "retry these" affordance.</summary>
    public IReadOnlyList<PortfolioSourceStatus> FailedSources => [.. Sources.Where(s => !s.IsComplete)];
}
