using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

/// <summary>
/// Everything the platform knows how to do with a broker.
///
/// The core NEVER constructs one of these directly and never learns which concrete type it
/// holds. It asks IConnectorFactory for a connector by id, and gets back an instance already
/// wrapped in the host's decorators (rate limiting, resilience, tracing, audit, error
/// normalisation). A connector author writes broker logic only.
///
/// An implementation may be in-process C#, or a gRPC proxy to a process written in Python or
/// Go, or a bridge to a vendor gateway daemon. Nothing above this interface can tell.
/// </summary>
public interface IBrokerConnector : IAsyncDisposable
{
    ConnectorManifest Manifest { get; }

    IConnectorAuth Auth { get; }

    IConnectorOrders Orders { get; }

    IConnectorPortfolio Portfolio { get; }

    IConnectorMarketData MarketData { get; }

    IConnectorReference Reference { get; }

    /// <summary>Null when the broker offers no live feed. Callers must handle null, not assume.</summary>
    IConnectorStream? Stream { get; }

    /// <summary>
    /// Liveness of this connector and, for gateway-hosted brokers, of the gateway behind it.
    /// Surfaced directly in the UI: a trader must never be guessing whether their broker link
    /// is working.
    /// </summary>
    Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default);
}

public sealed record ConnectorHealth
{
    public required bool IsHealthy { get; init; }

    public required StreamState StreamState { get; init; }

    public bool SessionValid { get; init; }

    public DateTimeOffset? SessionExpiresAt { get; init; }

    public bool GatewayRunning { get; init; } = true;

    public string? Detail { get; init; }

    public TimeSpan? Latency { get; init; }
}

/// <summary>
/// Creates connectors bound to a decrypted session. The only supported way to obtain an
/// IBrokerConnector; construction elsewhere bypasses the decorator chain, which means
/// bypassing rate limiting and audit.
/// </summary>
public interface IConnectorFactory
{
    /// <summary>Ids of every connector the host discovered and successfully loaded.</summary>
    IReadOnlyCollection<string> AvailableConnectorIds { get; }

    Result<ConnectorManifest> GetManifest(string connectorId);

    IReadOnlyCollection<ConnectorManifest> GetAllManifests();

    /// <summary>
    /// A connector bound to a live session. The returned instance is scoped to the caller and
    /// must be disposed; it is not safe to cache across requests, because the session it holds
    /// can expire underneath you.
    /// </summary>
    Task<Result<IBrokerConnector>> CreateAsync(
        string connectorId,
        BrokerSession session,
        CancellationToken ct = default);

    /// <summary>
    /// A connector with no session, for the authentication handshake itself. Only the Auth
    /// facet is usable; every other facet returns SessionExpired.
    /// </summary>
    Result<IBrokerConnector> CreateUnauthenticated(string connectorId);
}
