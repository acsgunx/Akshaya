namespace Akshaya.Connector.Fyers;

/// <summary>
/// Everything about the FYERS endpoint that an operator might legitimately need to change
/// without a redeploy: base addresses, timeouts, and the individual route templates.
///
/// The routes are configuration rather than constants for the same reason they are in every
/// other connector here: when a vendor renames a route at 09:10 on a trading morning the fix
/// must be a config push, not a release. FYERS has form — the v2 to v3 migration moved the
/// history, quote and depth routes off <c>api.fyers.in</c> onto <c>api-t1.fyers.in</c> and
/// left the margin calculator behind on the old host.
/// </summary>
public sealed record FyersOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:Fyers</c>.</summary>
    public const string SectionName = "Connectors:Fyers";

    /// <summary>
    /// REST base address for both the trading (<c>/api/v3</c>) and data (<c>/data</c>)
    /// surfaces. They share a host in v3; they did not in v2, which is why every route below
    /// carries its full path rather than assuming a common prefix.
    /// </summary>
    public Uri BaseUrl { get; init; } = new("https://api-t1.fyers.in");

    /// <summary>
    /// Where the symbol master CSVs live. A different host from the API and deliberately
    /// unauthenticated — the files are public, and fetching them must keep working before a
    /// user has linked an account.
    /// </summary>
    public Uri SymbolMasterUrl { get; init; } = new("https://public.fyers.in/sym_details/");

    /// <summary>Timeout for ordinary REST calls.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Timeout for a symbol-master download. The NSE F&amp;O file alone is tens of thousands
    /// of rows and several megabytes; sharing the ordinary timeout would make the nightly
    /// instrument ingest fail every night.
    /// </summary>
    public TimeSpan SymbolMasterTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fallback token lifetime, used only when the access token carries no readable expiry.
    /// The real expiry comes from the token itself; see <see cref="FyersAuth.ComputeExpiry"/>.
    /// </summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>IANA id for the venue whose midnight kills the token.</summary>
    public string VenueTimeZoneId { get; init; } = "Asia/Kolkata";

    // --- authentication -----------------------------------------------------------------

    /// <summary>Where the user is sent to sign in. Answers with an auth_code on the redirect.</summary>
    public string AuthorizePath { get; init; } = "/api/v3/generate-authcode";

    /// <summary>Exchanges the auth_code for an access token.</summary>
    public string TokenPath { get; init; } = "/api/v3/validate-authcode";

    /// <summary>
    /// Exchanges a refresh token for a new access token. Present so the route is not lost, but
    /// this connector does not use it — see <see cref="FyersAuth.RefreshAsync"/> and the
    /// manifest's <c>refreshSupported: false</c>.
    /// </summary>
    public string RefreshPath { get; init; } = "/api/v3/validate-refresh-token";

    /// <summary>Invalidates the access token for this app only, leaving other sessions alone.</summary>
    public string LogoutPath { get; init; } = "/api/v3/logout";

    public string ProfilePath { get; init; } = "/api/v3/profile";

    // --- orders -------------------------------------------------------------------------

    /// <summary>Place, modify and cancel all live on one route, distinguished by HTTP verb.</summary>
    public string OrderPath { get; init; } = "/api/v3/orders/sync";

    /// <summary>
    /// Cancel by path rather than by body; <c>{0}</c> is the broker order id.
    ///
    /// FYERS documents cancellation both as a DELETE carrying a JSON body and as a DELETE with
    /// the id in the path. The path form is used here because a DELETE with a body is poorly
    /// supported by intermediaries and by <c>HttpClient</c> itself, and the two are documented
    /// as equivalent.
    /// </summary>
    public string CancelOrderPathFormat { get; init; } = "/api/v3/orders/{0}/sync";

    /// <summary>Up to ten orders in one call. Not atomic — see the manifest's basket spec.</summary>
    public string MultiOrderPath { get; init; } = "/api/v3/multi-order/sync";

    /// <summary>The order book. Also serves a single order via <c>?id=</c>.</summary>
    public string OrderBookPath { get; init; } = "/api/v3/orders";

    public string TradeBookPath { get; init; } = "/api/v3/tradebook";

    /// <summary>Pre-trade margin for a list of orders, including hedge benefit.</summary>
    public string MarginPath { get; init; } = "/api/v3/multiorder/margin";

    // --- portfolio ----------------------------------------------------------------------

    /// <summary>
    /// Positions live on one route with four verbs: GET reads, POST converts between products,
    /// DELETE exits, PATCH attaches a stop-loss or target.
    /// </summary>
    public string PositionsPath { get; init; } = "/api/v3/positions";

    public string HoldingsPath { get; init; } = "/api/v3/holdings";

    public string FundsPath { get; init; } = "/api/v3/funds";

    // --- market data --------------------------------------------------------------------

    public string QuotesPath { get; init; } = "/data/quotes";

    public string DepthPath { get; init; } = "/data/depth";

    public string HistoryPath { get; init; } = "/data/history";

    public string OptionChainPath { get; init; } = "/data/options-chain-v3";

    public string MarketStatusPath { get; init; } = "/data/marketStatus";

    // --- documented API limits ------------------------------------------------------------

    /// <summary>
    /// Symbols accepted by one quote call. FYERS documents a maximum of 50 and silently
    /// truncates beyond it, so the market-data facet chunks rather than trusting the caller.
    /// </summary>
    public int MaxQuoteSymbols { get; init; } = 50;

    /// <summary>Legs accepted by one multi-order call.</summary>
    public int MaxBasketLegs { get; init; } = 10;

    /// <summary>Strikes either side of at-the-money the option chain will return. FYERS caps this at 50.</summary>
    public int OptionChainStrikeCount { get; init; } = 50;

    /// <summary>
    /// Days of intraday history FYERS serves in one request. Ranges longer than this are
    /// refused with a message naming the limit rather than silently truncated — see
    /// <see cref="FyersMarketData.GetHistoricalAsync"/>.
    /// </summary>
    public int MaxIntradayHistoryDays { get; init; } = 100;

    /// <summary>Days of daily, weekly or monthly history FYERS serves in one request.</summary>
    public int MaxDailyHistoryDays { get; init; } = 366;
}
