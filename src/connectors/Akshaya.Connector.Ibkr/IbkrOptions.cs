namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Everything about talking to the Client Portal Gateway that an operator might change without a redeploy.
/// WHERE the gateway listens is not here: the host resolves that from <c>Connectors:Gateways</c> (ADR 0008)
/// and hands it to the connector.
/// </summary>
public sealed record IbkrOptions
{
    /// <summary>Every Client Portal Web API route lives under this path on the gateway.</summary>
    public string BasePath { get; init; } = "/v1/api/";

    /// <summary>
    /// The gateway serves HTTPS with a self-signed certificate. On a loopback address that certificate is
    /// accepted, because the traffic never leaves the machine. On any other address it is refused unless this is
    /// set: accepting whatever certificate a remote host presents lets anyone who can sit between the two read
    /// and alter orders.
    /// </summary>
    public bool AllowUntrustedCertificate { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Order reply message ids (for example <c>o163</c>, a price-percentage precaution) this connector may confirm
    /// on the trader's behalf. EMPTY BY DEFAULT: every prompt is one of the username's own precautions, and
    /// confirming it silently would switch the precaution off. An order that raises any other prompt is rejected
    /// with the prompt's text and ids, so the operator can decide.
    /// </summary>
    public IReadOnlyCollection<string> AutoConfirmMessageIds { get; init; } = [];

    /// <summary>Confirmations in a row before an order is given up on; a prompt can be followed by another.</summary>
    public int MaxReplyRounds { get; init; } = 5;

    /// <summary>Conids per snapshot request. IBKR documents 100.</summary>
    public int MaxSnapshotConids { get; init; } = 100;

    /// <summary>
    /// A first snapshot for a conid only starts IBKR's stream for it and returns no prices. Rows that come back
    /// empty are asked for again this many times.
    /// </summary>
    public int SnapshotRetries { get; init; } = 2;

    public TimeSpan SnapshotRetryDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Conids per <c>/trsrv/secdef</c> request. IBKR documents 200.</summary>
    public int MaxSecdefConids { get; init; } = 200;

    /// <summary>Positions per page of <c>/portfolio/{account}/positions/{page}</c>.</summary>
    public int PositionsPageSize { get; init; } = 100;

    public int MaxPositionPages { get; init; } = 20;

    public int MaxHistoryPages { get; init; } = 50;

    /// <summary>
    /// Strikes loaded for an option chain, nearest the underlying's price. Each strike and right is one contract
    /// lookup against a 10-requests-a-second limit, so a full chain of a liquid underlying is not practical.
    /// </summary>
    public int MaxChainStrikes { get; init; } = 30;

    /// <summary>Orders sent for one basket: there is no basket route, so this is a loop.</summary>
    public int MaxBasketLegs { get; init; } = 5;

    /// <summary>Days of executions the trades route returns. IBKR's maximum is 7.</summary>
    public int MaxTradeDays { get; init; } = 7;

    /// <summary>How often the stream sends <c>tic</c> to keep the websocket session alive.</summary>
    public TimeSpan StreamHeartbeat { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Market data lines. An IBKR username has 100 by default.</summary>
    public int MaxStreamSubscriptions { get; init; } = 100;

    /// <summary>Largest websocket message accepted.</summary>
    public int MaxFrameBytes { get; init; } = 4 * 1024 * 1024;

    // --- stream reconnection -------------------------------------------------------------------

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double ReconnectJitter { get; init; } = 0.30;

    /// <summary>Zero means reconnect forever.</summary>
    public int MaxReconnectAttempts { get; init; }
}
