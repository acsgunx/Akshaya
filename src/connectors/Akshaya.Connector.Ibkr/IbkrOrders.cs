using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Orders, fills and order previews through the Client Portal Gateway.
///
/// IDEMPOTENCY IS THE BROKER'S. The ClientOrderId travels as <c>cOID</c>, which IBKR requires to be unique for 24
/// hours: a resubmission after a timeout is refused rather than doubled, and the id comes back on every order and
/// execution row as <c>order_ref</c>.
///
/// QUESTIONS ARE NOT ANSWERED SILENTLY. See <see cref="IbkrApi.WalkOrderAsync"/>.
///
/// A MODIFY RESTATES THE ORDER. IBKR requires every attribute of the original ticket on a modification, so the order
/// is read from the live list and sent back with only the changed values different.
///
/// THE BOOK IS THIS BROKERAGE SESSION'S. The Client Portal API lists orders working now or finished since the gateway
/// signed in; there is no route for older orders. Executions reach back at most seven days.
/// </summary>
public sealed class IbkrOrders : IConnectorOrders
{
    private readonly IbkrChannel _channel;
    private readonly IbkrOptions _options;
    private readonly IbkrInstrumentCache _cache;
    private readonly IbkrInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    internal IbkrOrders(
        IbkrChannel channel,
        IbkrOptions options,
        IbkrInstrumentCache cache,
        IbkrInstrumentResolver resolver,
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

        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        var record = await RecordForAsync(request.Instrument, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<OrderAck>.Failure(record.Error);
        }

        var ticket = BuildTicket(request, record.Value, account.Value.AccountId);
        if (ticket.IsFailure)
        {
            return Result<OrderAck>.Failure(ticket.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<OrderAck>.Failure(api.Error);
        }

        var ack = await api.Value
            .WalkOrderAsync(OrdersPath(account.Value.AccountId), new IbkrOrdersRequest { Orders = [ticket.Value] }, ct)
            .ConfigureAwait(false);

        if (ack.IsFailure)
        {
            return Result<OrderAck>.Failure(ack.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = ack.Value.OrderId!,
            Status = AckStatus(ack.Value.OrderStatus),
            ClientOrderId = request.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = IbkrAccount.Require(_requireSession);
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
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported("disclosed quantity through IBKR's Client Portal API"));
        }

        if (request.Quantity is null && request.LimitPrice is null && request.TriggerPrice is null)
        {
            return Invalid<OrderAck>("A modify must change the quantity, the limit price or the trigger price.");
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<OrderAck>.Failure(api.Error);
        }

        var accountId = account.Value.AccountId;
        var orderId = request.BrokerOrderId?.Trim() ?? string.Empty;

        var live = await LiveOrdersAsync(api.Value, accountId, ct).ConfigureAwait(false);
        if (live.IsFailure)
        {
            return Result<OrderAck>.Failure(live.Error);
        }

        var row = live.Value.FirstOrDefault(o => string.Equals(o.OrderId?.Trim(), orderId, StringComparison.Ordinal));
        if (row is null)
        {
            return Result<OrderAck>.Failure(IbkrErrors.OrderNotFound(orderId));
        }

        var ensured = await _resolver.EnsureConidsAsync(_channel, [IbkrNumber.Integer(row.Conid) ?? 0], ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<OrderAck>.Failure(ensured.Error);
        }

        var mapped = IbkrOrderMapper.MapLiveOrder(row, _cache, _clock);
        if (mapped.IsFailure)
        {
            return Result<OrderAck>.Failure(mapped.Error);
        }

        var order = mapped.Value;
        if (order.Status.IsTerminal())
        {
            return Result<OrderAck>.Failure(new Error(
                ConnectorErrorCodes.OrderRejected,
                $"IBKR order {orderId} is already {order.Status} and cannot be modified."));
        }

        var nativeType = IbkrMaps.ToNativeOrderType(order.OrderType);
        if (nativeType.IsFailure)
        {
            return Result<OrderAck>.Failure(nativeType.Error);
        }

        var nativeTif = IbkrMaps.ToNativeTimeInForce(order.TimeInForce);
        if (nativeTif.IsFailure)
        {
            return Result<OrderAck>.Failure(nativeTif.Error);
        }

        var quantity = request.Quantity?.Value ?? order.Quantity.Value;
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<OrderAck>("An IBKR order quantity must be a positive whole number.");
        }

        if (request.LimitPrice is not null && !IbkrMaps.UsesLimitPrice(order.OrderType))
        {
            return Invalid<OrderAck>($"A {order.OrderType} order has no limit price to change.");
        }

        if (request.TriggerPrice is not null && !IbkrMaps.UsesTriggerPrice(order.OrderType))
        {
            return Invalid<OrderAck>($"A {order.OrderType} order has no trigger price to change.");
        }

        var currency = _cache.TryGetByKey(order.Instrument, out var record) ? record.Definition.Currency : order.LimitPrice?.Currency ?? Currency.Usd;
        var limit = request.LimitPrice ?? order.LimitPrice;
        var trigger = request.TriggerPrice ?? order.TriggerPrice;

        if ((limit is { } l && (l.Amount <= 0m || l.Currency != currency)) || (trigger is { } t && (t.Amount <= 0m || t.Currency != currency)))
        {
            return Invalid<OrderAck>($"Prices for this order must be positive and in {currency}.");
        }

        var ticket = new IbkrOrderTicket
        {
            AccountId = accountId,
            Conid = IbkrNumber.Integer(row.Conid) ?? 0,
            ClientOrderId = string.IsNullOrWhiteSpace(row.OrderRef) ? null : row.OrderRef.Trim(),
            OrderType = nativeType.Value,
            Price = order.OrderType == OrderType.Stop ? trigger?.Amount : limit?.Amount,
            AuxPrice = order.OrderType == OrderType.StopLimit ? trigger?.Amount : null,
            Side = IbkrMaps.ToNativeSide(order.Side),
            TimeInForce = nativeTif.Value,
            Quantity = quantity,
        };

        var ack = await api.Value
            .WalkOrderAsync($"iserver/account/{Uri.EscapeDataString(accountId)}/order/{Uri.EscapeDataString(orderId)}", ticket, ct)
            .ConfigureAwait(false);

        if (ack.IsFailure)
        {
            return Result<OrderAck>.Failure(ack.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = orderId,
            Status = string.IsNullOrWhiteSpace(ack.Value.OrderStatus) ? order.Status : AckStatus(ack.Value.OrderStatus),
            ClientOrderId = order.ClientOrderId,
            Message = "Modification accepted; the order book shows the amended order once IBKR confirms it.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<OrderAck>("A broker order id is required to cancel an order.");
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<OrderAck>.Failure(api.Error);
        }

        var accountId = account.Value.AccountId;
        var id = brokerOrderId.Trim();

        var cancelled = await CancelOneAsync(api.Value, accountId, id, ct).ConfigureAwait(false);
        if (cancelled.IsFailure)
        {
            return Result<OrderAck>.Failure(cancelled.Error);
        }

        // IBKR's answer says the request was received, not that the order is cancelled. Read it back.
        var live = await LiveOrdersAsync(api.Value, accountId, ct).ConfigureAwait(false);
        var row = live.IsSuccess ? live.Value.FirstOrDefault(o => string.Equals(o.OrderId?.Trim(), id, StringComparison.Ordinal)) : null;
        var status = row is null ? OrderStatus.Open : IbkrMaps.ToCanonicalOrderStatusOrUnknown(row.Status, out _);

        return new OrderAck
        {
            BrokerOrderId = id,
            Status = status,
            ClientOrderId = IbkrOrderMapper.ParseClientOrderId(row?.OrderRef),
            Message = status == OrderStatus.Cancelled ? null : "Cancellation requested; IBKR has not confirmed it yet.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>There is no cancel-all route; this cancels each working order in the account's live list.</remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<int>.Failure(account.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<int>.Failure(api.Error);
        }

        var live = await LiveOrdersAsync(api.Value, account.Value.AccountId, ct).ConfigureAwait(false);
        if (live.IsFailure)
        {
            return Result<int>.Failure(live.Error);
        }

        var working = live.Value
            .Where(o => !string.IsNullOrWhiteSpace(o.OrderId) && IbkrMaps.ToCanonicalOrderStatusOrUnknown(o.Status, out _).IsWorking())
            .Select(o => o.OrderId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var cancelled = 0;
        Error? firstFailure = null;

        foreach (var id in working)
        {
            var result = await CancelOneAsync(api.Value, account.Value.AccountId, id, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                cancelled++;
                continue;
            }

            firstFailure ??= result.Error;
            _logger.LogWarning(
                "{ConnectorId}: cancel-all could not cancel order {OrderId}: {Error}",
                IbkrAuth.ConnectorId,
                id,
                result.Error.ToString());
        }

        return cancelled == 0 && firstFailure is { } failure ? Result<int>.Failure(failure) : cancelled;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(account.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(api.Error);
        }

        var live = await LiveOrdersAsync(api.Value, account.Value.AccountId, ct).ConfigureAwait(false);
        if (live.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(live.Error);
        }

        var mapped = await MapLiveAsync(live.Value, ct).ConfigureAwait(false);
        if (mapped.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(mapped.Error);
        }

        return Result<IReadOnlyList<BrokerOrder>>.Success(
        [
            .. mapped.Value
                .Where(o => query.Instrument is not { } wanted || IbkrInstrumentResolver.Matches(wanted, o.Instrument))
                .Where(o => !query.OpenOnly || o.Status.IsWorking())
                .Where(o => InWindow(o.PlacedAt, o.Instrument, query))
                .OrderByDescending(o => o.PlacedAt),
        ]);
    }

    /// <inheritdoc />
    public async Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<BrokerOrder>.Failure(account.Error);
        }

        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Invalid<BrokerOrder>("A broker order id is required.");
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<BrokerOrder>.Failure(api.Error);
        }

        var id = brokerOrderId.Trim();

        var live = await LiveOrdersAsync(api.Value, account.Value.AccountId, ct).ConfigureAwait(false);
        if (live.IsSuccess && live.Value.FirstOrDefault(o => string.Equals(o.OrderId?.Trim(), id, StringComparison.Ordinal)) is { } row)
        {
            var ensured = await _resolver.EnsureConidsAsync(_channel, [IbkrNumber.Integer(row.Conid) ?? 0], ct).ConfigureAwait(false);
            return ensured.IsFailure
                ? Result<BrokerOrder>.Failure(ensured.Error)
                : IbkrOrderMapper.MapLiveOrder(row, _cache, _clock);
        }

        var status = await api.Value
            .GetAsync<IbkrOrderStatus>($"iserver/account/order/status/{Uri.EscapeDataString(id)}", ct)
            .ConfigureAwait(false);

        if (status.IsFailure)
        {
            return Result<BrokerOrder>.Failure(status.Error);
        }

        if (!string.IsNullOrWhiteSpace(status.Value.Error) || string.IsNullOrWhiteSpace(status.Value.OrderId))
        {
            return Result<BrokerOrder>.Failure(IbkrErrors.OrderNotFound(id));
        }

        var described = await _resolver.EnsureConidsAsync(_channel, [IbkrNumber.Integer(status.Value.Conid) ?? 0], ct).ConfigureAwait(false);
        return described.IsFailure
            ? Result<BrokerOrder>.Failure(described.Error)
            : IbkrOrderMapper.MapOrderStatus(status.Value, _cache, _clock);
    }

    /// <inheritdoc />
    /// <remarks>
    /// IBKR paces the trades route at one request every five seconds and returns at most seven days; a query that
    /// starts earlier is answered with what those seven days hold.
    /// </remarks>
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(account.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(api.Error);
        }

        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        string? days = query.From is { } from
            ? Math.Clamp(today.DayNumber - from.DayNumber + 1, 1, Math.Max(1, _options.MaxTradeDays)).ToString(CultureInfo.InvariantCulture)
            : null;

        var response = await api.Value
            .GetAsync<List<IbkrTrade>>(HttpConnectorPath.WithQuery("iserver/account/trades", ("days", days)), ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(response.Error);
        }

        var rows = response.Value
            .Where(t => string.IsNullOrWhiteSpace(t.Account) || string.Equals(t.Account.Trim(), account.Value.AccountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ensured = await _resolver.EnsureConidsAsync(_channel, rows.Select(r => IbkrNumber.Integer(r.Conid) ?? 0), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(ensured.Error);
        }

        var trades = new List<BrokerTrade>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (IbkrNumber.Integer(row.Conid) is { } conid && _cache.IsOutOfScope(conid))
            {
                continue;
            }

            var mapped = IbkrOrderMapper.MapTrade(row, _cache, _clock);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            var trade = mapped.Value;
            if (!seen.Add(trade.TradeId)
                || (query.Instrument is { } wanted && !IbkrInstrumentResolver.Matches(wanted, trade.Instrument))
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
    /// No basket route is used; the manifest declares the basket non-atomic. Every leg's contract is resolved and its
    /// ticket validated before any is sent, and a leg IBKR then refuses stops the loop with the orders already placed
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
                $"IBKR baskets are sent one order at a time and capped at {_options.MaxBasketLegs} legs; this one has {requests.Count}.");
        }

        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(account.Error);
        }

        for (var i = 0; i < requests.Count; i++)
        {
            var record = await RecordForAsync(requests[i].Instrument, ct).ConfigureAwait(false);
            var ticket = record.IsSuccess ? BuildTicket(requests[i], record.Value, account.Value.AccountId) : Result<IbkrOrderTicket>.Failure(record.Error);

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
    /// <remarks>
    /// From IBKR's order preview. Figures are in the account's base currency: the initial margin the order adds, and
    /// the funds available before it (equity with loan value less current initial margin).
    /// </remarks>
    public async Task<Result<MarginEstimate>> EstimateMarginAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(request, ct).ConfigureAwait(false);
        if (preview.IsFailure)
        {
            return Result<MarginEstimate>.Failure(preview.Error);
        }

        var baseCurrency = await _channel.BaseCurrencyAsync(preview.Value.AccountId, ct).ConfigureAwait(false);
        var currency = baseCurrency.IsSuccess ? IbkrMaps.ToCanonicalCurrency(baseCurrency.Value) : Result<Currency>.Failure(baseCurrency.Error);
        if (currency.IsFailure)
        {
            return Result<MarginEstimate>.Failure(currency.Error);
        }

        var whatIf = preview.Value.WhatIf;
        if (IbkrNumber.Leading(whatIf.Initial?.Change) is not { } required)
        {
            return Result<MarginEstimate>.Failure(IbkrErrors.MissingField("orders/whatif", "initial.change"));
        }

        var available = IbkrNumber.Leading(whatIf.Equity?.Current) is { } equity && IbkrNumber.Leading(whatIf.Initial?.Current) is { } initial
            ? equity - initial
            : (decimal?)null;

        return new MarginEstimate
        {
            Required = new Money(required, currency.Value),
            Available = available is { } funds ? new Money(funds, currency.Value) : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>IBKR's preview states its commission only; exchange, regulatory and clearing fees may be charged on top.</remarks>
    public async Task<Result<ChargesEstimate>> EstimateChargesAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(request, ct).ConfigureAwait(false);
        if (preview.IsFailure)
        {
            return Result<ChargesEstimate>.Failure(preview.Error);
        }

        var commissionText = preview.Value.WhatIf.Amount?.Commission;
        if (IbkrNumber.Leading(commissionText) is not { } commission)
        {
            return Result<ChargesEstimate>.Failure(IbkrErrors.MissingField("orders/whatif", "amount.commission"));
        }

        var currency = IbkrMaps.ToCanonicalCurrency(IbkrNumber.CurrencyCode(commissionText)) is { IsSuccess: true } stated
            ? stated.Value
            : preview.Value.Record.Definition.Currency;

        var money = new Money(commission, currency);

        return new ChargesEstimate
        {
            Lines = [new ChargeLine("Commission", money, "IBKR's order preview; exchange, regulatory and clearing fees may be charged on top.")],
            Total = money,
        };
    }

    /// <summary>A canonical placement to an IBKR ticket. Structural once the contract is resolved.</summary>
    internal static Result<IbkrOrderTicket> BuildTicket(PlaceOrderRequest request, IbkrInstrumentRecord record, string accountId)
    {
        var quantity = request.Quantity.Value;
        if (quantity <= 0m || quantity != decimal.Truncate(quantity))
        {
            return Invalid<IbkrOrderTicket>("IBKR orders through this connector take positive whole numbers of shares and contracts.");
        }

        if (request.Variety != OrderVariety.Regular)
        {
            return Result<IbkrOrderTicket>.Failure(ConnectorErrors.NotSupported($"the {request.Variety} order variety through this connector"));
        }

        if (request.DisclosedQuantity is not null)
        {
            return Result<IbkrOrderTicket>.Failure(ConnectorErrors.NotSupported("disclosed quantity through IBKR's Client Portal API"));
        }

        var effect = IbkrMaps.ValidatePositionEffect(request.PositionEffect);
        if (effect.IsFailure)
        {
            return Result<IbkrOrderTicket>.Failure(effect.Error);
        }

        var orderType = IbkrMaps.ToNativeOrderType(request.OrderType);
        if (orderType.IsFailure)
        {
            return Result<IbkrOrderTicket>.Failure(orderType.Error);
        }

        var timeInForce = IbkrMaps.ToNativeTimeInForce(request.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<IbkrOrderTicket>.Failure(timeInForce.Error);
        }

        var currency = record.Definition.Currency;
        decimal? limit = null;
        decimal? trigger = null;

        if (IbkrMaps.UsesLimitPrice(request.OrderType))
        {
            if (request.LimitPrice is not { } price || price.Amount <= 0m)
            {
                return Invalid<IbkrOrderTicket>($"A {request.OrderType} order needs a positive limit price.");
            }

            if (price.Currency != currency)
            {
                return Invalid<IbkrOrderTicket>($"{record.Definition.Key} is priced in {currency}; the limit price is in {price.Currency}.");
            }

            limit = price.Amount;
        }

        if (IbkrMaps.UsesTriggerPrice(request.OrderType))
        {
            if (request.TriggerPrice is not { } price || price.Amount <= 0m)
            {
                return Invalid<IbkrOrderTicket>($"A {request.OrderType} order needs a positive trigger price.");
            }

            if (price.Currency != currency)
            {
                return Invalid<IbkrOrderTicket>($"{record.Definition.Key} is priced in {currency}; the trigger price is in {price.Currency}.");
            }

            trigger = price.Amount;
        }

        return new IbkrOrderTicket
        {
            AccountId = accountId,
            Conid = record.Conid,
            ClientOrderId = request.ClientOrderId.ToString("N", CultureInfo.InvariantCulture),
            OrderType = orderType.Value,

            // STP carries its stop in price; STOP_LIMIT carries its limit in price and its stop in auxPrice.
            Price = request.OrderType == OrderType.Stop ? trigger : limit,
            AuxPrice = request.OrderType == OrderType.StopLimit ? trigger : null,
            Side = IbkrMaps.ToNativeSide(request.Side),
            TimeInForce = timeInForce.Value,
            Quantity = quantity,

            // Regular hours only: the sessions the platform's calendars model.
            OutsideRegularHours = false,
        };
    }

    private async Task<Result<Preview>> PreviewAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<Preview>.Failure(account.Error);
        }

        var record = await RecordForAsync(request.Instrument, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<Preview>.Failure(record.Error);
        }

        var ticket = BuildTicket(request, record.Value, account.Value.AccountId);
        if (ticket.IsFailure)
        {
            return Result<Preview>.Failure(ticket.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<Preview>.Failure(api.Error);
        }

        var path = $"iserver/account/{Uri.EscapeDataString(account.Value.AccountId)}/orders/whatif";
        var response = await api.Value
            .PostAsync<IbkrWhatIf>(path, new IbkrOrdersRequest { Orders = [ticket.Value] }, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<Preview>.Failure(response.Error);
        }

        if (!string.IsNullOrWhiteSpace(response.Value.Error))
        {
            return Result<Preview>.Failure(_channel.Errors.MapOrderText(response.Value.Error, path));
        }

        return new Preview(response.Value, record.Value, account.Value.AccountId);
    }

    private async Task<Result<IbkrInstrumentRecord>> RecordForAsync(InstrumentKey key, CancellationToken ct)
    {
        var record = await _resolver.ForKeyAsync(_channel, key, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return record;
        }

        return IbkrInstrumentResolver.Matches(key, record.Value.Definition.Key)
            ? record
            : Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    private async Task<Result> CancelOneAsync(IbkrApi api, string accountId, string orderId, CancellationToken ct)
    {
        var path = $"iserver/account/{Uri.EscapeDataString(accountId)}/order/{Uri.EscapeDataString(orderId)}";
        var response = await api.DeleteAsync<IbkrCancelResponse>(path, ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure(response.Error);
        }

        return string.IsNullOrWhiteSpace(response.Value.Error)
            ? Result.Success()
            : Result.Failure(_channel.Errors.MapOrderText(response.Value.Error, path));
    }

    /// <summary>The account's live orders. A brokerage session's first read can come back before IBKR has built its order cache, so an answer with no list at all is read once more.</summary>
    private static async Task<Result<List<IbkrLiveOrder>>> LiveOrdersAsync(IbkrApi api, string accountId, CancellationToken ct)
    {
        var response = await api.GetAsync<IbkrLiveOrders>("iserver/account/orders", ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<List<IbkrLiveOrder>>.Failure(response.Error);
        }

        var orders = response.Value.Orders;
        if (orders is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);

            var again = await api.GetAsync<IbkrLiveOrders>("iserver/account/orders", ct).ConfigureAwait(false);
            if (again.IsFailure)
            {
                return Result<List<IbkrLiveOrder>>.Failure(again.Error);
            }

            orders = again.Value.Orders;
        }

        return Result<List<IbkrLiveOrder>>.Success(
        [
            .. (orders ?? []).Where(o => string.IsNullOrWhiteSpace(o.Account)
                                          || string.Equals(o.Account.Trim(), accountId, StringComparison.OrdinalIgnoreCase)),
        ]);
    }

    private async Task<Result<List<BrokerOrder>>> MapLiveAsync(List<IbkrLiveOrder> rows, CancellationToken ct)
    {
        var ensured = await _resolver.EnsureConidsAsync(_channel, rows.Select(r => IbkrNumber.Integer(r.Conid) ?? 0), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<List<BrokerOrder>>.Failure(ensured.Error);
        }

        var orders = new List<BrokerOrder>(rows.Count);

        foreach (var row in rows)
        {
            if (IbkrNumber.Integer(row.Conid) is { } conid && _cache.IsOutOfScope(conid))
            {
                continue;
            }

            var mapped = IbkrOrderMapper.MapLiveOrder(row, _cache, _clock);
            if (mapped.IsSuccess)
            {
                orders.Add(mapped.Value);
                continue;
            }

            if (mapped.Error.Code == ConnectorErrorCodes.Unknown)
            {
                // An order described in terms this table does not map — a type placed from Trader Workstation — is
                // logged and left out, rather than failing every order in the book.
                _logger.LogWarning(
                    "{ConnectorId}: order {OrderId} was left out of the book: {Error}",
                    IbkrAuth.ConnectorId,
                    row.OrderId,
                    mapped.Error.ToString());
                continue;
            }

            return Result<List<BrokerOrder>>.Failure(mapped.Error);
        }

        return orders;
    }

    private bool InWindow(DateTimeOffset instant, InstrumentKey instrument, OrderQuery query)
    {
        if (query.From is null && query.To is null)
        {
            return true;
        }

        var zone = _cache.TryGetByKey(instrument, out var record) ? record.Market.Zone : IbkrTime.NewYork;
        var date = IbkrTime.MarketDate(instant, zone);
        return (query.From is not { } from || date >= from) && (query.To is not { } to || date <= to);
    }

    private static OrderStatus AckStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? OrderStatus.Submitted : IbkrMaps.ToCanonicalOrderStatusOrUnknown(status, out _);

    private static string OrdersPath(string accountId) => $"iserver/account/{Uri.EscapeDataString(accountId)}/orders";

    private static Result<T> Invalid<T>(string message) => Result<T>.Failure(IbkrErrors.InvalidRequest(message));

    private sealed record Preview(IbkrWhatIf WhatIf, IbkrInstrumentRecord Record, string AccountId);
}
