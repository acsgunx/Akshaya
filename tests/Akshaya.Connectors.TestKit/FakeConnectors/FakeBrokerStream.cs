using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit.FakeConnectors;

/// <summary>
/// A stand-in broker socket that counts what it opened.
///
/// The only interesting thing it does is <see cref="UpstreamSubscriptions"/>. A real broker
/// socket that leaks a subscription per reconnect looks completely healthy — data flows, the
/// UI is green — right up until the account hits the broker's subscription quota and the whole
/// connection is dropped, usually days later and usually during a busy session. Nothing about
/// the leak is observable through <see cref="IConnectorStream"/>, so the conformance suite
/// cannot catch it from the outside; the connector has to expose a count, and this is how a
/// connector author is shown to do it.
/// </summary>
public sealed class FakeBrokerStream : IConnectorStream, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<InstrumentKey> _subscriptions = [];
    private readonly int _maxSubscriptions;

    private readonly Channel<StreamEvent> _events = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

    private int _upstreamSubscriptions;
    private bool _connected;

    /// <summary>Creates the stream.</summary>
    /// <param name="maxSubscriptions">The manifest's declared cap, enforced rather than trusted.</param>
    public FakeBrokerStream(int maxSubscriptions) => _maxSubscriptions = maxSubscriptions;

    /// <inheritdoc />
    public StreamState State { get; private set; } = StreamState.Disconnected;

    /// <summary>
    /// Upstream connections currently held. Must be 0 or 1 at all times; anything else is a
    /// leak. The conformance suite reads this after subscribe → unsubscribe → reconnect.
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
            if (_connected)
            {
                // Idempotent. A supervisor that calls Connect speculatively must not open a
                // second socket — which is one of the two ways the leak above happens.
                return Task.FromResult(Result.Success());
            }

            _connected = true;
            Interlocked.Increment(ref _upstreamSubscriptions);
            State = StreamState.Connected;
        }

        _events.Writer.TryWrite(new StreamEvent.ConnectionChanged(StreamState.Connected));
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_connected)
            {
                _connected = false;
                Interlocked.Decrement(ref _upstreamSubscriptions);
            }

            State = StreamState.Disconnected;
        }

        _events.Writer.TryWrite(new StreamEvent.ConnectionChanged(StreamState.Disconnected));
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var connect = await ConnectAsync(ct);
        if (connect.IsFailure)
        {
            return connect;
        }

        lock (_gate)
        {
            var projected = _subscriptions.Count;
            foreach (var instrument in instruments)
            {
                if (!_subscriptions.Contains(instrument))
                {
                    projected++;
                }
            }

            if (projected > _maxSubscriptions)
            {
                return Result.Failure(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"This broker caps streaming subscriptions at {_maxSubscriptions}."));
            }

            foreach (var instrument in instruments)
            {
                _subscriptions.Add(instrument);
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
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

    /// <summary>Pushes an event to whoever is enumerating <see cref="Events"/>. Test-only.</summary>
    public void Publish(StreamEvent @event) => _events.Writer.TryWrite(@event);

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamState initial;
        lock (_gate)
        {
            initial = State;
        }

        yield return new StreamEvent.ConnectionChanged(initial);

        await foreach (var @event in _events.Reader.ReadAllAsync(ct))
        {
            yield return @event;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _events.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }
}
