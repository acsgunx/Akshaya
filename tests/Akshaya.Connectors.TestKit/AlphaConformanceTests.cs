using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.Connectors.TestKit.FakeConnectors;
using Akshaya.Connectors.TestKit.TestDoubles;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit;

/// <summary>
/// Runs the shared conformance suite against <see cref="AlphaFakeConnector"/>: OAuth2,
/// multi-currency (USD/SGD), fractional quantities, a live feed, bracket orders, refreshable
/// sessions, venue-midnight-free expiry.
///
/// This class supplies nothing behavioural of its own — every assertion lives in
/// <see cref="ConnectorConformanceTests"/>. Its only job is wiring, which is deliberate: the
/// suite proves the abstraction by being identical for two very different brokers, and a
/// subclass that overrode or skipped a base test would defeat that proof.
/// </summary>
public sealed class AlphaConformanceTests : ConnectorConformanceTests
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "FakeConnectors", "alpha.connector.manifest.json");

    private readonly ConnectorManifest _manifest = LoadManifest();
    private readonly ManualClock _clock = TestClock.Frozen();

    /// <inheritdoc />
    protected override ConnectorManifest Manifest => _manifest;

    /// <inheritdoc />
    protected override ManualClock Clock => _clock;

    /// <inheritdoc />
    protected override ISymbolTranslator Symbols { get; } = new AlphaSymbolTranslator();

    /// <inheritdoc />
    protected override IReadOnlyList<InstrumentKey> SampleInstruments { get; } =
        [.. AlphaFakeConnector.Universe().Select(definition => definition.Key)];

    /// <inheritdoc />
    protected override IReadOnlyList<string> UnknownNativeSymbols { get; } =
        [
            // A well-formed native symbol whose exchange Alpha does not know.
            "LSE:VOD:EQ",
            // A well-formed native symbol whose instrument kind Alpha does not know.
            "NASDAQ:MSFT:BOND",
            // The wrong shape entirely.
            "not-an-alpha-symbol",
        ];

    /// <inheritdoc />
    protected override InstrumentKey UnknownInstrument { get; } =
        new(Venue.Nyse, "IBM", AssetClass.Equity);

    /// <inheritdoc />
    protected override IReadOnlyList<VendorErrorFixture> VendorErrorFixtures { get; } =
        [
            new(
                "Expired access token",
                new VendorErrorContext(401, "AUTH_401", "The access token has expired.", "/orders"),
                ConnectorErrorCodes.SessionExpired),
            new(
                "Revoked API grant",
                new VendorErrorContext(403, "AUTH_403", "This application's access was revoked.", "/orders"),
                ConnectorErrorCodes.ReauthRequired),
            new(
                "Order-endpoint throttling",
                new VendorErrorContext(429, "RATE_429", "Too many requests.", "/orders"),
                ConnectorErrorCodes.RateLimited),
            new(
                "Insufficient funds by vendor code",
                new VendorErrorContext(400, "FUNDS_1001", "Account has insufficient funds.", "/orders"),
                ConnectorErrorCodes.InsufficientFunds),
            new(
                "Insufficient funds by message phrase only",
                new VendorErrorContext(400, VendorCode: null, "Order exceeds available buying power.", "/orders"),
                ConnectorErrorCodes.InsufficientFunds),
            new(
                "Unknown symbol",
                new VendorErrorContext(404, "SYM_404", "No such instrument.", "/orders"),
                ConnectorErrorCodes.InstrumentNotFound),
            new(
                "Order rejected by the venue",
                new VendorErrorContext(409, "ORD_409", "Duplicate client order id.", "/orders"),
                ConnectorErrorCodes.OrderRejected),
        ];

    /// <inheritdoc />
    protected override Error NormaliseVendorError(VendorErrorContext context)
    {
        var mapper = AlphaFakeConnector.CreateErrorMapper();
        var canonical = mapper.MapToCanonicalCode(context) ?? ConnectorErrorCodes.Unknown;
        var message = mapper.DescribeCanonicalCode(canonical, context);
        return new Error(canonical, message, context.VendorCode, context.VendorMessage);
    }

    /// <inheritdoc />
    protected override IBrokerConnector CreateConnector(BrokerSession? session) =>
        new AlphaFakeConnector(_manifest, session, _clock);

    /// <inheritdoc />
    protected override BrokerSession CreateValidSession() =>
        TestClock.ValidSession(AlphaFakeConnector.ConnectorId, _clock, accountId: "ALPHA-1");

    /// <inheritdoc />
    protected override BrokerSession CreateExpiredSession() =>
        TestClock.ExpiredSession(AlphaFakeConnector.ConnectorId, _clock, accountId: "ALPHA-1");

    /// <inheritdoc />
    protected override PlaceOrderRequest BuildOrder(
        InstrumentKey instrument,
        OrderType orderType,
        TimeInForce timeInForce,
        PositionEffect positionEffect,
        Guid? clientOrderId = null)
    {
        var currency = instrument.Venue == Venue.Sgx ? Currency.Sgd : Currency.Usd;
        var reference = ReferencePriceFor(instrument);

        return new PlaceOrderRequest
        {
            ClientOrderId = clientOrderId ?? Guid.NewGuid(),
            Instrument = instrument,
            Side = Side.Buy,
            // Alpha allows fractional quantities; a whole quantity still exercises every path
            // and keeps the test's intent about the field under test unambiguous.
            Quantity = new Quantity(10m),
            OrderType = orderType,
            PositionEffect = positionEffect,
            TimeInForce = timeInForce,
            Variety = OrderVariety.Regular,
            LimitPrice = orderType is OrderType.Limit or OrderType.StopLimit
                ? new Money(reference, currency)
                : null,
            TriggerPrice = orderType is OrderType.Stop or OrderType.StopLimit
                ? new Money(reference * 0.95m, currency)
                : null,
        };
    }

    /// <inheritdoc />
    protected override void ArmPlaceTimeout(IBrokerConnector connector) =>
        ((AlphaFakeConnector)connector).Book.ArmPlaceTimeout();

    /// <inheritdoc />
    protected override int? UpstreamSubscriptions(IBrokerConnector connector) =>
        ((AlphaFakeConnector)connector).FakeStream.UpstreamSubscriptions;

    private static ConnectorManifest LoadManifest()
    {
        var result = ManifestLoader.LoadFromFile(ManifestPath);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"Alpha's fixture manifest at '{ManifestPath}' failed to load: {result.Error}");
    }

    private static decimal ReferencePriceFor(InstrumentKey instrument) => instrument.Symbol switch
    {
        "AAPL" => 225.50m,
        "QQQ" => 480.25m,
        "D05" => 38.40m,
        _ => 100m,
    };
}
