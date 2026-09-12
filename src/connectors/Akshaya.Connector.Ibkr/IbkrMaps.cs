using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>One history request's bar width, the period one page may cover, and both as spans for walking a window.</summary>
internal sealed record IbkrBar(string Bar, string Period, TimeSpan PageSpan, TimeSpan Length);

/// <summary>
/// THE mapping table between canonical Akshaya vocabulary and the Client Portal API's. Every IBKR literal lives
/// here, and every unmapped value is a failure rather than a default.
/// </summary>
internal static class IbkrMaps
{
    public const string SecTypeStock = "STK";
    public const string SecTypeOption = "OPT";

    public const string SideBuy = "BUY";
    public const string SideSell = "SELL";

    public const string OrderTypeMarket = "MKT";
    public const string OrderTypeLimit = "LMT";
    public const string OrderTypeStop = "STP";
    public const string OrderTypeStopLimit = "STOP_LIMIT";

    public const string TimeInForceDay = "DAY";
    public const string TimeInForceGtc = "GTC";
    public const string TimeInForceIoc = "IOC";

    /// <summary>IBKR's smart router, the exchange every option lookup is made against.</summary>
    public const string SmartRouting = "SMART";

    /// <summary>
    /// Snapshot fields: 31 last, 55 symbol, 70 high, 71 low, 84 bid, 85 ask size, 86 ask, 87 volume, 88 bid size,
    /// 7059 last size, 7295 open, 7741 prior close, 7638 option open interest, 6509 data availability, 7762 volume
    /// (high precision).
    /// </summary>
    public const string SnapshotFields = "31,55,70,71,84,85,86,87,88,7059,7295,7741,7638,6509,7762";

    public static readonly IReadOnlyList<string> StreamFields = ["31", "70", "71", "84", "86", "87", "7059", "7295", "7741", "7638", "7762"];

    /// <summary>NYSE American, NYSE Arca and Cboe BZX, which SharedKernel has no handle for.</summary>
    public static readonly Venue NyseAmerican = new("XASE");

    public static readonly Venue NyseArca = new("ARCX");

    public static readonly Venue CboeBzx = new("BATS");

    // --- markets and venues ---------------------------------------------------------------------------

    public static Result<IbkrMarket> MarketForVenue(Venue venue) => venue.Mic switch
    {
        "XNYS" or "XNAS" or "XASE" or "ARCX" or "BATS" => IbkrMarket.Us,
        "XHKG" => IbkrMarket.Hk,
        "XSES" => IbkrMarket.Sg,
        _ => Result<IbkrMarket>.Failure(Unsupported(
            "venue",
            venue.Mic,
            "This connector trades the US listing venues, HKEX and SGX — the venues the platform has calendars for.")),
    };

    /// <summary>The listing exchange as the contract routes name it, to pick a key's contract out of several listings.</summary>
    public static Result<string> ListingExchangeFor(Venue venue) => venue.Mic switch
    {
        "XNAS" => "NASDAQ",
        "XNYS" => "NYSE",
        "XASE" => "AMEX",
        "ARCX" => "ARCA",
        "BATS" => "BATS",
        "XHKG" => "SEHK",
        "XSES" => "SGX",
        _ => Result<string>.Failure(Unsupported("venue", venue.Mic, null)),
    };

    /// <summary>
    /// A listing exchange to a MIC. Order rows add a market tier after a dot (<c>NASDAQ.NMS</c>), which is dropped.
    /// Routing destinations such as <c>SMART</c> or <c>ISLAND</c> are not listing exchanges and are refused.
    /// </summary>
    public static Result<Venue> ToCanonicalVenue(string? listingExchange)
    {
        var code = listingExchange?.Trim().ToUpperInvariant() ?? string.Empty;
        var dot = code.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            code = code[..dot];
        }

        return code switch
        {
            "NASDAQ" => Venue.Nasdaq,
            "NYSE" => Venue.Nyse,
            "AMEX" => NyseAmerican,
            "ARCA" => NyseArca,
            "BATS" => CboeBzx,
            "SEHK" => Venue.Hkex,
            "SGX" => Venue.Sgx,
            _ => Result<Venue>.Failure(Unsupported(
                "listing exchange",
                listingExchange ?? string.Empty,
                "This connector describes securities listed on the US exchanges, HKEX and SGX.")),
        };
    }

    public static Result<AssetClass> ToCanonicalAssetClass(string? secType) => secType?.Trim().ToUpperInvariant() switch
    {
        SecTypeStock => AssetClass.Equity,
        SecTypeOption => AssetClass.Option,
        _ => Result<AssetClass>.Failure(Unsupported(
            "security type",
            secType ?? string.Empty,
            "This connector trades stocks (ETFs are stocks at IBKR) and US equity options.")),
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

    // --- symbols --------------------------------------------------------------------------------------

    /// <summary>
    /// IBKR's ticker to the canonical symbol: a US share class is written with a space (<c>BRK B</c>) where the
    /// canonical key uses a dot, and a Hong Kong code is padded to five digits.
    /// </summary>
    public static string ToCanonicalSymbol(IbkrMarket market, string ticker)
    {
        var upper = ticker.Trim().ToUpperInvariant();

        if (market.Is(IbkrMarket.Hk) && upper.Length is > 0 and < 5 && upper.All(char.IsAsciiDigit))
        {
            return upper.PadLeft(5, '0');
        }

        return market.Is(IbkrMarket.Us) ? upper.Replace(' ', '.') : upper;
    }

    public static string ToNativeSymbol(IbkrMarket market, string symbol)
    {
        var upper = symbol.Trim().ToUpperInvariant();

        if (market.Is(IbkrMarket.Hk) && upper.Length > 0 && upper.All(char.IsAsciiDigit))
        {
            var trimmed = upper.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }

        return market.Is(IbkrMarket.Us) ? upper.Replace('.', ' ') : upper;
    }

    // --- side and position effect --------------------------------------------------------------------------

    public static string ToNativeSide(Side side) => side == Side.Buy ? SideBuy : SideSell;

    /// <summary>Orders say BUY and SELL; executions and order status say B and S.</summary>
    public static Result<Side> ToCanonicalSide(string? side) => side?.Trim().ToUpperInvariant() switch
    {
        "BUY" or "B" or "BOT" => Side.Buy,
        "SELL" or "S" or "SLD" or "SSHORT" => Side.Sell,
        _ => Result<Side>.Failure(Unrecognised("side", side ?? string.Empty)),
    };

    /// <summary>
    /// IBKR has no product field: margin use is the account's, and a sell beyond the long position on a margin
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
                "IBKR has no product field: margin use is decided by the account, and a sell beyond the long position "
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
            "IBKR trails by an amount or a percentage, and the shared order contract carries neither.")),
        _ => Result<string>.Failure(Unsupported("order type", type.ToString(), null)),
    };

    /// <summary>
    /// An order type read back. Order rows spell it several ways — <c>LMT</c>, <c>Limit</c>, <c>LIMIT</c>,
    /// <c>Stop Limit</c>, <c>STOP_LIMIT</c> — so spaces and underscores are ignored and case does not matter.
    /// </summary>
    public static Result<OrderType> ToCanonicalOrderType(string? type)
    {
        var normalised = (type ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalised switch
        {
            "MKT" or "MARKET" => OrderType.Market,
            "LMT" or "LIMIT" => OrderType.Limit,
            "STP" or "STOP" => OrderType.Stop,
            "STOPLIMIT" or "STPLMT" => OrderType.StopLimit,
            "MIT" or "MARKETIFTOUCHED" => OrderType.MarketIfTouched,
            "TRAIL" or "TRAILINGSTOP" or "TRAILLMT" or "TRAILINGSTOPLIMIT" => OrderType.TrailingStop,
            _ => Result<OrderType>.Failure(Unrecognised("order type", type ?? string.Empty)),
        };
    }

    public static bool UsesLimitPrice(OrderType type) => type is OrderType.Limit or OrderType.StopLimit;

    public static bool UsesTriggerPrice(OrderType type) => type is OrderType.Stop or OrderType.StopLimit;

    // --- time in force -----------------------------------------------------------------------------------------------

    public static Result<string> ToNativeTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => TimeInForceDay,
        TimeInForce.Gtc => TimeInForceGtc,
        TimeInForce.Ioc => TimeInForceIoc,
        _ => Result<string>.Failure(Unsupported("time in force", tif.ToString(), "This connector sends Day, GTC and IOC.")),
    };

    public static Result<TimeInForce> ToCanonicalTimeInForce(string? tif) => tif?.Trim().ToUpperInvariant() switch
    {
        "DAY" => TimeInForce.Day,
        "GTC" => TimeInForce.Gtc,
        "IOC" => TimeInForce.Ioc,
        "OPG" => TimeInForce.AtTheOpen,
        _ => Result<TimeInForce>.Failure(Unrecognised("time in force", tif ?? string.Empty)),
    };

    // --- order status --------------------------------------------------------------------------------------------------

    /// <summary>
    /// IBKR's order statuses, collapsed onto the canonical lifecycle.
    ///
    ///  * <c>PendingSubmit</c> and <c>ApiPending</c> have not reached the order destination: Submitted.
    ///  * <c>PreSubmitted</c> is accepted and held by IBKR — a simulated stop waiting for its trigger, an order
    ///    waiting for the open — and <c>Submitted</c> is working at the destination. Both are Open.
    ///  * <c>PendingCancel</c> can still fill: Open.
    ///  * <c>Inactive</c> covers orders rejected, held for a margin or permission problem, or parked by the
    ///    exchange, and nothing in the status says which. It is Unknown, with IBKR's words kept, because calling it
    ///    Rejected would hide an order that may come back to life.
    /// </summary>
    public static Result<OrderStatus> ToCanonicalOrderStatus(string? status) =>
        (status ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "PENDINGSUBMIT" or "APIPENDING" => OrderStatus.Submitted,
            "PRESUBMITTED" or "SUBMITTED" or "PENDINGCANCEL" or "WARNSTATE" => OrderStatus.Open,
            "FILLED" => OrderStatus.Filled,
            "CANCELLED" or "CANCELED" or "APICANCELLED" => OrderStatus.Cancelled,
            "INACTIVE" => OrderStatus.Unknown,
            _ => Result<OrderStatus>.Failure(Unrecognised("order status", status ?? string.Empty)),
        };

    public static OrderStatus ToCanonicalOrderStatusOrUnknown(string? status, out string? rawStatus)
    {
        var mapped = ToCanonicalOrderStatus(status);
        rawStatus = mapped.IsSuccess && mapped.Value != OrderStatus.Unknown ? null : status;
        return mapped.IsSuccess ? mapped.Value : OrderStatus.Unknown;
    }

    // --- history --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Bar width and a page's period. A page is kept to roughly a thousand regular-session bars or fewer, the most
    /// the history route returns in one answer.
    /// </summary>
    public static Result<IbkrBar> ToNativeBar(TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => new IbkrBar("1min", "1d", TimeSpan.FromDays(1), TimeSpan.FromMinutes(1)),
        TimeFrame.ThreeMinutes => new IbkrBar("3min", "2d", TimeSpan.FromDays(2), TimeSpan.FromMinutes(3)),
        TimeFrame.FiveMinutes => new IbkrBar("5min", "1w", TimeSpan.FromDays(7), TimeSpan.FromMinutes(5)),
        TimeFrame.FifteenMinutes => new IbkrBar("15min", "2w", TimeSpan.FromDays(14), TimeSpan.FromMinutes(15)),
        TimeFrame.ThirtyMinutes => new IbkrBar("30min", "1m", TimeSpan.FromDays(30), TimeSpan.FromMinutes(30)),
        TimeFrame.OneHour => new IbkrBar("1h", "1m", TimeSpan.FromDays(30), TimeSpan.FromHours(1)),
        TimeFrame.OneDay => new IbkrBar("1d", "1y", TimeSpan.FromDays(365), TimeSpan.FromDays(1)),
        TimeFrame.OneWeek => new IbkrBar("1w", "5y", TimeSpan.FromDays(365 * 5), TimeSpan.FromDays(7)),
        TimeFrame.OneMonth => new IbkrBar("1m", "10y", TimeSpan.FromDays(365 * 10), TimeSpan.FromDays(28)),
        _ => Result<IbkrBar>.Failure(Unsupported("chart interval", frame.ToString(), null)),
    };

    // --- helpers ------------------------------------------------------------------------------------------------------------

    private static Error Unsupported(string what, string value, string? detail) => new(
        ConnectorErrorCodes.NotSupported,
        detail is null
            ? $"IBKR does not support the {what} '{value}' through this connector."
            : $"IBKR does not support the {what} '{value}' through this connector. {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["value"] = value,
        });

    private static Error Unrecognised(string what, string value) => new(
        ConnectorErrorCodes.Unknown,
        $"IBKR returned a {what} this connector does not recognise: '{value}'. "
        + "This is a vendor vocabulary change; the mapping table needs updating.",
        VendorCode: value,
        VendorMessage: value,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["field"] = what,
            ["vendorValue"] = value,
        });
}
