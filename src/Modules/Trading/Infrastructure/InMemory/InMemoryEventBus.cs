using System.Collections.Concurrent;
using Akshaya.Modules.Trading.Ports;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Infrastructure.InMemory;

/// <summary>
/// DEVELOPMENT ONLY. An in-process event bus.
///
/// PHASE 5 replaces this with a durable outbox plus a real broker, so that an event survives
/// the process that raised it. Until then:
///
///  * DELIVERY IS BEST EFFORT. Nothing is persisted; a subscriber that is down misses events
///    permanently.
///  * PUBLISHING AWAITS ITS SUBSCRIBERS. Handlers run inline on the publisher's task, so a slow
///    handler slows the order that raised the event. Real subscribers must be fast and must not
///    do I/O of their own.
///
/// What it does get right, and what the durable version must keep: A HANDLER THAT THROWS IS
/// ISOLATED. One broken dashboard subscriber must never fail the order that raised the event.
/// </summary>
public sealed class InMemoryEventBus(ILogger<InMemoryEventBus> logger) : IEventBus
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.Values)
        {
            try
            {
                await handler(domainEvent, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "A subscriber to {EventType} threw; the event is considered delivered to the others.",
                    typeof(TEvent).Name);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var bucket = _handlers.GetOrAdd(typeof(TEvent), _ => new ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>());
        var id = Guid.NewGuid();
        bucket[id] = (e, ct) => handler((TEvent)e, ct);

        return new Subscription(() => bucket.TryRemove(id, out _));
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
