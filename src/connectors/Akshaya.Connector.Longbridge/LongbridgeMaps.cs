using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and Longbridge's wire vocabulary. Every
/// Longbridge literal lives here, and every unmapped value is a failure rather than a default.
/// </summary>
internal static class LongbridgeMaps
{
    // --- order types ---------------------------------------------------------------------------------

    public const string OrderTypeLimit = "LO";

    /// <summary>HKEX's enhanced limit order: fills at the limit or up to ten price queues better.</summary>
    public const string OrderTypeEnhancedLimit = "ELO";

    public const string OrderTypeMarket = "MO";
    public const string OrderTypeAtAuction = "AO";
    public const string OrderTypeAtAuctionLimit = "ALO";
    public const string OrderTypeOddLot = "ODD";
    public const string OrderTypeLimitIfTouched = "LIT";
    public const string OrderTypeMarketIfTouched = "MIT";
    public const string OrderTypeTrailingLimitAmount = "TSLPAMT";
    public const string OrderTypeTrailingLimitPercent = "TSLPPCT";
    public const string OrderTypeTrailingMarketAmount = "TSMAMT";
    public const string OrderTypeTrailingMarketPercent = "TSMPCT";
    public const string OrderTypeSpecialLimit = "SLO";

    public const string SideBuy = "Buy";
    public const string SideSell = "Sell";

    public const string TimeInForceDay = "Day";
    public const string TimeInForceGtc = "GTC";
    public const string TimeInForceGtd = "GTD";

    public const string EnvironmentReal = "real";
    public const string EnvironmentPaper = "paper";

    /// <summary>SubType on the quote socket.</summary>
    public const int SubTypeQuote = 1;

    public const int SubTypeDepth = 2;

    /// <summary>TradeSessions: regular hours only. Every connector here charts regular sessions.</summary>
    public const int TradeSessionIntraday = 0;

    /// <summary>NYSE American, NYSE Arca and Cboe BZX, which SharedKernel has no handle for.</summary>
    public static readonly Venue NyseAmerican = new("XASE");

    public static readonly Venue NyseArca = new("ARCX");

    public static readonly Venue CboeBzx = new("BATS");

    // --- markets and venues ---------------------------------------------------------------------------

    /// <summary>Every US MIC reaches Longbridge's US market: its symbols name no listing exchange.</summary>
    public static Result<LongbridgeMarket> MarketForVenue(Venue venue) => venue.Mic switch
    {
        "XNYS" or "XNAS" or "XASE" or "ARCX" or "BATS" => LongbridgeMarket.Us,
        "XHKG" => LongbridgeMarket.Hk,
        "XSES" => LongbridgeMarket.Sg,
        _ => Result<LongbridgeMarket>.Failure(Unsupported(
            "venue",
            venue.Mic,
            "This connector reaches the US listing venues, HKEX and SGX. Longbridge also serves China Connect, "
            + "which stays out of scope until the platform has calendars for Shanghai and Shenzhen.")),
    };

    public static Result<LongbridgeMarket> MarketForSuffix(string? suffix) => suffix?.Trim().ToUpperInvariant() switch
    {
        "US" => LongbridgeMarket.Us,
        "HK" => LongbridgeMarket.Hk,
        "SG" => LongbridgeMarket.Sg,
        _ => Result<LongbridgeMarket>.Failure(Unsupported("market", suffix ?? string.Empty, null)),
    };

    /// <summary>
    /// The exchange static information reports, to a MIC. <c>NASD</c> and <c>SEHK</c> appear in
    /// Longbridge's own examples; the others are the exchanges' standard short names, and any value not
    /// listed is a failure, never a guess.
    /// </summary>
    public static Result<Venue> ToCanonicalVenue(string? exchange) => exchange?.Trim().ToUpperInvariant() switch
    {
        "NASD" or "NASDAQ" => Venue.Nasdaq,
        "NYSE" => Venue.Nyse,
        "AMEX" or "NYSEAMERICAN" or "NYSE AMERICAN" => NyseAmerican,
        "ARCA" or "NYSEARCA" or "NYSE ARCA" => NyseArca,
        "BATS" or "CBOEBZX" or "BZX" => CboeBzx,
        "SEHK" or "HKEX" => Venue.Hkex,
        "SGX" or "SES" => Venue.Sgx,
        _ => Result<Venue>.Failure(Unsupported("exchange", exchange ?? string.Empty, null)),
    };

    /// <summary><c>outside_rth</c> for US orders: regular hours only, the sessions the platform's calendars model.</summary>
    public const string OutsideRthRegularOnly = "RTH_ONLY";

    /// <summary>
    /// The board static information reports, to an asset class — the SDK's complete SecurityBoard list.
    ///
    /// Main boards are equities. Longbridge has no ETF board: an ETF lists on its market's main board and
    /// decodes as Equity, consistently, wherever it appears (search, orders, positions). Boards for OTC
    /// names, grey-market IPOs, warrants and China Connect are outside this connector, and a board this
    /// table does not know — including the SDK's own <c>Unknown</c> — is a failure, never a default.
    /// </summary>
    public static Result<AssetClass> ToCanonicalAssetClass(string? board) => board?.Trim() switch
    {
        "USMain" or "HKEquity" or "SGMain" => AssetClass.Equity,
        "USDJI" or "USNSDQ" or "USSector" or "SPXIndex" or "VIXIndex" or "HKHS" or "HKSector" or "STI" or "SGSector" =>
            AssetClass.Index,
        "USOption" or "USOptionS" => AssetClass.Option,
        "USPink" or "HKPreIPO" or "HKWarrant" or "SHMainConnect" or "SHMainNonConnect" or "SHSTAR" or "CNIX" or "CNSector"
            or "SZMainConnect" or "SZMainNonConnect" or "SZGEMConnect" or "SZGEMNonConnect" =>
            Result<AssetClass>.Failure(Unsupported(
                "board",
                board ?? string.Empty,
                "OTC securities, grey-market IPOs, warrants and China Connect are not traded through this connector.")),
        _ => Result<AssetClass>.Failure(Unrecognised("board", board ?? string.Empty)),
    };

    public static Result<Currency> ToCanonicalCurrency(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "USD" => Currency.Usd,
        "HKD" => Currency.Hkd,
        "SGD" => Currency.Sgd,
        _ => Result<Currency>.Failure(Unsupported(
            "currency",
            currency ?? string.Empty,
            "This connector reports USD, HKD and SGD, the currencies of the venues it trades.")),
    };

    // --- side and position effect --------------------------------------------------------------------------

    public static string ToNativeSide(Side side) => side == Side.Buy ? SideBuy : SideSell;

    public static Result<Side> ToCanonicalSide(string? side) => side?.Trim() switch
    {
        SideBuy => Side.Buy,
        SideSell => Side.Sell,
        _ => Result<Side>.Failure(Unrecognised("side", side ?? string.Empty)),
    };

    /// <summary>
    /// Longbridge has no product field: margin use is the account's, and a sell beyond the long position
    /// on a margin account is a short. So an order is Delivery, with ShortSell to say so.
    /// </summary>
    public static Result ValidatePositionEffect(PositionEffect effect)
    {
        var product = effect & ~PositionEffect.ShortSell;

        return product is PositionEffect.None or PositionEffect.Delivery && effect != PositionEffect.None
            ? Result.Success()
            : Unsupported(
                "product",
                effect.ToString(),
                "Longbridge has no product field: margin use is decided by the account, and a sell beyond the long "
                + "position on a margin account is a short sale. Use Delivery, with ShortSell to open or cover a short.");
    }

    // --- order type ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Canonical order type to Longbridge's, per market.
    ///
    /// Longbridge has no plain stop order. Its conditional orders are market-if-touched and
    /// limit-if-touched, and both trigger when the price TOUCHES the trigger from either side — which is
    /// exactly what a stop is when the trigger sits beyond the market. Longbridge's own attached stop-loss
    /// orders are placed as these types. So Stop is MIT and StopLimit is LIT, and the canonical
    /// MarketIfTouched is not declared: reading an MIT back could not say which of the two was meant.
    ///
    /// A Hong Kong limit is the ENHANCED limit order, which fills at the limit or better like a limit
    /// everywhere else; HKEX's plain "LO" fills only AT the price. Singapore orders are limit and market
    /// only, the types Longbridge documents for it.
    /// </summary>
    public static Result<string> ToNativeOrderType(OrderType type, LongbridgeMarket market) => (type, market.Suffix) switch
    {
        (OrderType.Market, _) => OrderTypeMarket,
        (OrderType.Limit, "HK") => OrderTypeEnhancedLimit,
        (OrderType.Limit, _) => OrderTypeLimit,
        (OrderType.Stop, "US" or "HK") => OrderTypeMarketIfTouched,
        (OrderType.StopLimit, "US" or "HK") => OrderTypeLimitIfTouched,
        (OrderType.Stop or OrderType.StopLimit, _) => Result<string>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "Longbridge's conditional orders are available for US and Hong Kong securities only.")),
        (OrderType.TrailingStop, _) => Result<string>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "Longbridge trails by an amount or a percentage, and the shared order contract carries neither.")),
        _ => Result<string>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>
    /// Longbridge's order type, read back, to the canonical one. The book returns types this connector
    /// never sends — auction, odd-lot and special limit orders from the app — so they fold onto their
    /// nearest canonical shape here, on the read side only.
    /// </summary>
    public static Result<OrderType> ToCanonicalOrderType(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        OrderTypeLimit or OrderTypeEnhancedLimit or OrderTypeAtAuctionLimit or OrderTypeOddLot or OrderTypeSpecialLimit =>
            OrderType.Limit,
        OrderTypeMarket or OrderTypeAtAuction => OrderType.Market,
        OrderTypeMarketIfTouched => OrderType.Stop,
        OrderTypeLimitIfTouched => OrderType.StopLimit,
        OrderTypeTrailingLimitAmount or OrderTypeTrailingLimitPercent or OrderTypeTrailingMarketAmount
            or OrderTypeTrailingMarketPercent => OrderType.TrailingStop,
        _ => Result<OrderType>.Failure(Unrecognised("order type", type ?? string.Empty)),
    };

    public static bool UsesLimitPrice(OrderType type) => type is OrderType.Limit or OrderType.StopLimit;

    public static bool UsesTriggerPrice(OrderType type) => type is OrderType.Stop or OrderType.StopLimit;

    // --- time in force -----------------------------------------------------------------------------------------------

    public static Result<string> ToNativeTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => TimeInForceDay,
        TimeInForce.Gtc => TimeInForceGtc,
        TimeInForce.Gtd => TimeInForceGtd,
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), "Longbridge accepts Day, GTC and GTD.")),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(string? tif) => tif?.Trim().ToUpperInvariant() switch
    {
        null or "" or "DAY" => TimeInForce.Day,
        TimeInForceGtc => TimeInForce.Gtc,
        TimeInForceGtd => TimeInForce.Gtd,
        _ => Result<TimeInForce>.Failure(Unrecognised("time in force", tif ?? string.Empty)),
    };

    // --- order status --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Longbridge's status vocabulary, collapsed onto the canonical lifecycle.
    ///
    ///  * The three <c>*NotReported</c> states mean Longbridge holds the order but has not sent it to the
    ///    exchange: Submitted. <c>VarietiesNotReported</c> is the exception — a conditional order genuinely
    ///    resting at the broker waiting for its trigger, the equivalent of a triggered-pending stop, so it is
    ///    Open. Calling it Submitted would make a working stop look as if it had not arrived.
    ///  * Replace and cancel requests in flight (<c>WaitToReplace</c>, <c>PendingReplaceStatus</c>,
    ///    <c>WaitToCancel</c>, <c>PendingCancelStatus</c>) sit on orders that can still fill: Open.
    ///  * <c>PartialWithdrawal</c> is a partly filled order whose remainder was withdrawn: Cancelled, with a
    ///    non-zero filled quantity.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(string? status) => status?.Trim() switch
    {
        "NotReported" or "ReplacedNotReported" or "ProtectedNotReported" or "WaitToNew" => OrderStatus.Submitted,
        "VarietiesNotReported" or "NewStatus" or "WaitToReplace" or "PendingReplaceStatus" or "ReplacedStatus"
            or "WaitToCancel" or "PendingCancelStatus" => OrderStatus.Open,
        "PartialFilledStatus" => OrderStatus.PartiallyFilled,
        "FilledStatus" => OrderStatus.Filled,
        "RejectedStatus" => OrderStatus.Rejected,
        "CanceledStatus" or "PartialWithdrawal" => OrderStatus.Cancelled,
        "ExpiredStatus" => OrderStatus.Expired,
        _ => Result<OrderStatus>.Failure(Unrecognised("order status", status ?? string.Empty)),
    };

    public static OrderStatus ToCanonicalOrderStatusOrUnknown(string? status, out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status);
        rawStatus = mapped.IsSuccess ? null : status;
        return mapped.IsSuccess ? mapped.Value : OrderStatus.Unknown;
    }

    // --- candles and streaming -------------------------------------------------------------------------------------------

    /// <summary>Period on the quote protocol: minutes for intraday bars, 1000/2000/3000 for day, week, month.</summary>
    public static Result<int> ToNativePeriod(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => 1,
        TimeFrame.ThreeMinutes => 3,
        TimeFrame.FiveMinutes => 5,
        TimeFrame.FifteenMinutes => 15,
        TimeFrame.ThirtyMinutes => 30,
        TimeFrame.OneHour => 60,
        TimeFrame.OneDay => 1000,
        TimeFrame.OneWeek => 2000,
        TimeFrame.OneMonth => 3000,
        _ => Result<int>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    /// <summary>A quote subscription for Ltp and Quote; Full adds the book.</summary>
    public static IReadOnlyList<int> ToNativeSubTypes(StreamMode mode) =>
        mode == StreamMode.Full ? [SubTypeQuote, SubTypeDepth] : [SubTypeQuote];

    // --- helpers ------------------------------------------------------------------------------------------------------------

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"Longbridge does not support the {what} '{value}' through this connector."
            : $"Longbridge does not support the {what} '{value}' through this connector. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["value"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"Longbridge returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}
