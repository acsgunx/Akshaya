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

    /// <summary>One gate per link, so a build for one broker never blocks a build for another.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

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
    /// The cached snapshot, or ONE build shared by every caller that arrives while it runs.
    ///
    /// WHY SINGLE-FLIGHT AND NOT JUST A CACHE. A plain check-then-build races: ten orders sent
    /// together on a cold entry all miss, all build, and all make their own positions and
    /// balances calls — ten round trips and ten rate-limit permits spent answering one
    /// question. The lost permits are the worse half, because they come out of the same bucket
    /// the trader's own quotes draw on. Under the gate the check is repeated, so the nine that
    /// queued behind the first take its answer instead of repeating its work.
    ///
    /// A PARTIAL snapshot is cached like any other, exactly as the un-gated version did. It is
    /// tempting to skip it so a recovered broker is noticed sooner, but the failure that
    /// produces a partial is usually a timeout: not caching it would make every subsequent
    /// order wait out its own timeout, serialised behind this gate, which is far worse than
    /// judging on a few seconds of known-partial data that the rules already fail closed on.
    /// </summary>
    public async Task<RiskSnapshot> GetOrBuildAsync(
        string brokerLinkId,
        DateTimeOffset now,
        Func<CancellationToken, Task<RiskSnapshot>> build,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentNullException.ThrowIfNull(build);

        if (TryGet(brokerLinkId, now, out var cached))
        {
            return cached;
        }

        var gate = _gates.GetOrAdd(brokerLinkId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: the caller ahead of us has very likely just published
            // the snapshot we were about to fetch again.
            if (TryGet(brokerLinkId, now, out var justBuilt))
            {
                return justBuilt;
            }

            var snapshot = await build(ct).ConfigureAwait(false);
            Set(brokerLinkId, now, snapshot);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops a link's snapshot. Called after anything that changes exposure, so the next order
    /// judges against fresh numbers rather than a five-second-old position count.
    /// </summary>
    public void Invalidate(string brokerLinkId) => _entries.TryRemove(brokerLinkId, out _);
}
