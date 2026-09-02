using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit.TestDoubles;

/// <summary>
/// <see cref="ManualClock"/> helpers.
///
/// Every test in this kit takes its time from one of these rather than from the machine.
/// That is not tidiness: session expiry, venue midnight, good-till-date and order timestamps
/// are all time-dependent, and a suite that used the wall clock would pass all day and fail
/// for eight hours after India crossed midnight. Those are the worst tests to own — they fail
/// on someone else's morning, in a timezone nobody on the team is in.
/// </summary>
public static class TestClock
{
    /// <summary>
    /// The instant every fixture starts from unless it says otherwise: a Tuesday, mid-session
    /// on all three of the venues this platform ships calendars for, and comfortably far from
    /// midnight in Kolkata, Singapore and New York so that no test accidentally depends on a
    /// day boundary it did not mean to exercise.
    /// </summary>
    public static readonly DateTimeOffset DefaultStart =
        new(2026, 9, 1, 6, 30, 0, TimeSpan.Zero); // 12:00 IST, 14:30 SGT, 02:30 ET

    /// <summary>A clock frozen at <see cref="DefaultStart"/>.</summary>
    public static ManualClock Frozen() => new(DefaultStart);

    /// <summary>A clock frozen at a specific instant.</summary>
    public static ManualClock Frozen(DateTimeOffset at) => new(at);

    /// <summary>
    /// A session that is comfortably alive on <paramref name="clock"/>.
    ///
    /// The issue time is stamped into Extras because <c>SessionMonitor</c> cannot compute
    /// venue-midnight expiry correctly without it, and a fixture that omitted it would make
    /// every venue-midnight connector's expiry approximate — which is precisely the case the
    /// suite is trying to pin down.
    /// </summary>
    public static BrokerSession ValidSession(
        string connectorId,
        IClock clock,
        string accountId = "TEST-ACCOUNT",
        TimeSpan? lifetime = null,
        string? refreshToken = "refresh-token")
    {
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;

        return new BrokerSession
        {
            ConnectorId = connectorId,
            AccountId = accountId,
            AccessToken = "access-token",
            RefreshToken = refreshToken,
            ExpiresAt = now + (lifetime ?? TimeSpan.FromHours(6)),
            Extras = SessionMonitor.WithIssuedAt(null, now),
        };
    }

    /// <summary>
    /// A session that died an hour ago.
    ///
    /// Note it still carries a plausible issue time. A session with no issue time would expire
    /// through <c>SessionMonitor</c>'s approximate path, and the test would then be asserting
    /// on the fallback rather than on the real expiry rule.
    /// </summary>
    public static BrokerSession ExpiredSession(
        string connectorId,
        IClock clock,
        string accountId = "TEST-ACCOUNT")
    {
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;

        return new BrokerSession
        {
            ConnectorId = connectorId,
            AccountId = accountId,
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresAt = now - TimeSpan.FromHours(1),
            Extras = SessionMonitor.WithIssuedAt(null, now - TimeSpan.FromHours(7)),
        };
    }
}
