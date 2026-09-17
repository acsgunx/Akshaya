using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and Tiger's. Every Tiger literal lives here, and every
/// unmapped value is a failure rather than a default.
/// </summary>
internal static class TigerMaps
{
    // --- methods ------------------------------------------------------------------------------------

    public const string MethodPlaceOrder = "place_order";
    public const string MethodModifyOrder = "modify_order";
    public const string MethodCancelOrder = "cancel_order";
    public const string MethodPreviewOrder = "preview_order";
    public const string MethodOrders = "orders";
    public const string MethodActiveOrders = "active_orders";
    public const string MethodFilledOrders = "filled_orders";
    public const string MethodOrderTransactions = "order_transactions";
    public const string MethodPositions = "positions";
    public const string MethodPrimeAssets = "prime_assets";
    public const string MethodContracts = "contracts";
    public const string MethodContract = "contract";
    public const string MethodQuoteRealTime = "quote_real_time";
    public const string MethodKline = "kline";
    public const string MethodQuoteDepth = "quote_depth";
    public const string MethodOptionChain = "option_chain";
    public const string MethodOptionBrief = "option_brief";
    public const string MethodOptionExpiration = "option_expiration";

    // --- order vocabulary ---------------------------------------------------------------------------

    public const string SecurityTypeStock = "STK";
    public const string SecurityTypeOption = "OPT";

    public const string ActionBuy = "BUY";
    public const string ActionSell = "SELL";

    public const string OrderTypeMarket = "MKT";
    public const string OrderTypeLimit = "LMT";
    public const string OrderTypeStop = "STP";
    public const string OrderTypeStopLimit = "STP_LMT";

    public const string TimeInForceDay = "DAY";
    public const string TimeInForceGtc = "GTC";
    public const string TimeInForceGtd = "GTD";

    /// <summary>No price adjustment: bars of prices that actually traded.</summary>
    public const string QuoteRightNone = "nr";

    public const string EnvironmentReal = "real";
    public const string EnvironmentPaper = "paper";

    /// <summary>NYSE American, NYSE Arca and Cboe BZX, which SharedKernel has no handle for.</summary>
    public static readonly Venue NyseAmerican = new("XASE");

    public static readonly Venue NyseArca = new("ARCX");

    public static readonly Venue CboeBzx = new("BATS");

    // --- markets and venues ---------------------------------------------------------------------------

    public static Result<TigerMarket> MarketForVenue(Venue venue) => venue.Mic switch
    {
        "XNYS" or "XNAS" or "XASE" or "ARCX" or "BATS" => TigerMarket.Us,
        "XHKG" => TigerMarket.Hk,
        "XSES" => TigerMarket.Sg,
        _ => Result<TigerMarket>.Failure(Unsupported(
            "venue",
            venue.Mic,
            "This connector reaches the US listing venues, HKEX and SGX — the venues the platform has calendars for.")),
    };

    public static Result<TigerMarket> MarketForCode(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "US" => TigerMarket.Us,
        "HK" => TigerMarket.Hk,
        "SG" => TigerMarket.Sg,
        _ => Result<TigerMarket>.Failure(Unsupported("market", code ?? string.Empty, null)),
    };

    /// <summary>
    /// A contract to its canonical venue. Hong Kong and Singapore have one listing venue each; a US contract needs
    /// its primary exchange, and a routing destination or an unknown exchange is a failure, never a guess.
    /// </summary>
    public static Result<Venue> ToCanonicalVenue(TigerMarket market, string? primaryExchange, string? exchange)
    {
        if (market.Is(TigerMarket.Hk))
        {
            return Venue.Hkex;
        }

        if (market.Is(TigerMarket.Sg))
        {
            return Venue.Sgx;
        }

        var code = (primaryExchange ?? exchange)?.Trim().ToUpperInvariant() ?? string.Empty;

        return code switch
        {
            "NASDAQ" or "NSDQ" => Venue.Nasdaq,
            "NYSE" => Venue.Nyse,
            "AMEX" or "ASE" => NyseAmerican,
            "ARCA" => NyseArca,
            "BATS" or "BZX" => CboeBzx,
            _ => Result<Venue>.Failure(Unsupported(
                "primary exchange",
                code,
                "This connector describes securities listed on the US exchanges, HKEX and SGX.")),
        };
    }

    public static Result<AssetClass> ToCanonicalAssetClass(string? secType) => secType?.Trim().ToUpperInvariant() switch
    {
        SecurityTypeStock => AssetClass.Equity,
        SecurityTypeOption => AssetClass.Option,
        _ => Result<AssetClass>.Failure(Unsupported(
            "security type",
            secType ?? string.Empty,
            "This connector trades stocks (an ETF is a stock at Tiger) and options.")),
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

    public static string ToNativeSide(Side side) => side == Side.Buy ? ActionBuy : ActionSell;

    public static Result<Side> ToCanonicalSide(string? action) => action?.Trim().ToUpperInvariant() switch
    {
        ActionBuy or "B" => Side.Buy,
        ActionSell or "S" or "SELL_SHORT" => Side.Sell,
        _ => Result<Side>.Failure(Unrecognised("action", action ?? string.Empty)),
    };

    /// <summary>
    /// Tiger has no product field: margin use is the account's, and a sell beyond the long position on a margin
    /// account is a short. So an order is Delivery, with ShortSell to say so.
    /// </summary>
    public static Result ValidatePositionEffect(PositionEffect effect)
    {
        var product = effect & ~PositionEffect.ShortSell;

        return product is PositionEffect.None or PositionEffect.Delivery && effect != PositionEffect.None
            ? Result.Success()
            : Unsupported(
                "product",
                effect.ToString(),
                "Tiger has no product field: margin use is decided by the account, and a sell beyond the long position "
                + "on a margin account is a short sale. Use Delivery, with ShortSell to open or cover a short.");
    }

    // --- order type ----------------------------------------------------------------------------------------------

    public static Result<string> ToNativeOrderType(OrderType type) => type switch
    {
        OrderType.Market => OrderTypeMarket,
        OrderType.Limit => OrderTypeLimit,
        OrderType.Stop => OrderTypeStop,
        OrderType.StopLimit => OrderTypeStopLimit,
        OrderType.TrailingStop => Result<string>.Failure(Unsupported(
            "order type",
            type.ToString(),
            "Tiger trails by an amount or a percentage, and the shared order contract carries neither.")),
        _ => Result<string>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    public static Result<OrderType> ToCanonicalOrderType(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        OrderTypeMarket or "MARKET" => OrderType.Market,
        OrderTypeLimit or "LIMIT" => OrderType.Limit,
        OrderTypeStop or "STOP" => OrderType.Stop,
        OrderTypeStopLimit or "STPLMT" or "STOP_LIMIT" => OrderType.StopLimit,
        "TRAIL" or "TRAILING_STOP" => OrderType.TrailingStop,
        _ => Result<OrderType>.Failure(Unrecognised("order type", type ?? string.Empty)),
    };

    /// <summary>A limit price belongs to LMT and STP_LMT.</summary>
    public static bool UsesLimitPrice(OrderType type) => type is OrderType.Limit or OrderType.StopLimit;

    /// <summary>Tiger carries a stop in <c>aux_price</c>, for both STP and STP_LMT.</summary>
    public static bool UsesTriggerPrice(OrderType type) => type is OrderType.Stop or OrderType.StopLimit;

    // --- time in force -----------------------------------------------------------------------------------------------

    public static Result<string> ToNativeTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => TimeInForceDay,
        TimeInForce.Gtc => TimeInForceGtc,
        TimeInForce.Gtd => TimeInForceGtd,
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), "Tiger accepts Day, GTC and GTD.")),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(string? tif) => tif?.Trim().ToUpperInvariant() switch
    {
        null or "" or TimeInForceDay => TimeInForce.Day,
        TimeInForceGtc => TimeInForce.Gtc,
        TimeInForceGtd => TimeInForce.Gtd,
        _ => Result<TimeInForce>.Failure(Unrecognised("time in force", tif ?? string.Empty)),
    };

    // --- order status --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Tiger's order statuses, which arrive as a name or as the number behind it:
    /// Invalid(-2), Initial(-1), PendingCancel(3), Cancelled(4), Submitted(5), Filled(6), Inactive(7), PendingSubmit(8).
    ///
    ///  * <c>Initial</c> and <c>PendingSubmit</c> have not reached the venue: Submitted.
    ///  * <c>Submitted</c> is working, and <c>PendingCancel</c> can still fill: Open.
    ///  * <c>Inactive</c> is Tiger's word for an order the venue or risk checks refused: Rejected.
    ///  * <c>Invalid</c> is an order that never became valid: Expired.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(string? status) => status?.Trim() switch
    {
        "-1" or "Initial" or "NEW" or "New" or "8" or "PendingSubmit" or "PENDING_NEW" or "PendingNew" => OrderStatus.Submitted,
        "2" or "5" or "Submitted" or "HELD" or "Held" or "3" or "PendingCancel" or "PENDING_CANCEL" => OrderStatus.Open,
        "PartiallyFilled" or "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "6" or "Filled" or "FILLED" => OrderStatus.Filled,
        "4" or "Cancelled" or "CANCELLED" or "Canceled" => OrderStatus.Cancelled,
        "7" or "Inactive" or "REJECTED" or "Rejected" => OrderStatus.Rejected,
        "-2" or "Invalid" or "EXPIRED" or "Expired" => OrderStatus.Expired,
        _ => Result<OrderStatus>.Failure(Unrecognised("order status", status ?? string.Empty)),
    };

    public static OrderStatus ToCanonicalOrderStatusOrUnknown(string? status, out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status);
        rawStatus = mapped.IsSuccess ? null : status;
        return mapped.IsSuccess ? mapped.Value : OrderStatus.Unknown;
    }

    // --- history --------------------------------------------------------------------------------------------------------

    public static Result<string> ToNativePeriod(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => "1min",
        TimeFrame.ThreeMinutes => "3min",
        TimeFrame.FiveMinutes => "5min",
        TimeFrame.FifteenMinutes => "15min",
        TimeFrame.ThirtyMinutes => "30min",
        TimeFrame.OneHour => "60min",
        TimeFrame.OneDay => "day",
        TimeFrame.OneWeek => "week",
        TimeFrame.OneMonth => "month",
        _ => Result<string>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    // --- helpers ------------------------------------------------------------------------------------------------------------

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"Tiger does not support the {what} '{value}' through this connector."
            : $"Tiger does not support the {what} '{value}' through this connector. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["value"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"Tiger returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}
