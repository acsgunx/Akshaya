using System.Collections.Concurrent;
using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Order placement, amendment and retrieval against the FYERS v3 order routes.
///
/// Four things about this broker shape the whole class.
///
/// First, FYERS has NO client-order-id field. The only free text it carries is <c>orderTag</c>,
/// so <see cref="PlaceOrderRequest.ClientOrderId"/> is folded into the tag (see
/// <see cref="FyersOrderTags"/>) and a process-local index maps it back. That index is a
/// convenience, not the system of record: the durable ClientOrderId-to-BrokerOrderId mapping
/// belongs to the order store above this layer, which persists it BEFORE the placement call
/// precisely so that a timeout can be reconciled against the order book rather than retried into
/// a duplicate.
///
/// Second, FYERS answers a PLACEMENT WITH CODE 201 to mean "we took the request and never heard
/// back from the exchange". That is a success-shaped response for an order whose fate is
/// genuinely unknown, and treating it as accepted is how a trader ends up flat when they believe
/// they are long. See <see cref="PlaceAsync"/>.
///
/// Third, a MODIFY must restate the order type. FYERS documents <c>type</c> as mandatory on the
/// PATCH, so amending only a price still requires knowing what kind of order it is — and sending
/// the wrong one converts a resting limit order into a market order that fills immediately.
///
/// Fourth, the order book contains every segment the ACCOUNT trades, not every segment this
/// connector declares. A FYERS user with an MCX position has commodity rows in the same
/// response, and those are skipped rather than allowed to fail the whole book.
/// </summary>
public sealed class FyersOrders : IConnectorOrders
{
    private readonly FyersApi _api;
    private readonly FyersOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeZoneInfo _venueZone;

    /// <summary>
    /// tag -> ClientOrderId for orders this connector instance placed. Bounded by the life of the
    /// connector, which is one request scope, so it cannot grow without limit.
    /// </summary>
    private readonly ConcurrentDictionary<string, Guid> _tagIndex = new(StringComparer.Ordinal);

    /// <summary>Creates the orders facet.</summary>
    internal FyersOrders(
        FyersApi api,
        FyersOptions options,
        ISymbolTranslator symbols,
        IClock clock,
        ILogger logger)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
        _logger = logger;
        _venueZone = FyersTime.ResolveZone(options.VenueTimeZoneId);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildPlaceBody(request);
        if (body.IsFailure)
        {
            return Result<OrderAck>.Failure(body.Error);
        }

        var response = await _api
            .PostJsonAsync<FyersOrderIdResponse>(_options.OrderPath, body.Value, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return BuildAck(response.Value, request.ClientOrderId, body.Value.OrderTag, _options.OrderPath);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BrokerOrderId))
        {
            return Result<OrderAck>.Failure(FyersErrors.InvalidRequest("A modify needs the broker's order id."));
        }

        if (request.TimeInForce is not null)
        {
            // Declared as unmodifiable in the manifest, and worth refusing explicitly rather than
            // dropping: silently ignoring a validity change would leave a caller believing it had
            // turned a day order into an IOC.
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported(
                "changing the validity of a live order. Cancel it and place a new one instead"));
        }

        // FYERS requires `type` on every modify. When the caller did not name one, read the
        // order back and restate what it already is — the alternative is guessing, and a wrong
        // guess turns a resting limit order into a market order that fills at once.
        var orderType = request.OrderType;
        if (orderType is null)
        {
            var existing = await GetOrderAsync(request.BrokerOrderId, ct).ConfigureAwait(false);
            if (existing.IsFailure)
            {
                return Result<OrderAck>.Failure(existing.Error);
            }

            orderType = existing.Value.OrderType;
        }

        var nativeType = FyersMaps.ToNativeOrderType(orderType.Value);
        if (nativeType.IsFailure)
        {
            return Result<OrderAck>.Failure(nativeType.Error);
        }

        var prices = ValidatePrices(orderType.Value, request.LimitPrice, request.TriggerPrice, forModify: true);
        if (prices.IsFailure)
        {
            return Result<OrderAck>.Failure(prices.Error);
        }

        var body = new FyersModifyOrderBody
        {
            Id = request.BrokerOrderId,
            Type = nativeType.Value,
            Quantity = request.Quantity is { } quantity ? ToWholeQuantity(quantity) : null,
            LimitPrice = request.LimitPrice?.Amount,
            StopPrice = request.TriggerPrice?.Amount,
            DisclosedQuantity = request.DisclosedQuantity is { } disclosed ? ToWholeQuantity(disclosed) : null,
        };

        var response = await _api
            .PatchJsonAsync<FyersOrderIdResponse>(_options.OrderPath, body, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return BuildAck(response.Value, clientOrderId: null, tag: null, _options.OrderPath);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<OrderAck>.Failure(FyersErrors.InvalidRequest("A cancel needs the broker's order id."));
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.CancelOrderPathFormat,
            Uri.EscapeDataString(brokerOrderId));

        var response = await _api.DeleteAsync<FyersOrderIdResponse>(path, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.Id ?? brokerOrderId,
            Status = OrderStatus.Cancelled,
            Message = response.Value.Message,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS has no cancel-all route, so this reads the book and cancels each working order in
    /// turn. Looping is safe here in a way that looping a PLACEMENT never is: a cancel is
    /// idempotent, and cancelling an order that has already gone is not a position.
    ///
    /// Partial failure is reported rather than swallowed. Returning a count that quietly omitted
    /// the three cancels that failed would tell a trader trying to flatten in a hurry that they
    /// were flat when they were not.
    /// </remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var book = await GetOrdersAsync(new OrderQuery { OpenOnly = true }, ct).ConfigureAwait(false);
        if (book.IsFailure)
        {
            return Result<int>.Failure(book.Error);
        }

        var cancelled = 0;
        Error? firstFailure = null;
        var failures = 0;

        foreach (var order in book.Value)
        {
            ct.ThrowIfCancellationRequested();

            var result = await CancelAsync(order.BrokerOrderId, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                cancelled++;
                continue;
            }

            // An order that filled or was cancelled between reading the book and getting here is
            // not a failure to report — the caller asked for no working orders and there are none.
            if (result.Error.Code == ConnectorErrorCodes.OrderNotFound)
            {
                continue;
            }

            failures++;
            firstFailure ??= result.Error;
        }

        if (firstFailure is { } error)
        {
            return Result<int>.Failure(new Error(
                error.Code,
                $"Cancelled {cancelled.ToString(CultureInfo.InvariantCulture)} of "
                + $"{book.Value.Count.ToString(CultureInfo.InvariantCulture)} working orders; "
                + $"{failures.ToString(CultureInfo.InvariantCulture)} could not be cancelled. {error.Message}",
                error.VendorCode,
                error.VendorMessage,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cancelled"] = cancelled.ToString(CultureInfo.InvariantCulture),
                    ["failed"] = failures.ToString(CultureInfo.InvariantCulture),
                }));
        }

        return cancelled;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var response = await _api
            .GetAsync<FyersOrderBookResponse>(_options.OrderBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(response.Error);
        }

        var rows = response.Value.OrderBook ?? [];
        var orders = new List<BrokerOrder>(rows.Count);

        foreach (var row in rows)
        {
            if (IsOutOfScope(row.Symbol))
            {
                continue;
            }

            var mapped = MapOrder(row, _options.OrderBookPath);
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
    public async Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<BrokerOrder>.Failure(FyersErrors.InvalidRequest("An order lookup needs an order id."));
        }

        var response = await _api
            .GetAsync<FyersOrderBookResponse>(
                _options.OrderBookPath,
                new FyersQuery().Add("id", brokerOrderId),
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<BrokerOrder>.Failure(response.Error);
        }

        // The filtered order book still answers with a LIST. An empty one means FYERS has no such
        // order, which is OrderNotFound rather than an empty success — the caller's recovery path
        // after a timed-out placement branches on exactly this.
        var row = response.Value.OrderBook?.FirstOrDefault(o =>
            string.Equals(o.Id, brokerOrderId, StringComparison.Ordinal))
            ?? response.Value.OrderBook?.FirstOrDefault();

        return row is null
            ? Result<BrokerOrder>.Failure(FyersErrors.OrderNotFound(brokerOrderId))
            : MapOrder(row, _options.OrderBookPath);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var response = await _api
            .GetAsync<FyersTradeBookResponse>(_options.TradeBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(response.Error);
        }

        var rows = response.Value.TradeBook ?? [];
        var trades = new List<BrokerTrade>(rows.Count);

        foreach (var row in rows)
        {
            if (IsOutOfScope(row.Symbol))
            {
                continue;
            }

            var mapped = MapTrade(row);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            if (Matches(mapped.Value, query))
            {
                trades.Add(mapped.Value);
            }
        }

        return Result<IReadOnlyList<BrokerTrade>>.Success(trades);
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS takes up to ten orders in one call but gives each leg its OWN status: the outer 200
    /// says only that the batch was received. The manifest declares <c>atomic: false</c> to match,
    /// so the UI warns that a partial fill of the basket is possible — a trader who assumed
    /// atomicity can end up half-hedged.
    /// </remarks>
    public async Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return Result<IReadOnlyList<OrderAck>>.Success([]);
        }

        if (requests.Count > _options.MaxBasketLegs)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(FyersErrors.InvalidRequest(
                $"FYERS accepts at most {_options.MaxBasketLegs.ToString(CultureInfo.InvariantCulture)} "
                + "orders in one basket; split the request."));
        }

        // Every leg is validated BEFORE any of them is sent. A basket whose fourth leg has an
        // unsupported product would otherwise place three orders and then fail, leaving a
        // half-built spread nobody asked for.
        var bodies = new List<FyersPlaceOrderBody>(requests.Count);
        foreach (var request in requests)
        {
            var body = BuildPlaceBody(request);
            if (body.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(body.Error);
            }

            bodies.Add(body.Value);
        }

        var response = await _api
            .PostJsonAsync<FyersMultiOrderResponse>(_options.MultiOrderPath, bodies, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(response.Error);
        }

        var legs = response.Value.Data ?? [];
        var acks = new List<OrderAck>(requests.Count);

        for (var i = 0; i < requests.Count; i++)
        {
            var leg = i < legs.Count ? legs[i] : null;
            var legBody = leg?.Body;

            if (legBody is null || !legBody.IsOk || string.IsNullOrWhiteSpace(legBody.Id))
            {
                // A rejected leg is a REJECTED ORDER, not a failed call: the other nine may well
                // be live. Reporting it as an ack with the broker's own reason keeps the basket
                // result complete, which is what the caller needs to decide what to unwind.
                acks.Add(new OrderAck
                {
                    BrokerOrderId = legBody?.Id ?? string.Empty,
                    Status = OrderStatus.Rejected,
                    ClientOrderId = requests[i].ClientOrderId,
                    Message = legBody?.Message ?? leg?.StatusDescription
                        ?? "FYERS did not report an outcome for this leg.",
                    AcknowledgedAt = _clock.UtcNow,
                });

                continue;
            }

            var ack = BuildAck(legBody, requests[i].ClientOrderId, bodies[i].OrderTag, _options.MultiOrderPath);
            if (ack.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(ack.Error);
            }

            acks.Add(ack.Value);
        }

        return Result<IReadOnlyList<OrderAck>>.Success(acks);
    }

    /// <inheritdoc />
    public async Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildPlaceBody(request);
        if (body.IsFailure)
        {
            return Result<MarginEstimate>.Failure(body.Error);
        }

        var response = await _api
            .PostJsonAsync<FyersMarginResponse>(
                _options.MarginPath,
                new { data = new[] { body.Value.ToMarginLeg() } },
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<MarginEstimate>.Failure(response.Error);
        }

        var margin = response.Value.Data;
        if (margin?.MarginNewOrder is null && margin?.MarginTotal is null)
        {
            return Result<MarginEstimate>.Failure(
                FyersErrors.MissingField(_options.MarginPath, "margin_new_order"));
        }

        // margin_new_order is what THIS order costs; margin_total folds in existing positions and
        // any hedge benefit. The contract asks what the order requires, so the former is the
        // answer and the latter is only the fallback when FYERS omits it.
        var required = margin.MarginNewOrder ?? margin.MarginTotal ?? 0m;

        return new MarginEstimate
        {
            Required = new Money(required, Currency.Inr),
            Available = margin.MarginAvailable is { } available
                ? new Money(available, Currency.Inr)
                : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS publishes charges only AFTER the fact, through the charges-history report. There is
    /// no pre-trade calculator, so the manifest declares <c>chargesEstimate: false</c> and this
    /// declines rather than inventing a brokerage schedule that would drift out of date silently.
    /// </remarks>
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Result<ChargesEstimate>.Failure(ConnectorErrors.NotSupported(
            "a pre-trade charges estimate. FYERS reports charges only after execution")));

    // --- request building --------------------------------------------------------------------

    private Result<FyersPlaceOrderBody> BuildPlaceBody(PlaceOrderRequest request)
    {
        var symbol = _symbols.ToNative(request.Instrument);
        if (symbol.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(symbol.Error);
        }

        var type = FyersMaps.ToNativeOrderType(request.OrderType);
        if (type.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(type.Error);
        }

        var side = FyersMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(side.Error);
        }

        var product = FyersMaps.ToNativeProduct(request.PositionEffect);
        if (product.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(product.Error);
        }

        var validity = FyersMaps.ToNativeValidity(request.TimeInForce);
        if (validity.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(validity.Error);
        }

        var offline = FyersMaps.ToNativeOfflineFlag(request.Variety);
        if (offline.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(offline.Error);
        }

        if (request.Quantity <= Quantity.Zero)
        {
            return Result<FyersPlaceOrderBody>.Failure(FyersErrors.InvalidRequest("Quantity must be positive."));
        }

        if (request.Quantity.IsFractional)
        {
            return Result<FyersPlaceOrderBody>.Failure(ConnectorErrors.NotSupported(
                "fractional quantities. Indian exchanges trade whole units, and derivatives trade in "
                + "multiples of the contract lot size"));
        }

        var currency = ValidateCurrency(request.LimitPrice, request.TriggerPrice);
        if (currency.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(currency.Error);
        }

        var prices = ValidatePrices(request.OrderType, request.LimitPrice, request.TriggerPrice, forModify: false);
        if (prices.IsFailure)
        {
            return Result<FyersPlaceOrderBody>.Failure(prices.Error);
        }

        return new FyersPlaceOrderBody
        {
            Symbol = symbol.Value,
            Quantity = ToWholeQuantity(request.Quantity),
            Type = type.Value,
            Side = side.Value,
            ProductType = product.Value,

            // FYERS wants 0, not null, for a price that does not apply. Sending null makes it
            // reject the order as a bad parameter rather than as a market order without a limit.
            LimitPrice = request.LimitPrice?.Amount ?? 0m,
            StopPrice = request.TriggerPrice?.Amount ?? 0m,
            DisclosedQuantity = request.DisclosedQuantity is { } disclosed ? ToWholeQuantity(disclosed) : 0,
            Validity = validity.Value,
            OfflineOrder = offline.Value,

            // FYERS has exactly one free-text slot and the ClientOrderId has to have it: it is
            // the only way to reconcile a timed-out placement. The trader's own Tag and the
            // AlgoId are persisted locally against this ClientOrderId instead — and SEBI's algo
            // identification runs through the platform's own records, not this field.
            OrderTag = FyersOrderTags.Encode(request.ClientOrderId),
        };
    }

    /// <summary>
    /// The price rules FYERS enforces, checked before the round trip.
    ///
    /// On a modify the rules are looser: a caller amending only the quantity of a limit order is
    /// not required to restate its price, and FYERS keeps whatever the order already had.
    /// </summary>
    private static Result ValidatePrices(OrderType type, Money? limit, Money? trigger, bool forModify)
    {
        var needsLimit = type is OrderType.Limit or OrderType.StopLimit;
        var needsTrigger = type is OrderType.Stop or OrderType.StopLimit;

        if (!forModify && needsLimit && limit is null)
        {
            return FyersErrors.InvalidRequest("A limit price is required for this order type.");
        }

        if (!forModify && needsTrigger && trigger is null)
        {
            return FyersErrors.InvalidRequest("A trigger price is required for this order type.");
        }

        if (limit is { Amount: <= 0m } && needsLimit)
        {
            return FyersErrors.InvalidRequest("A limit price must be positive.");
        }

        if (trigger is { Amount: <= 0m } && needsTrigger)
        {
            return FyersErrors.InvalidRequest("A trigger price must be positive.");
        }

        return Result.Success();
    }

    private static Result ValidateCurrency(params ReadOnlySpan<Money?> prices)
    {
        foreach (var price in prices)
        {
            if (price is { } money && money.Currency != Currency.Inr)
            {
                return FyersErrors.InvalidRequest(
                    $"FYERS settles in INR; this order is priced in {money.Currency}.");
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Quantities on the wire are integers.
    ///
    /// Truncation is safe here only because a fractional quantity has already been refused in
    /// <see cref="BuildPlaceBody"/>. Do not reuse this anywhere that check has not run — silently
    /// rounding 1.6 lots to 1 is a smaller position than the caller asked for, with no error.
    /// </summary>
    private static int ToWholeQuantity(Quantity quantity) => (int)decimal.Truncate(quantity.Value);

    // --- response mapping ---------------------------------------------------------------------

    private Result<OrderAck> BuildAck(
        FyersOrderIdResponse response,
        Guid? clientOrderId,
        string? tag,
        string route)
    {
        var orderId = response.Id;
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Result<OrderAck>.Failure(FyersErrors.MissingField(route, "id"));
        }

        if (tag is not null && clientOrderId is { } id)
        {
            _tagIndex[tag] = id;
        }

        // CODE 201 IS NOT AN ACCEPTANCE. FYERS documents it as "the order request has been made
        // but no acknowledgement has been received — check the orderbook before placing again".
        // The order may be live at the exchange, may have been rejected, or may never have
        // arrived. Reporting it as Submitted would tell the caller the order exists; reporting it
        // as a failure would invite a retry, which is how the duplicate gets created. Unknown is
        // the honest answer: it is neither working nor terminal, so the risk gate will not act on
        // it and the caller reconciles against the order book, which is exactly what FYERS asks
        // for.
        var unacknowledged = response.Code == FyersOrderCodes.PlacedWithoutAcknowledgement;

        return new OrderAck
        {
            BrokerOrderId = orderId,
            Status = unacknowledged ? OrderStatus.Unknown : OrderStatus.Submitted,
            ClientOrderId = clientOrderId,
            Message = unacknowledged
                ? $"{response.Message ?? "FYERS accepted the request."} FYERS did not receive an "
                  + "exchange acknowledgement for this order; its state must be confirmed from the "
                  + "order book before any further action is taken on it."
                : response.Message,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    private Result<BrokerOrder> MapOrder(FyersOrder row, string route)
    {
        if (string.IsNullOrWhiteSpace(row.Id))
        {
            return Result<BrokerOrder>.Failure(FyersErrors.MissingField(route, "id"));
        }

        if (string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerOrder>.Failure(FyersErrors.MissingField(route, "symbol"));
        }

        var instrument = _symbols.ToCanonical(row.Symbol);
        if (instrument.IsFailure)
        {
            return Result<BrokerOrder>.Failure(instrument.Error);
        }

        var side = FyersMaps.ToCanonicalSide(row.Side ?? 0);
        if (side.IsFailure)
        {
            return Result<BrokerOrder>.Failure(side.Error);
        }

        var orderType = FyersMaps.ToCanonicalOrderType(row.Type ?? 0);
        if (orderType.IsFailure)
        {
            return Result<BrokerOrder>.Failure(orderType.Error);
        }

        var effect = FyersMaps.ToCanonicalPositionEffect(row.ProductType);
        if (effect.IsFailure)
        {
            return Result<BrokerOrder>.Failure(effect.Error);
        }

        var quantity = row.Quantity ?? 0m;
        var filled = row.FilledQuantity ?? 0m;
        var status = FyersMaps.ToCanonicalOrderStatusOrUnknown(row.Status ?? 0, filled, out var rawStatus);

        var statusMessage = rawStatus is null
            ? Blank(row.Message)
            : Blank(row.Message) is { } vendorMessage
                ? $"{vendorMessage} ({rawStatus} is not recognised by this connector)"
                : $"{rawStatus} is not recognised by this connector.";

        return new BrokerOrder
        {
            BrokerOrderId = row.Id,
            ClientOrderId = FyersOrderTags.Decode(row.OrderTag, _tagIndex),
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(quantity),
            FilledQuantity = new Quantity(filled),
            Status = status,
            OrderType = orderType.Value,
            PositionEffect = effect.Value,
            TimeInForce = FyersMaps.ToCanonicalTimeInForce(row.Validity).ValueOr(TimeInForce.Day),
            Variety = FyersMaps.ToCanonicalVariety(row.OfflineOrder ?? false),

            // A zero price is how FYERS says "not applicable" on a market order, not a price of
            // zero; only positive values become a Money.
            LimitPrice = row.LimitPrice is > 0m ? new Money(row.LimitPrice.Value, Currency.Inr) : null,
            TriggerPrice = row.StopPrice is > 0m ? new Money(row.StopPrice.Value, Currency.Inr) : null,
            AveragePrice = row.TradedPrice is > 0m ? new Money(row.TradedPrice.Value, Currency.Inr) : null,
            PlacedAt = FyersTime.ParseOr(row.OrderDateTime, _clock.UtcNow),
            StatusMessage = statusMessage,
        };
    }

    private Result<BrokerTrade> MapTrade(FyersTrade row)
    {
        if (string.IsNullOrWhiteSpace(row.TradeNumber))
        {
            return Result<BrokerTrade>.Failure(FyersErrors.MissingField(_options.TradeBookPath, "tradeNumber"));
        }

        if (string.IsNullOrWhiteSpace(row.OrderNumber))
        {
            return Result<BrokerTrade>.Failure(FyersErrors.MissingField(_options.TradeBookPath, "orderNumber"));
        }

        if (string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerTrade>.Failure(FyersErrors.MissingField(_options.TradeBookPath, "symbol"));
        }

        var instrument = _symbols.ToCanonical(row.Symbol);
        if (instrument.IsFailure)
        {
            return Result<BrokerTrade>.Failure(instrument.Error);
        }

        var side = FyersMaps.ToCanonicalSide(row.Side ?? 0);
        if (side.IsFailure)
        {
            return Result<BrokerTrade>.Failure(side.Error);
        }

        return new BrokerTrade
        {
            TradeId = row.TradeNumber,
            BrokerOrderId = row.OrderNumber,
            Instrument = instrument.Value,
            Side = side.Value,
            Quantity = new Quantity(row.TradedQuantity ?? 0m),
            Price = new Money(row.TradePrice ?? 0m, Currency.Inr),
            ExecutedAt = FyersTime.ParseOr(row.OrderDateTime, _clock.UtcNow),

            // FYERS reports charges only in the post-trade charges report, aggregated by day and
            // segment rather than per fill. There is nothing per-trade to put here, and a
            // locally estimated number presented as the broker's own would be worse than none.
            Charges = null,
        };
    }

    /// <summary>
    /// Whether a row belongs to a venue this connector does not serve.
    ///
    /// A FYERS account's order book spans every segment the USER trades, and this connector
    /// declares only NSE and BSE. Someone who also trades gold on MCX has commodity rows in the
    /// same response, and failing the whole book over them would blank a blotter that is
    /// otherwise entirely correct. Skipping is checked on the SYMBOL PREFIX, before translation,
    /// so it can never swallow an in-scope instrument that merely failed to resolve — that case
    /// still fails loudly, which is what makes an un-ingested symbol master visible.
    /// </summary>
    private bool IsOutOfScope(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var separator = symbol.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (FyersMaps.ToCanonicalVenue(symbol[..separator]).IsSuccess)
        {
            return false;
        }

        // Guarded rather than left to the logging framework: this runs once per order-book row,
        // and the params array behind a structured-logging call is allocated whether or not the
        // level is enabled.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{ConnectorId}: skipping {Symbol}; its venue is outside this connector's declared venues.",
                FyersAuth.ConnectorId,
                symbol);
        }

        return true;
    }

    private bool Matches(BrokerOrder order, OrderQuery query)
    {
        if (query.OpenOnly && !order.Status.IsWorking())
        {
            return false;
        }

        if (query.Instrument is { } instrument && order.Instrument != instrument)
        {
            return false;
        }

        return WithinDates(order.PlacedAt, query);
    }

    private bool Matches(BrokerTrade trade, OrderQuery query)
    {
        if (query.Instrument is { } instrument && trade.Instrument != instrument)
        {
            return false;
        }

        return WithinDates(trade.ExecutedAt, query);
    }

    /// <summary>
    /// Date bounds are TRADING dates, evaluated in the venue's own zone.
    ///
    /// The distinction is not academic: an after-market order placed at 23:50 IST belongs to that
    /// day in Mumbai and to the next one in UTC, so filtering on the UTC date drops every
    /// evening's AMO flow out of "today".
    /// </summary>
    private bool WithinDates(DateTimeOffset instant, OrderQuery query)
    {
        if (query.From is null && query.To is null)
        {
            return true;
        }

        var date = FyersTime.VenueDate(instant, _venueZone);

        return (query.From is not { } from || date >= from)
               && (query.To is not { } to || date <= to);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Response codes FYERS returns whose meaning changes what the caller must do next.</summary>
internal static class FyersOrderCodes
{
    /// <summary>
    /// "Order request has been made but no acknowledgement has been received." Documented on
    /// every order route. The order's real state is only knowable from the order book.
    /// </summary>
    public const int PlacedWithoutAcknowledgement = 201;
}

/// <summary>
/// Folds the platform's <c>ClientOrderId</c> into the FYERS <c>orderTag</c>.
///
/// Twenty hex characters is eighty bits of the GUID — far more than enough to be unique across a
/// trading day, and comfortably inside any tag length FYERS accepts. The truncation is one-way,
/// which is why the connector keeps an index rather than trying to reconstruct the GUID.
///
/// FYERS PREFIXES EVERY TAG IT RETURNS. A tag supplied by the caller comes back as
/// <c>1:&lt;tag&gt;</c> and one FYERS generated itself as <c>2:&lt;tag&gt;</c> — so an order
/// placed with tag <c>a1b2…</c> is read back as <c>1:a1b2…</c>. Decoding without stripping that
/// prefix finds nothing in the index, which quietly makes every order look like it belonged to
/// somebody else and defeats the reconciliation the tag exists for.
///
/// This is a convenience for in-flight correlation. The authoritative mapping is written by the
/// order store BEFORE the placement call, so that a timed-out placement can be reconciled against
/// the order book instead of retried into a duplicate order.
/// </summary>
public static class FyersOrderTags
{
    /// <summary>Characters of the GUID carried in the tag.</summary>
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

        return index.TryGetValue(StripSourcePrefix(tag), out var id) ? id : null;
    }

    /// <summary>
    /// Removes the <c>1:</c> or <c>2:</c> source marker FYERS puts in front of every tag.
    /// A tag with no marker is returned unchanged, so this is safe to apply unconditionally.
    /// </summary>
    public static string StripSourcePrefix(string tag)
    {
        var trimmed = tag.Trim();

        return trimmed.Length > 2 && trimmed[1] == ':' && char.IsAsciiDigit(trimmed[0])
            ? trimmed[2..]
            : trimmed;
    }
}
