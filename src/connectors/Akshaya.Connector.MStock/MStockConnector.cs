using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.MStock;

/// <summary>
/// The mStock (Mirae Asset Capital Markets) connector: India, NSE and BSE, cash and F&amp;O.
///
/// This class is deliberately thin. It owns object lifetime and nothing else — every piece of
/// broker behaviour lives in a facet, and the facets know nothing about each other. Wiring is
/// the only job here, and if this file ever grows business logic that is a sign a facet
/// boundary is in the wrong place.
///
/// Two lifetime details worth knowing before you change anything:
///
/// 1. The instrument cache is shared by every connector instance in the process, not owned by
///    this one. It holds the parsed script master — hundreds of thousands of rows — and the
///    chart routes and the socket cannot work without it at all, because mStock identifies
///    instruments there by numeric token and nothing else. Connectors are built per request,
///    so a cache owned by the instance was empty on every request; see
///    <see cref="SharedInstrumentMaster{TCache}"/>. The instance never disposes it.
///
/// 2. Facets are built eagerly in the constructor rather than lazily per property. A lazy
///    property that constructs an <see cref="MStockApi"/> on first touch would quietly create
///    a second <see cref="HttpClient"/> if two threads raced, and the resulting socket
///    exhaustion would show up as intermittent timeouts under load — the worst possible
///    failure mode to debug in a trading system.
/// </summary>
public sealed class MStockConnector : ConnectorBase, IAsyncDisposable
{
    private readonly MStockApi _api;
    private readonly SharedInstrumentMaster<MStockInstrumentCache> _master;
    private readonly MStockInstrumentCache _instruments;
    private readonly MStockStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Creates a connector bound to a live session.
    /// </summary>
    /// <param name="manifest">Loaded from this assembly's connector.manifest.json by the host.</param>
    /// <param name="session">The authenticated session. Null is only valid via
    /// <see cref="CreateUnauthenticated"/>, which the host uses for the login handshake.</param>
    /// <param name="options">Endpoint and timeout configuration.</param>
    /// <param name="logger">Host-supplied logger, already scoped with connector and tenant ids.</param>
    /// <param name="clock">Injected so tests and the backtester can control expiry.</param>
    /// <param name="httpClientFactory">
    /// The host's shared connection pool, keyed by connector id. Supplied in production; null in
    /// tests, where this connector falls back to owning a client of its own. Using the pooled one
    /// is what stops every request-scoped activation paying a fresh TLS handshake — see
    /// <c>ConnectorHttpClientPool</c>.
    /// </param>
    public MStockConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        MStockOptions options,
        ILogger<MStockConnector> logger,
        IClock? clock = null,
        Func<string, HttpClient>? httpClientFactory = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Errors = new MStockErrorMapper();

        // Keyed by endpoint so the sandbox and production never share a list.
        _master = SharedInstrumentMaster.For(
            options.BaseUrl.AbsoluteUri,
            static () => new MStockInstrumentCache());
        _instruments = _master.Cache;

        // The symbol translator prefers the script master when it is loaded and falls back to
        // structural rules (INFY -> INFY-EQ, expiry/strike composition for F&O) when it is not.
        // The fallback matters at cold start: the master takes minutes to ingest, and refusing
        // every order until it finishes would make a restart look like an outage.
        Symbols = new MStockSymbolTranslator(_instruments);

        _api = MStockApi.Create(
            options,
            Errors,
            session,
            httpClientFactory?.Invoke(manifest.Id),
            Logger);

        AuthFacet = new MStockAuth(options, Errors, Clock);
        ReferenceFacet = new MStockReference(_api, options, _master, Clock);
        OrdersFacet = new MStockOrders(_api, options, Symbols, Clock);
        PortfolioFacet = new MStockPortfolio(_api, options, Symbols, _instruments);
        MarketDataFacet = new MStockMarketData(
            _api, options, Symbols, Clock, _instruments, ReferenceFacet.EnsureLoadedAsync);

        // No session means no socket. The unauthenticated instance exists only to run the
        // login handshake, and the contract says callers must handle a null Stream.
        _stream = session is null
            ? null
            : new MStockStream(options, session, _instruments, Clock, ReferenceFacet.EnsureLoadedAsync, logger);
    }

    /// <summary>Endpoint and timeout configuration, exposed for diagnostics.</summary>
    public MStockOptions Options { get; }

    /// <summary>Vendor-to-canonical error mapping. Shared by every facet so mapping stays consistent.</summary>
    public MStockErrorMapper Errors { get; }

    /// <summary>Canonical identity to mStock's symbology and back.</summary>
    public ISymbolTranslator Symbols { get; }

    /// <summary>
    /// The parsed script master, shared process-wide. Exposed so health checks can report how
    /// many instruments are loaded and how many rows were skipped.
    /// </summary>
    public MStockInstrumentCache Instruments => _instruments;

    private MStockAuth AuthFacet { get; }

    private MStockOrders OrdersFacet { get; }

    private MStockPortfolio PortfolioFacet { get; }

    private MStockMarketData MarketDataFacet { get; }

    private MStockReference ReferenceFacet { get; }

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
    /// <see cref="Auth"/> is usable on it; every other facet fails with SessionExpired,
    /// which is the contract's answer for "you have not signed in yet".
    /// </summary>
    public static MStockConnector CreateUnauthenticated(
        ConnectorManifest manifest,
        MStockOptions options,
        ILogger<MStockConnector> logger,
        IClock? clock = null,
        Func<string, HttpClient>? httpClientFactory = null) =>
        new(manifest, session: null, options, logger, clock, httpClientFactory);

    /// <summary>
    /// Health for mStock folds in two things the base class cannot know: whether the script
    /// master has been ingested, and how many of its rows we failed to parse.
    ///
    /// Both are reported as degraded rather than unhealthy, on purpose. An un-ingested master
    /// still permits trading via the structural symbol fallback, and skipped rows only affect
    /// the specific instruments involved. Marking the whole connector unhealthy for either
    /// would take a working broker link offline over a reference-data problem.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct).ConfigureAwait(false);
        if (baseHealth.IsFailure)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;

        if (!_instruments.IsLoaded)
        {
            return health with
            {
                Detail = Join(
                    health.Detail,
                    "Script master not loaded yet; it loads on the first chart, live price or "
                    + "search, and symbol resolution uses the structural fallback until then."),
            };
        }

        if (_instruments.SkippedRows > 0)
        {
            return health with
            {
                Detail = Join(
                    health.Detail,
                    $"{_instruments.SkippedRows} script-master rows could not be parsed "
                    + $"({_instruments.Count} loaded)."),
            };
        }

        return health;
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

        // Socket first: it may still be resolving ticks, and a tick arriving mid-teardown should
        // not race the HTTP client going away.
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        // The instrument cache is NOT disposed: it is process-wide and other connector instances
        // are reading it right now.
        await _api.DisposeAsync().ConfigureAwait(false);

        // Chain to the base, as ConnectorBase.DisposeAsync's own documentation requires. Until this
        // method carried `override` it hid the base instead, so this never ran.
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
