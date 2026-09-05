using System.Globalization;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Timestamp handling for Kite Connect payloads.
///
/// Kite documents its datetimes as <c>yyyy-mm-dd hh:mm:ss</c> "set under the Indian timezone
/// (IST)" — naive, with no offset. Binding those straight to a DateTimeOffset stamps them with
/// the <em>server's</em> offset, which on a UTC container makes every fill look five and a half
/// hours early. The historical-candle route is the exception and sends a full offset
/// (<c>2017-12-15T09:15:00+0530</c>), which is authoritative when present.
/// </summary>
internal static class ZerodhaTime
{
    /// <summary>
    /// India has a single, fixed, DST-free offset. Keeping it as a constant lets us degrade
    /// gracefully on a container with no tz database installed rather than throwing at 09:15.
    /// </summary>
    public static readonly TimeSpan IstOffset = new(5, 30, 0);

    private static readonly string[] NaiveFormats =
    [
        // The shape every JSON route uses.
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",

        // The trade book's order_timestamp is a bare time of day ("09:16:39"), with the date
        // carried separately on fill_timestamp.
        "HH:mm:ss",
    ];

    /// <summary>
    /// The Indian venue zone, resolved once. <see cref="ResolveZone"/> hits the OS time-zone
    /// database, and doing that per row would dominate the cost of parsing an instrument master.
    /// </summary>
    public static TimeZoneInfo India => LazyIndia.Value;

    private static readonly Lazy<TimeZoneInfo> LazyIndia =
        new(() => ResolveZone("Asia/Kolkata"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Resolves a time zone by IANA id, falling back to the Windows id and then to a fixed
    /// offset. .NET maps IANA ids on Windows through ICU, but a globalization-invariant
    /// container has no mapping table at all, and refusing to authenticate because of a missing
    /// tzdb would be an absurd way to lose a trading day.
    /// </summary>
    public static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return FallbackZone(timeZoneId);
        }
        catch (InvalidTimeZoneException)
        {
            return FallbackZone(timeZoneId);
        }
    }

    private static TimeZoneInfo FallbackZone(string timeZoneId)
    {
        if (string.Equals(timeZoneId, "Asia/Kolkata", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Asia/Calcutta", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall through to the fixed-offset zone below.
            }
            catch (InvalidTimeZoneException)
            {
                // Fall through to the fixed-offset zone below.
            }

            return TimeZoneInfo.CreateCustomTimeZone("IST", IstOffset, "India Standard Time", "IST");
        }

        throw new TimeZoneNotFoundException(
            $"Time zone '{timeZoneId}' is not available on this machine and has no built-in fallback.");
    }

    /// <summary>
    /// The next instant at which the clock in <paramref name="zone"/> reads
    /// <paramref name="hour"/>:00, strictly after <paramref name="now"/>.
    ///
    /// Kite's access token dies at 06:00 IST rather than at midnight, so this takes the hour as
    /// a parameter instead of assuming the day boundary. Getting that wrong in either direction
    /// is costly: assume midnight and the platform prompts for a re-login six hours early every
    /// single day; assume a rolling 24 hours and the token is dead before the market opens.
    /// </summary>
    public static DateTimeOffset NextVenueHour(DateTimeOffset now, TimeZoneInfo zone, int hour)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date.AddHours(hour);

        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    /// <summary>
    /// Parses a Kite timestamp. Returns null rather than throwing: a malformed timestamp on one
    /// row of the order book must not blank the whole blotter.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // The candle route carries its own offset and is authoritative when it does.
        if (HasExplicitOffset(trimmed)
            && DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var withOffset))
        {
            return withOffset;
        }

        if (DateTime.TryParseExact(
                trimmed,
                NaiveFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault,
                out var naive))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), IstOffset);
        }

        return null;
    }

    /// <summary>Parses a timestamp, falling back to <paramref name="fallback"/> when absent or unreadable.</summary>
    public static DateTimeOffset ParseOr(string? value, DateTimeOffset fallback) => Parse(value) ?? fallback;

    /// <summary>Parses a <c>yyyy-MM-dd</c> date, as the instrument master writes expiries.</summary>
    public static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The trading date an instant falls on, in the venue's own zone.</summary>
    public static DateOnly VenueDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).Date);

    /// <summary>Formats an instant the way the historical-candle route wants its bounds.</summary>
    public static string FormatBound(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        // A '+' anywhere, or a '-' that appears after the time separator, is an offset. Dates
        // themselves use '-' as a separator, so position matters.
        var timeMarker = value.IndexOfAny([' ', 'T', 't']);
        if (timeMarker < 0)
        {
            return false;
        }

        var timePart = value[timeMarker..];
        return timePart.Contains('+', StringComparison.Ordinal)
               || timePart.Contains('-', StringComparison.Ordinal);
    }
}

/// <summary>
/// Number formatting for the wire. Every value Kite receives from us is formatted with the
/// invariant culture; a server whose culture uses a comma decimal separator would otherwise send
/// "1560,50" as a limit price, and the exchange would read it as 156050.
/// </summary>
internal static class ZerodhaNumber
{
    /// <summary>Formats a price. Two decimals is the finest tick any Indian equity venue quotes.</summary>
    public static string Price(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Formats an integral wire value (quantities, tokens, counts).</summary>
    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A minimal RFC 4180 field splitter for the instrument master.
///
/// Kite quotes the <c>name</c> column — <c>"NIFTY 50"</c>, <c>"ABB INDIA"</c> — and a company
/// name containing a comma is a matter of when, not if. A naive <c>Split(',')</c> works on
/// today's file and would, on the day one appears, shift every subsequent column silently: the
/// tick size becomes the lot size, the strike becomes the tick size, and the instrument is
/// wrong in a way nothing detects. Fifteen lines is a cheap price for never having that
/// conversation.
/// </summary>
internal static class ZerodhaCsv
{
    public static string[] SplitLine(string line)
    {
        var fields = new List<string>(12);
        var field = new System.Text.StringBuilder(32);
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    // An escaped quote inside a quoted field.
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
