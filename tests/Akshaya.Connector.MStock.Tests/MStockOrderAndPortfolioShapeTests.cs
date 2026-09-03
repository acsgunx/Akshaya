using System.Text.Json;
using Akshaya.Connector.MStock;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// PREVENTS: the second wave of Kite-shaped assumptions, found by reading the Orders, Portfolio
/// and Position pages of the Type A documentation after the User page had already yielded five.
///
/// The theme running through all of them is that mStock is not internally consistent. The SAME
/// logical field arrives with a different name, a different type, or a different vocabulary
/// depending on which route answered — so a DTO written from one route's sample is wrong for
/// another's. Every payload below is copied verbatim from
/// https://tradingapi.mstock.com/docs/v1/typeA/ (retrieved 2026-09-03).
/// </summary>
public sealed class MStockOrderAndPortfolioShapeTests
{
    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<MStockEnvelope<T>>(json, MStockJson.Options)!.Data!;

    // ---- Order book -------------------------------------------------------------------

    /// <summary>One row of the documented order book. Note `"tag": []` and `"modified": "false"`.</summary>
    private const string OrderBookRow =
        """
        {"status":"success","data":[{"placed_by":"MA1234","order_id":"1151240930103","exchange_order_id":"0","parent_order_id":null,"status":"Rejected","status_message":"RMS:FUND LIMIT INSUFFICIENT","status_message_raw":null,"order_timestamp":"30-09-2024 15:45:46","exchange_update_timestamp":null,"exchange_timestamp":null,"variety":null,"modified":"false","exchange":"NSE","tradingsymbol":"INFY","instrument_token":1594,"order_type":"LIMIT","transaction_type":"BUY","validity":"DAY","product":"INTRADAY","quantity":10,"disclosed_quantity":0,"price":1250,"trigger_price":0,"average_price":0,"filled_quantity":0,"pending_quantity":0,"cancelled_quantity":0,"market_protection":0,"meta":{"demat_consent":"physical"},"tag":[],"guid":""}]}
        """;

    [Fact]
    public void The_order_book_parses_despite_tag_being_an_array()
    {
        // `"tag": []` against a string? property threw, which failed the WHOLE order book —
        // a trader could not see any of their orders because of an empty tag list.
        var orders = Parse<IReadOnlyList<MStockOrderDto>>(OrderBookRow);

        orders.Should().ContainSingle();
        orders[0].OrderId.Should().Be("1151240930103");
        orders[0].Tag.Should().BeNull();
    }

    [Fact]
    public void A_populated_tag_array_is_joined()
    {
        var json = OrderBookRow.Replace("\"tag\":[]", "\"tag\":[\"algo-1\",\"desk-b\"]", StringComparison.Ordinal);

        Parse<IReadOnlyList<MStockOrderDto>>(json)[0].Tag.Should().Be("algo-1,desk-b");
    }

    [Fact]
    public void Modified_is_read_whether_it_is_a_string_or_a_number()
    {
        // The order book sends "false"; /order/details sends 0. Same field.
        Parse<IReadOnlyList<MStockOrderDto>>(OrderBookRow)[0].Modified.Should().BeFalse();

        var numeric = OrderBookRow.Replace("\"modified\":\"false\"", "\"modified\":0", StringComparison.Ordinal);
        Parse<IReadOnlyList<MStockOrderDto>>(numeric)[0].Modified.Should().BeFalse();
    }

    [Theory]
    // mStock ACCEPTS these four on the way in...
    [InlineData("CNC", PositionEffect.Delivery)]
    [InlineData("MIS", PositionEffect.Intraday)]
    [InlineData("MTF", PositionEffect.Margin)]
    [InlineData("NRML", PositionEffect.CarryForward)]
    // ...and SENDS these back out, for the very same orders.
    [InlineData("INTRADAY", PositionEffect.Intraday)]
    [InlineData("DELIVERY", PositionEffect.Delivery)]
    [InlineData("CARRYFORWARD", PositionEffect.CarryForward)]
    public void Both_product_vocabularies_map(string product, PositionEffect expected)
    {
        // An order placed with product=MIS comes back from the order book as "INTRADAY".
        // Rejecting the response vocabulary made every order-book row unmappable.
        var mapped = MStockMaps.ToCanonicalPositionEffect(product);

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void An_unstated_product_falls_back_to_delivery(string? product)
    {
        // Positions document "product": "" and holdings "product": null. Delivery is the
        // conservative reading: believing an exposure persists when it does not is a far
        // smaller harm than believing it will square off by itself when it will not.
        var mapped = MStockMaps.ToCanonicalPositionEffect(product!);

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(PositionEffect.Delivery);
    }

    [Fact]
    public void An_unrecognised_product_is_still_an_error()
    {
        // Tolerance for the empty case must not become tolerance for a value that means
        // something we do not understand.
        MStockMaps.ToCanonicalPositionEffect("SOMETHING-NEW").IsFailure.Should().BeTrue();
    }

    // ---- Timestamps -------------------------------------------------------------------

    [Theory]
    // One API, one logical field, three formats — all three documented.
    [InlineData("30-09-2024 15:45:46")]      // order book: day-first, 24-hour
    [InlineData("2024-02-14 14:48:23")]      // trade history: ISO-ish
    [InlineData("23-01-2025 02:55:55 PM")]   // /order/details: day-first, 12-hour + meridiem
    [InlineData("10-06-2025 13:08:42")]      // trade book
    public void Every_documented_timestamp_format_parses(string raw)
    {
        MStockTime.Parse(raw).Should().NotBeNull($"'{raw}' is a documented mStock timestamp format");
    }

    // ---- Trade book vs trades ---------------------------------------------------------

    /// <summary>The documented /tradebook row. SCREAMING_SNAKE, entirely different names.</summary>
    private const string TradeBookResponse =
        """
        {"status":"success","data":[{"ALGO_ID":"0","BUY_SELL":"Sell","CLIENT_ID":"MA68123","ENCASH_FLG":"N","EXCHANGE":"NSE","EXCH_ORDER_NUMBER":"1100000048872942","EXPIRY_DATE":"0","FULL_SYMBOL":"VODAFONE IDEA LIMITED","GTC_FLG":"N","INSTRUMENT_NAME":"EQUITY","MKT_PROTECT_FLG":"N","MKT_PROTECT_VAL":0,"MKT_TYPE":"NL","OPT_TYPE":"XX","ORDER_DATE_TIME":"10-06-2025 13:08:42","ORDER_NUMBER":"21612506101476","ORDER_TYPE":"MARKET","PAN_NO":"BYYP123","PARTICIPANT_TYPE":"B","PRICE":6.98,"PRODUCT":"CNC","QUANTITY":4,"R":1,"REMARKS1":"NA","REMARKS2":"NA","SEC_ID":"14366","SEGMENT":"E","SETTLOR":"90144","SOURCE_FLG":"WEB","STRIKE_PRICE":0,"SYMBOL":"IDEA","TRADE_NUMBER":"206465040","TRADE_VALUE":27.92}]}
        """;

    [Fact]
    public void The_trade_book_binds_to_its_own_row_type()
    {
        var rows = Parse<IReadOnlyList<MStockTradeBookRow>>(TradeBookResponse);

        rows.Should().ContainSingle();
        rows[0].TradeNumber.Should().Be("206465040");
        rows[0].Symbol.Should().Be("IDEA");
        rows[0].Price.Should().Be(6.98m);
        rows[0].Quantity.Should().Be(4m);
        rows[0].BuySell.Should().Be("Sell");
    }

    [Fact]
    public void The_trade_book_row_projects_onto_the_shape_the_connector_maps()
    {
        var trade = Parse<IReadOnlyList<MStockTradeBookRow>>(TradeBookResponse)[0].ToTrade();

        trade.TradeId.Should().Be("206465040");
        trade.OrderId.Should().Be("21612506101476");
        trade.TradingSymbol.Should().Be("IDEA");
        trade.Exchange.Should().Be("NSE");
        trade.AveragePrice.Should().Be(6.98m);
    }

    [Fact]
    public void The_trade_book_read_through_the_snake_case_type_yields_nothing_usable()
    {
        // THE bug, pinned. This is what used to happen: the parse SUCCEEDS (unmapped members
        // simply bind to null), so the call reported success, mapping then failed with
        // "trade_id is missing", and the /trades fallback that would have worked never ran
        // because nothing had reported a failure. Two wrong shapes cancelling into a
        // plausible-looking error is the worst kind of bug to chase.
        var wrong = Parse<IReadOnlyList<MStockTradeDto>>(TradeBookResponse);

        wrong.Should().ContainSingle();
        wrong[0].TradeId.Should().BeNull("this is precisely why /tradebook needs its own type");
        wrong[0].TradingSymbol.Should().BeNull();
    }

    [Fact]
    public void The_documented_trades_route_still_binds_to_the_snake_case_type()
    {
        // /trades genuinely is snake_case. Both types are needed; neither replaces the other.
        var json =
            """
            {"status":"success","data":[{"trade_id":"68346395","order_id":"1300000040250500","exchange":"NSE","tradingsymbol":"RASHTRIYA CHEMICALS & FER","instrument_token":0,"product":"CNC","average_price":145.45,"quantity":1,"exchange_order_id":"1300000040250500","transaction_type":"SELL","fill_timestamp":"14:48:23","order_timestamp":"2024-02-14 14:48:23","exchange_timestamp":"2024-02-14 14:48:23"}]}
            """;

        var trades = Parse<IReadOnlyList<MStockTradeDto>>(json);

        trades[0].TradeId.Should().Be("68346395");
        trades[0].AveragePrice.Should().Be(145.45m);
    }

    // ---- Holdings and positions -------------------------------------------------------

    [Fact]
    public void The_documented_holdings_response_parses()
    {
        var json =
            """
            {"status":"success","data":[{"tradingsymbol":"BANK OF MAHARASHTRA","exchange":null,"instrument_token":11377,"isin":"INE457A01014","product":null,"price":30,"quantity":10,"used_quantity":1,"t1_quantity":0,"realised_quantity":0,"authorised_quantity":0,"authorised_date":null,"opening_quantity":0,"collateral_quantity":0,"collateral_type":null,"discrepancy":0,"average_price":30,"last_price":84.7,"close_price":51.1,"pnl":0,"day_change":0,"day_change_percentage":0}]}
            """;

        var holdings = Parse<IReadOnlyList<MStockHoldingDto>>(json);

        holdings.Should().ContainSingle();

        // The identifying facts: a COMPANY NAME where a ticker belongs, a null exchange, and a
        // numeric token that is the only thing here a lookup can actually use.
        holdings[0].TradingSymbol.Should().Be("BANK OF MAHARASHTRA");
        holdings[0].Exchange.Should().BeNull();
        holdings[0].InstrumentToken.Should().Be(11377);
        holdings[0].Isin.Should().Be("INE457A01014");
    }

    [Fact]
    public void The_documented_positions_response_parses_with_its_net_and_day_buckets()
    {
        var json =
            """
            {"status":"success","data":{"net":[{"tradingsymbol":"YESBANK","exchange":"NSE","instrument_token":11915,"product":"","quantity":100,"overnight_quantity":0,"multiplier":1,"average_price":19.05,"close_price":27.65,"last_price":27.65,"value":1905,"pnl":0,"m2m":860,"unrealised":0,"realised":0,"buy_quantity":100,"buy_price":19.05,"buy_value":1905,"buy_m2m":0,"sell_quantity":0,"sell_price":0,"sell_value":0,"sell_m2m":0,"day_buy_quantity":100,"day_buy_price":19.05,"day_buy_value":1905,"day_sell_quantity":0,"day_sell_price":0,"day_sell_value":0}],"day":null}}
            """;

        var data = Parse<MStockPositionsData>(json);

        data.Net.Should().ContainSingle();
        data.Net![0].TradingSymbol.Should().Be("YESBANK");
        data.Net[0].Quantity.Should().Be(100m);

        // "product": "" — the reason the empty-product fallback exists.
        data.Net[0].Product.Should().BeEmpty();
    }

    // ---- Order acknowledgements -------------------------------------------------------

    [Theory]
    [InlineData("""{"status":"success","data":{"order_id":"1131241001100"}}""")]   // place
    [InlineData("""{"status":"success","data":{"order_id":"1161241001100"}}""")]   // cancel
    public void Order_acknowledgements_parse(string json)
    {
        Parse<MStockOrderIdData>(json).OrderId.Should().NotBeNullOrWhiteSpace();
    }
}
