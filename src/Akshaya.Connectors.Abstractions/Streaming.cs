using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

public sealed record Tick
{
    public required InstrumentKey Instrument { get; init; }

    public required Money LastPrice { get; init; }

    public Quantity? LastQuantity { get; init; }

    public long? Volume { get; init; }

    public Money? BidPrice { get; init; }

    public Money? AskPrice { get; init; }

    public Money? Open { get; init; }

    public Money? High { get; init; }

    public Money? Low { get; init; }

    public Money? PreviousClose { get; init; }

    public long? OpenInterest { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Everything a broker socket can push. Order and execution updates are in here on purpose:
/// most brokers deliver fills on the same connection as prices, and modelling them as two
/// separate streams would mean either two sockets or a leaky abstraction.
/// </summary>
public abstract record StreamEvent
{
    public sealed record TickReceived(Tick Tick) : StreamEvent;

    public sealed record DepthReceived(MarketDepth Depth) : StreamEvent;

    /// <summary>A fill, cancel or rejection pushed by the broker. Faster than polling; still reconciled.</summary>
    public sealed record OrderUpdated(BrokerOrder Order) : StreamEvent;

    public sealed record TradeExecuted(BrokerTrade Trade) : StreamEvent;

    /// <summary>Connection state changed. The UI surfaces this directly as the stale-data banner.</summary>
    public sealed record ConnectionChanged(StreamState State, string? Reason = null) : StreamEvent;
}

public enum StreamState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>Connected but behind, or partially subscribed. The user must be told.</summary>
    Degraded,

    Reconnecting,
}

public interface IConnectorStream
{
    StreamState State { get; }

    Task<Result> ConnectAsync(CancellationToken ct = default);

    Task<Result> DisconnectAsync(CancellationToken ct = default);

    Task<Result> SubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        CancellationToken ct = default);

    Task<Result> UnsubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default);

    /// <summary>
    /// The event stream. Consumers must not block on it — the fan-out layer conflates and
    /// drops; back-pressure here would stall ingest for every user on the connection.
    /// </summary>
    IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default);
}
