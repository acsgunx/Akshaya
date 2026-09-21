using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// A connector's instrument master, held once per PROCESS and loaded the first time something
/// needs it.
///
/// <b>Why this exists.</b> Connectors are request-scoped — <c>BrokerLinkResolver</c> builds one
/// per request and disposes it at the end — so an instrument cache created in a connector's
/// constructor starts empty on every request and dies with it. At the brokers that address charts
/// and the price socket by numeric token (mStock, Kite), every chart and every live-price
/// subscription therefore failed, and the error told the user to "load the script master", which
/// nothing in the product ever did. The "daily ingest job" the connectors were written to expect
/// does not exist.
///
/// <b>One copy per endpoint, not per account.</b> Every mStock account sees the same list of NSE
/// contracts, so the master is keyed by the broker endpoint and shared by every connector instance
/// that talks to it.
///
/// <b>Single-flight.</b> A chart, a price subscription and a symbol search arriving together on a
/// cold process produce one download, not three. Callers that arrive mid-load wait for it.
///
/// <b>Refreshed, and stale beats nothing.</b> Masters change once a day (new strikes, yesterday's
/// expiries gone), so a load older than <see cref="SharedInstrumentMaster.RefreshInterval"/> is redone on next use — the
/// same twelve hours the platform's own search index uses. If that refresh fails, the previous
/// master keeps being served: yesterday's token for RELIANCE is still RELIANCE's token.
///
/// <b>A failure is remembered briefly.</b> When the broker refuses the download (an expired key,
/// an unregistered IP), every chart request would otherwise re-download and re-fail against a data
/// rate limit of about one request a second. For <see cref="SharedInstrumentMaster.FailureCooldown"/> the last failure is
/// returned without asking again.
///
/// The load itself is the connector's: it downloads, parses and swaps the new rows into
/// <see cref="Cache"/> in one step. This type only decides WHEN a load runs and makes sure only
/// one runs at a time.
/// </summary>
/// <typeparam name="TCache">The connector's own cache type.</typeparam>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Instances live for the whole process and are never released, so there is no owner "
        + "to call Dispose. The semaphore never allocates a wait handle, so nothing leaks.")]
public sealed class SharedInstrumentMaster<TCache>
    where TCache : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Published by reference swap, so readers never see a torn pair of timestamps.</summary>
    private volatile LoadState _state = LoadState.Never;

    internal SharedInstrumentMaster(TCache cache)
    {
        Cache = cache;
    }

    /// <summary>The shared cache. Lives for the process; connectors must never dispose it.</summary>
    public TCache Cache { get; }

    /// <summary>When the master last loaded successfully, or null if it never has.</summary>
    public DateTimeOffset? LoadedAt => _state.LoadedAt;

    /// <summary>
    /// Makes sure the master is loaded and not stale, running <paramref name="load"/> if it is not.
    ///
    /// The load runs on the CALLER's cancellation token, because it uses the caller's connector —
    /// its HTTP client and session — and that connector is disposed when the caller's request ends.
    /// A cancelled load is not recorded as a failure; the next caller simply tries again.
    /// </summary>
    /// <param name="load">Downloads the master and swaps it into <see cref="Cache"/>. Must replace,
    /// never append: the cache outlives the load and a second load would otherwise double it.</param>
    /// <param name="clock">The caller's clock.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async Task<Result> EnsureLoadedAsync(
        Func<CancellationToken, Task<Result>> load,
        IClock clock,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(clock);

        var current = _state;
        if (Answer(current, clock.UtcNow) is { } fast)
        {
            return fast;
        }

        if (current.LoadedAt is not null)
        {
            // A stale copy is a good answer for everyone except the one caller refreshing it. A
            // chart should not wait for a twenty-second download to learn tomorrow's strikes.
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return Result.Success();
            }
        }
        else
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            // Re-check under the gate: whoever held it before us may have just finished the very
            // load we were about to start.
            if (Answer(_state, clock.UtcNow) is { } settled)
            {
                return settled;
            }

            Result result;
            try
            {
                result = await load(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The loaders return Results, but they also read a multi-megabyte body off the
                // network, and a connection dropped halfway through surfaces as an IOException.
                result = Result.Failure(new Error(
                    ConnectorErrorCodes.BrokerUnavailable,
                    "The broker's instrument list could not be downloaded.",
                    ex.GetType().Name,
                    ex.Message));
            }

            var now = clock.UtcNow;
            var previous = _state;

            if (result.IsSuccess)
            {
                _state = new LoadState(now, FailedAt: null, Failure: null);
                return Result.Success();
            }

            _state = previous with { FailedAt = now, Failure = result.Error };

            // A failed REFRESH is not the same as never having loaded. Yesterday's list is what
            // the trader wants; the failure is still remembered, so it is not retried every call.
            return previous.LoadedAt is not null ? Result.Success() : result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The answer without loading, or null when a load has to run.</summary>
    private static Result? Answer(LoadState state, DateTimeOffset now)
    {
        if (state.LoadedAt is { } loadedAt && now - loadedAt < SharedInstrumentMaster.RefreshInterval)
        {
            return Result.Success();
        }

        if (state.FailedAt is { } failedAt && state.Failure is { } failure && now - failedAt < SharedInstrumentMaster.FailureCooldown)
        {
            return state.LoadedAt is not null ? Result.Success() : Result.Failure(failure);
        }

        return null;
    }

    private sealed record LoadState(DateTimeOffset? LoadedAt, DateTimeOffset? FailedAt, Error? Failure)
    {
        public static readonly LoadState Never = new(null, null, null);
    }
}

/// <summary>
/// The registry of <see cref="SharedInstrumentMaster{TCache}"/> instances, one per connector cache
/// type and broker endpoint.
/// </summary>
public static class SharedInstrumentMaster
{
    private static readonly ConcurrentDictionary<(Type Cache, string Endpoint), Lazy<object>> Instances = new();

    /// <summary>How long a loaded master is used before the next caller reloads it.</summary>
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromHours(12);

    /// <summary>How long a failed load is reported without retrying the download.</summary>
    public static TimeSpan FailureCooldown { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The process-wide master for one broker endpoint, created empty on first request.
    /// </summary>
    /// <typeparam name="TCache">The connector's own cache type. It is part of the key, so two
    /// connectors can never collide on an endpoint string.</typeparam>
    /// <param name="endpoint">Identifies the master's source — normally the broker's base URL, so a
    /// sandbox and production never share a list.</param>
    /// <param name="create">Builds the empty cache. Called at most once per endpoint.</param>
    public static SharedInstrumentMaster<TCache> For<TCache>(string endpoint, Func<TCache> create)
        where TCache : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(create);

        // Lazy, because GetOrAdd may run its factory twice under a race and the loser's cache would
        // be a second, never-loaded copy that some connector instance might have picked up.
        var entry = Instances.GetOrAdd(
            (typeof(TCache), endpoint.ToUpperInvariant()),
            _ => new Lazy<object>(
                () => new SharedInstrumentMaster<TCache>(create()),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return (SharedInstrumentMaster<TCache>)entry.Value;
    }
}
