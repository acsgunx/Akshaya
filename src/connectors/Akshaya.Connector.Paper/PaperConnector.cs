using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Paper;

/// <summary>
/// A simulated broker that implements the same <see cref="IBrokerConnector"/> as a live one.
///
/// It is used for two things and it is important that they are the same thing: paper trading,
/// where a user rehearses against live prices, and the backtester's execution venue, where the
/// same <see cref="MatchingEngine"/> is driven by a replayed tape. If those two used different
/// code, a strategy would be validated by one and deployed on the basis of the other.
///
/// <b>The design rule for this connector: nothing above it may be able to tell.</b> It slips,
/// it fills partially, it charges taxes, it rejects orders its manifest does not claim, it
/// gates on the session. Every place it is easier than a real broker is a place a user learns
/// a habit that will cost them money live. There is exactly one deliberate exception, session
/// expiry, and <see cref="PaperAuth"/> explains why at length.
///
/// Like <c>MStockConnector</c> this class is wiring only. All behaviour lives in the engine and
/// the facets; business logic appearing here means a facet boundary is wrong.
/// </summary>
public sealed class PaperConnector : ConnectorBase
{
    private readonly MatchingEngine _engine;
    private readonly PaperStream _stream;
    private bool _disposed;

    /// <summary>Creates a connector bound to a paper session.</summary>
    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">
    /// The paper session. Null is only valid via <see cref="CreateUnauthenticated"/>; the
    /// trading facets then fail with SessionExpired exactly as a real connector's would.
    /// </param>
    /// <param name="source">
    /// Where prices come from: a live feed for paper trading, a replay for a backtest. The one
    /// seam that distinguishes the two uses of this connector.
    /// </param>
    /// <param name="options">Fill model, opening cash and charge schedules.</param>
    /// <param name="logger">Host-supplied logger, already scoped with connector and tenant ids.</param>
    /// <param name="clock">
    /// Injected so a backtest controls time. Note the engine prefers TICK time over this once
    /// the tape starts — see the determinism remarks on <see cref="MatchingEngine"/>.
    /// </param>
    public PaperConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        IMarketDataSource source,
        PaperOptions options,
        ILogger<PaperConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        Source = source;
        Options = options;

        _engine = new MatchingEngine(source, options, Clock);

        AuthFacet = new PaperAuth(Clock);

        // RequireSession and ValidateAgainstManifest are passed as delegates rather than
        // reimplemented in the facets. That keeps Paper's session-gating and manifest-honouring
        // behaviour bit-identical to every other SDK connector's, which is the only way the
        // conformance suite's guarantees mean anything when applied to this one.
        OrdersFacet = new PaperOrders(_engine, RequireSession, ValidateAgainstManifest);
        PortfolioFacet = new PaperPortfolio(_engine, RequireSession);
        MarketDataFacet = new PaperMarketData(_engine, source, RequireSession);
        ReferenceFacet = new PaperReference(source);

        // Unlike a socket-backed connector this one has no reason to withhold the stream from
        // an unauthenticated instance: there is nothing to connect to and nothing to leak. The
        // manifest declares streaming, so Stream is non-null, always — which is precisely what
        // the conformance suite checks a streaming connector for.
        _stream = new PaperStream(_engine, manifest.MarketData.MaxStreamSubscriptions);
    }

    /// <summary>The price source this instance was built with. Exposed for diagnostics.</summary>
    public IMarketDataSource Source { get; }

    /// <summary>The simulation's configuration. Exposed so a backtest report can record what it ran with.</summary>
    public PaperOptions Options { get; }

    /// <summary>
    /// The simulated venue.
    ///
    /// Exposed because a backtest drives it directly — <see cref="MatchingEngine.OnTick"/> in a
    /// replay loop, then <see cref="MatchingEngine.EndSession"/> — rather than going through
    /// <see cref="MatchingEngine.RunAsync"/> and an async enumerable it does not need. Nothing
    /// in the trading core may touch this; it is not part of <see cref="IBrokerConnector"/>.
    /// </summary>
    public MatchingEngine Engine => _engine;

    private PaperAuth AuthFacet { get; }

    private PaperOrders OrdersFacet { get; }

    private PaperPortfolio PortfolioFacet { get; }

    private PaperMarketData MarketDataFacet { get; }

    private PaperReference ReferenceFacet { get; }

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
    /// The instance used for the login handshake, before any session exists. Only
    /// <see cref="Auth"/> is usable on it — and since <see cref="PaperAuth"/> completes on the
    /// first step, the handshake is one call.
    /// </summary>
    public static PaperConnector CreateUnauthenticated(
        ConnectorManifest manifest,
        IMarketDataSource source,
        PaperOptions options,
        ILogger<PaperConnector> logger,
        IClock? clock = null) =>
        new(manifest, session: null, source, options, logger, clock);

    /// <summary>
    /// Base health plus what only the engine knows: whether any price has arrived yet.
    ///
    /// Degraded rather than unhealthy, because an engine with no ticks can still accept and
    /// rest orders — it just cannot fill them. Reporting the connector as down would take a
    /// working paper account offline before its first print of the day; saying nothing would
    /// leave a user watching resting orders and wondering why the strategy is silent.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct);
        if (baseHealth.IsFailure)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;

        if (Source.Instruments.Count == 0)
        {
            return health with
            {
                Detail = Join(
                    health.Detail,
                    "The paper market-data source has no instruments; nothing can be priced or filled."),
            };
        }

        if (_engine.TradeCount == 0 && _stream.State != StreamState.Connected)
        {
            return health with
            {
                Detail = Join(health.Detail, "No ticks have been consumed yet; resting orders cannot fill."),
            };
        }

        return health;
    }

    private static string Join(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

    /// <inheritdoc />
    /// <remarks>
    /// <c>override</c>, not a new method: <c>ConnectorBase</c> already implements
    /// <see cref="IAsyncDisposable"/>, and hiding it would mean a caller holding the base type
    /// disposed the base implementation and leaked the stream and the engine.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stream first: it holds a subscription on the engine's event feed and would write
        // into a completed channel if the engine went first.
        await _stream.DisposeAsync();
        await _engine.DisposeAsync();
        await base.DisposeAsync();
    }
}
