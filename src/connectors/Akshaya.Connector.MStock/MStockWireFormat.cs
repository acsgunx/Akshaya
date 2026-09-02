using System.Globalization;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Timestamp handling for mStock payloads.
///
/// mStock sends naive local datetimes — "2025-09-01 09:15:04" — with no offset and no zone.
/// They are always Indian Standard Time. Binding those straight to a DateTimeOffset stamps
/// them with the <em>server's</em> offset, which on a UTC container makes every fill look
/// five and a half hours early; on a machine in Singapore it makes them look two hours early.
/// Every timestamp that crosses this connector's boundary goes through here instead.
/// </summary>
internal static class MStockTime
{
    /// <summary>
    /// India has a single, fixed, DST-free offset. Keeping it as a constant lets us degrade
    /// gracefully on a container with no tz database installed rather than throwing at 09:15.
    /// </summary>
    public static readonly TimeSpan IstOffset = new(5, 30, 0);

    private static readonly string[] NaiveFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
        "dd-MM-yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss",
        "dd-MMM-yyyy",
    ];

    /// <summary>
    /// Resolves a time zone by IANA id, falling back to the Windows id and then to a fixed
    /// offset. .NET maps IANA ids on Windows through ICU, but a globalization-invariant
    /// container has no mapping table at all, and refusing to authenticate because of a
    /// missing tzdb would be an absurd way to lose a trading day.
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
    /// The next instant at which the clock in <paramref name="zone"/> reads midnight, strictly
    /// after <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset NextVenueMidnight(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var nextMidnightLocal = local.Date.AddDays(1);
        var offset = zone.GetUtcOffset(nextMidnightLocal);
        return new DateTimeOffset(nextMidnightLocal, offset);
    }

    /// <summary>
    /// Parses an mStock timestamp. Returns null rather than throwing: a malformed timestamp on
    /// one row of the order book must not blank the whole blotter.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // Anything carrying its own offset ("2025-09-01T09:15:00+0530") is authoritative.
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

        if (DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault,
                out var loose))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(loose, DateTimeKind.Unspecified), IstOffset);
        }

        return null;
    }

    /// <summary>Parses a timestamp, falling back to <paramref name="fallback"/> when absent or unreadable.</summary>
    public static DateTimeOffset ParseOr(string? value, DateTimeOffset fallback) => Parse(value) ?? fallback;

    /// <summary>Formats an instant the way mStock's chart routes want their date bounds.</summary>
    public static string FormatDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Formats an instant the way mStock's chart routes want their datetime bounds.</summary>
    public static string FormatDateTime(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        // A '+' anywhere, or a '-' that appears after the time separator, is an offset.
        // Dates themselves use '-' as a separator, so position matters.
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
/// Number formatting for the wire. Every value mStock receives from us is formatted with the
/// invariant culture; a server whose culture uses a comma decimal separator would otherwise
/// send "1560,50" as a limit price, and the exchange would read it as 156050.
/// </summary>
internal static class MStockNumber
{
    /// <summary>Formats a price. Two decimals is the finest tick any Indian venue quotes.</summary>
    public static string Price(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a quantity. mStock rejects fractional quantities outright, so anything with a
    /// fractional part is a bug upstream — the manifest declares fractionalQuantity: false and
    /// the risk gate is supposed to have caught it long before here.
    /// </summary>
    public static string Quantity(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Formats an integral wire value (tokens, counts).</summary>
    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);
}
