using System.Collections.Concurrent;
using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Order placement, amendment and retrieval against mStock's Type A order routes.
///
/// Two things about this broker shape the whole class.
///
/// First, mStock has NO client-order-id field. The only free-text it will carry is <c>tag</c>,
/// and it is short. The platform's <see cref="PlaceOrderRequest.ClientOrderId"/> is therefore
/// folded into the tag (see <see cref="MStockOrderTags"/>) and a process-local index maps it
/// back. That index is a convenience, not the system of record: the durable
/// ClientOrderId-to-BrokerOrderId mapping belongs to the order store above this layer, which
/// persists it BEFORE the placement call precisely so that a timeout can be reconciled
/// against the order book rather than retried into a duplicate.
///
/// Second, mStock reports business failures as HTTP 200 with <c>status: "error"</c>. That is
/// unwrapped once, in <see cref="MStockApi"/>, so nothing here has to remember it.
/// </summary>
public sealed class MStockOrders : IConnectorOrders
{
    private readonly MStockApi _api;
    private readonly MStockOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IClock _clock;

    /// <summary>
    /// tag -> ClientOrderId for orders this connector instance placed. Bounded by the life of
    /// the connector, which is one request scope, so it cannot grow without limit.
    /// </summary>
    private readonly ConcurrentDictionary<string, Guid> _tagIndex = new(StringComparer.Ordinal);

    /// <summary>Both order-details segments, tried in turn when the book does not have the order.</summary>
    private static readonly string[] DetailSegments = [MStockMaps.SegmentEquity, MStockMaps.SegmentDerivative];

    /// <summary>
    /// The venue's own zone. Order-query date bounds are trading dates, and the trading date of
    /// a 23:50 IST after-market order is today in Mumbai and tomorrow in UTC. Filtering on the
    /// UTC date would drop the evening's AMO orders out of "today".
    /// </summary>
    private readonly TimeZoneInfo _venueZone;

    /// <summary>Creates the orders facet.</summary>
    internal MStockOrders(MStockApi api, MStockOptions options, ISymbolTranslator symbols, IClock clock)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
        _venueZone = MStockTime.ResolveZone(options.VenueTimeZoneId);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> PlaceAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        var body = BuildPlaceBody(request);
        if (body.IsFailure)
        {
            return Result<OrderAck>.Failure(body.Error);
        }

        var variety = MStockMaps.ToNativeVariety(request.Variety);
        if (variety.IsFailure)
        {
            return Result<OrderAck>.Failure(variety.Error);
        }

        var path = string.Format(CultureInfo.InvariantCulture, _options.PlaceOrderPathFormat, variety.Value);

        var response = await _api.PostJsonAsync<MStockOrderIdData>(path, body.Value, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        var orderId = response.Value.Any;
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Result<OrderAck>.Failure(MStockErrors.MissingField(path, "order_id"));
        }

        if (body.Value.Tag is { } tag)
        {
            _tagIndex[tag] = request.ClientOrderId;
        }

        return new OrderAck
        {
            BrokerOrderId = orderId,
            // mStock acknowledges receipt, not acceptance. The order is at the broker but not
            // necessarily at the exchange, so this is Submitted — never Open. The order book
            // or the socket says when it actually rests.
            Status = OrderStatus.Submitted,
            ClientOrderId = request.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(
        ModifyOrderRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.BrokerOrderId))
        {
            return Result<OrderAck>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "A modify needs the broker's order id."));
        }

        string? orderType = null;
        if (request.OrderType is { } type)
        {
            var mapped = MStockMaps.ToNativeOrderType(type);
            if (mapped.IsFailure)
            {
                return Result<OrderAck>.Failure(mapped.Error);
            }

            orderType = mapped.Value;
        }

        string? validity = null;
        if (request.TimeInForce is { } tif)
        {
            var mapped = MStockMaps.ToNativeValidity(tif);
            if (mapped.IsFailure)
            {
                return Result<OrderAck>.Failure(mapped.Error);
            }

            validity = mapped.Value;
        }

        var limit = RequireInr(request.LimitPrice, "limit price");
        if (limit.IsFailure)
        {
            return Result<OrderAck>.Failure(limit.Error);
        }

        var trigger = RequireInr(request.TriggerPrice, "trigger price");
        if (trigger.IsFailure)
        {
            return Result<OrderAck>.Failure(trigger.Error);
        }

        var body = new MStockModifyOrderRequest
        {
            OrderId = request.BrokerOrderId,
            Quantity = request.Quantity is { } q ? MStockNumber.Quantity(q.Value) : null,
            OrderType = orderType,
            Price = limit.Value is { } lp ? MStockNumber.Price(lp) : null,
            TriggerPrice = trigger.Value is { } tp ? MStockNumber.Price(tp) : null,
            DisclosedQuantity = request.DisclosedQuantity is { } dq
                ? MStockNumber.Quantity(dq.Value)
                : null,
            Validity = validity,
        };

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.ModifyOrderPathFormat,
            Uri.EscapeDataString(request.BrokerOrderId));

        var response = await _api.PutJsonAsync<MStockOrderIdData>(path, body, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.Any ?? request.BrokerOrderId,
            Status = OrderStatus.Submitted,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<OrderAck>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "A cancel needs the broker's order id."));
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.CancelOrderPathFormat,
            Uri.EscapeDataString(brokerOrderId));

        var response = await _api.DeleteAsync<MStockOrderIdData>(path, query: null, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.Any ?? brokerOrderId,
            // Again: mStock has accepted the cancel request, not confirmed the cancellation.
            // An order can still fill in the gap between the two, and reporting Cancelled here
            // would let the platform release risk budget for a position it still holds.
            Status = OrderStatus.Submitted,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var response = await _api
            .PostJsonAsync<MStockCancelAllData>(_options.CancelAllPath, new { }, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<int>.Failure(response.Error);
        }

        return response.Value.Count ?? response.Value.CancelledOrders?.Count ?? 0;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<IReadOnlyList<MStockOrderDto>>(_options.OrderBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(response.Error);
        }

        var orders = new List<BrokerOrder>(response.Value.Count);
        foreach (var dto in response.Value)
        {
            var mapped = MapOrder(dto, _options.OrderBookPath);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerOrder>>.Failure(mapped.Error);
            }

            if (Matches(mapped.Value, query))
            {
                orders.Add(mapped.Value);
            }
        }

        return Result<IReadOnlyList<BrokerOrder>>.Success(orders);
    }

    /// <inheritdoc />
    public async Task<Result<BrokerOrder>> GetOrderAsync(
        string brokerOrderId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<BrokerOrder>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "An order lookup needs the broker's order id."));
        }

        // The order-details route demands a segment (E or D) alongside the id, and an order id
        // on its own does not say which. Rather than guess — a wrong segment returns "not
        // found", which would be reported as a lost order — read the book first: it carries
        // the exchange, and therefore the segment, for every live order.
        var book = await GetOrdersAsync(new OrderQuery(), ct).ConfigureAwait(false);
        if (book.IsSuccess)
        {
            var found = book.Value.FirstOrDefault(o =>
                string.Equals(o.BrokerOrderId, brokerOrderId, StringComparison.Ordinal));

            if (found is not null)
            {
                return found;
            }
        }

        // Not in today's book. It may be a completed order that has aged out of it, so try the
        // details route in both segments before declaring it missing.
        foreach (var segment in DetailSegments)
        {
            var details = await _api.GetAsync<IReadOnlyList<MStockOrderDto>>(
                    _options.OrderDetailsPath,
                    new MStockQuery().Add("order_no", brokerOrderId).Add("segment", segment),
                    ct)
                .ConfigureAwait(false);

            if (details.IsFailure || details.Value.Count == 0)
            {
                continue;
            }

            // The details route returns the order's state transitions oldest-first; the last
            // one is the current truth.
            var latest = details.Value[^1];
            return MapOrder(latest, _options.OrderDetailsPath);
        }

        return Result<BrokerOrder>.Failure(MStockErrors.OrderNotFound(brokerOrderId));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<IReadOnlyList<MStockTradeDto>>(_options.TradeBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            // Some mStock builds serve the day's fills from /trades rather than /tradebook.
            // Falling back costs one call on a route that was going to fail anyway.
            response = await _api
                .GetAsync<IReadOnlyList<MStockTradeDto>>(_options.TradesPath, query: null, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(response.Error);
            }
        }

        var trades = new List<BrokerTrade>(response.Value.Count);
        foreach (var dto in response.Value)
        {
            var mapped = MapTrade(dto);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            if (MatchesTrade(mapped.Value, query))
            {
                trades.Add(mapped.Value);
            }
        }

        return Result<IReadOnlyList<BrokerTrade>>.Success(trades);
    }

    /// <inheritdoc />
    /// <remarks>
    /// mStock has no basket route, so this loops. The manifest says so
    /// (<c>basket.atomic: false</c>) and the UI warns about partial execution.
    ///
    /// On a failure part-way through it stops immediately and returns the ids of the legs that
    /// DID go through, in the error context. Continuing would build a position the trader
    /// never asked for; discarding the ids would leave orders live at the broker that the
    /// platform has no record of, which is the worse of the two failures by a wide margin.
    /// </remarks>
    public async Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
        {
            return Result<IReadOnlyList<OrderAck>>.Success([]);
        }

        var acks = new List<OrderAck>(requests.Count);

        for (var leg = 0; leg < requests.Count; leg++)
        {
            var ack = await PlaceAsync(requests[leg], ct).ConfigureAwait(false);
            if (ack.IsFailure)
            {
                var context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failedLeg"] = leg.ToString(CultureInfo.InvariantCulture),
                    ["legCount"] = requests.Count.ToString(CultureInfo.InvariantCulture),
                    ["placedOrderIds"] = string.Join(',', acks.Select(a => a.BrokerOrderId)),
                };

                return Result<IReadOnlyList<OrderAck>>.Failure(new Error(
                    ack.Error.Code,
                    $"Basket leg {leg + 1} of {requests.Count} was rejected; {acks.Count} earlier "
                    + "leg(s) are live at mStock and need reconciling. " + ack.Error.Message,
                    ack.Error.VendorCode,
                    ack.Error.VendorMessage,
                    context));
            }

            acks.Add(ack.Value);
        }

        return Result<IReadOnlyList<OrderAck>>.Success(acks);
    }

    /// <inheritdoc />
    /// <remarks>
    /// mStock's Type A surface publishes no margin calculator. The manifest declares
    /// <c>marginEstimate: false</c> so the order ticket hides the field rather than showing a
    /// number we invented.
    /// </remarks>
    public Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Result<MarginEstimate>.Failure(
            ConnectorErrors.NotSupported("margin estimation")));

    /// <inheritdoc />
    /// <remarks>
    /// Likewise no charges calculator. Estimating Indian charges is entirely possible — the
    /// Paper connector ships an itemised SEBI/STT/GST schedule — but doing it HERE would mean
    /// this connector quietly reporting our own arithmetic as the broker's. The platform's
    /// charge-schedule service is where that estimate belongs, and it is labelled as an
    /// estimate when it is shown.
    /// </remarks>
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Result<ChargesEstimate>.Failure(
            ConnectorErrors.NotSupported("charge estimation")));

    // --- request building -----------------------------------------------------------------

    private Result<MStockPlaceOrderRequest> BuildPlaceBody(PlaceOrderRequest request)
    {
        if (request.Quantity.Value <= 0m)
        {
            return Result<MStockPlaceOrderRequest>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "Order quantity must be positive."));
        }

        if (request.Quantity.IsFractional)
        {
            return Result<MStockPlaceOrderRequest>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"mStock trades whole units only; {request.Quantity} is fractional. "
                + "The manifest declares fractionalQuantity: false."));
        }

        var symbol = _symbols.ToNative(request.Instrument);
        if (symbol.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(symbol.Error);
        }

        var exchange = MStockMaps.ToNativeExchange(request.Instrument.Venue, request.Instrument.AssetClass);
        if (exchange.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(exchange.Error);
        }

        var side = MStockMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(side.Error);
        }

        var orderType = MStockMaps.ToNativeOrderType(request.OrderType);
        if (orderType.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(orderType.Error);
        }

        var product = MStockMaps.ToNativeProduct(request.PositionEffect);
        if (product.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(product.Error);
        }

        var validity = MStockMaps.ToNativeValidity(request.TimeInForce);
        if (validity.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(validity.Error);
        }

        var priceCheck = ValidatePrices(request);
        if (priceCheck.IsFailure)
        {
            return Result<MStockPlaceOrderRequest>.Failure(priceCheck.Error);
        }

        return new MStockPlaceOrderRequest
        {
            TradingSymbol = symbol.Value,
            Exchange = exchange.Value,
            TransactionType = side.Value,
            OrderType = orderType.Value,
            Quantity = MStockNumber.Quantity(request.Quantity.Value),
            Product = product.Value,
            Validity = validity.Value,

            // A price on a MARKET order is not merely redundant — some exchange gateways treat
            // it as a protection price and reject the order outright. Send only what the order
            // type actually needs.
            Price = request.OrderType is OrderType.Limit or OrderType.StopLimit
                ? MStockNumber.Price(request.LimitPrice!.Value.Amount)
                : null,
            TriggerPrice = request.OrderType is OrderType.Stop or OrderType.StopLimit
                ? MStockNumber.Price(request.TriggerPrice!.Value.Amount)
                : null,
            DisclosedQuantity = request.DisclosedQuantity is { } disclosed
                ? MStockNumber.Quantity(disclosed.Value)
                : null,
            // mStock has exactly one free-text slot and the ClientOrderId has to have it: it is
            // the only field the platform needs back in order to reconcile a timed-out
            // placement. The trader's own Tag and the AlgoId are persisted locally against this
            // ClientOrderId instead — and SEBI's algo identification runs through the
            // exchange-registered strategy attached to the API key, not through an order tag.
            Tag = MStockOrderTags.Encode(request.ClientOrderId),
        };
    }

    private static Result ValidatePrices(PlaceOrderRequest request)
    {
        if (request.OrderType is OrderType.Limit or OrderType.StopLimit)
        {
            if (request.LimitPrice is not { } limit)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"A {request.OrderType} order needs a limit price."));
            }

            if (limit.Currency != Currency.Inr)
            {
                return Result.Failure(NotInr("limit price", limit.Currency));
            }

            if (limit.Amount <= 0m)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    "The limit price must be positive."));
            }
        }

        if (request.OrderType is OrderType.Stop or OrderType.StopLimit)
        {
            if (request.TriggerPrice is not { } trigger)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"A {request.OrderType} order needs a trigger price."));
            }

            if (trigger.Currency != Currency.Inr)
            {
                return Result.Failure(NotInr("trigger price", trigger.Currency));
            }

            if (trigger.Amount <= 0m)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    "The trigger price must be positive."));
            }
        }

        return Result.Success();
    }

    private static Result<decimal?> RequireInr(Money? money, string what)
    {
        if (money is not { } value)
        {
            return Result<decimal?>.Success(null);
        }

        return value.Currency == Currency.Inr
            ? Result<decimal?>.Success(value.Amount)
            : Result<decimal?>.Failure(NotInr(what, value.Currency));
    }

    private static Error NotInr(string what, Currency currency) => new(
        ConnectorErrorCodes.InvalidRequest,
        $"mStock settles in INR; the {what} was quoted in {currency}. "
        + "Convert explicitly before sending the order rather than assuming a rate here.");

    // --- response mapping -------------------------------------------------------------------

    private Result<BrokerOrder> MapOrder(MStockOrderDto dto, string route)
    {
        if (string.IsNullOrWhiteSpace(dto.OrderId))
        {
            return Result<BrokerOrder>.Failure(MStockErrors.MissingField(route, "order_id"));
        }

        var instrument = ResolveInstrument(dto.TradingSymbol, dto.Exchange, route);
        if (instrument.IsFailure)
        {
            return Result<BrokerOrder>.Failure(instrument.Error);
        }

        var side = MStockMaps.ToCanonicalSide(dto.TransactionType ?? string.Empty);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = MStockMaps.ToCanonicalOrderType(dto.OrderType ?? string.Empty);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        var effect = MStockMaps.ToCanonicalPositionEffect(dto.Product ?? string.Empty);
        if (effect.IsFailure)
        {
            return Result<BrokerOrder>.Failure(effect.Error);
        }

        var status = MStockMaps.ToCanonicalOrderStatusOrUnknown(dto.Status ?? string.Empty, out var rawStatus);

        // mStock reports a partially executed order as OPEN with a non-zero filled quantity
        // rather than with its own status. Deriving PartiallyFilled here is what lets the risk
        // engine see the exposure that already exists.
        var quantity = dto.Quantity ?? 0m;
        var filled = dto.FilledQuantity ?? 0m;
        if (status == OrderStatus.Open && filled > 0m && filled < quantity)
        {
            status = OrderStatus.PartiallyFilled;
        }

        var validity = dto.Validity is { Length: > 0 }
            ? MStockMaps.ToCanonicalTimeInForce(dto.Validity).ValueOr(TimeInForce.Day)
            : TimeInForce.Day;

        var variety = dto.Variety is { Length: > 0 }
            ? MStockMaps.ToCanonicalVariety(dto.Variety).ValueOr(OrderVariety.Regular)
            : OrderVariety.Regular;

        var placedAt = MStockTime.ParseOr(dto.OrderTimestamp, _clock.UtcNow);
        var updatedAt = MStockTime.Parse(dto.ExchangeUpdateTimestamp ?? dto.ExchangeTimestamp);

        var statusMessage = rawStatus is null
            ? dto.StatusMessage
            : string.IsNullOrWhiteSpace(dto.StatusMessage)
                ? $"mStock status '{rawStatus}' is not recognised by this connector."
                : $"{dto.StatusMessage} (mStock status '{rawStatus}')";

        return new BrokerOrder
        {
            BrokerOrderId = dto.OrderId,
            ClientOrderId = MStockOrderTags.Decode(dto.Tag, _tagIndex),
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,
            PositionEffect = effect.Value,
            TimeInForce = validity,
            Variety = variety,
            // A zero price is mStock's way of saying "not applicable" on a market order, not
            // a price of zero; only positive values become a Money.
            LimitPrice = dto.Price is > 0m ? new Money(dto.Price.Value, Currency.Inr) : null,
            TriggerPrice = dto.TriggerPrice is > 0m
                ? new Money(dto.TriggerPrice.Value, Currency.Inr)
                : null,
            AveragePrice = dto.AveragePrice is > 0m
                ? new Money(dto.AveragePrice.Value, Currency.Inr)
                : null,
            PlacedAt = placedAt,
            UpdatedAt = updatedAt,
            StatusMessage = statusMessage,
        };
    }

    private Result<BrokerTrade> MapTrade(MStockTradeDto dto)
    {
        var tradeId = dto.TradeId;
        if (string.IsNullOrWhiteSpace(tradeId))
        {
            return Result<BrokerTrade>.Failure(MStockErrors.MissingField(_options.TradeBookPath, "trade_id"));
        }

        if (string.IsNullOrWhiteSpace(dto.OrderId))
        {
            return Result<BrokerTrade>.Failure(MStockErrors.MissingField(_options.TradeBookPath, "order_id"));
        }

        var instrument = ResolveInstrument(dto.TradingSymbol, dto.Exchange, _options.TradeBookPath);
        if (instrument.IsFailure)
        {
            return Result<BrokerTrade>.Failure(instrument.Error);
        }

        var side = MStockMaps.ToCanonicalSide(dto.TransactionType ?? string.Empty);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        var price = dto.AveragePrice ?? dto.Price;
        if (price is null)
        {
            return Result<BrokerTrade>.Failure(MStockErrors.MissingField(
                _options.TradeBookPath,
                "average_price"));
        }

        return new BrokerTrade
        {
            TradeId = tradeId,
            BrokerOrderId = dto.OrderId,
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(dto.Quantity ?? 0m),
            Price = new Money(price.Value, Currency.Inr),
            ExecutedAt = MStockTime.ParseOr(
                dto.FillTimestamp ?? dto.TradeTimestamp ?? dto.ExchangeTimestamp,
                _clock.UtcNow),
        };
    }

    /// <summary>
    /// Resolves a native symbol, turning a translation failure into an error that names both
    /// the symbol and the fix. This is the one place a stale instrument master becomes
    /// visible, and an operator reading the log needs to be told which symbol broke it.
    /// </summary>
    private Result<InstrumentKey> ResolveInstrument(string? tradingSymbol, string? exchange, string route)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol))
        {
            return Result<InstrumentKey>.Failure(MStockErrors.MissingField(route, "tradingsymbol"));
        }

        var resolved = _symbols.ToCanonical(tradingSymbol, exchange);
        if (resolved.IsSuccess)
        {
            return resolved;
        }

        return Result<InstrumentKey>.Failure(new Error(
            resolved.Error.Code,
            $"mStock returned an order or trade on '{tradingSymbol}' ({exchange ?? "no exchange"}) "
            + $"which this connector cannot identify. {resolved.Error.Message}",
            resolved.Error.VendorCode ?? tradingSymbol,
            resolved.Error.VendorMessage,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["route"] = route,
                ["tradingsymbol"] = tradingSymbol,
                ["exchange"] = exchange ?? string.Empty,
            }));
    }

    private bool Matches(BrokerOrder order, OrderQuery query)
    {
        if (query.OpenOnly && !order.Status.IsWorking())
        {
            return false;
        }

        if (query.Instrument is { } instrument && !order.Instrument.Equals(instrument))
        {
            return false;
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(order.PlacedAt, _venueZone).DateTime);
        return (query.From is not { } from || date >= from)
               && (query.To is not { } to || date <= to);
    }

    private bool MatchesTrade(BrokerTrade trade, OrderQuery query)
    {
        if (query.Instrument is { } instrument && !trade.Instrument.Equals(instrument))
        {
            return false;
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(trade.ExecutedAt, _venueZone).DateTime);
        return (query.From is not { } from || date >= from)
               && (query.To is not { } to || date <= to);
    }
}

/// <summary>
/// Folds the platform's <c>ClientOrderId</c> into mStock's short free-text <c>tag</c>.
///
/// mStock allows roughly twenty characters and rejects punctuation, so a full 36-character
/// GUID does not fit. Twenty hex characters is eighty bits of the GUID — far more than enough
/// to be unique across a trading day, and the truncation is one-way, which is why the
/// connector keeps an index rather than trying to reconstruct the GUID from the tag.
///
/// This is a convenience for in-flight correlation. The authoritative mapping is written by
/// the order store BEFORE the placement call, so that a timed-out placement can be reconciled
/// against the order book instead of retried into a duplicate order.
/// </summary>
public static class MStockOrderTags
{
    /// <summary>Characters of the GUID we can fit in mStock's tag field.</summary>
    public const int TagLength = 20;

    /// <summary>Builds the tag to send with an order.</summary>
    public static string Encode(Guid clientOrderId) =>
        clientOrderId.ToString("N", CultureInfo.InvariantCulture)[..TagLength];

    /// <summary>Recovers the client order id for a tag this process placed, if it did.</summary>
    public static Guid? Decode(string? tag, IReadOnlyDictionary<string, Guid> index)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        return index.TryGetValue(tag.Trim(), out var id) ? id : null;
    }
}
