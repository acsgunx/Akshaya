using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.RateLimiting;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// The scope vocabulary <see cref="RateLimitSpec.Scope"/> may use. Closed on purpose: a
/// manifest that invents a scope name would declare a limit nothing enforces, which is worse
/// than declaring none. <see cref="ManifestLoader"/> rejects unknown scopes.
/// </summary>
public static class RateLimitScopes
{
    /// <summary>Order placement, modification, cancellation. Nearly always the tightest bucket.</summary>
    public const string Orders = "orders";

    /// <summary>Portfolio, order book, reference data — everything read that is not a price.</summary>
    public const string Data = "data";

    /// <summary>Quote and LTP endpoints, which brokers meter separately and generously.</summary>
    public const string Quotes = "quotes";

    /// <summary>Applies on top of every other bucket. A broker's overall cap for the credential.</summary>
    public const string Global = "global";

    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Orders, Data, Quotes, Global };

    public static bool IsKnown(string scope) => Known.Contains(scope);
}

/// <summary>
/// Identity of one bucket.
///
/// Per CREDENTIAL, not per app and not per tenant: brokers meter the logged-in account, so two
/// users of the same broker must not be able to consume each other's budget, and one user's
/// two accounts each get their own. Including the connector id keeps two brokers' "orders"
/// buckets apart.
/// </summary>
public readonly record struct RateLimitKey(string ConnectorId, string CredentialId, string Scope)
{
    /// <summary>Stable string form. Also the Redis key shape — see <see cref="IRateLimitStore"/>.</summary>
    public override string ToString() => $"rl:{ConnectorId}:{CredentialId}:{Scope}";
}

/// <summary>A budget in the three windows brokers actually publish.</summary>
public sealed record RateLimitBudget(int? PerSecond, int? PerMinute, int? PerDay)
{
    public static readonly RateLimitBudget Unlimited = new(null, null, null);

    public bool IsUnlimited => PerSecond is null && PerMinute is null && PerDay is null;

    public static RateLimitBudget FromSpec(RateLimitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new RateLimitBudget(spec.PerSecond, spec.PerMinute, spec.PerDay);
    }
}

/// <summary>Outcome of an acquisition attempt.</summary>
/// <param name="Acquired">False means the call must not be made.</param>
/// <param name="RetryAfter">The limiter's own estimate of when to try again, when it has one.</param>
/// <param name="Window">Which window rejected — "second", "minute" or "day". For diagnostics.</param>
public readonly record struct RateLimitDecision(bool Acquired, TimeSpan? RetryAfter, string? Window)
{
    public static readonly RateLimitDecision Allowed = new(true, null, null);

    public static RateLimitDecision Rejected(string window, TimeSpan? retryAfter = null) =>
        new(false, retryAfter, window);
}

/// <summary>
/// Where the token buckets live.
///
/// THE SEAM FOR REDIS. The in-memory implementation below is correct for a single host and
/// WRONG the moment the API runs more than one replica: each replica would hold its own
/// bucket and the broker would see N times the declared rate, which gets the credential
/// throttled or banned. A Redis implementation is therefore expected in production and belongs
/// in the infrastructure assembly, not here — the SDK must not drag StackExchange.Redis into
/// every connector's load context.
///
/// What a Redis implementation must do, so that swapping it in is not a behaviour change:
///
///  * Key: <c>RateLimitKey.ToString()</c> with a per-window suffix (<c>:s</c>, <c>:m</c>, <c>:d</c>).
///  * Atomicity: one Lua script per acquisition doing check-and-decrement in a single round
///    trip. A GET-then-DECR pair races and over-admits under exactly the burst conditions the
///    limiter exists to handle.
///  * Algorithm: a sliding window (sorted set of timestamps) or a token bucket stored as
///    <c>(tokens, lastRefillUnixMs)</c>. Either is fine; a fixed window is not, because it
///    admits 2x the limit across a window boundary.
///  * Expiry: set a TTL slightly longer than the window so idle credentials cost nothing.
///  * Clock: use Redis's own TIME inside the script. Application clocks drift between replicas
///    and a drifting clock silently widens the window.
///  * Failure mode: if Redis is unreachable, FAIL OPEN with a logged warning. Refusing every
///    order because a cache is down is a worse outcome than briefly exceeding a broker's
///    published rate — the broker's own 429 remains the backstop, and it is mapped to
///    <see cref="ConnectorErrorCodes.RateLimited"/> and retried.
/// </summary>
public interface IRateLimitStore : IAsyncDisposable
{
    /// <summary>
    /// Attempts to take <paramref name="permits"/> from the bucket, waiting at most
    /// <paramref name="maxWait"/>. Implementations must not throw for an exhausted bucket —
    /// that is a decision, not an error — and must propagate cancellation of
    /// <paramref name="ct"/>.
    /// </summary>
    ValueTask<RateLimitDecision> AcquireAsync(
        RateLimitKey key,
        RateLimitBudget budget,
        int permits,
        TimeSpan maxWait,
        CancellationToken ct = default);
}

/// <summary>
/// Single-process token buckets built on <c>System.Threading.RateLimiting</c>.
///
/// One <see cref="RateLimiter"/> per (key, window). The per-second and per-minute windows are
/// token buckets replenishing evenly rather than in one lump, because an even trickle shapes
/// a burst into a smooth stream instead of a sawtooth that still spikes the broker at the top
/// of every window. The per-day window is a fixed window: it is a quota, not a shaping
/// concern, and nobody waits ten hours for a day-token.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<RateLimitKey, BucketSet> _buckets = new();
    private int _disposed;

    public ValueTask<RateLimitDecision> AcquireAsync(
        RateLimitKey key,
        RateLimitBudget budget,
        int permits,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);

        if (budget.IsUnlimited)
        {
            return ValueTask.FromResult(RateLimitDecision.Allowed);
        }

        var set = _buckets.GetOrAdd(key, static (_, b) => new BucketSet(b), budget);
        return set.AcquireAsync(permits, maxWait, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var set in _buckets.Values)
        {
            set.Dispose();
        }

        _buckets.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The limiters for one key. Acquisition walks them COARSEST FIRST — day, then minute,
    /// then second.
    ///
    /// Why that order: a day-quota rejection is terminal for the rest of the session, so
    /// discovering it first avoids consuming finer-grained tokens on a call that can never
    /// proceed. The converse waste (taking a day-token and then failing on the second bucket)
    /// is bounded by <c>maxWait</c> and is rare, because the finer buckets WAIT rather than
    /// reject. This over-consumption on rejection is an accepted, documented approximation:
    /// token-bucket leases are time-based and cannot be handed back.
    /// </summary>
    private sealed class BucketSet(RateLimitBudget budget) : IDisposable
    {
        private readonly RateLimiter? _day = budget.PerDay is { } perDay && perDay > 0
            ? new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = perDay,
                Window = TimeSpan.FromDays(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            })
            : null;

        private readonly RateLimiter? _minute = budget.PerMinute is { } perMinute && perMinute > 0
            ? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = DivideEvenly(TimeSpan.FromMinutes(1), perMinute),
                QueueLimit = QueueLimitFor(perMinute),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            })
            : null;

        private readonly RateLimiter? _second = budget.PerSecond is { } perSecond && perSecond > 0
            ? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = perSecond,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = DivideEvenly(TimeSpan.FromSeconds(1), perSecond),
                QueueLimit = QueueLimitFor(perSecond),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            })
            : null;

        public async ValueTask<RateLimitDecision> AcquireAsync(
            int permits,
            TimeSpan maxWait,
            CancellationToken ct)
        {
            if (_day is not null)
            {
                // Never wait on a day quota. Waiting hours for a token is indistinguishable
                // from a hang, and the caller needs to be told the quota is gone.
                var dayDecision = await TakeAsync(_day, "day", permits, TimeSpan.Zero, ct);
                if (!dayDecision.Acquired)
                {
                    return dayDecision;
                }
            }

            if (_minute is not null)
            {
                var minuteDecision = await TakeAsync(_minute, "minute", permits, maxWait, ct);
                if (!minuteDecision.Acquired)
                {
                    return minuteDecision;
                }
            }

            if (_second is not null)
            {
                return await TakeAsync(_second, "second", permits, maxWait, ct);
            }

            return RateLimitDecision.Allowed;
        }

        public void Dispose()
        {
            _day?.Dispose();
            _minute?.Dispose();
            _second?.Dispose();
        }

        private static async ValueTask<RateLimitDecision> TakeAsync(
            RateLimiter limiter,
            string window,
            int permits,
            TimeSpan maxWait,
            CancellationToken ct)
        {
            try
            {
                if (maxWait <= TimeSpan.Zero)
                {
                    using var immediate = limiter.AttemptAcquire(permits);
                    return immediate.IsAcquired
                        ? RateLimitDecision.Allowed
                        : RateLimitDecision.Rejected(window, RetryAfterOf(immediate));
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(maxWait);

                using var lease = await limiter.AcquireAsync(permits, linked.Token);
                return lease.IsAcquired
                    ? RateLimitDecision.Allowed
                    : RateLimitDecision.Rejected(window, RetryAfterOf(lease));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // We ran out of patience, not out of caller. Report it as a rejection with the
                // wait we already burned as the hint.
                return RateLimitDecision.Rejected(window, maxWait);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Asking for more permits than the bucket can ever hold throws in the
                // underlying limiter. Treat it as a rejection: it is a caller bug, but not one
                // worth taking the trading path down for.
                return RateLimitDecision.Rejected(window, null);
            }
        }

        private static TimeSpan? RetryAfterOf(RateLimitLease lease) =>
            lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null;

        /// <summary>
        /// Even replenishment interval, floored at 1ms because the limiter rejects a zero
        /// period. A limit above 1000/second therefore degrades to 1ms granularity, which is
        /// far beyond any broker's published rate.
        /// </summary>
        private static TimeSpan DivideEvenly(TimeSpan window, int permits)
        {
            var ticks = window.Ticks / Math.Max(permits, 1);
            var floor = TimeSpan.FromMilliseconds(1).Ticks;
            return TimeSpan.FromTicks(Math.Max(ticks, floor));
        }

        /// <summary>
        /// How many callers may queue. Generous, because queueing is how a burst gets SHAPED
        /// rather than rejected — the whole point. Bounded so a runaway loop surfaces as
        /// rate-limit errors instead of unbounded memory.
        /// </summary>
        private static int QueueLimitFor(int permits) => Math.Max(permits * 8, 64);
    }
}

/// <summary>Tuning for <see cref="ConnectorRateLimiter"/>.</summary>
public sealed class ConnectorRateLimiterOptions
{
    /// <summary>
    /// How long a call may wait for a permit before failing with
    /// <see cref="ConnectorErrorCodes.RateLimited"/>. Five seconds is chosen to sit under a
    /// typical HTTP request timeout: a caller should get a clear rate-limit error rather than
    /// a timeout whose cause is invisible.
    /// </summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Order-affecting calls get a SHORTER wait. A trader pressing Buy would rather be told
    /// "too fast, try again" in a second than have their order sit in a local queue while the
    /// price moves. Reads are happy to wait.
    /// </summary>
    public TimeSpan MaxWaitForOrders { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Enforces the rate limits a connector's manifest DECLARES, per credential.
///
/// Manifest-driven rather than configured, so that adding a broker cannot mean editing the
/// host. If the manifest is wrong the conformance suite's rate-limit test catches it against
/// the real broker's behaviour.
///
/// A scoped bucket and the global bucket both apply — brokers publish an overall cap plus
/// tighter endpoint caps, and enforcing only one of them is how a credential gets throttled.
/// </summary>
public sealed class ConnectorRateLimiter(
    ConnectorManifest manifest,
    IRateLimitStore store,
    ILogger logger,
    ConnectorRateLimiterOptions? options = null)
{
    private readonly ConnectorRateLimiterOptions _options = options ?? new ConnectorRateLimiterOptions();

    /// <summary>
    /// Takes a permit for one call, or returns a <see cref="ConnectorErrorCodes.RateLimited"/>
    /// error carrying a Retry-After hint the resilience decorator can honour.
    /// </summary>
    /// <param name="credentialId">
    /// The broker credential this call is made with — normally <c>BrokerSession.AccountId</c>.
    /// Never a tenant or user id: the broker meters the credential.
    /// </param>
    /// <param name="scope">One of <see cref="RateLimitScopes"/>.</param>
    /// <param name="permits">Permits to consume. A basket of N legs sent as N calls costs N.</param>
    public async Task<Result> AcquireAsync(
        string credentialId,
        string scope,
        int permits = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var maxWait = string.Equals(scope, RateLimitScopes.Orders, StringComparison.OrdinalIgnoreCase)
            ? _options.MaxWaitForOrders
            : _options.MaxWait;

        // Global first: it is the coarser constraint, so a global rejection should not be
        // preceded by burning a scoped token. See BucketSet's ordering note.
        if (!string.Equals(scope, RateLimitScopes.Global, StringComparison.OrdinalIgnoreCase))
        {
            var global = await TryAcquireAsync(credentialId, RateLimitScopes.Global, permits, maxWait, ct);
            if (global.IsFailure)
            {
                return global;
            }
        }

        return await TryAcquireAsync(credentialId, scope, permits, maxWait, ct);
    }

    /// <summary>The declared budget for a scope, or <see cref="RateLimitBudget.Unlimited"/>.</summary>
    public RateLimitBudget BudgetFor(string scope)
    {
        foreach (var spec in manifest.RateLimits)
        {
            if (string.Equals(spec.Scope, scope, StringComparison.OrdinalIgnoreCase))
            {
                return RateLimitBudget.FromSpec(spec);
            }
        }

        return RateLimitBudget.Unlimited;
    }

    private async Task<Result> TryAcquireAsync(
        string credentialId,
        string scope,
        int permits,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        var budget = BudgetFor(scope);
        if (budget.IsUnlimited)
        {
            return Result.Success();
        }

        var key = new RateLimitKey(manifest.Id, credentialId, scope);
        var decision = await store.AcquireAsync(key, budget, permits, maxWait, ct);

        if (decision.Acquired)
        {
            return Result.Success();
        }

        logger.LogWarning(
            "{ConnectorId}: rate limit hit on the {Scope} bucket ({Window} window) for credential {Credential}.",
            manifest.Id,
            scope,
            decision.Window ?? "unknown",
            credentialId);

        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope"] = scope,
            ["window"] = decision.Window ?? "unknown",
        };

        if (decision.RetryAfter is { } retryAfter)
        {
            context[HttpConnectorClient.RetryAfterSecondsKey] =
                retryAfter.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return new Error(
            ConnectorErrorCodes.RateLimited,
            $"The {manifest.DisplayName} {scope} rate limit was reached. This will retry shortly.",
            VendorCode: null,
            VendorMessage: null,
            Context: context);
    }
}
