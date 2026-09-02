using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Host.OutOfProcess;
using Akshaya.Connectors.Sdk;
using Akshaya.Connectors.Sdk.Decorators;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akshaya.Connectors.Host;

/// <summary>
/// The only supported way to obtain an <see cref="IBrokerConnector"/>.
///
/// It activates the raw connector — in-process, plugin or gRPC proxy — and wraps it in the
/// host's decorator chain. Constructing a connector anywhere else bypasses that chain, which
/// means bypassing rate limiting and the audit trail; the contract on
/// <see cref="IConnectorFactory"/> says so, and this is the implementation that makes it true.
///
/// ══════════════════════ THE DECORATOR ORDER, AND WHY IT IS THIS ══════════════════════
///
///     caller
///       │
///       ▼
///   ┌─────────────────┐  outermost
///   │ AuditingConnector│  one row per LOGICAL operation, including calls the rate limiter
///   └────────┬────────┘  refused — "we would not send your order" must be provable
///            ▼
///   ┌─────────────────┐
///   │ TracingConnector│  one span per logical operation, retries inside it, so a retried
///   └────────┬────────┘  call reads as one slow call rather than four unrelated fast ones
///            ▼
///   ┌───────────────────┐
///   │ResilienceConnector│  retries — idempotent reads only, NEVER a write. See that file.
///   └────────┬──────────┘
///            ▼
///   ┌──────────────────────┐
///   │RateLimitingConnector │  each ATTEMPT takes a permit, because the broker counts
///   └────────┬─────────────┘  attempts, not logical operations
///            ▼
///     raw connector
///
/// Every adjacent pair is deliberate:
///
///  * Audit outside tracing — so an audit row exists even for a call that never got a span
///    because tracing was disabled.
///  * Tracing outside resilience — spans measure what the caller experienced.
///  * Resilience outside rate limiting — a retry must take a fresh permit. The other order
///    would let a retry storm past a limiter that had already refused the first attempt.
///  * Rate limiting innermost — closest to the wire, counting exactly what reaches it.
///
/// Reordering these is a behaviour change, not a refactor.
/// ═════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ConnectorFactory : IConnectorFactory
{
    /// <summary>
    /// Credential id used for the unauthenticated instance. A constant rather than an empty
    /// string so login attempts share one rate-limit bucket instead of being unmetered — which
    /// is what an unauthenticated credential-stuffing attempt would exploit.
    /// </summary>
    public const string UnauthenticatedCredentialId = "unauthenticated";

    private readonly ConnectorCatalog _catalog;
    private readonly ConnectorHostOptions _options;
    private readonly IRateLimitStore _rateLimitStore;
    private readonly IConnectorAuditSink _auditSink;
    private readonly IGatewaySupervisor _gateways;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectorFactory> _logger;
    private readonly IClock _clock;

    public ConnectorFactory(
        ConnectorCatalog catalog,
        IOptions<ConnectorHostOptions> options,
        IRateLimitStore rateLimitStore,
        IConnectorAuditSink auditSink,
        IGatewaySupervisor gateways,
        ILoggerFactory loggerFactory,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rateLimitStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(clock);

        _catalog = catalog;
        _options = options.Value;
        _rateLimitStore = rateLimitStore;
        _auditSink = auditSink;
        _gateways = gateways;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConnectorFactory>();
        _clock = clock;
    }

    public IReadOnlyCollection<string> AvailableConnectorIds => _catalog.ConnectorIds;

    public Result<ConnectorManifest> GetManifest(string connectorId) => _catalog.GetManifest(connectorId);

    public IReadOnlyCollection<ConnectorManifest> GetAllManifests() => _catalog.Manifests;

    public async Task<Result<IBrokerConnector>> CreateAsync(
        string connectorId,
        BrokerSession session,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentNullException.ThrowIfNull(session);

        var entryResult = _catalog.Get(connectorId);
        if (entryResult.IsFailure)
        {
            return Result<IBrokerConnector>.Failure(entryResult.Error);
        }

        var entry = entryResult.Value;

        // A session issued for a different broker is a wiring bug that would otherwise show up
        // as a baffling authentication failure at the venue.
        if (!string.Equals(session.ConnectorId, entry.Manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The supplied session belongs to '{session.ConnectorId}', not '{entry.Manifest.Id}'.");
        }

        // Gateway-hosted brokers have a process behind them that must be up BEFORE we hand out
        // a connector. Doing it here means every caller gets GatewayUnavailable with an
        // actionable message rather than a timeout on their first real call.
        if (entry.Manifest.Hosting == ConnectorHosting.Gateway)
        {
            var gateway = await _gateways.EnsureAvailableAsync(entry.Manifest, session.AccountId, ct);
            if (gateway.IsFailure)
            {
                return Result<IBrokerConnector>.Failure(gateway.Error);
            }
        }

        var raw = Activate(entry, session);
        return raw.IsFailure
            ? Result<IBrokerConnector>.Failure(raw.Error)
            : Result<IBrokerConnector>.Success(Decorate(raw.Value, entry.Manifest, session.AccountId));
    }

    public Result<IBrokerConnector> CreateUnauthenticated(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var entryResult = _catalog.Get(connectorId);
        if (entryResult.IsFailure)
        {
            return Result<IBrokerConnector>.Failure(entryResult.Error);
        }

        var entry = entryResult.Value;
        var raw = Activate(entry, session: null);

        return raw.IsFailure
            ? Result<IBrokerConnector>.Failure(raw.Error)
            : Result<IBrokerConnector>.Success(
                Decorate(raw.Value, entry.Manifest, UnauthenticatedCredentialId));
    }

    /// <summary>
    /// Builds the raw connector from whichever source the catalog recorded. The three sources
    /// converge here and nothing above this method can tell them apart.
    /// </summary>
    private Result<IBrokerConnector> Activate(ConnectorCatalogEntry entry, BrokerSession? session)
    {
        var context = new ConnectorActivationContext
        {
            Manifest = entry.Manifest,
            Session = session,
            LoggerFactory = _loggerFactory,
            Clock = _clock,
            Settings = _options.SettingsFor(entry.Manifest.Id),
        };

        try
        {
            return entry.Source switch
            {
                ConnectorSource.OutOfProcess => ActivateRemote(entry, session),

                _ when entry.Plugin is { } plugin => plugin.Create(context),
                _ when entry.Factory is { } factory => factory(context),

                _ => new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Catalog entry for '{entry.Manifest.Id}' has neither a plugin nor a factory."),
            };
        }
        catch (Exception ex)
        {
            // Connector activation is third-party code. A throw here must become a clean
            // failure for this one broker, not an unhandled exception in the API.
            _logger.LogError(ex, "Activating connector {ConnectorId} threw.", entry.Manifest.Id);
            return new Error(
                ConnectorErrorCodes.Unknown,
                $"The {entry.Manifest.DisplayName} connector failed to start: {ex.Message}");
        }
    }

    private Result<IBrokerConnector> ActivateRemote(ConnectorCatalogEntry entry, BrokerSession? session)
    {
        if (entry.Address is null)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"'{entry.Manifest.Id}' is out-of-process but has no address.");
        }

        var proxy = new GrpcConnectorProxy(
            entry.Manifest,
            entry.Address,
            session,
            _loggerFactory.CreateLogger<GrpcConnectorProxy>());

        return Result<IBrokerConnector>.Success(proxy);
    }

    /// <summary>
    /// Applies the decorator chain, innermost first. Read the class remarks before changing
    /// the order of these four lines.
    /// </summary>
    private IBrokerConnector Decorate(IBrokerConnector raw, ConnectorManifest manifest, string credentialId)
    {
        var decorated = raw;

        if (_options.EnableRateLimiting)
        {
            var limiter = new ConnectorRateLimiter(
                manifest,
                _rateLimitStore,
                _loggerFactory.CreateLogger<ConnectorRateLimiter>(),
                _options.RateLimiter);

            decorated = new RateLimitingConnector(decorated, limiter, credentialId);
        }

        if (_options.EnableResilience)
        {
            decorated = new ResilienceConnector(
                decorated,
                _options.Resilience,
                _loggerFactory.CreateLogger<ResilienceConnector>());
        }

        if (_options.EnableTracing)
        {
            decorated = new TracingConnector(decorated);
        }

        if (_options.EnableAuditing)
        {
            decorated = new AuditingConnector(
                decorated,
                _auditSink,
                credentialId,
                _loggerFactory.CreateLogger<AuditingConnector>());
        }

        return decorated;
    }
}
