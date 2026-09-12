using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Ibkr;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  Client Portal Web API shapes, transcribed from IBKR's OpenAPI description and its examples.
//  Scalars are strings (see IbkrJson) and are parsed where they are used; only the fields this
//  connector reads are declared.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary><c>POST /iserver/auth/status</c>, and the <c>iserver.authStatus</c> inside a tickle.</summary>
internal sealed record IbkrAuthStatus
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; }

    [JsonPropertyName("established")]
    public bool? Established { get; init; }

    [JsonPropertyName("competing")]
    public bool Competing { get; init; }

    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("fail")]
    public string? Fail { get; init; }
}

/// <summary><c>POST /tickle</c>.</summary>
internal sealed record IbkrTickle
{
    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("iserver")]
    public IbkrTickleServer? Server { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed record IbkrTickleServer
{
    [JsonPropertyName("authStatus")]
    public IbkrAuthStatus? AuthStatus { get; init; }
}

/// <summary><c>GET /iserver/accounts</c>: the accounts the username may trade. Must be called before other iserver routes.</summary>
internal sealed record IbkrAccounts
{
    [JsonPropertyName("accounts")]
    public List<string>? Accounts { get; init; }

    [JsonPropertyName("selectedAccount")]
    public string? SelectedAccount { get; init; }

    [JsonPropertyName("isPaper")]
    public bool? IsPaper { get; init; }
}

/// <summary>One row of <c>GET /portfolio/accounts</c>. Must be called before other portfolio routes.</summary>
internal sealed record IbkrPortfolioAccount
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The account's base currency.</summary>
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

/// <summary>One order ticket for submit, modify and preview.</summary>
internal sealed record IbkrOrderTicket
{
    [JsonPropertyName("acctId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("conid")]
    public required long Conid { get; init; }

    /// <summary>Unique for 24 hours, at most 64 characters: carries the ClientOrderId.</summary>
    [JsonPropertyName("cOID")]
    public string? ClientOrderId { get; init; }

    [JsonPropertyName("orderType")]
    public required string OrderType { get; init; }

    /// <summary>The limit price of LMT and STOP_LIMIT; the stop price of STP.</summary>
    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    /// <summary>The stop price of STOP_LIMIT.</summary>
    [JsonPropertyName("auxPrice")]
    public decimal? AuxPrice { get; init; }

    [JsonPropertyName("side")]
    public required string Side { get; init; }

    [JsonPropertyName("tif")]
    public required string TimeInForce { get; init; }

    [JsonPropertyName("quantity")]
    public required decimal Quantity { get; init; }

    [JsonPropertyName("outsideRTH")]
    public bool OutsideRegularHours { get; init; }
}

internal sealed record IbkrOrdersRequest
{
    [JsonPropertyName("orders")]
    public required IReadOnlyList<IbkrOrderTicket> Orders { get; init; }
}

/// <summary>An order accepted: the end of the submit, modify and reply walk.</summary>
internal sealed record IbkrOrderAck
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("order_status")]
    public string? OrderStatus { get; init; }
}

/// <summary>An order reply message: a question IBKR asks before it will work the order.</summary>
internal sealed record IbkrReplyPrompt
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("message")]
    public List<string>? Message { get; init; }

    [JsonPropertyName("messageIds")]
    public List<string>? MessageIds { get; init; }
}

/// <summary><c>GET /iserver/account/orders</c>: orders working now or finished in this brokerage session.</summary>
internal sealed record IbkrLiveOrders
{
    [JsonPropertyName("orders")]
    public List<IbkrLiveOrder>? Orders { get; init; }
}

internal sealed record IbkrLiveOrder
{
    [JsonPropertyName("acct")]
    public string? Account { get; init; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; init; }

    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("ticker")]
    public string? Ticker { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("listingExchange")]
    public string? ListingExchange { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("orderType")]
    public string? OrderType { get; init; }

    [JsonPropertyName("origOrderType")]
    public string? OriginalOrderType { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("auxPrice")]
    public string? AuxPrice { get; init; }

    [JsonPropertyName("avgPrice")]
    public string? AveragePrice { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("filledQuantity")]
    public string? FilledQuantity { get; init; }

    [JsonPropertyName("remainingQuantity")]
    public string? RemainingQuantity { get; init; }

    [JsonPropertyName("totalSize")]
    public string? TotalSize { get; init; }

    [JsonPropertyName("timeInForce")]
    public string? TimeInForce { get; init; }

    [JsonPropertyName("lastExecutionTime_r")]
    public string? LastExecutionTime { get; init; }

    /// <summary>The <c>cOID</c> the order was placed with.</summary>
    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; init; }

    [JsonPropertyName("cashCcy")]
    public string? CashCurrency { get; init; }

    [JsonPropertyName("order_cancellation_by_system_reason")]
    public string? SystemCancellationReason { get; init; }
}

/// <summary><c>GET /iserver/account/order/status/{orderId}</c>, for an order no longer in the live list.</summary>
internal sealed record IbkrOrderStatus
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("total_size")]
    public string? TotalSize { get; init; }

    [JsonPropertyName("cum_fill")]
    public string? CumulativeFill { get; init; }

    [JsonPropertyName("order_status")]
    public string? Status { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("tif")]
    public string? TimeInForce { get; init; }

    [JsonPropertyName("limit_price")]
    public string? LimitPrice { get; init; }

    [JsonPropertyName("stop_price")]
    public string? StopPrice { get; init; }

    [JsonPropertyName("average_price")]
    public string? AveragePrice { get; init; }

    [JsonPropertyName("order_status_description")]
    public string? StatusDescription { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary><c>DELETE /iserver/account/{account}/order/{orderId}</c>.</summary>
internal sealed record IbkrCancelResponse
{
    [JsonPropertyName("msg")]
    public string? Message { get; init; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>One execution from <c>GET /iserver/account/trades</c>.</summary>
internal sealed record IbkrTrade
{
    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("size")]
    public string? Size { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("commission")]
    public string? Commission { get; init; }

    [JsonPropertyName("trade_time_r")]
    public string? TradeTime { get; init; }

    [JsonPropertyName("account")]
    public string? Account { get; init; }

    [JsonPropertyName("order_ref")]
    public string? OrderRef { get; init; }
}

/// <summary><c>POST /iserver/account/{account}/orders/whatif</c>. Every amount is formatted text.</summary>
internal sealed record IbkrWhatIf
{
    [JsonPropertyName("amount")]
    public IbkrWhatIfAmount? Amount { get; init; }

    [JsonPropertyName("initial")]
    public IbkrWhatIfChange? Initial { get; init; }

    [JsonPropertyName("equity")]
    public IbkrWhatIfChange? Equity { get; init; }

    [JsonPropertyName("warn")]
    public string? Warn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed record IbkrWhatIfAmount
{
    /// <summary>For example <c>1,977.60 USD (10 Shares)</c>.</summary>
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }

    [JsonPropertyName("commission")]
    public string? Commission { get; init; }

    [JsonPropertyName("total")]
    public string? Total { get; init; }
}

internal sealed record IbkrWhatIfChange
{
    [JsonPropertyName("current")]
    public string? Current { get; init; }

    [JsonPropertyName("change")]
    public string? Change { get; init; }

    [JsonPropertyName("after")]
    public string? After { get; init; }
}

/// <summary>One position from <c>GET /portfolio/{account}/positions/{page}</c>.</summary>
internal sealed record IbkrPosition
{
    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("position")]
    public string? Quantity { get; init; }

    [JsonPropertyName("mktPrice")]
    public string? MarketPrice { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    /// <summary>Per share for a stock; per contract, multiplier included, for an option.</summary>
    [JsonPropertyName("avgCost")]
    public string? AverageCost { get; init; }

    /// <summary>Per share, or per unit of the underlying for an option.</summary>
    [JsonPropertyName("avgPrice")]
    public string? AveragePrice { get; init; }

    [JsonPropertyName("realizedPnl")]
    public string? RealisedPnl { get; init; }

    [JsonPropertyName("unrealizedPnl")]
    public string? UnrealisedPnl { get; init; }

    [JsonPropertyName("assetClass")]
    public string? AssetClass { get; init; }
}

/// <summary>One currency of <c>GET /portfolio/{account}/ledger</c>, keyed by currency code or <c>BASE</c>.</summary>
internal sealed record IbkrLedgerEntry
{
    [JsonPropertyName("cashbalance")]
    public string? CashBalance { get; init; }

    [JsonPropertyName("settledcash")]
    public string? SettledCash { get; init; }

    [JsonPropertyName("unrealizedpnl")]
    public string? UnrealisedPnl { get; init; }

    [JsonPropertyName("realizedpnl")]
    public string? RealisedPnl { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

/// <summary>One figure of <c>GET /portfolio/{account}/summary</c>, keyed by figure name.</summary>
internal sealed record IbkrSummaryValue
{
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("isNull")]
    public bool? IsNull { get; init; }
}

/// <summary><c>GET /trsrv/secdef?conids=</c>.</summary>
internal sealed record IbkrSecdefResponse
{
    [JsonPropertyName("secdef")]
    public List<IbkrSecdef>? Secdef { get; init; }
}

internal sealed record IbkrSecdef
{
    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("assetClass")]
    public string? AssetClass { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("putOrCall")]
    public string? PutOrCall { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("ticker")]
    public string? Ticker { get; init; }

    [JsonPropertyName("undConid")]
    public string? UnderlyingConid { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("listingExchange")]
    public string? ListingExchange { get; init; }

    [JsonPropertyName("incrementRules")]
    public List<IbkrIncrementRule>? IncrementRules { get; init; }
}

internal sealed record IbkrIncrementRule
{
    [JsonPropertyName("lowerEdge")]
    public string? LowerEdge { get; init; }

    [JsonPropertyName("increment")]
    public string? Increment { get; init; }
}

/// <summary>One listing inside a <c>GET /trsrv/stocks?symbols=</c> answer, which is keyed by symbol.</summary>
internal sealed record IbkrStockEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("assetClass")]
    public string? AssetClass { get; init; }

    [JsonPropertyName("contracts")]
    public List<IbkrStockContract>? Contracts { get; init; }
}

internal sealed record IbkrStockContract
{
    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }
}

/// <summary><c>GET /iserver/contract/{conid}/info-and-rules</c>: only the size increment is read, for board lots.</summary>
internal sealed record IbkrContractRules
{
    [JsonPropertyName("rules")]
    public IbkrRules? Rules { get; init; }
}

internal sealed record IbkrRules
{
    [JsonPropertyName("sizeIncrement")]
    public string? SizeIncrement { get; init; }
}

/// <summary><c>GET /iserver/secdef/strikes</c>.</summary>
internal sealed record IbkrStrikes
{
    [JsonPropertyName("call")]
    public List<decimal>? Call { get; init; }

    [JsonPropertyName("put")]
    public List<decimal>? Put { get; init; }
}

/// <summary>One contract from <c>GET /iserver/secdef/info</c>, which answers an array for options.</summary>
internal sealed record IbkrSecdefInfo
{
    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("maturityDate")]
    public string? MaturityDate { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }
}

/// <summary>One match from <c>GET /iserver/secdef/search</c>.</summary>
internal sealed record IbkrSearchResult
{
    [JsonPropertyName("conid")]
    public string? Conid { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("companyName")]
    public string? CompanyName { get; init; }

    /// <summary>The listing exchange, for a stock.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary><c>GET /iserver/marketdata/history</c>.</summary>
internal sealed record IbkrHistory
{
    [JsonPropertyName("data")]
    public List<IbkrBarRow>? Data { get; init; }
}

internal sealed record IbkrBarRow
{
    [JsonPropertyName("o")]
    public string? Open { get; init; }

    [JsonPropertyName("h")]
    public string? High { get; init; }

    [JsonPropertyName("l")]
    public string? Low { get; init; }

    [JsonPropertyName("c")]
    public string? Close { get; init; }

    [JsonPropertyName("v")]
    public string? Volume { get; init; }

    /// <summary>Epoch milliseconds.</summary>
    [JsonPropertyName("t")]
    public string? Time { get; init; }
}

/// <summary>A snapshot row or a market data push: field ids to values, plus <c>conid</c>, <c>_updated</c> and <c>topic</c>.</summary>
internal sealed class IbkrFieldRow
{
    private readonly Dictionary<string, string?> _values;

    private IbkrFieldRow(Dictionary<string, string?> values) => _values = values;

    public string? this[string field] => _values.GetValueOrDefault(field);

    public long? Conid => IbkrNumber.Integer(this["conid"]);

    public bool Has(string field) => !string.IsNullOrWhiteSpace(this[field]);

    public static IbkrFieldRow From(JsonElement element)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            }
        }

        return new IbkrFieldRow(values);
    }
}
