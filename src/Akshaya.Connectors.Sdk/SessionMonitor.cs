using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk;

/// <summary>Where a session sits in its life. Drives UI copy and the re-auth prompt.</summary>
public enum SessionState
{
    /// <summary>Usable, and not close enough to expiry to bother the user about.</summary>
    Valid,

    /// <summary>Still usable, but inside the warning window. Warn now; do not wait for the failure.</summary>
    ExpiringSoon,

    /// <summary>Dead. Every non-auth call must fail fast with ReauthRequired.</summary>
    Expired,
}

/// <summary>The computed truth about a session, as opposed to what the broker claimed.</summary>
public sealed record SessionStatus
{
    /// <summary>
    /// When the session actually stops working — the earliest of everything we know.
    /// This is the number to show a trader, not <see cref="BrokerSession.ExpiresAt"/>.
    /// </summary>
    public required DateTimeOffset EffectiveExpiresAt { get; init; }

    /// <summary>Zero once expired; never negative, so UI formatting cannot produce "-3 minutes left".</summary>
    public required TimeSpan TimeRemaining { get; init; }

    public required SessionState State { get; init; }

    /// <summary>
    /// True only when the manifest declares refresh support AND a refresh token is present.
    /// When false the only way out is an interactive re-auth, and the UI must say so rather
    /// than spinning on a refresh that will never work.
    /// </summary>
    public required bool CanRefresh { get; init; }

    /// <summary>Human-readable explanation of which constraint bound the expiry. For support tickets.</summary>
    public string? Detail { get; init; }

    public bool IsUsable => State != SessionState.Expired;
}

/// <summary>
/// Works out when a broker session really dies, and warns before it does.
///
/// THIS IS THE MOST DANGEROUS SMALL PIECE OF LOGIC IN THE CONNECTOR STACK. Getting it wrong
/// does not produce a clean error; it produces a session the platform believes is alive,
/// used to place an order that silently fails, at the exact moment the trader is depending
/// on it. Three independent constraints exist and the effective expiry is the EARLIEST:
///
///   1. What the broker told us at login (<see cref="BrokerSession.ExpiresAt"/>). Brokers
///      routinely report issue-time + nominal-lifetime here even when their own token dies
///      earlier, so this alone cannot be trusted.
///   2. The manifest's nominal <see cref="AuthSpec.SessionLifetime"/> measured from issue.
///   3. Venue midnight, when <see cref="AuthSpec.ExpiresAtVenueMidnight"/> is set. This is
///      the case that catches people out: most Indian brokers (mStock, Zerodha, Angel One)
///      kill the token at 00:00 IST regardless of when it was issued. A token issued at
///      23:50 IST with a "24 hour lifetime" is dead in ten minutes. Taking the maximum, or
///      trusting the broker's own number, loses orders at the day boundary.
///
/// We take the minimum because being early is a re-auth prompt and being late is a lost
/// order. That asymmetry decides every judgement call in this file.
/// </summary>
public sealed class SessionMonitor
{
    /// <summary>
    /// Convention for carrying issue time in <see cref="BrokerSession.Extras"/>, round-trip
    /// ("o") formatted. CONTRACT GAP: <see cref="BrokerSession"/> has no IssuedAt property,
    /// but venue-midnight expiry cannot be computed correctly without it — "the next midnight
    /// after now" differs from "the next midnight after issue" for any session that has
    /// already crossed one. Connectors built on this SDK should set it; when it is absent we
    /// fall back to a conservative approximation and say so in <see cref="SessionStatus.Detail"/>.
    /// </summary>
    public const string IssuedAtExtraKey = "issuedAt";

    private static readonly TimeSpan MinimumWarningWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumWarningWindow = TimeSpan.FromMinutes(15);

    private readonly AuthSpec _auth;
    private readonly IClock _clock;

    public SessionMonitor(AuthSpec auth, IClock clock, TimeSpan? warningWindow = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(clock);

        _auth = auth;
        _clock = clock;
        WarningWindow = warningWindow ?? DefaultWarningWindow(auth);
    }

    /// <summary>
    /// How far ahead of death we start warning. Ten percent of the nominal lifetime, clamped
    /// to [1 minute, 15 minutes]: a fixed 15 minutes is absurd for a 60-second gateway
    /// handshake, and a pure percentage is uselessly short for one.
    /// </summary>
    public TimeSpan WarningWindow { get; }

    /// <summary>
    /// The venue-midnight calculation, isolated so it can be tested directly.
    ///
    /// Two timezone hazards are handled explicitly rather than by hope:
    ///
    ///  * INVALID local times. Some zones shift at midnight (America/Santiago,
    ///    America/Sao_Paulo historically), so 00:00 simply does not exist on those dates.
    ///    We walk forward a minute at a time to the first instant that does.
    ///  * AMBIGUOUS local times. On a fall-back night 00:00 happens twice. UTC = local -
    ///    offset, so the LARGER offset is the EARLIER instant, and earlier is the safe
    ///    direction (see the class remarks). We deliberately pick the larger offset.
    /// </summary>
    public static DateTimeOffset NextVenueMidnight(string timeZoneId, DateTimeOffset after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(after, tz);

        // The next wall-clock midnight strictly after `after`. If `after` is exactly midnight
        // the session gets the whole following day, which matches how brokers behave.
        var candidate = local.DateTime.Date.AddDays(1);

        // Skip a non-existent local midnight (spring-forward at 00:00).
        var guard = 0;
        while (tz.IsInvalidTime(candidate) && guard++ < 24 * 60)
        {
            candidate = candidate.AddMinutes(1);
        }

        var offset = tz.IsAmbiguousTime(candidate)
            ? tz.GetAmbiguousTimeOffsets(candidate).Max()   // larger offset == earlier UTC instant
            : tz.GetUtcOffset(candidate);

        return new DateTimeOffset(candidate, offset);
    }

    /// <summary>
    /// Effective expiry from first principles: the earliest of the broker's own claim, the
    /// manifest lifetime measured from issue, and venue midnight after issue.
    /// </summary>
    /// <param name="auth">The connector's declared auth behaviour.</param>
    /// <param name="issuedAt">When the session was created. See <see cref="IssuedAtExtraKey"/>.</param>
    /// <param name="declaredExpiry">What the broker said, if it said anything.</param>
    public static DateTimeOffset ComputeEffectiveExpiry(
        AuthSpec auth,
        DateTimeOffset issuedAt,
        DateTimeOffset? declaredExpiry)
    {
        ArgumentNullException.ThrowIfNull(auth);

        var effective = declaredExpiry ?? DateTimeOffset.MaxValue;

        if (auth.SessionLifetime is { } lifetime && lifetime > TimeSpan.Zero)
        {
            var byLifetime = issuedAt + lifetime;
            if (byLifetime < effective)
            {
                effective = byLifetime;
            }
        }

        if (auth.ExpiresAtVenueMidnight)
        {
            // ManifestLoader rejects ExpiresAtVenueMidnight without a timezone, so this
            // fallback should be unreachable for validated manifests. UTC is the choice that
            // errs early for every venue east of Greenwich, which is all of the ones this
            // flag currently applies to.
            var tzId = string.IsNullOrWhiteSpace(auth.VenueMidnightTimeZone)
                ? "UTC"
                : auth.VenueMidnightTimeZone;

            var midnight = NextVenueMidnight(tzId, issuedAt);
            if (midnight < effective)
            {
                effective = midnight;
            }
        }

        // Nothing at all was declared: refuse to invent a lifetime. A session with no known
        // expiry is treated as already dead so the caller re-authenticates rather than
        // trading against an unknown.
        return effective == DateTimeOffset.MaxValue ? issuedAt : effective;
    }

    /// <summary>Reads the issue time this SDK's connectors stash in <see cref="BrokerSession.Extras"/>.</summary>
    public static bool TryGetIssuedAt(BrokerSession session, out DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(session);

        issuedAt = default;
        return session.Extras.TryGetValue(IssuedAtExtraKey, out var raw)
               && DateTimeOffset.TryParse(
                   raw,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out issuedAt);
    }

    /// <summary>Stamps the issue time so <see cref="Evaluate"/> can be exact later.</summary>
    public static IReadOnlyDictionary<string, string> WithIssuedAt(
        IReadOnlyDictionary<string, string>? extras,
        DateTimeOffset issuedAt)
    {
        var copy = extras is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extras, StringComparer.Ordinal);

        copy[IssuedAtExtraKey] = issuedAt.ToString("o", CultureInfo.InvariantCulture);
        return copy;
    }

    public DateTimeOffset EffectiveExpiryFor(BrokerSession session) => Evaluate(session).EffectiveExpiresAt;

    public bool IsExpired(BrokerSession session) => Evaluate(session).State == SessionState.Expired;

    /// <summary>
    /// True inside the warning window OR already expired. Callers use this to prompt; they
    /// must not use it to block, because blocking on "expiring soon" would refuse trades that
    /// would have worked.
    /// </summary>
    public bool IsExpiringSoon(BrokerSession session) => Evaluate(session).State != SessionState.Valid;

    public SessionStatus Evaluate(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var now = _clock.UtcNow;

        string? detail = null;
        DateTimeOffset issuedAt;
        if (TryGetIssuedAt(session, out var stamped))
        {
            issuedAt = stamped;
        }
        else
        {
            // No issue time recorded. Anchoring the midnight calculation at `now` can only
            // ever produce a LATER midnight than the true one, which is the unsafe direction,
            // so we additionally never report more time remaining than the broker claimed.
            issuedAt = now;
            detail = "Session issue time unknown; expiry is approximated from the current instant.";
        }

        var effective = ComputeEffectiveExpiry(_auth, issuedAt, session.ExpiresAt);

        if (_auth.ExpiresAtVenueMidnight && effective < session.ExpiresAt)
        {
            detail = string.IsNullOrEmpty(detail)
                ? $"Expiry bound by venue midnight in {_auth.VenueMidnightTimeZone ?? "UTC"}, "
                  + $"not the broker's stated {session.ExpiresAt:u}."
                : detail;
        }

        var remaining = effective - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var state = remaining == TimeSpan.Zero
            ? SessionState.Expired
            : remaining <= WarningWindow
                ? SessionState.ExpiringSoon
                : SessionState.Valid;

        return new SessionStatus
        {
            EffectiveExpiresAt = effective,
            TimeRemaining = remaining,
            State = state,
            // Refresh is only real when BOTH the manifest declares it and the broker actually
            // handed us a refresh token. Declaring it and not having one is a common vendor
            // inconsistency and it must not turn into a retry loop.
            CanRefresh = _auth.RefreshSupported && !string.IsNullOrWhiteSpace(session.RefreshToken),
            Detail = detail,
        };
    }

    private static TimeSpan DefaultWarningWindow(AuthSpec auth)
    {
        if (auth.SessionLifetime is not { } lifetime || lifetime <= TimeSpan.Zero)
        {
            return MaximumWarningWindow;
        }

        var tenth = TimeSpan.FromTicks(lifetime.Ticks / 10);
        if (tenth < MinimumWarningWindow)
        {
            return MinimumWarningWindow;
        }

        return tenth > MaximumWarningWindow ? MaximumWarningWindow : tenth;
    }
}
