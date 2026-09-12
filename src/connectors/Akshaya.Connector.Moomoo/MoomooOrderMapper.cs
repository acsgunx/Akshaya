using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// OpenD order and fill rows to canonical <see cref="BrokerOrder"/> and <see cref="BrokerTrade"/>, shared
/// by the orders facet and the stream so a pushed update and a book read can never disagree.
///
/// Callers resolve the rows' securities into the cache FIRST (<see cref="MoomooInstrumentResolver.EnsureAsync"/>);
/// mapping itself never touches the network.
/// </summary>
internal static class MoomooOrderMapper
{
    /// <param name="row">A book row or an order push.</param>
    /// <param name="fallbackMarket">The market the request was made for, used when the row names none.</param>
    /// <param name="cache">Must already hold the row's security.</param>
    /// <param name="clock">Stamps a row that carries no creation time.</param>
    /// <param name="lenient">
    /// True for book reads and pushes: an unmapped status degrades to Unknown rather than failing the book.
    /// </param>
    public static Result<BrokerOrder> MapOrder(
        OpenDOrder row,
        MoomooMarket fallbackMarket,
        MoomooInstrumentCache cache,
        IClock clock,
        bool lenient)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.OrderId == 0 || string.IsNullOrWhiteSpace(row.Code))
        {
            return Result<BrokerOrder>.Failure(MoomooErrors.MissingField(MoomooProtoId.TrdGetOrderList, "orderID/code"));
        }

        var market = MarketOf(row.SecMarket, row.TrdMarket, fallbackMarket);
        var native = MoomooNative.Qualify(market, row.Code.Trim().ToUpperInvariant());

        if (!cache.TryGetByNative(native, out var record))
        {
            return Result<BrokerOrder>.Failure(MoomooNative.NotFound(native));
        }

        var side = MoomooMaps.ToCanonicalSide(row.TrdSide);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = MoomooMaps.ToCanonicalOrderType(row.OrderType);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        var timeInForce = MoomooMaps.ToCanonicalTimeInForce(row.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<BrokerOrder>.Failure(timeInForce.Error);
        }

        string? rawStatus = null;
        OrderStatus status;
        if (lenient)
        {
            status = MoomooMaps.ToCanonicalOrderStatusOrUnknown(row.OrderStatus, out rawStatus);
        }
        else
        {
            var strict = MoomooMaps.ToCanonicalOrderStatus(row.OrderStatus);
            if (strict.IsFailure)
            {
                return Result<BrokerOrder>.Failure(strict.Error);
            }

            status = strict.Value;
        }

        var quantity = MoomooNumber.DecimalOrZero(row.Qty, 4);
        var filled = MoomooNumber.DecimalOrZero(row.FillQty, 4);

        // Belt and braces: OpenD has an explicit partial-fill status, but a resting order reported as
        // Submitted with shares already filled is still half on, and anything sizing off it must know.
        if (status == OrderStatus.Open && filled > 0m && filled < quantity)
        {
            status = OrderStatus.PartiallyFilled;
        }

        var currency = market.Currency;
        var now = clock.UtcNow;

        return new BrokerOrder
        {
            BrokerOrderId = row.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ClientOrderId = ParseClientOrderId(row.Remark),
            Instrument = record.Definition.Key,
            Side = side.Value.Side,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,
            PositionEffect = MoomooMaps.ToCanonicalPositionEffect(side.Value.IsShort),
            TimeInForce = timeInForce.Value,
            LimitPrice = MoomooMaps.UsesLimitPrice(orderType.Value) ? MoneyOrNull(row.Price, currency) : null,
            TriggerPrice = MoomooMaps.UsesTriggerPrice(orderType.Value) ? MoneyOrNull(row.AuxPrice, currency) : null,
            AveragePrice = filled > 0m ? MoneyOrNull(row.FillAvgPrice, currency) : null,
            PlacedAt = MoomooTime.Best(row.CreateTimestamp, row.CreateTime, market.Zone) ?? now,
            UpdatedAt = MoomooTime.Best(row.UpdateTimestamp, row.UpdateTime, market.Zone),

            // OpenD's own words for a failure, verbatim; the raw status only when it had none.
            StatusMessage = string.IsNullOrWhiteSpace(row.LastErrMsg) ? rawStatus : row.LastErrMsg,
        };
    }

    public static Result<BrokerTrade> MapTrade(
        OpenDOrderFill row,
        MoomooMarket fallbackMarket,
        MoomooInstrumentCache cache,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.FillId == 0 || row.OrderId is not { } orderId || orderId == 0 || string.IsNullOrWhiteSpace(row.Code))
        {
            return Result<BrokerTrade>.Failure(MoomooErrors.MissingField(MoomooProtoId.TrdGetOrderFillList, "fillID/orderID/code"));
        }

        var market = MarketOf(row.SecMarket, row.TrdMarket, fallbackMarket);
        var native = MoomooNative.Qualify(market, row.Code.Trim().ToUpperInvariant());

        if (!cache.TryGetByNative(native, out var record))
        {
            return Result<BrokerTrade>.Failure(MoomooNative.NotFound(native));
        }

        var side = MoomooMaps.ToCanonicalSide(row.TrdSide);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        return new BrokerTrade
        {
            TradeId = row.FillId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BrokerOrderId = orderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Instrument = record.Definition.Key,
            Side = side.Value.Side,
            Quantity = new Quantity(MoomooNumber.DecimalOrZero(row.Qty, 4)),
            Price = new Money(MoomooNumber.DecimalOrZero(row.Price), market.Currency),
            ExecutedAt = MoomooTime.Best(row.CreateTimestamp, row.CreateTime, market.Zone) ?? clock.UtcNow,
        };
    }

    /// <summary>
    /// The ClientOrderId travels in <c>remark</c> as 32 hex characters. A remark that is not one — an
    /// order placed from the moomoo app, with the trader's own note — simply has no ClientOrderId, which
    /// is what an order this platform did not place should have.
    /// </summary>
    public static Guid? ParseClientOrderId(string? remark) =>
        Guid.TryParseExact(remark?.Trim(), "N", out var id) ? id : null;

    /// <summary>
    /// The market a row belongs to: its security market when OpenD sent one, else its trading market, else
    /// the market the request was made for.
    /// </summary>
    internal static MoomooMarket MarketOf(int? secMarket, int? trdMarket, MoomooMarket fallback)
    {
        if (secMarket is { } sec && MoomooMaps.MarketForSecMarket(sec) is { IsSuccess: true } bySec)
        {
            return bySec.Value;
        }

        return trdMarket is { } trd && MoomooMaps.MarketForTrdMarket(trd) is { IsSuccess: true } byTrd
            ? byTrd.Value
            : fallback;
    }

    private static Money? MoneyOrNull(double? value, Currency currency) =>
        MoomooNumber.Decimal(value) is { } amount && amount != 0m ? new Money(amount, currency) : null;
}
