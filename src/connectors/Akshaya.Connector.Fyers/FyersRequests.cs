using System.Text.Json.Serialization;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// The body of a place-order call, and of one leg of a basket.
///
/// A typed class rather than an anonymous object because it is built once and then used three
/// ways — placement, basket leg, margin estimate — and because every property name here is a
/// FYERS wire name that differs from the C# one. An anonymous object would put those wire names
/// in three places and let them drift.
/// </summary>
internal sealed class FyersPlaceOrderBody
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("qty")]
    public required int Quantity { get; init; }

    [JsonPropertyName("type")]
    public required int Type { get; init; }

    [JsonPropertyName("side")]
    public required int Side { get; init; }

    [JsonPropertyName("productType")]
    public required string ProductType { get; init; }

    /// <summary>Zero, not null, when the order type has no limit price. FYERS rejects null.</summary>
    [JsonPropertyName("limitPrice")]
    public required decimal LimitPrice { get; init; }

    /// <summary>Zero, not null, when the order type has no trigger.</summary>
    [JsonPropertyName("stopPrice")]
    public required decimal StopPrice { get; init; }

    [JsonPropertyName("disclosedQty")]
    public required int DisclosedQuantity { get; init; }

    [JsonPropertyName("validity")]
    public required string Validity { get; init; }

    /// <summary>True for an after-market order, which is the only variety FYERS still exposes.</summary>
    [JsonPropertyName("offlineOrder")]
    public required bool OfflineOrder { get; init; }

    /// <summary>
    /// Attached stop-loss, in points from the entry. Always zero here: this connector does not
    /// place bracketed orders, and FYERS deprecated the bracket product in August 2026. Sent
    /// explicitly rather than omitted because the field is documented as mandatory.
    /// </summary>
    [JsonPropertyName("stopLoss")]
    public decimal StopLoss { get; init; }

    /// <summary>Attached target, in points from the entry. Always zero, for the same reason.</summary>
    [JsonPropertyName("takeProfit")]
    public decimal TakeProfit { get; init; }

    /// <summary>Carries this platform's ClientOrderId. See <see cref="FyersOrderTags"/>.</summary>
    [JsonPropertyName("orderTag")]
    public string? OrderTag { get; init; }

    /// <summary>
    /// Whether FYERS may split an oversized order into several smaller ones.
    ///
    /// False, deliberately. Slicing turns one ClientOrderId into several broker orders, and this
    /// connector's whole reconciliation story — match the order book on the tag, exactly once —
    /// assumes the mapping is one to one. A quantity above the exchange freeze limit should be
    /// split by the strategy that knows why, with its own ids, not silently by the broker.
    /// </summary>
    [JsonPropertyName("isSliceOrder")]
    public bool IsSliceOrder { get; init; }

    /// <summary>
    /// The same order as the margin calculator wants it. The two shapes are nearly identical,
    /// which is exactly why the difference is worth isolating here rather than rebuilding the
    /// body at the call site: the calculator takes no validity, no tag and no offline flag.
    /// </summary>
    public FyersMarginLeg ToMarginLeg() => new()
    {
        Symbol = Symbol,
        Quantity = Quantity,
        Side = Side,
        Type = Type,
        ProductType = ProductType,
        LimitPrice = LimitPrice,
        StopPrice = StopPrice,
    };
}

/// <summary>One order as the multi-order margin calculator wants it.</summary>
internal sealed class FyersMarginLeg
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("qty")]
    public required int Quantity { get; init; }

    [JsonPropertyName("side")]
    public required int Side { get; init; }

    [JsonPropertyName("type")]
    public required int Type { get; init; }

    [JsonPropertyName("productType")]
    public required string ProductType { get; init; }

    [JsonPropertyName("limitPrice")]
    public required decimal LimitPrice { get; init; }

    [JsonPropertyName("stopPrice")]
    public required decimal StopPrice { get; init; }

    [JsonPropertyName("stopLoss")]
    public decimal StopLoss { get; init; }

    [JsonPropertyName("takeProfit")]
    public decimal TakeProfit { get; init; }
}

/// <summary>
/// The body of a modify call.
///
/// Every field except <c>id</c> and <c>type</c> is optional, and an omitted one keeps its
/// existing value — which is why the nulls here matter and why the serialiser is configured to
/// drop them rather than send explicit nulls that FYERS would read as a change.
/// </summary>
internal sealed class FyersModifyOrderBody
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Mandatory even when unchanged. See <c>FyersOrders.ModifyAsync</c>.</summary>
    [JsonPropertyName("type")]
    public required int Type { get; init; }

    [JsonPropertyName("qty")]
    public int? Quantity { get; init; }

    [JsonPropertyName("limitPrice")]
    public decimal? LimitPrice { get; init; }

    [JsonPropertyName("stopPrice")]
    public decimal? StopPrice { get; init; }

    [JsonPropertyName("disclosedQty")]
    public int? DisclosedQuantity { get; init; }
}

/// <summary>
/// The body of a position conversion.
///
/// FYERS reuses the positions route for four different operations distinguished only by HTTP
/// verb, so this shape is what makes a POST there a conversion rather than anything else.
/// </summary>
internal sealed class FyersConvertPositionBody
{
    /// <summary>The POSITION id — "NSE:SBIN-EQ-INTRADAY" — despite the field being called symbol.</summary>
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    /// <summary>1 for a long position, -1 for a short one.</summary>
    [JsonPropertyName("positionSide")]
    public required int PositionSide { get; init; }

    [JsonPropertyName("convertQty")]
    public required int ConvertQuantity { get; init; }

    [JsonPropertyName("convertFrom")]
    public required string ConvertFrom { get; init; }

    [JsonPropertyName("convertTo")]
    public required string ConvertTo { get; init; }

    /// <summary>1 when the position was carried in from a previous day, 0 when it was opened today.</summary>
    [JsonPropertyName("overnight")]
    public required int Overnight { get; init; }
}
