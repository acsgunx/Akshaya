using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Domain.Rules;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Akshaya.Trading.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Akshaya.Trading.Tests;

// One test class per rule, as the ten rules are meant to be reviewed and tested in isolation.
// Each class proves both directions: a legitimate order gets through (permit) and the
// specific danger the rule exists for is actually stopped (block).

/// <summary>
/// PREVENTS: the 2012 Knight Capital shape of incident — a runaway strategy sending orders
/// nobody can stop without a deploy. The switch must work on the very next order.
/// </summary>
public sealed class KillSwitchRuleTests
{
    [Fact]
    public async Task Permits_an_order_when_the_switch_is_not_engaged()
    {
        var killSwitch = Substitute.For<IKillSwitch>();
        killSwitch.IsEngagedAsync("tenant-1", Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));
        var rule = new KillSwitchRule(killSwitch);

        var decision = await rule.EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_every_new_order_once_the_switch_is_engaged_and_names_who_and_why()
    {
        var killSwitch = Substitute.For<IKillSwitch>();
        killSwitch.IsEngagedAsync("tenant-1", Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        killSwitch.GetAsync("tenant-1", Arg.Any<CancellationToken>()).Returns(new ValueTask<KillSwitchState>(
            new KillSwitchState
            {
                TenantId = "tenant-1",
                IsEngaged = true,
                Reason = "Unexplained order burst",
                ChangedBy = "ops@example.com",
                ChangedAt = new DateTimeOffset(2026, 9, 2, 5, 0, 0, TimeSpan.Zero),
            }));
        var rule = new KillSwitchRule(killSwitch);

        var decision = await rule.EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be(RiskRuleNames.KillSwitch);
        decision.Reason.Should().Contain("Unexplained order burst");
        decision.Context["engagedBy"].Should().Be("ops@example.com");
    }
}

/// <summary>
/// PREVENTS: trading a restricted-list security or breaching a fund's own mandate. Deny must
/// always beat allow, so an incident restriction cannot be defeated by a stale permission.
/// </summary>
public sealed class InstrumentAllowDenyRuleTests
{
    private static readonly IRiskRule Rule = new InstrumentAllowDenyRule();

    [Fact]
    public async Task Permits_an_instrument_when_no_lists_are_configured()
    {
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context());
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_an_instrument_on_the_deny_list()
    {
        var policy = RiskFixtures.Policy(deniedInstruments: new HashSet<string>(StringComparer.Ordinal)
        {
            RiskFixtures.Infy.ToString(),
        });

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be(RiskRuleNames.InstrumentAllowDenyList);
    }

    [Fact]
    public async Task Blocks_an_instrument_absent_from_a_non_empty_allow_list()
    {
        var policy = RiskFixtures.Policy(allowedInstruments: new HashSet<string>(StringComparer.Ordinal)
        {
            RiskFixtures.NiftyFuture.ToString(), // INFY is not here.
        });

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_an_instrument_on_the_allow_list()
    {
        var policy = RiskFixtures.Policy(allowedInstruments: new HashSet<string>(StringComparer.Ordinal)
        {
            RiskFixtures.Infy.ToString(),
        });

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Deny_always_beats_allow_when_an_instrument_is_on_both_lists()
    {
        var key = RiskFixtures.Infy.ToString();
        var policy = RiskFixtures.Policy(
            allowedInstruments: new HashSet<string>(StringComparer.Ordinal) { key },
            deniedInstruments: new HashSet<string>(StringComparer.Ordinal) { key });

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeFalse();
    }
}

/// <summary>
/// PREVENTS: the silent capability mismatch — a broker's API quietly downgrading an order
/// (dropping a stop leg, collapsing a GTC into a Day order) instead of refusing it outright.
/// </summary>
public sealed class CapabilitySupportedRuleTests
{
    private static readonly IRiskRule Rule = new CapabilitySupportedRule();

    [Fact]
    public async Task Permits_an_order_the_manifest_fully_supports()
    {
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context());
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_venue_the_manifest_does_not_reach()
    {
        var manifest = RiskFixtures.Manifest(venues: [Venue.Bse]);
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest));

        decision.IsAllowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(ConnectorErrorCodes.NotSupported);
        decision.Context["field"].Should().Be("venue");
    }

    [Fact]
    public async Task Blocks_an_asset_class_the_manifest_does_not_trade()
    {
        var manifest = RiskFixtures.Manifest(assetClasses: [AssetClass.Future]);
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest)); // request is Equity

        decision.IsAllowed.Should().BeFalse();
        decision.Context["field"].Should().Be("assetClass");
    }

    [Fact]
    public async Task Blocks_an_order_type_the_manifest_does_not_offer()
    {
        var manifest = RiskFixtures.Manifest(orderTypes: [OrderType.Market]);
        var request = RiskFixtures.Order(orderType: OrderType.StopLimit);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.Context["field"].Should().Be("orderType");
    }

    [Fact]
    public async Task Blocks_a_time_in_force_the_manifest_does_not_offer()
    {
        var manifest = RiskFixtures.Manifest(timeInForce: [TimeInForce.Day]);
        var request = RiskFixtures.Order(timeInForce: TimeInForce.Gtc);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.Context["field"].Should().Be("timeInForce");
    }

    [Fact]
    public async Task Blocks_a_position_effect_the_manifest_does_not_offer()
    {
        var manifest = RiskFixtures.Manifest(positionEffects: [PositionEffect.Intraday]);
        var request = RiskFixtures.Order(positionEffect: PositionEffect.Delivery);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.Context["field"].Should().Be("positionEffect");
    }

    [Fact]
    public async Task Blocks_a_variety_the_manifest_does_not_offer()
    {
        var manifest = RiskFixtures.Manifest(varieties: [OrderVariety.Regular]);
        var request = RiskFixtures.Order(variety: OrderVariety.Bracket);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.Context["field"].Should().Be("variety");
    }
}

/// <summary>
/// PREVENTS: the truncation surprise — a broker rounding a fractional or off-lot quantity to
/// something the trader never asked for instead of rejecting it.
/// </summary>
public sealed class FractionalQuantityRuleTests
{
    private static readonly IRiskRule Rule = new FractionalQuantityRule();

    [Fact]
    public async Task Permits_a_whole_quantity_when_the_broker_forbids_fractions()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: false);
        var request = RiskFixtures.Order(quantity: 5m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_a_fractional_quantity_when_the_broker_allows_fractions()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: true);
        var request = RiskFixtures.Order(quantity: 0.5m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_fractional_quantity_when_the_broker_forbids_fractions()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: false);
        var request = RiskFixtures.Order(quantity: 0.5m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(ConnectorErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Blocks_a_quantity_that_is_not_a_whole_number_of_lots()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: false);
        // NIFTY futures trade in lots of 25; 30 is not a whole number of lots.
        var request = RiskFixtures.Order(instrument: RiskFixtures.NiftyFuture, quantity: 30m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            manifest: manifest,
            request: request,
            instrument: RiskFixtures.NiftyFutureDefinition));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_a_whole_number_of_lots()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: false);
        var request = RiskFixtures.Order(instrument: RiskFixtures.NiftyFuture, quantity: 50m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            manifest: manifest,
            request: request,
            instrument: RiskFixtures.NiftyFutureDefinition));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_fractional_disclosed_quantity_even_when_the_main_quantity_is_whole()
    {
        var manifest = RiskFixtures.Manifest(fractionalQuantity: false);
        var request = RiskFixtures.Order(quantity: 10m) with { DisclosedQuantity = new Quantity(2.5m) };

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(manifest: manifest, request: request));

        decision.IsAllowed.Should().BeFalse();
    }
}

/// <summary>
/// PREVENTS: the classic fat-finger — a digit typed twice turning "1 share" into "610,000
/// shares" (Mizuho, 2005).
/// </summary>
public sealed class MaxQuantityRuleTests
{
    private static readonly IRiskRule Rule = new MaxQuantityRule();

    [Fact]
    public async Task Permits_any_quantity_when_no_limit_is_configured()
    {
        var request = RiskFixtures.Order(quantity: 1_000_000m);
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(request: request));
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_a_quantity_at_or_below_the_limit()
    {
        var policy = RiskFixtures.Policy(maxQuantity: 100m);
        var request = RiskFixtures.Order(quantity: 100m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_quantity_over_the_limit()
    {
        var policy = RiskFixtures.Policy(maxQuantity: 100m);
        var request = RiskFixtures.Order(quantity: 610_000m);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be(RiskRuleNames.MaxQuantity);
    }
}

/// <summary>
/// PREVENTS: the overnight order nobody is watching — an order the venue cannot possibly act
/// on right now resting unexamined until it fires into a gap.
/// </summary>
public sealed class VenueMarketHoursRuleTests
{
    private static IRiskRule Rule(ITradingCalendar calendar) => new VenueMarketHoursRule(calendar);

    private static ITradingCalendar CalendarReturning(VenueState state, DateTimeOffset? nextOpen = null)
    {
        var calendar = Substitute.For<ITradingCalendar>();
        calendar.GetState(Arg.Any<Venue>(), Arg.Any<DateTimeOffset>()).Returns(state);
        calendar.NextOpen(Arg.Any<Venue>(), Arg.Any<DateTimeOffset>()).Returns(nextOpen);
        return calendar;
    }

    [Fact]
    public async Task Permits_an_order_while_the_venue_is_open()
    {
        var decision = await Rule(CalendarReturning(VenueState.Open))
            .EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_an_order_while_closed_when_the_tenant_has_opted_out_of_queuing()
    {
        var policy = RiskFixtures.Policy(allowOrdersWhenVenueClosed: false);

        var decision = await Rule(CalendarReturning(VenueState.Holiday))
            .EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(ConnectorErrorCodes.MarketClosed);
    }

    [Fact]
    public async Task Permits_an_order_while_closed_when_the_tenant_lets_the_broker_queue_it()
    {
        var policy = RiskFixtures.Policy(allowOrdersWhenVenueClosed: true);

        var decision = await Rule(CalendarReturning(VenueState.Closed))
            .EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_a_deliberate_after_market_order_the_broker_supports_even_when_opted_out()
    {
        var policy = RiskFixtures.Policy(allowOrdersWhenVenueClosed: false);
        var manifest = RiskFixtures.Manifest(varieties: [OrderVariety.Regular, OrderVariety.AfterMarket]);
        var request = RiskFixtures.Order(variety: OrderVariety.AfterMarket);

        var decision = await Rule(CalendarReturning(VenueState.PostClose))
            .EvaluateAsync(RiskFixtures.Context(manifest: manifest, policy: policy, request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_an_after_market_order_the_broker_does_not_actually_support()
    {
        var policy = RiskFixtures.Policy(allowOrdersWhenVenueClosed: false);
        var manifest = RiskFixtures.Manifest(varieties: [OrderVariety.Regular]); // no AfterMarket
        var request = RiskFixtures.Order(variety: OrderVariety.AfterMarket);

        var decision = await Rule(CalendarReturning(VenueState.PostClose))
            .EvaluateAsync(RiskFixtures.Context(manifest: manifest, policy: policy, request: request));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_a_deliberate_at_the_open_auction_order_during_pre_open_even_when_opted_out()
    {
        var policy = RiskFixtures.Policy(allowOrdersWhenVenueClosed: false);
        var request = RiskFixtures.Order(timeInForce: TimeInForce.AtTheOpen);

        var decision = await Rule(CalendarReturning(VenueState.PreOpen))
            .EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeTrue();
    }
}

/// <summary>
/// PREVENTS: a diversifying-out-of-control strategy — many small, individually-sane orders
/// that together leave the account unclosable inside one session.
/// </summary>
public sealed class MaxOpenPositionsRuleTests
{
    private static readonly IRiskRule Rule = new MaxOpenPositionsRule();

    [Fact]
    public async Task Permits_opening_a_position_when_no_limit_is_configured()
    {
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context());
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_opening_a_position_below_the_cap()
    {
        var policy = RiskFixtures.Policy(maxOpenPositions: 5);
        var snapshot = RiskFixtures.Context().Snapshot with { OpenPositionCount = 4 };

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_opening_a_new_position_once_the_cap_is_reached()
    {
        var policy = RiskFixtures.Policy(maxOpenPositions: 5);
        var snapshot = RiskFixtures.Context().Snapshot with { OpenPositionCount = 5 };

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Never_blocks_a_closing_order_even_at_the_cap()
    {
        var policy = RiskFixtures.Policy(maxOpenPositions: 5);
        var snapshot = RiskFixtures.Context().Snapshot with { OpenPositionCount = 5 };

        var decision = await Rule.EvaluateAsync(
            RiskFixtures.Context(policy: policy, snapshot: snapshot, isReducingExposure: true));

        decision.IsAllowed.Should().BeTrue();
    }
}

/// <summary>
/// PREVENTS: sizing that looks fine in units but is enormous in notional — a derivative's
/// contract multiplier turning a "small" quantity into a huge real exposure.
/// </summary>
public sealed class MaxOrderValueRuleTests
{
    private static IRiskRule Rule(IFxConverter fx) => new MaxOrderValueRule(fx);

    private static IFxConverter NeverCalledFx() => Substitute.For<IFxConverter>();

    [Fact]
    public async Task Permits_any_value_when_no_limit_is_configured()
    {
        var request = RiskFixtures.Order(quantity: 1_000_000m, orderType: OrderType.Limit, limitPrice: new Money(5_000m, Currency.Inr));
        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_an_order_that_cannot_be_valued_at_all()
    {
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(1_000_000m, Currency.Inr));
        var request = RiskFixtures.Order(orderType: OrderType.Market); // no limit/trigger price

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(
            RiskFixtures.Context(policy: policy, request: request, lastTradedPrice: null));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_a_same_currency_order_within_the_limit()
    {
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(1_000_000m, Currency.Inr));
        var request = RiskFixtures.Order(quantity: 10m, orderType: OrderType.Limit, limitPrice: new Money(1_500m, Currency.Inr));

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_same_currency_order_that_exceeds_the_limit()
    {
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(10_000m, Currency.Inr));
        var request = RiskFixtures.Order(quantity: 10m, orderType: OrderType.Limit, limitPrice: new Money(1_500m, Currency.Inr));

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_derivative_multiplier_is_applied_so_a_small_quantity_can_still_breach_the_limit()
    {
        // 2 lots of NIFTY futures at 24,800, multiplier 25 => notional = 2 * 24,800 * 25 = 1,240,000.
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(500_000m, Currency.Inr));
        var request = RiskFixtures.Order(
            instrument: RiskFixtures.NiftyFuture,
            quantity: 2m,
            orderType: OrderType.Limit,
            limitPrice: new Money(24_800m, Currency.Inr));

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            instrument: RiskFixtures.NiftyFutureDefinition));

        decision.IsAllowed.Should().BeFalse();
        decision.Context["multiplier"].Should().Be("25");
    }

    [Fact]
    public async Task Converts_a_cross_currency_notional_through_the_normalisation_rate_before_comparing()
    {
        // Naive comparison (6,000 USD vs 500,000 INR) would pass; the real rate makes it fail.
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(500_000m, Currency.Inr));
        var request = RiskFixtures.Order(quantity: 30m, orderType: OrderType.Limit, limitPrice: new Money(200m, Currency.Usd));

        var fx = Substitute.For<IFxConverter>();
        fx.ConvertAsync(new Money(6_000m, Currency.Usd), Currency.Inr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Money>.Success(new Money(498_000m, Currency.Inr))));

        var allowed = await Rule(fx).EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));
        allowed.IsAllowed.Should().BeTrue("498,000 INR is within the 500,000 INR limit");

        fx.ConvertAsync(new Money(6_000m, Currency.Usd), Currency.Inr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Money>.Success(new Money(510_000m, Currency.Inr))));

        var blocked = await Rule(fx).EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));
        blocked.IsAllowed.Should().BeFalse("510,000 INR breaches the 500,000 INR limit");
    }

    [Fact]
    public async Task Fails_closed_when_no_fx_rate_is_available_to_normalise_the_notional()
    {
        var policy = RiskFixtures.Policy(maxOrderValue: new Money(500_000m, Currency.Inr));
        var request = RiskFixtures.Order(quantity: 30m, orderType: OrderType.Limit, limitPrice: new Money(200m, Currency.Usd));

        var fx = Substitute.For<IFxConverter>();
        fx.ConvertAsync(Arg.Any<Money>(), Currency.Inr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Money>.Failure(new Error(ConnectorErrorCodes.Unknown, "No rate."))));

        var decision = await Rule(fx).EvaluateAsync(RiskFixtures.Context(policy: policy, request: request));

        decision.IsAllowed.Should().BeFalse();
    }
}

/// <summary>
/// PREVENTS: revenge trading and the death-spiral pattern — increasing size to win back a
/// loss until the account is wiped, at exactly the moment self-control is least available.
/// </summary>
public sealed class DailyLossLimitRuleTests
{
    private static IRiskRule Rule(IFxConverter fx) => new DailyLossLimitRule(fx);

    private static IFxConverter NeverCalledFx() => Substitute.For<IFxConverter>();

    [Fact]
    public async Task Permits_new_exposure_when_no_limit_is_configured()
    {
        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context());
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Never_blocks_a_closing_order_regardless_of_todays_pnl()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(1_000m, Currency.Inr));
        var snapshot = new RiskSnapshot
        {
            OpenPositionCount = 1,
            RealisedPnlToday = [new Money(-50_000m, Currency.Inr)],
        };

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(
            RiskFixtures.Context(policy: policy, snapshot: snapshot, isReducingExposure: true));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_closed_when_todays_pnl_snapshot_is_only_partial()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(1_000m, Currency.Inr));
        var snapshot = new RiskSnapshot { OpenPositionCount = 0, RealisedPnlToday = [], IsPartial = true };

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_new_exposure_when_todays_loss_is_within_the_limit()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(10_000m, Currency.Inr));
        var snapshot = new RiskSnapshot { OpenPositionCount = 1, RealisedPnlToday = [new Money(-5_000m, Currency.Inr)] };

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_new_exposure_on_a_profitable_day_regardless_of_the_limit()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(10_000m, Currency.Inr));
        var snapshot = new RiskSnapshot { OpenPositionCount = 1, RealisedPnlToday = [new Money(50_000m, Currency.Inr)] };

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_new_exposure_once_todays_loss_reaches_the_limit()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(10_000m, Currency.Inr));
        var snapshot = new RiskSnapshot { OpenPositionCount = 1, RealisedPnlToday = [new Money(-10_000m, Currency.Inr)] };

        var decision = await Rule(NeverCalledFx()).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be(RiskRuleNames.DailyLossLimit);
    }

    [Fact]
    public async Task Sums_multi_currency_losses_through_an_explicit_fx_rate_rather_than_adding_raw_numbers()
    {
        // -5,000 INR and a converted -60 USD leg (~ -5,000 INR at 83.33) must total roughly
        // -10,000 INR, breaching a 10,000 INR limit. Adding "5,000 + 60" directly would not.
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(9_000m, Currency.Inr));
        var snapshot = new RiskSnapshot
        {
            OpenPositionCount = 1,
            RealisedPnlToday = [new Money(-5_000m, Currency.Inr), new Money(-60m, Currency.Usd)],
        };

        var fx = Substitute.For<IFxConverter>();
        fx.ConvertAsync(new Money(-60m, Currency.Usd), Currency.Inr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Money>.Success(new Money(-5_000m, Currency.Inr))));

        var decision = await Rule(fx).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Fails_closed_when_a_leg_cannot_be_converted_to_the_normalisation_currency()
    {
        var policy = RiskFixtures.Policy(dailyLossLimit: new Money(10_000m, Currency.Inr));
        var snapshot = new RiskSnapshot
        {
            OpenPositionCount = 1,
            RealisedPnlToday = [new Money(-60m, Currency.Usd)],
        };

        var fx = Substitute.For<IFxConverter>();
        fx.ConvertAsync(Arg.Any<Money>(), Currency.Inr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Money>.Failure(new Error(ConnectorErrorCodes.Unknown, "No rate."))));

        var decision = await Rule(fx).EvaluateAsync(RiskFixtures.Context(policy: policy, snapshot: snapshot));

        decision.IsAllowed.Should().BeFalse();
    }
}

/// <summary>
/// PREVENTS: the fat-finger price — a decimal point in the wrong place executing instantly
/// against every resting order at the venue (Mizuho's J-Com order, 2005).
/// </summary>
public sealed class PriceBandSanityRuleTests
{
    private static readonly IRiskRule Rule = new PriceBandSanityRule();

    [Fact]
    public async Task Permits_any_price_when_no_band_is_configured()
    {
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(10_000m, Currency.Inr));
        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            request: request,
            lastTradedPrice: new Money(100m, Currency.Inr)));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Ignores_a_market_order_because_it_has_no_price_to_sanity_check()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 5m);
        var request = RiskFixtures.Order(orderType: OrderType.Market);

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            lastTradedPrice: new Money(100m, Currency.Inr)));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Permits_a_limit_price_within_the_band()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m);
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(1_045m, Currency.Inr));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            lastTradedPrice: new Money(1_000m, Currency.Inr)));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_the_classic_fat_finger_a_limit_price_an_order_of_magnitude_off()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m);
        // 1 share at 1 yen instead of 610,000 (Mizuho's shape): a price 99% below the market.
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(10m, Currency.Inr));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            lastTradedPrice: new Money(1_000m, Currency.Inr)));

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be(RiskRuleNames.PriceBandSanity);
    }

    [Fact]
    public async Task Also_checks_the_trigger_price_for_stop_orders()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m);
        var request = RiskFixtures.Order(orderType: OrderType.Stop, triggerPrice: new Money(10m, Currency.Inr));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            lastTradedPrice: new Money(1_000m, Currency.Inr)));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Permits_a_priced_order_by_default_when_no_live_quote_is_available()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m, rejectWhenPriceUnavailable: false);
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(1_000m, Currency.Inr));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, request: request, lastTradedPrice: null));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_a_priced_order_when_the_tenant_opted_into_rejecting_on_missing_quotes()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m, rejectWhenPriceUnavailable: true);
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(1_000m, Currency.Inr));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(policy: policy, request: request, lastTradedPrice: null));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Blocks_a_price_expressed_in_a_currency_that_does_not_match_the_last_traded_price()
    {
        var policy = RiskFixtures.Policy(priceBandPercent: 10m);
        var request = RiskFixtures.Order(orderType: OrderType.Limit, limitPrice: new Money(1_000m, Currency.Usd));

        var decision = await Rule.EvaluateAsync(RiskFixtures.Context(
            policy: policy,
            request: request,
            lastTradedPrice: new Money(1_000m, Currency.Inr)));

        decision.IsAllowed.Should().BeFalse();
    }
}

/// <summary>
/// The gate's own contract: sequential (not parallel), stops at the first denial, fails
/// closed when a rule throws, and honours per-tenant enabled/disabled rules.
/// </summary>
public sealed class RiskGateTests
{
    private sealed class ScriptedRule(string name, int order, Func<RiskDecision> decide) : IRiskRule
    {
        public int CallCount { get; private set; }

        public string Name => name;

        public int Order => order;

        public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(decide());
        }
    }

    private sealed class ThrowingRule(string name, int order) : IRiskRule
    {
        public string Name => name;

        public int Order => order;

        public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated failure inside a risk rule.");
    }

    [Fact]
    public async Task Allows_the_order_when_every_enabled_rule_allows_it()
    {
        var first = new ScriptedRule("First", 10, RiskDecision.Allow);
        var second = new ScriptedRule("Second", 20, RiskDecision.Allow);
        var gate = new RiskGate([first, second], NullLogger<RiskGate>.Instance);

        var decision = await gate.EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeTrue();
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Stops_at_the_first_denial_and_never_calls_a_later_rule()
    {
        var denier = new ScriptedRule("Denier", 10, () => RiskDecision.Deny("Denier", "no"));
        var neverCalled = new ScriptedRule("NeverCalled", 20, RiskDecision.Allow);
        var gate = new RiskGate([denier, neverCalled], NullLogger<RiskGate>.Instance);

        var decision = await gate.EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be("Denier");
        neverCalled.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Runs_rules_in_ascending_Order_regardless_of_constructor_argument_order()
    {
        var callSequence = new List<string>();
        var late = new ScriptedRule("Late", 90, () =>
        {
            callSequence.Add("Late");
            return RiskDecision.Allow();
        });
        var early = new ScriptedRule("Early", 10, () =>
        {
            callSequence.Add("Early");
            return RiskDecision.Allow();
        });

        // Deliberately constructed with the LATE rule first; the gate must still run cheapest
        // (lowest Order) first regardless of registration order.
        var gate = new RiskGate([late, early], NullLogger<RiskGate>.Instance);

        await gate.EvaluateAsync(RiskFixtures.Context());

        callSequence.Should().Equal("Early", "Late");
    }

    [Fact]
    public async Task Fails_closed_when_a_rule_throws_instead_of_letting_the_exception_escape_as_a_pass()
    {
        var thrower = new ThrowingRule("Thrower", 10);
        var neverCalled = new ScriptedRule("NeverCalled", 20, RiskDecision.Allow);
        var gate = new RiskGate([thrower, neverCalled], NullLogger<RiskGate>.Instance);

        var decision = await gate.EvaluateAsync(RiskFixtures.Context());

        decision.IsAllowed.Should().BeFalse();
        decision.RuleName.Should().Be("Thrower");
        neverCalled.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Skips_a_rule_the_tenants_policy_has_disabled()
    {
        var denier = new ScriptedRule("Denier", 10, () => RiskDecision.Deny("Denier", "no"));
        var gate = new RiskGate([denier], NullLogger<RiskGate>.Instance);

        var policy = RiskFixtures.Policy(enabledRules: new HashSet<string>(StringComparer.Ordinal)); // nothing enabled

        var decision = await gate.EvaluateAsync(RiskFixtures.Context(policy: policy));

        decision.IsAllowed.Should().BeTrue();
        denier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_OperationCanceledException_propagates_rather_than_being_treated_as_a_denial()
    {
        var rule = Substitute.For<IRiskRule>();
        rule.Name.Returns("Cancellable");
        rule.Order.Returns(10);
        rule.EvaluateAsync(Arg.Any<RiskEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns<RiskDecision>(_ => throw new OperationCanceledException());

        var gate = new RiskGate([rule], NullLogger<RiskGate>.Instance);

        var act = async () => await gate.EvaluateAsync(RiskFixtures.Context());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
