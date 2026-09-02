using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// What the host hands a connector when it activates one.
///
/// A record rather than a parameter list so that adding a service later (an HTTP client
/// factory, a secret resolver) does not break every third-party connector's entry point.
/// </summary>
public sealed record ConnectorActivationContext
{
    /// <summary>The validated manifest. Always the one the host loaded, never a re-read.</summary>
    public required ConnectorManifest Manifest { get; init; }

    /// <summary>
    /// Null when creating the unauthenticated instance for the login handshake. A connector
    /// must tolerate null here and let only its Auth facet work.
    /// </summary>
    public BrokerSession? Session { get; init; }

    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Injected so a connector never calls DateTimeOffset.UtcNow — see SharedKernel/Clock.cs.</summary>
    public required IClock Clock { get; init; }

    /// <summary>
    /// The host's <c>IHttpClientFactory</c> equivalent, exposed as a plain factory so
    /// the SDK does not force <c>Microsoft.Extensions.Http</c> into every plugin load context.
    /// Null when the host has none configured, in which case a connector owning its own
    /// <see cref="HttpClient"/> is acceptable but must reuse it.
    /// </summary>
    public Func<string, HttpClient>? HttpClientFactory { get; init; }

    /// <summary>
    /// Deployment-specific settings from the host's configuration, scoped to this connector —
    /// a sandbox toggle, a base URL override, a partner id. Never credentials: those arrive
    /// through <see cref="AuthCredentials"/> and are never persisted in configuration.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Convenience: a logger named for the connector.</summary>
    public ILogger CreateLogger() => LoggerFactory.CreateLogger($"Akshaya.Connector.{Manifest.Id}");
}

/// <summary>
/// The entry point the host looks for in a connector assembly.
///
/// Why an interface and not "find the IBrokerConnector implementation by reflection": a
/// connector assembly legitimately contains several IBrokerConnector types (a real one, a
/// sandbox one, test doubles), and picking by convention would eventually pick the wrong one.
/// Requiring an explicit, parameterless-constructible plugin type makes the entry point a
/// deliberate declaration.
///
/// Implementations MUST have a public parameterless constructor — they are activated before
/// any DI container exists inside the plugin's load context — and MUST be thread-safe, since
/// one plugin instance serves every session for that connector.
/// </summary>
public interface IConnectorPlugin
{
    /// <summary>
    /// The manifest, for in-process connectors that build it in C# instead of shipping JSON.
    /// When the plugin directory also contains a <c>connector.manifest.json</c>, the FILE wins
    /// and the catalog logs the disagreement: the file is what CI validated against the schema.
    /// </summary>
    ConnectorManifest Manifest { get; }

    /// <summary>
    /// Creates a connector bound to the supplied session. Returns a failure rather than
    /// throwing for anything the operator could fix (missing setting, unusable session); throw
    /// only for programmer error.
    /// </summary>
    Result<IBrokerConnector> Create(ConnectorActivationContext context);
}
