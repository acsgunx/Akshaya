using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Order placement, amendment and retrieval through OpenD's trading protocols.
///
/// Four things about OpenD shape this class.
///
/// THE CLIENT ORDER ID FITS WHOLE. OpenD carries a free-text <c>remark</c> of up to 64 bytes on every order
/// and echoes it on every row, so the full ClientOrderId travels in it as 32 hex characters and comes back
/// on the book, on fills and on pushes — no truncation and no local index, unlike Kite's 20-character tag.
///
/// EVERY TRADING CALL NAMES A MARKET. The header carries a trading market, and a universal account trades
/// two. Reads therefore run once per market the account is authorised for, and a write goes to the market
/// of its instrument.
///
/// AMEND AND CANCEL READ THE ORDER BACK. The shared contract has only the order id, while OpenD needs the
/// market for the header, and a modify re-sends quantity and price together (the SDK always sends both).
/// One extra read, spent deliberately: guessing the market sends a cancel to the wrong book, where it
/// fails, and the order the trader wanted gone keeps working.
///
/// PLACEMENT IS NOT EXECUTION. OpenD's answer means moomoo accepted the request; the book says when it
/// reaches the exchange. So every write acknowledges as Submitted.
/// </summary>
public sealed class MoomooOrders : IConnectorOrders
{
    private readonly MoomooChannel _channel;
    private readonly MoomooOptions _options;
    private readonly MoomooInstrumentCache _cache;
    private readonly MoomooInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    internal MoomooOrders(
        MoomooChannel channel,
        MoomooOptions options,
        MoomooInstrumentCache cache,
        MoomooInstrumentResolver resolver,
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

        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        var prepared = await PrepareAsync(request, account.Value, ct).ConfigureAwait(false);
        return prepared.IsFailure
            ? Result<OrderAck>.Failure(prepared.Error)
            : await SendAsync(prepared.Value, account.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        if (request.OrderType is not null || request.TimeInForce is not null || request.DisclosedQuantity is not null)
        {
            return Result<OrderAck>.Failure(ConnectorErrors.NotSupported(
                "changing an order's type, time in force or disclosed quantity. OpenD amends quantity, limit price "
                + "and trigger price only; cancel and place a new order for anything else"));
        }

        if (request.Quantity is null && request.LimitPrice is null && request.TriggerPrice is null)
        {
            return Result<OrderAck>.Failure(MoomooErrors.InvalidRequest("A modify must change at least one field."));
        }

        var found = await FindOrderAsync(account.Value, request.BrokerOrderId, ct).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Result<OrderAck>.Failure(found.Error);
        }

        var (market, order) = found.Value;

        var orderType = MoomooMaps.ToCanonicalOrderType(order.OrderType);
        if (orderType.IsFailure)
        {
            return Result<OrderAck>.Failure(orderType.Error);
        }

        if (request.LimitPrice is not null && !MoomooMaps.UsesLimitPrice(orderType.Value))
        {
            return Result<OrderAck>.Failure(MoomooErrors.InvalidRequest("This order carries no limit price to change."));
        }

        if (request.TriggerPrice is not null && !MoomooMaps.UsesTriggerPrice(orderType.Value))
        {
            return Result<OrderAck>.Failure(MoomooErrors.InvalidRequest("This order carries no trigger price to change."));
        }

        decimal quantity;
        if (request.Quantity is { } requested)
        {
            if (requested <= Quantity.Zero)
            {
                return Result<OrderAck>.Failure(MoomooErrors.InvalidRequest("Quantity must be positive."));
            }

            if (requested.IsFractional)
            {
                return Result<OrderAck>.Failure(ConnectorErrors.NotSupported("fractional quantities"));
            }

            quantity = requested.Value;
        }
        else
        {
            quantity = MoomooNumber.DecimalOrZero(order.Qty, 4);
        }

        foreach (var price in (Money?[])[request.LimitPrice, request.TriggerPrice])
        {
            if (price is { } money && money.Currency != market.Currency)
            {
                return Result<OrderAck>.Failure(MoomooErrors.InvalidRequest(
                    $"This order is in the {market.Prefix} market, which trades in {market.Currency}; the new price is in {money.Currency}."));
            }
        }

        var limit = request.LimitPrice?.Amount ?? MoomooNumber.Decimal(order.Price) ?? 0m;
        var trigger = request.TriggerPrice?.Amount ?? MoomooNumber.Decimal(order.AuxPrice);
        var header = account.Value.HeaderFor(market);

        var response = await _channel.RequestTradeWriteAsync<OpenDModifyOrderC2S, OpenDModifyOrderS2C>(
            MoomooProtoId.TrdModifyOrder,
            packet => new OpenDModifyOrderC2S
            {
                PacketId = packet,
                Header = header,
                OrderId = order.OrderId,
                ModifyOrderOp = MoomooMaps.ModifyOpNormal,

                // Quantity and price are re-sent together, the unchanged one at its current value, because
                // that is the modify the SDK sends and the one OpenD is known to accept.
                Qty = (double)quantity,
                Price = MoomooNumber.Wire(limit),
                AuxPrice = MoomooMaps.UsesTriggerPrice(orderType.Value) && trigger is { } t ? MoomooNumber.Wire(t) : null,
            },
            ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = order.OrderId.ToString(CultureInfo.InvariantCulture),
            Status = OrderStatus.Submitted,
            ClientOrderId = MoomooOrderMapper.ParseClientOrderId(order.Remark),
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OrderAck>.Failure(account.Error);
        }

        var found = await FindOrderAsync(account.Value, brokerOrderId, ct).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Result<OrderAck>.Failure(found.Error);
        }

        var (market, order) = found.Value;
        var header = account.Value.HeaderFor(market);

        var response = await _channel.RequestTradeWriteAsync<OpenDModifyOrderC2S, OpenDModifyOrderS2C>(
            MoomooProtoId.TrdModifyOrder,
            packet => new OpenDModifyOrderC2S
            {
                PacketId = packet,
                Header = header,
                OrderId = order.OrderId,
                ModifyOrderOp = MoomooMaps.ModifyOpCancel,
            },
            ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = order.OrderId.ToString(CultureInfo.InvariantCulture),

            // The order passes through Cancelling_All, where it can still fill. Calling it cancelled now is
            // how a trader ends up holding a position they believe they closed.
            Status = OrderStatus.Submitted,
            ClientOrderId = MoomooOrderMapper.ParseClientOrderId(order.Remark),
            Message = "Cancellation requested; the order book confirms it.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// OpenD has a real cancel-all — <c>Trd_ModifyOrder</c> with <c>forAll</c> — but it is per trading
    /// market, so a universal account needs one per market. The count returned is the number of working
    /// orders seen immediately before each cancel-all went out; an order that filled in between is counted
    /// but is not a position anyone is left holding by mistake.
    ///
    /// A partial failure is reported rather than swallowed: a trader flattening in a hurry must not be told
    /// they are flat when one market's cancel-all was refused.
    /// </remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<int>.Failure(account.Error);
        }

        var cancelled = 0;
        var failed = 0;
        Error? firstFailure = null;

        foreach (var market in account.Value.Markets)
        {
            var header = account.Value.HeaderFor(market);

            var book = await _channel.RequestAsync<OpenDGetOrderListC2S, OpenDOrderListS2C>(
                MoomooProtoId.TrdGetOrderList,
                new OpenDGetOrderListC2S { Header = header },
                ct).ConfigureAwait(false);

            if (book.IsFailure)
            {
                firstFailure ??= book.Error;
                failed++;
                continue;
            }

            var working = (book.Value.OrderList ?? []).Count(o =>
                MoomooMaps.ToCanonicalOrderStatusOrUnknown(o.OrderStatus, out _).IsWorking());

            if (working == 0)
            {
                continue;
            }

            var response = await _channel.RequestTradeWriteAsync<OpenDModifyOrderC2S, OpenDModifyOrderS2C>(
                MoomooProtoId.TrdModifyOrder,
                packet => new OpenDModifyOrderC2S
                {
                    PacketId = packet,
                    Header = header,
                    OrderId = 0,
                    ModifyOrderOp = MoomooMaps.ModifyOpCancel,
                    ForAll = true,
                    TrdMarket = market.TrdMarket,
                },
                ct).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                cancelled += working;
            }
            else
            {
                failed += working;
                firstFailure ??= response.Error;
            }
        }

        if (firstFailure is { } error)
        {
            return Result<int>.Failure(new Error(
                error.Code,
                $"Cancelled {cancelled.ToString(CultureInfo.InvariantCulture)} working orders; "
                + $"{failed.ToString(CultureInfo.InvariantCulture)} could not be cancelled. {error.Message}",
                error.VendorCode,
                error.VendorMessage,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cancelled"] = cancelled.ToString(CultureInfo.InvariantCulture),
                    ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
                }));
        }

        return cancelled;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(account.Error);
        }

        var rows = await ReadOrderRowsAsync(account.Value, query, ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(rows.Error);
        }

        var ensured = await _resolver.EnsureAsync(
            _channel,
            rows.Value.Select(r => Security(r.Market, r.Order.SecMarket, r.Order.TrdMarket, r.Order.Code)),
            ct).ConfigureAwait(false);

        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(ensured.Error);
        }

        var orders = new List<BrokerOrder>(rows.Value.Count);

        foreach (var (market, row) in rows.Value)
        {
            if (IsOutOfScope(market, row.SecMarket, row.TrdMarket, row.Code))
            {
                continue;
            }

            var mapped = MoomooOrderMapper.MapOrder(row, market, _cache, _clock, lenient: true);
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
        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<BrokerOrder>.Failure(account.Error);
        }

        var found = await FindOrderAsync(account.Value, brokerOrderId, ct).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Result<BrokerOrder>.Failure(found.Error);
        }

        var (market, order) = found.Value;

        var ensured = await _resolver.EnsureAsync(
            _channel,
            [Security(market, order.SecMarket, order.TrdMarket, order.Code)],
            ct).ConfigureAwait(false);

        return ensured.IsFailure
            ? Result<BrokerOrder>.Failure(ensured.Error)
            : MoomooOrderMapper.MapOrder(order, market, _cache, _clock, lenient: false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(OrderQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(account.Error);
        }

        if (account.Value.IsPaper)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(ConnectorErrors.NotSupported(
                "fills on a paper account. OpenD serves no deal list for paper trading; the order book's filled "
                + "quantities and average prices are the record"));
        }

        var rows = new List<(MoomooMarket Market, OpenDOrderFill Fill)>();
        var seen = new HashSet<ulong>();

        foreach (var market in account.Value.Markets)
        {
            var header = account.Value.HeaderFor(market);

            var today = await _channel.RequestAsync<OpenDGetOrderFillListC2S, OpenDOrderFillListS2C>(
                MoomooProtoId.TrdGetOrderFillList,
                new OpenDGetOrderFillListC2S { Header = header },
                ct).ConfigureAwait(false);

            if (today.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(today.Error);
            }

            Collect(today.Value.OrderFillList, market);

            if (HistoryWindow(query, market) is { } window)
            {
                var history = await _channel.RequestAsync<OpenDGetHistoryOrderFillListC2S, OpenDOrderFillListS2C>(
                    MoomooProtoId.TrdGetHistoryOrderFillList,
                    new OpenDGetHistoryOrderFillListC2S { Header = header, FilterConditions = window },
                    ct).ConfigureAwait(false);

                if (history.IsFailure)
                {
                    return Result<IReadOnlyList<BrokerTrade>>.Failure(history.Error);
                }

                Collect(history.Value.OrderFillList, market);
            }
        }

        var ensured = await _resolver.EnsureAsync(
            _channel,
            rows.Select(r => Security(r.Market, r.Fill.SecMarket, r.Fill.TrdMarket, r.Fill.Code)),
            ct).ConfigureAwait(false);

        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(ensured.Error);
        }

        var trades = new List<BrokerTrade>(rows.Count);

        foreach (var (market, fill) in rows)
        {
            if (IsOutOfScope(market, fill.SecMarket, fill.TrdMarket, fill.Code))
            {
                continue;
            }

            var mapped = MoomooOrderMapper.MapTrade(fill, market, _cache, _clock);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerTrade>>.Failure(mapped.Error);
            }

            if ((query.Instrument is not { } instrument || mapped.Value.Instrument == instrument)
                && WithinDates(mapped.Value.ExecutedAt, query, market))
            {
                trades.Add(mapped.Value);
            }
        }

        return Result<IReadOnlyList<BrokerTrade>>.Success(trades);

        void Collect(List<OpenDOrderFill>? fills, MoomooMarket market)
        {
            foreach (var fill in fills ?? [])
            {
                // A fill moomoo later voided is not a trade, and counting it would put phantom shares in
                // every P&L that reads from fills.
                if (fill.Status != MoomooMaps.FillStatusCancelled && seen.Add(fill.FillId))
                {
                    rows.Add((market, fill));
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// OpenD's only multi-leg order is an options combo, which the shared contract cannot express, so a
    /// basket is a loop and the manifest says <c>atomic: false</c>. Every leg is validated before any is
    /// sent, and the loop stops at the first failure: a basket is usually a hedge, and pressing on past a
    /// failed leg builds exactly the exposure the trader was avoiding.
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
            return Result<IReadOnlyList<OrderAck>>.Failure(MoomooErrors.InvalidRequest(
                $"This connector sends at most {_options.MaxBasketLegs.ToString(CultureInfo.InvariantCulture)} orders per "
                + "basket: OpenD has no basket route and allows fifteen placements per thirty seconds. Split the request."));
        }

        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<OrderAck>>.Failure(account.Error);
        }

        var prepared = new List<PreparedOrder>(requests.Count);
        foreach (var request in requests)
        {
            var leg = await PrepareAsync(request, account.Value, ct).ConfigureAwait(false);
            if (leg.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(leg.Error);
            }

            prepared.Add(leg.Value);
        }

        var acks = new List<OrderAck>(prepared.Count);

        foreach (var leg in prepared)
        {
            var ack = await SendAsync(leg, account.Value, ct).ConfigureAwait(false);
            if (ack.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(new Error(
                    ack.Error.Code,
                    $"Basket leg {(acks.Count + 1).ToString(CultureInfo.InvariantCulture)} of "
                    + $"{prepared.Count.ToString(CultureInfo.InvariantCulture)} failed and the remaining legs were not sent; "
                    + $"{acks.Count.ToString(CultureInfo.InvariantCulture)} order(s) are live and may need unwinding. "
                    + ack.Error.Message,
                    ack.Error.VendorCode,
                    ack.Error.VendorMessage,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["placedLegs"] = acks.Count.ToString(CultureInfo.InvariantCulture),
                        ["placedOrderIds"] = string.Join(',', acks.Select(a => a.BrokerOrderId)),
                    }));
            }

            acks.Add(ack.Value);
        }

        return Result<IReadOnlyList<OrderAck>>.Success(acks);
    }

    /// <inheritdoc />
    public Task<Result<MarginEstimate>> EstimateMarginAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result<MarginEstimate>.Failure(ConnectorErrors.NotSupported(
            "pre-trade margin estimates. OpenD reports the maximum quantity an account can buy and a per-contract "
            + "initial margin for derivatives, but not the margin an equity order would use")));

    /// <inheritdoc />
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result<ChargesEstimate>.Failure(ConnectorErrors.NotSupported(
            "pre-trade charges estimates. OpenD itemises an order's fees only after it has been placed")));

    // --- plumbing --------------------------------------------------------------------------------------

    private async Task<Result<PreparedOrder>> PrepareAsync(PlaceOrderRequest request, MoomooAccount account, CancellationToken ct)
    {
        var effect = MoomooMaps.ValidatePositionEffect(request.PositionEffect);
        if (effect.IsFailure)
        {
            return Result<PreparedOrder>.Failure(effect.Error);
        }

        if (request.Variety != OrderVariety.Regular)
        {
            return Result<PreparedOrder>.Failure(ConnectorErrors.NotSupported($"{request.Variety} orders"));
        }

        if (request.Quantity <= Quantity.Zero)
        {
            return Result<PreparedOrder>.Failure(MoomooErrors.InvalidRequest("Quantity must be positive."));
        }

        if (request.Quantity.IsFractional)
        {
            return Result<PreparedOrder>.Failure(ConnectorErrors.NotSupported("fractional quantities"));
        }

        if (request.Instrument.AssetClass is not (AssetClass.Equity or AssetClass.Etf or AssetClass.Option))
        {
            return Result<PreparedOrder>.Failure(ConnectorErrors.InstrumentNotFound(request.Instrument));
        }

        var orderType = MoomooMaps.ToNativeOrderType(request.OrderType);
        if (orderType.IsFailure)
        {
            return Result<PreparedOrder>.Failure(orderType.Error);
        }

        var timeInForce = MoomooMaps.ToNativeTimeInForce(request.TimeInForce);
        if (timeInForce.IsFailure)
        {
            return Result<PreparedOrder>.Failure(timeInForce.Error);
        }

        var needsLimit = MoomooMaps.UsesLimitPrice(request.OrderType);
        var needsTrigger = MoomooMaps.UsesTriggerPrice(request.OrderType);

        if (needsLimit && request.LimitPrice is null)
        {
            return Result<PreparedOrder>.Failure(MoomooErrors.InvalidRequest("A limit price is required for this order type."));
        }

        if (needsTrigger && request.TriggerPrice is null)
        {
            return Result<PreparedOrder>.Failure(MoomooErrors.InvalidRequest("A trigger price is required for this order type."));
        }

        MoomooMarket market;
        string code;

        if (request.Instrument.AssetClass == AssetClass.Option)
        {
            var record = await _resolver.ForKeyAsync(_channel, request.Instrument, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                return Result<PreparedOrder>.Failure(record.Error);
            }

            (market, code) = (record.Value.Market, record.Value.Code);
        }
        else
        {
            var cash = MoomooNative.ForCashKey(request.Instrument);
            if (cash.IsFailure)
            {
                return Result<PreparedOrder>.Failure(cash.Error);
            }

            (market, code) = cash.Value;
        }

        if (!account.Trades(market))
        {
            return Result<PreparedOrder>.Failure(ConnectorErrors.NotSupported(
                $"orders in the {market.Prefix} market on this account, which moomoo has not authorised for it"));
        }

        foreach (var price in (Money?[])[request.LimitPrice, request.TriggerPrice])
        {
            if (price is { } money && money.Currency != market.Currency)
            {
                return Result<PreparedOrder>.Failure(MoomooErrors.InvalidRequest(
                    $"{request.Instrument} trades in {market.Currency}; this order is priced in {money.Currency}."));
            }
        }

        return new PreparedOrder(
            request.ClientOrderId,
            market,
            code,
            MoomooMaps.ToNativeSide(request.Side),
            orderType.Value,
            (long)decimal.Truncate(request.Quantity.Value),
            needsLimit ? request.LimitPrice!.Value.Amount : null,
            needsTrigger ? request.TriggerPrice!.Value.Amount : null,
            timeInForce.Value);
    }

    private async Task<Result<OrderAck>> SendAsync(PreparedOrder order, MoomooAccount account, CancellationToken ct)
    {
        var header = account.HeaderFor(order.Market);

        var response = await _channel.RequestTradeWriteAsync<OpenDPlaceOrderC2S, OpenDPlaceOrderS2C>(
            MoomooProtoId.TrdPlaceOrder,
            packet => new OpenDPlaceOrderC2S
            {
                PacketId = packet,
                Header = header,
                TrdSide = order.TrdSide,
                OrderType = order.OrderType,
                Code = order.Code,
                Qty = order.Quantity,

                // The SDK always sends a price, zero for an order type that has none, and OpenD reads it only
                // for the types that use one. Sending the same shape is the conservative choice.
                Price = order.LimitPrice is { } limit ? MoomooNumber.Wire(limit) : 0d,
                SecMarket = order.Market.TrdSecMarket,
                Remark = order.ClientOrderId.ToString("N", CultureInfo.InvariantCulture),
                TimeInForce = order.TimeInForce,
                AuxPrice = order.TriggerPrice is { } trigger ? MoomooNumber.Wire(trigger) : null,
            },
            ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        if (response.Value.OrderId is not { } orderId || orderId == 0)
        {
            return Result<OrderAck>.Failure(MoomooErrors.MissingField(MoomooProtoId.TrdPlaceOrder, "orderID"));
        }

        return new OrderAck
        {
            BrokerOrderId = orderId.ToString(CultureInfo.InvariantCulture),
            Status = OrderStatus.Submitted,
            ClientOrderId = order.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <summary>Reads one order back, looking in each market the account trades.</summary>
    private async Task<Result<(MoomooMarket Market, OpenDOrder Order)>> FindOrderAsync(
        MoomooAccount account,
        string brokerOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId)
            || !ulong.TryParse(brokerOrderId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var orderId)
            || orderId == 0)
        {
            return Result<(MoomooMarket, OpenDOrder)>.Failure(MoomooErrors.OrderNotFound(brokerOrderId ?? string.Empty));
        }

        foreach (var market in account.Markets)
        {
            var response = await _channel.RequestAsync<OpenDGetOrderListC2S, OpenDOrderListS2C>(
                MoomooProtoId.TrdGetOrderList,
                new OpenDGetOrderListC2S
                {
                    Header = account.HeaderFor(market),
                    FilterConditions = new OpenDTrdFilterConditions { IdList = [orderId] },
                },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<(MoomooMarket, OpenDOrder)>.Failure(response.Error);
            }

            if (response.Value.OrderList?.FirstOrDefault(o => o.OrderId == orderId) is { } order)
            {
                return Result<(MoomooMarket, OpenDOrder)>.Success((market, order));
            }
        }

        return Result<(MoomooMarket, OpenDOrder)>.Failure(MoomooErrors.OrderNotFound(brokerOrderId));
    }

    /// <summary>
    /// Today's book for every market, plus history when the query reaches back before today. OpenD keeps
    /// the two apart: the order list is today's orders and still-working older ones, and the history list
    /// requires an explicit window.
    /// </summary>
    private async Task<Result<List<(MoomooMarket Market, OpenDOrder Order)>>> ReadOrderRowsAsync(
        MoomooAccount account,
        OrderQuery query,
        CancellationToken ct)
    {
        var rows = new List<(MoomooMarket Market, OpenDOrder Order)>();
        var seen = new HashSet<ulong>();

        foreach (var market in account.Markets)
        {
            var header = account.HeaderFor(market);

            var today = await _channel.RequestAsync<OpenDGetOrderListC2S, OpenDOrderListS2C>(
                MoomooProtoId.TrdGetOrderList,
                new OpenDGetOrderListC2S { Header = header },
                ct).ConfigureAwait(false);

            if (today.IsFailure)
            {
                return Result<List<(MoomooMarket Market, OpenDOrder Order)>>.Failure(today.Error);
            }

            foreach (var row in today.Value.OrderList ?? [])
            {
                if (seen.Add(row.OrderId))
                {
                    rows.Add((market, row));
                }
            }

            if (HistoryWindow(query, market) is not { } window)
            {
                continue;
            }

            var history = await _channel.RequestAsync<OpenDGetHistoryOrderListC2S, OpenDOrderListS2C>(
                MoomooProtoId.TrdGetHistoryOrderList,
                new OpenDGetHistoryOrderListC2S { Header = header, FilterConditions = window },
                ct).ConfigureAwait(false);

            if (history.IsFailure)
            {
                return Result<List<(MoomooMarket Market, OpenDOrder Order)>>.Failure(history.Error);
            }

            foreach (var row in history.Value.OrderList ?? [])
            {
                if (seen.Add(row.OrderId))
                {
                    rows.Add((market, row));
                }
            }
        }

        return Result<List<(MoomooMarket Market, OpenDOrder Order)>>.Success(rows);
    }

    /// <summary>The history window for a query, in the market's own time, or null when it does not reach before today.</summary>
    private OpenDTrdFilterConditions? HistoryWindow(OrderQuery query, MoomooMarket market)
    {
        var today = MoomooTime.MarketDate(_clock.UtcNow, market.Zone);
        if (query.From is not { } from || from >= today)
        {
            return null;
        }

        var to = query.To is { } requested && requested < today ? requested : today;

        return new OpenDTrdFilterConditions
        {
            BeginTime = $"{MoomooTime.FormatDate(from)} 00:00:00",
            EndTime = $"{MoomooTime.FormatDate(to)} 23:59:59",
        };
    }

    private static OpenDSecurity Security(MoomooMarket fallback, int? secMarket, int? trdMarket, string? code)
    {
        var market = MoomooOrderMapper.MarketOf(secMarket, trdMarket, fallback);
        return new OpenDSecurity { Market = market.QotMarket, Code = code?.Trim().ToUpperInvariant() ?? string.Empty };
    }

    /// <summary>
    /// Whether a row is for a security outside this connector's venues — an OTC name, a warrant. Such rows
    /// are skipped, as the Indian connectors skip rows for segments they do not declare; a security OpenD
    /// could not describe at all still fails the read, loudly.
    /// </summary>
    private bool IsOutOfScope(MoomooMarket fallback, int? secMarket, int? trdMarket, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var market = MoomooOrderMapper.MarketOf(secMarket, trdMarket, fallback);
        var native = MoomooNative.Qualify(market, code.Trim().ToUpperInvariant());

        if (!_cache.IsOutOfScope(native))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("{ConnectorId}: skipping a row for {Native}, outside the declared venues.", MoomooAuth.ConnectorId, native);
        }

        return true;
    }

    private static bool Matches(BrokerOrder order, OrderQuery query)
    {
        if (query.OpenOnly && !order.Status.IsWorking())
        {
            return false;
        }

        if (query.Instrument is { } instrument && order.Instrument != instrument)
        {
            return false;
        }

        return MoomooMaps.MarketForVenue(order.Instrument.Venue) is { IsSuccess: true } market
            ? WithinDates(order.PlacedAt, query, market.Value)
            : WithinDates(order.PlacedAt, query, MoomooMarket.Us);
    }

    /// <summary>Date bounds are TRADING dates in the market's zone: an order at 23:30 New York time is that day's, not the next UTC day's.</summary>
    private static bool WithinDates(DateTimeOffset instant, OrderQuery query, MoomooMarket market)
    {
        if (query.From is null && query.To is null)
        {
            return true;
        }

        var date = MoomooTime.MarketDate(instant, market.Zone);
        return (query.From is not { } from || date >= from) && (query.To is not { } to || date <= to);
    }

    /// <summary>A placement, validated and mapped, ready to send.</summary>
    private sealed record PreparedOrder(
        Guid ClientOrderId,
        MoomooMarket Market,
        string Code,
        int TrdSide,
        int OrderType,
        long Quantity,
        decimal? LimitPrice,
        decimal? TriggerPrice,
        int TimeInForce);
}
