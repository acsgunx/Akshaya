using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// One of the two markets this connector trades, with every per-market code OpenD uses for it.
///
/// OpenD names a market three different ways depending on the protocol family — a trading market, a
/// trading SECURITY market, and a quote market — and the three numberings disagree (the US is 2, 2 and
/// 11). Carrying them together means a US order can never go out with Hong Kong's quote code.
/// </summary>
/// <param name="Prefix">The SDK's own notation, as in <c>US.AAPL</c> and <c>HK.00700</c>.</param>
/// <param name="TrdMarket">Trd_Common.TrdMarket.</param>
/// <param name="TrdSecMarket">Trd_Common.TrdSecMarket.</param>
/// <param name="QotMarket">Qot_Common.QotMarket.</param>
/// <param name="Currency">What the market's instruments are priced in.</param>
/// <param name="Zone">The zone OpenD's naive time strings for this market are in.</param>
/// <param name="TickSize">
/// The SMALLEST tick the venue uses. A per-instrument tick depends on price bands OpenD does not
/// publish, and the smallest one never makes the platform refuse an order the venue would accept.
/// </param>
/// <param name="SettlementDays">T+1 in the US, T+2 in Hong Kong.</param>
internal sealed record MoomooMarket(
    string Prefix,
    int TrdMarket,
    int TrdSecMarket,
    int QotMarket,
    Currency Currency,
    TimeZoneInfo Zone,
    decimal TickSize,
    int SettlementDays)
{
    public static readonly MoomooMarket Us = new("US", 2, 2, 11, Currency.Usd, MoomooTime.NewYork, 0.0001m, 1);

    public static readonly MoomooMarket Hk = new("HK", 1, 1, 1, Currency.Hkd, MoomooTime.HongKong, 0.001m, 2);

    /// <summary>In the order the connector reads accounts: the US first, because it is where most accounts trade.</summary>
    public static readonly IReadOnlyList<MoomooMarket> All = [Us, Hk];
}

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and OpenD's numeric enums.
///
/// It lives in one file on purpose, and every number OpenD uses appears here and nowhere else. Every
/// method returns <see cref="Result{T}"/>, and an unmapped value is a failure rather than a default: a
/// silently defaulted status is a phantom order, and a silently defaulted side is a position in the
/// wrong direction.
/// </summary>
internal static class MoomooMaps
{
    // --- environment, account and connection ------------------------------------------------------

    public const int TrdEnvSimulate = 0;
    public const int TrdEnvReal = 1;
    public const int TrdCategorySecurity = 1;
    public const int TrdAccStatusActive = 0;
    public const int TrdAccTypeCash = 1;
    public const int TrdAccTypeMargin = 2;
    public const int PacketEncAlgoNone = -1;
    public const int ProtoFmtJson = 1;
    public const int ProgramStatusReady = 10;

    public const string EnvironmentReal = "real";
    public const string EnvironmentPaper = "paper";

    // --- Trd_Common.TrdSide -------------------------------------------------------------------------

    public const int TrdSideBuy = 1;
    public const int TrdSideSell = 2;

    /// <summary>Returned on a US short sale. Clients never send it; a sell beyond the long position is a short.</summary>
    public const int TrdSideSellShort = 3;

    /// <summary>Buying back a short. Documented as not currently used, but not excluded either.</summary>
    public const int TrdSideBuyBack = 4;

    // --- Trd_Common.OrderType -----------------------------------------------------------------------

    /// <summary>
    /// "Normal": a limit order in the US, and HKEX's ENHANCED limit order in Hong Kong, which may fill
    /// at up to ten price levels better than the limit. Either way it carries a limit price.
    /// </summary>
    public const int OrderTypeNormal = 1;

    public const int OrderTypeMarket = 2;
    public const int OrderTypeAbsoluteLimit = 5;
    public const int OrderTypeAuction = 6;
    public const int OrderTypeAuctionLimit = 7;
    public const int OrderTypeSpecialLimit = 8;
    public const int OrderTypeSpecialLimitAll = 9;
    public const int OrderTypeStop = 10;
    public const int OrderTypeStopLimit = 11;
    public const int OrderTypeMarketIfTouched = 12;
    public const int OrderTypeLimitIfTouched = 13;
    public const int OrderTypeTrailingStop = 14;
    public const int OrderTypeTrailingStopLimit = 15;
    public const int OrderTypeTwapMarket = 16;
    public const int OrderTypeTwapLimit = 17;
    public const int OrderTypeVwapMarket = 18;
    public const int OrderTypeVwapLimit = 19;

    // --- Trd_Common.OrderStatus ---------------------------------------------------------------------

    public const int StatusUnsubmitted = 0;
    public const int StatusUnknown = -1;
    public const int StatusWaitingSubmit = 1;
    public const int StatusSubmitting = 2;
    public const int StatusSubmitFailed = 3;
    public const int StatusTimeOut = 4;
    public const int StatusSubmitted = 5;
    public const int StatusFilledPart = 10;
    public const int StatusFilledAll = 11;
    public const int StatusCancellingPart = 12;
    public const int StatusCancellingAll = 13;
    public const int StatusCancelledPart = 14;
    public const int StatusCancelledAll = 15;
    public const int StatusFailed = 21;
    public const int StatusDisabled = 22;
    public const int StatusDeleted = 23;
    public const int StatusFillCancelled = 24;

    // --- modification, validity, positions, fills ----------------------------------------------------

    public const int ModifyOpNormal = 1;
    public const int ModifyOpCancel = 2;
    public const int TimeInForceDay = 0;
    public const int TimeInForceGtc = 1;
    public const int PositionSideLong = 0;
    public const int PositionSideShort = 1;
    public const int FillStatusOk = 0;
    public const int FillStatusCancelled = 1;

    // --- quotes ----------------------------------------------------------------------------------------

    public const int SecurityTypeEquity = 3;
    public const int SecurityTypeTrust = 4;
    public const int SecurityTypeIndex = 6;
    public const int SecurityTypeDerivative = 8;

    public const int ExchHkMainBoard = 1;
    public const int ExchHkGemBoard = 2;
    public const int ExchHkHkex = 3;
    public const int ExchUsNyse = 4;
    public const int ExchUsNasdaq = 5;
    public const int ExchUsPink = 6;
    public const int ExchUsAmex = 7;
    public const int ExchUsOption = 8;

    public const int RehabNone = 0;
    public const int SubTypeBasic = 1;
    public const int SubTypeOrderBook = 2;
    public const int OptionTypeCall = 1;
    public const int OptionTypePut = 2;

    public const int NotifyGatewayEvent = 1;
    public const int NotifyProgramStatus = 2;
    public const int NotifyConnectionStatus = 3;

    /// <summary>NYSE American. Not one of SharedKernel's convenience handles.</summary>
    public static readonly Venue NyseAmerican = new("XASE");

    // --- markets --------------------------------------------------------------------------------------

    /// <summary>
    /// Every US MIC routes to OpenD's single US market: moomoo addresses a US security by symbol and
    /// routes it itself, so an order keyed on ARCX:SPY and one keyed on XNYS:SPY both reach SPY. The
    /// reverse direction is NOT symmetric — see <see cref="ToCanonicalVenue"/>.
    /// </summary>
    public static Result<MoomooMarket> MarketForVenue(Venue venue) => venue.Mic switch
    {
        "XNYS" or "XNAS" or "XASE" or "ARCX" or "BATS" => MoomooMarket.Us,
        "XHKG" => MoomooMarket.Hk,
        _ => Result<MoomooMarket>.Failure(Unsupported(
            "venue",
            venue.Mic,
            "This connector reaches the US listing venues and HKEX. moomoo also serves China Connect, "
            + "Singapore, Japan, Australia, Canada and Malaysia; those stay out of scope until the platform "
            + "has trading calendars for them.")),
    };

    public static Result<MoomooMarket> MarketForTrdMarket(int trdMarket) => trdMarket switch
    {
        2 => MoomooMarket.Us,
        1 => MoomooMarket.Hk,
        _ => Result<MoomooMarket>.Failure(Unsupported("trading market", Text(trdMarket), null)),
    };

    public static Result<MoomooMarket> MarketForSecMarket(int secMarket) => secMarket switch
    {
        2 => MoomooMarket.Us,
        1 => MoomooMarket.Hk,
        _ => Result<MoomooMarket>.Failure(Unsupported("security market", Text(secMarket), null)),
    };

    /// <summary>QotMarket_HK_Future (2) is deprecated in favour of HK_Security and still maps to Hong Kong.</summary>
    public static Result<MoomooMarket> MarketForQotMarket(int qotMarket) => qotMarket switch
    {
        11 => MoomooMarket.Us,
        1 or 2 => MoomooMarket.Hk,
        _ => Result<MoomooMarket>.Failure(Unsupported("quote market", Text(qotMarket), null)),
    };

    public static Result<MoomooMarket> MarketForPrefix(string? prefix) => prefix?.Trim().ToUpperInvariant() switch
    {
        "US" => MoomooMarket.Us,
        "HK" => MoomooMarket.Hk,
        _ => Result<MoomooMarket>.Failure(Unsupported("market prefix", prefix ?? string.Empty, null)),
    };

    // --- listing exchange -> venue --------------------------------------------------------------------

    /// <summary>
    /// The listing exchange OpenD reports in static info, to a MIC.
    ///
    /// OpenD's exchange list has NYSE, Nasdaq and NYSE American but no NYSE Arca or Cboe BZX, so ETFs
    /// listed on those arrive labelled as one of the three — which means SPY decodes as whatever OpenD
    /// calls it rather than ARCX. Orders route correctly either way (<see cref="MarketForVenue"/>);
    /// what differs is the canonical key another broker would give the same fund, and the blended
    /// portfolio cannot merge the two without an ISIN, which OpenD does not provide.
    /// </summary>
    public static Result<Venue> ToCanonicalVenue(int exchType) => exchType switch
    {
        ExchUsNyse => Venue.Nyse,
        ExchUsNasdaq => Venue.Nasdaq,
        ExchUsAmex => NyseAmerican,
        ExchHkMainBoard or ExchHkGemBoard or ExchHkHkex => Venue.Hkex,
        ExchUsPink => Result<Venue>.Failure(Unsupported(
            "exchange",
            "US_Pink",
            "OTC securities are outside this connector's venues, and moomoo does not quote them over the API.")),
        _ => Result<Venue>.Failure(Unsupported("exchange type", Text(exchType), null)),
    };

    // --- security type -> asset class -------------------------------------------------------------------

    /// <summary>
    /// OpenD's security type to the canonical asset class. "Trust" is OpenD's word for funds, which on
    /// these two venues means ETFs. Warrants and CBBCs are Hong Kong structured products the canonical
    /// vocabulary has no class for; they fail rather than pass as equities.
    /// </summary>
    public static Result<AssetClass> ToCanonicalAssetClass(int secType) => secType switch
    {
        SecurityTypeEquity => AssetClass.Equity,
        SecurityTypeTrust => AssetClass.Etf,
        SecurityTypeIndex => AssetClass.Index,
        SecurityTypeDerivative => AssetClass.Option,
        _ => Result<AssetClass>.Failure(Unsupported(
            "security type",
            Text(secType),
            "Warrants, CBBCs, bonds and futures are not traded through this connector.")),
    };

    // --- side -----------------------------------------------------------------------------------------------

    public static int ToNativeSide(Side side) => side == Side.Buy ? TrdSideBuy : TrdSideSell;

    /// <summary>OpenD's side, which also says whether the order opens or closes a short.</summary>
    public static Result<(Side Side, bool IsShort)> ToCanonicalSide(int trdSide) => trdSide switch
    {
        TrdSideBuy => (Side.Buy, false),
        TrdSideSell => (Side.Sell, false),
        TrdSideSellShort => (Side.Sell, true),
        TrdSideBuyBack => (Side.Buy, true),
        _ => Result<(Side, bool)>.Failure(Unrecognised("trade side", Text(trdSide))),
    };

    // --- position effect ---------------------------------------------------------------------------------------

    /// <summary>
    /// Which canonical position effects an order may carry.
    ///
    /// OpenD has no product field at all. Whether a buy uses margin is decided by the ACCOUNT — a margin
    /// account borrows when cash runs out, a cash account refuses — and a sell beyond the long position
    /// is a short sale on a margin account. So the honest vocabulary is two words: <c>Delivery</c> for
    /// an ordinary order and <c>ShortSell</c> to say a sell is meant to open a short (or a buy to close
    /// one). Accepting <c>Margin</c> would promise a funding choice this connector cannot make, and a
    /// trader who picked "cash" on a margin account would be borrowing without knowing it.
    /// </summary>
    public static Result ValidatePositionEffect(PositionEffect effect)
    {
        var product = effect & ~PositionEffect.ShortSell;

        return product is PositionEffect.None or PositionEffect.Delivery
               && effect != PositionEffect.None
            ? Result.Success()
            : Unsupported(
                "product",
                effect.ToString(),
                "moomoo has no product field: margin use is decided by the account type, and a sell beyond "
                + "the long position on a margin account is a short sale. Use Delivery for an ordinary order, "
                + "with ShortSell to open or cover a short.");
    }

    public static PositionEffect ToCanonicalPositionEffect(bool isShort) =>
        isShort ? PositionEffect.ShortSell : PositionEffect.Delivery;

    // --- order type ------------------------------------------------------------------------------------------------

    public static Result<int> ToNativeOrderType(OrderType type) => type switch
    {
        OrderType.Market => OrderTypeMarket,
        OrderType.Limit => OrderTypeNormal,
        OrderType.Stop => OrderTypeStop,
        OrderType.StopLimit => OrderTypeStopLimit,
        OrderType.MarketIfTouched => OrderTypeMarketIfTouched,
        OrderType.TrailingStop => Result<int>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "moomoo trails by a ratio or an amount (trailType and trailValue), and the shared order "
            + "contract carries neither.")),
        _ => Result<int>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>
    /// OpenD's order type, read back from the book, to the canonical one.
    ///
    /// The order book returns every type the account has used, including ones this connector never
    /// SENDS, and refusing to read those would hide live orders from the blotter. So several native
    /// types fold onto their nearest canonical shape — on the read side only:
    ///
    ///  * Hong Kong's absolute, special and auction-limit orders all carry a limit price: Limit.
    ///  * An at-auction order and the TWAP/VWAP market algos fill at the market: Market.
    ///  * Limit-if-touched carries a trigger AND a limit, which is StopLimit's shape. Its trigger
    ///    fires in the opposite direction, which is exactly why it is never sent as one.
    ///  * Both trailing variants: TrailingStop.
    /// </summary>
    public static Result<OrderType> ToCanonicalOrderType(int type) => type switch
    {
        OrderTypeNormal or OrderTypeAbsoluteLimit or OrderTypeAuctionLimit or OrderTypeSpecialLimit
            or OrderTypeSpecialLimitAll or OrderTypeTwapLimit or OrderTypeVwapLimit => OrderType.Limit,
        OrderTypeMarket or OrderTypeAuction or OrderTypeTwapMarket or OrderTypeVwapMarket => OrderType.Market,
        OrderTypeStop => OrderType.Stop,
        OrderTypeStopLimit or OrderTypeLimitIfTouched => OrderType.StopLimit,
        OrderTypeMarketIfTouched => OrderType.MarketIfTouched,
        OrderTypeTrailingStop or OrderTypeTrailingStopLimit => OrderType.TrailingStop,
        _ => Result<OrderType>.Failure(Unrecognised("order type", Text(type))),
    };

    /// <summary>Whether OpenD's type carries a limit price in <c>price</c>.</summary>
    public static bool UsesLimitPrice(OrderType type) => type is OrderType.Limit or OrderType.StopLimit;

    /// <summary>Whether OpenD's type carries a trigger in <c>auxPrice</c>.</summary>
    public static bool UsesTriggerPrice(OrderType type) =>
        type is OrderType.Stop or OrderType.StopLimit or OrderType.MarketIfTouched;

    // --- time in force ---------------------------------------------------------------------------------------------

    public static Result<int> ToNativeTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => TimeInForceDay,
        TimeInForce.Gtc => TimeInForceGtc,
        _ => Result<int>.Failure(Unsupported(
            "time in force",
            tif.ToString(),
            "OpenD accepts DAY and GTC only. Its GTC lapses after ninety calendar days.")),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(int? tif) => tif switch
    {
        null or TimeInForceDay => TimeInForce.Day,
        TimeInForceGtc => TimeInForce.Gtc,
        _ => Result<TimeInForce>.Failure(Unrecognised("time in force", Text(tif.Value))),
    };

    // --- order status ------------------------------------------------------------------------------------------------

    /// <summary>
    /// OpenD's status vocabulary, collapsed onto the canonical lifecycle.
    ///
    /// Three mappings are deliberate rather than obvious:
    ///
    ///  * <c>TimeOut</c> is documented as "processing timed out, result unknown". It is Unknown, not
    ///    Rejected: the order may well be live, and calling it rejected invites a second order.
    ///  * <c>Cancelling_Part</c> and <c>Cancelling_All</c> are cancels in flight against orders that can
    ///    still fill, so they stay working — PartiallyFilled and Open respectively.
    ///  * <c>Disabled</c> is a Hong Kong order the user switched off; it can be switched back on. No
    ///    terminal status is honest for that, so it is Unknown — non-working and non-terminal, which the
    ///    risk gate refuses to act on and the UI flags for attention.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(int status) => status switch
    {
        StatusUnsubmitted or StatusWaitingSubmit or StatusSubmitting => OrderStatus.Submitted,
        StatusSubmitted or StatusCancellingAll => OrderStatus.Open,
        StatusFilledPart or StatusCancellingPart => OrderStatus.PartiallyFilled,
        StatusFilledAll => OrderStatus.Filled,
        StatusCancelledPart or StatusCancelledAll or StatusDeleted => OrderStatus.Cancelled,
        StatusSubmitFailed or StatusFailed or StatusFillCancelled => OrderStatus.Rejected,
        StatusTimeOut or StatusUnknown or StatusDisabled => OrderStatus.Unknown,
        _ => Result<OrderStatus>.Failure(Unrecognised("order status", Text(status))),
    };

    /// <summary>
    /// The order-book variant: an unmapped status degrades to <see cref="OrderStatus.Unknown"/> with the
    /// raw value returned for <see cref="BrokerOrder.StatusMessage"/>, so one new status cannot blank the
    /// blotter. Single-order reads still use the strict overload.
    /// </summary>
    public static OrderStatus ToCanonicalOrderStatusOrUnknown(int status, out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status);
        rawStatus = mapped.IsSuccess ? null : $"OpenD order status {Text(status)}";
        return mapped.IsSuccess ? mapped.Value : OrderStatus.Unknown;
    }

    // --- candles ------------------------------------------------------------------------------------------------------

    public static Result<int> ToNativeKlType(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => 1,
        TimeFrame.ThreeMinutes => 10,
        TimeFrame.FiveMinutes => 6,
        TimeFrame.FifteenMinutes => 7,
        TimeFrame.ThirtyMinutes => 8,
        TimeFrame.OneHour => 9,
        TimeFrame.OneDay => 2,
        TimeFrame.OneWeek => 3,
        TimeFrame.OneMonth => 4,
        _ => Result<int>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    // --- streaming ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Subscription types per canonical stream mode. OpenD's Basic quote has no bid or ask, so Ltp and
    /// Quote are the same subscription; Full adds the order book, which costs a second quota unit per
    /// security.
    /// </summary>
    public static IReadOnlyList<int> ToNativeSubTypes(StreamMode mode) => mode == StreamMode.Full
        ? [SubTypeBasic, SubTypeOrderBook]
        : [SubTypeBasic];

    // --- currency and option right ------------------------------------------------------------------------------------

    /// <summary>Trd_Common.Currency. Only the two in-scope currencies are accepted; see the manifest.</summary>
    public static Result<Currency> ToCanonicalCurrency(int currency) => currency switch
    {
        1 => Currency.Hkd,
        2 => Currency.Usd,
        _ => Result<Currency>.Failure(Unsupported(
            "currency",
            Text(currency),
            "This connector reports USD and HKD only, the currencies of the venues it trades.")),
    };

    public static Result<OptionRight> ToCanonicalOptionRight(int? optionType) => optionType switch
    {
        OptionTypeCall => OptionRight.Call,
        OptionTypePut => OptionRight.Put,
        _ => Result<OptionRight>.Failure(Unrecognised("option type", optionType is { } t ? Text(t) : "(none)")),
    };

    // --- helpers ---------------------------------------------------------------------------------------------------------

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"moomoo does not support the {what} '{value}' through this connector."
            : $"moomoo does not support the {what} '{value}' through this connector. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["value"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"OpenD returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}
