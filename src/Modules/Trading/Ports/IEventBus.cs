namespace Akshaya.Modules.Trading.Ports;

/// <summary>
/// In-process publish/subscribe for domain events.
///
/// Deliberately fire-and-forget from the publisher's point of view: an order handler must
/// never wait on a dashboard push or an audit write to finish before it returns to the trader.
/// Handlers that throw are isolated by the implementation — one bad subscriber must not fail
/// the order that raised the event.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : notnull;

    /// <summary>Dispose the returned handle to unsubscribe. Handlers must not throw; if they do, they are logged and ignored.</summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : notnull;
}
