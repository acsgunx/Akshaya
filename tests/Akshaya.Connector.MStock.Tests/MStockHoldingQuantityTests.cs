using System.Net;
using System.Text;
using Akshaya.Connector.MStock;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// PREVENTS: reporting more stock than a trader actually owns.
///
/// A real account holding 400 HAPPSTMNDS was shown as 800. The connector was computing
/// <c>quantity + t1_quantity</c>, which is right for Kite — where the two are disjoint,
/// <c>quantity</c> being settled demat stock and <c>t1_quantity</c> the unsettled tranche — and
/// wrong for mStock, whose own console reported "Unsettled Qty 0, DP Qty 400" for the very same
/// holding.
///
/// The numbers below are that account's, verbatim. They are worth keeping because they
/// reconcile in four independent ways at once, which is what made the diagnosis certain rather
/// than plausible.
/// </summary>
public sealed class MStockHoldingQuantityTests
{
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// The shape that produced the doubling: <c>t1_quantity</c> carrying the SAME 400 as
    /// <c>quantity</c> rather than a separate unsettled tranche.
    /// </summary>
    private const string HoldingsResponse =
        """
        {"status":"success","data":[{"tradingsymbol":"HAPPSTMNDS","exchange":"NSE","instrument_token":0,"isin":"INE419U01012","product":null,"price":412.00,"quantity":400,"used_quantity":0,"t1_quantity":400,"realised_quantity":0,"authorised_quantity":0,"authorised_date":null,"opening_quantity":400,"collateral_quantity":0,"collateral_type":null,"discrepancy":0,"average_price":412.00,"last_price":355.80,"close_price":354.88,"pnl":-22480.00,"day_change":0.94,"day_change_percentage":0.26}]}
        """;

    private static async Task<Akshaya.Connectors.Abstractions.BrokerHolding> SingleHoldingAsync(string body)
    {
        var options = new MStockOptions();
        var errors = new MStockErrorMapper();
        var instruments = new MStockInstrumentCache();

        await using var api = MStockApi.Create(
            options, errors, session: null, httpClient: new HttpClient(new CannedHandler(body)));

        var portfolio = new MStockPortfolio(
            api, options, new MStockSymbolTranslator(instruments), instruments);

        var result = await portfolio.GetHoldingsAsync();

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        return result.Value.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task The_quantity_is_what_the_broker_holds_not_that_plus_t1()
    {
        // THE bug. 400, not 800.
        var holding = await SingleHoldingAsync(HoldingsResponse);

        holding.Quantity.Value.Should().Be(400m);
    }

    [Fact]
    public async Task The_current_value_follows_the_corrected_quantity()
    {
        // CurrentValue is derived on the contract as LastPrice x Quantity, so a doubled
        // quantity silently doubled the value of the book. 400 x 355.80.
        var holding = await SingleHoldingAsync(HoldingsResponse);

        holding.CurrentValue!.Value.Amount.Should().Be(142_320.00m);
    }

    [Fact]
    public async Task Cost_value_and_pnl_all_reconcile_with_each_other()
    {
        // The four-way check that made the diagnosis certain. Before the fix the P&L was right
        // (it comes straight from the broker's own `pnl`) while the quantity, the value and the
        // return percentage were all doubled — a position disagreeing with its own percentage.
        var holding = await SingleHoldingAsync(HoldingsResponse);

        var quantity = holding.Quantity.Value;
        var invested = quantity * holding.AveragePrice.Amount;
        var current = holding.CurrentValue!.Value.Amount;
        var pnl = holding.UnrealisedPnl!.Value.Amount;

        invested.Should().Be(164_800.00m);
        current.Should().Be(142_320.00m);

        // The broker's own P&L equals current minus invested — the two agree.
        pnl.Should().Be(-22_480.00m);
        (current - invested).Should().Be(pnl);

        // And the return the UI computes from cost is the broker's own -13.64%, not -6.82%.
        var returnPercent = Math.Round(pnl / invested * 100m, 2);
        returnPercent.Should().Be(-13.64m);
    }

    [Fact]
    public async Task A_holding_with_no_t1_is_unaffected()
    {
        // The documented sample has t1_quantity 0. The fix must not disturb it.
        var body = HoldingsResponse.Replace("\"t1_quantity\":400", "\"t1_quantity\":0", StringComparison.Ordinal);

        (await SingleHoldingAsync(body)).Quantity.Value.Should().Be(400m);
    }

    [Fact]
    public async Task Pledged_stock_is_reported_separately_from_the_quantity()
    {
        // collateral_quantity is pledged against margin and cannot be sold. It stays OUT of the
        // headline quantity and is surfaced on its own, which is what the UI's pledged badge
        // reads — a trader must not discover it by having a sell rejected.
        var body = HoldingsResponse.Replace("\"collateral_quantity\":0", "\"collateral_quantity\":150", StringComparison.Ordinal);

        var holding = await SingleHoldingAsync(body);

        holding.Quantity.Value.Should().Be(400m);
        holding.PledgedQuantity.Value.Should().Be(150m);
    }
}
