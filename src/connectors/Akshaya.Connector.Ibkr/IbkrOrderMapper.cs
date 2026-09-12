using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Client Portal order and execution rows to canonical <see cref="BrokerOrder"/> and <see cref="BrokerTrade"/>.
///
/// Callers describe the rows' conids FIRST (<see cref="IbkrInstrumentResolver.EnsureConidsAsync"/>); mapping never
/// touches the network.
/// </summary>
internal static class IbkrOrderMapper
{
    /// <summary>A row of the live orders list. Unmapped statuses degrade to Unknown; an unmapped order type fails the row.</summary>
    public static Result<BrokerOrder> MapLiveOrder(IbkrLiveOrder row, IbkrInstrumentCache cache, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.OrderId) || IbkrNumber.Integer(row.Conid) is not { } conid)
        {
            return Result<BrokerOrder>.Failure(IbkrErrors.MissingField("iserver/account/orders", "orderId/conid"));
        }

        var filled = IbkrNumber.Decimal(row.FilledQuantity) ?? 0m;
        var total = IbkrNumber.Decimal(row.TotalSize) ?? (filled + (IbkrNumber.Decimal(row.RemainingQuantity) ?? 0m));

        return Map(
            conid,
            row.OrderId.Trim(),
            row.Side,
            row.OrderType ?? row.OriginalOrderType,
            row.TimeInForce,
            row.Status,
            total,
            filled,
            row.Price,
            row.AuxPrice,
            row.AveragePrice,
            IbkrTime.FromUnixMilliseconds(row.LastExecutionTime),
            row.OrderRef,
            row.SystemCancellationReason,
            cache,
            clock);
    }

    /// <summary>The single-order status route, for an order no longer in the live list.</summary>
    public static Result<BrokerOrder> MapOrderStatus(IbkrOrderStatus row, IbkrInstrumentCache cache, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.OrderId) || IbkrNumber.Integer(row.Conid) is not { } conid)
        {
            return Result<BrokerOrder>.Failure(IbkrErrors.MissingField("iserver/account/order/status", "order_id/conid"));
        }

        var type = IbkrMaps.ToCanonicalOrderType(row.OrderType);

        return Map(
            conid,
            row.OrderId.Trim(),
            row.Side,
            row.OrderType,
            row.TimeInForce,
            row.Status,
            IbkrNumber.Decimal(row.TotalSize) ?? 0m,
            IbkrNumber.Decimal(row.CumulativeFill) ?? 0m,
            type is { IsSuccess: true, Value: OrderType.Stop } ? row.StopPrice : row.LimitPrice,
            row.StopPrice,
            row.AveragePrice,
            placedAt: null,
            orderRef: null,
            row.StatusDescription,
            cache,
            clock);
    }

    public static Result<BrokerTrade> MapTrade(IbkrTrade row, IbkrInstrumentCache cache, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.ExecutionId)
            || string.IsNullOrWhiteSpace(row.OrderId)
            || IbkrNumber.Integer(row.Conid) is not { } conid)
        {
            return Result<BrokerTrade>.Failure(IbkrErrors.MissingField("iserver/account/trades", "execution_id/order_id/conid"));
        }

        if (!cache.TryGetByConid(conid, out var record))
        {
            return Result<BrokerTrade>.Failure(NotDescribed(conid));
        }

        var side = IbkrMaps.ToCanonicalSide(row.Side);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        if (IbkrNumber.Decimal(row.Price) is not { } price)
        {
            return Result<BrokerTrade>.Failure(IbkrErrors.MissingField("iserver/account/trades", "price"));
        }

        return new BrokerTrade
        {
            TradeId = row.ExecutionId.Trim(),
            BrokerOrderId = row.OrderId.Trim(),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(Math.Abs(IbkrNumber.Decimal(row.Size) ?? 0m)),
            Price = new Money(price, record.Definition.Currency),
            ExecutedAt = IbkrTime.FromUnixMilliseconds(row.TradeTime) ?? clock.UtcNow,

            // IBKR states the commission without its currency, which is the account's and not necessarily the trade's.
            Charges = null,
        };
    }

    /// <summary>
    /// The ClientOrderId travels in <c>cOID</c> and comes back as <c>order_ref</c>, 32 hex characters. An order placed
    /// from Trader Workstation has none.
    /// </summary>
    public static Guid? ParseClientOrderId(string? orderRef) =>
        Guid.TryParseExact(orderRef?.Trim(), "N", out var id) ? id : null;

    public static Error NotDescribed(long conid) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"IBKR returned no contract definition for conid {conid}.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["conid"] = conid.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static Result<BrokerOrder> Map(
        long conid,
        string orderId,
        string? sideText,
        string? typeText,
        string? tifText,
        string? statusText,
        decimal total,
        decimal filled,
        string? price,
        string? auxPrice,
        string? averagePrice,
        DateTimeOffset? placedAt,
        string? orderRef,
        string? statusMessage,
        IbkrInstrumentCache cache,
        IClock clock)
    {
        if (!cache.TryGetByConid(conid, out var record))
        {
            return Result<BrokerOrder>.Failure(NotDescribed(conid));
        }

        var side = IbkrMaps.ToCanonicalSide(sideText);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var type = IbkrMaps.ToCanonicalOrderType(typeText);
        if (type.IsFailure)
        {
            return Result<BrokerOrder>.Failure(type.Error);
        }

        // A time in force this table does not know is reported as Day rather than failing the order: misreading how
        // long an order lives is recoverable, hiding the order is not.
        var timeInForce = IbkrMaps.ToCanonicalTimeInForce(tifText) is { IsSuccess: true } tif ? tif.Value : TimeInForce.Day;

        var status = IbkrMaps.ToCanonicalOrderStatusOrUnknown(statusText, out var rawStatus);
        if (status == OrderStatus.Open && filled > 0m && filled < total)
        {
            status = OrderStatus.PartiallyFilled;
        }

        var currency = record.Definition.Currency;
        var limit = Positive(price, currency);
        var aux = Positive(auxPrice, currency);

        return new BrokerOrder
        {
            BrokerOrderId = orderId,
            ClientOrderId = ParseClientOrderId(orderRef),
            Instrument = record.Definition.Key,
            Side = side.Value,
            Quantity = new Quantity(total),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = type.Value,

            // IBKR reports no short flag on an order.
            PositionEffect = PositionEffect.Delivery,
            TimeInForce = timeInForce,

            // STP carries its stop in price; STOP_LIMIT carries its limit in price and its stop in auxPrice.
            LimitPrice = type.Value is OrderType.Limit or OrderType.StopLimit ? limit : null,
            TriggerPrice = type.Value switch
            {
                OrderType.Stop => aux ?? limit,
                OrderType.StopLimit => aux,
                _ => null,
            },
            AveragePrice = filled > 0m ? Positive(averagePrice, currency) : null,

            // The live list has no submission time, only the last execution's; an order that has not traded is
            // stamped when it is read.
            PlacedAt = placedAt ?? clock.UtcNow,
            UpdatedAt = placedAt,
            StatusMessage = string.IsNullOrWhiteSpace(statusMessage) ? rawStatus : statusMessage,
        };
    }

    private static Money? Positive(string? value, Currency currency) =>
        IbkrNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;
}
