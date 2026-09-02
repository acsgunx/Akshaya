using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.SharedKernel;

namespace Akshaya.Trading.Tests.TestSupport;

/// <summary>
/// Builders for the objects every risk-rule test needs, so each test file spells out only the
/// one field it is actually exercising rather than re-typing a manifest and a policy from
/// scratch a hundred times.
///
/// Defaults are deliberately PERMISSIVE (every order type, every time-in-force, fractional
/// quantities allowed, no numeric limits set) so that a test targeting one rule is not
/// accidentally blocked by another. A test that wants a restriction sets it explicitly.
/// </summary>
internal static class RiskFixtures
{
    public static readonly InstrumentKey Infy = new(Venue.Nse, "INFY", AssetClass.Equity);

    public static readonly InstrumentDefinition InfyDefinition = new()
    {
        Key = Infy,
        Name = "Infosys Limited",
        Currency = Currency.Inr,
        LotSize = 1m,
        TickSize = 0.05m,
    };

    public static readonly InstrumentKey NiftyFuture = new(
        Venue.Nse,
        "NIFTY",
        AssetClass.Future,
        new DateOnly(2026, 9, 24));

    public static readonly InstrumentDefinition NiftyFutureDefinition = new()
    {
        Key = NiftyFuture,
        Name = "NIFTY futures 2026-09",
        Currency = Currency.Inr,
        LotSize = 25m,
        TickSize = 0.05m,
        Multiplier = 25m,
    };

    public static ConnectorManifest Manifest(
        bool fractionalQuantity = true,
        IReadOnlyList<Venue>? venues = null,
        IReadOnlyList<AssetClass>? assetClasses = null,
        IReadOnlyList<OrderType>? orderTypes = null,
        IReadOnlyList<TimeInForce>? timeInForce = null,
        IReadOnlyList<PositionEffect>? positionEffects = null,
        IReadOnlyList<OrderVariety>? varieties = null) => new()
    {
        Id = "fixture-broker",
        DisplayName = "Fixture Broker",
        Vendor = "Fixture Broker Ltd (test-only)",
        ContractVersion = "1.0",
        ConnectorVersion = "1.0.0",
        Jurisdictions = ["IN"],
        Venues = [.. (venues ?? [Venue.Nse]).Select(v => v.Mic)],
        Currencies = ["INR", "USD"],
        AssetClasses = assetClasses ?? [AssetClass.Equity, AssetClass.Future],
        Auth = new AuthSpec
        {
            Model = AuthModel.PasswordOtp,
            CredentialFields = [],
        },
        Orders = new OrderSpec
        {
            Types = orderTypes ?? [OrderType.Market, OrderType.Limit, OrderType.Stop, OrderType.StopLimit],
            TimeInForce = timeInForce ?? [TimeInForce.Day, TimeInForce.Ioc],
            PositionEffects = positionEffects ?? [PositionEffect.Delivery, PositionEffect.Intraday],
            Varieties = varieties ?? [OrderVariety.Regular, OrderVariety.AfterMarket],
            FractionalQuantity = fractionalQuantity,
        },
        MarketData = new MarketDataSpec(),
    };

    public static RiskPolicy Policy(
        string tenantId = "tenant-1",
        Currency? normalisationCurrency = null,
        Money? maxOrderValue = null,
        decimal? maxQuantity = null,
        int? maxOpenPositions = null,
        Money? dailyLossLimit = null,
        decimal? priceBandPercent = null,
        bool allowOrdersWhenVenueClosed = true,
        bool rejectWhenPriceUnavailable = false,
        IReadOnlySet<string>? allowedInstruments = null,
        IReadOnlySet<string>? deniedInstruments = null,
        IReadOnlySet<string>? enabledRules = null) => new()
    {
        TenantId = tenantId,
        NormalisationCurrency = normalisationCurrency ?? Currency.Inr,
        MaxOrderValue = maxOrderValue,
        MaxQuantity = maxQuantity,
        MaxOpenPositions = maxOpenPositions,
        DailyLossLimit = dailyLossLimit,
        PriceBandPercent = priceBandPercent,
        AllowOrdersWhenVenueClosed = allowOrdersWhenVenueClosed,
        RejectWhenPriceUnavailable = rejectWhenPriceUnavailable,
        AllowedInstruments = allowedInstruments ?? new HashSet<string>(StringComparer.Ordinal),
        DeniedInstruments = deniedInstruments ?? new HashSet<string>(StringComparer.Ordinal),
        EnabledRules = enabledRules ?? RiskRuleNames.All,
    };

    public static PlaceOrderRequest Order(
        InstrumentKey? instrument = null,
        Side side = Side.Buy,
        decimal quantity = 1m,
        OrderType orderType = OrderType.Market,
        PositionEffect positionEffect = PositionEffect.Delivery,
        TimeInForce timeInForce = TimeInForce.Day,
        OrderVariety variety = OrderVariety.Regular,
        Money? limitPrice = null,
        Money? triggerPrice = null,
        Guid? clientOrderId = null) => new()
    {
        ClientOrderId = clientOrderId ?? Guid.NewGuid(),
        Instrument = instrument ?? Infy,
        Side = side,
        Quantity = new Quantity(quantity),
        OrderType = orderType,
        PositionEffect = positionEffect,
        TimeInForce = timeInForce,
        Variety = variety,
        LimitPrice = limitPrice,
        TriggerPrice = triggerPrice,
    };

    public static RiskEvaluationContext Context(
        ConnectorManifest? manifest = null,
        RiskPolicy? policy = null,
        PlaceOrderRequest? request = null,
        DateTimeOffset? at = null,
        InstrumentDefinition? instrument = null,
        Money? lastTradedPrice = null,
        RiskSnapshot? snapshot = null,
        bool isReducingExposure = false,
        string tenantId = "tenant-1",
        string brokerLinkId = "link-1",
        string connectorId = "fixture-broker") => new()
    {
        TenantId = tenantId,
        UserId = "user-1",
        BrokerLinkId = brokerLinkId,
        ConnectorId = connectorId,
        Manifest = manifest ?? Manifest(),
        Request = request ?? Order(),
        Policy = policy ?? Policy(tenantId: tenantId),
        // An arbitrary fixed instant. Only VenueMarketHoursRuleTests cares what time this
        // actually is anywhere; every other rule ignores At entirely.
        At = at ?? new DateTimeOffset(2026, 9, 2, 6, 0, 0, TimeSpan.Zero),
        Instrument = instrument ?? InfyDefinition,
        LastTradedPrice = lastTradedPrice,
        Snapshot = snapshot ?? RiskSnapshot.Empty,
        IsReducingExposure = isReducingExposure,
    };
}
