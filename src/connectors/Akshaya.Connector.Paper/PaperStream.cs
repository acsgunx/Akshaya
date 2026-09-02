using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// The Paper connector's live feed: ticks, order updates and executions, exactly as a real
/// broker socket delivers them on one connection.
///
/// <b>Why order and trade events come down the same pipe as prices.</b> Because that is what
/// real brokers do, and a paper connector that split them would let a strategy be written
/// against an event ordering that does not exist upstream. The engine publishes trade-then-order
/// for every fill, and this class preserves that ordering all the way to the consumer.
///
/// <b>Exactly one upstream subscription, ever.</b> This class holds at most one
/// <see cref="MatchingEngine.Subscribe"/> handle at a time, and
/// <see cref="DisconnectAsync"/> always releases it. Reconnecting does not accumulate handles.
/// That is not a micro-optimisation: a leaked upstream subscription on a real broker is a
/// socket that keeps consuming the account's subscription quota and eventually gets the whole
/// connection dropped, and it is invisible until it is not. The conformance suite exercises
/// subscribe → unsubscribe → reconnect for exactly this reason, and
/// <see cref="UpstreamSubscriptions"/> is exposed so this connector's own tests can assert it
/// directly.
/// </summary>
public sealed class PaperStream : IConnectorStream, IAsyncDisposable
{
    /// <summary>
    /// Outbound buffer. Bounded, drop-oldest: the contract says a consumer must never
    /// back-pressure ingest, and a stale tick is the cheapest thing in the system to lose.
    /// </summary>
    private const int OutboundCapacity = 8192;

    private readonly MatchingEngine _engine;
    private readonly int _maxSubscriptions;
    private readonly Lock _gate = new();

    /// <summary>The subscription set we want. Insertion order is irrelevant here — nothing iterates it.</summary>
    private readonly Dictionary<InstrumentKey, StreamMode> _subscriptions = [];

    private readonly Channel<StreamEvent> _outbound = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

    private ChannelReader<StreamEvent>? _upstream;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private int _upstreamSubscriptions;
    private bool _disposed;

    /// <summary>Creates the streaming facet.</summary>
    /// <param name="engine">The simulated venue whose events are forwarded.</param>
    /// <param name="maxSubscriptions">
    /// The manifest's declared cap. Enforced here rather than trusted, so a fan-out bug shows
    /// up as a refused subscription in paper trading instead of a dropped socket in production.
    /// </param>
    public PaperStream(MatchingEngine engine, int maxSubscriptions)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _engine = engine;
        _maxSubscriptions = maxSubscriptions > 0 ? maxSubscriptions : int.MaxValue;
    }

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>
    /// Handles currently held on the engine's event feed. Must be 0 or 1 at all times; any
    /// other value is a leak. Diagnostic surface, not part of the contract.
    /// </summary>
    public int UpstreamSubscriptions => Volatile.Read(ref _upstreamSubscriptions);

    /// <summary>Instruments currently subscribed.</summary>
    public int SubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(Result.Failure(new Error(
                    ConnectorErrorCodes.BrokerUnavailable,
                    "This paper stream has been disposed.")));
            }

            if (_pump is { IsCompleted: false })
            {
                // Connecting an already-connected stream is a no-op success, not an error.
                // The host's reconnect supervisor calls this speculatively.
                return Task.FromResult(Result.Success());
            }

            State = StreamState.Connecting;

            var upstream = _engine.Subscribe();
            _upstream = upstream;
            Interlocked.Increment(ref _upstreamSubscriptions);

            var cts = new CancellationTokenSource();
            _cts = cts;
            _pump = Task.Run(() => PumpAsync(upstream, cts.Token), CancellationToken.None);

            State = StreamState.Connected;
            _outbound.Writer.TryWrite(new StreamEvent.ConnectionChanged(StreamState.Connected));

            return Task.FromResult(Result.Success());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Releases the upstream handle before returning. The subscription SET survives, so a
    /// reconnect restores what the user asked for — a stream that came back connected and
    /// silent because it forgot its subscriptions looks healthy and is worse than being down.
    /// </remarks>
    public async Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Task? pump;
        ChannelReader<StreamEvent>? upstream;

        lock (_gate)
        {
            cts = _cts;
            pump = _pump;
            upstream = _upstream;
            _cts = null;
            _pump = null;
            _upstream = null;
        }

        if (upstream is not null)
        {
            _engine.Unsubscribe(upstream);
            Interlocked.Decrement(ref _upstreamSubscriptions);
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the pump is how a disconnect works.
            }
        }

        lock (_gate)
        {
            State = StreamState.Disconnected;
        }

        _outbound.Writer.TryWrite(new StreamEvent.ConnectionChanged(StreamState.Disconnected));
        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Connects on demand. Most broker sockets behave this way and a caller that has to
    /// remember to connect first will eventually forget on the one path that matters.
    /// </remarks>
    public async Task<Result> SubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        if (instruments.Count == 0)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "Subscribe was called with no instruments."));
        }

        lock (_gate)
        {
            // Count the union, not the increment: re-subscribing an existing instrument at a
            // richer mode must not consume a second slot.
            var projected = _subscriptions.Count;
            foreach (var instrument in instruments)
            {
                if (!_subscriptions.ContainsKey(instrument))
                {
                    projected++;
                }
            }

            if (projected > _maxSubscriptions)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"This connector declares a cap of {_maxSubscriptions} streaming subscriptions; "
                    + $"the request would need {projected}."));
            }

            foreach (var instrument in instruments)
            {
                _subscriptions[instrument] = mode;
            }
        }

        var connect = await ConnectAsync(ct);
        return connect.IsFailure ? connect : Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>Removing something that was never subscribed succeeds: the desired end state holds.</remarks>
    public Task<Result> UnsubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        lock (_gate)
        {
            foreach (var instrument in instruments)
            {
                _subscriptions.Remove(instrument);
            }
        }

        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Lead with the current state so a consumer that renders a stale-data banner from
        // ConnectionChanged is correct from its first frame rather than from the first change.
        StreamState initial;
        lock (_gate)
        {
            initial = State;
        }

        yield return new StreamEvent.ConnectionChanged(initial);

        await foreach (var @event in _outbound.Reader.ReadAllAsync(ct))
        {
            yield return @event;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await DisconnectAsync();
        _outbound.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Forwards engine events, filtering ticks to the subscription set. Order and trade events
    /// are never filtered: a fill on an instrument the user has since unsubscribed from is
    /// still their fill, and dropping it would strand the order in the UI.
    /// </summary>
    private async Task PumpAsync(ChannelReader<StreamEvent> upstream, CancellationToken ct)
    {
        try
        {
            await foreach (var @event in upstream.ReadAllAsync(ct))
            {
                if (@event is StreamEvent.TickReceived tick)
                {
                    StreamMode mode;
                    lock (_gate)
                    {
                        if (!_subscriptions.TryGetValue(tick.Tick.Instrument, out mode))
                        {
                            continue;
                        }
                    }

                    _outbound.Writer.TryWrite(@event);

                    // Full mode gets the level-1 book alongside the print, which is what the
                    // manifest's depthLevels: 1 promises. Ltp and Quote do not, so a
                    // watchlist subscription does not pay for depth it will not render.
                    if (mode == StreamMode.Full)
                    {
                        var depth = BuildDepth(tick.Tick);
                        if (depth is not null)
                        {
                            _outbound.Writer.TryWrite(new StreamEvent.DepthReceived(depth));
                        }
                    }

                    continue;
                }

                _outbound.Writer.TryWrite(@event);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect. Not a failure.
        }
    }

    private static MarketDepth? BuildDepth(Tick tick)
    {
        if (tick.BidPrice is null && tick.AskPrice is null)
        {
            return null;
        }

        var bids = new List<DepthLevel>(1);
        if (tick.BidPrice is { } bid)
        {
            // Tick carries no size; zero says "size not reported" rather than inventing one.
            bids.Add(new DepthLevel(bid, Quantity.Zero));
        }

        var asks = new List<DepthLevel>(1);
        if (tick.AskPrice is { } ask)
        {
            asks.Add(new DepthLevel(ask, Quantity.Zero));
        }

        return new MarketDepth
        {
            Instrument = tick.Instrument,
            Bids = bids,
            Asks = asks,
            Timestamp = tick.Timestamp,
        };
    }
}
