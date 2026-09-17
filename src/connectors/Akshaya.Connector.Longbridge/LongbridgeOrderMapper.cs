using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Longbridge order and execution rows to canonical <see cref="BrokerOrder"/> and <see cref="BrokerTrade"/>,
/// shared by the orders facet and the stream so a pushed update and a book read can never disagree.
///
/// Callers resolve the rows' securities into the cache FIRST (<see cref="LongbridgeInstrumentResolver.EnsureAsync"/>);
/// mapping itself never touches the network.
/// </summary>
internal static class LongbridgeOrderMapper
{
    /// <param name="row">A book row, an order detail or an order-changed push.</param>
    /// <param name="cache">Must already hold the row's security.</param>
    /// <param name="clock">Stamps a row that carries no submission time.</param>
    /// <param name="lenient">True for book reads and pushes: an unmapped status degrades to Unknown rather than failing the book.</param>
    public static Result<BrokerOrder> MapOrder(LbOrder row, LongbridgeInstrumentCache cache, IClock clock, bool lenient)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.OrderId) || string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerOrder>.Failure(LongbridgeErrors.MissingField("order", "order_id/symbol"));
        }

        var native = row.Symbol.Trim().ToUpperInvariant();
        if (!cache.TryGetByNative(native, out var record))
        {
            return Result<BrokerOrder>.Failure(LongbridgeNative.NotFound(native));
        }

        var side = LongbridgeMaps.ToCanonicalSide(row.Side);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = LongbridgeMaps.ToCanonicalOrderType(row.OrderType);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        // The order-changed push carries no time in force; a push therefore reads as Day until the book is read.
        var timeInForce = LongbridgeMaps.ToCanonicalTimeInForce(row.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<BrokerOrder>.Failure(timeInForce.Error);
        }

        string? rawStatus = null;
        OrderStatus status;
        if (lenient)
        {
            status = LongbridgeMaps.ToCanonicalOrderStatusOrUnknown(row.Status, out rawStatus);
        }
        else
        {
            var strict = LongbridgeMaps.ToCanonicalOrderStatus(row.Status);
            if (strict.IsFailure)
            {
                return Result<BrokerOrder>.Failure(strict.Error);
            }

            status = strict.Value;
        }

        // Book rows say quantity; the push says submitted_quantity.
        var quantity = LongbridgeNumber.Decimal(row.Quantity) ?? LongbridgeNumber.DecimalOrZero(row.SubmittedQuantity);
        var filled = LongbridgeNumber.DecimalOrZero(row.ExecutedQuantity);

        if (status == OrderStatus.Open && filled > 0m && filled < quantity)
        {
            status = OrderStatus.PartiallyFilled;
        }

        var currency = LongbridgeMaps.ToCanonicalCurrency(row.Currency) is { IsSuccess: true } rowCurrency
            ? rowCurrency.Value
            : record.Definition.Currency;

        return new BrokerOrder
        {
            BrokerOrderId = row.OrderId.Trim(),
            ClientOrderId = ParseClientOrderId(row.Remark),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,

            // Longbridge reports no short flag on an order: a sell is a sell, and whether it opened a short is
            // the account's business.
            PositionEffect = PositionEffect.Delivery,
            TimeInForce = timeInForce.Value,
            LimitPrice = LongbridgeMaps.UsesLimitPrice(orderType.Value) ? Positive(row.Price ?? row.SubmittedPrice, currency) : null,
            TriggerPrice = LongbridgeMaps.UsesTriggerPrice(orderType.Value) ? Positive(row.TriggerPrice, currency) : null,
            AveragePrice = filled > 0m ? Positive(row.ExecutedPrice, currency) : null,
            PlacedAt = LongbridgeTime.FromUnixSeconds(row.SubmittedAt) ?? clock.UtcNow,
            UpdatedAt = LongbridgeTime.FromUnixSeconds(row.UpdatedAt),

            // Longbridge's own words for a rejection, verbatim; the raw status only when it had none.
            StatusMessage = string.IsNullOrWhiteSpace(row.Message) ? rawStatus : row.Message,
        };
    }

    public static Result<BrokerTrade> MapTrade(LbExecution row, LongbridgeInstrumentCache cache, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.TradeId) || string.IsNullOrWhiteSpace(row.OrderId) || string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerTrade>.Failure(LongbridgeErrors.MissingField("execution", "trade_id/order_id/symbol"));
        }

        var native = row.Symbol.Trim().ToUpperInvariant();
        if (!cache.TryGetByNative(native, out var record))
        {
            return Result<BrokerTrade>.Failure(LongbridgeNative.NotFound(native));
        }

        var side = LongbridgeMaps.ToCanonicalSide(row.Side);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        if (LongbridgeNumber.Decimal(row.Price) is not { } price)
        {
            return Result<BrokerTrade>.Failure(LongbridgeErrors.MissingField("execution", "price"));
        }

        return new BrokerTrade
        {
            TradeId = row.TradeId.Trim(),
            BrokerOrderId = row.OrderId.Trim(),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(LongbridgeNumber.DecimalOrZero(row.Quantity)),

            // Executions carry no currency: a fill is in the currency its security trades in.
            Price = new Money(price, record.Definition.Currency),
            ExecutedAt = LongbridgeTime.FromUnixSeconds(row.TradeDoneAt) ?? clock.UtcNow,
        };
    }

    /// <summary>
    /// The ClientOrderId travels in <c>remark</c> as 32 hex characters. A remark that is not one — an order
    /// placed from the Longbridge app, with the trader's own note — simply has no ClientOrderId.
    /// </summary>
    public static Guid? ParseClientOrderId(string? remark) =>
        Guid.TryParseExact(remark?.Trim(), "N", out var id) ? id : null;

    private static Money? Positive(string? value, Currency currency) =>
        LongbridgeNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;
}
