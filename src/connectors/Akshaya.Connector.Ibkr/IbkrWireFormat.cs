using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>One of the three markets this connector trades.</summary>
/// <param name="Code">A label for logs and errors: <c>US</c>, <c>HK</c>, <c>SG</c>.</param>
/// <param name="Currency">What the market's instruments are priced in.</param>
/// <param name="Zone">The market's own clock, for trading dates.</param>
/// <param name="TickSize">The smallest tick the venue uses.</param>
/// <param name="SettlementDays">T+1 in the US, T+2 in Hong Kong and Singapore.</param>
internal sealed record IbkrMarket(string Code, Currency Currency, TimeZoneInfo Zone, decimal TickSize, int SettlementDays)
{
    public static readonly IbkrMarket Us = new("US", Currency.Usd, IbkrTime.NewYork, 0.01m, 1);

    public static readonly IbkrMarket Hk = new("HK", Currency.Hkd, IbkrTime.HongKong, 0.001m, 2);

    public static readonly IbkrMarket Sg = new("SG", Currency.Sgd, IbkrTime.Singapore, 0.001m, 2);

    public bool Is(IbkrMarket other) => string.Equals(Code, other.Code, StringComparison.Ordinal);
}

/// <summary>
/// JSON conventions for the gateway.
///
/// The Client Portal API is inconsistent about types: a conid is a number on one route and a string on the next,
/// prices are strings, sizes are numbers. Every scalar a DTO reads is therefore declared as a string and parsed
/// in one place, and <see cref="LenientStringConverter"/> accepts a number, a boolean or a string for it.
/// </summary>
internal static class IbkrJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        options.Converters.Add(new LenientStringConverter());
        return options;
    }

    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(
                    reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => SkipAndReturnNull(ref reader),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);

        /// <summary>An object or array where a scalar was expected is not worth failing a whole response over.</summary>
        private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
        {
            reader.Skip();
            return null;
        }
    }
}

/// <summary>Numbers as the gateway writes them: <c>"1,300"</c>, <c>"C168.42"</c>, <c>"1.2M"</c>, <c>"1,977.60 USD (10 Shares)"</c>.</summary>
internal static partial class IbkrNumber
{
    public static decimal? Decimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// A snapshot price. Field 31 may carry a one-letter prefix — <c>C</c> when the value is the previous close
    /// because nothing has traded, <c>H</c> when trading is halted — which is stripped and reported.
    /// </summary>
    public static decimal? Price(string? value, out char? prefix)
    {
        prefix = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length > 1 && char.IsAsciiLetter(text[0]))
        {
            prefix = char.ToUpperInvariant(text[0]);
            text = text[1..];
        }

        return Decimal(text);
    }

    /// <summary>A formatted quantity such as <c>1.2M</c> or <c>250K</c>.</summary>
    public static decimal? Scaled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var factor = char.ToUpperInvariant(text[^1]) switch
        {
            'K' => 1_000m,
            'M' => 1_000_000m,
            'B' => 1_000_000_000m,
            _ => 1m,
        };

        if (factor != 1m)
        {
            text = text[..^1];
        }

        return Decimal(text) is { } number ? number * factor : null;
    }

    /// <summary>The first number in a sentence, for the order preview's formatted amounts.</summary>
    public static decimal? Leading(string? value) =>
        value is not null && NumberPattern().Match(value) is { Success: true } match ? Decimal(match.Value) : null;

    /// <summary>The first three-letter upper-case word in a sentence, taken as its currency.</summary>
    public static string? CurrencyCode(string? value) =>
        value is not null && CurrencyPattern().Match(value) is { Success: true } match ? match.Groups[1].Value : null;

    public static long? Integer(string? value) =>
        Decimal(value) is { } number && number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue
            ? (long)number
            : null;

    [GeneratedRegex(@"-?\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b([A-Z]{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}

/// <summary>Timestamps and dates in the gateway's several formats.</summary>
internal static class IbkrTime
{
    private static readonly Lazy<TimeZoneInfo> LazyNewYork = new(
        () => ResolveZone("America/New_York", "Eastern Standard Time", TimeSpan.FromHours(-5)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<TimeZoneInfo> LazyHongKong = new(
        () => ResolveZone("Asia/Hong_Kong", "China Standard Time", TimeSpan.FromHours(8)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<TimeZoneInfo> LazySingapore = new(
        () => ResolveZone("Asia/Singapore", "Singapore Standard Time", TimeSpan.FromHours(8)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static TimeZoneInfo NewYork => LazyNewYork.Value;

    public static TimeZoneInfo HongKong => LazyHongKong.Value;

    public static TimeZoneInfo Singapore => LazySingapore.Value;

    /// <summary>Epoch milliseconds; zero or less means "not set".</summary>
    public static DateTimeOffset? FromUnixMilliseconds(string? value) =>
        IbkrNumber.Integer(value) is { } milliseconds and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    /// <summary>The history route's <c>startTime</c>: UTC, <c>yyyyMMdd-HH:mm:ss</c>.</summary>
    public static string UtcStamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>A contract month as the option routes want it: <c>JAN26</c>.</summary>
    public static string ContractMonth(DateOnly date) =>
        date.ToString("MMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    public static DateOnly MarketDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static TimeZoneInfo ResolveZone(string ianaId, string windowsId, TimeSpan fallbackOffset)
    {
        foreach (var id in (string[])[ianaId, windowsId])
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next id.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next id.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(ianaId, fallbackOffset, ianaId, ianaId);
    }
}
