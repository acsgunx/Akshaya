using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and the Kite Connect wire vocabulary.
///
/// It lives in one file, on purpose. The single most common way a broker integration rots is
/// that "NRML" gets spelled out in eight different files and one of them is missed when the
/// vendor renames it. Every translation in this connector goes through here; nothing else in the
/// assembly is allowed to contain a bare product / order-type / exchange literal.
///
/// Every method returns <see cref="Result{T}"/> and every unmapped value is a failure. There is
/// deliberately no <c>_ =&gt; "CNC"</c> fallback anywhere below: a silently defaulted product
/// code is an order placed with the wrong settlement, and a silently defaulted status is a
/// phantom order. Failing loudly costs a rejected ticket; guessing costs money.
/// </summary>
public static class ZerodhaMaps
{
    // --- Kite wire literals. The only place these strings appear. --------------------------

    /// <summary>Cash and carry — equity delivery.</summary>
    public const string ProductCnc = "CNC";

    /// <summary>Margin intraday square-off.</summary>
    public const string ProductMis = "MIS";

    /// <summary>Normal — the derivatives carry-forward product.</summary>
    public const string ProductNrml = "NRML";

    /// <summary>Margin Trading Facility — funded equity delivery.</summary>
    public const string ProductMtf = "MTF";

    public const string OrderTypeMarket = "MARKET";
    public const string OrderTypeLimit = "LIMIT";

    /// <summary>Stop-LIMIT, despite the name. Carries both a trigger and a price.</summary>
    public const string OrderTypeStopLoss = "SL";

    /// <summary>Stop-MARKET.</summary>
    public const string OrderTypeStopLossMarket = "SL-M";

    public const string ValidityDay = "DAY";
    public const string ValidityIoc = "IOC";

    /// <summary>Validity in minutes. Not exposed: the canonical vocabulary has no equivalent.</summary>
    public const string ValidityTtl = "TTL";

    public const string VarietyRegular = "regular";
    public const string VarietyAfterMarket = "amo";
    public const string VarietyCover = "co";
    public const string VarietyIceberg = "iceberg";

    public const string TransactionBuy = "BUY";
    public const string TransactionSell = "SELL";

    public const string ExchangeNse = "NSE";
    public const string ExchangeBse = "BSE";

    /// <summary>NSE's derivatives segment. An NSE future must be sent here, not to <c>NSE</c>.</summary>
    public const string ExchangeNfo = "NFO";

    /// <summary>BSE's derivatives segment.</summary>
    public const string ExchangeBfo = "BFO";

    /// <summary>Position is carried in from a previous day.</summary>
    public const string PositionTypeOvernight = "overnight";

    /// <summary>Position was opened today.</summary>
    public const string PositionTypeDay = "day";

    /// <summary>The instrument master's segment for index rows, which are not tradable.</summary>
    public const string SegmentIndices = "INDICES";

    // --- PositionEffect <-> product --------------------------------------------------------

    /// <summary>
    /// Canonical position effect to the Kite product code.
    ///
    /// <see cref="PositionEffect"/> is a flags enum because the concept fragments by market, but
    /// India's products are mutually exclusive, so only one product bit may be set. The one
    /// combination accepted is <c>ShortSell</c> alongside an intraday or carry-forward product:
    /// on Indian equities a short is expressed by <see cref="Side.Sell"/> with MIS, and on F&amp;O
    /// by NRML, so the extra bit is redundant rather than contradictory. <c>Delivery | ShortSell</c>
    /// is rejected — you cannot short a delivery position in India, and quietly turning that into
    /// a CNC sell would liquidate a holding the trader still has.
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
                "Kite supports exactly one of CNC (delivery), MIS (intraday), MTF (margin funding) or "
                + "NRML (carry-forward), and does not allow short selling in the delivery or MTF "
                + "products.")),
        };
    }

    /// <summary>
    /// A Kite product code to the canonical position effect.
    ///
    /// <c>BO</c> and <c>CO</c> appear on positions and orders created before Zerodha withdrew
    /// those products, and on second-leg orders. They are intraday exposures, so they map to
    /// <see cref="PositionEffect.Intraday"/> rather than failing — an old cover-order position
    /// that could not be read would be an exposure the risk engine cannot see.
    ///
    /// An EMPTY product is not an error. Falling back to <see cref="PositionEffect.Delivery"/> is
    /// the conservative choice rather than the neutral one: guessing "intraday" would tell the
    /// risk engine and the UI that this exposure disappears at the intraday square-off, and a
    /// trader who believes a position will close itself and is wrong is in a far worse place than
    /// one who believes it will persist and is wrong.
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
            ProductMis or "INTRADAY" or "BO" or "CO" => PositionEffect.Intraday,
            ProductMtf => PositionEffect.Margin,
            ProductNrml or "CARRYFORWARD" or "CARRY FORWARD" => PositionEffect.CarryForward,
            _ => Result<PositionEffect>.Failure(Unrecognised("product", product)),
        };
    }

    // --- OrderType <-> order_type -----------------------------------------------------------

    /// <summary>
    /// Canonical order type to Kite's <c>order_type</c>.
    ///
    /// Note the deliberate crossover that catches everyone once: Kite's <c>SL</c> is a
    /// stop-LIMIT (it carries both a trigger and a price) and <c>SL-M</c> is a stop-MARKET. The
    /// canonical names read the other way round, so <see cref="OrderType.Stop"/> maps to
    /// <c>SL-M</c> and <see cref="OrderType.StopLimit"/> maps to <c>SL</c>. Swapping these sends
    /// a protective stop as a limit order that never fills, or an intended limit as a market
    /// order that fills instantly.
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
            "Kite exposes only MARKET, LIMIT, SL and SL-M. Market-if-touched and trailing stops must "
            + "be synthesised above the connector if they are wanted.")),
        _ => Result<string>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>Kite's <c>order_type</c> to the canonical order type.</summary>
    public static Result<OrderType> ToCanonicalOrderType(string? orderType) => Normalise(orderType) switch
    {
        OrderTypeMarket => OrderType.Market,
        OrderTypeLimit => OrderType.Limit,
        OrderTypeStopLossMarket or "SL M" => OrderType.Stop,
        OrderTypeStopLoss => OrderType.StopLimit,
        _ => Result<OrderType>.Failure(Unrecognised("order type", orderType ?? string.Empty)),
    };

    // --- TimeInForce <-> validity -------------------------------------------------------------

    public static Result<string> ToNativeValidity(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => ValidityDay,
        TimeInForce.Ioc => ValidityIoc,
        TimeInForce.Gtc or TimeInForce.Gtd => Result<string>.Failure(Unsupported(
            "time in force",
            tif.ToString(),
            "Indian exchanges do not accept good-till-cancelled orders. Kite offers a separate GTT "
            + "trigger surface for resting multi-day orders, which this connector does not yet "
            + "expose, so a resting order must be managed by the platform.")),
        TimeInForce.Fok or TimeInForce.AtTheOpen or TimeInForce.AtTheClose =>
            Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
    };

    /// <summary>
    /// Kite's <c>validity</c> to the canonical time in force.
    ///
    /// <c>TTL</c> — a lifetime in minutes — has no canonical equivalent and is deliberately NOT
    /// mapped onto Day: an order that expires in two minutes is not a day order, and reporting it
    /// as one would leave the platform believing an order is still working long after it lapsed.
    /// </summary>
    public static Result<TimeInForce> ToCanonicalTimeInForce(string? validity)
    {
        if (string.IsNullOrWhiteSpace(validity))
        {
            return TimeInForce.Day;
        }

        return Normalise(validity) switch
        {
            ValidityDay => TimeInForce.Day,
            ValidityIoc => TimeInForce.Ioc,
            _ => Result<TimeInForce>.Failure(Unrecognised("validity", validity)),
        };
    }

    // --- OrderVariety <-> variety --------------------------------------------------------------

    public static Result<string> ToNativeVariety(OrderVariety variety) => variety switch
    {
        OrderVariety.Regular => VarietyRegular,
        OrderVariety.AfterMarket => VarietyAfterMarket,
        OrderVariety.Cover => Result<string>.Failure(Unsupported(
            "order variety",
            variety.ToString(),
            "Kite still routes cover orders, but they are two-legged: the first leg spawns a second "
            + "whose lifetime this connector does not model. Supporting them means teaching the order "
            + "store about parent_order_id first.")),
        OrderVariety.Iceberg => Result<string>.Failure(Unsupported(
            "order variety",
            variety.ToString(),
            "Kite's iceberg orders need a leg count and a per-leg quantity, and the shared order "
            + "contract has no field for either.")),
        OrderVariety.Bracket => Result<string>.Failure(Unsupported(
            "order variety",
            variety.ToString(),
            "Zerodha withdrew bracket orders.")),
        OrderVariety.GoodTillTriggered => Result<string>.Failure(Unsupported(
            "order variety",
            variety.ToString(),
            "Kite's GTT triggers are a separate API surface this connector does not yet expose.")),
        _ => Result<string>.Failure(Unsupported("order variety", variety.ToString(), null)),
    };

    /// <summary>
    /// Kite's <c>variety</c> back to a canonical one.
    ///
    /// Lenient in a way <see cref="ToNativeVariety"/> is not, and deliberately so: the order book
    /// returns every variety the ACCOUNT has used, including cover and iceberg orders placed from
    /// the Kite web terminal. Refusing to read those would hide live orders from the blotter.
    /// </summary>
    public static Result<OrderVariety> ToCanonicalVariety(string? variety) => Normalise(variety) switch
    {
        "REGULAR" => OrderVariety.Regular,
        "AMO" => OrderVariety.AfterMarket,
        "CO" => OrderVariety.Cover,
        "ICEBERG" => OrderVariety.Iceberg,
        "BO" => OrderVariety.Bracket,
        "AUCTION" => Result<OrderVariety>.Failure(Unrecognised("order variety", variety ?? string.Empty)),
        _ => Result<OrderVariety>.Failure(Unrecognised("order variety", variety ?? string.Empty)),
    };

    // --- Side <-> transaction_type ---------------------------------------------------------------

    public static Result<string> ToNativeSide(Side side) => side switch
    {
        Side.Buy => TransactionBuy,
        Side.Sell => TransactionSell,
        _ => Result<string>.Failure(Unsupported("side", side.ToString(), null)),
    };

    public static Result<Side> ToCanonicalSide(string? transactionType) => Normalise(transactionType) switch
    {
        TransactionBuy => Side.Buy,
        TransactionSell => Side.Sell,
        _ => Result<Side>.Failure(Unrecognised("transaction type", transactionType ?? string.Empty)),
    };

    // --- Venue <-> exchange ------------------------------------------------------------------------

    /// <summary>
    /// Canonical venue plus asset class to Kite's exchange segment.
    ///
    /// The asset class is load-bearing: NSE cash is <c>NSE</c> but NSE derivatives are <c>NFO</c>,
    /// and sending a futures order to <c>NSE</c> is rejected at the exchange. The MIC does not
    /// carry that distinction, which is why this takes both arguments.
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
                "This connector reaches NSE (XNSE) and BSE (XBOM), cash and derivative segments. Kite "
                + "also serves MCX commodities and currency derivatives; those are out of scope until "
                + "the platform ships a trading calendar and charge schedule for them.")),
        };
    }

    /// <summary>Kite's exchange segment to the canonical venue MIC.</summary>
    public static Result<Venue> ToCanonicalVenue(string? exchange) => Normalise(exchange) switch
    {
        ExchangeNse or ExchangeNfo => Venue.Nse,
        ExchangeBse or ExchangeBfo => Venue.Bse,
        "MCX" or "CDS" or "BCD" => Result<Venue>.Failure(Unsupported(
            "venue",
            exchange ?? string.Empty,
            "Commodity and currency segments are outside this connector's declared venues.")),
        _ => Result<Venue>.Failure(Unrecognised("exchange", exchange ?? string.Empty)),
    };

    /// <summary>True when the Kite exchange segment is a derivatives segment.</summary>
    public static bool IsDerivativeSegment(string? exchange) =>
        Normalise(exchange) is ExchangeNfo or ExchangeBfo;

    // --- OrderStatus ---------------------------------------------------------------------------------

    /// <summary>
    /// Kite's status vocabulary, collapsed onto the canonical lifecycle.
    ///
    /// The intermediate states matter more than they look. "PUT ORDER REQ RECEIVED",
    /// "VALIDATION PENDING" and "OPEN PENDING" mean the order is somewhere between Kite's OMS and
    /// the exchange and has NOT been acknowledged — treating those as Open is how a trader ends
    /// up believing they are in the market when they are not. They map to
    /// <see cref="OrderStatus.Submitted"/>.
    ///
    /// "TRIGGER PENDING" is the opposite case: an SL or SL-M order that is genuinely resting at
    /// the exchange waiting for its trigger, so it is Open. Calling it Submitted would make a
    /// working protective stop look like it had not arrived.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(string? status) => Normalise(status) switch
    {
        "COMPLETE" => OrderStatus.Filled,
        "REJECTED" => OrderStatus.Rejected,
        "CANCELLED" or "CANCELED" or "CANCELLED AMO" => OrderStatus.Cancelled,

        // Resting at the exchange. "MODIFY PENDING" and "CANCEL PENDING" are amendments in
        // flight against an order that is still working, so the order itself is still Open.
        "OPEN" or "TRIGGER PENDING" or "MODIFY PENDING" or "MODIFY VALIDATION PENDING"
            or "CANCEL PENDING" => OrderStatus.Open,

        // In flight between the OMS and the exchange, not yet acknowledged.
        "PUT ORDER REQ RECEIVED" or "VALIDATION PENDING" or "OPEN PENDING"
            or "AMO REQ RECEIVED" => OrderStatus.Submitted,

        _ => Result<OrderStatus>.Failure(Unrecognised("order status", status ?? string.Empty)),
    };

    /// <summary>
    /// The order-book variant of <see cref="ToCanonicalOrderStatus"/>.
    ///
    /// Kite's own documentation says of the status field: "There may be other values as well."
    /// Reading the whole order book must not fail because one of them turned up overnight — that
    /// would blank the trader's blotter and hide the nineteen orders we <em>do</em> understand.
    /// So an unmapped status degrades to <see cref="OrderStatus.Unknown"/> and the raw vendor
    /// text is returned in <paramref name="rawStatus"/> for the caller to put verbatim in
    /// <see cref="BrokerOrder.StatusMessage"/>. This is a loud, visible fallback, not a silent
    /// default: <see cref="OrderStatus.Unknown"/> is non-terminal and non-working, so the risk
    /// gate refuses to act on it and the UI shows it as needing attention.
    ///
    /// Single-order reads still use the strict overload — there, failing is the right answer.
    /// </summary>
    public static OrderStatus ToCanonicalOrderStatusOrUnknown(string? status, out string? rawStatus)
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

    // --- TimeFrame -> interval -------------------------------------------------------------------------

    /// <summary>
    /// Canonical time frame to Kite's historical-candle interval token.
    ///
    /// Kite stops at daily candles. Weekly and monthly series are aggregated from daily bars in
    /// the platform's own store rather than requested from the broker, which is why they fail
    /// here rather than being silently approximated by a 7-day or 30-day window.
    /// </summary>
    public static Result<string> ToNativeInterval(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => "minute",
        TimeFrame.ThreeMinutes => "3minute",
        TimeFrame.FiveMinutes => "5minute",
        TimeFrame.FifteenMinutes => "15minute",
        TimeFrame.ThirtyMinutes => "30minute",
        TimeFrame.OneHour => "60minute",
        TimeFrame.OneDay => "day",
        TimeFrame.OneWeek or TimeFrame.OneMonth => Result<string>.Failure(Unsupported(
            "chart interval",
            frame.ToString(),
            "Kite's candles stop at daily. Weekly and monthly series are aggregated from daily bars in "
            + "our own store.")),
        _ => Result<string>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    // --- StreamMode -> socket mode ---------------------------------------------------------------------

    /// <summary>The three modes Kite's WebSocket streams quote packets in.</summary>
    public static string ToNativeStreamMode(StreamMode mode) => mode switch
    {
        StreamMode.Ltp => "ltp",
        StreamMode.Quote => "quote",
        StreamMode.Full => "full",

        // Every member of the enum is covered above; this arm exists only because the compiler
        // cannot know that. Quote is the middle option and the safe one to land on.
        _ => "quote",
    };

    // --- instrument_type (instrument master) ------------------------------------------------------------

    /// <summary>
    /// The instrument master's <c>instrument_type</c> and <c>segment</c> to a canonical asset class.
    ///
    /// The segment is load-bearing and not decoration: Kite files index rows as
    /// <c>instrument_type=EQ</c> with <c>segment=INDICES</c>, so a mapping that read only the
    /// instrument type would file NIFTY 50 as a tradable equity — and the order ticket would then
    /// happily offer to buy an index.
    ///
    /// ETFs are NOT distinguishable here. Kite files them as ordinary <c>EQ</c> rows with no flag
    /// of any kind, so an ETF decodes as <see cref="AssetClass.Equity"/>. The manifest still
    /// declares Etf because Kite genuinely trades ETFs and an Etf order routes correctly; what is
    /// missing is the reference data to label one, not the ability to trade it.
    /// </summary>
    public static Result<AssetClass> ToCanonicalAssetClass(string? instrumentType, string? segment)
    {
        if (string.Equals(Normalise(segment), SegmentIndices, StringComparison.Ordinal))
        {
            return AssetClass.Index;
        }

        return Normalise(instrumentType) switch
        {
            "EQ" => AssetClass.Equity,
            "FUT" => AssetClass.Future,
            "CE" or "PE" => AssetClass.Option,
            _ => Result<AssetClass>.Failure(Unrecognised("instrument type", instrumentType ?? string.Empty)),
        };
    }

    /// <summary>The option right in the instrument master's <c>instrument_type</c> column.</summary>
    public static Result<OptionRight> ToCanonicalOptionRight(string? instrumentType) =>
        Normalise(instrumentType) switch
        {
            "CE" => OptionRight.Call,
            "PE" => OptionRight.Put,
            _ => Result<OptionRight>.Failure(Unrecognised("option right", instrumentType ?? string.Empty)),
        };

    // --- helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// Vendor strings arrive with inconsistent case and stray whitespace, and Kite's statuses are
    /// multi-word ("TRIGGER PENDING"). Normalise once, here, so that every <c>switch</c> above
    /// compares against a single canonical form. Invariant culture, because a Turkish server must
    /// still uppercase "i" to "I".
    /// </summary>
    private static string Normalise(string? value) =>
        value is null ? string.Empty : value.Trim().ToUpperInvariant().Replace('_', ' ');

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"Kite does not support the {what} '{value}'."
            : $"Kite does not support the {what} '{value}'. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["canonicalValue"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"Kite returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}
