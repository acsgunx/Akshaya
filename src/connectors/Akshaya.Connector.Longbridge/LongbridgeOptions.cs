namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Everything about the Longbridge OpenAPI endpoints an operator might need to change without a
/// redeploy. The defaults are the global production hosts; the mainland-China hosts differ only in
/// their domain and are selected with the <c>region</c> setting (see <see cref="LongbridgePlugin"/>).
/// </summary>
public sealed record LongbridgeOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:Longbridge</c>.</summary>
    public const string SectionName = "Connectors:Longbridge";

    /// <summary>REST base address.</summary>
    public Uri HttpBaseUrl { get; init; } = new("https://openapi.longbridge.com");

    /// <summary>OAuth 2.0 endpoints: <c>/authorize</c>, <c>/token</c> and <c>/revoke</c> live under it.</summary>
    public Uri OAuthBaseUrl { get; init; } = new("https://openapi.longbridge.com/oauth2/");

    /// <summary>The quote gateway's WebSocket endpoint.</summary>
    public Uri QuoteSocketUrl { get; init; } = new("wss://openapi-quote.longbridge.com/v2");

    /// <summary>The trade gateway's WebSocket endpoint, which carries order-change pushes.</summary>
    public Uri TradeSocketUrl { get; init; } = new("wss://openapi-trade.longbridge.com/v2");

    /// <summary>
    /// The scope the authorization URL requests. The documentation's example sends <c>3</c> and does
    /// not explain it; it is a setting so a change at Longbridge is a config push, not a release.
    /// </summary>
    public string OAuthScope { get; init; } = "3";

    /// <summary>Sent as <c>Accept-Language</c> and in the socket auth metadata. English keeps error text classifiable.</summary>
    public string Language { get; init; } = "en";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan SocketConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-request timeout on a socket, also sent in the request frame so the gateway can give up first.</summary>
    public TimeSpan SocketRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Client-side WebSocket PING interval and how long to wait for the PONG. The gateway pings too,
    /// but .NET answers those without surfacing them, so the client's own pings are how a dead
    /// connection is noticed on a quiet market.
    /// </summary>
    public TimeSpan SocketKeepAlive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest frame body accepted after decompression.</summary>
    public int MaxFrameBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Symbols per quote, static-info or subscription request. Longbridge documents 500.</summary>
    public int MaxSymbolsPerRequest { get; init; } = 500;

    /// <summary>Candles requested per history page.</summary>
    public int HistoryPageSize { get; init; } = 1000;

    /// <summary>Safety cap on history paging.</summary>
    public int MaxHistoryPages { get; init; } = 50;

    /// <summary>Orders sent for one basket: there is no basket route, so this is a loop.</summary>
    public int MaxBasketLegs { get; init; } = 10;

    /// <summary>Securities one user may have subscribed. Longbridge documents 500.</summary>
    public int MaxSubscriptions { get; init; } = 500;

    // --- stream reconnection -------------------------------------------------------------------

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double ReconnectJitter { get; init; } = 0.30;

    /// <summary>Zero means reconnect forever.</summary>
    public int MaxReconnectAttempts { get; init; }
}
