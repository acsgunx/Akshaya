using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and the FYERS wire vocabulary.
///
/// It lives in one file, on purpose. The single most common way a broker integration rots is
/// that "INTRADAY" gets spelled out in eight different files and one of them is missed when the
/// vendor renames it. Every translation in this connector goes through here; nothing else in
/// the assembly is allowed to contain a bare product / order-type / exchange literal.
///
/// Every method returns <see cref="Result{T}"/> and every unmapped value is a failure. There is
/// deliberately no <c>_ =&gt; "CNC"</c> fallback anywhere below: a silently defaulted product
/// code is an order placed with the wrong settlement, and a silently defaulted status is a
/// phantom order. Failing loudly costs a rejected ticket; guessing costs money.
/// </summary>
public static class FyersMaps
{
    // --- FYERS wire literals. The only place these strings and numbers appear. ------------

    /// <summary>Delivery. Equity only.</summary>
    public const string ProductCnc = "CNC";

    /// <summary>Intraday, in every segment.</summary>
    public const string ProductIntraday = "INTRADAY";

    /// <summary>Carry-forward on derivatives. NOT the canonical <see cref="PositionEffect.Margin"/>.</summary>
    public const string ProductMargin = "MARGIN";

    /// <summary>Margin Trading Facility — funded equity delivery, on approved symbols only.</summary>
    public const string ProductMtf = "MTF";

    public const int OrderTypeLimit = 1;
    public const int OrderTypeMarket = 2;

    /// <summary>Stop-market. FYERS calls it SL-M.</summary>
    public const int OrderTypeStopMarket = 3;

    /// <summary>Stop-limit. FYERS calls it SL-L.</summary>
    public const int OrderTypeStopLimit = 4;

    public const string ValidityDay = "DAY";
    public const string ValidityIoc = "IOC";

    public const int SideBuy = 1;
    public const int SideSell = -1;

    public const string ExchangeNse = "NSE";
    public const string ExchangeBse = "BSE";
    public const string ExchangeMcx = "MCX";

    /// <summary>Exchange code on the wire. Note that 11 is MCX and 12 is BSE, not the reverse.</summary>
    public const int ExchangeCodeNse = 10;

    /// <inheritdoc cref="ExchangeCodeNse"/>
    public const int ExchangeCodeMcx = 11;

    /// <inheritdoc cref="ExchangeCodeNse"/>
    public const int ExchangeCodeBse = 12;

    public const int SegmentCapitalMarket = 10;
    public const int SegmentEquityDerivatives = 11;
    public const int SegmentCurrencyDerivatives = 12;
    public const int SegmentCommodityDerivatives = 20;

    // --- PositionEffect <-> productType ---------------------------------------------------

    /// <summary>
    /// Canonical position effect to the FYERS product code.
    ///
    /// READ THE MARGIN LINE TWICE. FYERS' <c>MARGIN</c> is the derivatives CARRY-FORWARD
    /// product — the NRML of every other Indian broker — and its margin-funding product is
    /// called <c>MTF</c>. The canonical vocabulary uses <see cref="PositionEffect.Margin"/> for
    /// margin funding and <see cref="PositionEffect.CarryForward"/> for overnight derivatives,
    /// so the two names CROSS here. Mapping them by name rather than by meaning would take a
    /// trader's overnight futures position and place it as funded equity, or refuse it
    /// outright — and the symmetry of the mistake means it survives a casual review.
    ///
    /// <see cref="PositionEffect"/> is a flags enum because the concept fragments by market,
    /// but India's products are mutually exclusive, so only one product bit may be set. The one
    /// combination accepted is <c>ShortSell</c> alongside an intraday or carry-forward product:
    /// on Indian equities a short is expressed by <see cref="Side.Sell"/> with an intraday
    /// product, and on F&amp;O by the carry-forward one, so the extra bit is redundant rather
    /// than contradictory. <c>Delivery | ShortSell</c> is rejected — you cannot short a delivery
    /// position in India, and quietly turning that into a CNC sell would liquidate a holding the
    /// trader still has.
    /// </summary>
    public static Result<string> ToNativeProduct(PositionEffect effect)
    {
        var product = effect & ~PositionEffect.ShortSell;
        var isShort = effect.HasFlag(PositionEffect.ShortSell);

        return product switch
        {
            PositionEffect.Delivery when !isShort => ProductCnc,
            PositionEffect.Intraday => ProductIntraday,
            PositionEffect.Margin when !isShort => ProductMtf,
            PositionEffect.CarryForward => ProductMargin,
            _ => Result<string>.Failure(Unsupported(
                "product",
                effect.ToString(),
                "FYERS supports exactly one of CNC (delivery), INTRADAY, MARGIN (derivatives "
                + "carry-forward) or MTF (margin funding), and does not allow short selling in the "
                + "delivery or MTF products.")),
        };
    }

    /// <summary>
    /// A FYERS product code to the canonical position effect.
    ///
    /// The reports API answers with a different vocabulary from the one the order routes accept
    /// — <c>product_type: "Overnight"</c> where the order book says <c>MARGIN</c> — so the
    /// aliases below are accepted on the way IN but must never be emitted on the way OUT. Only
    /// the four codes above are valid in a request; see <see cref="ToNativeProduct"/>.
    ///
    /// An EMPTY product is not an error. Falling back to <see cref="PositionEffect.Delivery"/>
    /// is the conservative choice rather than the neutral one: guessing "intraday" would tell
    /// the risk engine and the UI that this exposure disappears at the intraday square-off, and
    /// a trader who believes a position will close itself and is wrong is in a far worse place
    /// than one who believes it will persist and is wrong.
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
            ProductIntraday or "MIS" or "DAY" => PositionEffect.Intraday,
            ProductMtf => PositionEffect.Margin,
            ProductMargin or "OVERNIGHT" or "NRML" or "CARRYFORWARD" or "CARRY FORWARD"
                => PositionEffect.CarryForward,
            _ => Result<PositionEffect>.Failure(Unrecognised("product", product)),
        };
    }

    // --- OrderType <-> type ---------------------------------------------------------------

    /// <summary>
    /// Canonical order type to the FYERS numeric <c>type</c>.
    ///
    /// FYERS names these the way the canonical vocabulary does — its 3 is documented as
    /// "Stop Order (SL-M)" and its 4 as "Stoplimit Order (SL-L)" — so unlike some Indian
    /// brokers there is no crossover here. Do not "fix" it into one.
    /// </summary>
    public static Result<int> ToNativeOrderType(OrderType type) => type switch
    {
        OrderType.Market => OrderTypeMarket,
        OrderType.Limit => OrderTypeLimit,
        OrderType.Stop => OrderTypeStopMarket,
        OrderType.StopLimit => OrderTypeStopLimit,
        OrderType.MarketIfTouched or OrderType.TrailingStop => Result<int>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "FYERS exposes only limit, market, SL-M and SL-L. Market-if-touched and trailing stops "
            + "must be synthesised above the connector if they are wanted.")),
        _ => Result<int>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>The FYERS numeric <c>type</c> to a canonical order type.</summary>
    public static Result<OrderType> ToCanonicalOrderType(int type) => type switch
    {
        OrderTypeLimit => OrderType.Limit,
        OrderTypeMarket => OrderType.Market,
        OrderTypeStopMarket => OrderType.Stop,
        OrderTypeStopLimit => OrderType.StopLimit,
        _ => Result<OrderType>.Failure(
            Unrecognised("order type", type.ToString(CultureInfo.InvariantCulture))),
    };

    // --- TimeInForce <-> validity ----------------------------------------------------------

    public static Result<string> ToNativeValidity(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => ValidityDay,
        TimeInForce.Ioc => ValidityIoc,
        TimeInForce.Gtc or TimeInForce.Gtd => Result<string>.Failure(Unsupported(
            "time in force",
            tif.ToString(),
            "Indian exchanges do not accept good-till-cancelled orders. FYERS offers a separate GTT "
            + "surface for resting multi-day orders, which this connector does not yet expose, so a "
            + "resting order must be managed by the platform.")),
        TimeInForce.Fok or TimeInForce.AtTheOpen or TimeInForce.AtTheClose =>
            Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), null)),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(string? validity)
    {
        // The order socket omits validity on some acknowledgement frames. DAY is the exchange
        // default for every order this connector can place, and the alternative — failing the
        // whole update — would drop a fill.
        if (string.IsNullOrWhiteSpace(validity))
        {
            return TimeInForce.Day;
        }

        return Normalise(validity) switch
        {
            ValidityDay => TimeInForce.Day,
            ValidityIoc or "IMMEDIATE OR CANCEL" => TimeInForce.Ioc,
            _ => Result<TimeInForce>.Failure(Unrecognised("validity", validity)),
        };
    }

    // --- OrderVariety <-> offlineOrder -----------------------------------------------------

    /// <summary>
    /// Canonical variety to the FYERS <c>offlineOrder</c> flag.
    ///
    /// FYERS has no variety field: an after-market order is an ordinary order with
    /// <c>offlineOrder: true</c>. Cover and bracket orders were removed from the API on
    /// 2 August 2026 and the manifest declares neither.
    /// </summary>
    public static Result<bool> ToNativeOfflineFlag(OrderVariety variety) => variety switch
    {
        OrderVariety.Regular => false,
        OrderVariety.AfterMarket => true,
        OrderVariety.Cover or OrderVariety.Bracket => Result<bool>.Failure(Unsupported(
            "order variety",
            variety.ToString(),
            "FYERS deprecated cover and bracket orders in its API on 2 August 2026.")),
        OrderVariety.Iceberg or OrderVariety.GoodTillTriggered =>
            Result<bool>.Failure(Unsupported("order variety", variety.ToString(), null)),
        _ => Result<bool>.Failure(Unsupported("order variety", variety.ToString(), null)),
    };

    /// <summary>The FYERS <c>offlineOrder</c> flag back to a canonical variety.</summary>
    public static OrderVariety ToCanonicalVariety(bool offlineOrder) =>
        offlineOrder ? OrderVariety.AfterMarket : OrderVariety.Regular;

    // --- Side <-> side ---------------------------------------------------------------------

    public static Result<int> ToNativeSide(Side side) => side switch
    {
        Side.Buy => SideBuy,
        Side.Sell => SideSell,
        _ => Result<int>.Failure(Unsupported("side", side.ToString(), null)),
    };

    public static Result<Side> ToCanonicalSide(int side) => side switch
    {
        SideBuy => Side.Buy,
        SideSell => Side.Sell,
        _ => Result<Side>.Failure(Unrecognised("side", side.ToString(CultureInfo.InvariantCulture))),
    };

    // --- Venue <-> exchange -----------------------------------------------------------------

    /// <summary>
    /// Canonical venue to the FYERS exchange prefix.
    ///
    /// Unlike most Indian APIs the asset class does NOT change this: FYERS puts the segment in
    /// the symbol itself, so an NSE future is <c>NSE:NIFTY26SEPFUT</c> rather than an <c>NFO</c>
    /// order. There is therefore nothing to get wrong here — and nothing to "improve" by adding
    /// an NFO branch, which FYERS would reject.
    /// </summary>
    public static Result<string> ToNativeExchange(Venue venue) => venue.Mic switch
    {
        "XNSE" => ExchangeNse,
        "XBOM" => ExchangeBse,
        _ => Result<string>.Failure(Unsupported(
            "venue",
            venue.Mic,
            "This connector reaches NSE (XNSE) and BSE (XBOM). FYERS also serves MCX commodities and "
            + "currency derivatives; those are out of scope until the platform ships a commodity "
            + "trading calendar and charge schedule for them.")),
    };

    /// <summary>The FYERS exchange prefix to the canonical venue MIC.</summary>
    public static Result<Venue> ToCanonicalVenue(string exchange) => Normalise(exchange) switch
    {
        ExchangeNse => Venue.Nse,
        ExchangeBse => Venue.Bse,
        ExchangeMcx => Result<Venue>.Failure(Unsupported(
            "venue",
            ExchangeMcx,
            "MCX commodities are outside this connector's declared venues.")),
        _ => Result<Venue>.Failure(Unrecognised("exchange", exchange)),
    };

    /// <summary>
    /// The numeric exchange code to the canonical venue MIC.
    ///
    /// Used only where a payload carries no symbol to read the prefix from. Prefer the SYMBOL
    /// wherever both are present: FYERS' own documentation contains a positions sample whose
    /// symbol is <c>MCX:SILVERMIC20AUGFUT</c> and whose <c>exchange</c> field says 10 (NSE), so
    /// the numeric field is demonstrably not reliable on its own.
    /// </summary>
    public static Result<Venue> ToCanonicalVenue(int exchangeCode) => exchangeCode switch
    {
        ExchangeCodeNse => Venue.Nse,
        ExchangeCodeBse => Venue.Bse,
        ExchangeCodeMcx => Result<Venue>.Failure(Unsupported(
            "venue",
            ExchangeMcx,
            "MCX commodities are outside this connector's declared venues.")),
        _ => Result<Venue>.Failure(
            Unrecognised("exchange", exchangeCode.ToString(CultureInfo.InvariantCulture))),
    };

    // --- OrderStatus -------------------------------------------------------------------------

    /// <summary>
    /// The FYERS numeric order status, collapsed onto the canonical lifecycle.
    ///
    /// Two things here are worth more attention than the switch suggests.
    ///
    /// FYERS HAS NO PARTIALLY-FILLED STATUS. A partly executed order stays at 6 (Pending) with a
    /// non-zero <c>filledQty</c>, and 2 (Traded) means fully filled. That is why this takes the
    /// filled quantity: without it a half-filled order reads as merely resting, and anything
    /// sizing off it — a square-off, a hedge, the risk gate's exposure check — works from a
    /// position that is already half on.
    ///
    /// 4 (Transit) means the order is somewhere between FYERS and the exchange and has NOT been
    /// acknowledged. Treating it as Open is how a trader ends up believing they are in the
    /// market when they are not, so it maps to <see cref="OrderStatus.Submitted"/>.
    /// </summary>
    /// <param name="status">The wire <c>status</c> field.</param>
    /// <param name="filledQuantity">The wire <c>filledQty</c>, which carries the partial fill.</param>
    public static Result<OrderStatus> ToCanonicalOrderStatus(int status, decimal filledQuantity = 0m)
    {
        var mapped = status switch
        {
            1 => OrderStatus.Cancelled,
            2 => OrderStatus.Filled,
            4 => OrderStatus.Submitted,
            5 => OrderStatus.Rejected,
            6 => OrderStatus.Open,
            7 => OrderStatus.Expired,

            // 3 is documented as "For future use". An undocumented status is not something to
            // guess at: it fails here, and the order-book path below degrades it to Unknown.
            _ => (OrderStatus?)null,
        };

        if (mapped is not { } value)
        {
            return Result<OrderStatus>.Failure(
                Unrecognised("order status", status.ToString(CultureInfo.InvariantCulture)));
        }

        return filledQuantity > 0m && value is OrderStatus.Open or OrderStatus.Submitted
            ? OrderStatus.PartiallyFilled
            : value;
    }

    /// <summary>
    /// The order-book variant of <see cref="ToCanonicalOrderStatus"/>.
    ///
    /// Reading the whole order book must not fail because FYERS started using status 3 overnight
    /// — that would blank the trader's blotter and hide the nineteen orders we <em>do</em>
    /// understand. So a status we cannot map degrades to <see cref="OrderStatus.Unknown"/> and
    /// the raw value is returned in <paramref name="rawStatus"/> for the caller to put verbatim
    /// in <see cref="BrokerOrder.StatusMessage"/>. This is a loud, visible fallback, not a silent
    /// default: <see cref="OrderStatus.Unknown"/> is non-terminal and non-working, so the risk
    /// gate refuses to act on it and the UI shows it as needing attention.
    /// </summary>
    public static OrderStatus ToCanonicalOrderStatusOrUnknown(
        int status,
        decimal filledQuantity,
        out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status, filledQuantity);
        if (mapped.IsSuccess)
        {
            rawStatus = null;
            return mapped.Value;
        }

        rawStatus = $"FYERS status {status.ToString(CultureInfo.InvariantCulture)}";
        return OrderStatus.Unknown;
    }

    // --- TimeFrame -> resolution ---------------------------------------------------------------

    /// <summary>
    /// Canonical time frame to the FYERS chart <c>resolution</c> token, plus whether the
    /// per-request range cap is the intraday one or the daily one. They differ by a factor of
    /// nearly four, so the caller needs both facts.
    /// </summary>
    public static Result<FyersResolution> ToNativeResolution(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => new FyersResolution("1", Intraday: true),
        TimeFrame.ThreeMinutes => new FyersResolution("3", Intraday: true),
        TimeFrame.FiveMinutes => new FyersResolution("5", Intraday: true),
        TimeFrame.FifteenMinutes => new FyersResolution("15", Intraday: true),
        TimeFrame.ThirtyMinutes => new FyersResolution("30", Intraday: true),
        TimeFrame.OneHour => new FyersResolution("60", Intraday: true),
        TimeFrame.OneDay => new FyersResolution("1D", Intraday: false),
        TimeFrame.OneWeek => new FyersResolution("1W", Intraday: false),
        TimeFrame.OneMonth => new FyersResolution("1M", Intraday: false),
        _ => Result<FyersResolution>.Failure(Unsupported("chart resolution", frame.ToString(), null)),
    };

    // --- instrument type (symbol master) ---------------------------------------------------------

    /// <summary>
    /// The symbol master's exchange-instrument-type column to a canonical asset class.
    ///
    /// Only the classes this connector's manifest declares are mapped. Preference shares,
    /// debentures, warrants, sovereign gold bonds, G-secs, T-bills and mutual funds all appear
    /// in the same files and all fail here on purpose: the ingest counts them as skipped rows
    /// rather than filing them under Equity, which would put an untradable instrument in the
    /// search box.
    /// </summary>
    public static Result<AssetClass> ToCanonicalAssetClass(int instrumentType) => instrumentType switch
    {
        0 => AssetClass.Equity,
        9 => AssetClass.Etf,
        10 => AssetClass.Index,
        11 or 12 or 13 => AssetClass.Future,
        14 or 15 => AssetClass.Option,
        _ => Result<AssetClass>.Failure(
            Unrecognised("instrument type", instrumentType.ToString(CultureInfo.InvariantCulture))),
    };

    /// <summary>The option right in the symbol master's option-type column, and in the option chain.</summary>
    public static Result<OptionRight> ToCanonicalOptionRight(string? optionType) => Normalise(optionType) switch
    {
        "CE" or "CALL" or "C" => OptionRight.Call,
        "PE" or "PUT" or "P" => OptionRight.Put,
        _ => Result<OptionRight>.Failure(Unrecognised("option right", optionType ?? string.Empty)),
    };

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Vendor strings arrive with inconsistent case and stray whitespace. Normalise once, here,
    /// so that every <c>switch</c> above compares against a single canonical form. Invariant
    /// culture, because a Turkish server must still uppercase "i" to "I".
    /// </summary>
    private static string Normalise(string? value) =>
        value is null ? string.Empty : value.Trim().ToUpperInvariant().Replace('_', ' ');

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"FYERS does not support the {what} '{value}'."
            : $"FYERS does not support the {what} '{value}'. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["canonicalValue"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"FYERS returned a {what} this connector does not recognise: '{value}'. "
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
/// A resolved FYERS chart resolution. <paramref name="Intraday"/> selects which documented
/// per-request range cap applies: 100 days for minute resolutions, 366 for day, week and month.
/// </summary>
public readonly record struct FyersResolution(string Resolution, bool Intraday);
