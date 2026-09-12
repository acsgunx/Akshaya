using System.Text.Json.Serialization;

namespace Akshaya.Connector.Longbridge;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  REST shapes, transcribed from the Longbridge SDK's serde types and the route documentation.
//  Numbers are strings on this wire (see LongbridgeJson) and are parsed where they are used.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>An OAuth 2.0 token response. Not enveloped: the token endpoint speaks RFC 6749.</summary>
internal sealed record LbTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }
}

internal sealed record LbSocketToken
{
    [JsonPropertyName("otp")]
    public string? Otp { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("online")]
    public int Online { get; init; }
}

internal sealed record LbSubmitOrderRequest
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; }

    [JsonPropertyName("side")]
    public required string Side { get; init; }

    [JsonPropertyName("submitted_quantity")]
    public required string SubmittedQuantity { get; init; }

    [JsonPropertyName("time_in_force")]
    public required string TimeInForce { get; init; }

    [JsonPropertyName("submitted_price")]
    public string? SubmittedPrice { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    /// <summary><c>yyyy-MM-dd</c>, required for GTD.</summary>
    [JsonPropertyName("expire_date")]
    public string? ExpireDate { get; init; }

    /// <summary>US securities only: <c>RTH_ONLY</c>, <c>ANY_TIME</c> or <c>OVERNIGHT</c>. Omitted elsewhere.</summary>
    [JsonPropertyName("outside_rth")]
    public string? OutsideRth { get; init; }

    /// <summary>Up to 255 characters on submission, echoed on every order row. Carries the ClientOrderId.</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; init; }

    /// <summary>
    /// Server-side idempotency: a second submission with the same id within ten minutes returns the first
    /// order instead of creating another. The ClientOrderId goes here too.
    /// </summary>
    [JsonPropertyName("client_request_id")]
    public string? ClientRequestId { get; init; }
}

internal sealed record LbSubmitOrderResponse
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }
}

internal sealed record LbReplaceOrderRequest
{
    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    /// <summary>Required on every replace, even when only the price changes.</summary>
    [JsonPropertyName("quantity")]
    public required string Quantity { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }
}

internal sealed record LbOrder
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("stock_name")]
    public string? StockName { get; init; }

    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("executed_quantity")]
    public string? ExecutedQuantity { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("executed_price")]
    public string? ExecutedPrice { get; init; }

    [JsonPropertyName("submitted_at")]
    public string? SubmittedAt { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    [JsonPropertyName("msg")]
    public string? Message { get; init; }

    [JsonPropertyName("time_in_force")]
    public string? TimeInForce { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("remark")]
    public string? Remark { get; init; }

    // --- the order-changed push spells two of these differently ---

    [JsonPropertyName("submitted_quantity")]
    public string? SubmittedQuantity { get; init; }

    [JsonPropertyName("submitted_price")]
    public string? SubmittedPrice { get; init; }
}

internal sealed record LbOrdersResponse
{
    [JsonPropertyName("orders")]
    public List<LbOrder>? Orders { get; init; }
}

internal sealed record LbExecution
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("trade_id")]
    public string? TradeId { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("trade_done_at")]
    public string? TradeDoneAt { get; init; }

    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }
}

internal sealed record LbExecutionsResponse
{
    [JsonPropertyName("trades")]
    public List<LbExecution>? Trades { get; init; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }
}

internal sealed record LbStockPosition
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("symbol_name")]
    public string? SymbolName { get; init; }

    /// <summary>Negative for a short.</summary>
    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("available_quantity")]
    public string? AvailableQuantity { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("cost_price")]
    public string? CostPrice { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }
}

internal sealed record LbPositionChannel
{
    [JsonPropertyName("account_channel")]
    public string? AccountChannel { get; init; }

    [JsonPropertyName("stock_info")]
    public List<LbStockPosition>? StockInfo { get; init; }
}

internal sealed record LbStockPositionsResponse
{
    [JsonPropertyName("list")]
    public List<LbPositionChannel>? List { get; init; }
}

internal sealed record LbCashInfo
{
    [JsonPropertyName("withdraw_cash")]
    public string? WithdrawCash { get; init; }

    [JsonPropertyName("available_cash")]
    public string? AvailableCash { get; init; }

    [JsonPropertyName("frozen_cash")]
    public string? FrozenCash { get; init; }

    [JsonPropertyName("settling_cash")]
    public string? SettlingCash { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

internal sealed record LbAccountBalance
{
    [JsonPropertyName("total_cash")]
    public string? TotalCash { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("net_assets")]
    public string? NetAssets { get; init; }

    [JsonPropertyName("init_margin")]
    public string? InitMargin { get; init; }

    [JsonPropertyName("buy_power")]
    public string? BuyPower { get; init; }

    [JsonPropertyName("cash_infos")]
    public List<LbCashInfo>? CashInfos { get; init; }
}

internal sealed record LbAccountBalanceResponse
{
    [JsonPropertyName("list")]
    public List<LbAccountBalance>? List { get; init; }
}

/// <summary>The trade socket's JSON push: <c>{ "event": "order_changed_lb", "data": { … } }</c>.</summary>
internal sealed record LbPushEvent
{
    public const string OrderChanged = "order_changed_lb";

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("data")]
    public LbOrder? Data { get; init; }
}
