using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Tiger;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  Tiger OpenAPI shapes, transcribed from the official Python SDK's parsers. Scalars are strings
//  (see TigerJson) and are parsed where they are used; only the fields this connector reads are here.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The envelope every method answers with. <c>data</c> is sometimes a JSON string rather than an object.</summary>
internal sealed record TigerEnvelope
{
    [JsonPropertyName("code")]
    public long Code { get; init; } = -1;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    /// <summary>Tiger's signature over the request's timestamp, which proves the answer came from Tiger.</summary>
    [JsonPropertyName("sign")]
    public string? Sign { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

/// <summary>
/// The contract fields Tiger repeats on every row — an order, a position, an execution, a contract lookup. They are
/// enough to describe a security without a second request, except for a US listing venue.
/// </summary>
internal sealed record TigerContractFields
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("primaryExchange")]
    public string? PrimaryExchange { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("lotSize")]
    public string? LotSize { get; init; }

    [JsonPropertyName("minTick")]
    public string? MinTick { get; init; }
}

/// <summary>One order. The contract fields are flattened into the same object.</summary>
internal sealed record TigerOrder
{
    /// <summary>Tiger's global order id: what cancel and modify take.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The account-scoped order number, kept for support conversations.</summary>
    [JsonPropertyName("orderId")]
    public string? OrderNumber { get; init; }

    [JsonPropertyName("account")]
    public string? Account { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("orderType")]
    public string? OrderType { get; init; }

    [JsonPropertyName("limitPrice")]
    public string? LimitPrice { get; init; }

    [JsonPropertyName("auxPrice")]
    public string? AuxPrice { get; init; }

    [JsonPropertyName("totalQuantity")]
    public string? TotalQuantity { get; init; }

    [JsonPropertyName("filledQuantity")]
    public string? FilledQuantity { get; init; }

    [JsonPropertyName("avgFillPrice")]
    public string? AverageFillPrice { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("timeInForce")]
    public string? TimeInForce { get; init; }

    [JsonPropertyName("outsideRth")]
    public string? OutsideRegularHours { get; init; }

    [JsonPropertyName("openTime")]
    public string? OpenTime { get; init; }

    [JsonPropertyName("latestTime")]
    public string? LatestTime { get; init; }

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; init; }

    /// <summary>Tiger's own words when it refuses or cancels an order.</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; init; }

    /// <summary>The caller's marker, which carries the ClientOrderId.</summary>
    [JsonPropertyName("userMark")]
    public string? UserMark { get; init; }

    [JsonPropertyName("commission")]
    public string? Commission { get; init; }

    // --- the contract, flattened ---

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("primaryExchange")]
    public string? PrimaryExchange { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    public TigerContractFields Contract() => new()
    {
        Symbol = Symbol,
        SecType = SecType,
        Market = Market,
        Currency = Currency,
        Exchange = Exchange,
        PrimaryExchange = PrimaryExchange,
        Expiry = Expiry,
        Strike = Strike,
        Right = Right,
        Multiplier = Multiplier,
        Identifier = Identifier,
    };
}

/// <summary>A page of orders, executions or positions.</summary>
internal sealed record TigerPage<T>
{
    [JsonPropertyName("items")]
    public List<T>? Items { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }
}

/// <summary>What a submitted order answers with.</summary>
internal sealed record TigerPlacedOrder
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("orderId")]
    public string? OrderNumber { get; init; }
}

internal sealed record TigerPosition
{
    [JsonPropertyName("position")]
    public string? Quantity { get; init; }

    [JsonPropertyName("averageCost")]
    public string? AverageCost { get; init; }

    [JsonPropertyName("latestPrice")]
    public string? LatestPrice { get; init; }

    [JsonPropertyName("marketValue")]
    public string? MarketValue { get; init; }

    [JsonPropertyName("unrealizedPnl")]
    public string? UnrealisedPnl { get; init; }

    [JsonPropertyName("realizedPnl")]
    public string? RealisedPnl { get; init; }

    // --- the contract, flattened ---

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("primaryExchange")]
    public string? PrimaryExchange { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    public TigerContractFields Contract() => new()
    {
        Symbol = Symbol,
        SecType = SecType,
        Market = Market,
        Currency = Currency,
        Exchange = Exchange,
        PrimaryExchange = PrimaryExchange,
        Expiry = Expiry,
        Strike = Strike,
        Right = Right,
        Multiplier = Multiplier,
        Identifier = Identifier,
    };
}

internal sealed record TigerTransaction
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("filledQuantity")]
    public string? FilledQuantity { get; init; }

    [JsonPropertyName("filledPrice")]
    public string? FilledPrice { get; init; }

    [JsonPropertyName("filledAmount")]
    public string? FilledAmount { get; init; }

    [JsonPropertyName("transactedAt")]
    public string? TransactedAt { get; init; }

    [JsonPropertyName("commission")]
    public string? Commission { get; init; }

    // --- the contract, flattened ---

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("primaryExchange")]
    public string? PrimaryExchange { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    public TigerContractFields Contract() => new()
    {
        Symbol = Symbol,
        SecType = SecType,
        Market = Market,
        Currency = Currency,
        PrimaryExchange = PrimaryExchange,
        Expiry = Expiry,
        Strike = Strike,
        Right = Right,
        Multiplier = Multiplier,
        Identifier = Identifier,
    };
}

/// <summary>Prime assets: segments by asset class, each with a row per currency.</summary>
internal sealed record TigerPrimeAssets
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("segments")]
    public List<TigerSegment>? Segments { get; init; }
}

internal sealed record TigerSegment
{
    /// <summary><c>S</c> for securities, <c>C</c> for commodities.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("buyingPower")]
    public string? BuyingPower { get; init; }

    [JsonPropertyName("initMargin")]
    public string? InitialMargin { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public string? UnrealisedPnl { get; init; }

    [JsonPropertyName("realizedPL")]
    public string? RealisedPnl { get; init; }

    [JsonPropertyName("currencyAssets")]
    public List<TigerCurrencyAsset>? CurrencyAssets { get; init; }
}

internal sealed record TigerCurrencyAsset
{
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("cashBalance")]
    public string? CashBalance { get; init; }

    [JsonPropertyName("cashAvailableForTrade")]
    public string? CashAvailableForTrade { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public string? UnrealisedPnl { get; init; }

    [JsonPropertyName("realizedPL")]
    public string? RealisedPnl { get; init; }
}

/// <summary>An order preview: what the order would cost and require.</summary>
internal sealed record TigerPreview
{
    [JsonPropertyName("initMargin")]
    public string? InitialMargin { get; init; }

    [JsonPropertyName("maintMargin")]
    public string? MaintenanceMargin { get; init; }

    [JsonPropertyName("equityWithLoan")]
    public string? EquityWithLoan { get; init; }

    [JsonPropertyName("commission")]
    public string? Commission { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("marginCurrency")]
    public string? MarginCurrency { get; init; }

    [JsonPropertyName("commissionCurrency")]
    public string? CommissionCurrency { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("warningText")]
    public string? WarningText { get; init; }
}

/// <summary>A contract as the contract lookups describe it.</summary>
internal sealed record TigerContract
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("secType")]
    public string? SecType { get; init; }

    [JsonPropertyName("market")]
    public string? Market { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("primaryExchange")]
    public string? PrimaryExchange { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("lotSize")]
    public string? LotSize { get; init; }

    [JsonPropertyName("minTick")]
    public string? MinTick { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("tradeable")]
    public string? Tradeable { get; init; }

    public TigerContractFields Fields() => new()
    {
        Symbol = Symbol,
        SecType = SecType,
        Market = Market,
        Currency = Currency,
        Exchange = Exchange,
        PrimaryExchange = PrimaryExchange,
        Expiry = Expiry,
        Strike = Strike,
        Right = Right,
        Multiplier = Multiplier,
        Identifier = Identifier,
        Name = Name,
        LotSize = LotSize,
        MinTick = MinTick,
    };
}

/// <summary>A real-time stock quote.</summary>
internal sealed record TigerQuoteBrief
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("open")]
    public string? Open { get; init; }

    [JsonPropertyName("high")]
    public string? High { get; init; }

    [JsonPropertyName("low")]
    public string? Low { get; init; }

    [JsonPropertyName("preClose")]
    public string? PreviousClose { get; init; }

    [JsonPropertyName("latestPrice")]
    public string? LatestPrice { get; init; }

    [JsonPropertyName("latestTime")]
    public string? LatestTime { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("askPrice")]
    public string? AskPrice { get; init; }

    [JsonPropertyName("askSize")]
    public string? AskSize { get; init; }

    [JsonPropertyName("bidPrice")]
    public string? BidPrice { get; init; }

    [JsonPropertyName("bidSize")]
    public string? BidSize { get; init; }

    [JsonPropertyName("volume")]
    public string? Volume { get; init; }
}

/// <summary>One symbol's bars, with the token that continues them.</summary>
internal sealed record TigerBarSeries
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    [JsonPropertyName("items")]
    public List<TigerBar>? Items { get; init; }
}

internal sealed record TigerBar
{
    /// <summary>Epoch milliseconds at the bar's open.</summary>
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("open")]
    public string? Open { get; init; }

    [JsonPropertyName("high")]
    public string? High { get; init; }

    [JsonPropertyName("low")]
    public string? Low { get; init; }

    [JsonPropertyName("close")]
    public string? Close { get; init; }

    [JsonPropertyName("volume")]
    public string? Volume { get; init; }
}

internal sealed record TigerDepthBook
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("asks")]
    public List<TigerDepthLevel>? Asks { get; init; }

    [JsonPropertyName("bids")]
    public List<TigerDepthLevel>? Bids { get; init; }
}

internal sealed record TigerDepthLevel
{
    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("volume")]
    public string? Volume { get; init; }

    [JsonPropertyName("count")]
    public string? Count { get; init; }
}

/// <summary>One expiry's chain: each item holds the call and the put at a strike.</summary>
internal sealed record TigerOptionChainGroup
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("items")]
    public List<Dictionary<string, TigerOptionQuote>>? Items { get; init; }
}

/// <summary>An option's quote, from the chain or from the option brief.</summary>
internal sealed record TigerOptionQuote
{
    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("expiry")]
    public string? Expiry { get; init; }

    [JsonPropertyName("strike")]
    public string? Strike { get; init; }

    [JsonPropertyName("right")]
    public string? Right { get; init; }

    [JsonPropertyName("multiplier")]
    public string? Multiplier { get; init; }

    [JsonPropertyName("latestPrice")]
    public string? LatestPrice { get; init; }

    [JsonPropertyName("preClose")]
    public string? PreviousClose { get; init; }

    [JsonPropertyName("open")]
    public string? Open { get; init; }

    [JsonPropertyName("high")]
    public string? High { get; init; }

    [JsonPropertyName("low")]
    public string? Low { get; init; }

    [JsonPropertyName("askPrice")]
    public string? AskPrice { get; init; }

    [JsonPropertyName("bidPrice")]
    public string? BidPrice { get; init; }

    [JsonPropertyName("volume")]
    public string? Volume { get; init; }

    [JsonPropertyName("openInterest")]
    public string? OpenInterest { get; init; }

    [JsonPropertyName("openInt")]
    public string? OpenInt { get; init; }

    [JsonPropertyName("latestTime")]
    public string? LatestTime { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }
}
