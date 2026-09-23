using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akshaya.Modules.MarketData;

/// <summary>Streams a connector's full instrument master. Supplied by the caller, who owns the connector.</summary>
/// <remarks>
/// A delegate rather than an interface, and passed per call rather than injected, because of
/// who owns the connector's lifetime: connectors are REQUEST-SCOPED and disposed by the
/// endpoint that resolved them. A port this module could call whenever it liked would need a
/// connector of its own, which means a broker session of its own, which means this module
/// would have to know about broker links. It does not, and should not.
/// </remarks>
public delegate IAsyncEnumerable<InstrumentDefinition> InstrumentLoader(CancellationToken ct);

/// <summary>
/// The process-wide instrument master, one searchable snapshot per connector id.
///
/// PER CONNECTOR, NOT PER LINK OR PER USER: every mStock account sees the same list of NSE
/// contracts, so keying by broker link would multiply an expensive download by the number of
/// linked accounts to no purpose whatsoever.
///
/// Loads are SINGLE-FLIGHT. Ten watchlist keystrokes arriving while the master is still
/// downloading must produce one download, not ten — this is the entire reason the type holds a
/// gate per connector rather than being a plain <c>ConcurrentDictionary</c>.
/// </summary>
public sealed class InstrumentMaster : IDisposable
{
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _slotsGate = new();
    private readonly InstrumentMasterOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<InstrumentMaster> _logger;
    private bool _disposed;

    /// <summary>Creates the master.</summary>
    public InstrumentMaster(
        IOptions<InstrumentMasterOptions> options,
        IClock clock,
        ILogger<InstrumentMaster> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns a fresh snapshot if one is already in memory, WITHOUT loading.
    ///
    /// This is what lets the common case — a search box on its fourth keystroke — skip
    /// activating a connector at all: no session decrypt, no decorator chain, no broker call.
    /// </summary>
    public bool TryGetFresh(string connectorId, out InstrumentSearchIndex index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        Slot? slot;
        lock (_slotsGate)
        {
            _slots.TryGetValue(connectorId, out slot);
        }

        var current = slot?.Current;
        if (current is not null && IsFresh(current))
        {
            index = current.Index;
            return true;
        }

        index = null!;
        return false;
    }

    /// <summary>
    /// Returns a fresh snapshot, loading it through <paramref name="load"/> if necessary.
    ///
    /// The load runs on the CALLER's cancellation token and, for the first caller, inside the
    /// caller's request — because the connector the loader reads from belongs to that request
    /// and dies with it. Callers that arrive while a load is in flight wait for it rather than
    /// starting their own.
    /// </summary>
    public async Task<Result<InstrumentSearchIndex>> GetOrLoadAsync(
        string connectorId,
        InstrumentLoader load,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentNullException.ThrowIfNull(load);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = GetSlot(connectorId);

        if (slot.Current is { } cached && IsFresh(cached))
        {
            return cached.Index;
        }

        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: whoever held it before us may have just loaded the very
            // snapshot we were about to download again.
            if (slot.Current is { } loaded && IsFresh(loaded))
            {
                return loaded.Index;
            }

            var result = await LoadAsync(connectorId, load, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                slot.Current = new Snapshot(result.Value, _clock.UtcNow);
                return result.Value;
            }

            // A failed REFRESH is not the same as never having loaded at all. If we are
            // holding yesterday's list, yesterday's list is what the trader wants to search.
            if (_options.ServeStaleOnRefreshFailure && slot.Current is { } stale)
            {
                _logger.LogWarning(
                    "Refreshing the instrument master for {ConnectorId} failed ({Error}); serving the snapshot loaded at {AsOf}.",
                    connectorId,
                    result.Error.Code,
                    stale.Index.AsOf);

                return stale.Index;
            }

            return Result<InstrumentSearchIndex>.Failure(result.Error);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// The snapshot to SERVE right now, even when it has aged out, saying whether a refresh is
    /// due. Returns false only when nothing usable is held at all.
    ///
    /// This is the read half of stale-while-revalidate: it is what lets the caller that
    /// happens to arrive first after the refresh interval elapsed get an answer immediately
    /// instead of being the one who pays for the whole download. Pair it with
    /// <see cref="RefreshInBackground"/>.
    /// </summary>
    /// <param name="connectorId">Which connector's master.</param>
    /// <param name="index">The snapshot to serve, fresh or stale.</param>
    /// <param name="refreshDue">True when the snapshot has aged out and a reload should start.</param>
    public bool TryGetForServing(string connectorId, out InstrumentSearchIndex index, out bool refreshDue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        Slot? slot;
        lock (_slotsGate)
        {
            _slots.TryGetValue(connectorId, out slot);
        }

        if (slot?.Current is not { } current)
        {
            index = null!;
            refreshDue = true;
            return false;
        }

        if (IsFresh(current))
        {
            index = current.Index;
            refreshDue = false;
            return true;
        }

        // Aged out. Serveable only while the operator allows it and only up to MaxStaleAge —
        // past that a stale list is misinformation rather than a stand-in, and the caller
        // waits for a real one.
        var age = _clock.UtcNow - current.LoadedAt;
        if (_options.ServeStaleWhileRefreshing && age <= _options.MaxStaleAge)
        {
            index = current.Index;
            refreshDue = true;
            return true;
        }

        index = null!;
        refreshDue = true;
        return false;
    }

    /// <summary>
    /// Starts at most ONE background reload for this connector and returns immediately.
    ///
    /// The caller supplies a loader that owns everything it needs for the whole download —
    /// crucially including the connector, which is why <paramref name="cleanup"/> exists: the
    /// request that triggered this refresh returns long before the download finishes, so the
    /// connector must NOT be the request's own <c>await using</c> one. Cleanup runs however the
    /// load ends.
    ///
    /// Returns false when a load for this connector is already running, which is the whole
    /// point: a hundred keystrokes arriving after the interval elapsed must start one download.
    /// </summary>
    public bool RefreshInBackground(string connectorId, InstrumentLoader load, Func<ValueTask>? cleanup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentNullException.ThrowIfNull(load);

        if (_disposed)
        {
            return false;
        }

        var slot = GetSlot(connectorId);

        // Non-blocking: if the gate is taken, somebody is already downloading this master and
        // there is nothing useful for us to add.
        if (!slot.Gate.Wait(0))
        {
            return false;
        }

        _ = RunBackgroundRefreshAsync(connectorId, slot, load, cleanup);
        return true;
    }

    /// <summary>Owns the gate it was handed and always releases it. Never throws to its caller.</summary>
    private async Task RunBackgroundRefreshAsync(
        string connectorId,
        Slot slot,
        InstrumentLoader load,
        Func<ValueTask>? cleanup)
    {
        // Yield first so the request thread that started this returns its response immediately
        // rather than running the first synchronous stretch of the download.
        await Task.Yield();

        try
        {
            // CancellationToken.None on purpose. This load is deliberately detached from the
            // request that noticed the snapshot was stale — that request has already been
            // answered from the stale copy, and cancelling the download when it completes
            // would mean the refresh never happens at all.
            var result = await LoadAsync(connectorId, load, CancellationToken.None).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                slot.Current = new Snapshot(result.Value, _clock.UtcNow);
            }
            else
            {
                // The stale snapshot stays exactly where it is and keeps being served. A failed
                // background refresh must be invisible to traders and visible in the log.
                _logger.LogWarning(
                    "Background refresh of the instrument master for {ConnectorId} failed ({Error}); the previous snapshot stands.",
                    connectorId,
                    result.Error.Code);
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception would be an unobserved one.
            _logger.LogError(ex, "Background refresh of the instrument master for {ConnectorId} threw.", connectorId);
        }
        finally
        {
            slot.Gate.Release();

            if (cleanup is not null)
            {
                try
                {
                    await cleanup().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cleaning up after the {ConnectorId} instrument-master refresh threw.", connectorId);
                }
            }
        }
    }

    /// <summary>Drops a connector's snapshot, forcing the next caller to reload it.</summary>
    public void Invalidate(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        lock (_slotsGate)
        {
            if (_slots.TryGetValue(connectorId, out var slot))
            {
                slot.Current = null;
            }
        }
    }

    private async Task<Result<InstrumentSearchIndex>> LoadAsync(
        string connectorId,
        InstrumentLoader load,
        CancellationToken ct)
    {
        var startedAt = _clock.UtcNow;
        var instruments = new List<InstrumentDefinition>(capacity: 8192);

        try
        {
            await foreach (var instrument in load(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                instruments.Add(instrument);

                if (instruments.Count > _options.MaxInstruments)
                {
                    return new Error(
                        ConnectorErrorCodes.BrokerUnavailable,
                        $"The instrument master for '{connectorId}' exceeded {_options.MaxInstruments} rows and was abandoned.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away — their connector is being disposed underneath us. Not a
            // broker failure, and it must propagate as itself.
            throw;
        }
        catch (Exception ex)
        {
            // `GetInstrumentsAsync` has no failure channel — it is a bare IAsyncEnumerable —
            // so connectors signal a download failure by throwing. This is the boundary where
            // that becomes a Result again.
            _logger.LogError(ex, "Loading the instrument master for {ConnectorId} threw.", connectorId);

            return new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                $"The instrument list for '{connectorId}' could not be read.");
        }

        if (instruments.Count == 0)
        {
            // An empty master is treated as a failure, never cached as "this broker lists
            // nothing". Caching it would leave search silently broken until the next refresh.
            return new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                $"'{connectorId}' returned an empty instrument list.");
        }

        var index = InstrumentSearchIndex.Build(instruments, startedAt);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Loaded {Count} instruments for {ConnectorId} in {ElapsedMs}ms.",
                index.Count,
                connectorId,
                (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
        }

        return index;
    }

    private Slot GetSlot(string connectorId)
    {
        lock (_slotsGate)
        {
            if (!_slots.TryGetValue(connectorId, out var slot))
            {
                slot = new Slot();
                _slots[connectorId] = slot;
            }

            return slot;
        }
    }

    private bool IsFresh(Snapshot snapshot) =>
        _clock.UtcNow - snapshot.LoadedAt < _options.RefreshInterval;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_slotsGate)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Gate.Dispose();
            }

            _slots.Clear();
        }
    }

    /// <summary>One connector's snapshot plus the gate that serialises its loads.</summary>
    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Published by reference swap; readers never see a partially built index.</summary>
        public Snapshot? Current { get; set; }
    }

    private sealed record Snapshot(InstrumentSearchIndex Index, DateTimeOffset LoadedAt);
}
