using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Timestamp handling for FYERS payloads.
///
/// FYERS sends naive local datetimes — "18-Dec-2023 16:33:24" — with no offset and no zone.
/// They are always Indian Standard Time. Binding those straight to a DateTimeOffset stamps
/// them with the <em>server's</em> offset, which on a UTC container makes every fill look five
/// and a half hours early. It also sends Unix epochs in three different units depending on the
/// route (seconds on candles and quote timestamps, milliseconds in the reports API), so every
/// timestamp that crosses this connector's boundary goes through here instead.
/// </summary>
internal static class FyersTime
{
    /// <summary>
    /// India has a single, fixed, DST-free offset. Keeping it as a constant lets us degrade
    /// gracefully on a container with no tz database installed rather than throwing at 09:15.
    /// </summary>
    public static readonly TimeSpan IstOffset = new(5, 30, 0);

    /// <summary>
    /// Epochs above this are milliseconds, not seconds. 10^12 seconds is the year 33658 and
    /// 10^12 milliseconds is September 2001, so the boundary is unambiguous for any timestamp
    /// this platform will ever see — and guessing wrong puts a fill thirty thousand years out.
    /// </summary>
    private const long MillisecondEpochThreshold = 100_000_000_000L;

    private static readonly string[] NaiveFormats =
    [
        // The shape the order book, trade book and order socket all use.
        "dd-MMM-yyyy HH:mm:ss",
        "dd-MMM-yyyy HH:mm",
        "dd-MMM-yyyy",

        // The reports API (order-history, trade-history) uses numeric months instead.
        "dd-MM-yyyy HH:mm:ss",
        "dd-MM-yyyy HH:mm",
        "dd-MM-yyyy",
        "dd/MM/yyyy HH:mm:ss",

        // Profile stamps pin_change_date and pwd_change_date this way.
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd",
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
    /// Parses a FYERS timestamp. Returns null rather than throwing: a malformed timestamp on
    /// one row of the order book must not blank the whole blotter.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // Several routes carry the timestamp as a bare epoch inside a string ("1622160000").
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            return FromEpoch(epoch);
        }

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

    /// <summary>
    /// A Unix epoch as FYERS sends it, in whichever unit this particular route happens to use.
    /// Zero is treated as absent rather than as 1 January 1970 — the symbol master writes 0 in
    /// the expiry column for every cash instrument.
    /// </summary>
    public static DateTimeOffset? FromEpoch(long epoch) => epoch switch
    {
        <= 0 => null,
        >= MillisecondEpochThreshold => DateTimeOffset.FromUnixTimeMilliseconds(epoch),
        _ => DateTimeOffset.FromUnixTimeSeconds(epoch),
    };

    /// <summary>
    /// The Indian venue zone, resolved once.
    ///
    /// <see cref="ResolveZone"/> hits the OS time-zone database, and doing that per row would
    /// dominate the cost of parsing a hundred thousand symbol-master rows.
    /// </summary>
    public static TimeZoneInfo India => LazyIndia.Value;

    private static readonly Lazy<TimeZoneInfo> LazyIndia =
        new(() => ResolveZone("Asia/Kolkata"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The trading date an instant falls on, in the venue's own zone.</summary>
    public static DateOnly VenueDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).Date);

    /// <summary>Formats an instant as the epoch seconds the history route wants.</summary>
    public static string EpochSeconds(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

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
/// Reads the claims out of a FYERS access token.
///
/// The access token is a JWT, and it carries its own expiry. That matters more here than it
/// would elsewhere: FYERS publishes no access-token lifetime at all, so without this the only
/// options are to guess a duration or to discover the expiry from a rejected order. The token
/// knows, so ask it.
///
/// Nothing here VERIFIES the token — we are not the audience and have no key. It is a payload
/// we were handed by the very server we are about to call, read only to schedule a re-login
/// slightly early, and a tampered value could at worst make us re-authenticate sooner. Every
/// method is total and returns null rather than throwing, because a token whose shape changes
/// must degrade to the configured fallback lifetime, not fail the login.
/// </summary>
internal static class FyersToken
{
    /// <summary>The <c>exp</c> claim, or null when the token is not a readable JWT.</summary>
    public static DateTimeOffset? ReadExpiry(string? accessToken) =>
        ReadClaims(accessToken) is { } claims
            && claims.TryGetProperty("exp", out var exp)
            && exp.TryGetInt64(out var seconds)
                ? FyersTime.FromEpoch(seconds)
                : null;

    /// <summary>
    /// The FYERS client id carried in the token, if it is there.
    ///
    /// A fallback for <see cref="FyersAuth"/>, which prefers the profile route because that one
    /// is documented. Claim names are probed rather than assumed for the same reason: this is
    /// an undocumented shape, so it may only ever be a hint.
    /// </summary>
    public static string? ReadClientId(string? accessToken)
    {
        if (ReadClaims(accessToken) is not { } claims)
        {
            return null;
        }

        foreach (var name in (string[])["fy_id", "client_id", "sub"])
        {
            if (claims.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static JsonElement? ReadClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = DecodeBase64Url(parts[1]);
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                // Clone: the JsonDocument is disposed on the way out of this method, and a
                // JsonElement borrowed from a disposed document throws on first access.
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? DecodeBase64Url(string segment)
    {
        // JWT segments are base64url with the padding stripped; Base64Url.DecodeFromChars
        // handles both, and returns false rather than throwing on anything else.
        return Base64Url.IsValid(segment) && Base64Url.DecodeFromChars(segment) is { } bytes
            ? bytes
            : null;
    }
}

/// <summary>
/// Number formatting for the wire. Every value FYERS receives from us is formatted with the
/// invariant culture; a server whose culture uses a comma decimal separator would otherwise
/// send "1560,50" as a limit price, and the exchange would read it as 156050.
/// </summary>
internal static class FyersNumber
{
    /// <summary>Formats a price. Two decimals is the finest tick any Indian venue quotes.</summary>
    public static string Price(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Formats an integral wire value (tokens, counts, quantities).</summary>
    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);
}
