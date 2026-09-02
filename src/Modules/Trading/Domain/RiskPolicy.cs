using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain;

/// <summary>
/// Stable names for the pre-trade rules.
///
/// Constants rather than <c>nameof</c> on the rule classes, because these names are persisted
/// in risk policies, returned in API error payloads and shown in the UI. Renaming a class must
/// not silently disable a tenant's rule.
/// </summary>
public static class RiskRuleNames
{
    public const string KillSwitch = "KillSwitch";
    public const string InstrumentAllowDenyList = "InstrumentAllowDenyList";
    public const string CapabilitySupported = "CapabilitySupported";
    public const string FractionalQuantityAllowed = "FractionalQuantityAllowed";
    public const string MaxQuantity = "MaxQuantity";
    public const string MaxOrderValue = "MaxOrderValue";
    public const string MaxOpenPositions = "MaxOpenPositions";
    public const string DailyLossLimit = "DailyLossLimit";
    public const string VenueMarketHours = "VenueMarketHours";
    public const string PriceBandSanity = "PriceBandSanity";

    /// <summary>Every rule this build ships. Used as the default enabled set.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        KillSwitch,
        InstrumentAllowDenyList,
        CapabilitySupported,
        FractionalQuantityAllowed,
        MaxQuantity,
        MaxOrderValue,
        MaxOpenPositions,
        DailyLossLimit,
        VenueMarketHours,
        PriceBandSanity,
    };
}

/// <summary>
/// Per-tenant pre-trade limits.
///
/// Every limit is nullable and every rule is individually switchable, because risk appetite is
/// not one dial. A prop desk running a market-making strategy legitimately wants the price-band
/// guard off and the daily loss limit tight; a retail tenant wants the exact opposite. A policy
/// that can only be turned on or off as a whole gets turned off as a whole.
///
/// A null limit means "this rule has nothing to enforce" and the rule passes. Removing a rule
/// from <see cref="EnabledRules"/> means "do not run it at all" — a deliberate, auditable act
/// that the risk endpoint records.
/// </summary>
public sealed record RiskPolicy
{
    public required string TenantId { get; init; }

    /// <summary>
    /// The currency every monetary limit in this policy is expressed in. Orders priced in any
    /// other currency are converted to it before comparison, via <see cref="Ports.IFxConverter"/>.
    /// Comparing a USD notional against an INR limit without a rate is an eighty-fold error.
    /// </summary>
    public required Currency NormalisationCurrency { get; init; }

    /// <summary>Which rules run at all. Defaults to every rule this build ships.</summary>
    public IReadOnlySet<string> EnabledRules { get; init; } = RiskRuleNames.All;

    /// <summary>Largest notional a single order may have, in <see cref="NormalisationCurrency"/>.</summary>
    public Money? MaxOrderValue { get; init; }

    /// <summary>Largest quantity a single order may have, in instrument units.</summary>
    public decimal? MaxQuantity { get; init; }

    /// <summary>Cap on distinct open positions. Applies to orders that OPEN exposure, not to closes.</summary>
    public int? MaxOpenPositions { get; init; }

    /// <summary>
    /// Realised loss today, in <see cref="NormalisationCurrency"/>, beyond which no new order
    /// is accepted. Stored as a positive magnitude; the rule compares against realised P&amp;L.
    /// </summary>
    public Money? DailyLossLimit { get; init; }

    /// <summary>
    /// Canonical instrument strings (<see cref="InstrumentKey.ToString"/>) that may be traded.
    /// Empty means "no allow-list configured", which permits everything not denied. A non-empty
    /// allow-list is exclusive: anything absent is refused.
    /// </summary>
    public IReadOnlySet<string> AllowedInstruments { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Canonical instrument strings that may never be traded. Deny always beats allow.</summary>
    public IReadOnlySet<string> DeniedInstruments { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// How far a limit price may sit from the last traded price, in percent. The fat-finger
    /// guard. Null disables it — which is a legitimate choice for an illiquid book where the
    /// LTP is hours old, and a dangerous one everywhere else.
    /// </summary>
    public decimal? PriceBandPercent { get; init; }

    /// <summary>
    /// Whether an order may be accepted while the venue is closed. True is normal — after-market
    /// orders are a real product — but it must be a decision, not an accident.
    /// </summary>
    public bool AllowOrdersWhenVenueClosed { get; init; } = true;

    /// <summary>
    /// What to do when the price-band rule cannot get an LTP. True refuses the order.
    ///
    /// Defaults to FALSE, and that default is a genuine trade-off rather than laziness: an
    /// unavailable quote feed would otherwise block every limit order on the platform,
    /// including the ones a trader is placing precisely because the market is moving. The
    /// other guards still apply.
    /// </summary>
    public bool RejectWhenPriceUnavailable { get; init; }

    public bool IsEnabled(string ruleName) => EnabledRules.Contains(ruleName);

    /// <summary>
    /// A conservative starting policy for a new tenant: every rule on, no numeric limits set.
    /// Deliberately has no default MaxOrderValue — a number invented here would be wrong for
    /// every tenant and would be trusted precisely because it was there.
    /// </summary>
    public static RiskPolicy DefaultFor(string tenantId, Currency normalisationCurrency) => new()
    {
        TenantId = tenantId,
        NormalisationCurrency = normalisationCurrency,
        EnabledRules = RiskRuleNames.All,
        PriceBandPercent = 10m,
    };
}
