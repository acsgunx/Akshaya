using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Wire shapes for the FYERS API v3.
///
/// These are all <c>internal</c> and all deliberately dumb: every field is nullable and no
/// field is validated here. Vendor payloads drift — a field that has been an integer for two
/// years turns into a string, an object turns into an array of one — and a DTO that throws on
/// deserialisation takes the whole order book down with it. Validation happens in the facets,
/// where a missing field can be turned into a proper <c>Result</c> failure that names it.
///
/// Timestamps are carried as strings and parsed by <see cref="FyersTime"/>, because FYERS sends
/// naive local datetimes ("18-Dec-2023 16:33:24") with no offset, and letting System.Text.Json
/// bind those to a DateTimeOffset silently stamps them with the SERVER's offset — which turns
/// every timestamp on a UTC container into a five-and-a-half-hour lie.
/// </summary>
internal static class FyersJson
{
    /// <summary>
    /// Shared serialiser settings.
    ///
    /// Two of these are load-bearing rather than stylistic. <see cref="LenientStringConverter"/>
    /// exists because FYERS documents <c>fyToken</c>, <c>id</c> and <c>tradeNumber</c> as strings
    /// and then sends them as bare JSON numbers on several routes — holdings and the multi-order
    /// modify response among them. Without it, one numeric id fails the whole document and the
    /// holdings list comes back as "the response could not be understood".
    /// <see cref="JsonNumberHandling.AllowReadingFromString"/> covers the mirror image, where a
    /// documented number arrives quoted.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new LenientStringConverter() },
    };

    /// <summary>The value of <c>s</c> on a successful response.</summary>
    public const string StatusOk = "ok";

    /// <summary>The value of <c>s</c> on a failed one, including HTTP 200 failures.</summary>
    public const string StatusError = "error";

    /// <summary>
    /// The response field carrying the status, for <c>ConnectorHttpOptions.BodyStatusField</c>.
    /// </summary>
    public const string StatusField = "s";
}

/// <summary>
/// Reads a JSON string that the vendor sometimes sends as a number or a boolean.
///
/// Deliberately narrow: it only widens what can be READ. Writing is unchanged, so a request
/// body still serialises a string as a string and this cannot quietly reshape an outgoing
/// order.
/// </summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Number => reader.TryGetInt64(out var integral)
                ? integral.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException(
                $"Expected a string but found {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// The three fields every FYERS response carries.
///
/// Unlike most broker APIs, FYERS does NOT nest its payload under a common <c>data</c> key —
/// the order book arrives under <c>orderBook</c>, funds under <c>fund_limit</c>, quotes under
/// <c>d</c>. So this is a base class each route's response extends, rather than a generic
/// envelope with a payload parameter.
/// </summary>
internal abstract class FyersResponse
{
    /// <summary>"ok" or "error". Present even on HTTP 200 failures, which is the point of it.</summary>
    [JsonPropertyName("s")]
    public string? Status { get; init; }

    /// <summary>
    /// A positive code on success and a negative one on failure, with a documented meaning
    /// (-8 expired token, -50 bad parameters, -99 rejected order, -300 bad symbol).
    /// </summary>
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool IsOk =>
        Status is null || string.Equals(Status, FyersJson.StatusOk, StringComparison.OrdinalIgnoreCase);
}

// --- authentication -------------------------------------------------------------------------

internal sealed class FyersTokenResponse : FyersResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }
}

internal sealed class FyersProfileResponse : FyersResponse
{
    [JsonPropertyName("data")]
    public FyersProfile? Data { get; init; }
}

internal sealed class FyersProfile
{
    /// <summary>The FYERS client id — "FX0011". This is the account identifier.</summary>
    [JsonPropertyName("fy_id")]
    public string? FyId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>Whether the account may use the margin-trading product at all.</summary>
    [JsonPropertyName("mtf_enabled")]
    public bool? MtfEnabled { get; init; }

    /// <summary>Whether the account has a DDPI mandate, which decides how a sell is authorised.</summary>
    [JsonPropertyName("ddpi_enabled")]
    public bool? DdpiEnabled { get; init; }
}

// --- orders ----------------------------------------------------------------------------------

/// <summary>A place, modify or cancel acknowledgement.</summary>
internal sealed class FyersOrderIdResponse : FyersResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

internal sealed class FyersOrderBookResponse : FyersResponse
{
    [JsonPropertyName("orderBook")]
    public List<FyersOrder>? OrderBook { get; init; }
}

internal sealed class FyersOrder
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("exchOrdId")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("qty")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("filledQty")]
    public decimal? FilledQuantity { get; init; }

    [JsonPropertyName("remainingQuantity")]
    public decimal? RemainingQuantity { get; init; }

    [JsonPropertyName("disclosedQty")]
    public decimal? DisclosedQuantity { get; init; }

    [JsonPropertyName("limitPrice")]
    public decimal? LimitPrice { get; init; }

    [JsonPropertyName("stopPrice")]
    public decimal? StopPrice { get; init; }

    [JsonPropertyName("tradedPrice")]
    public decimal? TradedPrice { get; init; }

    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("side")]
    public int? Side { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("productType")]
    public string? ProductType { get; init; }

    [JsonPropertyName("orderValidity")]
    public string? Validity { get; init; }

    [JsonPropertyName("orderDateTime")]
    public string? OrderDateTime { get; init; }

    [JsonPropertyName("offlineOrder")]
    public bool? OfflineOrder { get; init; }

    /// <summary>The rejection reason, when there is one. Always shown to the trader verbatim.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Carries this platform's ClientOrderId. See <see cref="FyersOrderTags"/>.</summary>
    [JsonPropertyName("orderTag")]
    public string? OrderTag { get; init; }

    [JsonPropertyName("exchange")]
    public int? Exchange { get; init; }

    [JsonPropertyName("segment")]
    public int? Segment { get; init; }

    [JsonPropertyName("fyToken")]
    public string? FyToken { get; init; }
}

internal sealed class FyersTradeBookResponse : FyersResponse
{
    [JsonPropertyName("tradeBook")]
    public List<FyersTrade>? TradeBook { get; init; }
}

internal sealed class FyersTrade
{
    [JsonPropertyName("tradeNumber")]
    public string? TradeNumber { get; init; }

    [JsonPropertyName("orderNumber")]
    public string? OrderNumber { get; init; }

    [JsonPropertyName("exchangeOrderNo")]
    public string? ExchangeOrderNumber { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("side")]
    public int? Side { get; init; }

    [JsonPropertyName("tradedQty")]
    public decimal? TradedQuantity { get; init; }

    [JsonPropertyName("tradePrice")]
    public decimal? TradePrice { get; init; }

    [JsonPropertyName("tradeValue")]
    public decimal? TradeValue { get; init; }

    [JsonPropertyName("orderDateTime")]
    public string? OrderDateTime { get; init; }

    [JsonPropertyName("productType")]
    public string? ProductType { get; init; }

    [JsonPropertyName("orderTag")]
    public string? OrderTag { get; init; }

    [JsonPropertyName("exchange")]
    public int? Exchange { get; init; }

    [JsonPropertyName("segment")]
    public int? Segment { get; init; }
}

/// <summary>
/// The multi-order response. Each leg carries its OWN status: a 200 on the outer call says
/// nothing about whether any individual order was accepted, which is exactly why the manifest
/// declares this basket non-atomic.
/// </summary>
internal sealed class FyersMultiOrderResponse : FyersResponse
{
    [JsonPropertyName("data")]
    public List<FyersMultiOrderLeg>? Data { get; init; }
}

internal sealed class FyersMultiOrderLeg
{
    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; init; }

    [JsonPropertyName("body")]
    public FyersOrderIdResponse? Body { get; init; }

    [JsonPropertyName("statusDescription")]
    public string? StatusDescription { get; init; }
}

internal sealed class FyersMarginResponse : FyersResponse
{
    [JsonPropertyName("data")]
    public FyersMargin? Data { get; init; }
}

internal sealed class FyersMargin
{
    /// <summary>Margin this order alone requires.</summary>
    [JsonPropertyName("margin_new_order")]
    public decimal? MarginNewOrder { get; init; }

    /// <summary>Margin required including existing positions, after any hedge benefit.</summary>
    [JsonPropertyName("margin_total")]
    public decimal? MarginTotal { get; init; }

    [JsonPropertyName("margin_avail")]
    public decimal? MarginAvailable { get; init; }
}

// --- portfolio ---------------------------------------------------------------------------------

internal sealed class FyersPositionsResponse : FyersResponse
{
    [JsonPropertyName("netPositions")]
    public List<FyersPosition>? NetPositions { get; init; }
}

internal sealed class FyersPosition
{
    /// <summary>Position id — "NSE:SBIN-EQ-INTRADAY". Required by the conversion and exit routes.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    /// <summary>Signed net quantity. Negative is short.</summary>
    [JsonPropertyName("netQty")]
    public decimal? NetQuantity { get; init; }

    [JsonPropertyName("netAvg")]
    public decimal? NetAveragePrice { get; init; }

    [JsonPropertyName("avgPrice")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("buyQty")]
    public decimal? BuyQuantity { get; init; }

    [JsonPropertyName("buyAvg")]
    public decimal? BuyAveragePrice { get; init; }

    [JsonPropertyName("sellQty")]
    public decimal? SellQuantity { get; init; }

    [JsonPropertyName("sellAvg")]
    public decimal? SellAveragePrice { get; init; }

    [JsonPropertyName("productType")]
    public string? ProductType { get; init; }

    [JsonPropertyName("realized_profit")]
    public decimal? RealisedProfit { get; init; }

    [JsonPropertyName("unrealized_profit")]
    public decimal? UnrealisedProfit { get; init; }

    [JsonPropertyName("pl")]
    public decimal? ProfitAndLoss { get; init; }

    [JsonPropertyName("ltp")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("side")]
    public int? Side { get; init; }

    [JsonPropertyName("exchange")]
    public int? Exchange { get; init; }

    [JsonPropertyName("segment")]
    public int? Segment { get; init; }

    /// <summary>
    /// Quantity carried in from a previous day. Non-zero is what makes a position "overnight",
    /// which the conversion route needs told explicitly.
    /// </summary>
    [JsonPropertyName("cfBuyQty")]
    public decimal? CarriedForwardBuyQuantity { get; init; }

    /// <inheritdoc cref="CarriedForwardBuyQuantity"/>
    [JsonPropertyName("cfSellQty")]
    public decimal? CarriedForwardSellQuantity { get; init; }

    /// <summary>"Y" for a cross-currency position, whose P&amp;L needs the RBI reference rate.</summary>
    [JsonPropertyName("crossCurrency")]
    public string? CrossCurrency { get; init; }
}

internal sealed class FyersHoldingsResponse : FyersResponse
{
    [JsonPropertyName("holdings")]
    public List<FyersHolding>? Holdings { get; init; }
}

internal sealed class FyersHolding
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    /// <summary>Quantity at the start of the day, before anything sold today.</summary>
    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    /// <summary>Quantity minus whatever was sold today. This is what can still be sold.</summary>
    [JsonPropertyName("remainingQuantity")]
    public decimal? RemainingQuantity { get; init; }

    [JsonPropertyName("costPrice")]
    public decimal? CostPrice { get; init; }

    [JsonPropertyName("marketVal")]
    public decimal? MarketValue { get; init; }

    [JsonPropertyName("ltp")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("pl")]
    public decimal? ProfitAndLoss { get; init; }

    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    /// <summary>"HLD" when settled in the demat account, "T1" when bought and not yet delivered.</summary>
    [JsonPropertyName("holdingType")]
    public string? HoldingType { get; init; }

    /// <summary>Quantity pledged as collateral. Cannot be sold without unpledging first.</summary>
    [JsonPropertyName("collateralQuantity")]
    public decimal? CollateralQuantity { get; init; }

    [JsonPropertyName("exchange")]
    public int? Exchange { get; init; }

    [JsonPropertyName("segment")]
    public int? Segment { get; init; }
}

internal sealed class FyersFundsResponse : FyersResponse
{
    [JsonPropertyName("fund_limit")]
    public List<FyersFundLimit>? FundLimit { get; init; }
}

/// <summary>
/// One line of the funds ledger.
///
/// The lines are addressed by <see cref="Id"/> and never by <see cref="Title"/>. The title is
/// display text — "Available Balance", "Limit at start of the day" — and matching on it would
/// break the day FYERS improves its wording, silently, by reporting a zero balance.
/// </summary>
internal sealed class FyersFundLimit
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("equityAmount")]
    public decimal? EquityAmount { get; init; }

    [JsonPropertyName("commodityAmount")]
    public decimal? CommodityAmount { get; init; }
}

// --- market data -----------------------------------------------------------------------------

internal sealed class FyersQuotesResponse : FyersResponse
{
    [JsonPropertyName("d")]
    public List<FyersQuoteEntry>? Quotes { get; init; }
}

/// <summary>One symbol's quote, with its own per-symbol status: a bad symbol in a batch of
/// fifty fails only its own entry.</summary>
internal sealed class FyersQuoteEntry
{
    [JsonPropertyName("n")]
    public string? Name { get; init; }

    [JsonPropertyName("s")]
    public string? Status { get; init; }

    [JsonPropertyName("v")]
    public FyersQuoteValues? Values { get; init; }
}

internal sealed class FyersQuoteValues
{
    [JsonPropertyName("lp")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("open_price")]
    public decimal? Open { get; init; }

    [JsonPropertyName("high_price")]
    public decimal? High { get; init; }

    [JsonPropertyName("low_price")]
    public decimal? Low { get; init; }

    [JsonPropertyName("prev_close_price")]
    public decimal? PreviousClose { get; init; }

    [JsonPropertyName("bid")]
    public decimal? Bid { get; init; }

    [JsonPropertyName("ask")]
    public decimal? Ask { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    /// <summary>Quote timestamp, as an epoch. FYERS sends it quoted on some routes.</summary>
    [JsonPropertyName("tt")]
    public string? Timestamp { get; init; }
}

internal sealed class FyersDepthResponse : FyersResponse
{
    /// <summary>Keyed by the symbol asked for.</summary>
    [JsonPropertyName("d")]
    public Dictionary<string, FyersDepth>? Depth { get; init; }
}

internal sealed class FyersDepth
{
    [JsonPropertyName("bids")]
    public List<FyersDepthLevel>? Bids { get; init; }

    /// <summary>Singular on the wire, unlike <c>bids</c>. Not a typo to be tidied up.</summary>
    [JsonPropertyName("ask")]
    public List<FyersDepthLevel>? Asks { get; init; }

    [JsonPropertyName("ltp")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("ltt")]
    public long? LastTradedTime { get; init; }

    [JsonPropertyName("o")]
    public decimal? Open { get; init; }

    [JsonPropertyName("h")]
    public decimal? High { get; init; }

    [JsonPropertyName("l")]
    public decimal? Low { get; init; }

    [JsonPropertyName("c")]
    public decimal? PreviousClose { get; init; }

    [JsonPropertyName("v")]
    public long? Volume { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterest { get; init; }
}

internal sealed class FyersDepthLevel
{
    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("volume")]
    public decimal? Volume { get; init; }

    [JsonPropertyName("ord")]
    public int? Orders { get; init; }
}

/// <summary>
/// Historical candles, as a raw numeric matrix: <c>[epoch, open, high, low, close, volume]</c>.
/// Positional rather than named, so the column order below is a contract with the vendor and
/// the only thing standing between a chart and transposed highs and lows.
/// </summary>
internal sealed class FyersHistoryResponse : FyersResponse
{
    [JsonPropertyName("candles")]
    public List<decimal[]>? Candles { get; init; }
}

internal sealed class FyersOptionChainResponse : FyersResponse
{
    [JsonPropertyName("data")]
    public FyersOptionChainData? Data { get; init; }
}

internal sealed class FyersOptionChainData
{
    [JsonPropertyName("expiryData")]
    public List<FyersOptionExpiry>? ExpiryData { get; init; }

    [JsonPropertyName("optionsChain")]
    public List<FyersOptionChainRow>? OptionsChain { get; init; }

    [JsonPropertyName("callOi")]
    public long? CallOpenInterest { get; init; }

    [JsonPropertyName("putOi")]
    public long? PutOpenInterest { get; init; }
}

internal sealed class FyersOptionExpiry
{
    /// <summary>"24-03-2026".</summary>
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    /// <summary>Epoch seconds. This is the value the chain route wants back as its timestamp.</summary>
    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    /// <summary>"W" for a weekly expiry, "M" for a monthly one.</summary>
    [JsonPropertyName("expiry_flag")]
    public string? ExpiryFlag { get; init; }
}

/// <summary>
/// One row of the option chain. The first row is the UNDERLYING, not an option: it carries
/// <c>option_type: ""</c> and <c>strike_price: -1</c>, and reading it as a contract would put a
/// phantom strike of minus one in the chain.
/// </summary>
internal sealed class FyersOptionChainRow
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("option_type")]
    public string? OptionType { get; init; }

    [JsonPropertyName("strike_price")]
    public decimal? StrikePrice { get; init; }

    [JsonPropertyName("ltp")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("bid")]
    public decimal? Bid { get; init; }

    [JsonPropertyName("ask")]
    public decimal? Ask { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("ltpch")]
    public decimal? Change { get; init; }

    [JsonPropertyName("fyToken")]
    public string? FyToken { get; init; }
}
