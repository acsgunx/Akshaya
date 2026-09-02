using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.Connectors.Sdk.Decorators;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Host;

/// <summary>
/// An in-process connector the host was told about in code, as opposed to one discovered on
/// disk. Used for connectors that ship with the platform and for tests.
/// </summary>
/// <param name="Manifest">Validated on registration, exactly as a file-based manifest is.</param>
/// <param name="Factory">Builds the raw connector; the host adds the decorator chain.</param>
public sealed record InProcessConnectorRegistration(
    ConnectorManifest Manifest,
    Func<ConnectorActivationContext, Result<IBrokerConnector>> Factory);

/// <summary>
/// A connector that runs somewhere else entirely and speaks the gRPC contract in
/// <c>src/Akshaya.Connectors.Proto/broker_connector.proto</c>.
/// </summary>
/// <param name="Manifest">
/// Loaded from disk like any other. The host validates it locally rather than trusting the
/// remote's <c>GetManifest</c>, so a remote cannot widen its own capabilities at runtime.
/// </param>
/// <param name="Address">gRPC address, e.g. <c>https://connector-ibkr:5001</c>.</param>
public sealed record OutOfProcessConnectorRegistration(ConnectorManifest Manifest, Uri Address);

/// <summary>Everything the connector host needs to know at startup.</summary>
public sealed class ConnectorHostOptions
{
    /// <summary>
    /// Directory scanned for plugin folders, each containing a
    /// <c>connector.manifest.json</c>. Null disables disk scanning entirely, which is the
    /// right setting for tests and for deployments that only use built-in connectors.
    /// </summary>
    public string? PluginDirectory { get; set; }

    /// <summary>
    /// Contract version this host implements. Overridable only so tests can exercise the
    /// compatibility rule; production should leave it alone.
    /// </summary>
    public string ContractVersion { get; set; } = ConnectorContract.CurrentVersion;

    /// <summary>
    /// Fail host startup if any plugin fails to load, instead of skipping it.
    ///
    /// False by default, and that default is deliberate: one broken third-party connector must
    /// not stop a trader reaching the other five brokers they are linked to. Failures are
    /// recorded on the catalog and surfaced in health.
    /// </summary>
    public bool FailFastOnPluginError { get; set; }

    /// <summary>Connectors compiled into the host.</summary>
    public IList<InProcessConnectorRegistration> InProcess { get; } = [];

    /// <summary>Connectors reached over gRPC.</summary>
    public IList<OutOfProcessConnectorRegistration> OutOfProcess { get; } = [];

    /// <summary>Per-connector settings, keyed by connector id then setting name.</summary>
    public IDictionary<string, IReadOnlyDictionary<string, string>> Settings { get; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    // ── Decorator toggles. All on by default; turning one off is an explicit, auditable act. ──

    /// <summary>
    /// Records order-affecting calls. Turning this off in production is a compliance decision,
    /// not a performance one — the sink is required to be non-blocking.
    /// </summary>
    public bool EnableAuditing { get; set; } = true;

    public bool EnableTracing { get; set; } = true;

    public bool EnableResilience { get; set; } = true;

    /// <summary>
    /// Enforces the manifest's declared limits. Off means the broker enforces them instead, by
    /// throttling or banning the credential.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;

    public ResilienceOptions Resilience { get; set; } = new();

    public ConnectorRateLimiterOptions RateLimiter { get; set; } = new();

    /// <summary>How long a gateway health probe result is trusted before re-probing.</summary>
    public TimeSpan GatewayProbeCacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for one gateway probe. Short: a hung probe is a down gateway.</summary>
    public TimeSpan GatewayProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Registers a connector compiled into the host.</summary>
    public ConnectorHostOptions AddInProcess(
        ConnectorManifest manifest,
        Func<ConnectorActivationContext, Result<IBrokerConnector>> factory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(factory);

        InProcess.Add(new InProcessConnectorRegistration(manifest, factory));
        return this;
    }

    /// <summary>Registers a plugin type compiled into the host, using its own manifest.</summary>
    public ConnectorHostOptions AddInProcess<TPlugin>()
        where TPlugin : IConnectorPlugin, new()
    {
        var plugin = new TPlugin();
        return AddInProcess(plugin.Manifest, plugin.Create);
    }

    /// <summary>Registers a connector running out of process behind gRPC.</summary>
    public ConnectorHostOptions AddOutOfProcess(ConnectorManifest manifest, Uri address)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(address);

        OutOfProcess.Add(new OutOfProcessConnectorRegistration(manifest, address));
        return this;
    }

    /// <summary>Settings for one connector, empty when none were configured.</summary>
    public IReadOnlyDictionary<string, string> SettingsFor(string connectorId) =>
        Settings.TryGetValue(connectorId, out var settings)
            ? settings
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
