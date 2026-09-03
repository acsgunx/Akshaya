using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and mStock's wire vocabulary.
///
/// It lives in one file, on purpose. The single most common way a broker integration rots is
/// that "CNC" gets spelled out in eight different files and one of them is missed when the
/// vendor renames it. Every translation in this connector goes through here; nothing else in
/// the assembly is allowed to contain a bare product / order-type / exchange literal.
///
/// Every method returns <see cref="Result{T}"/> and every unmapped value is a failure. There
/// is deliberately no <c>_ =&gt; "CNC"</c> fallback anywhere below: a silently defaulted
/// product code is an order placed with the wrong settlement, and a silently defaulted status
/// is a phantom order. Failing loudly costs a rejected ticket; guessing costs money.
/// </summary>
public static class MStockMaps
{
    // --- mStock wire literals. The only place these strings appear. -----------------------

    public const string ProductCnc = "CNC";
    public const string ProductMis = "MIS";
    public const string ProductMtf = "MTF";
    public const string ProductNrml = "NRML";

    public const string OrderTypeMarket = "MARKET";
    public const string OrderTypeLimit = "LIMIT";
    public const string OrderTypeStopLoss = "SL";
    public const string OrderTypeStopLossMarket = "SL-M";

    public const string ValidityDay = "DAY";
    public const string ValidityIoc = "IOC";

    public const string VarietyRegular = "reg";
    public const string VarietyAfterMarket = "amo";

    public const string TransactionBuy = "BUY";
    public const string TransactionSell = "SELL";

    public const string ExchangeNse = "NSE";
    public const string ExchangeBse = "BSE";
    public const string ExchangeNfo = "NFO";
    public const string ExchangeBfo = "BFO";

    /// <summary>Segment discriminator required by <c>GET /openapi/typea/order/details</c>.</summary>
    public const string SegmentEquity = "E";

    /// <summary>Segment discriminator required by <c>GET /openapi/typea/order/details</c>.</summary>
    public const string SegmentDerivative = "D";

    // --- PositionEffect <-> product ------------------------------------------------------

    /// <summary>
    /// Canonical position effect to mStock's product code.
    ///
    /// <see cref="PositionEffect"/> is a flags enum because the concept fragments by market,
    /// but India's products are mutually exclusive, so only one product bit may be set. The
    /// one combination we do accept is <c>ShortSell</c> alongside an intraday or carry-forward
    /// product: on Indian equities a short is expressed by <see cref="Side.Sell"/> with an MIS
    /// product, and on F&amp;O by NRML, so the extra bit is redundant rather than contradictory.
    /// <c>Delivery | ShortSell</c> is rejected — you cannot short a delivery position in India,
    /// and quietly turning that into a CNC sell would liquidate a holding the trader still has.
    /// </summary>
    public static Result<string> ToNativeProduct(PositionEffect effect)
    {
        var product = effect & ~PositionEffect.ShortSell;
        var isShort = effect.HasFlag(PositionEffect.ShortSell);

        return product switch
        {
            PositionEffect.Delivery when !isShort => ProductCnc,
            PositionEffect.Intraday => ProductMis,
            PositionEffect.Margin when !isShort => ProductMtf,
            PositionEffect.CarryForward => ProductNrml,
            _ => Result<string>.Failure(Unsupported(
                "product",
                effect.ToString(),
                "mStock supports exactly one of CNC (delivery), MIS (intraday), MTF (margin) or NRML "
                + "(carry-forward), and does not allow short selling in the delivery or MTF products.")),
        };
    }

    /// <summary>
    /// mStock's product code to canonical position effect.
    ///
    /// MSTOCK SENDS BACK A DIFFERENT VOCABULARY FROM THE ONE IT ACCEPTS. An order placed with
    /// <c>product=MIS</c> comes back from the order book as <c>"product": "INTRADAY"</c>, and
    /// the same order read through <c>/order/details</c> comes back as <c>"CNC"</c>. Only the
    /// four codes in the glossary are valid on the way IN — see
    /// <see cref="ToNativeProduct"/>, which must never emit any of the aliases below — but all
    /// of them turn up on the way out, and rejecting them made every row of the order book and
    /// the positions list unmappable.
    ///
    /// An EMPTY product is not an error either. The positions route documents
    /// <c>"product": ""</c> and holdings documents <c>"product": null</c>; the broker simply
    /// does not say. Falling back to <see cref="PositionEffect.Delivery"/> is the conservative
    /// choice rather than the neutral one: guessing "intraday" would tell the risk engine and
    /// the UI that this exposure disappears at the intraday square-off, and a trader who
    /// believes a position will close itself and is wrong is in a far worse place than one who
    /// believes it will persist and is wrong.
    /// </summary>
    public static Result<PositionEffect> ToCanonicalPositionEffect(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return PositionEffect.Delivery;
        }

        return Normalise(product) switch
        {
            ProductCnc or "DELIVERY" => PositionEffect.Delivery,
            ProductMis or "INTRADAY" => PositionEffect.Intraday,
            ProductMtf => PositionEffect.Margin,
            ProductNrml or "CARRYFORWARD" or "CARRY FORWARD" => PositionEffect.CarryForward,
            _ => Result<PositionEffect>.Failure(Unrecognised("product", product)),
        };
    }

    // --- OrderType <-> order_type --------------------------------------------------------

    /// <summary>
    /// Canonical order type to mStock's <c>order_type</c>.
    ///
    /// Note the deliberate crossover that catches everyone once: mStock's <c>SL</c> is a
    /// stop-LIMIT (it carries both a trigger and a price) and <c>SL-M</c> is a stop-MARKET.
    /// The canonical names read the other way round, so <see cref="OrderType.Stop"/> maps to
    /// <c>SL-M</c> and <see cref="OrderType.StopLimit"/> maps to <c>SL</c>.
    /// </summary>
    public static Result<string> ToNativeOrderType(OrderType type) => type switch
    {
        OrderType.Market => OrderTypeMarket,
        OrderType.Limit => OrderTypeLimit,
        OrderType.Stop => OrderTypeStopLossMarket,
        OrderType.StopLimit => OrderTypeStopLoss,
        OrderType.MarketIfTouched or OrderType.TrailingStop => Result<string>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "mStock exposes only MARKET, LIMIT, SL and SL-M. Market-if-touched and trailing stops "
            + "must be synthesised above the connector if they are wanted.")),
        _ => Result<string>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>mStock's <c>order_type</c> to canonical order type.</summary>
    public static Result<OrderType> ToCanonicalOrderType(string orderType) =>
        Normalise(orderType) switch
        {
            OrderTypeMarket => OrderType.Market,
            OrderTypeLimit => OrderType.Limit,
            OrderTypeStopLossMarket => OrderType.Stop,
            OrderTypeStopLoss => OrderType.StopLimit,
            _ => Result<OrderType>.Failure(Unrecognised("order type", orderType)),
        };

    // --- TimeInForce <-> validity --------------------------------------------------------

    public static Result<string> ToNativeValidity(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => ValidityDay,
        TimeInForce.Ioc => ValidityIoc,
        TimeInForce.Gtc or TimeInForce.Gtd => Result<string>.Failure(Unsupported(
            "time in force",
            tif.ToString(),
            "Indian exchanges do not accept good-till-cancelled orders. mStock's Type A API has no "
            + "GTT surface either, so a resting multi-day order must be managed by the platform.")),
        TimeInForce.Fok or TimeInForce.AtTheOpen or TimeInForce.AtTheClose =>
            Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(string validity) =>
        Normalise(validity) switch
        {
            ValidityDay => TimeInForce.Day,
            ValidityIoc => TimeInForce.Ioc,
            // mStock reports an immediate-or-cancel order back as "IOC" but some builds spell
            // the day validity "NORMAL". Both are accepted; nothing else is.
            "NORMAL" => TimeInForce.Day,
            _ => Result<TimeInForce>.Failure(Unrecognised("validity", validity)),
        };

    // --- OrderVariety <-> variety --------------------------------------------------------

    public static Result<string> ToNativeVariety(OrderVariety variety) => variety switch
    {
        OrderVariety.Regular => VarietyRegular,
        OrderVariety.AfterMarket => VarietyAfterMarket,
        OrderVariety.Cover or OrderVariety.Bracket or OrderVariety.Iceberg
            or OrderVariety.GoodTillTriggered => Result<string>.Failure(Unsupported(
                "order variety",
                variety.ToString(),
                "mStock's Type A order route accepts only 'reg' and 'amo'.")),
        _ => Result<string>.Failure(Unsupported("order variety", variety.ToString(), null)),
    };

    public static Result<OrderVariety> ToCanonicalVariety(string variety) =>
        Normalise(variety) switch
        {
            "REG" or "REGULAR" => OrderVariety.Regular,
            "AMO" => OrderVariety.AfterMarket,
            _ => Result<OrderVariety>.Failure(Unrecognised("order variety", variety)),
        };

    // --- Side <-> transaction_type -------------------------------------------------------

    public static Result<string> ToNativeSide(Side side) => side switch
    {
        Side.Buy => TransactionBuy,
        Side.Sell => TransactionSell,
        _ => Result<string>.Failure(Unsupported("side", side.ToString(), null)),
    };

    public static Result<Side> ToCanonicalSide(string transactionType) =>
        Normalise(transactionType) switch
        {
            TransactionBuy or "B" => Side.Buy,
            TransactionSell or "S" => Side.Sell,
            _ => Result<Side>.Failure(Unrecognised("transaction type", transactionType)),
        };

    // --- Venue <-> exchange --------------------------------------------------------------

    /// <summary>
    /// Canonical venue plus asset class to mStock's exchange segment.
    ///
    /// The asset class is load-bearing: NSE cash is <c>NSE</c> but NSE derivatives are
    /// <c>NFO</c>, and sending a futures order to <c>NSE</c> is rejected at the exchange.
    /// The MIC does not carry that distinction, which is why this takes both arguments.
    /// </summary>
    public static Result<string> ToNativeExchange(Venue venue, AssetClass assetClass)
    {
        var isDerivative = assetClass is AssetClass.Future or AssetClass.Option;

        return venue.Mic switch
        {
            "XNSE" => isDerivative ? ExchangeNfo : ExchangeNse,
            "XBOM" => isDerivative ? ExchangeBfo : ExchangeBse,
            _ => Result<string>.Failure(Unsupported(
                "venue",
                venue.Mic,
                "mStock reaches only NSE (XNSE) and BSE (XBOM), cash and derivative segments.")),
        };
    }

    /// <summary>mStock's exchange segment to the canonical venue MIC.</summary>
    public static Result<Venue> ToCanonicalVenue(string exchange) =>
        Normalise(exchange) switch
        {
            ExchangeNse or ExchangeNfo or "NCO" or "NSE_EQ" or "NSE_FO" => Venue.Nse,
            ExchangeBse or ExchangeBfo or "BCO" or "BSE_EQ" or "BSE_FO" => Venue.Bse,
            _ => Result<Venue>.Failure(Unrecognised("exchange", exchange)),
        };

    /// <summary>True when the mStock exchange segment is a derivatives segment.</summary>
    public static bool IsDerivativeSegment(string exchange) =>
        Normalise(exchange) is ExchangeNfo or ExchangeBfo or "NSE_FO" or "BSE_FO";

    /// <summary>
    /// The <c>segment</c> query parameter required by the order-details route. mStock splits
    /// its order store by segment and will not find a derivative order under <c>E</c>.
    /// </summary>
    public static Result<string> ToNativeSegment(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Equity or AssetClass.Etf or AssetClass.Index => SegmentEquity,
        AssetClass.Future or AssetClass.Option => SegmentDerivative,
        _ => Result<string>.Failure(Unsupported(
            "segment",
            assetClass.ToString(),
            "mStock's order store is split into equity (E) and derivative (D) segments only.")),
    };

    // --- OrderStatus ---------------------------------------------------------------------

    /// <summary>
    /// mStock's status vocabulary, collapsed onto the canonical lifecycle.
    ///
    /// The intermediate states matter more than they look. "PUT ORDER REQ RECEIVED" and
    /// "VALIDATION PENDING" mean the order is somewhere between us and the exchange and has
    /// NOT been acknowledged — treating those as Open is how a trader ends up believing they
    /// are in the market when they are not. They map to <see cref="OrderStatus.Submitted"/>.
    /// "TRIGGER PENDING" is the opposite case: an SL/SL-M order that is genuinely resting at
    /// the exchange waiting for its trigger, so it is Open.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(string status) =>
        Normalise(status) switch
        {
            "COMPLETE" or "COMPLETED" or "FILLED" or "EXECUTED" => OrderStatus.Filled,
            "REJECTED" or "AMO REJECTED" => OrderStatus.Rejected,
            "CANCELLED" or "CANCELED" or "CANCELLED AMO" or "CANCELLED AFTER MARKET ORDER"
                => OrderStatus.Cancelled,
            "OPEN" or "TRIGGER PENDING" or "AMO MODIFIED" or "MODIFIED" or "OPEN PENDING"
                or "MODIFY PENDING" or "MODIFY VALIDATION PENDING" or "CANCEL PENDING"
                or "AFTER MARKET ORDER REQ RECEIVED" => OrderStatus.Open,
            "PARTIALLY FILLED" or "PARTIAL" or "PARTIALLY EXECUTED" => OrderStatus.PartiallyFilled,
            "PUT ORDER REQ RECEIVED" or "VALIDATION PENDING" or "AMO REQ RECEIVED"
                or "TRIGGER PENDING VALIDATION" => OrderStatus.Submitted,
            "EXPIRED" or "LAPSED" => OrderStatus.Expired,
            _ => Result<OrderStatus>.Failure(Unrecognised("order status", status)),
        };

    /// <summary>
    /// The order-book variant of <see cref="ToCanonicalOrderStatus"/>.
    ///
    /// Reading the whole order book must not fail because mStock introduced one new
    /// intermediate status overnight — that would blank the trader's blotter and hide the
    /// nineteen orders we <em>do</em> understand. So a status we cannot map degrades to
    /// <see cref="OrderStatus.Unknown"/> and the raw vendor text is returned in
    /// <paramref name="rawStatus"/> for the caller to put verbatim in
    /// <see cref="BrokerOrder.StatusMessage"/>. This is a loud, visible fallback, not a
    /// silent default: <see cref="OrderStatus.Unknown"/> is non-terminal and non-working, so
    /// the risk gate refuses to act on it and the UI shows it as needing attention.
    ///
    /// Single-order reads still use the strict overload — there, failing is the right answer.
    /// </summary>
    public static OrderStatus ToCanonicalOrderStatusOrUnknown(string status, out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status);
        if (mapped.IsSuccess)
        {
            rawStatus = null;
            return mapped.Value;
        }

        rawStatus = status;
        return OrderStatus.Unknown;
    }

    // --- TimeFrame -> chart interval -----------------------------------------------------

    /// <summary>
    /// Canonical time frame to mStock's chart interval token, plus whether the daily
    /// (historical) or intraday chart route serves it. They are different endpoints with
    /// different parameter shapes, so the caller needs both facts.
    /// </summary>
    public static Result<MStockChartInterval> ToNativeInterval(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => new MStockChartInterval("1minute", Intraday: true),
        TimeFrame.ThreeMinutes => new MStockChartInterval("3minute", Intraday: true),
        TimeFrame.FiveMinutes => new MStockChartInterval("5minute", Intraday: true),
        TimeFrame.FifteenMinutes => new MStockChartInterval("15minute", Intraday: true),
        TimeFrame.ThirtyMinutes => new MStockChartInterval("30minute", Intraday: true),
        TimeFrame.OneHour => new MStockChartInterval("60minute", Intraday: true),
        TimeFrame.OneDay => new MStockChartInterval("day", Intraday: false),
        TimeFrame.OneWeek or TimeFrame.OneMonth => Result<MStockChartInterval>.Failure(Unsupported(
            "chart interval",
            frame.ToString(),
            "mStock's charts stop at daily candles. Weekly and monthly series are aggregated from "
            + "daily bars in our own store rather than requested from the broker.")),
        _ => Result<MStockChartInterval>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    // --- instrument_type (script master) -------------------------------------------------

    /// <summary>
    /// The script master's <c>instrument_type</c> column to a canonical asset class.
    /// <paramref name="segment"/> disambiguates the cash rows, where mStock reuses "EQ" for
    /// both ordinary shares and exchange-traded funds and only the segment tells them apart.
    /// </summary>
    public static Result<AssetClass> ToCanonicalAssetClass(string instrumentType, string? segment)
    {
        var normalisedSegment = Normalise(segment ?? string.Empty);

        return Normalise(instrumentType) switch
        {
            "FUT" or "FUTIDX" or "FUTSTK" or "FUTCUR" or "FUTCOM" => AssetClass.Future,
            "CE" or "PE" or "OPT" or "OPTIDX" or "OPTSTK" or "OPTCUR" or "OPTFUT"
                => AssetClass.Option,
            "INDEX" or "INDICES" or "IDX" => AssetClass.Index,
            "ETF" => AssetClass.Etf,
            "EQ" or "EQUITY" or "BE" or "BZ" or "SM" or "ST" =>
                normalisedSegment.Contains("ETF", StringComparison.Ordinal)
                    ? AssetClass.Etf
                    : AssetClass.Equity,
            _ => Result<AssetClass>.Failure(Unrecognised("instrument type", instrumentType)),
        };
    }

    /// <summary>The option right encoded in the script master's <c>instrument_type</c>.</summary>
    public static Result<OptionRight> ToCanonicalOptionRight(string instrumentType) =>
        Normalise(instrumentType) switch
        {
            "CE" or "CALL" or "C" => OptionRight.Call,
            "PE" or "PUT" or "P" => OptionRight.Put,
            _ => Result<OptionRight>.Failure(Unrecognised("option right", instrumentType)),
        };

    // --- helpers -------------------------------------------------------------------------

    /// <summary>
    /// Vendor strings arrive with inconsistent case and stray whitespace, and mStock has
    /// shipped both "TRIGGER PENDING" and "trigger pending" in the same week. Normalise once,
    /// here, so that every <c>switch</c> above compares against a single canonical form.
    /// Invariant culture, because a Turkish server must still uppercase "i" to "I".
    /// </summary>
    private static string Normalise(string value) =>
        value.Trim().ToUpperInvariant().Replace('_', ' ');

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"mStock does not support the {what} '{value}'."
            : $"mStock does not support the {what} '{value}'. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["canonicalValue"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"mStock returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}

/// <summary>
/// A resolved mStock chart interval. <paramref name="Intraday"/> selects between the intraday
/// and the daily chart route, which are separate endpoints.
/// </summary>
public readonly record struct MStockChartInterval(string Interval, bool Intraday);
