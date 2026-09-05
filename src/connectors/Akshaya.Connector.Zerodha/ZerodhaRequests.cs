using System.Text.Json.Serialization;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// One order as the margin-and-charges calculator wants it.
///
/// The margin route is the one place Kite takes JSON rather than a form, so this is the one
/// request shape in the connector that is a typed object instead of a list of key-value pairs.
/// Its fields mirror a placement exactly, which is the point: an estimate built from anything
/// other than the order it is quoting is worse than no estimate, because it looks authoritative.
/// </summary>
internal sealed class KiteMarginRequest
{
    [JsonPropertyName("exchange")]
    public required string Exchange { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public required string TradingSymbol { get; init; }

    [JsonPropertyName("transaction_type")]
    public required string TransactionType { get; init; }

    [JsonPropertyName("variety")]
    public required string Variety { get; init; }

    [JsonPropertyName("product")]
    public required string Product { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; }

    [JsonPropertyName("quantity")]
    public required long Quantity { get; init; }

    /// <summary>Zero, not null, when the order type has no limit price.</summary>
    [JsonPropertyName("price")]
    public required decimal Price { get; init; }

    /// <summary>Zero, not null, when the order type has no trigger.</summary>
    [JsonPropertyName("trigger_price")]
    public required decimal TriggerPrice { get; init; }
}
