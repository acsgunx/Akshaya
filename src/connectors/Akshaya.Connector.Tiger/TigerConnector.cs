using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// The Tiger Brokers connector: US, Hong Kong and Singapore stocks and options, through Tiger's OpenAPI.
///
/// Wiring only. Three details matter:
///
/// 1. EVERY REQUEST IS SIGNED. There is no session to hold open, so there is nothing to keep alive and nothing to
///    refresh; a connector is cheap to build and holds no connection.
/// 2. TRADING AND QUOTES CAN BE DIFFERENT HOSTS, decided by the account's licence, so the endpoint is derived per
///    credential rather than fixed per connector. See <see cref="TigerChannel"/>.
/// 3. THE CONTRACT CACHE IS SHARED BY THE PROCESS: a Tiger contract's listing, currency and lot are the same for
///    every account.
///
/// There is no <see cref="IConnectorStream"/>: Tiger's push feed is a protocol of its own, and the manifest declares
/// no streaming, so prices are polled.
/// </summary>
public sealed class TigerConnector : ConnectorBase
{
    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">The linked session, or null for the link handshake.</param>
    /// <param name="options">Endpoints, timeouts and limits.</param>
    /// <param name="logger">Host-supplied logger.</param>
    /// <param name="clock">Injected so tests and the backtester can control time.</param>
    public TigerConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        TigerOptions options,
        ILogger<TigerConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Errors = new TigerErrorMapper();
        Instruments = TigerInstrumentCache.Shared;
        Symbols = new TigerSymbolTranslator(Instruments);

        var channel = new TigerChannel(options, Errors, Clock, Logger);
        var resolver = new TigerInstrumentResolver(Instruments, options);

        Func<Result<BrokerSession>> requireSession = RequireSession;

        AuthFacet = new TigerAuth(options, channel, Clock, Logger, manifest.Auth.SessionLifetime ?? TimeSpan.FromDays(365));
        OrdersFacet = new TigerOrders(channel, options, Instruments, resolver, requireSession, Clock, Logger);
        PortfolioFacet = new TigerPortfolio(channel, options, resolver, requireSession, Logger);
        MarketDataFacet = new TigerMarketData(channel, options, Instruments, resolver, requireSession, Clock);
        ReferenceFacet = new TigerReference(channel, options, Instruments, resolver, requireSession);
    }

    public TigerOptions Options { get; }

    public TigerErrorMapper Errors { get; }

    public ISymbolTranslator Symbols { get; }

    internal TigerInstrumentCache Instruments { get; }

    private TigerAuth AuthFacet { get; }

    private TigerOrders OrdersFacet { get; }

    private TigerPortfolio PortfolioFacet { get; }

    private TigerMarketData MarketDataFacet { get; }

    private TigerReference ReferenceFacet { get; }

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
    /// <remarks>Tiger publishes no live feed this connector speaks, so there is no stream to offer.</remarks>
    public override IConnectorStream? Stream => null;
}
