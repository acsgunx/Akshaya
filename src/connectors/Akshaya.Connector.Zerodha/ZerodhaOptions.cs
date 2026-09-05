namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Everything about the Kite Connect endpoint that an operator might legitimately need to change
/// without a redeploy: base addresses, timeouts, and the individual route templates.
///
/// The routes are configuration rather than constants for the same reason they are in every other
/// connector here: when a vendor renames a route at 09:10 on a trading morning the fix must be a
/// config push, not a release.
/// </summary>
public sealed record ZerodhaOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:Zerodha</c>.</summary>
    public const string SectionName = "Connectors:Zerodha";

    /// <summary>REST base address. Every route below is relative to this.</summary>
    public Uri BaseUrl { get; init; } = new("https://api.kite.trade");

    /// <summary>
    /// Where the user signs in. A different host from the API — this one is the Kite trading
    /// terminal, not the developer API — which is why it is a separate setting.
    /// </summary>
    public Uri LoginUrl { get; init; } = new("https://kite.zerodha.com/connect/login");

    /// <summary>Streaming base address for <see cref="ZerodhaStream"/>.</summary>
    public Uri StreamUrl { get; init; } = new("wss://ws.kite.trade");

    /// <summary>
    /// Kite pins its API surface with a version header rather than a path segment. It is sent on
    /// every call, including the unauthenticated token exchange.
    /// </summary>
    public string ApiVersion { get; init; } = "3";

    /// <summary>Timeout for ordinary REST calls.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Timeout for an instrument-master download. The NFO dump alone is a few megabytes of CSV
    /// and routinely takes far longer than any other call; sharing the ordinary timeout would
    /// make the nightly instrument ingest fail every night.
    /// </summary>
    public TimeSpan InstrumentMasterTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The hour, in the venue's zone, at which Kite invalidates every access token.
    ///
    /// Six, not midnight, and it is documented as a regulatory requirement rather than a
    /// convention. Assuming midnight would prompt for a re-login six hours early every day;
    /// assuming a rolling twenty-four hours would leave a dead token in place at the open.
    /// </summary>
    public int TokenExpiryHourVenueTime { get; init; } = 6;

    /// <summary>IANA id for the venue whose clock the expiry above is measured against.</summary>
    public string VenueTimeZoneId { get; init; } = "Asia/Kolkata";

    // --- authentication -----------------------------------------------------------------

    /// <summary>Exchanges the request_token for an access token, and revokes it on DELETE.</summary>
    public string SessionTokenPath { get; init; } = "/session/token";

    public string ProfilePath { get; init; } = "/user/profile";

    /// <summary>Funds and margins across segments.</summary>
    public string MarginsPath { get; init; } = "/user/margins";

    // --- orders -------------------------------------------------------------------------

    /// <summary>Placement route; <c>{0}</c> is the variety (<c>regular</c> or <c>amo</c>).</summary>
    public string PlaceOrderPathFormat { get; init; } = "/orders/{0}";

    /// <summary>Modify and cancel; <c>{0}</c> is the variety and <c>{1}</c> the order id.</summary>
    public string OrderPathFormat { get; init; } = "/orders/{0}/{1}";

    /// <summary>The day's order book.</summary>
    public string OrderBookPath { get; init; } = "/orders";

    /// <summary>One order's full state history; <c>{0}</c> is the order id.</summary>
    public string OrderHistoryPathFormat { get; init; } = "/orders/{0}";

    public string TradeBookPath { get; init; } = "/trades";

    /// <summary>Pre-trade margin AND itemised charges for a list of orders. Takes JSON, not a form.</summary>
    public string OrderMarginPath { get; init; } = "/margins/orders";

    // --- portfolio ----------------------------------------------------------------------

    /// <summary>GET reads positions; PUT converts one between margin products.</summary>
    public string PositionsPath { get; init; } = "/portfolio/positions";

    public string HoldingsPath { get; init; } = "/portfolio/holdings";

    // --- market data --------------------------------------------------------------------

    /// <summary>Full quotes including five-level depth and open interest. Up to 500 instruments.</summary>
    public string QuotePath { get; init; } = "/quote";

    public string LtpPath { get; init; } = "/quote/ltp";

    public string OhlcPath { get; init; } = "/quote/ohlc";

    /// <summary>Candles; <c>{0}</c> is the numeric instrument token and <c>{1}</c> the interval.</summary>
    public string HistoricalPathFormat { get; init; } = "/instruments/historical/{0}/{1}";

    /// <summary>Instrument master for one exchange; <c>{0}</c> is the exchange segment.</summary>
    public string InstrumentsPathFormat { get; init; } = "/instruments/{0}";

    // --- documented API limits -------------------------------------------------------------

    /// <summary>
    /// Instruments accepted by one quote call. Kite documents 500 and silently omits the rest,
    /// so the market-data facet chunks rather than trusting the caller.
    /// </summary>
    public int MaxQuoteInstruments { get; init; } = 500;

    /// <summary>
    /// Orders this connector will send for one basket. Kite has no batch placement route, so a
    /// basket is a loop — and the loop is capped at the documented ten-orders-per-second limit so
    /// one basket cannot spend the whole second's budget and then some.
    /// </summary>
    public int MaxBasketLegs { get; init; } = 10;

    /// <summary>Instruments one socket may subscribe to. Kite documents 3000.</summary>
    public int MaxStreamSubscriptions { get; init; } = 3000;

    // --- streaming ---------------------------------------------------------------------------

    /// <summary>First reconnect delay. Doubles on each attempt up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Jitter fraction applied to every reconnect delay. Without it, a deploy that drops every
    /// socket at once brings them all back in the same millisecond and Kite throttles the lot.
    /// </summary>
    public double ReconnectJitter { get; init; } = 0.30;

    /// <summary>
    /// How long the socket may be silent before we treat it as dead and reconnect.
    ///
    /// Kite sends a one-byte heartbeat every couple of seconds when there is nothing else to
    /// send, so silence for this long really does mean the connection is gone rather than that
    /// the market is quiet.
    /// </summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Zero means reconnect forever, which is what a trading session wants.</summary>
    public int MaxReconnectAttempts { get; init; }
}
