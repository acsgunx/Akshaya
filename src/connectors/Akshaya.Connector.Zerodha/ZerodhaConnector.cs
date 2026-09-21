using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// The Zerodha Kite connector: India, NSE and BSE, cash and equity derivatives, with a live feed.
///
/// This class is deliberately thin. It owns object lifetime and nothing else — every piece of
/// broker behaviour lives in a facet, and the facets know nothing about each other. Wiring is the
/// only job here, and if this file ever grows business logic that is a sign a facet boundary is in
/// the wrong place.
///
/// Four lifetime details worth knowing before you change anything:
///
/// 1. The instrument cache is shared by every connector instance in the process, not owned by
///    this one. The stream cannot work without it at all — Kite's socket identifies instruments by
///    numeric token and nothing else, as does the historical-candle route. Connectors are built
///    per request, so a cache owned by the instance was empty on every request; see
///    <see cref="SharedInstrumentMaster{TCache}"/>. The instance never disposes it.
///
/// 2. The order-tag index is shared between the ORDERS facet and the STREAM. Orders writes it on
///    placement; the stream reads it to attach a ClientOrderId to the fill Kite pushes back
///    seconds later. Two indices would mean a fill that cannot be matched to the order that caused
///    it without a round trip to the order book.
///
/// 3. Facets are built eagerly in the constructor rather than lazily per property. A lazy property
///    that constructed a <see cref="ZerodhaApi"/> on first touch would quietly create a second
///    <see cref="HttpClient"/> if two threads raced, and the resulting socket exhaustion would
///    show up as intermittent timeouts under load — the worst possible failure mode to debug in a
///    trading system.
///
/// 4. No session means no socket. The unauthenticated instance exists only to run the login
///    handshake, and the contract says callers must handle a null Stream.
/// </summary>
public sealed class ZerodhaConnector : ConnectorBase
{
    private readonly ZerodhaApi _api;
    private readonly ZerodhaStream? _stream;
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
    public ZerodhaConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        ZerodhaOptions options,
        ILogger<ZerodhaConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Errors = new ZerodhaErrorMapper();
        // Keyed by endpoint so a test double and production never share a list.
        var master = SharedInstrumentMaster.For(options.BaseUrl.AbsoluteUri, static () => new ZerodhaInstrumentCache());
        Instruments = master.Cache;
        Tags = new ZerodhaOrderTagIndex();

        // The translator prefers the instrument master when it is loaded and falls back to
        // structural rules when it is not. The fallback matters at cold start and covers more here
        // than at most brokers: Kite's cash symbols carry no series suffix, so every equity
        // translates correctly with no master at all. Only monthly derivatives need it.
        Symbols = new ZerodhaSymbolTranslator(Instruments);

        _api = ZerodhaApi.Create(options, Errors, session, logger: Logger);

        AuthFacet = new ZerodhaAuth(options, Errors, Clock);
        OrdersFacet = new ZerodhaOrders(_api, options, Symbols, Clock, Tags, Logger);
        PortfolioFacet = new ZerodhaPortfolio(_api, options, Symbols, Logger);
        ReferenceFacet = new ZerodhaReference(_api, options, master, Clock);
        MarketDataFacet = new ZerodhaMarketData(
            _api, options, Symbols, Instruments, ReferenceFacet.EnsureLoadedAsync, Clock);

        _stream = session is null
            ? null
            : new ZerodhaStream(
                options, session, Instruments, Symbols, Tags, Clock, Logger, ReferenceFacet.EnsureLoadedAsync);
    }

    /// <summary>Endpoint and timeout configuration, exposed for diagnostics.</summary>
    public ZerodhaOptions Options { get; }

    /// <summary>Vendor-to-canonical error mapping. Shared by every facet so mapping stays consistent.</summary>
    public ZerodhaErrorMapper Errors { get; }

    /// <summary>Canonical identity to Kite's symbology and back.</summary>
    public ISymbolTranslator Symbols { get; }

    /// <summary>
    /// The parsed instrument master, shared process-wide. Exposed so health checks can report how
    /// many instruments are loaded and how many rows were skipped.
    /// </summary>
    public ZerodhaInstrumentCache Instruments { get; }

    /// <summary>Shared between the orders facet and the stream. See the class remarks.</summary>
    internal ZerodhaOrderTagIndex Tags { get; }

    private ZerodhaAuth AuthFacet { get; }

    private ZerodhaOrders OrdersFacet { get; }

    private ZerodhaPortfolio PortfolioFacet { get; }

    private ZerodhaMarketData MarketDataFacet { get; }

    private ZerodhaReference ReferenceFacet { get; }

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
    /// <see cref="Auth"/> is usable on it; every other facet fails with SessionExpired, which is
    /// the contract's answer for "you have not signed in yet".
    /// </summary>
    public static ZerodhaConnector CreateUnauthenticated(
        ConnectorManifest manifest,
        ZerodhaOptions options,
        ILogger<ZerodhaConnector> logger,
        IClock? clock = null) =>
        new(manifest, session: null, options, logger, clock);

    /// <summary>
    /// Health folds in three things the base class cannot know: whether the instrument master has
    /// been ingested, how many of its rows were not kept, and how many ticks arrived for a token
    /// the master does not contain.
    ///
    /// An un-ingested master is reported as detail rather than as unhealthy, because a trader can
    /// still place cash orders without it — Kite's symbols translate structurally. Unresolved
    /// ticks are the sharper signal: they mean the socket is delivering instruments the master does
    /// not know about, so the master is stale and a re-ingest is due.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct).ConfigureAwait(false);
        if (baseHealth.IsFailure)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;
        var detail = health.Detail;

        if (!Instruments.IsLoaded)
        {
            detail = Join(
                detail,
                "Instrument master not loaded yet; it loads on the first chart, live price or search. "
                + "Cash symbols translate structurally until then, but monthly derivatives cannot be "
                + "resolved.");
        }
        else if (Instruments.SkippedRows > 0)
        {
            detail = Join(
                detail,
                $"{Instruments.SkippedRows} instrument-master rows were not kept "
                + $"({Instruments.Count} loaded). Instrument types this connector does not trade are "
                + "counted here, so a non-zero value is expected.");
        }

        if (_stream is { UnresolvedTicks: > 0 } stream)
        {
            detail = Join(
                detail,
                $"{stream.UnresolvedTicks} ticks arrived for instrument tokens the master does not "
                + "contain; it is stale and reloads within twelve hours.");
        }

        return health with { Detail = detail };
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

        // The base suppresses finalization; doing it here as well would be harmless but would hide
        // the fact that this type is expected to chain.
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
