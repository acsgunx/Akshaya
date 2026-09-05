using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// <c>tag</c> to <c>ClientOrderId</c> for orders this connector instance placed.
///
/// ONE per connector, shared by the orders facet that writes it and the stream that reads it.
/// That sharing is the whole point: an order placed over REST and filled a second later on the
/// socket has to come back carrying the same ClientOrderId, or the fill cannot be matched to the
/// order that caused it without a round trip to the order book.
///
/// Bounded by the life of the connector — one request scope — so it cannot grow without limit.
/// It is a convenience for in-flight correlation and NOT the system of record: the durable mapping
/// belongs to the order store above this layer, which persists it before the placement call
/// precisely so a timeout can be reconciled rather than retried into a duplicate.
/// </summary>
public sealed class ZerodhaOrderTagIndex
{
    private readonly ConcurrentDictionary<string, Guid> _entries = new(StringComparer.Ordinal);

    /// <summary>Records the tag sent with an order.</summary>
    public void Remember(string tag, Guid clientOrderId) => _entries[tag] = clientOrderId;

    /// <summary>Recovers the client order id for a tag this process placed, if it did.</summary>
    public Guid? Resolve(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && _entries.TryGetValue(tag.Trim(), out var id) ? id : null;
}

/// <summary>
/// Kite order and trade payloads to the canonical contract types.
///
/// Shared because the SAME shapes arrive by two routes: the REST order book, and the postbacks
/// Kite pushes down the WebSocket as an order's state changes. Mapping them in two places is how
/// a fill read from the socket ends up disagreeing with the same fill read from the book —
/// different status, different quantity, and no way to tell which is right.
/// </summary>
internal static class ZerodhaOrderMapper
{
    /// <summary>
    /// One Kite order row to a <see cref="BrokerOrder"/>.
    ///
    /// <c>lenient</c> is true for bulk reads and socket pushes, where an unmapped status degrades
    /// to <see cref="OrderStatus.Unknown"/> rather than failing the whole read; false for a
    /// single-order lookup, where a status we cannot understand is worth failing on.
    /// </summary>
    public static Result<BrokerOrder> MapOrder(
        KiteOrder row,
        ISymbolTranslator symbols,
        IClock clock,
        ZerodhaOrderTagIndex tags,
        string route,
        bool lenient)
    {
        if (string.IsNullOrWhiteSpace(row.OrderId))
        {
            return Result<BrokerOrder>.Failure(ZerodhaErrors.MissingField(route, "order_id"));
        }

        if (string.IsNullOrWhiteSpace(row.TradingSymbol))
        {
            return Result<BrokerOrder>.Failure(ZerodhaErrors.MissingField(route, "tradingsymbol"));
        }

        var instrument = symbols.ToCanonical(row.TradingSymbol, row.Exchange);
        if (instrument.IsFailure)
        {
            return Result<BrokerOrder>.Failure(instrument.Error);
        }

        var side = ZerodhaMaps.ToCanonicalSide(row.TransactionType);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = ZerodhaMaps.ToCanonicalOrderType(row.OrderType);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        var effect = ZerodhaMaps.ToCanonicalPositionEffect(row.Product);
        if (effect.IsFailure)
        {
            return Result<BrokerOrder>.Failure(effect.Error);
        }

        string? rawStatus;
        OrderStatus status;

        if (lenient)
        {
            status = ZerodhaMaps.ToCanonicalOrderStatusOrUnknown(row.Status, out rawStatus);
        }
        else
        {
            var strict = ZerodhaMaps.ToCanonicalOrderStatus(row.Status);
            if (strict.IsFailure)
            {
                return Result<BrokerOrder>.Failure(strict.Error);
            }

            status = strict.Value;
            rawStatus = null;
        }

        var quantity = row.Quantity ?? 0m;
        var filled = row.FilledQuantity ?? 0m;

        // Kite reports a partly executed order as OPEN with a non-zero filled quantity rather than
        // with a status of its own. Deriving PartiallyFilled here is what lets the risk engine see
        // the exposure that already exists.
        if (status == OrderStatus.Open && filled > 0m && filled < quantity)
        {
            status = OrderStatus.PartiallyFilled;
        }

        // status_message is the human sentence; status_message_raw is the OMS's own and is often
        // the more specific of the two. Both are kept, in that order, because a trader reading a
        // rejection needs the sentence and support chasing it needs the raw code.
        var statusMessage = Join(
            Blank(row.StatusMessage),
            Blank(row.StatusMessageRaw),
            rawStatus is null ? null : $"Kite status '{rawStatus}' is not recognised by this connector.");

        return new BrokerOrder
        {
            BrokerOrderId = row.OrderId,
            ClientOrderId = tags.Resolve(row.Tag ?? row.Tags?.FirstOrDefault()),
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,
            PositionEffect = effect.Value,
            TimeInForce = ZerodhaMaps.ToCanonicalTimeInForce(row.Validity).ValueOr(TimeInForce.Day),
            Variety = ZerodhaMaps.ToCanonicalVariety(row.Variety).ValueOr(OrderVariety.Regular),

            // A zero price is Kite's way of saying "not applicable" on a market order, not a price
            // of zero; only positive values become a Money.
            LimitPrice = row.Price is > 0m ? new Money(row.Price.Value, Currency.Inr) : null,
            TriggerPrice = row.TriggerPrice is > 0m ? new Money(row.TriggerPrice.Value, Currency.Inr) : null,
            AveragePrice = row.AveragePrice is > 0m ? new Money(row.AveragePrice.Value, Currency.Inr) : null,
            PlacedAt = ZerodhaTime.ParseOr(row.OrderTimestamp, clock.UtcNow),
            UpdatedAt = ZerodhaTime.Parse(row.ExchangeUpdateTimestamp ?? row.ExchangeTimestamp),
            StatusMessage = statusMessage,
        };
    }

    public static Result<BrokerTrade> MapTrade(
        KiteTrade row,
        ISymbolTranslator symbols,
        IClock clock,
        string route)
    {
        if (string.IsNullOrWhiteSpace(row.TradeId))
        {
            return Result<BrokerTrade>.Failure(ZerodhaErrors.MissingField(route, "trade_id"));
        }

        if (string.IsNullOrWhiteSpace(row.OrderId))
        {
            return Result<BrokerTrade>.Failure(ZerodhaErrors.MissingField(route, "order_id"));
        }

        if (string.IsNullOrWhiteSpace(row.TradingSymbol))
        {
            return Result<BrokerTrade>.Failure(ZerodhaErrors.MissingField(route, "tradingsymbol"));
        }

        var instrument = symbols.ToCanonical(row.TradingSymbol, row.Exchange);
        if (instrument.IsFailure)
        {
            return Result<BrokerTrade>.Failure(instrument.Error);
        }

        var side = ZerodhaMaps.ToCanonicalSide(row.TransactionType);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        return new BrokerTrade
        {
            TradeId = row.TradeId,
            BrokerOrderId = row.OrderId,
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(row.Quantity ?? 0m),

            // On a TRADE, Kite's average_price is the price this fill happened at — the trade is
            // atomic, so there is nothing to average. It is not the order's running average.
            Price = new Money(row.AveragePrice ?? 0m, Currency.Inr),

            // fill_timestamp is when the exchange filled it; order_timestamp on a trade is a bare
            // time of day with no date, and using it would put every fill on 1 January year one.
            ExecutedAt = ZerodhaTime.ParseOr(row.FillTimestamp ?? row.ExchangeTimestamp, clock.UtcNow),

            // Kite prices charges through the margin route, per prospective order, and reports
            // realised ones only in the post-trade contract note. There is nothing per-fill to put
            // here, and a locally estimated number presented as the broker's own would be worse
            // than none.
            Charges = null,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(params ReadOnlySpan<string?> parts)
    {
        var kept = new List<string>(3);
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part) && !kept.Contains(part, StringComparer.Ordinal))
            {
                kept.Add(part);
            }
        }

        return kept.Count == 0 ? null : string.Join(" — ", kept);
    }
}
