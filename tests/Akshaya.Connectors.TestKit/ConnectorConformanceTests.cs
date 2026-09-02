using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connectors.TestKit;

/// <summary>
/// One recorded vendor failure and the canonical code it must become.
///
/// Fixtures rather than live calls, because the whole point of the error-mapping layer is that
/// it can be verified without a broker: vendors add codes without telling anyone, and the only
/// way to keep up is to paste the payload into a fixture and let CI hold the line.
/// </summary>
/// <param name="Name">Shown in the assertion message. Say what the broker was complaining about.</param>
/// <param name="Vendor">The failure as the connector saw it.</param>
/// <param name="ExpectedCanonicalCode">The <see cref="ConnectorErrorCodes"/> member it must map to.</param>
public sealed record VendorErrorFixture(
    string Name,
    VendorErrorContext Vendor,
    string ExpectedCanonicalCode);

/// <summary>
/// The suite every connector must pass.
///
/// A connector's test project subclasses this and fills in the abstract members; it inherits
/// every test below. That is the mechanism that makes "plug and play" mean something stronger
/// than "it compiles": a broker integration is not done when it returns data, it is done when
/// it behaves the way the manifest says it does, fails the way the core expects, and does not
/// leak.
///
/// Each test carries a note about the REAL bug it catches. A conformance test whose failure
/// nobody can interpret gets deleted the first time it goes red on a Friday.
/// </summary>
public abstract class ConnectorConformanceTests
{
    /// <summary>The manifest under test. Usually loaded through <c>ManifestLoader</c> from the shipped file.</summary>
    protected abstract ConnectorManifest Manifest { get; }

    /// <summary>The suite's clock. Manual, so nothing here depends on when CI happens to run.</summary>
    protected abstract ManualClock Clock { get; }

    /// <summary>Builds a connector bound to <paramref name="session"/>, or unbound when null.</summary>
    protected abstract IBrokerConnector CreateConnector(BrokerSession? session);

    /// <summary>A session that is comfortably alive on <see cref="Clock"/>.</summary>
    protected abstract BrokerSession CreateValidSession();

    /// <summary>A session that has already died on <see cref="Clock"/>.</summary>
    protected abstract BrokerSession CreateExpiredSession();

    /// <summary>The connector's symbol translator.</summary>
    protected abstract ISymbolTranslator Symbols { get; }

    /// <summary>Instruments this broker really trades. At least two, ideally in different asset classes.</summary>
    protected abstract IReadOnlyList<InstrumentKey> SampleInstruments { get; }

    /// <summary>Native symbols this broker does NOT know. Must not resolve to anything.</summary>
    protected abstract IReadOnlyList<string> UnknownNativeSymbols { get; }

    /// <summary>A canonical instrument this broker cannot trade.</summary>
    protected abstract InstrumentKey UnknownInstrument { get; }

    /// <summary>Recorded vendor failures and their expected canonical codes.</summary>
    protected abstract IReadOnlyList<VendorErrorFixture> VendorErrorFixtures { get; }

    /// <summary>Runs the connector's real error-normalisation path over one fixture.</summary>
    protected abstract Error NormaliseVendorError(VendorErrorContext context);

    /// <summary>
    /// Builds a valid order for this broker with the given capabilities. Implementations must
    /// supply whatever prices the order type needs, so that an undeclared capability fails with
    /// NotSupported rather than with a missing-price InvalidRequest.
    /// </summary>
    protected abstract PlaceOrderRequest BuildOrder(
        InstrumentKey instrument,
        OrderType orderType,
        TimeInForce timeInForce,
        PositionEffect positionEffect,
        Guid? clientOrderId = null);

    /// <summary>
    /// Makes the connector's next Place behave like a lost response: the order is created
    /// upstream and the caller gets a timeout.
    /// </summary>
    protected abstract void ArmPlaceTimeout(IBrokerConnector connector);

    /// <summary>
    /// Upstream connections the connector currently holds, when it can report them. Null means
    /// "cannot say", and the leak assertion is skipped — with a real risk noted in the test.
    /// </summary>
    protected virtual int? UpstreamSubscriptions(IBrokerConnector connector) => null;

    /// <summary>The first declared order type. Used as the neutral value when varying something else.</summary>
    private OrderType DeclaredOrderType => Manifest.Orders.Types[0];

    /// <summary>The first declared time-in-force.</summary>
    private TimeInForce DeclaredTimeInForce => Manifest.Orders.TimeInForce[0];

    /// <summary>The first declared position effect.</summary>
    private PositionEffect DeclaredPositionEffect => Manifest.Orders.PositionEffects[0];

    private InstrumentKey PrimaryInstrument => SampleInstruments[0];

    // =================================================================================
    // 1. Manifest self-consistency
    // =================================================================================

    /// <summary>
    /// CATCHES: a manifest that promises something it cannot describe.
    ///
    /// Every one of these has a specific, expensive failure behind it. A basket declared
    /// supported with maxLegs 0 makes the UI offer a basket ticket that rejects every basket.
    /// Streaming declared with no stream modes gives the fan-out layer nothing to subscribe
    /// with, so the socket connects and stays silent — which reads as healthy. Gateway hosting
    /// with no gateway spec leaves the supervisor with nothing to start, and the connector
    /// simply never authenticates. And "NSE" where the MIC "XNSE" was expected does not fail
    /// loudly at all: it silently makes every NSE instrument untradable through that broker,
    /// and someone spends a day finding out why.
    /// </summary>
    [Fact]
    public void Manifest_is_self_consistent()
    {
        // The loader is the authority; running it here means a connector cannot pass the suite
        // with a manifest the host would refuse to load.
        var validation = ManifestLoader.Validate(Manifest);
        validation.IsSuccess.Should().BeTrue(
            "the shipped manifest must load: {0}", validation.Error.Message);

        if (Manifest.Orders.Basket.Supported)
        {
            Manifest.Orders.Basket.MaxLegs.Should().BePositive(
                "a supported basket with no leg limit cannot be validated against");
        }

        if (Manifest.MarketData.Streaming)
        {
            Manifest.MarketData.StreamModes.Should().NotBeEmpty(
                "the fan-out layer has no way to subscribe without a mode");
        }
        else
        {
            Manifest.MarketData.StreamModes.Should().BeEmpty(
                "declaring modes on a non-streaming connector promises a feed that does not exist");
        }

        if (Manifest.Hosting == ConnectorHosting.Gateway)
        {
            Manifest.Gateway.Should().NotBeNull(
                "the supervisor has nothing to start or probe without a gateway spec");
        }
        else
        {
            Manifest.Gateway.Should().BeNull(
                "a gateway spec on a non-gateway connector is configuration nothing reads");
        }

        foreach (var venue in Manifest.Venues)
        {
            venue.Should().MatchRegex("^[A-Z0-9]{4}$",
                "venues are ISO 10383 MICs: 'XNSE' not 'NSE', 'XNAS' not 'NASDAQ'");
        }

        foreach (var currency in Manifest.Currencies)
        {
            currency.Should().MatchRegex("^[A-Z]{3}$",
                "currencies are 3-letter ISO 4217 codes");
        }

        Manifest.Orders.PositionEffects.Should().NotContain(PositionEffect.None,
            "None is the absence of a product type, not a product type");
    }

    // =================================================================================
    // 2. Declared versus actual
    // =================================================================================

    /// <summary>
    /// CATCHES: a manifest that over-promises.
    ///
    /// The order ticket renders from the manifest, so a declared-but-unimplemented order type
    /// becomes a button the trader can press that always fails. Worse, the risk gate validates
    /// against the manifest too, so it waves the order through to a connector that cannot send
    /// it — and the trader learns about the gap from a rejection at the moment they were
    /// relying on it working.
    /// </summary>
    [Fact]
    public async Task Every_capability_the_manifest_declares_is_actually_accepted()
    {
        await using var connector = CreateConnector(CreateValidSession());

        foreach (var orderType in Manifest.Orders.Types)
        {
            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, orderType, DeclaredTimeInForce, DeclaredPositionEffect));

            AssertNotDeclined(result, $"order type {orderType}");
        }

        foreach (var timeInForce in Manifest.Orders.TimeInForce)
        {
            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, DeclaredOrderType, timeInForce, DeclaredPositionEffect));

            AssertNotDeclined(result, $"time-in-force {timeInForce}");
        }

        foreach (var positionEffect in Manifest.Orders.PositionEffects)
        {
            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, DeclaredOrderType, DeclaredTimeInForce, positionEffect));

            AssertNotDeclined(result, $"position effect {positionEffect}");
        }
    }

    /// <summary>
    /// CATCHES: the two ways a connector can be wrong about a capability it does NOT have.
    ///
    /// THROWING turns a known capability gap into an incident: the host's resilience decorator
    /// sees an exception, not a Result, and a page goes out for something the UI should simply
    /// have rendered as a disabled control.
    ///
    /// SILENTLY SUBSTITUTING is far worse and is the reason this test exists. A connector that
    /// receives a Stop order it cannot send, and quietly sends a Market order instead, has
    /// turned a protective order into an immediate execution at whatever the market is doing.
    /// The trader believes they have a stop. They do not. Nothing anywhere reports a problem.
    /// </summary>
    [Fact]
    public async Task Every_capability_the_manifest_omits_is_refused_with_NotSupported()
    {
        await using var connector = CreateConnector(CreateValidSession());

        foreach (var orderType in Enum.GetValues<OrderType>())
        {
            if (Manifest.Orders.Supports(orderType))
            {
                continue;
            }

            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, orderType, DeclaredTimeInForce, DeclaredPositionEffect));

            AssertDeclined(result, $"undeclared order type {orderType}");
        }

        foreach (var timeInForce in Enum.GetValues<TimeInForce>())
        {
            if (Manifest.Orders.Supports(timeInForce))
            {
                continue;
            }

            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, DeclaredOrderType, timeInForce, DeclaredPositionEffect));

            AssertDeclined(result, $"undeclared time-in-force {timeInForce}");
        }

        foreach (var positionEffect in Enum.GetValues<PositionEffect>())
        {
            if (Manifest.Orders.Supports(positionEffect))
            {
                continue;
            }

            var result = await connector.Orders.PlaceAsync(
                BuildOrder(PrimaryInstrument, DeclaredOrderType, DeclaredTimeInForce, positionEffect));

            AssertDeclined(result, $"undeclared position effect {positionEffect}");
        }
    }

    // =================================================================================
    // 3. Symbol round-trip
    // =================================================================================

    /// <summary>
    /// CATCHES: a symbology that does not survive a round trip.
    ///
    /// Translation runs in both directions on every order's life: canonical to native on the
    /// way out, native back to canonical when the fill comes back on the socket. If those two
    /// disagree, the fill is booked against a different instrument than the order — the
    /// position is wrong, the P&amp;L is wrong, and the next order sized off that position is
    /// wrong too. It is silent, and it compounds.
    /// </summary>
    [Fact]
    public void Symbol_translation_round_trips_across_the_sample_set()
    {
        SampleInstruments.Should().NotBeEmpty("the suite needs instruments to translate");

        foreach (var instrument in SampleInstruments)
        {
            var native = Symbols.ToNative(instrument);
            native.IsSuccess.Should().BeTrue(
                "{0} is a sample instrument this broker trades: {1}",
                instrument,
                native.IsSuccess ? string.Empty : native.Error.Message);

            var canonical = Symbols.ToCanonical(native.Value);
            canonical.IsSuccess.Should().BeTrue(
                "'{0}' came out of ToNative and must go back in: {1}",
                native.Value,
                canonical.IsSuccess ? string.Empty : canonical.Error.Message);

            canonical.Value.Should().Be(instrument,
                "translation must be lossless in both directions, or fills book against the wrong instrument");
        }
    }

    /// <summary>
    /// CATCHES: a translator that guesses.
    ///
    /// A guessed symbol is an order on the wrong instrument, which is the most expensive single
    /// mistake this codebase can make. "Not found" is always the correct answer to "I do not
    /// recognise this", and it must arrive as InstrumentNotFound so the caller can tell it apart
    /// from a transport failure it would otherwise retry.
    /// </summary>
    [Fact]
    public void Unknown_symbols_resolve_to_InstrumentNotFound_and_are_never_guessed()
    {
        foreach (var unknown in UnknownNativeSymbols)
        {
            var canonical = Symbols.ToCanonical(unknown);

            canonical.IsFailure.Should().BeTrue("'{0}' is not a symbol this broker knows", unknown);
            canonical.Error.Code.Should().Be(ConnectorErrorCodes.InstrumentNotFound,
                "'{0}' must be reported as not found, not as a transport error the caller would retry",
                unknown);
        }

        var native = Symbols.ToNative(UnknownInstrument);
        native.IsFailure.Should().BeTrue("{0} is not tradable through this broker", UnknownInstrument);
        native.Error.Code.Should().Be(ConnectorErrorCodes.InstrumentNotFound);
    }

    // =================================================================================
    // 4. Error normalisation
    // =================================================================================

    /// <summary>
    /// CATCHES: a mis-mapped vendor error, in both directions.
    ///
    /// Map something to RateLimited that is not, and the host's resilience decorator RETRIES
    /// it — and a retried order placement is a duplicate order. Map a genuine session expiry to
    /// Unknown, and the user gets an unhelpful error instead of a re-auth prompt, and keeps
    /// clicking a button that cannot work.
    ///
    /// The second half — vendor code and message preserved — is what support lives on. Once the
    /// canonical code is all that survives, "what did the broker actually say" becomes
    /// unanswerable, and every ticket turns into a request to reproduce the failure live.
    /// </summary>
    [Fact]
    public void Vendor_errors_map_to_canonical_codes_and_keep_the_vendor_detail()
    {
        VendorErrorFixtures.Should().NotBeEmpty(
            "a connector with no recorded vendor failures has an untested error path");

        foreach (var fixture in VendorErrorFixtures)
        {
            var error = NormaliseVendorError(fixture.Vendor);

            error.Code.Should().Be(fixture.ExpectedCanonicalCode,
                "fixture '{0}' must normalise correctly; a wrong code changes whether this is retried",
                fixture.Name);

            if (!string.IsNullOrEmpty(fixture.Vendor.VendorCode))
            {
                error.VendorCode.Should().Be(fixture.Vendor.VendorCode,
                    "fixture '{0}' must carry the broker's own code through for support",
                    fixture.Name);
            }

            if (!string.IsNullOrEmpty(fixture.Vendor.VendorMessage))
            {
                error.VendorMessage.Should().Be(fixture.Vendor.VendorMessage,
                    "fixture '{0}' must carry the broker's own message through verbatim",
                    fixture.Name);
            }

            error.Message.Should().NotBeNullOrWhiteSpace(
                "the trader reads this sentence first, and it must be in the platform's voice");
        }
    }

    // =================================================================================
    // 5. Idempotency and reconciliation
    // =================================================================================

    /// <summary>
    /// CATCHES: the duplicate-order bug, which is the most expensive failure in this codebase.
    ///
    /// A place that times out has NOT necessarily failed. The order may be live at the venue
    /// and only the response lost. The naive recovery — retry the place — doubles the position,
    /// with real money, and the trader finds out from a fill they did not ask for. The correct
    /// recovery is to read the order book back and match on ClientOrderId, which only works if
    /// the connector actually carries ClientOrderId onto the order it returns.
    ///
    /// This test simulates the lost response, then reconciles, and demands exactly one match.
    /// It deliberately does NOT retry the placement: that is the behaviour being ruled out.
    /// </summary>
    [Fact]
    public async Task A_timed_out_place_reconciles_to_exactly_one_order()
    {
        await using var connector = CreateConnector(CreateValidSession());

        var clientOrderId = Guid.NewGuid();
        var request = BuildOrder(
            PrimaryInstrument,
            DeclaredOrderType,
            DeclaredTimeInForce,
            DeclaredPositionEffect,
            clientOrderId);

        ArmPlaceTimeout(connector);

        var placed = await connector.Orders.PlaceAsync(request);

        placed.IsFailure.Should().BeTrue("the simulated broker did not answer");
        placed.Error.Code.Should().Be(ConnectorErrorCodes.Timeout,
            "a lost response is a Timeout, not an Unknown — the caller's recovery path branches on it");
        ConnectorErrorCodes.IsRetryable(placed.Error.Code).Should().BeTrue(
            "a timeout is retryable in general; it is order PLACEMENT that the caller must reconcile instead");

        // The recovery: read the book, do not re-send.
        var book = await connector.Orders.GetOrdersAsync(new OrderQuery());
        book.IsSuccess.Should().BeTrue("reconciliation is impossible if the order book cannot be read");

        var matches = book.Value.Where(o => o.ClientOrderId == clientOrderId).ToList();

        matches.Should().ContainSingle(
            "the order was created upstream exactly once; zero matches means ClientOrderId was dropped "
            + "and reconciliation is impossible, more than one means the connector duplicated it");

        matches[0].Instrument.Should().Be(request.Instrument);
        matches[0].Side.Should().Be(request.Side);
    }

    // =================================================================================
    // 6. Session lifecycle
    // =================================================================================

    /// <summary>
    /// CATCHES: a dead session that fails as something else, or does not fail at all.
    ///
    /// The failure this prevents is specific: the platform believes a session is alive, uses it
    /// to place an order, and the order silently does not happen — at exactly the moment the
    /// trader is depending on it. ReauthRequired is the code the UI turns into a "sign in
    /// again" prompt; anything else (Unknown, BrokerUnavailable, or worst of all a success with
    /// empty data) leaves the user with no way to fix it.
    /// </summary>
    [Fact]
    public async Task An_expired_session_fails_every_call_with_ReauthRequired()
    {
        await using var connector = CreateConnector(CreateExpiredSession());

        var positions = await connector.Portfolio.GetPositionsAsync();
        positions.IsFailure.Should().BeTrue(
            "a dead session must not return data; empty data would be aggregated as 'flat'");
        positions.Error.Code.Should().Be(ConnectorErrorCodes.ReauthRequired);

        var placed = await connector.Orders.PlaceAsync(
            BuildOrder(PrimaryInstrument, DeclaredOrderType, DeclaredTimeInForce, DeclaredPositionEffect));

        placed.IsFailure.Should().BeTrue();
        placed.Error.Code.Should().Be(ConnectorErrorCodes.ReauthRequired,
            "the trader must be told to sign in, not shown a generic failure");

        var health = await connector.CheckHealthAsync();
        health.IsSuccess.Should().BeTrue("health must answer even when the session is dead");
        health.Value.SessionValid.Should().BeFalse();
        health.Value.IsHealthy.Should().BeFalse();
    }

    /// <summary>
    /// CATCHES: a refresh capability the manifest and the code disagree about.
    ///
    /// Both directions are live failures. Manifest says yes, code says no: the session monitor
    /// keeps trying to refresh, never succeeds, and never falls through to prompting the user —
    /// so the session quietly stops working with no dialog. Manifest says no, code says yes: the
    /// platform prompts for a full interactive re-login every time, for a broker that could have
    /// refreshed silently, and the user is trained to expect constant re-authentication.
    /// </summary>
    [Fact]
    public async Task Refresh_is_supported_exactly_when_the_manifest_says_so()
    {
        var session = CreateValidSession();
        await using var connector = CreateConnector(session);

        var refreshed = await connector.Auth.RefreshAsync(session);

        if (Manifest.Auth.RefreshSupported)
        {
            refreshed.IsSuccess.Should().BeTrue(
                "the manifest declares refreshSupported: the session monitor will call this and must not "
                + "be left retrying something that cannot work. Error was: {0}",
                refreshed.IsSuccess ? string.Empty : refreshed.Error.ToString());

            refreshed.Value.ConnectorId.Should().Be(session.ConnectorId);
            refreshed.Value.ExpiresAt.Should().BeAfter(Clock.UtcNow,
                "a refresh that returns an already-dead session is not a refresh");
        }
        else
        {
            refreshed.IsFailure.Should().BeTrue();
            refreshed.Error.Code.Should().Be(ConnectorErrorCodes.NotSupported,
                "the manifest declares no refresh, so this must be a capability statement the session "
                + "monitor stops asking about — not a transient failure it retries");
        }
    }

    // =================================================================================
    // 7. Streaming lifecycle
    // =================================================================================

    /// <summary>
    /// CATCHES: a leaked upstream subscription, and a stream that exists when it should not.
    ///
    /// The leak is invisible while it matters least. Every reconnect adds a subscription the
    /// connector no longer tracks; data keeps flowing, the UI stays green, and the account
    /// slowly approaches the broker's subscription quota. When it crosses, the broker drops the
    /// WHOLE connection — typically days later, typically mid-session, and with nothing in the
    /// logs pointing back at the reconnect that caused it.
    ///
    /// The null-stream half catches the opposite mistake: a connector with no feed that returns
    /// a stream object anyway. The contract says callers handle null; a non-null stand-in makes
    /// a caller that forgot to null-check appear correct, and that caller then breaks on the
    /// next broker that follows the contract.
    /// </summary>
    [Fact]
    public async Task Streaming_reconnects_without_leaking_upstream_subscriptions()
    {
        await using var connector = CreateConnector(CreateValidSession());

        if (!Manifest.MarketData.Streaming)
        {
            connector.Stream.Should().BeNull(
                "the manifest declares no live feed, and the contract's answer for that is a null Stream");
            return;
        }

        var stream = connector.Stream;
        stream.Should().NotBeNull("the manifest declares streaming, so a feed must exist");

        (await stream!.ConnectAsync()).IsSuccess.Should().BeTrue();
        stream.State.Should().Be(StreamState.Connected);

        (await stream.SubscribeAsync(SampleInstruments, Manifest.MarketData.StreamModes[0]))
            .IsSuccess.Should().BeTrue();

        (await stream.UnsubscribeAsync(SampleInstruments)).IsSuccess.Should().BeTrue();
        (await stream.DisconnectAsync()).IsSuccess.Should().BeTrue();
        stream.State.Should().Be(StreamState.Disconnected);

        // Reconnect twice. One reconnect can hide an off-by-one; two cannot.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await stream.ConnectAsync()).IsSuccess.Should().BeTrue();
            (await stream.SubscribeAsync(SampleInstruments, Manifest.MarketData.StreamModes[0]))
                .IsSuccess.Should().BeTrue();
            (await stream.UnsubscribeAsync(SampleInstruments)).IsSuccess.Should().BeTrue();
            (await stream.DisconnectAsync()).IsSuccess.Should().BeTrue();
        }

        // Connecting an already-connected stream must not open a second one either. A
        // supervisor calls Connect speculatively, and that is the other way the leak happens.
        (await stream.ConnectAsync()).IsSuccess.Should().BeTrue();
        (await stream.ConnectAsync()).IsSuccess.Should().BeTrue();

        var held = UpstreamSubscriptions(connector);
        if (held is { } count)
        {
            count.Should().BeLessThanOrEqualTo(1,
                "after three connect/disconnect cycles and a duplicate connect, the connector must hold "
                + "at most one upstream subscription; anything more accumulates until the broker drops "
                + "the whole connection");
        }

        (await stream.DisconnectAsync()).IsSuccess.Should().BeTrue();

        var afterDisconnect = UpstreamSubscriptions(connector);
        if (afterDisconnect is { } remaining)
        {
            remaining.Should().Be(0, "a disconnect must release what it took");
        }
    }

    // =================================================================================
    // 8. Money discipline
    // =================================================================================

    /// <summary>
    /// CATCHES: a currency the connector invented.
    ///
    /// Money that carries the wrong currency does not throw where it is created; it throws
    /// somewhere else entirely, when the Portfolio module tries to add it to a balance in a
    /// different currency — or worse, it does not throw at all because the wrong currency
    /// happens to match, and the number is silently converted at no rate. A connector that
    /// defaults an unknown currency to USD, or to whatever the first balance was, produces a
    /// blended portfolio that is wrong by an FX rate and looks completely plausible.
    ///
    /// The manifest is the authority: if a currency was not declared, it must not appear.
    /// </summary>
    [Fact]
    public async Task Every_Money_returned_carries_a_currency_the_manifest_declares()
    {
        await using var connector = CreateConnector(CreateValidSession());

        var declared = Manifest.Currencies.Select(c => new Currency(c)).ToHashSet();

        // Trade first, so positions, trades and a non-trivial balance exist to inspect. A
        // connector with nothing in it passes this test vacuously.
        var placed = await connector.Orders.PlaceAsync(
            BuildOrder(PrimaryInstrument, DeclaredOrderType, DeclaredTimeInForce, DeclaredPositionEffect));
        placed.IsSuccess.Should().BeTrue("the suite needs at least one order to inspect");

        var monies = new List<(string Source, Money Value)>();

        var balances = await connector.Portfolio.GetBalancesAsync();
        if (balances.IsSuccess)
        {
            foreach (var balance in balances.Value)
            {
                declared.Should().Contain(balance.Currency,
                    "a balance in an undeclared currency cannot be aggregated by the portfolio module");

                Collect(monies, "balance.AvailableToTrade", balance.AvailableToTrade);
                Collect(monies, "balance.CashBalance", balance.CashBalance);
                Collect(monies, "balance.UsedMargin", balance.UsedMargin);
                Collect(monies, "balance.AvailableMargin", balance.AvailableMargin);
                Collect(monies, "balance.RealisedPnl", balance.RealisedPnl);
                Collect(monies, "balance.UnrealisedPnl", balance.UnrealisedPnl);
            }
        }

        var positions = await connector.Portfolio.GetPositionsAsync();
        if (positions.IsSuccess)
        {
            foreach (var position in positions.Value)
            {
                Collect(monies, "position.AveragePrice", position.AveragePrice);
                Collect(monies, "position.LastPrice", position.LastPrice);
                Collect(monies, "position.UnrealisedPnl", position.UnrealisedPnl);
                Collect(monies, "position.RealisedPnl", position.RealisedPnl);
            }
        }

        var holdings = await connector.Portfolio.GetHoldingsAsync();
        if (holdings.IsSuccess)
        {
            foreach (var holding in holdings.Value)
            {
                Collect(monies, "holding.AveragePrice", holding.AveragePrice);
                Collect(monies, "holding.LastPrice", holding.LastPrice);
                Collect(monies, "holding.UnrealisedPnl", holding.UnrealisedPnl);
            }
        }

        var orders = await connector.Orders.GetOrdersAsync(new OrderQuery());
        if (orders.IsSuccess)
        {
            foreach (var order in orders.Value)
            {
                Collect(monies, "order.LimitPrice", order.LimitPrice);
                Collect(monies, "order.TriggerPrice", order.TriggerPrice);
                Collect(monies, "order.AveragePrice", order.AveragePrice);
            }
        }

        var trades = await connector.Orders.GetTradesAsync(new OrderQuery());
        if (trades.IsSuccess)
        {
            foreach (var trade in trades.Value)
            {
                Collect(monies, "trade.Price", trade.Price);
                Collect(monies, "trade.Charges", trade.Charges);
            }
        }

        foreach (var instrument in SampleInstruments)
        {
            var quote = await connector.MarketData.GetQuoteAsync(instrument);
            if (quote.IsFailure)
            {
                continue;
            }

            Collect(monies, "quote.LastPrice", quote.Value.LastPrice);
            Collect(monies, "quote.Open", quote.Value.Open);
            Collect(monies, "quote.High", quote.Value.High);
            Collect(monies, "quote.Low", quote.Value.Low);
            Collect(monies, "quote.PreviousClose", quote.Value.PreviousClose);
            Collect(monies, "quote.BidPrice", quote.Value.BidPrice);
            Collect(monies, "quote.AskPrice", quote.Value.AskPrice);
        }

        monies.Should().NotBeEmpty("a connector that returns no Money at all has not been exercised");

        foreach (var (source, money) in monies)
        {
            declared.Should().Contain(money.Currency,
                "{0} returned {1}, which the manifest does not declare",
                source,
                money.Currency);
        }
    }

    // =================================================================================
    // Shared assertions
    // =================================================================================

    private static void Collect(List<(string Source, Money Value)> sink, string source, Money? money)
    {
        if (money is { } value)
        {
            sink.Add((source, value));
        }
    }

    /// <summary>
    /// A declared capability must not be refused as unsupported. It may still fail for an
    /// honest reason — no funds, market closed — and that is not what this is checking.
    /// </summary>
    private static void AssertNotDeclined(Result<OrderAck> result, string capability)
    {
        if (result.IsSuccess)
        {
            return;
        }

        result.Error.Code.Should().NotBe(ConnectorErrorCodes.NotSupported,
            "the manifest declares {0}, so the order ticket offers it and the risk gate lets it "
            + "through; refusing it here makes that a button which always fails. Error: {1}",
            capability,
            result.Error.ToString());
    }

    /// <summary>An undeclared capability must be refused, as a capability statement rather than a failure.</summary>
    private static void AssertDeclined(Result<OrderAck> result, string capability)
    {
        result.IsFailure.Should().BeTrue(
            "{0} is not declared, and accepting it means the connector is either substituting a "
            + "different order type or lying about what it sent",
            capability);

        result.Error.Code.Should().Be(ConnectorErrorCodes.NotSupported,
            "{0} must be declined as a capability gap, not as a transport failure the host would retry. "
            + "Error: {1}",
            capability,
            result.Error.ToString());
    }
}
