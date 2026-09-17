using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Orders and fills through Longbridge's REST trade routes.
///
/// IDEMPOTENCY IS THE BROKER'S. Every submission carries the ClientOrderId twice: as <c>client_request_id</c>,
/// which Longbridge caches for ten minutes and answers a repeat of with the original order instead of a second
/// one, and as <c>remark</c>, which is echoed on every order row indefinitely, so a timed-out placement can be
/// found in the book after the cache has expired.
///
/// A REPLACE RESTATES THE ORDER. The replace route requires the quantity on every call, so a price-only
/// amendment reads the order first and sends its quantity back unchanged, and a quantity-only amendment sends
/// the existing prices back.
///
/// THE BOOK IS TWO ROUTES. Today's orders and fills have their own routes; older ones are history, queried by a
/// window of Unix seconds. A query spanning both reads both and merges by id, today's copy winning as the fresher.
/// </summary>
public sealed class LongbridgeOrders : IConnectorOrders
{
    private const string OrderPath = "/v1/trade/order";
    private const string TodayOrdersPath = "/v1/trade/order/today";
    private const string HistoryOrdersPath = "/v1/trade/order/history";
    private const string TodayExecutionsPath = "/v1/trade/execution/today";
    private const string HistoryExecutionsPath = "/v1/trade/execution/history";

    private readonly LongbridgeChannel _channel;
    private readonly LongbridgeOptions _options;
    private readonly LongbridgeInstrumentCache _cache;
    private readonly LongbridgeInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    internal LongbridgeOrders(
        LongbridgeChannel channel,
        LongbridgeOptions options,
        LongbridgeInstrumentCache cache,
        LongbridgeInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        IClock clock,
        ILogger logger)
    {
        _channel = channel;
        _options = options;
        _cache = cache;
        _resolver = resolver;
        _requireSession = requireSession;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<OrderAck>.Failure(session.Error);
        }

        var body = BuildSubmit(request);
        if (body.IsFailure)
        {
            return Result<OrderAck>.Failure(body.Error);
        }

        var response = await _channel.Api
            .PostJsonAsync<LbSubmitOrderResponse>(OrderPath, body.Value, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        if (string.IsNullOrWhiteSpace(response.Value.OrderId))
        {
            return Result<OrderAck>.Failure(LongbridgeErrors.MissingField(OrderPath, "order_id"));
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.OrderId.Trim(),
            Status = OrderStatus.Submitted,
            ClientOrderId = request.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<OrderAck>.Failure(session.Error);
        }

        if (request.OrderType is not null || request.TimeInForce is not null)
        {
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported(
                "changing an order's type or time in force through Longbridge; cancel the order and place a new one"));
        }

        if (request.DisclosedQuantity is not null)
        {
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported("disclosed quantity through Longbridge"));
        }

        if (request.Quantity is null && request.LimitPrice is null && request.TriggerPrice is null)
        {
            return Invalid<OrderAck>("A modify must change the quantity, the limit price or the trigger price.");
        }

        var current = await ReadOrderAsync(request.BrokerOrderId, ct).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<OrderAck>.Failure(current.Error);
        }

        var order = current.Value;
        var orderId = order.OrderId!.Trim();

        var type = LongbridgeMaps.ToCanonicalOrderType(order.OrderType);
        if (type.IsFailure)
        {
            return Result<OrderAck>.Failure(type.Error);
        }

        var status = LongbridgeMaps.ToCanonicalOrderStatusOrUnknown(order.Status, out _);
        if (status.IsTerminal())
        {
            return Result<OrderAck>.Failure(new Error(
                ConnectorErrorCodes.OrderRejected,
                $"Longbridge order {orderId} is already {status} and cannot be modified."));
        }

        var quantity = request.Quantity?.Value ?? LongbridgeNumber.DecimalOrZero(order.Quantity);
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<OrderAck>("A Longbridge order quantity must be a positive whole number.");
        }

        if (request.LimitPrice is not null && !LongbridgeMaps.UsesLimitPrice(type.Value))
        {
            return Invalid<OrderAck>($"A {type.Value} order has no limit price to change.");
        }

        if (request.TriggerPrice is not null && !LongbridgeMaps.UsesTriggerPrice(type.Value))
        {
            return Invalid<OrderAck>($"A {type.Value} order has no trigger price to change.");
        }

        var currency = CurrencyOf(order);
        if (request.LimitPrice is { } newLimit && (newLimit.Amount <= 0m || newLimit.Currency != currency))
        {
            return Invalid<OrderAck>($"The new limit price must be positive and in {currency}.");
        }

        if (request.TriggerPrice is { } newTrigger && (newTrigger.Amount <= 0m || newTrigger.Currency != currency))
        {
            return Invalid<OrderAck>($"The new trigger price must be positive and in {currency}.");
        }

        var body = new LbReplaceOrderRequest
        {
            OrderId = orderId,
            Quantity = LongbridgeNumber.Wire(quantity),
            Price = LongbridgeMaps.UsesLimitPrice(type.Value)
                ? request.LimitPrice is { } limit ? LongbridgeNumber.Wire(limit.Amount) : order.Price
                : null,
            TriggerPrice = LongbridgeMaps.UsesTriggerPrice(type.Value)
                ? request.TriggerPrice is { } trigger ? LongbridgeNumber.Wire(trigger.Amount) : order.TriggerPrice
                : null,
        };

        var response = await _channel.Api
            .PutJsonAsync<LongbridgeEmpty>(OrderPath, body, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = orderId,
            Status = status,
            ClientOrderId = LongbridgeOrderMapper.ParseClientOrderId(order.Remark),
            Message = "Replace accepted; the order book shows the amended order once Longbridge confirms it.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<OrderAck>.Failure(session.Error);
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<OrderAck>("A broker order id is required to cancel an order.");
        }

        var id = brokerOrderId.Trim();

        var response = await _channel.Api
            .DeleteAsync<LongbridgeEmpty>(LongbridgeQuery.Build(OrderPath, ("order_id", id)), isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        // A cancel is a request: the order passes through WaitToCancel before Longbridge confirms it. Read it
        // back so the acknowledgement says where the order actually stands, not where it is hoped to be.
        var after = await ReadOrderAsync(id, ct).ConfigureAwait(false);
        var status = after.IsSuccess ? LongbridgeMaps.ToCanonicalOrderStatusOrUnknown(after.Value.Status, out _) : OrderStatus.Open;

        return new OrderAck
        {
            BrokerOrderId = id,
            Status = status,
            ClientOrderId = after.IsSuccess ? LongbridgeOrderMapper.ParseClientOrderId(after.Value.Remark) : null,
            Message = status == OrderStatus.Cancelled ? null : "Cancellation requested; Longbridge has not confirmed it yet.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// There is no cancel-all route, so this reads today's working orders and cancels each. It reports how
    /// many cancellations Longbridge accepted, and fails only when there were orders to cancel and none could be.
    /// </remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<int>.Failure(session.Error);
        }

        var today = await _channel.Api.GetAsync<LbOrdersResponse>(TodayOrdersPath, ct).ConfigureAwait(false);
        if (today.IsFailure)
        {
            return Result<int>.Failure(today.Error);
        }

        var working = (today.Value.Orders ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.OrderId)
                        && LongbridgeMaps.ToCanonicalOrderStatusOrUnknown(o.Status, out _).IsWorking())
            .Select(o => o.OrderId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var cancelled = 0;
        Error? firstFailure = null;

        foreach (var id in working)
        {
            var result = await _channel.Api
                .DeleteAsync<LongbridgeEmpty>(LongbridgeQuery.Build(OrderPath, ("order_id", id)), isTradeWrite: true, ct)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                cancelled++;
                continue;
            }

            firstFailure ??= result.Error;
            _logger.LogWarning(
                "{ConnectorId}: cancel-all could not cancel order {OrderId}: {Error}",
                LongbridgeAuth.ConnectorId,
                id,
                result.Error.ToString());
        }

        return cancelled == 0 && firstFailure is { } failure ? Result<int>.Failure(failure) : cancelled;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(session.Error);
        }

        var symbol = SymbolFilter(query.Instrument);
        if (symbol.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(symbol.Error);
        }

        var plan = Plan(query);
        var rows = new Dictionary<string, LbOrder>(StringComparer.Ordinal);

        if (plan.ReadToday)
        {
            var today = await _channel.Api
                .GetAsync<LbOrdersResponse>(LongbridgeQuery.Build(TodayOrdersPath, ("symbol", symbol.Value)), ct)
                .ConfigureAwait(false);

            if (today.IsFailure)
            {
                return Result<IReadOnlyList<BrokerOrder>>.Failure(today.Error);
            }

            foreach (var row in today.Value.Orders ?? [])
            {
                if (row.OrderId is { Length: > 0 } id)
                {
                    rows[id] = row;
                }
            }
        }

        if (plan.ReadHistory)
        {
            var history = await _channel.Api
                .GetAsync<LbOrdersResponse>(
                    LongbridgeQuery.Build(HistoryOrdersPath, ("symbol", symbol.Value), ("start_at", plan.Start), ("end_at", plan.End)),
                    ct)
                .ConfigureAwait(false);

            if (history.IsFailure)
            {
                return Result<IReadOnlyList<BrokerOrder>>.Failure(history.Error);
            }

            foreach (var row in history.Value.Orders ?? [])
            {
                if (row.OrderId is { Length: > 0 } id)
                {
                    rows.TryAdd(id, row);
                }
            }
        }

        var ensured = await _resolver.EnsureAsync(_channel, rows.Values.Select(r => r.Symbol ?? string.Empty), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(ensured.Error);
        }

        var orders = new List<BrokerOrder>(rows.Count);

        foreach (var row in rows.Values)
        {
            if (SkipOutOfScope(row.Symbol))
            {
                continue;
            }

            var mapped = LongbridgeOrderMapper.MapOrder(row, _cache, _clock, lenient: true);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerOrder>>.Failure(mapped.Error);
            }

            var order = mapped.Value;
            if ((query.Instrument is { } wanted && order.Instrument != wanted)
                || (query.OpenOnly && !order.Status.IsWorking())
                || !InWindow(order.PlacedAt, row.Symbol, query))
            {
                continue;
            }

            orders.Add(order);
        }

        return Result<IReadOnlyList<BrokerOrder>>.Success([.. orders.OrderByDescending(o => o.PlacedAt)]);
    }

    /// <inheritdoc />
    public async Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<BrokerOrder>.Failure(session.Error);
        }

        var row = await ReadOrderAsync(brokerOrderId, ct).ConfigureAwait(false);
        if (row.IsFailure)
        {
            return Result<BrokerOrder>.Failure(row.Error);
        }

        var ensured = await _resolver.EnsureAsync(_channel, [row.Value.Symbol ?? string.Empty], ct).ConfigureAwait(false);
        return ensured.IsFailure
            ? Result<BrokerOrder>.Failure(ensured.Error)
            : LongbridgeOrderMapper.MapOrder(row.Value, _cache, _clock, lenient: true);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(session.Error);
        }

        var symbol = SymbolFilter(query.Instrument);
        if (symbol.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(symbol.Error);
        }

        var plan = Plan(query);
        var rows = new Dictionary<string, LbExecution>(StringComparer.Ordinal);

        if (plan.ReadToday)
        {
            var today = await _channel.Api
                .GetAsync<LbExecutionsResponse>(LongbridgeQuery.Build(TodayExecutionsPath, ("symbol", symbol.Value)), ct)
                .ConfigureAwait(false);

            if (today.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(today.Error);
            }

            foreach (var row in today.Value.Trades ?? [])
            {
                rows[TradeKey(row)] = row;
            }
        }

        if (plan.ReadHistory)
        {
            for (var page = 1; page <= _options.MaxHistoryPages; page++)
            {
                var history = await _channel.Api
                    .GetAsync<LbExecutionsResponse>(
                        LongbridgeQuery.Build(
                            HistoryExecutionsPath,
                            ("symbol", symbol.Value),
                            ("start_at", plan.Start),
                            ("end_at", plan.End),
                            ("page", page.ToString(CultureInfo.InvariantCulture))),
                        ct)
                    .ConfigureAwait(false);

                if (history.IsFailure)
                {
                    return Result<IReadOnlyList<BrokerTrade>>.Failure(history.Error);
                }

                foreach (var row in history.Value.Trades ?? [])
                {
                    rows.TryAdd(TradeKey(row), row);
                }

                if (history.Value.HasMore != true)
                {
                    break;
                }
            }
        }

        var ensured = await _resolver.EnsureAsync(_channel, rows.Values.Select(r => r.Symbol ?? string.Empty), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(ensured.Error);
        }

        var trades = new List<BrokerTrade>(rows.Count);

        foreach (var row in rows.Values)
        {
            if (SkipOutOfScope(row.Symbol))
            {
                continue;
            }

            var mapped = LongbridgeOrderMapper.MapTrade(row, _cache, _clock);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            var trade = mapped.Value;
            if ((query.Instrument is { } wanted && trade.Instrument != wanted) || !InWindow(trade.ExecutedAt, row.Symbol, query))
            {
                continue;
            }

            trades.Add(trade);
        }

        return Result<IReadOnlyList<BrokerTrade>>.Success([.. trades.OrderByDescending(t => t.ExecutedAt)]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Longbridge has no basket route; the manifest declares the basket non-atomic. Every leg is validated before
    /// any is sent, so a basket that is wrong on leg four never leaves three orders working. A leg the broker then
    /// rejects stops the loop, and the failure names the orders already placed.
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
            return Invalid<IReadOnlyList<OrderAck>>(
                $"Longbridge baskets are sent one order at a time and capped at {_options.MaxBasketLegs} legs; this one has {requests.Count}.");
        }

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(session.Error);
        }

        for (var i = 0; i < requests.Count; i++)
        {
            var built = BuildSubmit(requests[i]);
            if (built.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(built.Error with { Message = $"Leg {i + 1}: {built.Error.Message}" });
            }
        }

        var acks = new List<OrderAck>(requests.Count);

        foreach (var request in requests)
        {
            var ack = await PlaceAsync(request, ct).ConfigureAwait(false);
            if (ack.IsFailure)
            {
                var placed = acks.Count == 0 ? "none" : string.Join(", ", acks.Select(a => a.BrokerOrderId));
                return Result<IReadOnlyList<OrderAck>>.Failure(ack.Error with
                {
                    Message = $"Leg {acks.Count + 1} of {requests.Count} failed; orders already placed: {placed}. {ack.Error.Message}",
                });
            }

            acks.Add(ack.Value);
        }

        return Result<IReadOnlyList<OrderAck>>.Success(acks);
    }

    /// <inheritdoc />
    public Task<Result<MarginEstimate>> EstimateMarginAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result<MarginEstimate>.Failure(ConnectorErrors.NotSupported(
            "pre-trade margin estimates. Longbridge estimates the maximum purchasable quantity, not the margin an order requires")));

    /// <inheritdoc />
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result<ChargesEstimate>.Failure(ConnectorErrors.NotSupported(
            "pre-trade charges estimates. Longbridge itemises charges only on an order that exists")));

    /// <summary>A canonical placement to Longbridge's submit body. Structural: no network, so a basket validates cheaply.</summary>
    internal static Result<LbSubmitOrderRequest> BuildSubmit(PlaceOrderRequest request)
    {
        var quantity = request.Quantity.Value;
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<LbSubmitOrderRequest>("Longbridge takes positive whole numbers of shares and contracts.");
        }

        if (request.Variety != OrderVariety.Regular)
        {
            return Result<LbSubmitOrderRequest>.Failure(ConnectorErrors.NotSupported($"the {request.Variety} order variety through Longbridge"));
        }

        if (request.DisclosedQuantity is not null)
        {
            return Result<LbSubmitOrderRequest>.Failure(ConnectorErrors.NotSupported("disclosed quantity through Longbridge"));
        }

        var effect = LongbridgeMaps.ValidatePositionEffect(request.PositionEffect);
        if (effect.IsFailure)
        {
            return Result<LbSubmitOrderRequest>.Failure(effect.Error);
        }

        LongbridgeMarket market;
        string native;

        if (request.Instrument.AssetClass == AssetClass.Option)
        {
            var option = LongbridgeNative.ForOptionKey(request.Instrument);
            if (option.IsFailure)
            {
                return Result<LbSubmitOrderRequest>.Failure(option.Error);
            }

            (market, native) = (LongbridgeMarket.Us, option.Value);
        }
        else
        {
            var cash = LongbridgeNative.ForCashKey(request.Instrument);
            if (cash.IsFailure)
            {
                return Result<LbSubmitOrderRequest>.Failure(cash.Error);
            }

            (market, native) = cash.Value;
        }

        var orderType = LongbridgeMaps.ToNativeOrderType(request.OrderType, market);
        if (orderType.IsFailure)
        {
            return Result<LbSubmitOrderRequest>.Failure(orderType.Error);
        }

        var timeInForce = LongbridgeMaps.ToNativeTimeInForce(request.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<LbSubmitOrderRequest>.Failure(timeInForce.Error);
        }

        if (request.TimeInForce == TimeInForce.Gtd && request.GoodTillDate is null)
        {
            return Invalid<LbSubmitOrderRequest>("A GTD order needs a good-till date.");
        }

        string? limitPrice = null;
        if (LongbridgeMaps.UsesLimitPrice(request.OrderType))
        {
            if (request.LimitPrice is not { } limit || limit.Amount <= 0m)
            {
                return Invalid<LbSubmitOrderRequest>($"A {request.OrderType} order needs a positive limit price.");
            }

            if (limit.Currency != market.Currency)
            {
                return Invalid<LbSubmitOrderRequest>($"{native} is priced in {market.Currency}; the limit price is in {limit.Currency}.");
            }

            limitPrice = LongbridgeNumber.Wire(limit.Amount);
        }

        string? triggerPrice = null;
        if (LongbridgeMaps.UsesTriggerPrice(request.OrderType))
        {
            if (request.TriggerPrice is not { } trigger || trigger.Amount <= 0m)
            {
                return Invalid<LbSubmitOrderRequest>($"A {request.OrderType} order needs a positive trigger price.");
            }

            if (trigger.Currency != market.Currency)
            {
                return Invalid<LbSubmitOrderRequest>($"{native} is priced in {market.Currency}; the trigger price is in {trigger.Currency}.");
            }

            triggerPrice = LongbridgeNumber.Wire(trigger.Amount);
        }

        var clientOrderId = request.ClientOrderId.ToString("N", CultureInfo.InvariantCulture);

        return new LbSubmitOrderRequest
        {
            Symbol = native,
            OrderType = orderType.Value,
            Side = LongbridgeMaps.ToNativeSide(request.Side),
            SubmittedQuantity = LongbridgeNumber.Wire(quantity),
            TimeInForce = timeInForce.Value,
            SubmittedPrice = limitPrice,
            TriggerPrice = triggerPrice,
            ExpireDate = request.TimeInForce == TimeInForce.Gtd ? LongbridgeTime.Iso(request.GoodTillDate!.Value) : null,
            OutsideRth = market.Suffix == LongbridgeMarket.Us.Suffix ? LongbridgeMaps.OutsideRthRegularOnly : null,
            Remark = clientOrderId,
            ClientRequestId = clientOrderId,
        };
    }

    private async Task<Result<LbOrder>> ReadOrderAsync(string brokerOrderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<LbOrder>("A broker order id is required.");
        }

        var id = brokerOrderId.Trim();
        var response = await _channel.Api
            .GetAsync<LbOrder>(LongbridgeQuery.Build(OrderPath, ("order_id", id)), ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return response;
        }

        return string.IsNullOrWhiteSpace(response.Value.OrderId)
            ? Result<LbOrder>.Failure(LongbridgeErrors.OrderNotFound(id))
            : response;
    }

    /// <summary>
    /// Which routes a query needs, and the history window. "Today" at Longbridge is a trading day in the
    /// market's own zone, up to a calendar day either side of UTC, so the edges are generous and rows are
    /// filtered afterwards by their market date.
    /// </summary>
    private (bool ReadToday, bool ReadHistory, string? Start, string? End) Plan(OrderQuery query)
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var readToday = query.To is not { } to || to >= today.AddDays(-1);
        var readHistory = query.From is { } from && from < today || query.To is { } until && until < today;

        var start = query.From is { } first
            ? UnixSeconds(new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14))
            : null;

        var end = query.To is { } last
            ? UnixSeconds(Min(new DateTimeOffset(last.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).AddHours(12), now))
            : null;

        return (readToday, readHistory, start, end);
    }

    private static bool InWindow(DateTimeOffset instant, string? symbol, OrderQuery query)
    {
        if (query.From is null && query.To is null)
        {
            return true;
        }

        var zone = symbol is not null && LongbridgeNative.TrySplit(symbol.Trim().ToUpperInvariant(), out var market, out _)
            ? market.Zone
            : LongbridgeTime.HongKong;

        var date = LongbridgeTime.MarketDate(instant, zone);
        return (query.From is not { } from || date >= from) && (query.To is not { } to || date <= to);
    }

    private bool SkipOutOfScope(string? symbol)
    {
        var native = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!_cache.IsOutOfScope(native))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{ConnectorId}: skipping a row for {Native}, outside the declared venues.", LongbridgeAuth.ConnectorId, native);
        }

        return true;
    }

    private static Result<string?> SymbolFilter(InstrumentKey? instrument)
    {
        if (instrument is not { } key)
        {
            return Result<string?>.Success(null);
        }

        if (key.AssetClass == AssetClass.Option)
        {
            var option = LongbridgeNative.ForOptionKey(key);
            return option.IsFailure ? Result<string?>.Failure(option.Error) : Result<string?>.Success(option.Value);
        }

        var cash = LongbridgeNative.ForCashKey(key);
        return cash.IsFailure ? Result<string?>.Failure(cash.Error) : Result<string?>.Success(cash.Value.Native);
    }

    private Currency CurrencyOf(LbOrder order)
    {
        if (LongbridgeMaps.ToCanonicalCurrency(order.Currency) is { IsSuccess: true } listed)
        {
            return listed.Value;
        }

        var native = order.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        return _cache.TryGetByNative(native, out var record)
            ? record.Definition.Currency
            : LongbridgeNative.TrySplit(native, out var market, out _) ? market.Currency : Currency.Usd;
    }

    private static string TradeKey(LbExecution row) => $"{row.OrderId}:{row.TradeId}";

    private static string UnixSeconds(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static Result<T> Invalid<T>(string message) => Result<T>.Failure(LongbridgeErrors.InvalidRequest(message));
}
