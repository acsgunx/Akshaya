using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Wire shapes for the Kite Connect v3 API.
///
/// These are all <c>internal</c> and all deliberately dumb: every field is nullable and no field
/// is validated here. Vendor payloads drift — a field that has been an integer for two years
/// turns into a string, an object turns into an array of one — and a DTO that throws on
/// deserialisation takes the whole order book down with it. Validation happens in the facets,
/// where a missing field can be turned into a proper <c>Result</c> failure that names it.
///
/// Timestamps are carried as strings and parsed by <see cref="ZerodhaTime"/>, because Kite sends
/// naive local datetimes ("2021-05-31 09:18:57") with no offset, and letting System.Text.Json
/// bind those to a DateTimeOffset silently stamps them with the SERVER's offset — which turns
/// every timestamp on a UTC container into a five-and-a-half-hour lie.
/// </summary>
internal static class ZerodhaJson
{
    /// <summary>
    /// Shared serialiser settings. <see cref="JsonNumberHandling.AllowReadingFromString"/> is
    /// the one that earns its place: Kite documents <c>instrument_token</c> as a string on some
    /// routes and sends it as a bare number on others, and the order and trade books disagree
    /// with each other about <c>exchange_order_id</c>.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The value of <c>status</c> on a successful response.</summary>
    public const string StatusSuccess = "success";

    /// <summary>The value of <c>status</c> on a failed one.</summary>
    public const string StatusError = "error";

    /// <summary>The response field carrying the status, for <c>ConnectorHttpOptions.BodyStatusField</c>.</summary>
    public const string StatusField = "status";
}

/// <summary>
/// The envelope every Kite route wraps its payload in.
///
/// Unlike several Indian broker APIs, Kite is consistent about this: a success is
/// <c>{"status":"success","data":…}</c> and a failure is
/// <c>{"status":"error","message":…,"error_type":…}</c>, and a failure always carries a 4xx or
/// 5xx status line with it. That consistency is why this connector can use one generic envelope
/// rather than a response class per route.
/// </summary>
/// <typeparam name="T">The payload shape.</typeparam>
internal sealed class KiteEnvelope<T>
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Kite's error taxonomy: TokenException, InputException, MarginException, …</summary>
    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }

    [JsonIgnore]
    public bool IsSuccess =>
        Status is null || string.Equals(Status, ZerodhaJson.StatusSuccess, StringComparison.OrdinalIgnoreCase);
}

// --- authentication --------------------------------------------------------------------------

internal sealed class KiteSession
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_shortname")]
    public string? UserShortName { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("broker")]
    public string? Broker { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>A token for public session validation. Not used for signing requests.</summary>
    [JsonPropertyName("public_token")]
    public string? PublicToken { get; init; }

    /// <summary>Only issued to platforms Zerodha has specifically approved. See ZerodhaAuth.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("login_time")]
    public string? LoginTime { get; init; }

    /// <summary>Exchanges this login may trade. Carried into the session so an entitlement gap
    /// is caught locally rather than as an exchange rejection.</summary>
    [JsonPropertyName("exchanges")]
    public List<string>? Exchanges { get; init; }

    [JsonPropertyName("products")]
    public List<string>? Products { get; init; }

    [JsonPropertyName("order_types")]
    public List<string>? OrderTypes { get; init; }
}

// --- orders ------------------------------------------------------------------------------------

/// <summary>A place, modify or cancel acknowledgement.</summary>
internal sealed class KiteOrderId
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }
}

internal sealed class KiteOrder
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("parent_order_id")]
    public string? ParentOrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Human-readable rejection reason. Always shown to the trader verbatim.</summary>
    [JsonPropertyName("status_message")]
    public string? StatusMessage { get; init; }

    /// <summary>The OMS's own words, which are terser and often more specific than the above.</summary>
    [JsonPropertyName("status_message_raw")]
    public string? StatusMessageRaw { get; init; }

    [JsonPropertyName("variety")]
    public string? Variety { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("instrument_token")]
    public uint? InstrumentToken { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("validity")]
    public string? Validity { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public decimal? DisclosedQuantity { get; init; }

    [JsonPropertyName("filled_quantity")]
    public decimal? FilledQuantity { get; init; }

    [JsonPropertyName("pending_quantity")]
    public decimal? PendingQuantity { get; init; }

    [JsonPropertyName("cancelled_quantity")]
    public decimal? CancelledQuantity { get; init; }

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

    /// <summary>Carries this platform's ClientOrderId. See <see cref="ZerodhaOrderTags"/>.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>Kite v3 also returns the tags as a list. Read as a fallback when <see cref="Tag"/> is null.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }
}

internal sealed class KiteTrade
{
    [JsonPropertyName("trade_id")]
    public string? TradeId { get; init; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    /// <summary>The price the fill happened at. Kite calls it average_price on a trade.</summary>
    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("fill_timestamp")]
    public string? FillTimestamp { get; init; }

    [JsonPropertyName("exchange_timestamp")]
    public string? ExchangeTimestamp { get; init; }

    [JsonPropertyName("order_timestamp")]
    public string? OrderTimestamp { get; init; }
}

// --- margins and charges --------------------------------------------------------------------------

internal sealed class KiteOrderMargin
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    /// <summary>The total margin the order requires. The sum of the components below.</summary>
    [JsonPropertyName("total")]
    public decimal? Total { get; init; }

    [JsonPropertyName("span")]
    public decimal? Span { get; init; }

    [JsonPropertyName("exposure")]
    public decimal? Exposure { get; init; }

    [JsonPropertyName("option_premium")]
    public decimal? OptionPremium { get; init; }

    [JsonPropertyName("additional")]
    public decimal? Additional { get; init; }

    [JsonPropertyName("var")]
    public decimal? Var { get; init; }

    [JsonPropertyName("cash")]
    public decimal? Cash { get; init; }

    [JsonPropertyName("charges")]
    public KiteCharges? Charges { get; init; }
}

/// <summary>
/// The itemised cost of an order, as Kite computes it. This is what makes
/// <c>chargesEstimate: true</c> honest: the numbers are the broker's own, not a schedule this
/// connector guessed at and would have to keep in step with every regulatory change.
/// </summary>
internal sealed class KiteCharges
{
    /// <summary>STT on equity, CTT on commodities. <see cref="TransactionTaxType"/> says which.</summary>
    [JsonPropertyName("transaction_tax")]
    public decimal? TransactionTax { get; init; }

    [JsonPropertyName("transaction_tax_type")]
    public string? TransactionTaxType { get; init; }

    [JsonPropertyName("exchange_turnover_charge")]
    public decimal? ExchangeTurnoverCharge { get; init; }

    [JsonPropertyName("sebi_turnover_charge")]
    public decimal? SebiTurnoverCharge { get; init; }

    [JsonPropertyName("brokerage")]
    public decimal? Brokerage { get; init; }

    [JsonPropertyName("stamp_duty")]
    public decimal? StampDuty { get; init; }

    [JsonPropertyName("gst")]
    public KiteGst? Gst { get; init; }

    [JsonPropertyName("total")]
    public decimal? Total { get; init; }
}

internal sealed class KiteGst
{
    [JsonPropertyName("igst")]
    public decimal? Igst { get; init; }

    [JsonPropertyName("cgst")]
    public decimal? Cgst { get; init; }

    [JsonPropertyName("sgst")]
    public decimal? Sgst { get; init; }

    [JsonPropertyName("total")]
    public decimal? Total { get; init; }
}

// --- portfolio --------------------------------------------------------------------------------------

/// <summary>
/// The positions payload. Kite returns TWO sets: <c>net</c> is the actual current position and
/// <c>day</c> is that day's activity alone. Reading <c>day</c> as the position would erase every
/// overnight carry-forward from the portfolio.
/// </summary>
internal sealed class KitePositions
{
    [JsonPropertyName("net")]
    public List<KitePosition>? Net { get; init; }

    [JsonPropertyName("day")]
    public List<KitePosition>? Day { get; init; }
}

internal sealed class KitePosition
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public uint? InstrumentToken { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    /// <summary>Signed net quantity. Negative is short.</summary>
    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    /// <summary>Quantity carried in from a previous day. Decides the conversion's position_type.</summary>
    [JsonPropertyName("overnight_quantity")]
    public decimal? OvernightQuantity { get; init; }

    [JsonPropertyName("multiplier")]
    public decimal? Multiplier { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("close_price")]
    public decimal? ClosePrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }

    [JsonPropertyName("unrealised")]
    public decimal? Unrealised { get; init; }

    [JsonPropertyName("realised")]
    public decimal? Realised { get; init; }

    [JsonPropertyName("buy_quantity")]
    public decimal? BuyQuantity { get; init; }

    [JsonPropertyName("sell_quantity")]
    public decimal? SellQuantity { get; init; }
}

internal sealed class KiteHolding
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public uint? InstrumentToken { get; init; }

    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    /// <summary>Settled quantity available to sell.</summary>
    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    /// <summary>Bought today, not yet delivered into the demat account.</summary>
    [JsonPropertyName("t1_quantity")]
    public decimal? T1Quantity { get; init; }

    /// <summary>Sold today out of the net holding. Cannot be sold again.</summary>
    [JsonPropertyName("used_quantity")]
    public decimal? UsedQuantity { get; init; }

    [JsonPropertyName("collateral_quantity")]
    public decimal? CollateralQuantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("close_price")]
    public decimal? ClosePrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }
}

/// <summary>
/// The funds payload, keyed by segment. This connector reads <c>equity</c>; <c>commodity</c>
/// describes an MCX balance it has no venue for.
/// </summary>
internal sealed class KiteMargins
{
    [JsonPropertyName("equity")]
    public KiteSegmentMargin? Equity { get; init; }

    [JsonPropertyName("commodity")]
    public KiteSegmentMargin? Commodity { get; init; }
}

internal sealed class KiteSegmentMargin
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>Net cash available for trading. This is the "can I place this order" number.</summary>
    [JsonPropertyName("net")]
    public decimal? Net { get; init; }

    [JsonPropertyName("available")]
    public KiteAvailableMargin? Available { get; init; }

    [JsonPropertyName("utilised")]
    public KiteUtilisedMargin? Utilised { get; init; }
}

internal sealed class KiteAvailableMargin
{
    [JsonPropertyName("cash")]
    public decimal? Cash { get; init; }

    [JsonPropertyName("opening_balance")]
    public decimal? OpeningBalance { get; init; }

    [JsonPropertyName("live_balance")]
    public decimal? LiveBalance { get; init; }

    /// <summary>Margin derived from pledged stock.</summary>
    [JsonPropertyName("collateral")]
    public decimal? Collateral { get; init; }

    [JsonPropertyName("intraday_payin")]
    public decimal? IntradayPayin { get; init; }

    [JsonPropertyName("adhoc_margin")]
    public decimal? AdhocMargin { get; init; }
}

internal sealed class KiteUtilisedMargin
{
    /// <summary>Sum of everything blocked. The closest thing Kite has to "used margin".</summary>
    [JsonPropertyName("debits")]
    public decimal? Debits { get; init; }

    [JsonPropertyName("m2m_realised")]
    public decimal? M2mRealised { get; init; }

    [JsonPropertyName("m2m_unrealised")]
    public decimal? M2mUnrealised { get; init; }

    [JsonPropertyName("span")]
    public decimal? Span { get; init; }

    [JsonPropertyName("exposure")]
    public decimal? Exposure { get; init; }

    [JsonPropertyName("option_premium")]
    public decimal? OptionPremium { get; init; }
}

// --- market data ------------------------------------------------------------------------------------

internal sealed class KiteQuote
{
    [JsonPropertyName("instrument_token")]
    public uint? InstrumentToken { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("last_trade_time")]
    public string? LastTradeTime { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("last_quantity")]
    public decimal? LastQuantity { get; init; }

    [JsonPropertyName("buy_quantity")]
    public long? BuyQuantity { get; init; }

    [JsonPropertyName("sell_quantity")]
    public long? SellQuantity { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("net_change")]
    public decimal? NetChange { get; init; }

    [JsonPropertyName("lower_circuit_limit")]
    public decimal? LowerCircuitLimit { get; init; }

    [JsonPropertyName("upper_circuit_limit")]
    public decimal? UpperCircuitLimit { get; init; }

    [JsonPropertyName("ohlc")]
    public KiteOhlc? Ohlc { get; init; }

    [JsonPropertyName("depth")]
    public KiteDepth? Depth { get; init; }
}

internal sealed class KiteOhlc
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

internal sealed class KiteDepth
{
    [JsonPropertyName("buy")]
    public List<KiteDepthLevel>? Buy { get; init; }

    [JsonPropertyName("sell")]
    public List<KiteDepthLevel>? Sell { get; init; }
}

internal sealed class KiteDepthLevel
{
    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("orders")]
    public int? Orders { get; init; }
}

/// <summary>
/// Historical candles, as a raw matrix: <c>[timestamp, open, high, low, close, volume]</c>, with
/// open interest appended as a seventh element when <c>oi=1</c> was asked for. Positional rather
/// than named, so the column order is a contract with the vendor and the only thing standing
/// between a chart and transposed highs and lows.
/// </summary>
internal sealed class KiteCandles
{
    [JsonPropertyName("candles")]
    public List<JsonElement>? Candles { get; init; }
}
