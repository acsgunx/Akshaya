namespace Akshaya.SharedKernel;

/// <summary>
/// One session window on a venue, in the venue's local time. Modelled as a list rather than
/// a single open/close because HKEX and TSE break for lunch, India has a pre-open auction,
/// and US markets have pre- and post-market sessions.
/// </summary>
public sealed record TradingSession(TimeOnly Open, TimeOnly Close, SessionKind Kind = SessionKind.Regular);

public enum SessionKind
{
    PreOpen,
    Regular,
    PostClose,
    Break,
}

/// <summary>
/// A venue's trading hours, holidays and timezone. Seeded from reference data per venue,
/// overridable per tenant for venues we have not shipped yet.
/// </summary>
public sealed record VenueCalendar
{
    public required Venue Venue { get; init; }

    public required string TimeZoneId { get; init; }

    public required IReadOnlyList<TradingSession> Sessions { get; init; }

    /// <summary>Days the venue does not trade at all.</summary>
    public IReadOnlySet<DateOnly> Holidays { get; init; } = new HashSet<DateOnly>();

    /// <summary>Days with a shortened session (Christmas Eve, muhurat trading, and so on).</summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<TradingSession>> SpecialDays { get; init; }
        = new Dictionary<DateOnly, IReadOnlyList<TradingSession>>();

    public IReadOnlySet<DayOfWeek> TradingDays { get; init; } = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    };
}

public enum VenueState
{
    Closed,
    PreOpen,
    Open,
    Break,
    PostClose,
    Holiday,
}

public interface ITradingCalendar
{
    /// <summary>State of a venue at an instant. The UI shows this per venue; the risk gate enforces it.</summary>
    VenueState GetState(Venue venue, DateTimeOffset at);

    bool IsOpen(Venue venue, DateTimeOffset at);

    /// <summary>Next instant the venue opens. Used for AMO ordering and "market opens in…" copy.</summary>
    DateTimeOffset? NextOpen(Venue venue, DateTimeOffset after);
}

/// <summary>
/// Calendar-data-driven implementation. Deliberately has no knowledge of any specific venue:
/// NSE and SGX differ only by the <see cref="VenueCalendar"/> rows registered against them.
/// </summary>
public sealed class TradingCalendar(IReadOnlyDictionary<Venue, VenueCalendar> calendars) : ITradingCalendar
{
    public VenueState GetState(Venue venue, DateTimeOffset at)
    {
        if (!calendars.TryGetValue(venue, out var calendar))
        {
            // An unknown venue is treated as closed rather than open. Refusing to trade is
            // always the safer default when reference data is missing.
            return VenueState.Closed;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(at, tz);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);

        if (calendar.Holidays.Contains(date) || !calendar.TradingDays.Contains(local.DayOfWeek))
        {
            return VenueState.Holiday;
        }

        var sessions = calendar.SpecialDays.TryGetValue(date, out var special)
            ? special
            : calendar.Sessions;

        foreach (var session in sessions)
        {
            if (time >= session.Open && time < session.Close)
            {
                return session.Kind switch
                {
                    SessionKind.PreOpen => VenueState.PreOpen,
                    SessionKind.Regular => VenueState.Open,
                    SessionKind.Break => VenueState.Break,
                    SessionKind.PostClose => VenueState.PostClose,
                    _ => VenueState.Closed,
                };
            }
        }

        return VenueState.Closed;
    }

    public bool IsOpen(Venue venue, DateTimeOffset at) => GetState(venue, at) == VenueState.Open;

    public DateTimeOffset? NextOpen(Venue venue, DateTimeOffset after)
    {
        if (!calendars.TryGetValue(venue, out var calendar))
        {
            return null;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(after, tz);

        // Look ahead a fortnight; longer than any exchange holiday run we care about.
        for (var dayOffset = 0; dayOffset <= 14; dayOffset++)
        {
            var probeDate = DateOnly.FromDateTime(local.DateTime.AddDays(dayOffset));
            if (calendar.Holidays.Contains(probeDate)
                || !calendar.TradingDays.Contains(probeDate.DayOfWeek))
            {
                continue;
            }

            var sessions = calendar.SpecialDays.TryGetValue(probeDate, out var special)
                ? special
                : calendar.Sessions;

            var regular = sessions.FirstOrDefault(s => s.Kind == SessionKind.Regular);
            if (regular is null)
            {
                continue;
            }

            var candidate = new DateTimeOffset(
                probeDate.ToDateTime(regular.Open),
                tz.GetUtcOffset(probeDate.ToDateTime(regular.Open)));

            if (candidate > after)
            {
                return candidate;
            }
        }

        return null;
    }
}
