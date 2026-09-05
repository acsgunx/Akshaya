using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Order placement, amendment and retrieval against Kite's order routes.
///
/// Four things about this broker shape the whole class.
///
/// First, Kite has NO client-order-id field. The only free text it carries is <c>tag</c>, capped
/// at twenty alphanumeric characters. The platform's <see cref="PlaceOrderRequest.ClientOrderId"/>
/// is folded into it (see <see cref="ZerodhaOrderTags"/>) and a process-local index maps it back.
/// That index is a convenience, not the system of record: the durable ClientOrderId-to-
/// BrokerOrderId mapping belongs to the order store above this layer, which persists it BEFORE
/// the placement call precisely so a timeout can be reconciled rather than retried into a
/// duplicate.
///
/// Second, MODIFY AND CANCEL NEED THE VARIETY, and the shared contract does not carry one — it
/// has only the broker order id. Kite routes them as <c>/orders/{variety}/{order_id}</c> and
/// answers a wrong variety with a flat rejection. So both read the order back first to learn what
/// it is. That is one extra round trip on the amendment path, spent deliberately: the alternative
/// is assuming <c>regular</c>, which silently fails to cancel every after-market order a trader
/// placed the previous evening — exactly the orders someone is most likely to want cancelled
/// before the open.
///
/// Third, a single-order read returns the order's STATE HISTORY, not the order. The current state
/// is the last element, and taking the first would report every order as it was when first
/// received — permanently "pending", never filled.
///
/// Fourth, Kite has no batch placement route at all. A basket is a loop, which the manifest says
/// with <c>atomic: false</c>.
/// </summary>
public sealed class ZerodhaOrders : IConnectorOrders
{
    private readonly ZerodhaApi _api;
    private readonly ZerodhaOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeZoneInfo _venueZone;

    /// <summary>
    /// Shared with the stream, which reads the same tags off Kite's order postbacks. See
    /// <see cref="ZerodhaOrderTagIndex"/> for why it is one object rather than one per facet.
    /// </summary>
    private readonly ZerodhaOrderTagIndex _tags;

    /// <summary>Creates the orders facet.</summary>
    internal ZerodhaOrders(
        ZerodhaApi api,
        ZerodhaOptions options,
        ISymbolTranslator symbols,
        IClock clock,
        ZerodhaOrderTagIndex tags,
        ILogger logger)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
        _tags = tags;
        _logger = logger;
        _venueZone = ZerodhaTime.ResolveZone(options.VenueTimeZoneId);
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var form = BuildPlaceForm(request);
        if (form.IsFailure)
        {
            return Result<OrderAck>.Failure(form.Error);
        }

        var variety = ZerodhaMaps.ToNativeVariety(request.Variety);
        if (variety.IsFailure)
        {
            return Result<OrderAck>.Failure(variety.Error);
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.PlaceOrderPathFormat,
            variety.Value);

        var response = await _api
            .PostFormAsync<KiteOrderId>(path, form.Value, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        var orderId = response.Value.OrderId;
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Result<OrderAck>.Failure(ZerodhaErrors.MissingField(path, "order_id"));
        }

        _tags.Remember(ZerodhaOrderTags.Encode(request.ClientOrderId), request.ClientOrderId);

        return new OrderAck
        {
            BrokerOrderId = orderId,

            // Kite is explicit that placement is registration with its OMS and NOT receipt at the
            // exchange: "Successful placement of an order via the API does not imply its
            // successful execution." So this is Submitted, never Open. The order book or the
            // socket says when it actually rests.
            Status = OrderStatus.Submitted,
            ClientOrderId = request.ClientOrderId,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BrokerOrderId))
        {
            return Result<OrderAck>.Failure(ZerodhaErrors.InvalidRequest("A modify needs the broker's order id."));
        }

        var existing = await FindOrderAsync(request.BrokerOrderId, ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<OrderAck>.Failure(existing.Error);
        }

        var variety = Blank(existing.Value.Variety) ?? ZerodhaMaps.VarietyRegular;
        var form = new List<KeyValuePair<string, string>>(6);

        if (request.OrderType is { } orderType)
        {
            var native = ZerodhaMaps.ToNativeOrderType(orderType);
            if (native.IsFailure)
            {
                return Result<OrderAck>.Failure(native.Error);
            }

            form.Add(new KeyValuePair<string, string>("order_type", native.Value));
        }

        if (request.TimeInForce is { } tif)
        {
            var native = ZerodhaMaps.ToNativeValidity(tif);
            if (native.IsFailure)
            {
                return Result<OrderAck>.Failure(native.Error);
            }

            form.Add(new KeyValuePair<string, string>("validity", native.Value));
        }

        if (request.Quantity is { } quantity)
        {
            if (quantity.IsFractional)
            {
                return Result<OrderAck>.Failure(ConnectorErrors.NotSupported("fractional quantities"));
            }

            form.Add(new KeyValuePair<string, string>(
                "quantity",
                ZerodhaNumber.Integer((long)decimal.Truncate(quantity.Value))));
        }

        if (request.LimitPrice is { } limit)
        {
            var currency = RequireInr(limit);
            if (currency.IsFailure)
            {
                return Result<OrderAck>.Failure(currency.Error);
            }

            form.Add(new KeyValuePair<string, string>("price", ZerodhaNumber.Price(limit.Amount)));
        }

        if (request.TriggerPrice is { } trigger)
        {
            var currency = RequireInr(trigger);
            if (currency.IsFailure)
            {
                return Result<OrderAck>.Failure(currency.Error);
            }

            form.Add(new KeyValuePair<string, string>("trigger_price", ZerodhaNumber.Price(trigger.Amount)));
        }

        if (request.DisclosedQuantity is { } disclosed)
        {
            form.Add(new KeyValuePair<string, string>(
                "disclosed_quantity",
                ZerodhaNumber.Integer((long)decimal.Truncate(disclosed.Value))));
        }

        if (form.Count == 0)
        {
            return Result<OrderAck>.Failure(ZerodhaErrors.InvalidRequest(
                "A modify must change at least one field."));
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.OrderPathFormat,
            variety,
            Uri.EscapeDataString(request.BrokerOrderId));

        var response = await _api.PutFormAsync<KiteOrderId>(path, form, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.OrderId ?? request.BrokerOrderId,
            Status = OrderStatus.Submitted,
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<OrderAck>.Failure(ZerodhaErrors.InvalidRequest("A cancel needs the broker's order id."));
        }

        var existing = await FindOrderAsync(brokerOrderId, ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<OrderAck>.Failure(existing.Error);
        }

        return await CancelWithVarietyAsync(
            brokerOrderId,
            Blank(existing.Value.Variety) ?? ZerodhaMaps.VarietyRegular,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kite has no cancel-all route, so this reads the book and cancels each working order in
    /// turn. Looping is safe here in a way that looping a PLACEMENT never is: a cancel is
    /// idempotent, and cancelling an order that has already gone is not a position.
    ///
    /// It reads the book ONCE and cancels using the variety already on each row, rather than
    /// going back through <see cref="CancelAsync"/> — which would re-read the whole book per
    /// order and, at ten requests a second, turn a twenty-order flatten into four seconds of
    /// round trips at the moment someone least wants to wait.
    ///
    /// Partial failure is reported rather than swallowed. Returning a count that quietly omitted
    /// the three cancels that failed would tell a trader trying to flatten in a hurry that they
    /// were flat when they were not.
    /// </remarks>
    public async Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var book = await _api
            .GetAsync<List<KiteOrder>>(_options.OrderBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (book.IsFailure)
        {
            return Result<int>.Failure(book.Error);
        }

        var working = new List<KiteOrder>();
        foreach (var row in book.Value)
        {
            if (string.IsNullOrWhiteSpace(row.OrderId))
            {
                continue;
            }

            var status = ZerodhaMaps.ToCanonicalOrderStatusOrUnknown(row.Status, out _);
            if (status.IsWorking())
            {
                working.Add(row);
            }
        }

        var cancelled = 0;
        var failures = 0;
        Error? firstFailure = null;

        foreach (var row in working)
        {
            ct.ThrowIfCancellationRequested();

            var result = await CancelWithVarietyAsync(
                row.OrderId!,
                Blank(row.Variety) ?? ZerodhaMaps.VarietyRegular,
                ct).ConfigureAwait(false);

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
                + $"{working.Count.ToString(CultureInfo.InvariantCulture)} working orders; "
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
            .GetAsync<List<KiteOrder>>(_options.OrderBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerOrder>>.Failure(response.Error);
        }

        var orders = new List<BrokerOrder>(response.Value.Count);

        foreach (var row in response.Value)
        {
            if (IsOutOfScope(row.Exchange))
            {
                continue;
            }

            var mapped = MapOrder(row, _options.OrderBookPath, lenient: true);
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
        var row = await FindOrderAsync(brokerOrderId, ct).ConfigureAwait(false);

        return row.IsFailure
            ? Result<BrokerOrder>.Failure(row.Error)
            : MapOrder(row.Value, _options.OrderBookPath, lenient: false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var response = await _api
            .GetAsync<List<KiteTrade>>(_options.TradeBookPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerTrade>>.Failure(response.Error);
        }

        var trades = new List<BrokerTrade>(response.Value.Count);

        foreach (var row in response.Value)
        {
            if (IsOutOfScope(row.Exchange))
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
    /// Kite has no batch placement route, so this is a sequential loop and the manifest declares
    /// <c>atomic: false</c> to say so — the UI warns that a partial fill of the basket is
    /// possible, and a trader who assumed atomicity can end up half-hedged.
    ///
    /// It stops at the first REJECTION rather than pressing on. A basket is usually a spread or a
    /// hedge, and continuing past a failed leg builds exactly the position the trader was trying
    /// to avoid. The acks already returned name what did go through, so the caller can unwind
    /// deliberately instead of discovering the imbalance later.
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
            return Result<IReadOnlyList<OrderAck>>.Failure(ZerodhaErrors.InvalidRequest(
                $"This connector sends at most {_options.MaxBasketLegs.ToString(CultureInfo.InvariantCulture)} "
                + "orders per basket, because Kite has no batch placement route and caps order placement "
                + "at ten per second. Split the request."));
        }

        // Every leg is validated BEFORE any of them is sent. A basket whose fourth leg has an
        // unsupported product would otherwise place three orders and then fail, leaving a
        // half-built spread nobody asked for.
        foreach (var request in requests)
        {
            var validation = BuildPlaceForm(request);
            if (validation.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(validation.Error);
            }
        }

        var acks = new List<OrderAck>(requests.Count);

        foreach (var request in requests)
        {
            var ack = await PlaceAsync(request, ct).ConfigureAwait(false);
            if (ack.IsFailure)
            {
                return Result<IReadOnlyList<OrderAck>>.Failure(new Error(
                    ack.Error.Code,
                    $"Basket leg {(acks.Count + 1).ToString(CultureInfo.InvariantCulture)} of "
                    + $"{requests.Count.ToString(CultureInfo.InvariantCulture)} failed and the remaining "
                    + $"legs were not sent; {acks.Count.ToString(CultureInfo.InvariantCulture)} order(s) "
                    + $"are live and may need unwinding. {ack.Error.Message}",
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
    public async Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        var margin = await GetOrderMarginAsync(request, ct).ConfigureAwait(false);
        if (margin.IsFailure)
        {
            return Result<MarginEstimate>.Failure(margin.Error);
        }

        return new MarginEstimate
        {
            Required = new Money(margin.Value.Total ?? 0m, Currency.Inr),

            // Kite's margin route says what the order COSTS, not what is available. The funds
            // route is the authority on that, and the Portfolio facet already reads it — leaving
            // this null is what stops two different numbers for the same thing appearing in the UI.
            Available = null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same call that prices the margin also itemises the charges, which is why this connector
    /// can declare <c>chargesEstimate: true</c> honestly: every number below is Kite's own, so the
    /// estimate stays correct through a stamp-duty revision or a GST change without anyone here
    /// noticing there was one.
    /// </remarks>
    public async Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        var margin = await GetOrderMarginAsync(request, ct).ConfigureAwait(false);
        if (margin.IsFailure)
        {
            return Result<ChargesEstimate>.Failure(margin.Error);
        }

        if (margin.Value.Charges is not { } charges)
        {
            return Result<ChargesEstimate>.Failure(
                ZerodhaErrors.MissingField(_options.OrderMarginPath, "charges"));
        }

        var lines = new List<ChargeLine>(7);

        void Add(string name, decimal? amount, string? note = null)
        {
            // Zero lines are dropped rather than listed. An itemised bill of nine zeroes and one
            // real number is harder to read than the one number, and every Indian charge is
            // segment-dependent — STT does not apply to an option buy, stamp duty not to a sell.
            if (amount is > 0m)
            {
                lines.Add(new ChargeLine(name, new Money(amount.Value, Currency.Inr), note));
            }
        }

        Add("Brokerage", charges.Brokerage);
        Add(
            // Equity is STT, commodities are CTT. Kite says which in transaction_tax_type, so the
            // label is the broker's rather than a guess that would be wrong on half the segments.
            charges.TransactionTaxType?.ToUpperInvariant() ?? "Transaction tax",
            charges.TransactionTax,
            "Securities transaction tax");
        Add("Exchange turnover charge", charges.ExchangeTurnoverCharge);
        Add("SEBI turnover charge", charges.SebiTurnoverCharge);
        Add("Stamp duty", charges.StampDuty);
        Add("GST", charges.Gst?.Total, "IGST, CGST and SGST combined");

        return new ChargesEstimate
        {
            Lines = lines,

            // Kite's own total, not a sum of the lines above. They should agree, and if they ever
            // do not it is because Kite added a charge this connector does not itemise yet — in
            // which case its total is right and our arithmetic would be quietly short.
            Total = new Money(charges.Total ?? 0m, Currency.Inr),
        };
    }

    // --- shared plumbing ---------------------------------------------------------------------

    private async Task<Result<KiteOrderMargin>> GetOrderMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct)
    {
        var leg = BuildMarginLeg(request);
        if (leg.IsFailure)
        {
            return Result<KiteOrderMargin>.Failure(leg.Error);
        }

        var response = await _api
            .PostJsonAsync<List<KiteOrderMargin>>(_options.OrderMarginPath, new[] { leg.Value }, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<KiteOrderMargin>.Failure(response.Error);
        }

        return response.Value.Count > 0
            ? response.Value[0]
            : Result<KiteOrderMargin>.Failure(
                ZerodhaErrors.MissingField(_options.OrderMarginPath, "data[0]"));
    }

    /// <summary>
    /// Reads one order back, resolving the state history to its CURRENT state.
    ///
    /// <c>GET /orders/:order_id</c> returns every state the order has passed through, oldest
    /// first. The last element is the current one; the first is how the order looked the instant
    /// Kite received it, which is "pending" for every order ever placed.
    /// </summary>
    private async Task<Result<KiteOrder>> FindOrderAsync(string brokerOrderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
        {
            return Result<KiteOrder>.Failure(ZerodhaErrors.InvalidRequest("An order lookup needs an order id."));
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.OrderHistoryPathFormat,
            Uri.EscapeDataString(brokerOrderId));

        var response = await _api.GetAsync<List<KiteOrder>>(path, query: null, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            // Kite answers an unknown order id with an InputException rather than a 404, which the
            // error mapper already turns into OrderNotFound. Anything else is passed through.
            return Result<KiteOrder>.Failure(response.Error);
        }

        return response.Value.Count > 0
            ? response.Value[^1]
            : Result<KiteOrder>.Failure(ZerodhaErrors.OrderNotFound(brokerOrderId));
    }

    private async Task<Result<OrderAck>> CancelWithVarietyAsync(
        string brokerOrderId,
        string variety,
        CancellationToken ct)
    {
        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.OrderPathFormat,
            variety,
            Uri.EscapeDataString(brokerOrderId));

        var response = await _api.DeleteAsync<KiteOrderId>(path, query: null, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<OrderAck>.Failure(response.Error);
        }

        return new OrderAck
        {
            BrokerOrderId = response.Value.OrderId ?? brokerOrderId,

            // Kite acknowledges the cancellation REQUEST. The order passes through
            // "CANCEL PENDING" before it is actually gone, so this is Submitted rather than
            // Cancelled — claiming it is cancelled while it can still fill is how a trader ends
            // up with a position they believe they closed.
            Status = OrderStatus.Submitted,
            Message = "Cancellation requested; Kite confirms it through the order book.",
            AcknowledgedAt = _clock.UtcNow,
        };
    }

    // --- request building ---------------------------------------------------------------------

    private Result<List<KeyValuePair<string, string>>> BuildPlaceForm(PlaceOrderRequest request)
    {
        var parts = BuildCommonOrderFields(request);
        if (parts.IsFailure)
        {
            return Result<List<KeyValuePair<string, string>>>.Failure(parts.Error);
        }

        var fields = parts.Value;

        var validity = ZerodhaMaps.ToNativeValidity(request.TimeInForce);
        if (validity.IsFailure)
        {
            return Result<List<KeyValuePair<string, string>>>.Failure(validity.Error);
        }

        var form = new List<KeyValuePair<string, string>>(12)
        {
            new("exchange", fields.Exchange),
            new("tradingsymbol", fields.TradingSymbol),
            new("transaction_type", fields.Side),
            new("order_type", fields.OrderType),
            new("product", fields.Product),
            new("quantity", ZerodhaNumber.Integer(fields.Quantity)),
            new("validity", validity.Value),

            // Kite has exactly one free-text slot and the ClientOrderId has to have it: it is the
            // only way to reconcile a timed-out placement. The trader's own Tag and the AlgoId are
            // persisted locally against this ClientOrderId instead — and SEBI's algo identification
            // runs through the platform's own records, not this field.
            new("tag", ZerodhaOrderTags.Encode(request.ClientOrderId)),
        };

        // Prices are sent ONLY when the order type uses them. Kite rejects a price on a market
        // order rather than ignoring it.
        if (fields.LimitPrice is { } limit)
        {
            form.Add(new KeyValuePair<string, string>("price", ZerodhaNumber.Price(limit)));
        }

        if (fields.TriggerPrice is { } trigger)
        {
            form.Add(new KeyValuePair<string, string>("trigger_price", ZerodhaNumber.Price(trigger)));
        }

        if (request.DisclosedQuantity is { } disclosed && disclosed > Quantity.Zero)
        {
            form.Add(new KeyValuePair<string, string>(
                "disclosed_quantity",
                ZerodhaNumber.Integer((long)decimal.Truncate(disclosed.Value))));
        }

        return form;
    }

    private Result<KiteMarginRequest> BuildMarginLeg(PlaceOrderRequest request)
    {
        var parts = BuildCommonOrderFields(request);
        if (parts.IsFailure)
        {
            return Result<KiteMarginRequest>.Failure(parts.Error);
        }

        var variety = ZerodhaMaps.ToNativeVariety(request.Variety);
        if (variety.IsFailure)
        {
            return Result<KiteMarginRequest>.Failure(variety.Error);
        }

        var fields = parts.Value;

        return new KiteMarginRequest
        {
            Exchange = fields.Exchange,
            TradingSymbol = fields.TradingSymbol,
            TransactionType = fields.Side,
            Variety = variety.Value,
            Product = fields.Product,
            OrderType = fields.OrderType,
            Quantity = fields.Quantity,
            Price = fields.LimitPrice ?? 0m,
            TriggerPrice = fields.TriggerPrice ?? 0m,
        };
    }

    /// <summary>
    /// The fields a placement and a margin estimate agree on, validated once.
    ///
    /// Sharing this is what keeps the margin quote honest: an estimate built from a different
    /// product or a different quantity than the order it is quoting is worse than no estimate,
    /// because it looks authoritative.
    /// </summary>
    private Result<OrderFields> BuildCommonOrderFields(PlaceOrderRequest request)
    {
        var native = _symbols.ToNative(request.Instrument);
        if (native.IsFailure)
        {
            return Result<OrderFields>.Failure(native.Error);
        }

        if (!ZerodhaInstrument.TrySplit(native.Value, out var exchange, out var tradingSymbol))
        {
            return Result<OrderFields>.Failure(ZerodhaErrors.MissingField("symbol translation", "exchange"));
        }

        var side = ZerodhaMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result<OrderFields>.Failure(side.Error);
        }

        var orderType = ZerodhaMaps.ToNativeOrderType(request.OrderType);
        if (orderType.IsFailure)
        {
            return Result<OrderFields>.Failure(orderType.Error);
        }

        var product = ZerodhaMaps.ToNativeProduct(request.PositionEffect);
        if (product.IsFailure)
        {
            return Result<OrderFields>.Failure(product.Error);
        }

        if (request.Quantity <= Quantity.Zero)
        {
            return Result<OrderFields>.Failure(ZerodhaErrors.InvalidRequest("Quantity must be positive."));
        }

        if (request.Quantity.IsFractional)
        {
            return Result<OrderFields>.Failure(ConnectorErrors.NotSupported(
                "fractional quantities. Indian exchanges trade whole units, and derivatives trade in "
                + "multiples of the contract lot size"));
        }

        var needsLimit = request.OrderType is OrderType.Limit or OrderType.StopLimit;
        var needsTrigger = request.OrderType is OrderType.Stop or OrderType.StopLimit;

        if (needsLimit && request.LimitPrice is null)
        {
            return Result<OrderFields>.Failure(
                ZerodhaErrors.InvalidRequest("A limit price is required for this order type."));
        }

        if (needsTrigger && request.TriggerPrice is null)
        {
            return Result<OrderFields>.Failure(
                ZerodhaErrors.InvalidRequest("A trigger price is required for this order type."));
        }

        foreach (var price in (Money?[])[request.LimitPrice, request.TriggerPrice])
        {
            if (price is { } money)
            {
                var currency = RequireInr(money);
                if (currency.IsFailure)
                {
                    return Result<OrderFields>.Failure(currency.Error);
                }
            }
        }

        return new OrderFields(
            exchange,
            tradingSymbol,
            side.Value,
            orderType.Value,
            product.Value,
            (long)decimal.Truncate(request.Quantity.Value),
            needsLimit ? request.LimitPrice!.Value.Amount : null,
            needsTrigger ? request.TriggerPrice!.Value.Amount : null);
    }

    private static Result RequireInr(Money money) =>
        money.Currency == Currency.Inr
            ? Result.Success()
            : ZerodhaErrors.InvalidRequest($"Kite settles in INR; this order is priced in {money.Currency}.");

    // --- response mapping -----------------------------------------------------------------------

    private Result<BrokerOrder> MapOrder(KiteOrder row, string route, bool lenient) =>
        ZerodhaOrderMapper.MapOrder(row, _symbols, _clock, _tags, route, lenient);

    private Result<BrokerTrade> MapTrade(KiteTrade row) =>
        ZerodhaOrderMapper.MapTrade(row, _symbols, _clock, _options.TradeBookPath);

    /// <summary>
    /// Whether a row belongs to a venue this connector does not serve.
    ///
    /// A Kite account's order book spans every segment the USER trades, and this connector
    /// declares only NSE and BSE. Someone who also trades gold on MCX or USDINR on CDS has those
    /// rows in the same response, and failing the whole book over them would blank a blotter that
    /// is otherwise entirely correct. Checked on the EXCHANGE field, before translation, so it can
    /// never swallow an in-scope instrument that merely failed to resolve — that case still fails
    /// loudly, which is what makes an un-ingested instrument master visible.
    /// </summary>
    private bool IsOutOfScope(string? exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange) || ZerodhaMaps.ToCanonicalVenue(exchange).IsSuccess)
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{ConnectorId}: skipping a row on {Exchange}; it is outside this connector's declared venues.",
                ZerodhaAuth.ConnectorId,
                exchange);
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

        var date = ZerodhaTime.VenueDate(instant, _venueZone);

        return (query.From is not { } from || date >= from)
               && (query.To is not { } to || date <= to);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The fields a placement and a margin estimate share, already validated and mapped.</summary>
    private readonly record struct OrderFields(
        string Exchange,
        string TradingSymbol,
        string Side,
        string OrderType,
        string Product,
        long Quantity,
        decimal? LimitPrice,
        decimal? TriggerPrice);
}

/// <summary>
/// Folds the platform's <c>ClientOrderId</c> into Kite's <c>tag</c>.
///
/// Kite documents the tag as alphanumeric with a maximum of twenty characters, so twenty hex
/// characters of the GUID is the largest thing that fits — eighty bits, far more than enough to be
/// unique across a trading day. The truncation is one-way, which is why the connector keeps an
/// index rather than trying to reconstruct the GUID from the tag.
///
/// This is a convenience for in-flight correlation. The authoritative mapping is written by the
/// order store BEFORE the placement call, so that a timed-out placement can be reconciled against
/// the order book instead of retried into a duplicate order.
/// </summary>
public static class ZerodhaOrderTags
{
    /// <summary>Characters of the GUID carried in the tag. Kite's documented maximum.</summary>
    public const int TagLength = 20;

    /// <summary>Builds the tag to send with an order.</summary>
    public static string Encode(Guid clientOrderId) =>
        clientOrderId.ToString("N", CultureInfo.InvariantCulture)[..TagLength];

}
