using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Orders, fills and order previews through Tiger's OpenAPI.
///
/// THE CLIENT ORDER ID TRAVELS IN <c>user_mark</c>, which Tiger echoes on every order row. Tiger offers no
/// idempotency key, so a placement that times out is recovered the way the platform recovers any such order: by
/// reading the book back and matching on that marker, never by resending.
///
/// A MODIFY RESTATES THE ORDER. Tiger's modify takes a whole ticket, so the order is read first and sent back with
/// only the changed values different.
///
/// ORDERS ARE PAGED. Every list method answers a page and a token; a query walks them to
/// <see cref="TigerOptions.MaxOrderPages"/>.
/// </summary>
public sealed class TigerOrders : IConnectorOrders
{
    private readonly TigerChannel _channel;
    private readonly TigerOptions _options;
    private readonly TigerInstrumentCache _cache;
    private readonly TigerInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    internal TigerOrders(
        TigerChannel channel,
        TigerOptions options,
        TigerInstrumentCache cache,
        TigerInstrumentResolver resolver,
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

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        var ticket = await BuildTicketAsync(api, request, account.Value, ct).ConfigureAwait(false);
        if (ticket.IsFailure)
        {
            return Result<OrderAck>.Failure(ticket.Error);
        }

        var placed = await api
            .CallAsync<TigerPlacedOrder>(TigerMaps.MethodPlaceOrder, ticket.Value.Biz, account.Value.Credentials, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (placed.IsFailure)
        {
            return Result<OrderAck>.Failure(placed.Error);
        }

        if (string.IsNullOrWhiteSpace(placed.Value.Id))
        {
            return Result<OrderAck>.Failure(TigerErrors.MissingField(TigerMaps.MethodPlaceOrder, "id"));
        }

        return new OrderAck
        {
            BrokerOrderId = placed.Value.Id.Trim(),
            Status = OrderStatus.Submitted,
            ClientOrderId = request.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        if (request.OrderType is not null || request.TimeInForce is not null)
        {
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported(
                "changing an order's type or time in force through this connector; cancel the order and place a new one"));
        }

        if (request.DisclosedQuantity is not null)
        {
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported("disclosed quantity through Tiger"));
        }

        if (request.Quantity is null && request.LimitPrice is null && request.TriggerPrice is null)
        {
            return Invalid<OrderAck>("A modify must change the quantity, the limit price or the trigger price.");
        }

        var orderId = request.BrokerOrderId?.Trim() ?? string.Empty;
        if (orderId.Length == 0)
        {
            return Invalid<OrderAck>("A broker order id is required.");
        }

        var api = _channel.Trading(account.Value.Credentials);

        var existing = await ReadOrderAsync(api, account.Value, orderId, ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<OrderAck>.Failure(existing.Error);
        }

        var (row, record, order) = existing.Value;
        if (order.Status.IsTerminal())
        {
            return Result<OrderAck>.Failure(new Error(
                ConnectorErrorCodes.OrderRejected,
                $"Tiger order {orderId} is already {order.Status} and cannot be modified."));
        }

        if (request.LimitPrice is not null && !TigerMaps.UsesLimitPrice(order.OrderType))
        {
            return Invalid<OrderAck>($"A {order.OrderType} order has no limit price to change.");
        }

        if (request.TriggerPrice is not null && !TigerMaps.UsesTriggerPrice(order.OrderType))
        {
            return Invalid<OrderAck>($"A {order.OrderType} order has no trigger price to change.");
        }

        var quantity = request.Quantity?.Value ?? order.Quantity.Value;
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<OrderAck>("A Tiger order quantity must be a positive whole number.");
        }

        var currency = record.Definition.Currency;
        var limit = request.LimitPrice ?? order.LimitPrice;
        var trigger = request.TriggerPrice ?? order.TriggerPrice;

        if ((limit is { } l && (l.Amount <= 0m || l.Currency != currency))
            || (trigger is { } t && (t.Amount <= 0m || t.Currency != currency)))
        {
            return Invalid<OrderAck>($"Prices for this order must be positive and in {currency}.");
        }

        var nativeType = TigerMaps.ToNativeOrderType(order.OrderType);
        if (nativeType.IsFailure)
        {
            return Result<OrderAck>.Failure(nativeType.Error);
        }

        var nativeTif = TigerMaps.ToNativeTimeInForce(order.TimeInForce);
        if (nativeTif.IsFailure)
        {
            return Result<OrderAck>.Failure(nativeTif.Error);
        }

        var biz = Contract(record)
            .Add("account", account.Value.AccountId)
            .Add("id", orderId)
            .Add("action", TigerMaps.ToNativeSide(order.Side))
            .Add("order_type", nativeType.Value)
            .Add("total_quantity", quantity)
            .Add("limit_price", TigerMaps.UsesLimitPrice(order.OrderType) ? limit?.Amount : null)
            .Add("aux_price", TigerMaps.UsesTriggerPrice(order.OrderType) ? trigger?.Amount : null)
            .Add("time_in_force", nativeTif.Value)
            .Add("outside_rth", false)
            .Add("user_mark", row.UserMark)
            .Add("lang", _options.Language);

        var modified = await api
            .CallAsync(TigerMaps.MethodModifyOrder, biz, account.Value.Credentials, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (modified.IsFailure)
        {
            return Result<OrderAck>.Failure(modified.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = orderId,
            Status = order.Status,
            ClientOrderId = order.ClientOrderId,
            Message = "Modification accepted; the order book shows the amended order once Tiger confirms it.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<OrderAck>("A broker order id is required to cancel an order.");
        }

        var api = _channel.Trading(account.Value.Credentials);
        var id = brokerOrderId.Trim();

        var cancelled = await CancelOneAsync(api, account.Value, id, ct).ConfigureAwait(false);
        if (cancelled.IsFailure)
        {
            return Result<OrderAck>.Failure(cancelled.Error);
        }

        // Tiger acknowledges the request, not the cancellation. Read the order back so the acknowledgement is honest.
        var after = await ReadOrderAsync(api, account.Value, id, ct).ConfigureAwait(false);
        var status = after.IsSuccess ? after.Value.Order.Status : OrderStatus.Open;

        return new OrderAck
        {
            BrokerOrderId = id,
            Status = status,
            ClientOrderId = after.IsSuccess ? after.Value.Order.ClientOrderId : null,
            Message = status == OrderStatus.Cancelled ? null : "Cancellation requested; Tiger has not confirmed it yet.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>There is no cancel-all method; this cancels each of the account's active orders.</remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<int>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        var active = await api
            .CallAsync<TigerPage<TigerOrder>>(
                TigerMaps.MethodActiveOrders,
                TigerBiz.New().Add("account", account.Value.AccountId).Add("lang", _options.Language),
                account.Value.Credentials,
                isTradeWrite: false,
                ct)
            .ConfigureAwait(false);

        if (active.IsFailure)
        {
            return Result<int>.Failure(active.Error);
        }

        var working = (active.Value.Items ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Id)
                        && TigerMaps.ToCanonicalOrderStatusOrUnknown(o.Status, out _).IsWorking())
            .Select(o => o.Id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var cancelled = 0;
        Error? firstFailure = null;

        foreach (var id in working)
        {
            var result = await CancelOneAsync(api, account.Value, id, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                cancelled++;
                continue;
            }

            firstFailure ??= result.Error;
            _logger.LogWarning(
                "{ConnectorId}: cancel-all could not cancel order {OrderId}: {Error}",
                TigerAuth.ConnectorId,
                id,
                result.Error.ToString());
        }

        return cancelled == 0 && firstFailure is { } failure ? Result<int>.Failure(failure) : cancelled;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);
        var rows = new List<TigerOrder>();
        string? pageToken = null;

        for (var page = 0; page < _options.MaxOrderPages; page++)
        {
            var biz = TigerBiz.New()
                .Add("account", account.Value.AccountId)
                .Add("start_date", Milliseconds(query.From, startOfDay: true))
                .Add("end_date", Milliseconds(query.To, startOfDay: false))
                .Add("limit", (long)_options.HistoryPageSize)
                .Add("page_token", pageToken)
                .Add("lang", _options.Language);

            var response = await api
                .CallAsync<TigerPage<TigerOrder>>(TigerMaps.MethodOrders, biz, account.Value.Credentials, isTradeWrite: false, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyList<BrokerOrder>>.Failure(response.Error);
            }

            rows.AddRange(response.Value.Items ?? []);
            pageToken = response.Value.NextPageToken;

            if (string.IsNullOrWhiteSpace(pageToken) || (response.Value.Items?.Count ?? 0) == 0)
            {
                break;
            }
        }

        var mapped = await MapOrdersAsync(api, account.Value, rows, ct).ConfigureAwait(false);
        if (mapped.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(mapped.Error);
        }

        return Result<IReadOnlyList<BrokerOrder>>.Success(
        [
            .. mapped.Value
                .Where(o => query.Instrument is not { } wanted || TigerInstrumentResolver.Matches(wanted, o.Instrument))
                .Where(o => !query.OpenOnly || o.Status.IsWorking())
                .Where(o => InWindow(o.PlacedAt, o.Instrument, query))
                .OrderByDescending(o => o.PlacedAt),
        ]);
    }

    /// <inheritdoc />
    public async Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<BrokerOrder>.Failure(account.Error);
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<BrokerOrder>("A broker order id is required.");
        }

        var api = _channel.Trading(account.Value.Credentials);
        var read = await ReadOrderAsync(api, account.Value, brokerOrderId.Trim(), ct).ConfigureAwait(false);

        return read.IsFailure ? Result<BrokerOrder>.Failure(read.Error) : read.Value.Order;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);
        var rows = new List<TigerTransaction>();
        string? pageToken = null;

        for (var page = 0; page < _options.MaxOrderPages; page++)
        {
            var biz = TigerBiz.New()
                .Add("account", account.Value.AccountId)
                .Add("start_date", Milliseconds(query.From, startOfDay: true))
                .Add("end_date", Milliseconds(query.To, startOfDay: false))
                .Add("limit", (long)_options.HistoryPageSize)
                .Add("page_token", pageToken)
                .Add("lang", _options.Language);

            var response = await api
                .CallAsync<TigerPage<TigerTransaction>>(TigerMaps.MethodOrderTransactions, biz, account.Value.Credentials, isTradeWrite: false, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(response.Error);
            }

            rows.AddRange(response.Value.Items ?? []);
            pageToken = response.Value.NextPageToken;

            if (string.IsNullOrWhiteSpace(pageToken) || (response.Value.Items?.Count ?? 0) == 0)
            {
                break;
            }
        }

        var trades = new List<BrokerTrade>(rows.Count);

        foreach (var row in rows)
        {
            var record = await _resolver.ForContractAsync(api, row.Contract(), account.Value.Credentials, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                if (Skippable(record.Error))
                {
                    continue;
                }

                return Result<IReadOnlyList<BrokerTrade>>.Failure(record.Error);
            }

            var mapped = TigerOrderMapper.MapTrade(row, record.Value, _clock);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            var trade = mapped.Value;
            if ((query.Instrument is { } wanted && !TigerInstrumentResolver.Matches(wanted, trade.Instrument))
                || !InWindow(trade.ExecutedAt, trade.Instrument, query))
            {
                continue;
            }

            trades.Add(trade);
        }

        return Result<IReadOnlyList<BrokerTrade>>.Success([.. trades.OrderByDescending(t => t.ExecutedAt)]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tiger has no basket method; the manifest declares the basket non-atomic. Every leg is built — which resolves
    /// its contract — before any is sent, and a leg Tiger then refuses stops the loop with the orders already placed
    /// named in the failure.
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
                $"Tiger baskets are sent one order at a time and capped at {_options.MaxBasketLegs} legs; this one has {requests.Count}.");
        }

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        for (var i = 0; i < requests.Count; i++)
        {
            var ticket = await BuildTicketAsync(api, requests[i], account.Value, ct).ConfigureAwait(false);
            if (ticket.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(ticket.Error with { Message = $"Leg {i + 1}: {ticket.Error.Message}" });
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
    /// <remarks>From Tiger's order preview: the initial margin the order would require.</remarks>
    public async Task<Result<MarginEstimate>> EstimateMarginAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(request, ct).ConfigureAwait(false);
        if (preview.IsFailure)
        {
            return Result<MarginEstimate>.Failure(preview.Error);
        }

        var (view, record) = preview.Value;

        if (TigerNumber.Decimal(view.InitialMargin) is not { } required)
        {
            return Result<MarginEstimate>.Failure(TigerErrors.MissingField(TigerMaps.MethodPreviewOrder, "initMargin"));
        }

        var currency = TigerMaps.ToCanonicalCurrency(view.MarginCurrency ?? view.Currency) is { IsSuccess: true } stated
            ? stated.Value
            : record.Definition.Currency;

        return new MarginEstimate
        {
            Required = new Money(required, currency),
            Available = TigerNumber.Decimal(view.EquityWithLoan) is { } equity ? new Money(equity, currency) : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>Tiger's preview states its commission; exchange and regulatory fees may be charged on top.</remarks>
    public async Task<Result<ChargesEstimate>> EstimateChargesAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(request, ct).ConfigureAwait(false);
        if (preview.IsFailure)
        {
            return Result<ChargesEstimate>.Failure(preview.Error);
        }

        var (view, record) = preview.Value;

        if (TigerNumber.Decimal(view.Commission) is not { } commission)
        {
            return Result<ChargesEstimate>.Failure(TigerErrors.MissingField(TigerMaps.MethodPreviewOrder, "commission"));
        }

        var currency = TigerMaps.ToCanonicalCurrency(view.CommissionCurrency ?? view.Currency) is { IsSuccess: true } stated
            ? stated.Value
            : record.Definition.Currency;

        var money = new Money(commission, currency);

        return new ChargesEstimate
        {
            Lines = [new ChargeLine("Commission", money, "Tiger's order preview; exchange and regulatory fees may be charged on top.")],
            Total = money,
        };
    }

    // --- plumbing ----------------------------------------------------------------------------------------------

    private async Task<Result<(TigerBiz Biz, TigerInstrumentRecord Record)>> BuildTicketAsync(
        TigerApi api,
        PlaceOrderRequest request,
        TigerAccount account,
        CancellationToken ct)
    {
        var quantity = request.Quantity.Value;
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<(TigerBiz, TigerInstrumentRecord)>("Tiger takes positive whole numbers of shares and contracts.");
        }

        if (request.Variety != OrderVariety.Regular)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(
                ConnectorErrors.NotSupported($"the {request.Variety} order variety through Tiger"));
        }

        if (request.DisclosedQuantity is not null)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(ConnectorErrors.NotSupported("disclosed quantity through Tiger"));
        }

        var effect = TigerMaps.ValidatePositionEffect(request.PositionEffect);
        if (effect.IsFailure)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(effect.Error);
        }

        var orderType = TigerMaps.ToNativeOrderType(request.OrderType);
        if (orderType.IsFailure)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(orderType.Error);
        }

        var timeInForce = TigerMaps.ToNativeTimeInForce(request.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(timeInForce.Error);
        }

        if (request.TimeInForce == TimeInForce.Gtd && request.GoodTillDate is null)
        {
            return Invalid<(TigerBiz, TigerInstrumentRecord)>("A GTD order needs a good-till date.");
        }

        var resolved = await _resolver.ForKeyAsync(api, request.Instrument, account.Credentials, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(resolved.Error);
        }

        var record = resolved.Value;
        if (!TigerInstrumentResolver.Matches(request.Instrument, record.Definition.Key))
        {
            return Result<(TigerBiz, TigerInstrumentRecord)>.Failure(ConnectorErrors.InstrumentNotFound(request.Instrument));
        }

        var currency = record.Definition.Currency;
        decimal? limit = null;
        decimal? trigger = null;

        if (TigerMaps.UsesLimitPrice(request.OrderType))
        {
            if (request.LimitPrice is not { } price || price.Amount <= 0m)
            {
                return Invalid<(TigerBiz, TigerInstrumentRecord)>($"A {request.OrderType} order needs a positive limit price.");
            }

            if (price.Currency != currency)
            {
                return Invalid<(TigerBiz, TigerInstrumentRecord)>(
                    $"{record.Definition.Key} is priced in {currency}; the limit price is in {price.Currency}.");
            }

            limit = price.Amount;
        }

        if (TigerMaps.UsesTriggerPrice(request.OrderType))
        {
            if (request.TriggerPrice is not { } price || price.Amount <= 0m)
            {
                return Invalid<(TigerBiz, TigerInstrumentRecord)>($"A {request.OrderType} order needs a positive trigger price.");
            }

            if (price.Currency != currency)
            {
                return Invalid<(TigerBiz, TigerInstrumentRecord)>(
                    $"{record.Definition.Key} is priced in {currency}; the trigger price is in {price.Currency}.");
            }

            trigger = price.Amount;
        }

        long? expireTime = null;
        if (request.TimeInForce == TimeInForce.Gtd && request.GoodTillDate is { } until)
        {
            var localMidnight = until.ToDateTime(TimeOnly.MaxValue);
            expireTime = new DateTimeOffset(localMidnight, record.Market.Zone.GetUtcOffset(localMidnight)).ToUnixTimeMilliseconds();
        }

        var biz = Contract(record)
            .Add("account", account.AccountId)
            .Add("action", TigerMaps.ToNativeSide(request.Side))
            .Add("order_type", orderType.Value)
            .Add("total_quantity", quantity)
            .Add("limit_price", limit)
            .Add("aux_price", trigger)
            .Add("time_in_force", timeInForce.Value)
            .Add("expire_time", expireTime)

            // Regular hours only: the sessions the platform's calendars model.
            .Add("outside_rth", false)
            .Add("user_mark", request.ClientOrderId.ToString("N", CultureInfo.InvariantCulture))
            .Add("lang", _options.Language);

        return Result<(TigerBiz Biz, TigerInstrumentRecord Record)>.Success((biz, record));
    }

    /// <summary>The contract fields every order method wants, from a described instrument.</summary>
    private static TigerBiz Contract(TigerInstrumentRecord record)
    {
        var key = record.Definition.Key;
        var biz = TigerBiz.New()
            .Add("symbol", record.Symbol)
            .Add("market", record.Market.Code)
            .Add("currency", record.Definition.Currency.ToString())
            .Add("sec_type", key.AssetClass == AssetClass.Option ? TigerMaps.SecurityTypeOption : TigerMaps.SecurityTypeStock);

        if (key is { AssetClass: AssetClass.Option, Expiry: { } expiry, Strike: { } strike, Right: { } right })
        {
            biz.Add("expiry", TigerTime.ExpiryStamp(expiry))
                .Add("strike", strike)
                .Add("right", right == OptionRight.Call ? "CALL" : "PUT")
                .Add("multiplier", record.Definition.Multiplier);
        }

        return biz;
    }

    private async Task<Result<(TigerOrder Row, TigerInstrumentRecord Record, BrokerOrder Order)>> ReadOrderAsync(
        TigerApi api,
        TigerAccount account,
        string orderId,
        CancellationToken ct)
    {
        var biz = TigerBiz.New()
            .Add("account", account.AccountId)
            .Add("id", orderId)
            .Add("lang", _options.Language);

        var response = await api
            .CallAsync<TigerOrder>(TigerMaps.MethodOrders, biz, account.Credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<(TigerOrder, TigerInstrumentRecord, BrokerOrder)>.Failure(response.Error);
        }

        if (string.IsNullOrWhiteSpace(response.Value.Id))
        {
            return Result<(TigerOrder, TigerInstrumentRecord, BrokerOrder)>.Failure(TigerErrors.OrderNotFound(orderId));
        }

        var record = await _resolver.ForContractAsync(api, response.Value.Contract(), account.Credentials, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<(TigerOrder, TigerInstrumentRecord, BrokerOrder)>.Failure(record.Error);
        }

        var mapped = TigerOrderMapper.MapOrder(response.Value, record.Value, _clock, lenient: true);
        return mapped.IsFailure
            ? Result<(TigerOrder, TigerInstrumentRecord, BrokerOrder)>.Failure(mapped.Error)
            : Result<(TigerOrder Row, TigerInstrumentRecord Record, BrokerOrder Order)>.Success((response.Value, record.Value, mapped.Value));
    }

    private async Task<Result> CancelOneAsync(TigerApi api, TigerAccount account, string orderId, CancellationToken ct)
    {
        var biz = TigerBiz.New()
            .Add("account", account.AccountId)
            .Add("id", orderId)
            .Add("lang", _options.Language);

        var response = await api
            .CallAsync(TigerMaps.MethodCancelOrder, biz, account.Credentials, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        return response.IsFailure ? Result.Failure(response.Error) : Result.Success();
    }

    private async Task<Result<List<BrokerOrder>>> MapOrdersAsync(
        TigerApi api,
        TigerAccount account,
        List<TigerOrder> rows,
        CancellationToken ct)
    {
        var orders = new List<BrokerOrder>(rows.Count);

        foreach (var row in rows)
        {
            var record = await _resolver.ForContractAsync(api, row.Contract(), account.Credentials, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                if (Skippable(record.Error))
                {
                    // A future, a warrant or a market this connector does not describe: left out of the book, not fatal.
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "{ConnectorId}: order {OrderId} was left out of the book: {Error}",
                            TigerAuth.ConnectorId,
                            row.Id,
                            record.Error.ToString());
                    }

                    continue;
                }

                return Result<List<BrokerOrder>>.Failure(record.Error);
            }

            var mapped = TigerOrderMapper.MapOrder(row, record.Value, _clock, lenient: true);
            if (mapped.IsFailure)
            {
                return Result<List<BrokerOrder>>.Failure(mapped.Error);
            }

            orders.Add(mapped.Value);
        }

        return orders;
    }

    private async Task<Result<(TigerPreview Preview, TigerInstrumentRecord Record)>> PreviewAsync(
        PlaceOrderRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<(TigerPreview, TigerInstrumentRecord)>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        var ticket = await BuildTicketAsync(api, request, account.Value, ct).ConfigureAwait(false);
        if (ticket.IsFailure)
        {
            return Result<(TigerPreview, TigerInstrumentRecord)>.Failure(ticket.Error);
        }

        var preview = await api
            .CallAsync<TigerPreview>(TigerMaps.MethodPreviewOrder, ticket.Value.Biz, account.Value.Credentials, isTradeWrite: true, ct)
            .ConfigureAwait(false);

        if (preview.IsFailure)
        {
            return Result<(TigerPreview, TigerInstrumentRecord)>.Failure(preview.Error);
        }

        if (preview.Value.WarningText is { Length: > 0 } warning && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{ConnectorId}: Tiger's order preview warns: {Warning}", TigerAuth.ConnectorId, warning);
        }

        return Result<(TigerPreview Preview, TigerInstrumentRecord Record)>.Success((preview.Value, ticket.Value.Record));
    }

    /// <summary>A row this connector cannot describe because the instrument is out of scope, rather than because something broke.</summary>
    private static bool Skippable(Error error) => error.Code == ConnectorErrorCodes.NotSupported;

    private bool InWindow(DateTimeOffset instant, InstrumentKey instrument, OrderQuery query)
    {
        if (query.From is null && query.To is null)
        {
            return true;
        }

        var zone = _cache.TryGetByKey(instrument, out var record) ? record.Market.Zone : TigerTime.NewYork;
        var date = TigerTime.MarketDate(instant, zone);
        return (query.From is not { } from || date >= from) && (query.To is not { } to || date <= to);
    }

    /// <summary>A calendar date as the epoch milliseconds Tiger's windows take.</summary>
    private static long? Milliseconds(DateOnly? date, bool startOfDay) =>
        date is { } value
            ? new DateTimeOffset(value.ToDateTime(startOfDay ? TimeOnly.MinValue : TimeOnly.MaxValue), TimeSpan.Zero).ToUnixTimeMilliseconds()
            : null;

    private static Result<T> Invalid<T>(string message) => Result<T>.Failure(TigerErrors.InvalidRequest(message));
}
