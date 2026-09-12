using System.Diagnostics;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// The Interactive Brokers connector: US, Hong Kong and Singapore stocks and US options, through an operator-run
/// Client Portal Gateway.
///
/// Wiring only. Two lifetime details matter:
///
/// 1. THE HTTP CLIENT AND THE CONTRACT CACHE OUTLIVE THE CONNECTOR. The host builds a connector per request; the
///    gateway's HTTP client (<see cref="IbkrApi"/>) and the conid cache (<see cref="IbkrInstrumentCache.Shared"/>)
///    are per process, because neither holds anything specific to a user.
/// 2. THE STREAM OWNS ITS OWN WEBSOCKET, and exists only for a connector bound to a session and a gateway.
/// </summary>
public sealed class IbkrConnector : ConnectorBase
{
    private readonly IbkrStream? _stream;
    private bool _disposed;

    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">The linked session, or null for the link handshake.</param>
    /// <param name="gateway">Where the Client Portal Gateway listens, as resolved by the host.</param>
    /// <param name="options">Timeouts, limits and the certificate and reply-confirmation policies.</param>
    /// <param name="logger">Host-supplied logger.</param>
    /// <param name="clock">Injected so tests and the backtester can control time.</param>
    public IbkrConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        GatewayAddress? gateway,
        IbkrOptions options,
        ILogger<IbkrConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Gateway = gateway;
        Errors = new IbkrErrorMapper();
        Instruments = IbkrInstrumentCache.Shared;
        Symbols = new IbkrSymbolTranslator(Instruments);

        var channel = new IbkrChannel(gateway, options, Errors, Clock, Logger);
        var resolver = new IbkrInstrumentResolver(Instruments, options, Logger);

        Func<Result<BrokerSession>> requireSession = RequireSession;

        AuthFacet = new IbkrAuth(options, gateway, Errors, Clock, Logger, manifest.Auth.SessionLifetime ?? TimeSpan.FromDays(1));
        OrdersFacet = new IbkrOrders(channel, options, Instruments, resolver, requireSession, Clock, Logger);
        PortfolioFacet = new IbkrPortfolio(channel, options, Instruments, resolver, requireSession, Logger);
        MarketDataFacet = new IbkrMarketData(channel, options, Instruments, resolver, requireSession, Clock);
        ReferenceFacet = new IbkrReference(channel, Instruments, resolver, requireSession);

        // No session means no feed: the unauthenticated instance exists only to run the link handshake.
        _stream = session is null || gateway is null ? null : new IbkrStream(options, channel, resolver, Clock, Logger);
    }

    public IbkrOptions Options { get; }

    /// <summary>Where the gateway is being reached. Null only if the host activated this without a gateway.</summary>
    public GatewayAddress? Gateway { get; }

    public IbkrErrorMapper Errors { get; }

    public ISymbolTranslator Symbols { get; }

    internal IbkrInstrumentCache Instruments { get; }

    private IbkrAuth AuthFacet { get; }

    private IbkrOrders OrdersFacet { get; }

    private IbkrPortfolio PortfolioFacet { get; }

    private IbkrMarketData MarketDataFacet { get; }

    private IbkrReference ReferenceFacet { get; }

    /// <inheritdoc />
    public override IConnectorAuth Auth => AuthFacet;

    /// <inheritdoc />
    public override IConnectorOrders Orders => OrdersFacet;

    /// <inheritdoc />
    public override IConnectorPortfolio Portfolio => PortfolioFacet;

    /// <inheritdoc />
    public override IConnectorMarketData MarketData => MarketDataFacet;

    /// <inheritdoc />
    public override IConnectorReference Reference => ReferenceFacet;

    /// <inheritdoc />
    public override IConnectorStream? Stream => _stream;

    /// <summary>
    /// Health adds what only the gateway knows: whether its brokerage session is signed in, connected and not competing
    /// with another session. A gateway that is listening but signed out accepts connections and fails every request,
    /// which is exactly the state a trader needs to see.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct).ConfigureAwait(false);
        if (baseHealth.IsFailure || Session is null || Gateway is null)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;
        var started = Stopwatch.GetTimestamp();

        var status = await IbkrApi.Create(Gateway, Options, Errors, Logger)
            .PostAsync<IbkrAuthStatus>("iserver/auth/status", body: null, ct)
            .ConfigureAwait(false);

        var latency = Stopwatch.GetElapsedTime(started);

        if (status.IsFailure)
        {
            return health with
            {
                IsHealthy = false,
                GatewayRunning = false,
                Detail = Join(health.Detail, status.Error.Message),
            };
        }

        var problem = status.Value switch
        {
            { Authenticated: false } => "The Client Portal Gateway is running but not signed in to IBKR.",
            { Competing: true } => "Another session is competing for this IBKR username.",
            { Connected: false } => "The Client Portal Gateway is not connected to IBKR's servers.",
            _ => null,
        };

        return health with
        {
            IsHealthy = health.IsHealthy && problem is null,
            GatewayRunning = true,
            Latency = latency,
            Detail = problem is null ? health.Detail : Join(health.Detail, problem),
        };
    }

    private static string Join(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
