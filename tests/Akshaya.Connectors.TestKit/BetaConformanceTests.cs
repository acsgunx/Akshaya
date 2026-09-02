using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.Connectors.TestKit.FakeConnectors;
using Akshaya.Connectors.TestKit.TestDoubles;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit;

/// <summary>
/// Runs the shared conformance suite against <see cref="BetaFakeConnector"/>: password+OTP,
/// rupees only, whole-lot quantities, no live feed, no refresh, venue-midnight expiry.
///
/// This class supplies nothing behavioural of its own — every assertion lives in
/// <see cref="ConnectorConformanceTests"/>. Its only job is wiring, which is deliberate: the
/// suite proves the abstraction by being identical for two very different brokers, and a
/// subclass that overrode or skipped a base test would defeat that proof.
/// </summary>
public sealed class BetaConformanceTests : ConnectorConformanceTests
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "FakeConnectors", "beta.connector.manifest.json");

    private readonly ConnectorManifest _manifest = LoadManifest();
    private readonly ManualClock _clock = TestClock.Frozen();

    /// <inheritdoc />
    protected override ConnectorManifest Manifest => _manifest;

    /// <inheritdoc />
    protected override ManualClock Clock => _clock;

    /// <inheritdoc />
    protected override ISymbolTranslator Symbols { get; } =
        new BetaSymbolTranslator(BetaFakeConnector.Universe());

    /// <inheritdoc />
    protected override IReadOnlyList<InstrumentKey> SampleInstruments { get; } =
        [.. BetaFakeConnector.Universe().Select(definition => definition.Key)];

    /// <inheritdoc />
    protected override IReadOnlyList<string> UnknownNativeSymbols { get; } =
        [
            // Well-formed by Beta's own convention, but not in the script master this fake was
            // built with.
            "TCS-EQ",
            "BANKNIFTY26SEPFUT",
            // The wrong shape entirely: no venue suffix at all.
            "not-a-beta-symbol",
        ];

    /// <inheritdoc />
    protected override InstrumentKey UnknownInstrument { get; } =
        new(Venue.Nse, "TCS", AssetClass.Equity);

    /// <inheritdoc />
    protected override IReadOnlyList<VendorErrorFixture> VendorErrorFixtures { get; } =
        [
            new(
                "Bad client code or password",
                new VendorErrorContext(400, "B-101", "Invalid client code or password.", "/login"),
                ConnectorErrorCodes.InvalidCredentials),
            new(
                "Wrong OTP",
                new VendorErrorContext(400, "B-102", "Invalid OTP.", "/login/otp"),
                ConnectorErrorCodes.ChallengeFailed),
            new(
                "Session expired mid-day",
                new VendorErrorContext(401, "B-201", "Session expired.", "/orders"),
                ConnectorErrorCodes.SessionExpired),
            new(
                "Order rejected by the exchange",
                new VendorErrorContext(400, "B-220", "Order rejected by exchange.", "/orders"),
                ConnectorErrorCodes.OrderRejected),
            new(
                "Market closed",
                new VendorErrorContext(400, "B-330", "Market is closed.", "/orders"),
                ConnectorErrorCodes.MarketClosed),
            new(
                "Insufficient funds",
                new VendorErrorContext(400, "B-440", "Insufficient funds.", "/orders"),
                ConnectorErrorCodes.InsufficientFunds),
            new(
                "RMS rejection by message phrase only",
                new VendorErrorContext(400, VendorCode: null, "RMS rejection: order value exceeds limit.", "/orders"),
                ConnectorErrorCodes.RiskRejected),
        ];

    /// <inheritdoc />
    protected override Error NormaliseVendorError(VendorErrorContext context)
    {
        var mapper = BetaFakeConnector.CreateErrorMapper();
        var canonical = mapper.MapToCanonicalCode(context) ?? ConnectorErrorCodes.Unknown;
        var message = mapper.DescribeCanonicalCode(canonical, context);
        return new Error(canonical, message, context.VendorCode, context.VendorMessage);
    }

    /// <inheritdoc />
    protected override IBrokerConnector CreateConnector(BrokerSession? session) =>
        new BetaFakeConnector(_manifest, session, _clock);

    /// <inheritdoc />
    protected override BrokerSession CreateValidSession() =>
        TestClock.ValidSession(BetaFakeConnector.ConnectorId, _clock, accountId: "BETA-1");

    /// <inheritdoc />
    protected override BrokerSession CreateExpiredSession() =>
        TestClock.ExpiredSession(BetaFakeConnector.ConnectorId, _clock, accountId: "BETA-1");

    /// <inheritdoc />
    protected override PlaceOrderRequest BuildOrder(
        InstrumentKey instrument,
        OrderType orderType,
        TimeInForce timeInForce,
        PositionEffect positionEffect,
        Guid? clientOrderId = null)
    {
        var reference = ReferencePriceFor(instrument);

        return new PlaceOrderRequest
        {
            ClientOrderId = clientOrderId ?? Guid.NewGuid(),
            Instrument = instrument,
            Side = Side.Buy,
            // Beta has no fractional quantities; a whole lot (or a whole share, for the equity
            // legs whose lot size is 1) keeps this fixture legal under Beta's own manifest.
            Quantity = new Quantity(instrument.AssetClass == AssetClass.Future ? 25m : 1m),
            OrderType = orderType,
            PositionEffect = positionEffect,
            TimeInForce = timeInForce,
            Variety = OrderVariety.Regular,
            LimitPrice = orderType == OrderType.Limit
                ? new Money(reference, Currency.Inr)
                : null,
            // Beta's manifest declares neither Stop nor StopLimit, so no trigger price is ever
            // needed here; the conformance suite only drives BuildOrder with types the manifest
            // itself declares as supported.
            TriggerPrice = null,
        };
    }

    /// <inheritdoc />
    protected override void ArmPlaceTimeout(IBrokerConnector connector) =>
        ((BetaFakeConnector)connector).Book.ArmPlaceTimeout();

    // No UpstreamSubscriptions override: Beta's Stream is always null (manifest says
    // streaming: false), so the base class's default of "no leak assertion" is correct here —
    // there is no upstream socket to leak.

    private static ConnectorManifest LoadManifest()
    {
        var result = ManifestLoader.LoadFromFile(ManifestPath);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"Beta's fixture manifest at '{ManifestPath}' failed to load: {result.Error}");
    }

    private static decimal ReferencePriceFor(InstrumentKey instrument) => instrument.Symbol switch
    {
        "INFY" => 1_540.25m,
        "RELIANCE" => 2_980.50m,
        "NIFTY" => 24_800m,
        _ => 100m,
    };
}
