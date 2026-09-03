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

/// <summary>
/// The first login leg's payload, as mStock actually sends it:
///
/// <code>
/// {"status":"success","data":{
///    "ugid":"5544454f-…","is_kyc":true,"is_activate":false,"is_password_reset":true,
///    "is_error":false,"cid":"1111","nm":"","flag":0}}
/// </code>
///
/// EVERY FIELD IS OPTIONAL AND LENIENTLY TYPED, on purpose. This connector needs exactly one
/// value out of the eight above — <see cref="Ugid"/> — and an earlier version of this class
/// declared the three <c>is_*</c> flags as strings, which mStock sends as bare booleans. The
/// result was a login that failed with "the broker's response could not be understood" while
/// the broker had in fact said "success": a field nothing reads killed the whole document,
/// because System.Text.Json treats one type mismatch as fatal for all of it.
///
/// The rule this class now follows: a field the connector does not act on must never be able
/// to fail a login. See docs/connectors/mstock-login-response.md.
/// </summary>
internal sealed class MStockLoginData
{
    /// <summary>Opaque login identifier some builds require to be echoed on the token call.</summary>
    [JsonPropertyName("ugid")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Ugid { get; init; }

    /// <summary>Client code. Quoted on some builds ("1111"), bare on others.</summary>
    [JsonPropertyName("cid")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ClientId { get; init; }

    /// <summary>Account nickname. Sent as <c>nm</c>; <see cref="NickNameLong"/> covers the older key.</summary>
    [JsonPropertyName("nm")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? NickName { get; init; }

    /// <summary>
    /// The <c>nick_name</c> spelling. Kept because this connector was originally written
    /// against it and there is no way to tell which builds still send it.
    /// </summary>
    [JsonPropertyName("nick_name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? NickNameLong { get; init; }

    /// <summary>
    /// Masked mobile the OTP went to, when the build sends one.
    ///
    /// OFTEN ABSENT — the observed payload has no such field at all. The wizard therefore has
    /// to treat "we cannot tell you where the code went" as normal and simply not show a
    /// destination, rather than failing or inventing one.
    /// </summary>
    [JsonPropertyName("mobile")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? MaskedMobile { get; init; }

    [JsonPropertyName("is_kyc")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsKyc { get; init; }

    [JsonPropertyName("is_activate")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsActivated { get; init; }

    [JsonPropertyName("is_password_reset")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsPasswordReset { get; init; }

    /// <summary>
    /// mStock's own in-payload error flag, alongside the envelope's <c>status</c>.
    ///
    /// Not currently branched on: the envelope's status is the authority, and trusting a second
    /// disagreeing signal would make "did this succeed" ambiguous. Mapped so it is visible in a
    /// debugger and in logs when a support question needs answering.
    /// </summary>
    [JsonPropertyName("is_error")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsError { get; init; }

    /// <summary>Undocumented status/sequence value. Mapped only so it cannot break the parse.</summary>
    [JsonPropertyName("flag")]
    [JsonConverter(typeof(LenientIntConverter))]
    public int? Flag { get; init; }

    /// <summary>The best available label for the account, whichever key the build used.</summary>
    [JsonIgnore]
    public string? DisplayName =>
        !string.IsNullOrWhiteSpace(NickName) ? NickName
        : !string.IsNullOrWhiteSpace(NickNameLong) ? NickNameLong
        : null;
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

/// <summary>
/// One row of <c>/openapi/typea/user/fundsummary</c>, as mStock actually documents it:
///
/// <code>
/// {"status":"success","data":[{
///    "AVAILABLE_BALANCE":"299972678840.29","AMOUNT_UTILIZED":"27395824.71",
///    "CLEAR_BALANCE":"199999949998","COLLATERALS":"74668","SEG":"A", … }]}
/// </code>
///
/// TWO THINGS THIS CLASS EXISTS TO CORRECT. The previous version expected
/// <c>{"equity":{"available":{…}}}</c> — Zerodha Kite's margins shape, not mStock's. mStock
/// returns an ARRAY of flat rows with SCREAMING_SNAKE_CASE keys, so deserialising it into an
/// object threw and the fund summary could never have worked. And every monetary value is a
/// QUOTED string ("0.0", "299972678840.29"); they bind to <c>decimal?</c> only because
/// <see cref="MStockJson.Options"/> sets <c>AllowReadingFromString</c>.
///
/// Field names are the vendor's own, verbatim, including the misspelled
/// <c>OPT_BUY_PRIMIUM_UTILIZE</c>. Renaming them to read nicely would mean this class no longer
/// matches the payload it is documenting, which is exactly how the last mismatch survived.
/// </summary>
internal sealed class MStockFundRow
{
    /// <summary>Segment code. "A" in the documented sample; the only row for an equity account.</summary>
    [JsonPropertyName("SEG")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Segment { get; init; }

    [JsonPropertyName("LIMIT_TYPE")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? LimitType { get; init; }

    /// <summary>What the account can actually trade with right now. The headline number.</summary>
    [JsonPropertyName("AVAILABLE_BALANCE")]
    public decimal? AvailableBalance { get; init; }

    /// <summary>Settled cash, excluding collateral and unsettled payins.</summary>
    [JsonPropertyName("CLEAR_BALANCE")]
    public decimal? ClearBalance { get; init; }

    [JsonPropertyName("UNCLEAR_BALANCE")]
    public decimal? UnclearBalance { get; init; }

    /// <summary>Margin consumed by open positions and working orders.</summary>
    [JsonPropertyName("AMOUNT_UTILIZED")]
    public decimal? AmountUtilized { get; init; }

    [JsonPropertyName("COLLATERALS")]
    public decimal? Collaterals { get; init; }

    [JsonPropertyName("MF_COLLATERAL")]
    public decimal? MutualFundCollateral { get; init; }

    [JsonPropertyName("REALISED_PROFITS")]
    public decimal? RealisedProfits { get; init; }

    [JsonPropertyName("MTM_COMBINED")]
    public decimal? MarkToMarketCombined { get; init; }

    [JsonPropertyName("ADDITIONAL_MARGIN")]
    public decimal? AdditionalMargin { get; init; }

    [JsonPropertyName("PEAK_MARGIN")]
    public decimal? PeakMargin { get; init; }

    [JsonPropertyName("PHYSICAL_MARGIN")]
    public decimal? PhysicalMargin { get; init; }

    [JsonPropertyName("ADHOC_LIMIT")]
    public decimal? AdhocLimit { get; init; }

    [JsonPropertyName("BANK_HOLDING")]
    public decimal? BankHolding { get; init; }

    [JsonPropertyName("LIMIT_SOD")]
    public decimal? LimitStartOfDay { get; init; }

    [JsonPropertyName("SUM_OF_ALL")]
    public decimal? SumOfAll { get; init; }

    [JsonPropertyName("RECEIVABLES")]
    public decimal? Receivables { get; init; }

    [JsonPropertyName("PAY_OUT_AMT")]
    public decimal? PayOutAmount { get; init; }

    [JsonPropertyName("OFS_UTILIZED")]
    public decimal? OfsUtilized { get; init; }

    /// <summary>Vendor's spelling of "premium". Kept verbatim so this matches the wire.</summary>
    [JsonPropertyName("OPT_BUY_PRIMIUM_UTILIZE")]
    public decimal? OptionBuyPremiumUtilized { get; init; }

    [JsonPropertyName("MTF_AVAILABLE_BALANCE")]
    public decimal? MtfAvailableBalance { get; init; }

    [JsonPropertyName("MTF_COLLATERAL")]
    public decimal? MtfCollateral { get; init; }

    [JsonPropertyName("MTF_UTILIZE")]
    public decimal? MtfUtilized { get; init; }
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
