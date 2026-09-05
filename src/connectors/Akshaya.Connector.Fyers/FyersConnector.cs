using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// The FYERS connector: India, NSE and BSE, cash and equity derivatives.
///
/// This class is deliberately thin. It owns object lifetime and nothing else — every piece of
/// broker behaviour lives in a facet, and the facets know nothing about each other. Wiring is the
/// only job here, and if this file ever grows business logic that is a sign a facet boundary is
/// in the wrong place.
///
/// Three lifetime details worth knowing before you change anything:
///
/// 1. The instrument cache is shared BY REFERENCE between the reference, market-data, portfolio
///    and orders facets. It holds the parsed symbol master — a few hundred thousand rows across
///    four files — and one cache per facet would both quadruple the memory and let the facets
///    disagree about what a symbol means. One cache per connector instance.
///
/// 2. Facets are built eagerly in the constructor rather than lazily per property. A lazy
///    property that constructed a <see cref="FyersApi"/> on first touch would quietly create a
///    second <see cref="HttpClient"/> if two threads raced, and the resulting socket exhaustion
///    would show up as intermittent timeouts under load — the worst possible failure mode to
///    debug in a trading system.
///
/// 3. <see cref="Stream"/> is null, and that is a statement rather than an omission. FYERS'
///    market-data socket speaks a proprietary binary protocol published only inside its own
///    language SDKs; there is no wire format to implement against. The ORDER socket at
///    <c>wss://socket.fyers.in/trade/v3</c> is fully documented JSON and could be implemented
///    today — but <c>IConnectorStream</c> is reached through one manifest flag,
///    <c>marketData.streaming</c>, which the conformance suite reads as "this connector has a
///    live feed" and which the fan-out layer reads as "this connector can be subscribed to for
///    prices". Declaring it true to get order updates would promise a price feed that does not
///    exist; declaring it false and returning a stream anyway fails conformance. So the manifest
///    says false, this says null, and order state is reconciled by polling the order book. See
///    docs/connectors/fyers.md for what it would take to have both.
/// </summary>
public sealed class FyersConnector : ConnectorBase
{
    private readonly FyersApi _api;
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
    public FyersConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        FyersOptions options,
        ILogger<FyersConnector> logger,
        IClock? clock = null)
        : base(manifest, session, logger, clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Errors = new FyersErrorMapper();

        Instruments = new FyersInstrumentCache();

        // The translator prefers the symbol master when it is loaded and falls back to structural
        // rules (SBIN -> NSE:SBIN-EQ, expiry and strike composition for F&O) when it is not. The
        // fallback matters at cold start: the master takes minutes to ingest, and refusing every
        // NSE order until it finishes would make a restart look like an outage.
        Symbols = new FyersSymbolTranslator(Instruments);

        _api = FyersApi.Create(options, Errors, session, logger: Logger);

        AuthFacet = new FyersAuth(options, Errors, Clock);
        OrdersFacet = new FyersOrders(_api, options, Symbols, Clock, Logger);
        PortfolioFacet = new FyersPortfolio(_api, options, Symbols, Logger);
        MarketDataFacet = new FyersMarketData(_api, options, Symbols, Clock);
        ReferenceFacet = new FyersReference(_api, Instruments);
    }

    /// <summary>Endpoint and timeout configuration, exposed for diagnostics.</summary>
    public FyersOptions Options { get; }

    /// <summary>Vendor-to-canonical error mapping. Shared by every facet so mapping stays consistent.</summary>
    public FyersErrorMapper Errors { get; }

    /// <summary>Canonical identity to the FYERS symbology and back.</summary>
    public ISymbolTranslator Symbols { get; }

    /// <summary>
    /// The parsed symbol master. Exposed so the host's daily ingest job can populate it and so
    /// health checks can report how many instruments are loaded and how many rows were skipped.
    /// </summary>
    public FyersInstrumentCache Instruments { get; }

    private FyersAuth AuthFacet { get; }

    private FyersOrders OrdersFacet { get; }

    private FyersPortfolio PortfolioFacet { get; }

    private FyersMarketData MarketDataFacet { get; }

    private FyersReference ReferenceFacet { get; }

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

    /// <summary>
    /// The instance used for the login handshake, before any session exists. Only
    /// <see cref="Auth"/> is usable on it; every other facet fails with SessionExpired, which is
    /// the contract's answer for "you have not signed in yet".
    /// </summary>
    public static FyersConnector CreateUnauthenticated(
        ConnectorManifest manifest,
        FyersOptions options,
        ILogger<FyersConnector> logger,
        IClock? clock = null) =>
        new(manifest, session: null, options, logger, clock);

    /// <summary>
    /// Health folds in two things the base class cannot know: whether the symbol master has been
    /// ingested, and how many of its rows were not kept.
    ///
    /// Both are reported as detail on an otherwise healthy connector, on purpose. An un-ingested
    /// master still permits NSE trading via the structural symbol fallback, and skipped rows
    /// affect only the specific instruments involved. Marking the whole connector unhealthy for
    /// either would take a working broker link offline over a reference-data problem.
    /// </summary>
    public override async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseHealth = await base.CheckHealthAsync(ct).ConfigureAwait(false);
        if (baseHealth.IsFailure)
        {
            return baseHealth;
        }

        var health = baseHealth.Value;

        if (!Instruments.IsLoaded)
        {
            return health with
            {
                Detail = Join(
                    health.Detail,
                    "Symbol master not yet ingested; symbol resolution is using the structural fallback, "
                    + "which cannot resolve BSE cash series or monthly derivative expiries."),
            };
        }

        if (Instruments.SkippedRows > 0)
        {
            return health with
            {
                Detail = Join(
                    health.Detail,
                    $"{Instruments.SkippedRows} symbol-master rows were not kept "
                    + $"({Instruments.Count} loaded). Instrument types this connector does not trade "
                    + "are counted here, so a non-zero value is expected."),
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

        await _api.DisposeAsync().ConfigureAwait(false);
        Instruments.Dispose();

        // The base suppresses finalization; doing it here as well would be harmless but would
        // hide the fact that this type is expected to chain.
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
