using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// The Longbridge connector: US, Hong Kong and Singapore equities and indices, and US options, through Longbridge
/// OpenAPI — REST for trading and assets, binary WebSocket gateways for quotes and pushes.
///
/// Wiring only. Three lifetime details matter:
///
/// 1. ONE REST CLIENT AND ONE LAZILY OPENED QUOTE SOCKET PER CONNECTOR. The host builds a connector per request,
///    and most requests are REST alone. See <see cref="LongbridgeChannel"/>.
/// 2. THE STREAM OWNS ITS OWN SOCKETS. Pushes belong to a connection, and a request socket that also carried
///    them would buffer them for a request that has already returned.
/// 3. THE INSTRUMENT CACHE IS SHARED BY REFERENCE across every facet, so the security an order was placed on and
///    the one its update is decoded to can never have been looked up twice and disagree.
/// </summary>
public sealed class LongbridgeConnector : ConnectorBase
{
    private readonly LongbridgeChannel _channel;
    private readonly LongbridgeStream? _stream;
    private bool _disposed;

    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">The linked session, or null for the sign-in handshake.</param>
    /// <param name="options">Endpoints, timeouts and limits.</param>
    /// <param name="logger">Host-supplied logger.</param>
    /// <param name="clock">Injected so tests and the backtester can control time.</param>
    /// <param name="httpClientFactory">The host's HTTP client factory, when it has one.</param>
    public LongbridgeConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        LongbridgeOptions options,
        ILogger<LongbridgeConnector> logger,
        IClock? clock = null,
        Func<string, HttpClient>? httpClientFactory = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Errors = new LongbridgeErrorMapper();
        Instruments = new LongbridgeInstrumentCache();
        Symbols = new LongbridgeSymbolTranslator(Instruments);

        var errors = Errors;
        var connectorClock = Clock;
        var connectorLogger = Logger;

        Func<LongbridgeCredentials?, LongbridgeApi> apiFactory = credentials => LongbridgeApi.Create(
            options,
            errors,
            credentials,
            connectorClock,
            httpClientFactory?.Invoke(LongbridgeAuth.ConnectorId),
            connectorLogger);

        // A session whose extras are unusable is reported by every facet as a re-link, not as a 401 from Longbridge.
        LongbridgeCredentials? credentials = null;
        Error? accountError = null;

        if (session is not null)
        {
            var account = LongbridgeAccount.FromSession(session);
            if (account.IsSuccess)
            {
                credentials = account.Value.Credentials;
            }
            else
            {
                accountError = account.Error;
            }
        }

        Func<Result<BrokerSession>> requireSession = () =>
        {
            var required = RequireSession();
            return required.IsFailure || accountError is not { } invalid ? required : Result<BrokerSession>.Failure(invalid);
        };

        _channel = new LongbridgeChannel(apiFactory(credentials), options, Errors, Logger);
        var resolver = new LongbridgeInstrumentResolver(Instruments, options);

        AuthFacet = new LongbridgeAuth(options, Errors, Clock, Logger, manifest.Auth.SessionLifetime ?? TimeSpan.FromDays(30), apiFactory);
        OrdersFacet = new LongbridgeOrders(_channel, options, Instruments, resolver, requireSession, Clock, Logger);
        PortfolioFacet = new LongbridgePortfolio(_channel, Instruments, resolver, requireSession, Logger);
        MarketDataFacet = new LongbridgeMarketData(_channel, options, Instruments, resolver, requireSession, Clock);
        ReferenceFacet = new LongbridgeReference(_channel, Instruments, resolver, requireSession);

        // No usable session means no feed: the unauthenticated instance exists only to run the sign-in.
        _stream = session is null || credentials is null
            ? null
            : new LongbridgeStream(options, session, Errors, Instruments, resolver, apiFactory, Clock, Logger);
    }

    public LongbridgeOptions Options { get; }

    public LongbridgeErrorMapper Errors { get; }

    public ISymbolTranslator Symbols { get; }

    internal LongbridgeInstrumentCache Instruments { get; }

    private LongbridgeAuth AuthFacet { get; }

    private LongbridgeOrders OrdersFacet { get; }

    private LongbridgePortfolio PortfolioFacet { get; }

    private LongbridgeMarketData MarketDataFacet { get; }

    private LongbridgeReference ReferenceFacet { get; }

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
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
