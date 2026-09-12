using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Tiger order and execution rows to canonical <see cref="BrokerOrder"/> and <see cref="BrokerTrade"/>.
///
/// The caller describes each row's contract first (<see cref="TigerInstrumentResolver.ForContractAsync"/>) and passes
/// the record in; mapping itself never touches the network.
/// </summary>
internal static class TigerOrderMapper
{
    /// <param name="row">An order row from any of the order methods.</param>
    /// <param name="record">The row's contract, already described.</param>
    /// <param name="clock">Stamps a row that carries no open time.</param>
    /// <param name="lenient">True for book reads: an unmapped status degrades to Unknown rather than failing the book.</param>
    public static Result<BrokerOrder> MapOrder(TigerOrder row, TigerInstrumentRecord record, IClock clock, bool lenient)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(row.Id))
        {
            return Result<BrokerOrder>.Failure(TigerErrors.MissingField("orders", "id"));
        }

        var side = TigerMaps.ToCanonicalSide(row.Action);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = TigerMaps.ToCanonicalOrderType(row.OrderType);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        var timeInForce = TigerMaps.ToCanonicalTimeInForce(row.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<BrokerOrder>.Failure(timeInForce.Error);
        }

        string? rawStatus = null;
        OrderStatus status;
        if (lenient)
        {
            status = TigerMaps.ToCanonicalOrderStatusOrUnknown(row.Status, out rawStatus);
        }
        else
        {
            var strict = TigerMaps.ToCanonicalOrderStatus(row.Status);
            if (strict.IsFailure)
            {
                return Result<BrokerOrder>.Failure(strict.Error);
            }

            status = strict.Value;
        }

        var quantity = TigerNumber.DecimalOrZero(row.TotalQuantity);
        var filled = TigerNumber.DecimalOrZero(row.FilledQuantity);

        // Tiger reports a partly filled order as Submitted with a filled quantity; anything sizing off it must know.
        if (status == OrderStatus.Open && filled > 0m && filled < quantity)
        {
            status = OrderStatus.PartiallyFilled;
        }

        var currency = TigerMaps.ToCanonicalCurrency(row.Currency) is { IsSuccess: true } listed
            ? listed.Value
            : record.Definition.Currency;

        return new BrokerOrder
        {
            BrokerOrderId = row.Id.Trim(),
            ClientOrderId = ParseClientOrderId(row.UserMark),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,

            // Tiger reports no short flag on an order: margin use is the account's.
            PositionEffect = PositionEffect.Delivery,
            TimeInForce = timeInForce.Value,
            LimitPrice = TigerMaps.UsesLimitPrice(orderType.Value) ? Positive(row.LimitPrice, currency) : null,
            TriggerPrice = TigerMaps.UsesTriggerPrice(orderType.Value) ? Positive(row.AuxPrice, currency) : null,
            AveragePrice = filled > 0m ? Positive(row.AverageFillPrice, currency) : null,
            PlacedAt = TigerTime.FromUnixMilliseconds(row.OpenTime) ?? clock.UtcNow,
            UpdatedAt = TigerTime.FromUnixMilliseconds(row.UpdateTime) ?? TigerTime.FromUnixMilliseconds(row.LatestTime),

            // Tiger's own words for a rejection, verbatim; the raw status only when it had none.
            StatusMessage = string.IsNullOrWhiteSpace(row.Remark) ? rawStatus : row.Remark,
        };
    }

    public static Result<BrokerTrade> MapTrade(TigerTransaction row, TigerInstrumentRecord record, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.OrderId))
        {
            return Result<BrokerTrade>.Failure(TigerErrors.MissingField("order_transactions", "id/orderId"));
        }

        var side = TigerMaps.ToCanonicalSide(row.Action);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        if (TigerNumber.Decimal(row.FilledPrice) is not { } price)
        {
            return Result<BrokerTrade>.Failure(TigerErrors.MissingField("order_transactions", "filledPrice"));
        }

        var currency = TigerMaps.ToCanonicalCurrency(row.Currency) is { IsSuccess: true } listed
            ? listed.Value
            : record.Definition.Currency;

        return new BrokerTrade
        {
            TradeId = row.Id.Trim(),
            BrokerOrderId = row.OrderId.Trim(),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(Math.Abs(TigerNumber.DecimalOrZero(row.FilledQuantity))),
            Price = new Money(price, currency),
            ExecutedAt = TigerTime.FromUnixMilliseconds(row.TransactedAt) ?? clock.UtcNow,
            Charges = TigerNumber.Decimal(row.Commission) is { } commission ? new Money(commission, currency) : null,
        };
    }

    /// <summary>
    /// The ClientOrderId travels in <c>user_mark</c> as 32 hex characters. A marker that is not one — an order placed
    /// from the Tiger app — simply has no ClientOrderId.
    /// </summary>
    public static Guid? ParseClientOrderId(string? userMark) =>
        Guid.TryParseExact(userMark?.Trim(), "N", out var id) ? id : null;

    private static Money? Positive(string? value, Currency currency) =>
        TigerNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;
}
