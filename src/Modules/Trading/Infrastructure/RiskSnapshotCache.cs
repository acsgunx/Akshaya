using System.Collections.Concurrent;
using Akshaya.Modules.Trading.Ports;

namespace Akshaya.Modules.Trading.Infrastructure;

/// <summary>
/// A tiny, process-wide cache of <see cref="RiskSnapshot"/> keyed by broker link.
///
/// It is a SINGLETON deliberately, while its consumer
/// <see cref="ConnectorRiskSnapshotProvider"/> is request-scoped. The whole value of this cache
/// is across requests — a burst of orders on one link should make one broker call, not one per
/// order — and a cache living inside a scoped service would be discarded before it was ever
/// read a second time.
/// </summary>
public sealed class RiskSnapshotCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RiskSnapshot Snapshot)> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// How long a snapshot is reused. Short enough that a closed position disappears from the
    /// count within a few orders, long enough that a burst of orders makes one broker call.
    /// A position COUNT tolerates seconds of staleness; an extra broker round trip on the order
    /// path does not.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);

    public bool TryGet(string brokerLinkId, DateTimeOffset now, out RiskSnapshot snapshot)
    {
        if (_entries.TryGetValue(brokerLinkId, out var entry) && now - entry.At < Duration)
        {
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = RiskSnapshot.Empty;
        return false;
    }

    public void Set(string brokerLinkId, DateTimeOffset now, RiskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _entries[brokerLinkId] = (now, snapshot);
    }

    /// <summary>
    /// Drops a link's snapshot. Called after anything that changes exposure, so the next order
    /// judges against fresh numbers rather than a five-second-old position count.
    /// </summary>
    public void Invalidate(string brokerLinkId) => _entries.TryRemove(brokerLinkId, out _);
}
