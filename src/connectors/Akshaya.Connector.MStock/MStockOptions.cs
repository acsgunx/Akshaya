namespace Akshaya.Connector.MStock;

/// <summary>
/// Everything about the mStock endpoint that an operator might legitimately need to change
/// without a redeploy: base addresses, timeouts, and the individual route templates.
///
/// The routes are configuration rather than constants on purpose. mStock has moved paths
/// under <c>/openapi/typea</c> more than once, and when a vendor renames a route at 09:10 on
/// a trading morning the fix must be a config push, not a release.
/// </summary>
public sealed record MStockOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:MStock</c>.</summary>
    public const string SectionName = "Connectors:MStock";

    /// <summary>REST base address. Every route below is relative to this.</summary>
    public Uri BaseUrl { get; init; } = new("https://api.mstock.trade");

    /// <summary>Streaming base address for <see cref="MStockStream"/>.</summary>
    public Uri StreamUrl { get; init; } = new("wss://ws.mstock.trade");

    /// <summary>
    /// mStock pins its API surface with a version header rather than a path segment. It is
    /// sent on every call, including the unauthenticated login call.
    /// </summary>
    public string ApiVersion { get; init; } = "1";

    /// <summary>Timeout for ordinary REST calls.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Timeout for the script master download. It is a multi-hundred-thousand-row CSV and
    /// routinely takes far longer than any other call; sharing the ordinary timeout would
    /// make the nightly instrument ingest fail every night.
    /// </summary>
    public TimeSpan ScriptMasterTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Nominal token lifetime published by mStock. The effective expiry is the earlier of
    /// this and the next midnight in <see cref="VenueTimeZoneId"/>; see <see cref="MStockAuth"/>.
    /// </summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>IANA id for the venue whose midnight kills the token.</summary>
    public string VenueTimeZoneId { get; init; } = "Asia/Kolkata";

    // --- routes -------------------------------------------------------------------------

    public string LoginPath { get; init; } = "/openapi/typea/connect/login";

    public string SessionTokenPath { get; init; } = "/openapi/typea/session/token";

    public string VerifyTotpPath { get; init; } = "/openapi/typea/session/verifytotp";

    public string LogoutPath { get; init; } = "/openapi/typea/logout";

    /// <summary>Placement route; <c>{0}</c> is the variety (<c>reg</c> or <c>amo</c>).</summary>
    public string PlaceOrderPathFormat { get; init; } = "/openapi/typea/orders/{0}";

    /// <summary>Modify route; <c>{0}</c> is the broker order id.</summary>
    public string ModifyOrderPathFormat { get; init; } = "/openapi/typea/orders/regular/{0}";

    /// <summary>Cancel route; <c>{0}</c> is the broker order id.</summary>
    public string CancelOrderPathFormat { get; init; } = "/openapi/typea/orders/regular/{0}";

    public string CancelAllPath { get; init; } = "/openapi/typea/orders/cancelall";

    public string OrderBookPath { get; init; } = "/openapi/typea/orders";

    public string OrderDetailsPath { get; init; } = "/openapi/typea/order/details";

    public string TradeBookPath { get; init; } = "/openapi/typea/tradebook";

    /// <summary>Alternate trade feed. Used as a fallback when the trade book comes back empty.</summary>
    public string TradesPath { get; init; } = "/openapi/typea/trades";

    public string PositionsPath { get; init; } = "/openapi/typea/portfolio/positions";

    public string HoldingsPath { get; init; } = "/openapi/typea/portfolio/holdings";

    public string FundsPath { get; init; } = "/openapi/typea/user/fundsummary";

    public string ScriptMasterPath { get; init; } = "/openapi/typea/instruments/scriptmaster";

    public string LtpPath { get; init; } = "/openapi/typea/instruments/quote/ltp";

    public string OhlcPath { get; init; } = "/openapi/typea/instruments/quote/ohlc";

    /// <summary>Daily/holding-period chart route; <c>{0}</c> is the instrument token.</summary>
    public string HistoricalChartPathFormat { get; init; } = "/openapi/typea/instruments/historicalchart/{0}";

    /// <summary>Intraday chart route; <c>{0}</c> is the instrument token, <c>{1}</c> the interval.</summary>
    public string IntradayChartPathFormat { get; init; } = "/openapi/typea/instruments/intradaychart/{0}/{1}";

    public string OptionChainPath { get; init; } = "/openapi/typea/instruments/optionchain";

    // --- streaming ----------------------------------------------------------------------

    /// <summary>First reconnect delay. Doubles on each attempt up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Jitter fraction applied to every reconnect delay. Without it, a deploy that drops
    /// every socket at once brings them all back in the same millisecond and mStock throttles
    /// the lot of us.
    /// </summary>
    public double ReconnectJitter { get; init; } = 0.30;

    /// <summary>How long the socket may be silent before we treat it as dead and reconnect.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Zero means reconnect forever, which is what a trading session wants.</summary>
    public int MaxReconnectAttempts { get; init; }

    /// <summary>
    /// Per-socket subscription cap. Zero (the default) means "use mStock's documented limit"
    /// (<see cref="MStockStreamOptionExtensions.DefaultMaxStreamSubscriptions"/>); set it only to
    /// pull the ceiling in for a deployment that fans out across more sockets deliberately.
    /// </summary>
    public int MaxStreamSubscriptions { get; init; }
}
