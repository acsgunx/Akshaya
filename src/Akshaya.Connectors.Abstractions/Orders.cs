using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

/// <summary>
/// An order as the platform expresses it. Note what is NOT here: no "product" string, no
/// "variety" string, no exchange name. All of that is canonical and the connector maps it.
/// </summary>
public sealed record PlaceOrderRequest
{
    /// <summary>
    /// Caller-generated and persisted BEFORE the broker call. On a timeout we re-read the
    /// order book and match on this rather than retrying — which is the difference between
    /// a recovered order and a duplicate one.
    /// </summary>
    public required Guid ClientOrderId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required OrderType OrderType { get; init; }

    public required PositionEffect PositionEffect { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;

    public OrderVariety Variety { get; init; } = OrderVariety.Regular;

    /// <summary>Required for Limit and StopLimit. Carries its currency — cross-border orders exist.</summary>
    public Money? LimitPrice { get; init; }

    /// <summary>Required for Stop, StopLimit and trailing variants.</summary>
    public Money? TriggerPrice { get; init; }

    public Quantity? DisclosedQuantity { get; init; }

    /// <summary>Required when TimeInForce is Gtd.</summary>
    public DateOnly? GoodTillDate { get; init; }

    /// <summary>
    /// Identifies the strategy that produced this order. Several jurisdictions require
    /// automated orders to be tagged; carrying it in the contract means no connector has to
    /// invent its own field for it.
    /// </summary>
    public string? AlgoId { get; init; }

    public string? Tag { get; init; }
}

public sealed record ModifyOrderRequest
{
    public required string BrokerOrderId { get; init; }

    public Quantity? Quantity { get; init; }

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public OrderType? OrderType { get; init; }

    public TimeInForce? TimeInForce { get; init; }

    public Quantity? DisclosedQuantity { get; init; }
}

/// <summary>What the broker said when we sent an order. Deliberately thin: the truth is the order book.</summary>
public sealed record OrderAck
{
    public required string BrokerOrderId { get; init; }

    public required OrderStatus Status { get; init; }

    public Guid? ClientOrderId { get; init; }

    public string? Message { get; init; }

    public required DateTimeOffset AcknowledgedAt { get; init; }
}

/// <summary>An order as it currently stands at the broker. This is the source of truth, not our copy.</summary>
public sealed record BrokerOrder
{
    public required string BrokerOrderId { get; init; }

    public Guid? ClientOrderId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required Quantity FilledQuantity { get; init; }

    public required OrderStatus Status { get; init; }

    public required OrderType OrderType { get; init; }

    public required PositionEffect PositionEffect { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;

    public OrderVariety Variety { get; init; } = OrderVariety.Regular;

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public Money? AveragePrice { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The broker's own rejection text. Always show this to the trader verbatim.</summary>
    public string? StatusMessage { get; init; }

    public Quantity PendingQuantity => new(Quantity.Value - FilledQuantity.Value);
}

public sealed record BrokerTrade
{
    public required string TradeId { get; init; }

    public required string BrokerOrderId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required Money Price { get; init; }

    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>Populated when the broker reports charges per trade; otherwise estimated locally.</summary>
    public Money? Charges { get; init; }
}

public sealed record MarginEstimate
{
    public required Money Required { get; init; }

    public Money? Available { get; init; }

    public bool IsSufficient => Available is null || Available.Value >= Required;
}

/// <summary>
/// Itemised so the UI can show a trader exactly where the money went. Every market's
/// breakdown differs (STT and stamp duty in India, SGX clearing fees, SEC/TAF in the US),
/// so this is a list of named lines rather than fixed fields.
/// </summary>
public sealed record ChargesEstimate
{
    public required IReadOnlyList<ChargeLine> Lines { get; init; }

    public required Money Total { get; init; }
}

public sealed record ChargeLine(string Name, Money Amount, string? Note = null);

public sealed record OrderQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public InstrumentKey? Instrument { get; init; }

    public bool OpenOnly { get; init; }
}

public interface IConnectorOrders
{
    Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default);

    Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default);

    Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default);

    Task<Result<int>> CancelAllAsync(CancellationToken ct = default);

    Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(OrderQuery query, CancellationToken ct = default);

    Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(OrderQuery query, CancellationToken ct = default);

    /// <summary>
    /// All-or-nothing at the broker where supported; connectors that fake it by looping must
    /// say so in their manifest so the UI can warn about partial execution.
    /// </summary>
    Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default);

    Task<Result<MarginEstimate>> EstimateMarginAsync(PlaceOrderRequest request, CancellationToken ct = default);

    Task<Result<ChargesEstimate>> EstimateChargesAsync(PlaceOrderRequest request, CancellationToken ct = default);
}
