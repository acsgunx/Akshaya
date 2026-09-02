using System.Globalization;
using System.Threading.Channels;
using Akshaya.Connector.Paper.Charges;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// The simulated venue: an in-memory order book, a fill model, and position and cash
/// accounting. Everything the Paper connector actually does happens here; the facets around
/// it are adapters.
///
/// <b>Why determinism is the primary requirement.</b>
/// This engine is the execution venue for the backtester. A backtest that produces different
/// fills on a re-run is worse than useless: it cannot be debugged (you cannot bisect a change
/// whose baseline moves), it cannot be reviewed (two people get two answers from one config),
/// and it cannot be trusted (a strategy that looked profitable might have been one lucky
/// ordering). Reproducibility is not a nice property of this file, it is the point of it.
///
/// <b>How determinism is guaranteed.</b> Four rules, all of them mechanical:
///
///  1. NO AMBIENT TIME. Nothing here reads <c>DateTimeOffset.UtcNow</c>. The engine's notion
///     of "now" is <see cref="Now"/>: the highest tick timestamp seen so far, falling back to
///     the injected <see cref="IClock"/> before the first tick. Replay the same tape and every
///     timestamp on every order and trade is identical.
///  2. NO UNORDERED ITERATION. Nothing that can influence a fill, an event or a returned list
///     iterates a <see cref="Dictionary{TKey,TValue}"/>. Orders live in a
///     <see cref="SortedDictionary{TKey,TValue}"/> keyed by a monotonic sequence number;
///     resting orders per instrument are a <see cref="List{T}"/> in arrival order; positions
///     and currencies keep parallel insertion-ordered lists beside their lookup indexes.
///     Dictionaries appear only as O(1) indexes that are read by key, never enumerated.
///  3. INJECTED SEED. The only randomness is fill-size jitter, drawn from a
///     <see cref="Random"/> constructed from <see cref="PaperFillPolicy.Seed"/> and touched
///     exclusively under the engine lock — so the Nth draw of a run is the Nth draw of every
///     replay. It is deliberately <see cref="Random"/> and not a cryptographic generator;
///     unpredictability here would be a defect.
///  4. ONE LOCK, ONE ORDER OF EFFECTS. Every mutation and every event publication happens
///     under <c>_gate</c>. Publication is inside the lock on purpose: writing to the
///     subscriber channels outside it would let two concurrent ticks interleave their events
///     and give a consumer an order update before the trade that caused it.
///
/// Thread-safe: every public member takes the lock. Fills are computed synchronously inside
/// <see cref="OnTick"/>, so a caller driving the engine from a replay loop needs no
/// asynchrony at all — <see cref="RunAsync"/> exists only for the live-feed case.
/// </summary>
public sealed class MatchingEngine : IAsyncDisposable
{
    /// <summary>
    /// Per-subscriber event buffer. Bounded with drop-oldest because the contract says a
    /// stream consumer must never back-pressure ingest: a slow UI must lose stale ticks, not
    /// stall the engine that is filling orders.
    /// </summary>
    private const int EventBufferCapacity = 8192;

    /// <summary>Quantities are rounded here so a fractional fill cannot drift by a rounding tail.</summary>
    private const int QuantityDecimals = 8;

    private readonly IMarketDataSource _source;
    private readonly PaperOptions _options;
    private readonly IClock _clock;
    private readonly Random _random;
    private readonly Lock _gate = new();

    /// <summary>Every order ever accepted, keyed by arrival sequence. Sorted, so iteration order is arrival order.</summary>
    private readonly SortedDictionary<long, WorkingOrder> _bySequence = [];

    private readonly Dictionary<string, WorkingOrder> _byBrokerId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, WorkingOrder> _byClientId = [];

    /// <summary>Working orders per instrument, in arrival order. The matching loop walks this list.</summary>
    private readonly Dictionary<InstrumentKey, List<long>> _resting = [];

    private readonly Dictionary<InstrumentKey, Tick> _lastTick = [];
    private readonly Dictionary<InstrumentKey, InstrumentDefinition> _definitions = [];

    /// <summary>Positions in creation order. The index beside it is for lookup only, never enumerated.</summary>
    private readonly List<PositionState> _positions = [];

    private readonly Dictionary<PositionKey, PositionState> _positionIndex = [];

    /// <summary>Currencies in the order they were first funded or traded. Balances are reported in this order.</summary>
    private readonly List<Currency> _currencies = [];

    private readonly Dictionary<Currency, CashState> _cash = [];
    private readonly List<BrokerTrade> _trades = [];
    private readonly List<Channel<StreamEvent>> _subscribers = [];

    private long _sequence;
    private long _tradeSequence;
    private DateTimeOffset? _marketTime;
    private DateOnly? _lastExpirySweepDate;
    private bool _sessionClosed;

    /// <summary>Creates an engine bound to a price source.</summary>
    /// <param name="source">Live feed for paper trading, or a replay for a backtest. Same engine either way.</param>
    /// <param name="options">Fill model, opening cash and charge schedules.</param>
    /// <param name="clock">Used only until the first tick arrives; see the determinism remarks.</param>
    public MatchingEngine(IMarketDataSource source, PaperOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _source = source;
        _options = options;
        _clock = clock;
        _random = new Random(options.Fills.Seed);

        foreach (var (code, amount) in options.StartingCash.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Ordered by code so the balances list is stable regardless of how the caller's
            // dictionary happened to be built.
            var currency = new Currency(code);
            _currencies.Add(currency);
            _cash[currency] = new CashState { Balance = amount };
        }
    }

    /// <summary>
    /// The engine's clock: the newest tick timestamp seen, or the injected clock before any
    /// tick. Exposed so the facets stamp acknowledgements with the same instant the engine
    /// stamps fills — two different "now"s in one connector produce trades that appear to
    /// precede the orders that caused them.
    /// </summary>
    public DateTimeOffset MarketTime
    {
        get
        {
            lock (_gate)
            {
                return Now();
            }
        }
    }

    /// <summary>Total executions so far. Cheap health signal for the UI and for tests.</summary>
    public long TradeCount
    {
        get
        {
            lock (_gate)
            {
                return _trades.Count;
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // Driving the engine
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Pumps the source into the engine until it is exhausted or cancelled, then closes the
    /// session. A backtest may call this, or may call <see cref="OnTick"/> itself in a tight
    /// loop; both take the identical path through <see cref="OnTick"/>.
    /// </summary>
    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var tick in _source.Ticks(ct).WithCancellation(ct))
            {
                OnTick(tick);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is how a paper session is stopped, not a failure.
            return Result.Success();
        }
        catch (Exception ex)
        {
            // The single catch-all boundary in this file: an arbitrary source implementation
            // must not be able to tear the engine down, and the caller needs a Result.
            return new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                $"The paper market-data source failed: {ex.Message}");
        }

        EndSession();
        return Result.Success();
    }

    /// <summary>
    /// Applies one tick: updates the book, expires what has aged out, and matches every
    /// resting order on that instrument in arrival order.
    /// </summary>
    public void OnTick(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        lock (_gate)
        {
            AdvanceTime(tick.Timestamp);
            _lastTick[tick.Instrument] = tick;
            Emit(new StreamEvent.TickReceived(tick));

            SweepExpired();
            MatchInstrument(tick.Instrument, tick);
        }
    }

    /// <summary>
    /// Ends the trading session: fills at-the-close orders at the last known price and expires
    /// day orders. Idempotent, so a backtest that both exhausts its tape and calls this
    /// explicitly does not double-fill.
    /// </summary>
    public void EndSession()
    {
        lock (_gate)
        {
            if (_sessionClosed)
            {
                return;
            }

            _sessionClosed = true;

            foreach (var order in _bySequence.Values.ToArray())
            {
                if (!order.Status.IsWorking())
                {
                    continue;
                }

                // Armed matters even at the close: an untriggered stop must expire, not fill.
                if (order.TimeInForce == TimeInForce.AtTheClose
                    && order.Armed
                    && _lastTick.TryGetValue(order.Request.Instrument, out var closingTick))
                {
                    FillAgainst(order, closingTick, FillMode.Full);
                }

                if (!order.Status.IsWorking())
                {
                    continue;
                }

                if (order.TimeInForce is TimeInForce.Day or TimeInForce.AtTheOpen or TimeInForce.AtTheClose)
                {
                    Terminate(order, OrderStatus.Expired, "The trading session ended before this order filled.");
                }
            }

            PruneResting();
        }
    }

    /// <summary>
    /// Re-opens the engine for another session. Positions, cash and history survive; only the
    /// day-order lifecycle resets. A multi-day backtest calls this between days.
    /// </summary>
    public void BeginSession()
    {
        lock (_gate)
        {
            _sessionClosed = false;
        }
    }

    /// <summary>
    /// A private event feed. Each subscriber gets its own channel, so a slow consumer drops
    /// its own ticks rather than everyone's, and every subscriber sees events in the same
    /// order the engine produced them.
    /// </summary>
    public ChannelReader<StreamEvent> Subscribe()
    {
        var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    /// <summary>Detaches a feed obtained from <see cref="Subscribe"/> and completes it.</summary>
    public void Unsubscribe(ChannelReader<StreamEvent> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_gate)
        {
            for (var i = 0; i < _subscribers.Count; i++)
            {
                if (ReferenceEquals(_subscribers[i].Reader, reader))
                {
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                    return;
                }
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // Order entry
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Accepts an order, attempts an immediate match against the current book, and resolves
    /// the time-in-force. Idempotent on <see cref="PlaceOrderRequest.ClientOrderId"/>.
    /// </summary>
    public Result<OrderAck> Place(PlaceOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_byClientId.TryGetValue(request.ClientOrderId, out var duplicate))
            {
                // The reconciliation contract in PlaceOrderRequest says the caller persists
                // the ClientOrderId BEFORE calling, then matches on it after a timeout. A
                // simulated broker that happily created a second order on the retry would
                // hide exactly the bug that contract exists to prevent.
                return Ack(duplicate);
            }

            var definition = ResolveDefinition(request.Instrument);
            if (definition.IsFailure)
            {
                return Result<OrderAck>.Failure(definition.Error);
            }

            var def = definition.Value;

            var validation = Validate(request, def);
            if (validation.IsFailure)
            {
                return Result<OrderAck>.Failure(validation.Error);
            }

            var order = Register(request, def);

            Emit(new StreamEvent.OrderUpdated(ToBrokerOrder(order)));

            // An immediate attempt against the snapshot the book already holds. Without it a
            // market order placed between ticks would sit unfilled, which no venue does and
            // which would make every backtest's entry price one tick late.
            if (_lastTick.TryGetValue(request.Instrument, out var tick))
            {
                Progress(order, tick, isPlacement: true);
            }

            ResolveImmediateTimeInForce(order);

            if (order.Status.IsWorking())
            {
                RestingList(request.Instrument).Add(order.Sequence);
            }

            return Ack(order);
        }
    }

    /// <summary>Amends a working order. Terminal orders are refused rather than silently recreated.</summary>
    public Result<OrderAck> Modify(ModifyOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (!_byBrokerId.TryGetValue(request.BrokerOrderId, out var order))
            {
                return Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No paper order with id '{request.BrokerOrderId}'."));
            }

            if (!order.Status.IsWorking())
            {
                return Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Order '{request.BrokerOrderId}' is {order.Status} and can no longer be modified."));
            }

            if (request.Quantity is { } quantity)
            {
                if (quantity.Value <= order.Filled.Value)
                {
                    return Result<OrderAck>.Failure(new Error(
                        ConnectorErrorCodes.InvalidRequest,
                        "The new quantity is not greater than the quantity already filled."));
                }

                order.Quantity = quantity;
            }

            if (request.LimitPrice is { } limit)
            {
                order.LimitPrice = limit;
            }

            if (request.TriggerPrice is { } trigger)
            {
                order.TriggerPrice = trigger;

                // A re-priced trigger re-arms: a stop moved back above the market must stop
                // being a market order again, or a modify would be a one-way door.
                order.Armed = order.OrderType is OrderType.Market or OrderType.Limit;
                order.TrailOffset = null;
                order.TrailWatermark = null;
            }

            if (request.OrderType is { } type)
            {
                order.OrderType = type;
                order.Armed = type is OrderType.Market or OrderType.Limit;
            }

            if (request.TimeInForce is { } tif)
            {
                order.TimeInForce = tif;
            }

            if (request.DisclosedQuantity is { } disclosed)
            {
                order.DisclosedQuantity = disclosed;
            }

            order.UpdatedAt = Now();
            order.StatusMessage = "Modified.";
            Emit(new StreamEvent.OrderUpdated(ToBrokerOrder(order)));

            return Ack(order);
        }
    }

    /// <summary>Cancels a working order.</summary>
    public Result<OrderAck> Cancel(string brokerOrderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);

        lock (_gate)
        {
            if (!_byBrokerId.TryGetValue(brokerOrderId, out var order))
            {
                return Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No paper order with id '{brokerOrderId}'."));
            }

            if (!order.Status.IsWorking())
            {
                return Result<OrderAck>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"Order '{brokerOrderId}' is already {order.Status}."));
            }

            Terminate(order, OrderStatus.Cancelled, "Cancelled by the trader.");
            PruneResting();
            return Ack(order);
        }
    }

    /// <summary>Cancels every working order and returns how many were cancelled.</summary>
    public Result<int> CancelAll()
    {
        lock (_gate)
        {
            var cancelled = 0;

            foreach (var order in _bySequence.Values.ToArray())
            {
                if (!order.Status.IsWorking())
                {
                    continue;
                }

                Terminate(order, OrderStatus.Cancelled, "Cancelled by cancel-all.");
                cancelled++;
            }

            PruneResting();
            return cancelled;
        }
    }

    // -----------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------

    /// <summary>The order book, in arrival order, filtered by the query.</summary>
    public IReadOnlyList<BrokerOrder> Orders(OrderQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var result = new List<BrokerOrder>();

            foreach (var order in _bySequence.Values)
            {
                if (query.OpenOnly && !order.Status.IsWorking())
                {
                    continue;
                }

                if (query.Instrument is { } instrument && order.Request.Instrument != instrument)
                {
                    continue;
                }

                if (!WithinDates(query, order.PlacedAt))
                {
                    continue;
                }

                result.Add(ToBrokerOrder(order));
            }

            return result;
        }
    }

    /// <summary>One order by broker id.</summary>
    public Result<BrokerOrder> Order(string brokerOrderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);

        lock (_gate)
        {
            return _byBrokerId.TryGetValue(brokerOrderId, out var order)
                ? ToBrokerOrder(order)
                : Result<BrokerOrder>.Failure(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No paper order with id '{brokerOrderId}'."));
        }
    }

    /// <summary>The trade book, in execution order.</summary>
    public IReadOnlyList<BrokerTrade> Trades(OrderQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var result = new List<BrokerTrade>();

            foreach (var trade in _trades)
            {
                if (query.Instrument is { } instrument && trade.Instrument != instrument)
                {
                    continue;
                }

                if (!WithinDates(query, trade.ExecutedAt))
                {
                    continue;
                }

                result.Add(trade);
            }

            return result;
        }
    }

    /// <summary>Open positions, marked to the latest known price, in the order they were opened.</summary>
    public IReadOnlyList<BrokerPosition> Positions()
    {
        lock (_gate)
        {
            var result = new List<BrokerPosition>(_positions.Count);

            foreach (var position in _positions)
            {
                var mark = MarkPrice(position.Instrument);

                result.Add(new BrokerPosition
                {
                    Instrument = position.Instrument,
                    NetQuantity = new Quantity(position.Net),
                    PositionEffect = position.Effect,
                    AveragePrice = new Money(position.AverageCost, position.Currency),
                    LastPrice = mark,
                    UnrealisedPnl = mark is { } m
                        ? new Money(
                            (m.Amount - position.AverageCost) * position.Net * position.Multiplier,
                            position.Currency)
                        : null,
                    RealisedPnl = new Money(position.RealisedPnl, position.Currency),
                    BuyQuantity = new Quantity(position.BuyQuantity),
                    SellQuantity = new Quantity(position.SellQuantity),
                });
            }

            return result;
        }
    }

    /// <summary>
    /// Long delivery positions, presented as settled holdings. A simulated depository: the
    /// engine has no settlement cycle, so a delivery buy appears as a holding immediately.
    /// Documented rather than hidden, because a T+1 strategy backtested here will look like it
    /// can sell a day earlier than it can.
    /// </summary>
    public IReadOnlyList<BrokerHolding> Holdings()
    {
        lock (_gate)
        {
            var result = new List<BrokerHolding>();

            foreach (var position in _positions)
            {
                if (position.Net <= 0m
                    || !(position.Effect.HasFlag(PositionEffect.Delivery)
                         || position.Effect.HasFlag(PositionEffect.CarryForward)))
                {
                    continue;
                }

                var mark = MarkPrice(position.Instrument);

                result.Add(new BrokerHolding
                {
                    Instrument = position.Instrument,
                    Quantity = new Quantity(position.Net),
                    AveragePrice = new Money(position.AverageCost, position.Currency),
                    LastPrice = mark,
                    UnrealisedPnl = mark is { } m
                        ? new Money(
                            (m.Amount - position.AverageCost) * position.Net * position.Multiplier,
                            position.Currency)
                        : null,
                    Isin = position.Isin,
                });
            }

            return result;
        }
    }

    /// <summary>
    /// One balance per funded currency, never collapsed into a single figure. A paper account
    /// that holds SGD and USD has two balances for the same reason a real one does.
    /// </summary>
    public IReadOnlyList<BrokerBalance> Balances()
    {
        lock (_gate)
        {
            var result = new List<BrokerBalance>(_currencies.Count);

            foreach (var currency in _currencies)
            {
                var cash = _cash[currency];
                var realised = 0m;
                var unrealised = 0m;
                var used = 0m;

                foreach (var position in _positions)
                {
                    if (position.Currency != currency)
                    {
                        continue;
                    }

                    realised += position.RealisedPnl;

                    if (MarkPrice(position.Instrument) is { } mark)
                    {
                        unrealised += (mark.Amount - position.AverageCost) * position.Net * position.Multiplier;
                    }

                    if (position.Effect.HasFlag(PositionEffect.Margin)
                        || position.Effect.HasFlag(PositionEffect.Intraday)
                        || position.Effect.HasFlag(PositionEffect.CarryForward))
                    {
                        used += Math.Abs(position.Net) * position.AverageCost * position.Multiplier
                                * _options.MarginFraction;
                    }
                }

                result.Add(new BrokerBalance
                {
                    Currency = currency,
                    AvailableToTrade = new Money(cash.Balance - used, currency),
                    CashBalance = new Money(cash.Balance, currency),
                    UsedMargin = new Money(used, currency),
                    AvailableMargin = new Money(cash.Balance - used, currency),
                    RealisedPnl = new Money(realised, currency),
                    UnrealisedPnl = new Money(unrealised, currency),
                });
            }

            return result;
        }
    }

    /// <summary>The most recent tick for an instrument, or InstrumentNotFound if none has arrived.</summary>
    public Result<Tick> LastTick(InstrumentKey instrument)
    {
        lock (_gate)
        {
            return _lastTick.TryGetValue(instrument, out var tick)
                ? tick
                : Result<Tick>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
        }
    }

    /// <summary>Instrument definition, resolved through the source and cached.</summary>
    public Result<InstrumentDefinition> Definition(InstrumentKey instrument)
    {
        lock (_gate)
        {
            return ResolveDefinition(instrument);
        }
    }

    // -----------------------------------------------------------------------------------
    // Estimates
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Itemised charges for an order that has not executed yet, priced at its limit price when
    /// it has one and at the last traded price otherwise.
    /// </summary>
    public Result<ChargesEstimate> EstimateCharges(PlaceOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var definition = ResolveDefinition(request.Instrument);
            if (definition.IsFailure)
            {
                return Result<ChargesEstimate>.Failure(definition.Error);
            }

            var price = request.LimitPrice ?? MarkPrice(request.Instrument);
            if (price is null)
            {
                return Result<ChargesEstimate>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    "Charges cannot be estimated for a market order before a price is known for the instrument."));
            }

            return ChargesFor(new ChargeContext
            {
                Instrument = request.Instrument,
                Side = request.Side,
                Quantity = request.Quantity,
                Price = price.Value,
                PositionEffect = request.PositionEffect,
                Multiplier = definition.Value.Multiplier,
            });
        }
    }

    /// <summary>
    /// Crude margin: full notional for anything that settles, a configurable fraction of it for
    /// anything leveraged. See <see cref="PaperOptions.MarginFraction"/> for why this is
    /// deliberately not a SPAN calculation.
    /// </summary>
    public Result<MarginEstimate> EstimateMargin(PlaceOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var definition = ResolveDefinition(request.Instrument);
            if (definition.IsFailure)
            {
                return Result<MarginEstimate>.Failure(definition.Error);
            }

            var def = definition.Value;
            var price = request.LimitPrice ?? MarkPrice(request.Instrument);
            if (price is null)
            {
                return Result<MarginEstimate>.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    "Margin cannot be estimated before a price is known for the instrument."));
            }

            var notional = Math.Abs(price.Value.Amount * request.Quantity.Value * def.Multiplier);

            var leveraged = request.PositionEffect.HasFlag(PositionEffect.Intraday)
                            || request.PositionEffect.HasFlag(PositionEffect.Margin)
                            || request.PositionEffect.HasFlag(PositionEffect.CarryForward)
                            || request.Instrument.IsDerivative;

            var required = leveraged ? notional * _options.MarginFraction : notional;

            var available = _cash.TryGetValue(def.Currency, out var cash)
                ? new Money(cash.Balance, def.Currency)
                : (Money?)null;

            return new MarginEstimate
            {
                Required = new Money(Math.Round(required, 2, MidpointRounding.ToEven), def.Currency),
                Available = available,
            };
        }
    }

    // -----------------------------------------------------------------------------------
    // Matching
    // -----------------------------------------------------------------------------------

    private void MatchInstrument(InstrumentKey instrument, Tick tick)
    {
        if (!_resting.TryGetValue(instrument, out var list) || list.Count == 0)
        {
            return;
        }

        // Snapshot the arrival-ordered list: filling can terminate orders, and mutating the
        // list while walking it would make the fill order depend on removal timing.
        foreach (var sequence in list.ToArray())
        {
            if (!_bySequence.TryGetValue(sequence, out var order) || !order.Status.IsWorking())
            {
                continue;
            }

            order.TicksSeen++;
            Progress(order, tick, isPlacement: false);
        }

        list.RemoveAll(sequence =>
            !_bySequence.TryGetValue(sequence, out var order) || !order.Status.IsWorking());
    }

    private void Progress(WorkingOrder order, Tick tick, bool isPlacement)
    {
        UpdateTrailing(order, tick);

        if (!order.Armed && !TryArm(order, tick))
        {
            return;
        }

        switch (order.TimeInForce)
        {
            case TimeInForce.AtTheClose:
                // Rests until EndSession. Filling it on an ordinary tick would make it an
                // ordinary order with a misleading name.
                return;

            case TimeInForce.AtTheOpen when !isPlacement && order.TicksSeen > 1:
                Terminate(order, OrderStatus.Expired, "At-the-open order did not fill on the opening print.");
                return;

            case TimeInForce.AtTheOpen:
                // An opening auction prints the whole size or none of it; the per-tick ratio
                // cap has no meaning there.
                FillAgainst(order, tick, FillMode.Full);
                if (order.Status.IsWorking())
                {
                    Terminate(order, OrderStatus.Expired, "At-the-open order did not fill on the opening print.");
                }

                return;

            case TimeInForce.Fok:
                FillAgainst(order, tick, FillMode.Full);
                return;

            default:
                FillAgainst(order, tick, FillMode.Sliced);
                return;
        }
    }

    /// <summary>
    /// Stop, stop-limit, market-if-touched and trailing-stop orders are dormant until their
    /// trigger is reached. Arming is separate from filling because an armed stop becomes a
    /// MARKET order and will then fill at the touch, slippage included — which is the whole
    /// risk of a stop and must not be modelled away.
    /// </summary>
    private bool TryArm(WorkingOrder order, Tick tick)
    {
        var trigger = order.TriggerPrice?.Amount;
        if (trigger is null)
        {
            // No trigger on an order type that needs one cannot happen: Validate rejects it.
            // Treat as armed rather than as permanently dormant, so a modify cannot strand it.
            order.Armed = true;
            return true;
        }

        var last = tick.LastPrice.Amount;

        var effectiveTrigger = order.OrderType == OrderType.TrailingStop && order.TrailWatermark is { } watermark
            ? order.Side == Side.Sell
                ? watermark - (order.TrailOffset ?? 0m)
                : watermark + (order.TrailOffset ?? 0m)
            : trigger.Value;

        var armed = order.OrderType switch
        {
            // A stop protects a position: a sell stop triggers on the way DOWN, a buy stop
            // (covering a short, or a breakout entry) on the way UP.
            OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop =>
                order.Side == Side.Buy ? last >= effectiveTrigger : last <= effectiveTrigger,

            // Market-if-touched is the mirror image: it is an entry, so a buy triggers on the
            // way DOWN. Getting these two the same way round is a classic and expensive bug.
            OrderType.MarketIfTouched =>
                order.Side == Side.Buy ? last <= effectiveTrigger : last >= effectiveTrigger,

            _ => true,
        };

        if (!armed)
        {
            return false;
        }

        order.Armed = true;
        order.UpdatedAt = Now();
        order.StatusMessage = "Trigger reached; the order is now live.";
        Emit(new StreamEvent.OrderUpdated(ToBrokerOrder(order)));
        return true;
    }

    /// <summary>
    /// Maintains a trailing stop's watermark and its distance from it.
    ///
    /// The offset is taken once, from the gap between the market and the trigger at the moment
    /// the order first sees a price. It is not re-derived later: a trailing stop whose distance
    /// widened as the market moved would not be a trailing stop.
    /// </summary>
    private static void UpdateTrailing(WorkingOrder order, Tick tick)
    {
        if (order.OrderType != OrderType.TrailingStop || order.TriggerPrice is not { } trigger)
        {
            return;
        }

        var last = tick.LastPrice.Amount;

        if (order.TrailWatermark is null)
        {
            order.TrailWatermark = last;
            order.TrailOffset = Math.Abs(last - trigger.Amount);
            return;
        }

        order.TrailWatermark = order.Side == Side.Sell
            ? Math.Max(order.TrailWatermark.Value, last)   // a sell stop ratchets up with the high
            : Math.Min(order.TrailWatermark.Value, last);  // a buy stop ratchets down with the low
    }

    private void FillAgainst(WorkingOrder order, Tick tick, FillMode mode)
    {
        var definition = ResolveDefinition(order.Request.Instrument);
        if (definition.IsFailure)
        {
            return;
        }

        var def = definition.Value;

        var reference = ReferencePrice(order.Side, tick);
        if (reference is null)
        {
            return;
        }

        var slipped = ApplySlippage(reference.Value, order.Side, def.TickSize);
        decimal price;

        switch (EffectiveType(order))
        {
            case OrderType.Market:
                price = slipped;
                break;

            case OrderType.Limit:
                if (order.LimitPrice is not { } limitMoney)
                {
                    return;
                }

                var limit = limitMoney.Amount;

                // The crossing test uses the RAW touch, not the slipped price: slippage models
                // execution quality, and letting it create a fill would let a limit order
                // trade through its own limit.
                var crosses = _options.Fills.FillLimitOnTouch
                    ? order.Side == Side.Buy ? reference.Value <= limit : reference.Value >= limit
                    : order.Side == Side.Buy ? reference.Value < limit : reference.Value > limit;

                if (!crosses)
                {
                    return;
                }

                // A limit order never fills worse than its limit, whatever the slippage model
                // says. It may fill better.
                price = order.Side == Side.Buy ? Math.Min(limit, slipped) : Math.Max(limit, slipped);
                break;

            default:
                return;
        }

        var quantity = FillQuantity(order, tick, def, mode);
        if (quantity <= 0m)
        {
            return;
        }

        RecordFill(order, quantity, price, def);
    }

    /// <summary>
    /// How much of an order may trade against a single print.
    ///
    /// The per-tick ratio is the important part. Without it a hundred-thousand-share order
    /// fills on one tick at one price, and every size-sensitive strategy backtests as if
    /// liquidity were infinite. Fill-or-kill and at-the-open bypass it, because for those the
    /// question is all-or-nothing rather than how-much.
    /// </summary>
    private decimal FillQuantity(WorkingOrder order, Tick tick, InstrumentDefinition def, FillMode mode)
    {
        var pending = order.Quantity.Value - order.Filled.Value;
        if (pending <= 0m)
        {
            return 0m;
        }

        var available = _options.Fills.RespectTickQuantity
                        && tick.LastQuantity is { } printed
                        && printed.Value > 0m
            ? printed.Value
            : decimal.MaxValue;

        if (mode == FillMode.Full)
        {
            // All or nothing: if the print is too small to take the whole order, no fill.
            return available >= pending ? pending : 0m;
        }

        var ratio = _options.Fills.MaxFillRatioPerTick;
        var cap = ratio is <= 0m or >= 1m ? pending : order.Quantity.Value * ratio;

        var quantity = Math.Min(pending, Math.Min(cap, available));

        if (_options.Fills.FillJitter > 0m)
        {
            // Seeded, drawn under the engine lock: the Nth draw of a run is the Nth draw of
            // every replay of the same tape. See the determinism remarks on the class.
            var factor = 1m - (_options.Fills.FillJitter * (decimal)_random.NextDouble());
            quantity *= factor;
        }

        return RoundToLot(quantity, pending, def.LotSize);
    }

    private static decimal RoundToLot(decimal quantity, decimal pending, decimal lotSize)
    {
        var rounded = Math.Round(quantity, QuantityDecimals, MidpointRounding.ToZero);

        if (rounded >= pending)
        {
            return pending;
        }

        if (lotSize <= 0m || lotSize == 1m)
        {
            return rounded <= 0m ? 0m : rounded;
        }

        var lots = Math.Floor(rounded / lotSize);
        var byLot = lots * lotSize;

        // Never let the per-tick cap round a lot-sized instrument down to nothing: an order
        // that can never fill any slice would rest forever and look like an engine bug.
        return byLot > 0m ? byLot : Math.Min(lotSize, pending);
    }

    private void RecordFill(WorkingOrder order, decimal quantity, decimal price, InstrumentDefinition def)
    {
        var now = Now();
        var currency = def.Currency;

        order.Filled = new Quantity(
            Math.Round(order.Filled.Value + quantity, QuantityDecimals, MidpointRounding.ToEven));
        order.FilledNotional += quantity * price;
        order.UpdatedAt = now;
        order.Status = order.Filled.Value >= order.Quantity.Value
            ? OrderStatus.Filled
            : OrderStatus.PartiallyFilled;
        order.StatusMessage = null;

        _tradeSequence++;

        var charges = ChargesFor(new ChargeContext
        {
            Instrument = order.Request.Instrument,
            Side = order.Side,
            Quantity = new Quantity(quantity),
            Price = new Money(price, currency),
            PositionEffect = order.Request.PositionEffect,
            Multiplier = def.Multiplier,
        });

        var chargeAmount = charges.IsSuccess ? charges.Value.Total : (Money?)null;

        var trade = new BrokerTrade
        {
            TradeId = "PAPER-T-" + _tradeSequence.ToString("D8", CultureInfo.InvariantCulture),
            BrokerOrderId = order.BrokerOrderId,
            Instrument = order.Request.Instrument,
            Side = order.Side,
            Quantity = new Quantity(quantity),
            Price = new Money(price, currency),
            ExecutedAt = now,
            Charges = chargeAmount,
        };

        _trades.Add(trade);

        ApplyToPosition(order, quantity, price, def);
        ApplyToCash(order.Side, quantity, price, def, chargeAmount);

        // Trade first, then the order snapshot. A consumer that sees an order go to Filled
        // before the trade that filled it will reconcile a fill it has no execution for, and
        // will then double-count when the execution arrives.
        Emit(new StreamEvent.TradeExecuted(trade));
        Emit(new StreamEvent.OrderUpdated(ToBrokerOrder(order)));
    }

    /// <summary>
    /// Position accounting, including the case everything else gets wrong: a fill large enough
    /// to close the existing position AND open a new one on the other side.
    ///
    /// Buy 100 then sell 150 must realise P&amp;L on the 100 that closed, and leave a 50 short
    /// whose average cost is the sell price — NOT a 50 short carrying the original buy's
    /// average, and NOT realised P&amp;L computed on 150. Both of those mistakes are easy to
    /// make in one expression and neither shows up until a strategy reverses.
    /// </summary>
    private void ApplyToPosition(WorkingOrder order, decimal quantity, decimal price, InstrumentDefinition def)
    {
        var position = GetOrCreatePosition(order.Request.Instrument, order.Request.PositionEffect, def);
        var signed = order.Side == Side.Buy ? quantity : -quantity;

        if (order.Side == Side.Buy)
        {
            position.BuyQuantity += quantity;
        }
        else
        {
            position.SellQuantity += quantity;
        }

        var opening = position.Net == 0m || Math.Sign(position.Net) == Math.Sign(signed);

        if (opening)
        {
            // Adding to (or starting) a position: weighted-average cost over the absolute size.
            var oldSize = Math.Abs(position.Net);
            var newNet = position.Net + signed;
            var newSize = Math.Abs(newNet);

            position.AverageCost = newSize == 0m
                ? 0m
                : ((position.AverageCost * oldSize) + (price * quantity)) / newSize;

            position.Net = newNet;
            return;
        }

        // Reducing, closing, or crossing through flat.
        var closing = Math.Min(Math.Abs(position.Net), quantity);
        var direction = position.Net > 0m ? 1m : -1m;

        // Realised only on the part that actually closed. The rest of the fill opens new risk
        // and has no P&L yet.
        position.RealisedPnl += (price - position.AverageCost) * closing * direction * def.Multiplier;

        var net = position.Net + signed;

        if (net == 0m)
        {
            position.AverageCost = 0m;
        }
        else if (Math.Sign(net) != Math.Sign(position.Net))
        {
            // Crossed through flat: the remainder is a brand-new position opened at this price.
            position.AverageCost = price;
        }

        // Partial close (sign unchanged): the average cost of what remains is untouched.
        position.Net = net;
    }

    private void ApplyToCash(Side side, decimal quantity, decimal price, InstrumentDefinition def, Money? charges)
    {
        var cash = GetOrCreateCash(def.Currency);
        var notional = quantity * price * def.Multiplier;

        cash.Balance += side == Side.Buy ? -notional : notional;

        if (charges is { } fee)
        {
            cash.Balance -= fee.Amount;
        }
    }

    // -----------------------------------------------------------------------------------
    // Pricing helpers
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The price a marketable order trades at: the far side of the touch when the feed carries
    /// one, the last print otherwise. Filling a buy at the BID would be a systematic free
    /// half-spread on every entry, which is the most common way a paper engine flatters a
    /// strategy.
    /// </summary>
    private static decimal? ReferencePrice(Side side, Tick tick) => side switch
    {
        Side.Buy => tick.AskPrice?.Amount ?? tick.LastPrice.Amount,
        _ => tick.BidPrice?.Amount ?? tick.LastPrice.Amount,
    };

    private decimal ApplySlippage(decimal reference, Side side, decimal tickSize)
    {
        var bps = reference * _options.Fills.SlippageBps / 10_000m;
        var ticks = _options.Fills.SlippageTicks * (tickSize > 0m ? tickSize : 0m);
        var adjustment = bps + ticks;

        // Always against the trader. Slippage that could help is not slippage.
        return side == Side.Buy ? reference + adjustment : Math.Max(0m, reference - adjustment);
    }

    /// <summary>
    /// The price a position is marked at: the engine's own last tick when it has one, the
    /// source's last price otherwise. Null when neither knows — deliberately, because a
    /// made-up mark flows straight into unrealised P&amp;L and into the balances the UI shows.
    /// </summary>
    private Money? MarkPrice(InstrumentKey instrument)
    {
        if (_lastTick.TryGetValue(instrument, out var tick))
        {
            return tick.LastPrice;
        }

        var fromSource = _source.LastPrice(instrument);
        return fromSource.IsSuccess ? fromSource.Value : null;
    }

    private Result<ChargesEstimate> ChargesFor(ChargeContext context)
    {
        foreach (var schedule in _options.ChargeSchedules)
        {
            if (schedule.Handles(context.Instrument.Venue))
            {
                return schedule.Estimate(context);
            }
        }

        // No schedule for this venue. Declining beats returning zero: a zero total is
        // indistinguishable from "this market is free", and a backtest would bank it.
        return Result<ChargesEstimate>.Failure(ConnectorErrors.NotSupported(
            $"charge estimation for {context.Instrument.Venue}"));
    }

    // -----------------------------------------------------------------------------------
    // Lifecycle plumbing
    // -----------------------------------------------------------------------------------

    private WorkingOrder Register(PlaceOrderRequest request, InstrumentDefinition def)
    {
        _sequence++;

        var order = new WorkingOrder
        {
            Sequence = _sequence,
            BrokerOrderId = "PAPER-" + _sequence.ToString("D8", CultureInfo.InvariantCulture),
            Request = request,
            Side = request.Side,
            Quantity = request.Quantity,
            OrderType = request.OrderType,
            TimeInForce = request.TimeInForce,
            LimitPrice = request.LimitPrice,
            TriggerPrice = request.TriggerPrice,
            DisclosedQuantity = request.DisclosedQuantity,
            PlacedAt = Now(),
            Status = OrderStatus.Open,
            // Market and limit orders are live from the moment they are accepted; everything
            // else waits for its trigger.
            Armed = request.OrderType is OrderType.Market or OrderType.Limit,
            Currency = def.Currency,
        };

        _bySequence[order.Sequence] = order;
        _byBrokerId[order.BrokerOrderId] = order;
        _byClientId[request.ClientOrderId] = order;

        return order;
    }

    /// <summary>
    /// IOC and FOK resolve at placement, not on the next tick: they are instructions about the
    /// instant of arrival. Leaving them resting would turn every IOC into a day order.
    /// </summary>
    private void ResolveImmediateTimeInForce(WorkingOrder order)
    {
        if (!order.Status.IsWorking())
        {
            return;
        }

        switch (order.TimeInForce)
        {
            case TimeInForce.Ioc:
                // A partially filled IOC is still Cancelled, not Filled: the fills stand and
                // the remainder dies. Reporting it as Filled would overstate the position.
                Terminate(
                    order,
                    OrderStatus.Cancelled,
                    order.Filled.Value > 0m
                        ? "Immediate-or-cancel: the unfilled remainder was cancelled."
                        : "Immediate-or-cancel: no liquidity was available at arrival.");
                break;

            case TimeInForce.Fok:
                Terminate(order, OrderStatus.Cancelled, "Fill-or-kill: the full quantity was not available.");
                break;

            default:
                break;
        }
    }

    private void Terminate(WorkingOrder order, OrderStatus status, string message)
    {
        order.Status = status;
        order.StatusMessage = message;
        order.UpdatedAt = Now();
        Emit(new StreamEvent.OrderUpdated(ToBrokerOrder(order)));
    }

    /// <summary>
    /// Expires good-till-date orders. Runs only when the market DATE advances rather than on
    /// every tick: a backtest processes millions of ticks and an O(orders) sweep on each one
    /// would dominate the run time while changing nothing.
    /// </summary>
    private void SweepExpired()
    {
        var now = Now();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (_lastExpirySweepDate == today)
        {
            return;
        }

        _lastExpirySweepDate = today;

        foreach (var order in _bySequence.Values.ToArray())
        {
            if (!order.Status.IsWorking())
            {
                continue;
            }

            if (order.TimeInForce == TimeInForce.Gtd
                && order.Request.GoodTillDate is { } until
                && today > until)
            {
                Terminate(order, OrderStatus.Expired, "Good-till-date reached.");
            }
        }

        PruneResting();
    }

    private void PruneResting()
    {
        // The one place a dictionary is enumerated. It is safe under the determinism rules
        // because removal is order-independent: no fill, event or returned list can differ
        // depending on which instrument's list was pruned first.
        foreach (var list in _resting.Values)
        {
            list.RemoveAll(sequence =>
                !_bySequence.TryGetValue(sequence, out var order) || !order.Status.IsWorking());
        }
    }

    private List<long> RestingList(InstrumentKey instrument)
    {
        if (!_resting.TryGetValue(instrument, out var list))
        {
            list = [];
            _resting[instrument] = list;
        }

        return list;
    }

    private static Result Validate(PlaceOrderRequest request, InstrumentDefinition def)
    {
        if (request.Quantity.Value <= 0m)
        {
            return new Error(ConnectorErrorCodes.InvalidRequest, "Quantity must be positive.");
        }

        if (!def.IsTradable)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"{request.Instrument} is not currently tradable.");
        }

        if (request.LimitPrice is { } limit && limit.Currency != def.Currency)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"{request.Instrument} trades in {def.Currency}; the limit price is in {limit.Currency}.");
        }

        if (request.TriggerPrice is { } trigger && trigger.Currency != def.Currency)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"{request.Instrument} trades in {def.Currency}; the trigger price is in {trigger.Currency}.");
        }

        // Lot size is enforced here rather than left to the venue, because a real venue
        // rejects it and a paper engine that accepted it would let a strategy trade sizes it
        // could never trade live.
        if (def.LotSize > 1m && request.Quantity.Value % def.LotSize != 0m)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"{request.Instrument} trades in lots of {def.LotSize.ToString(CultureInfo.InvariantCulture)}.");
        }

        return Result.Success();
    }

    private PositionState GetOrCreatePosition(
        InstrumentKey instrument,
        PositionEffect effect,
        InstrumentDefinition def)
    {
        var key = new PositionKey(instrument, effect);

        if (_positionIndex.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new PositionState
        {
            Instrument = instrument,
            Effect = effect,
            Currency = def.Currency,
            Multiplier = def.Multiplier,
            Isin = def.Isin,
        };

        _positions.Add(created);
        _positionIndex[key] = created;
        GetOrCreateCash(def.Currency);
        return created;
    }

    private CashState GetOrCreateCash(Currency currency)
    {
        if (_cash.TryGetValue(currency, out var existing))
        {
            return existing;
        }

        var created = new CashState { Balance = 0m };
        _cash[currency] = created;
        _currencies.Add(currency);
        return created;
    }

    private Result<InstrumentDefinition> ResolveDefinition(InstrumentKey instrument)
    {
        if (_definitions.TryGetValue(instrument, out var cached))
        {
            return cached;
        }

        var resolved = _source.Resolve(instrument);
        if (resolved.IsFailure)
        {
            return resolved;
        }

        _definitions[instrument] = resolved.Value;
        return resolved;
    }

    private static OrderType EffectiveType(WorkingOrder order) => order.OrderType switch
    {
        // An armed stop is a market order — that is what a stop IS, and modelling it as
        // anything gentler understates the risk it carries.
        OrderType.Stop or OrderType.MarketIfTouched or OrderType.TrailingStop => OrderType.Market,
        OrderType.StopLimit => OrderType.Limit,
        var other => other,
    };

    private static bool WithinDates(OrderQuery query, DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(at.UtcDateTime);

        if (query.From is { } from && date < from)
        {
            return false;
        }

        return query.To is not { } to || date <= to;
    }

    private BrokerOrder ToBrokerOrder(WorkingOrder order) => new()
    {
        BrokerOrderId = order.BrokerOrderId,
        ClientOrderId = order.Request.ClientOrderId,
        Instrument = order.Request.Instrument,
        Side = order.Side,
        Quantity = order.Quantity,
        FilledQuantity = order.Filled,
        Status = order.Status,
        OrderType = order.OrderType,
        PositionEffect = order.Request.PositionEffect,
        TimeInForce = order.TimeInForce,
        Variety = order.Request.Variety,
        LimitPrice = order.LimitPrice,
        TriggerPrice = order.TriggerPrice,
        AveragePrice = order.Filled.Value > 0m
            ? new Money(order.FilledNotional / order.Filled.Value, order.Currency)
            : null,
        PlacedAt = order.PlacedAt,
        UpdatedAt = order.UpdatedAt,
        StatusMessage = order.StatusMessage,
    };

    private Result<OrderAck> Ack(WorkingOrder order) => new OrderAck
    {
        BrokerOrderId = order.BrokerOrderId,
        Status = order.Status,
        ClientOrderId = order.Request.ClientOrderId,
        Message = order.StatusMessage,
        AcknowledgedAt = Now(),
    };

    private void Emit(StreamEvent @event)
    {
        // Called under _gate. TryWrite never blocks on a drop-oldest bounded channel, so
        // holding the lock costs nothing and buys a single global event ordering.
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryWrite(@event);
        }
    }

    private DateTimeOffset Now() => _marketTime ?? _clock.UtcNow;

    private void AdvanceTime(DateTimeOffset at)
    {
        // Monotonic: an out-of-order tick must not rewind the engine's clock, or a later order
        // could be stamped before an earlier one and the audit trail would be a lie.
        if (_marketTime is null || at > _marketTime.Value)
        {
            _marketTime = at;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private enum FillMode
    {
        /// <summary>Subject to the per-tick ratio cap. The normal path.</summary>
        Sliced,

        /// <summary>All or nothing. Fill-or-kill and at-the-open.</summary>
        Full,
    }

    private readonly record struct PositionKey(InstrumentKey Instrument, PositionEffect Effect);

    private sealed class CashState
    {
        public decimal Balance { get; set; }
    }

    private sealed class PositionState
    {
        public required InstrumentKey Instrument { get; init; }

        public required PositionEffect Effect { get; init; }

        public required Currency Currency { get; init; }

        public required decimal Multiplier { get; init; }

        public string? Isin { get; init; }

        /// <summary>Signed net size. Negative is short.</summary>
        public decimal Net { get; set; }

        /// <summary>Weighted average cost per unit of the CURRENT position only.</summary>
        public decimal AverageCost { get; set; }

        public decimal RealisedPnl { get; set; }

        public decimal BuyQuantity { get; set; }

        public decimal SellQuantity { get; set; }
    }

    private sealed class WorkingOrder
    {
        public required long Sequence { get; init; }

        public required string BrokerOrderId { get; init; }

        public required PlaceOrderRequest Request { get; init; }

        public required Side Side { get; init; }

        public required Currency Currency { get; init; }

        public required DateTimeOffset PlacedAt { get; init; }

        // Mutable because a modify changes them and because the engine fills into them.
        public Quantity Quantity { get; set; }

        public Quantity Filled { get; set; } = Quantity.Zero;

        public decimal FilledNotional { get; set; }

        public OrderType OrderType { get; set; }

        public TimeInForce TimeInForce { get; set; }

        public Money? LimitPrice { get; set; }

        public Money? TriggerPrice { get; set; }

        public Quantity? DisclosedQuantity { get; set; }

        public OrderStatus Status { get; set; }

        public string? StatusMessage { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary>False while a stop-family order is still waiting for its trigger.</summary>
        public bool Armed { get; set; }

        /// <summary>High-water (sell) or low-water (buy) mark for a trailing stop.</summary>
        public decimal? TrailWatermark { get; set; }

        /// <summary>Distance the trailing stop keeps from its watermark. Fixed at first sight of a price.</summary>
        public decimal? TrailOffset { get; set; }

        /// <summary>Ticks this order has been offered. Only at-the-open cares.</summary>
        public int TicksSeen { get; set; }
    }
}
