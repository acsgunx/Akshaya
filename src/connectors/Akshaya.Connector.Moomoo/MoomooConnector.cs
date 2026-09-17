using System.Diagnostics;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// The moomoo connector: US and Hong Kong equities, ETFs and options, through an OpenD gateway.
///
/// Wiring only. Three lifetime details matter:
///
/// 1. ONE REQUEST CONNECTION PER CONNECTOR, OPENED ON FIRST USE. The host builds a connector per request,
///    and opening a socket in the constructor would put a TCP handshake on requests that never call OpenD.
///    See <see cref="MoomooChannel"/>.
/// 2. THE STREAM HAS ITS OWN CONNECTION. Pushes are registered per connection, and a request connection
///    that also carried quote pushes would buffer them for a request that has already returned.
/// 3. THE INSTRUMENT CACHE IS SHARED BY REFERENCE across every facet, so the security an order was placed
///    on and the one its fill is decoded to can never be looked up twice and disagree.
/// </summary>
public sealed class MoomooConnector : ConnectorBase
{
    private readonly MoomooChannel _channel;
    private readonly MoomooStream? _stream;
    private bool _disposed;

    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">The linked session, or null for the login handshake.</param>
    /// <param name="gateway">Where OpenD listens, as resolved by the host.</param>
    /// <param name="options">Timeouts and limits.</param>
    /// <param name="logger">Host-supplied logger.</param>
    /// <param name="clock">Injected so tests and the backtester can control time.</param>
    public MoomooConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        GatewayAddress? gateway,
        MoomooOptions options,
        ILogger<MoomooConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Gateway = gateway;
        Errors = new MoomooErrorMapper();
        Instruments = new MoomooInstrumentCache();
        Symbols = new MoomooSymbolTranslator(Instruments);

        var resolver = new MoomooInstrumentResolver(Instruments, options);
        _channel = new MoomooChannel(gateway, options, Errors, Clock, Logger);

        Func<Result<BrokerSession>> requireSession = RequireSession;

        AuthFacet = new MoomooAuth(options, gateway, Errors, Clock, Logger, manifest.Auth.SessionLifetime ?? TimeSpan.FromDays(1));
        OrdersFacet = new MoomooOrders(_channel, options, Instruments, resolver, requireSession, Clock, Logger);
        PortfolioFacet = new MoomooPortfolio(_channel, Instruments, resolver, requireSession, Logger);
        MarketDataFacet = new MoomooMarketData(_channel, options, Instruments, resolver, requireSession, Clock);
        ReferenceFacet = new MoomooReference(_channel, options, Instruments, resolver, requireSession, Logger);

        // No session means no feed: the unauthenticated instance exists only to run the link handshake.
        _stream = session is null || gateway is null
            ? null
            : new MoomooStream(options, gateway, session, Errors, Instruments, resolver, Clock, Logger);
    }

    public MoomooOptions Options { get; }

    /// <summary>Where OpenD is being reached. Null only if the host activated this without a gateway.</summary>
    public GatewayAddress? Gateway { get; }

    public MoomooErrorMapper Errors { get; }

    public ISymbolTranslator Symbols { get; }

    internal MoomooInstrumentCache Instruments { get; }

    private MoomooAuth AuthFacet { get; }

    private MoomooOrders OrdersFacet { get; }

    private MoomooPortfolio PortfolioFacet { get; }

    private MoomooMarketData MarketDataFacet { get; }

    private MoomooReference ReferenceFacet { get; }

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
    /// Health adds what only OpenD knows: whether it answers, and whether it is still signed in to moomoo.
    /// The host's supervisor proves something is listening; a daemon that is listening but signed out keeps
    /// accepting connections and fails every request, which is exactly the state a trader needs to see.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct).ConfigureAwait(false);
        if (baseHealth.IsFailure || Session is null)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;
        var started = Stopwatch.GetTimestamp();

        var state = await _channel.RequestAsync<OpenDGetGlobalStateC2S, OpenDGetGlobalStateS2C>(
            MoomooProtoId.GetGlobalState,
            new OpenDGetGlobalStateC2S(),
            ct).ConfigureAwait(false);

        var latency = Stopwatch.GetElapsedTime(started);

        if (state.IsFailure)
        {
            return health with
            {
                IsHealthy = false,
                GatewayRunning = false,
                Detail = Join(health.Detail, state.Error.Message),
            };
        }

        var signedIn = state.Value is { QotLogined: true, TrdLogined: true } && state.Value.ProgramStatus is not { IsReady: false };
        var detail = health.Detail;

        if (!signedIn)
        {
            detail = Join(detail, $"OpenD is running but not signed in to moomoo ({state.Value.ProgramStatus?.Description ?? "trade or quote server logged out"}).");
        }

        if (!Instruments.IsLoaded)
        {
            detail = Join(detail, "Security lists not yet ingested; securities are resolved from OpenD one request at a time until they are.");
        }

        return health with
        {
            IsHealthy = health.IsHealthy && signedIn,
            GatewayRunning = true,
            Latency = latency,
            Detail = detail,
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

        await _channel.DisposeAsync().ConfigureAwait(false);
        Instruments.Dispose();

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
