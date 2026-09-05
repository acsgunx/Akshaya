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
/// 1. The instrument cache is shared BY REFERENCE between the reference, market-data and stream
///    facets. The stream cannot work without it at all — Kite's socket identifies instruments by
///    numeric token and nothing else, as does the historical-candle route — and one cache per
///    facet would both multiply the memory and let the facets disagree about what a token means.
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
        Instruments = new ZerodhaInstrumentCache();
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
        MarketDataFacet = new ZerodhaMarketData(_api, options, Symbols, Instruments, Clock);
        ReferenceFacet = new ZerodhaReference(_api, options, Instruments);

        _stream = session is null
            ? null
            : new ZerodhaStream(options, session, Instruments, Symbols, Tags, Clock, Logger);
    }

    /// <summary>Endpoint and timeout configuration, exposed for diagnostics.</summary>
    public ZerodhaOptions Options { get; }

    /// <summary>Vendor-to-canonical error mapping. Shared by every facet so mapping stays consistent.</summary>
    public ZerodhaErrorMapper Errors { get; }

    /// <summary>Canonical identity to Kite's symbology and back.</summary>
    public ISymbolTranslator Symbols { get; }

    /// <summary>
    /// The parsed instrument master. Exposed so the host's daily ingest job can populate it and so
    /// health checks can report how many instruments are loaded and how many rows were skipped.
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
                "Instrument master not yet ingested. Cash symbols still translate structurally, but "
                + "monthly derivatives cannot be resolved, and neither the live feed nor historical "
                + "candles can run — both address instruments by numeric token.");
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
                + "contain; it is stale and should be re-ingested.");
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

        // Socket first: it holds a reference to the instrument cache and would log against a
        // disposed lookup if the cache went first.
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        await _api.DisposeAsync().ConfigureAwait(false);
        Instruments.Dispose();

        // The base suppresses finalization; doing it here as well would be harmless but would hide
        // the fact that this type is expected to chain.
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
