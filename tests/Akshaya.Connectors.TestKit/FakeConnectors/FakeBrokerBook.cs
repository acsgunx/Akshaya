using System.Globalization;
using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit.FakeConnectors;

/// <summary>
/// The order book, portfolio, prices and instrument master shared by the two fake connectors.
///
/// <b>Why sharing it does not weaken what the fakes prove.</b> Alpha and Beta exist to
/// demonstrate that the abstraction spans genuinely different brokers, and the axes on which
/// brokers actually differ are the ones the fakes keep separate: the authentication flow, the
/// symbology, the currency, the quantity granularity, whether there is a live feed at all,
/// whether a session can be refreshed, and the capability matrix in the manifest. What a book
/// of resting orders looks like in memory is not one of those axes, and duplicating it would
/// only create two places for a fixture bug to hide.
///
/// Everything here honours the MANIFEST rather than hard-coding a capability, so a fake that
/// declares no cancel-all actually refuses cancel-all. A fake that lied would let the
/// conformance suite pass against a connector that lies, which is the one failure mode the
/// suite exists to prevent.
/// </summary>
public sealed class FakeBrokerBook :
    IConnectorOrders, IConnectorPortfolio, IConnectorMarketData, IConnectorReference
{
    private readonly ConnectorManifest _manifest;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly Func<PlaceOrderRequest, Result> _validate;
    private readonly IClock _clock;

    private readonly List<InstrumentDefinition> _instruments = [];
    private readonly Dictionary<InstrumentKey, InstrumentDefinition> _byKey = [];
    private readonly Dictionary<InstrumentKey, decimal> _prices = [];

    private readonly List<FakeOrder> _orders = [];
    private readonly Dictionary<string, FakeOrder> _byOrderId = new(StringComparer.Ordinal);
    private readonly List<BrokerTrade> _trades = [];

    private readonly List<FakePosition> _positions = [];
    private readonly Dictionary<(InstrumentKey Instrument, PositionEffect Effect), FakePosition> _positionIndex = [];

    private readonly List<Currency> _currencies = [];
    private readonly Dictionary<Currency, decimal> _cash = [];

    private readonly Lock _gate = new();

    private int _sequence;
    private bool _timeoutArmed;

    /// <summary>Creates a book bound to a manifest.</summary>
    /// <param name="manifest">Drives every capability decision this book makes.</param>
    /// <param name="requireSession">The connector's <c>RequireSession</c>.</param>
    /// <param name="validate">The connector's <c>ValidateAgainstManifest</c>.</param>
    /// <param name="clock">Injected: order timestamps must be controllable from a test.</param>
    public FakeBrokerBook(
        ConnectorManifest manifest,
        Func<Result<BrokerSession>> requireSession,
        Func<PlaceOrderRequest, Result> validate,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(clock);

        _manifest = manifest;
        _requireSession = requireSession;
        _validate = validate;
        _clock = clock;

        foreach (var code in manifest.Currencies)
        {
            var currency = new Currency(code);
            _currencies.Add(currency);
            _cash[currency] = 1_000_000m;
        }
    }

    /// <summary>The universe, in declaration order.</summary>
    public IReadOnlyList<InstrumentDefinition> Instruments => _instruments;

    /// <summary>Adds a tradable instrument and its reference price.</summary>
    public FakeBrokerBook WithInstrument(InstrumentDefinition definition, decimal price)
    {
        ArgumentNullException.ThrowIfNull(definition);

        lock (_gate)
        {
            _instruments.Add(definition);
            _byKey[definition.Key] = definition;
            _prices[definition.Key] = price;
        }

        return this;
    }

    /// <summary>
    /// Makes the next <see cref="PlaceAsync"/> behave like a broker whose response was lost:
    /// the order IS created upstream, and the caller gets a Timeout.
    ///
    /// This is the single most important failure to be able to simulate. A caller that treats
    /// a timeout as "it did not happen" and retries creates a duplicate order — real money,
    /// twice — and the only defence is reconciling against the book on ClientOrderId. The
    /// conformance suite drives exactly that path through this switch.
    /// </summary>
    public void ArmPlaceTimeout()
    {
        lock (_gate)
        {
            _timeoutArmed = true;
        }
    }

    // -----------------------------------------------------------------------------------
    // IConnectorOrders
    // -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<OrderAck>.Failure(session.Error));
        }

        var validation = _validate(request);
        if (validation.IsFailure)
        {
            return Task.FromResult(Result<OrderAck>.Failure(validation.Error));
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(request.Instrument, out var definition))
            {
                return Task.FromResult(Result<OrderAck>.Failure(
                    ConnectorErrors.InstrumentNotFound(request.Instrument)));
            }

            // Idempotent on ClientOrderId, like any broker that carries one. Without this the
            // reconciliation test would pass for the wrong reason.
            foreach (var existing in _orders)
            {
                if (existing.Request.ClientOrderId == request.ClientOrderId)
                {
                    return Task.FromResult(Result<OrderAck>.Success(AckFor(existing)));
                }
            }

            var order = Accept(request, definition);

            if (_timeoutArmed)
            {
                _timeoutArmed = false;

                // The order exists. The answer does not arrive. This is what a real timeout
                // looks like from the caller's side, and why "retry the place" is wrong.
                return Task.FromResult(Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.Timeout,
                    "The broker did not respond in time. The order may or may not have been accepted.",
                    VendorCode: "TIMEOUT",
                    VendorMessage: "upstream read timeout")));
            }

            return Task.FromResult(Result<OrderAck>.Success(AckFor(order)));
        }
    }

    /// <inheritdoc />
    public Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<OrderAck>.Failure(session.Error));
        }

        lock (_gate)
        {
            if (!_byOrderId.TryGetValue(request.BrokerOrderId, out var order))
            {
                return Task.FromResult(Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No order '{request.BrokerOrderId}'.")));
            }

            if (!order.Status.IsWorking())
            {
                return Task.FromResult(Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Order '{request.BrokerOrderId}' is {order.Status}.")));
            }

            if (request.Quantity is { } quantity)
            {
                order.Quantity = quantity;
            }

            if (request.LimitPrice is { } limit)
            {
                order.LimitPrice = limit;
            }

            order.UpdatedAt = _clock.UtcNow;
            return Task.FromResult(Result<OrderAck>.Success(AckFor(order)));
        }
    }

    /// <inheritdoc />
    public Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<OrderAck>.Failure(session.Error));
        }

        lock (_gate)
        {
            if (!_byOrderId.TryGetValue(brokerOrderId, out var order))
            {
                return Task.FromResult(Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No order '{brokerOrderId}'.")));
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = _clock.UtcNow;
            return Task.FromResult(Result<OrderAck>.Success(AckFor(order)));
        }
    }

    /// <inheritdoc />
    public Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        if (!_manifest.Orders.CancelAll)
        {
            return NotSupportedFacets.DeclineAsync<int>("cancel-all");
        }

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<int>.Failure(session.Error));
        }

        lock (_gate)
        {
            var count = 0;

            foreach (var order in _orders)
            {
                if (!order.Status.IsWorking())
                {
                    continue;
                }

                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = _clock.UtcNow;
                count++;
            }

            return Task.FromResult(Result<int>.Success(count));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<BrokerOrder>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new List<BrokerOrder>();

            foreach (var order in _orders)
            {
                if (query.OpenOnly && !order.Status.IsWorking())
                {
                    continue;
                }

                if (query.Instrument is { } instrument && order.Request.Instrument != instrument)
                {
                    continue;
                }

                result.Add(ToBrokerOrder(order));
            }

            return Task.FromResult(Result<IReadOnlyList<BrokerOrder>>.Success(result));
        }
    }

    /// <inheritdoc />
    public Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<BrokerOrder>.Failure(session.Error));
        }

        lock (_gate)
        {
            return Task.FromResult(_byOrderId.TryGetValue(brokerOrderId, out var order)
                ? Result<BrokerOrder>.Success(ToBrokerOrder(order))
                : Result<BrokerOrder>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No order '{brokerOrderId}'.")));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<BrokerTrade>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new List<BrokerTrade>();

            foreach (var trade in _trades)
            {
                if (query.Instrument is { } instrument && trade.Instrument != instrument)
                {
                    continue;
                }

                result.Add(trade);
            }

            return Task.FromResult(Result<IReadOnlyList<BrokerTrade>>.Success(result));
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (!_manifest.Orders.Basket.Supported)
        {
            return await NotSupportedFacets.DeclineAsync<IReadOnlyList<OrderAck>>("basket orders");
        }

        if (requests.Count > _manifest.Orders.Basket.MaxLegs)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"This broker accepts at most {_manifest.Orders.Basket.MaxLegs} legs per basket.");
        }

        var acks = new List<OrderAck>(requests.Count);

        foreach (var request in requests)
        {
            var ack = await PlaceAsync(request, ct);
            if (ack.IsFailure)
            {
                // Non-atomic, and the manifest says so. Returning the partial set would hide
                // that; returning the failure with nothing placed would be a lie in the other
                // direction. The manifest's atomic:false is what tells the UI to warn.
                return Result<IReadOnlyList<OrderAck>>.Failure(ack.Error);
            }

            acks.Add(ack.Value);
        }

        return acks;
    }

    /// <inheritdoc />
    public Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_manifest.Orders.MarginEstimate)
        {
            return NotSupportedFacets.DeclineAsync<MarginEstimate>("margin estimation");
        }

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<MarginEstimate>.Failure(session.Error));
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(request.Instrument, out var definition))
            {
                return Task.FromResult(Result<MarginEstimate>.Failure(
                    ConnectorErrors.InstrumentNotFound(request.Instrument)));
            }

            var notional = _prices[request.Instrument] * request.Quantity.Value * definition.Multiplier;

            return Task.FromResult(Result<MarginEstimate>.Success(new MarginEstimate
            {
                Required = new Money(notional * 0.25m, definition.Currency),
                Available = new Money(_cash[definition.Currency], definition.Currency),
            }));
        }
    }

    /// <inheritdoc />
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_manifest.Orders.ChargesEstimate)
        {
            return NotSupportedFacets.DeclineAsync<ChargesEstimate>("charges estimation");
        }

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<ChargesEstimate>.Failure(session.Error));
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(request.Instrument, out var definition))
            {
                return Task.FromResult(Result<ChargesEstimate>.Failure(
                    ConnectorErrors.InstrumentNotFound(request.Instrument)));
            }

            var commission = new Money(5m, definition.Currency);

            return Task.FromResult(Result<ChargesEstimate>.Success(new ChargesEstimate
            {
                Lines = [new ChargeLine("Commission", commission)],
                Total = commission,
            }));
        }
    }

    // -----------------------------------------------------------------------------------
    // IConnectorPortfolio
    // -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<BrokerPosition>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new List<BrokerPosition>(_positions.Count);

            foreach (var position in _positions)
            {
                var last = new Money(_prices[position.Instrument], position.Currency);

                result.Add(new BrokerPosition
                {
                    Instrument = position.Instrument,
                    NetQuantity = new Quantity(position.Net),
                    PositionEffect = position.Effect,
                    AveragePrice = new Money(position.AverageCost, position.Currency),
                    LastPrice = last,
                    UnrealisedPnl = new Money(
                        (last.Amount - position.AverageCost) * position.Net * position.Multiplier,
                        position.Currency),
                    RealisedPnl = new Money(position.Realised, position.Currency),
                    BuyQuantity = new Quantity(position.Bought),
                    SellQuantity = new Quantity(position.Sold),
                });
            }

            return Task.FromResult(Result<IReadOnlyList<BrokerPosition>>.Success(result));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<BrokerHolding>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new List<BrokerHolding>();

            foreach (var position in _positions)
            {
                if (position.Net <= 0m || !position.Effect.HasFlag(PositionEffect.Delivery))
                {
                    continue;
                }

                var last = new Money(_prices[position.Instrument], position.Currency);

                result.Add(new BrokerHolding
                {
                    Instrument = position.Instrument,
                    Quantity = new Quantity(position.Net),
                    AveragePrice = new Money(position.AverageCost, position.Currency),
                    LastPrice = last,
                    UnrealisedPnl = new Money(
                        (last.Amount - position.AverageCost) * position.Net,
                        position.Currency),
                    Isin = position.Isin,
                });
            }

            return Task.FromResult(Result<IReadOnlyList<BrokerHolding>>.Success(result));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<BrokerBalance>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new List<BrokerBalance>(_currencies.Count);

            foreach (var currency in _currencies)
            {
                var cash = _cash[currency];

                result.Add(new BrokerBalance
                {
                    Currency = currency,
                    AvailableToTrade = new Money(cash, currency),
                    CashBalance = new Money(cash, currency),
                    UsedMargin = new Money(0m, currency),
                    AvailableMargin = new Money(cash, currency),
                });
            }

            return Task.FromResult(Result<IReadOnlyList<BrokerBalance>>.Success(result));
        }
    }

    // -----------------------------------------------------------------------------------
    // IConnectorMarketData
    // -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var session = _requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<Quote>.Failure(session.Error)
            : QuoteFor(instrument));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(session.Error));
        }

        lock (_gate)
        {
            var result = new Dictionary<InstrumentKey, Money>();

            foreach (var instrument in instruments)
            {
                if (_byKey.TryGetValue(instrument, out var definition))
                {
                    result[instrument] = new Money(_prices[instrument], definition.Currency);
                }
            }

            return Task.FromResult(Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(result));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(session.Error));
        }

        var result = new Dictionary<InstrumentKey, Quote>();

        foreach (var instrument in instruments)
        {
            var quote = QuoteFor(instrument);
            if (quote.IsSuccess)
            {
                result[instrument] = quote.Value;
            }
        }

        return Task.FromResult(Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(result));
    }

    /// <inheritdoc />
    public Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_manifest.MarketData.Historical)
        {
            return NotSupportedFacets.DeclineAsync<CandleSeries>("historical candles");
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(request.Instrument, out var definition))
            {
                return Task.FromResult(Result<CandleSeries>.Failure(
                    ConnectorErrors.InstrumentNotFound(request.Instrument)));
            }

            var price = _prices[request.Instrument];

            return Task.FromResult(Result<CandleSeries>.Success(new CandleSeries
            {
                Instrument = request.Instrument,
                TimeFrame = request.TimeFrame,
                Currency = definition.Currency,
                Candles =
                [
                    new Candle
                    {
                        OpenTime = request.From,
                        Open = price,
                        High = price,
                        Low = price,
                        Close = price,
                        Volume = 1_000,
                    },
                ],
            }));
        }
    }

    /// <inheritdoc />
    public Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        if (_manifest.MarketData.DepthLevels <= 0)
        {
            return NotSupportedFacets.DeclineAsync<MarketDepth>("market depth");
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(instrument, out var definition))
            {
                return Task.FromResult(Result<MarketDepth>.Failure(
                    ConnectorErrors.InstrumentNotFound(instrument)));
            }

            var price = _prices[instrument];
            var tick = definition.TickSize;

            return Task.FromResult(Result<MarketDepth>.Success(new MarketDepth
            {
                Instrument = instrument,
                Bids = [new DepthLevel(new Money(price - tick, definition.Currency), new Quantity(100m))],
                Asks = [new DepthLevel(new Money(price + tick, definition.Currency), new Quantity(100m))],
                Timestamp = _clock.UtcNow,
            }));
        }
    }

    /// <inheritdoc />
    public Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OptionChain>("option chains");

    // -----------------------------------------------------------------------------------
    // IConnectorReference
    // -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<InstrumentDefinition> snapshot;
        lock (_gate)
        {
            snapshot = [.. _instruments];
        }

        foreach (var definition in snapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (venue is { } v && definition.Key.Venue != v)
            {
                continue;
            }

            if (assetClass is { } a && definition.Key.AssetClass != a)
            {
                continue;
            }

            yield return definition;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_byKey.TryGetValue(key, out var definition)
                ? Result<InstrumentDefinition>.Success(definition)
                : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key)));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(Result<IReadOnlyList<InstrumentDefinition>>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "A search needs a non-empty query.")));
        }

        lock (_gate)
        {
            var matches = new List<InstrumentDefinition>();

            foreach (var definition in _instruments)
            {
                if (matches.Count >= limit)
                {
                    break;
                }

                if (definition.Key.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(definition);
                }
            }

            return Task.FromResult(Result<IReadOnlyList<InstrumentDefinition>>.Success(matches));
        }
    }

    // -----------------------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------------------

    private FakeOrder Accept(PlaceOrderRequest request, InstrumentDefinition definition)
    {
        _sequence++;

        var now = _clock.UtcNow;
        var price = _prices[request.Instrument];

        var order = new FakeOrder
        {
            BrokerOrderId = "FAKE-" + _sequence.ToString("D6", CultureInfo.InvariantCulture),
            Request = request,
            Quantity = request.Quantity,
            LimitPrice = request.LimitPrice,
            PlacedAt = now,
            Status = OrderStatus.Open,
            Currency = definition.Currency,
        };

        _orders.Add(order);
        _byOrderId[order.BrokerOrderId] = order;

        // Market orders fill immediately at the reference price; everything else rests. That
        // gives the suite both a filled order (so positions, trades and money exist to check)
        // and a working one (so cancel and modify have something to act on).
        if (request.OrderType == OrderType.Market)
        {
            Fill(order, request.Quantity.Value, price, definition, now);
        }

        return order;
    }

    private void Fill(
        FakeOrder order,
        decimal quantity,
        decimal price,
        InstrumentDefinition definition,
        DateTimeOffset at)
    {
        order.Filled = new Quantity(order.Filled.Value + quantity);
        order.Status = order.Filled.Value >= order.Quantity.Value
            ? OrderStatus.Filled
            : OrderStatus.PartiallyFilled;
        order.AveragePrice = new Money(price, definition.Currency);
        order.UpdatedAt = at;

        _trades.Add(new BrokerTrade
        {
            TradeId = order.BrokerOrderId + "-T1",
            BrokerOrderId = order.BrokerOrderId,
            Instrument = order.Request.Instrument,
            Side = order.Request.Side,
            Quantity = new Quantity(quantity),
            Price = new Money(price, definition.Currency),
            ExecutedAt = at,
        });

        var key = (order.Request.Instrument, order.Request.PositionEffect);

        if (!_positionIndex.TryGetValue(key, out var position))
        {
            position = new FakePosition
            {
                Instrument = order.Request.Instrument,
                Effect = order.Request.PositionEffect,
                Currency = definition.Currency,
                Multiplier = definition.Multiplier,
                Isin = definition.Isin,
            };

            _positions.Add(position);
            _positionIndex[key] = position;
        }

        var signed = order.Request.Side == Side.Buy ? quantity : -quantity;
        var oldSize = Math.Abs(position.Net);
        var newNet = position.Net + signed;

        if (position.Net == 0m || Math.Sign(position.Net) == Math.Sign(signed))
        {
            var newSize = Math.Abs(newNet);
            position.AverageCost = newSize == 0m
                ? 0m
                : ((position.AverageCost * oldSize) + (price * quantity)) / newSize;
        }
        else
        {
            var closing = Math.Min(oldSize, quantity);
            var direction = position.Net > 0m ? 1m : -1m;
            position.Realised += (price - position.AverageCost) * closing * direction * definition.Multiplier;

            if (newNet == 0m)
            {
                position.AverageCost = 0m;
            }
            else if (Math.Sign(newNet) != Math.Sign(position.Net))
            {
                position.AverageCost = price;
            }
        }

        position.Net = newNet;

        if (order.Request.Side == Side.Buy)
        {
            position.Bought += quantity;
        }
        else
        {
            position.Sold += quantity;
        }

        _cash[definition.Currency] += (order.Request.Side == Side.Buy ? -1m : 1m)
                                      * quantity * price * definition.Multiplier;
    }

    private Result<Quote> QuoteFor(InstrumentKey instrument)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(instrument, out var definition))
            {
                return Result<Quote>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
            }

            var price = _prices[instrument];
            var currency = definition.Currency;

            return new Quote
            {
                Instrument = instrument,
                LastPrice = new Money(price, currency),
                Open = new Money(price, currency),
                High = new Money(price, currency),
                Low = new Money(price, currency),
                PreviousClose = new Money(price, currency),
                BidPrice = new Money(price - definition.TickSize, currency),
                AskPrice = new Money(price + definition.TickSize, currency),
                Volume = 1_000,
                Timestamp = _clock.UtcNow,
            };
        }
    }

    private OrderAck AckFor(FakeOrder order) => new()
    {
        BrokerOrderId = order.BrokerOrderId,
        Status = order.Status,
        ClientOrderId = order.Request.ClientOrderId,
        AcknowledgedAt = _clock.UtcNow,
    };

    private static BrokerOrder ToBrokerOrder(FakeOrder order) => new()
    {
        BrokerOrderId = order.BrokerOrderId,
        ClientOrderId = order.Request.ClientOrderId,
        Instrument = order.Request.Instrument,
        Side = order.Request.Side,
        Quantity = order.Quantity,
        FilledQuantity = order.Filled,
        Status = order.Status,
        OrderType = order.Request.OrderType,
        PositionEffect = order.Request.PositionEffect,
        TimeInForce = order.Request.TimeInForce,
        Variety = order.Request.Variety,
        LimitPrice = order.LimitPrice,
        TriggerPrice = order.Request.TriggerPrice,
        AveragePrice = order.AveragePrice,
        PlacedAt = order.PlacedAt,
        UpdatedAt = order.UpdatedAt,
    };

    private sealed class FakeOrder
    {
        public required string BrokerOrderId { get; init; }

        public required PlaceOrderRequest Request { get; init; }

        public required DateTimeOffset PlacedAt { get; init; }

        public required Currency Currency { get; init; }

        public Quantity Quantity { get; set; }

        public Quantity Filled { get; set; } = Quantity.Zero;

        public Money? LimitPrice { get; set; }

        public Money? AveragePrice { get; set; }

        public OrderStatus Status { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class FakePosition
    {
        public required InstrumentKey Instrument { get; init; }

        public required PositionEffect Effect { get; init; }

        public required Currency Currency { get; init; }

        public required decimal Multiplier { get; init; }

        public string? Isin { get; init; }

        public decimal Net { get; set; }

        public decimal AverageCost { get; set; }

        public decimal Realised { get; set; }

        public decimal Bought { get; set; }

        public decimal Sold { get; set; }
    }
}
