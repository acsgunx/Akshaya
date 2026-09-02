using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Wire shapes for the mStock Type A REST API.
///
/// These are all <c>internal</c> and all deliberately dumb: every field is nullable and no
/// field is validated here. Vendor payloads drift — a field that has been an integer for two
/// years turns into a string, an object turns into an array of one — and a DTO that throws on
/// deserialisation takes the whole order book down with it. Validation happens in the facets,
/// where a missing field can be turned into a proper <c>Result</c> failure that names it.
///
/// Timestamps are carried as strings and parsed by <see cref="MStockTime"/>, because mStock
/// sends naive local datetimes ("2025-09-01 09:15:04") with no offset, and letting
/// System.Text.Json bind those to a DateTimeOffset silently stamps them with the SERVER's
/// offset — which turns every timestamp on a UTC container into a 5.5-hour lie.
/// </summary>
internal static class MStockJson
{
    /// <summary>
    /// Shared serialiser settings. <see cref="JsonNumberHandling.AllowReadingFromString"/> is
    /// the important one: mStock quotes numbers inconsistently across endpoints (prices come
    /// back as <c>1560.5</c> from the quote route and <c>"1560.50"</c> from the order book).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>
/// The envelope every Type A route wraps its payload in. A 200 response with
/// <c>status: "error"</c> is mStock's normal way of reporting a business failure, so the
/// envelope must be inspected even on HTTP success.
/// </summary>
/// <typeparam name="T">The payload shape.</typeparam>
internal sealed class MStockEnvelope<T>
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>mStock's error taxonomy: TokenException, InputException, NetworkException, ...</summary>
    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }

    /// <summary>Some routes use <c>errorcode</c> instead of <c>error_type</c>.</summary>
    [JsonPropertyName("errorcode")]
    public string? ErrorCode { get; init; }

    [JsonIgnore]
    public bool IsSuccess =>
        Status is null || string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
}

// --- authentication ---------------------------------------------------------------------

internal sealed class MStockLoginData
{
    /// <summary>Opaque login identifier some builds require to be echoed on the token call.</summary>
    [JsonPropertyName("ugid")]
    public string? Ugid { get; init; }

    [JsonPropertyName("cid")]
    public string? ClientId { get; init; }

    [JsonPropertyName("nick_name")]
    public string? NickName { get; init; }

    [JsonPropertyName("mobile")]
    public string? MaskedMobile { get; init; }

    [JsonPropertyName("is_kyc")]
    public string? IsKyc { get; init; }

    [JsonPropertyName("is_activate")]
    public string? IsActivated { get; init; }

    [JsonPropertyName("is_password_reset")]
    public string? IsPasswordReset { get; init; }
}

internal sealed class MStockSessionData
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_shortname")]
    public string? UserShortName { get; init; }

    [JsonPropertyName("user_type")]
    public string? UserType { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("broker")]
    public string? Broker { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("public_token")]
    public string? PublicToken { get; init; }

    /// <summary>Token the streaming socket authenticates with. Not interchangeable with the access token.</summary>
    [JsonPropertyName("enctoken")]
    public string? EncToken { get; init; }

    [JsonPropertyName("login_time")]
    public string? LoginTime { get; init; }

    /// <summary>Exchanges this login is entitled to. Enforced before we send an order there.</summary>
    [JsonPropertyName("exchanges")]
    public IReadOnlyList<string>? Exchanges { get; init; }

    [JsonPropertyName("products")]
    public IReadOnlyList<string>? Products { get; init; }

    [JsonPropertyName("order_types")]
    public IReadOnlyList<string>? OrderTypes { get; init; }
}

// --- orders -----------------------------------------------------------------------------

/// <summary>Order placement body. Field names are mStock's, not ours.</summary>
internal sealed class MStockPlaceOrderRequest
{
    [JsonPropertyName("tradingsymbol")]
    public required string TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public required string Exchange { get; init; }

    [JsonPropertyName("transaction_type")]
    public required string TransactionType { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; }

    [JsonPropertyName("quantity")]
    public required string Quantity { get; init; }

    [JsonPropertyName("product")]
    public required string Product { get; init; }

    [JsonPropertyName("validity")]
    public required string Validity { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public string? DisclosedQuantity { get; init; }

    /// <summary>
    /// mStock's only free-text correlation field, and it is short. See
    /// <see cref="MStockOrders"/> for how the platform's ClientOrderId is folded into it.
    /// </summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }
}

internal sealed class MStockModifyOrderRequest
{
    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public string? DisclosedQuantity { get; init; }

    [JsonPropertyName("validity")]
    public string? Validity { get; init; }
}

internal sealed class MStockOrderIdData
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    /// <summary>Some builds spell it <c>order_no</c>; both are accepted.</summary>
    [JsonPropertyName("order_no")]
    public string? OrderNo { get; init; }

    [JsonIgnore]
    public string? Any => OrderId ?? OrderNo;
}

internal sealed class MStockCancelAllData
{
    [JsonPropertyName("cancelled_orders")]
    public IReadOnlyList<string>? CancelledOrders { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }
}

internal sealed class MStockOrderDto
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("parent_order_id")]
    public string? ParentOrderId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("status_message")]
    public string? StatusMessage { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("validity")]
    public string? Validity { get; init; }

    [JsonPropertyName("variety")]
    public string? Variety { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("filled_quantity")]
    public decimal? FilledQuantity { get; init; }

    [JsonPropertyName("pending_quantity")]
    public decimal? PendingQuantity { get; init; }

    [JsonPropertyName("cancelled_quantity")]
    public decimal? CancelledQuantity { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public decimal? DisclosedQuantity { get; init; }

    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public decimal? TriggerPrice { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("order_timestamp")]
    public string? OrderTimestamp { get; init; }

    [JsonPropertyName("exchange_timestamp")]
    public string? ExchangeTimestamp { get; init; }

    [JsonPropertyName("exchange_update_timestamp")]
    public string? ExchangeUpdateTimestamp { get; init; }

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }
}

internal sealed class MStockTradeDto
{
    [JsonPropertyName("trade_id")]
    public string? TradeId { get; init; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("fill_timestamp")]
    public string? FillTimestamp { get; init; }

    [JsonPropertyName("trade_timestamp")]
    public string? TradeTimestamp { get; init; }

    [JsonPropertyName("exchange_timestamp")]
    public string? ExchangeTimestamp { get; init; }
}

// --- portfolio ---------------------------------------------------------------------------

/// <summary>
/// mStock returns positions split into a <c>net</c> and a <c>day</c> bucket. We surface
/// <c>net</c>: the day bucket double-counts anything carried in from a previous session.
/// </summary>
internal sealed class MStockPositionsData
{
    [JsonPropertyName("net")]
    public IReadOnlyList<MStockPositionDto>? Net { get; init; }

    [JsonPropertyName("day")]
    public IReadOnlyList<MStockPositionDto>? Day { get; init; }
}

internal sealed class MStockPositionDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("buy_quantity")]
    public decimal? BuyQuantity { get; init; }

    [JsonPropertyName("sell_quantity")]
    public decimal? SellQuantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }

    [JsonPropertyName("realised")]
    public decimal? Realised { get; init; }

    [JsonPropertyName("unrealised")]
    public decimal? Unrealised { get; init; }

    [JsonPropertyName("multiplier")]
    public decimal? Multiplier { get; init; }
}

internal sealed class MStockHoldingDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("t1_quantity")]
    public decimal? T1Quantity { get; init; }

    [JsonPropertyName("realised_quantity")]
    public decimal? RealisedQuantity { get; init; }

    [JsonPropertyName("collateral_quantity")]
    public decimal? CollateralQuantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }
}

internal sealed class MStockFundsData
{
    [JsonPropertyName("equity")]
    public MStockFundSegment? Equity { get; init; }

    [JsonPropertyName("commodity")]
    public MStockFundSegment? Commodity { get; init; }
}

internal sealed class MStockFundSegment
{
    [JsonPropertyName("net")]
    public decimal? Net { get; init; }

    [JsonPropertyName("available")]
    public MStockFundAvailable? Available { get; init; }

    [JsonPropertyName("utilised")]
    public MStockFundUtilised? Utilised { get; init; }
}

internal sealed class MStockFundAvailable
{
    [JsonPropertyName("cash")]
    public decimal? Cash { get; init; }

    [JsonPropertyName("live_balance")]
    public decimal? LiveBalance { get; init; }

    [JsonPropertyName("collateral")]
    public decimal? Collateral { get; init; }

    [JsonPropertyName("intraday_payin")]
    public decimal? IntradayPayin { get; init; }
}

internal sealed class MStockFundUtilised
{
    [JsonPropertyName("debits")]
    public decimal? Debits { get; init; }

    [JsonPropertyName("exposure")]
    public decimal? Exposure { get; init; }

    [JsonPropertyName("m2m_realised")]
    public decimal? RealisedM2M { get; init; }

    [JsonPropertyName("m2m_unrealised")]
    public decimal? UnrealisedM2M { get; init; }

    [JsonPropertyName("span")]
    public decimal? Span { get; init; }
}

// --- market data --------------------------------------------------------------------------

internal sealed class MStockQuoteDto
{
    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("last_quantity")]
    public decimal? LastQuantity { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("net_change")]
    public decimal? NetChange { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("ohlc")]
    public MStockOhlcDto? Ohlc { get; init; }

    [JsonPropertyName("depth")]
    public MStockDepthDto? Depth { get; init; }
}

internal sealed class MStockOhlcDto
{
    [JsonPropertyName("open")]
    public decimal? Open { get; init; }

    [JsonPropertyName("high")]
    public decimal? High { get; init; }

    [JsonPropertyName("low")]
    public decimal? Low { get; init; }

    [JsonPropertyName("close")]
    public decimal? Close { get; init; }
}

internal sealed class MStockDepthDto
{
    [JsonPropertyName("buy")]
    public IReadOnlyList<MStockDepthLevelDto>? Buy { get; init; }

    [JsonPropertyName("sell")]
    public IReadOnlyList<MStockDepthLevelDto>? Sell { get; init; }
}

internal sealed class MStockDepthLevelDto
{
    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("orders")]
    public int? Orders { get; init; }
}

/// <summary>
/// Candle payload. mStock sends candles as positional arrays
/// (<c>[timestamp, open, high, low, close, volume, oi?]</c>) rather than objects, so the
/// element type has to stay <see cref="JsonElement"/> and be read by index.
/// </summary>
internal sealed class MStockCandlesData
{
    [JsonPropertyName("candles")]
    public IReadOnlyList<IReadOnlyList<JsonElement>>? Candles { get; init; }
}

internal sealed class MStockOptionChainRowDto
{
    [JsonPropertyName("strikePrice")]
    public decimal? StrikePrice { get; init; }

    [JsonPropertyName("strike_price")]
    public decimal? StrikePriceSnake { get; init; }

    [JsonPropertyName("expiryDate")]
    public string? ExpiryDate { get; init; }

    [JsonPropertyName("CE")]
    public MStockOptionChainLegDto? Call { get; init; }

    [JsonPropertyName("PE")]
    public MStockOptionChainLegDto? Put { get; init; }

    [JsonIgnore]
    public decimal? Strike => StrikePrice ?? StrikePriceSnake;
}

internal sealed class MStockOptionChainLegDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("lastPrice")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPriceSnake { get; init; }

    [JsonPropertyName("openInterest")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterestShort { get; init; }

    [JsonPropertyName("bidprice")]
    public decimal? BidPrice { get; init; }

    [JsonPropertyName("askPrice")]
    public decimal? AskPrice { get; init; }

    [JsonPropertyName("totalTradedVolume")]
    public long? Volume { get; init; }

    [JsonIgnore]
    public decimal? Ltp => LastPrice ?? LastPriceSnake;

    [JsonIgnore]
    public long? Oi => OpenInterest ?? OpenInterestShort;
}

internal sealed class MStockOptionChainData
{
    [JsonPropertyName("underlyingValue")]
    public decimal? UnderlyingValue { get; init; }

    [JsonPropertyName("data")]
    public IReadOnlyList<MStockOptionChainRowDto>? Rows { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<MStockOptionChainRowDto>? Records { get; init; }

    [JsonIgnore]
    public IReadOnlyList<MStockOptionChainRowDto> AllRows =>
        Rows ?? Records ?? [];
}
