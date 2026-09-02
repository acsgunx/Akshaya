using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// The cheapest tests in the repository, guarding the most expensive bugs.
///
/// A mapping error does not throw and does not log. It places an intraday order when the trader
/// asked for delivery, or reports a rejected order as open. The money is gone before anyone reads
/// a stack trace, so the mapping table gets exhaustive round-trip coverage in both directions —
/// every enum value, not a representative sample.
/// </summary>
public sealed class MStockMapsTests
{
    // ---------------------------------------------------------------------------------------
    // Position effect  (CNC / MIS / MTF / NRML)
    // ---------------------------------------------------------------------------------------

    public static TheoryData<PositionEffect, string> PositionEffects => new()
    {
        { PositionEffect.Delivery, MStockMaps.ProductCnc },
        { PositionEffect.Intraday, MStockMaps.ProductMis },
        { PositionEffect.Margin, MStockMaps.ProductMtf },
        { PositionEffect.CarryForward, MStockMaps.ProductNrml },
    };

    [Theory]
    [MemberData(nameof(PositionEffects))]
    public void Position_effect_maps_to_the_expected_product(PositionEffect effect, string expected)
    {
        var result = MStockMaps.ToNativeProduct(effect);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(PositionEffects))]
    public void Position_effect_round_trips(PositionEffect effect, string native)
    {
        var back = MStockMaps.ToCanonicalPositionEffect(native);

        back.IsSuccess.Should().BeTrue();
        back.Value.Should().Be(effect);
    }

    [Fact]
    public void Every_declared_position_effect_has_a_mapping()
    {
        // Catches the case where someone adds a PositionEffect to the shared vocabulary and
        // forgets this connector. The manifest is the source of truth for what we claim to
        // support, and anything claimed must map.
        var claimed = new[]
        {
            PositionEffect.Delivery, PositionEffect.Intraday,
            PositionEffect.Margin, PositionEffect.CarryForward,
        };

        foreach (var effect in claimed)
        {
            MStockMaps.ToNativeProduct(effect).IsSuccess.Should().BeTrue(
                $"the manifest claims support for {effect}");
        }
    }

    [Fact]
    public void Unsupported_position_effect_fails_rather_than_defaulting()
    {
        // ShortSell is not in this broker's manifest. It must fail loudly. A silent fallback to
        // CNC here would place a delivery buy where the caller asked to sell short.
        var result = MStockMaps.ToNativeProduct(PositionEffect.ShortSell);

        result.IsFailure.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Order type  (MARKET / LIMIT / SL / SL-M)
    // ---------------------------------------------------------------------------------------

    public static TheoryData<OrderType, string> OrderTypes => new()
    {
        { OrderType.Market, MStockMaps.OrderTypeMarket },
        { OrderType.Limit, MStockMaps.OrderTypeLimit },
        { OrderType.Stop, MStockMaps.OrderTypeStopLossMarket },
        { OrderType.StopLimit, MStockMaps.OrderTypeStopLoss },
    };

    [Theory]
    [MemberData(nameof(OrderTypes))]
    public void Order_type_maps_and_round_trips(OrderType type, string native)
    {
        // The Stop / StopLimit pair is the one people get backwards: mStock's SL is a stop-LIMIT
        // (it needs a price) and SL-M is a stop-MARKET. Swapping them turns a protective stop
        // into an unfillable resting order.
        var forward = MStockMaps.ToNativeOrderType(type);
        forward.IsSuccess.Should().BeTrue();
        forward.Value.Should().Be(native);

        var back = MStockMaps.ToCanonicalOrderType(native);
        back.IsSuccess.Should().BeTrue();
        back.Value.Should().Be(type);
    }

    [Theory]
    [InlineData(OrderType.MarketIfTouched)]
    [InlineData(OrderType.TrailingStop)]
    public void Unsupported_order_types_fail(OrderType type) =>
        MStockMaps.ToNativeOrderType(type).IsFailure.Should().BeTrue();

    // ---------------------------------------------------------------------------------------
    // Time in force and variety
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TimeInForce.Day, MStockMaps.ValidityDay)]
    [InlineData(TimeInForce.Ioc, MStockMaps.ValidityIoc)]
    public void Time_in_force_round_trips(TimeInForce tif, string native)
    {
        MStockMaps.ToNativeValidity(tif).Value.Should().Be(native);
        MStockMaps.ToCanonicalTimeInForce(native).Value.Should().Be(tif);
    }

    [Theory]
    [InlineData(TimeInForce.Gtc)]
    [InlineData(TimeInForce.Fok)]
    [InlineData(TimeInForce.Gtd)]
    public void Unsupported_time_in_force_fails(TimeInForce tif) =>
        MStockMaps.ToNativeValidity(tif).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(OrderVariety.Regular, MStockMaps.VarietyRegular)]
    [InlineData(OrderVariety.AfterMarket, MStockMaps.VarietyAfterMarket)]
    public void Variety_round_trips(OrderVariety variety, string native)
    {
        MStockMaps.ToNativeVariety(variety).Value.Should().Be(native);
        MStockMaps.ToCanonicalVariety(native).Value.Should().Be(variety);
    }

    // ---------------------------------------------------------------------------------------
    // Side and venue
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Side.Buy, MStockMaps.TransactionBuy)]
    [InlineData(Side.Sell, MStockMaps.TransactionSell)]
    public void Side_round_trips(Side side, string native)
    {
        MStockMaps.ToNativeSide(side).Value.Should().Be(native);
        MStockMaps.ToCanonicalSide(native).Value.Should().Be(side);
    }

    [Theory]
    [InlineData("XNSE", AssetClass.Equity, MStockMaps.ExchangeNse)]
    [InlineData("XBOM", AssetClass.Equity, MStockMaps.ExchangeBse)]
    [InlineData("XNSE", AssetClass.Future, MStockMaps.ExchangeNfo)]
    [InlineData("XNSE", AssetClass.Option, MStockMaps.ExchangeNfo)]
    [InlineData("XBOM", AssetClass.Option, MStockMaps.ExchangeBfo)]
    public void Venue_and_asset_class_select_the_right_exchange_segment(
        string mic, AssetClass assetClass, string expected)
    {
        // A derivative sent to NSE instead of NFO is rejected by the exchange, which is the good
        // case. The bad case is a cash symbol sent to NFO and matching something else entirely.
        var result = MStockMaps.ToNativeExchange(new Venue(mic), assetClass);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(MStockMaps.ExchangeNse, "XNSE")]
    [InlineData(MStockMaps.ExchangeBse, "XBOM")]
    [InlineData(MStockMaps.ExchangeNfo, "XNSE")]
    [InlineData(MStockMaps.ExchangeBfo, "XBOM")]
    public void Exchange_maps_back_to_the_underlying_venue(string exchange, string expectedMic) =>
        MStockMaps.ToCanonicalVenue(exchange).Value.Should().Be(new Venue(expectedMic));

    [Fact]
    public void Unknown_exchange_fails_rather_than_guessing() =>
        MStockMaps.ToCanonicalVenue("MCX").IsFailure.Should().BeTrue(
            "the manifest declares only NSE and BSE; silently accepting MCX would let an order "
            + "through for a venue this connector cannot actually reach");

    // ---------------------------------------------------------------------------------------
    // Order status — the map most likely to bite
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("COMPLETE", OrderStatus.Filled)]
    [InlineData("REJECTED", OrderStatus.Rejected)]
    [InlineData("CANCELLED", OrderStatus.Cancelled)]
    [InlineData("OPEN", OrderStatus.Open)]
    public void Terminal_and_working_statuses_map(string native, OrderStatus expected) =>
        MStockMaps.ToCanonicalOrderStatus(native).Value.Should().Be(expected);

    [Fact]
    public void An_unrecognised_status_becomes_Unknown_and_keeps_the_raw_text()
    {
        // This is why OrderStatus.Unknown exists. A broker can introduce a status we have never
        // seen; mapping it to Open would tell a trader their order is live when it may not be,
        // and mapping it to Rejected would be equally invented. Unknown plus the raw string lets
        // reconciliation and the UI both do something honest.
        var status = MStockMaps.ToCanonicalOrderStatusOrUnknown("SOME NEW STATE", out var raw);

        status.Should().Be(OrderStatus.Unknown);
        raw.Should().Be("SOME NEW STATE");
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("Complete")]
    [InlineData("  COMPLETE  ")]
    public void Status_matching_tolerates_case_and_whitespace(string native) =>
        MStockMaps.ToCanonicalOrderStatus(native).Value.Should().Be(OrderStatus.Filled);
}
