namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Everything about talking to OpenD that an operator might need to change without a redeploy.
///
/// There is no address here. Where OpenD listens is the host's decision — it resolves the gateway,
/// probes it, and hands the connector the address through <c>ConnectorActivationContext.Gateway</c>
/// — so the supervisor and the connector can never disagree about which daemon they mean.
/// </summary>
public sealed record MoomooOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:Moomoo</c>.</summary>
    public const string SectionName = "Connectors:Moomoo";

    /// <summary>Sent in InitConnect. OpenD requires one and only uses it for its own logs.</summary>
    public string ClientId { get; init; } = "akshaya";

    /// <summary>
    /// Sent in InitConnect as <c>major * 100 + minor</c>. OpenD refuses clients it considers too old,
    /// so this tracks the protocol revision the DTOs were written against rather than our release.
    /// </summary>
    public int ClientVersion { get; init; } = 1000;

    /// <summary>How long to wait for the TCP connection and the InitConnect answer together.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout for one request/response exchange.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Timeout for a whole-market static-info request. The US equity list is tens of thousands of rows
    /// in one response, and sharing the ordinary timeout would fail every instrument ingest.
    /// </summary>
    public TimeSpan InstrumentListTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Used only when InitConnect does not say how often OpenD wants a KeepAlive.</summary>
    public TimeSpan DefaultKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest frame body accepted. A corrupt length field must not allocate gigabytes.</summary>
    public int MaxFrameBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Securities per snapshot request. OpenD documents 400.</summary>
    public int MaxSnapshotSecurities { get; init; } = 400;

    /// <summary>Securities per static-info lookup. Chunked at the snapshot size to stay well inside OpenD's frame.</summary>
    public int MaxStaticInfoSecurities { get; init; } = 400;

    /// <summary>Order-book levels requested per side.</summary>
    public int OrderBookLevels { get; init; } = 10;

    /// <summary>Candles requested per page of Qot_RequestHistoryKL.</summary>
    public int HistoryPageSize { get; init; } = 1000;

    /// <summary>Safety cap on history paging, so a server that keeps returning a next key cannot loop forever.</summary>
    public int MaxHistoryPages { get; init; } = 50;

    /// <summary>
    /// Orders sent for one basket. OpenD allows fifteen placements per thirty seconds per account, so a
    /// larger loop would spend most of a window on one basket.
    /// </summary>
    public int MaxBasketLegs { get; init; } = 5;

    /// <summary>
    /// Subscription quota the stream enforces locally. moomoo assigns 100 to the smallest accounts and
    /// more to larger ones; one Basic or OrderBook subscription on one security uses one unit.
    /// </summary>
    public int SubscriptionQuota { get; init; } = 100;

    /// <summary>OpenD refuses an unsubscribe within a minute of the subscribe.</summary>
    public TimeSpan MinimumSubscriptionHold { get; init; } = TimeSpan.FromMinutes(1);

    // --- stream reconnection -------------------------------------------------------------------

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Jitter fraction on every reconnect delay, so a restarted OpenD is not hit in lockstep.</summary>
    public double ReconnectJitter { get; init; } = 0.30;

    /// <summary>Zero means reconnect forever.</summary>
    public int MaxReconnectAttempts { get; init; }
}
