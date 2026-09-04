using System.Text.Json;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// Guards the ORDER WIRE FORMAT against mStock's published Type A documentation.
///
/// Every fact asserted here was read off the vendor's own docs, and every one of them was
/// wrong in this connector at some point:
///
///  * placement and modification are form-encoded, and were being posted as JSON;
///  * modify is a replace and needs the full order context, and was sending only the deltas;
///  * cancel-all returns no count, and was being read as though it did;
///  * "Triggered" and "Pending" are real statuses, and were degrading to Unknown.
///
/// None of those failures throws. A JSON body to a form route comes back as an HTTP 200
/// carrying status:"error", which reads exactly like a rejected order — so the compiler, the
/// logs and a smoke test all stay quiet while the trader is told their order bounced. These
/// assertions are the only thing standing between that and a trading morning.
/// </summary>
public sealed class MStockOrderWireFormatTests
{
    private static readonly Dictionary<string, Guid> NoTags = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------------------------------
    // Placement: field names and the form shape
    // ---------------------------------------------------------------------------------------

    private static MStockPlaceOrderRequest SampleLimitOrder() => new()
    {
        TradingSymbol = "INFY-EQ",
        Exchange = MStockMaps.ExchangeNse,
        TransactionType = MStockMaps.TransactionBuy,
        OrderType = MStockMaps.OrderTypeLimit,
        Quantity = "10",
        Product = MStockMaps.ProductMis,
        Validity = MStockMaps.ValidityDay,
        Price = "1250",
    };

    [Fact]
    public void A_placement_carries_every_field_mstock_documents_as_required()
    {
        var form = SampleLimitOrder().ToForm().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // The exact spellings from the vendor's cURL sample. "tradingsymbol" is one word and
        // all lower case; a camelCased or snake_cased variant is silently ignored by the
        // gateway, which then rejects the order for a missing symbol.
        form.Should().ContainKey("tradingsymbol").WhoseValue.Should().Be("INFY-EQ");
        form.Should().ContainKey("exchange").WhoseValue.Should().Be("NSE");
        form.Should().ContainKey("transaction_type").WhoseValue.Should().Be("BUY");
        form.Should().ContainKey("order_type").WhoseValue.Should().Be("LIMIT");
        form.Should().ContainKey("quantity").WhoseValue.Should().Be("10");
        form.Should().ContainKey("product").WhoseValue.Should().Be("MIS");
        form.Should().ContainKey("validity").WhoseValue.Should().Be("DAY");
        form.Should().ContainKey("price").WhoseValue.Should().Be("1250");
    }

    [Fact]
    public void A_market_order_sends_no_price_at_all()
    {
        var market = new MStockPlaceOrderRequest
        {
            TradingSymbol = "INFY-EQ",
            Exchange = MStockMaps.ExchangeNse,
            TransactionType = MStockMaps.TransactionBuy,
            OrderType = MStockMaps.OrderTypeMarket,
            Quantity = "10",
            Product = MStockMaps.ProductMis,
            Validity = MStockMaps.ValidityDay,
            Price = null,
        };

        var form = market.ToForm();

        // OMITTED, not empty. Some exchange gateways read an empty protection price as zero
        // and reject the order outright, so "price=" is not equivalent to sending nothing.
        form.Should().NotContain(p => p.Key == "price");
        form.Should().NotContain(p => p.Key == "trigger_price");
    }

    // ---------------------------------------------------------------------------------------
    // Modification: the full order context
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_modify_carries_the_order_context_not_just_the_changed_fields()
    {
        var modify = new MStockModifyOrderRequest
        {
            OrderId = "1131241001100",
            Variety = MStockMaps.VarietyRegular,
            TradingSymbol = "INFY-EQ",
            Exchange = MStockMaps.ExchangeNse,
            TransactionType = MStockMaps.TransactionBuy,
            Product = MStockMaps.ProductCnc,
            OrderType = MStockMaps.OrderTypeLimit,
            Quantity = "5",
            Price = "2000",
            Validity = MStockMaps.ValidityDay,
            RemainingQuantity = "5",
        };

        var form = modify.ToForm().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        // THE POINT OF THIS TEST. mStock's modify is a replace: omit the product and the broker
        // fills it from its own default, so amending the price of a delivery order can hand
        // back an intraday one that squares off at 15:20 without anyone asking for it.
        form.Should().ContainKey("product").WhoseValue.Should().Be("CNC");
        form.Should().ContainKey("transaction_type").WhoseValue.Should().Be("BUY");
        form.Should().ContainKey("tradingsymbol").WhoseValue.Should().Be("INFY-EQ");
        form.Should().ContainKey("exchange").WhoseValue.Should().Be("NSE");
        form.Should().ContainKey("variety").WhoseValue.Should().Be("reg");

        // And the documented remaining-quantity field, which a part-filled amendment needs.
        form.Should().ContainKey("modqty_remng").WhoseValue.Should().Be("5");
    }

    // ---------------------------------------------------------------------------------------
    // Cancel-all: the absent count
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_documented_cancel_all_payload_reports_an_unknown_count_not_zero()
    {
        // mStock's documented success payload, verbatim: one order id and no count.
        const string Payload = """{"order_id":"1161241001100"}""";

        var data = JsonSerializer.Deserialize<MStockCancelAllData>(Payload, MStockJson.Options);

        data.Should().NotBeNull();

        // NOT ZERO. A successful sweep reported as "0 cancelled" is indistinguishable from a
        // sweep that did nothing, and a trader who believes the panic button failed will start
        // cancelling by hand while the sweep is still settling.
        data!.ReportedCount.Should().Be(MStockCancelAllData.UnknownCount);
    }

    [Fact]
    public void A_cancel_all_payload_that_does_report_a_count_is_believed()
    {
        const string Payload = """{"count":9}""";

        var data = JsonSerializer.Deserialize<MStockCancelAllData>(Payload, MStockJson.Options);

        data!.ReportedCount.Should().Be(9);
    }

    // ---------------------------------------------------------------------------------------
    // Statuses that appear in the vendor's own samples
    // ---------------------------------------------------------------------------------------

    [Theory]
    // From the /order/details sample: a stop-loss whose trigger has fired.
    [InlineData("Triggered", OrderStatus.Open)]
    // From the same sample, paired with status_message "CONFIRMED".
    [InlineData("Pending", OrderStatus.Open)]
    // From the order-book sample.
    [InlineData("Rejected", OrderStatus.Rejected)]
    public void Statuses_from_the_vendor_samples_map_to_the_canonical_lifecycle(
        string vendorStatus,
        OrderStatus expected)
    {
        var mapped = MStockMaps.ToCanonicalOrderStatus(vendorStatus);

        mapped.IsSuccess.Should().BeTrue(
            $"'{vendorStatus}' appears in mStock's own documentation and must not degrade to Unknown");
        mapped.Value.Should().Be(expected);
    }

    [Fact]
    public void A_triggered_stop_is_working_not_filled()
    {
        // The distinction that costs money if it is wrong. "Triggered" means the stop fired and
        // the order is now live at the exchange — booking it as Filled would create a position
        // in the platform's books that does not exist at the broker.
        var mapped = MStockMaps.ToCanonicalOrderStatus("Triggered");

        mapped.Value.Should().Be(OrderStatus.Open);
        mapped.Value.IsWorking().Should().BeTrue();
        mapped.Value.IsTerminal().Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // The margin calculator's separate variety vocabulary
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_margin_route_spells_the_regular_variety_differently_from_placement()
    {
        // Same vendor, same concept, two spellings: placement takes "reg" and the margin
        // calculator documents "regular". Exactly the sort of thing that gets fixed at one
        // call site and missed at the other.
        MStockMaps.ToNativeVariety(OrderVariety.Regular).Value.Should().Be("reg");
        MStockMaps.ToNativeMarginVariety(OrderVariety.Regular).Should().Be("regular");
    }

    [Fact]
    public void The_margin_response_separates_blocked_margin_from_charges()
    {
        // mStock's documented sample. `total` is the margin BLOCKED; `charges.total` is a
        // separate estimate of what the trade costs. Adding the two together — an easy
        // mistake, they are both called "total" — overstates the cost of every trade.
        const string Payload = """
        {
            "type": "equity",
            "tradingsymbol": "INFY",
            "exchange": "NSE",
            "additional": 1699.37,
            "charges": {
                "transaction_tax": 0,
                "transaction_tax_type": "stt",
                "brokerage": 80.92,
                "gst": { "igst": 0, "cgst": 0, "sgst": 0, "total": 0 },
                "total": 80.92
            },
            "total": 1699.37
        }
        """;

        var data = JsonSerializer.Deserialize<MStockMarginData>(Payload, MStockJson.Options);

        data.Should().NotBeNull();
        data!.Total.Should().Be(1699.37m);
        data.Charges.Should().NotBeNull();
        data.Charges!.Total.Should().Be(80.92m);
        data.Charges.Brokerage.Should().Be(80.92m);
        data.Charges.TransactionTaxType.Should().Be("stt");
    }

    // ---------------------------------------------------------------------------------------
    // Position conversion
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_position_conversion_carries_both_products_and_the_day_position_type()
    {
        var convert = new MStockConvertPositionRequest
        {
            TradingSymbol = "ACC",
            Exchange = MStockMaps.ExchangeNse,
            TransactionType = MStockMaps.TransactionBuy,
            PositionType = MStockMaps.PositionTypeDay,
            Quantity = "1",
            OldProduct = MStockMaps.ProductCnc,
            NewProduct = MStockMaps.ProductMis,
        };

        var form = convert.ToForm().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        form.Should().ContainKey("old_product").WhoseValue.Should().Be("CNC");
        form.Should().ContainKey("new_product").WhoseValue.Should().Be("MIS");

        // mStock documents exactly one position type, and an overnight position has already
        // settled into its product — there is nothing left to convert.
        form.Should().ContainKey("position_type").WhoseValue.Should().Be("DAY");
    }

    // ---------------------------------------------------------------------------------------
    // The tag field's three shapes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_client_order_id_survives_a_round_trip_through_the_tag()
    {
        var clientOrderId = Guid.CreateVersion7();
        var tag = MStockOrderTags.Encode(clientOrderId);

        var index = new Dictionary<string, Guid>(StringComparer.Ordinal) { [tag] = clientOrderId };

        MStockOrderTags.Decode(tag, index).Should().Be(clientOrderId);
        MStockOrderTags.Decode("never-placed-by-us", NoTags).Should().BeNull();
    }
}
