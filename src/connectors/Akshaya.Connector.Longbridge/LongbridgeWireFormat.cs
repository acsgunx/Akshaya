using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// One of the three markets this connector trades, with the suffix Longbridge's symbols carry for it.
/// </summary>
/// <param name="Suffix">The region in <c>ticker.region</c>: <c>US</c>, <c>HK</c>, <c>SG</c>.</param>
/// <param name="Currency">What the market's instruments are priced in.</param>
/// <param name="Zone">The market's own clock, for trading dates.</param>
/// <param name="TickSize">The smallest tick the venue uses; see <see cref="Akshaya.SharedKernel.InstrumentDefinition.TickSize"/>.</param>
/// <param name="SettlementDays">T+1 in the US, T+2 in Hong Kong and Singapore.</param>
internal sealed record LongbridgeMarket(string Suffix, Currency Currency, TimeZoneInfo Zone, decimal TickSize, int SettlementDays)
{
    public static readonly LongbridgeMarket Us = new("US", Currency.Usd, LongbridgeTime.NewYork, 0.0001m, 1);

    public static readonly LongbridgeMarket Hk = new("HK", Currency.Hkd, LongbridgeTime.HongKong, 0.001m, 2);

    public static readonly LongbridgeMarket Sg = new("SG", Currency.Sgd, LongbridgeTime.Singapore, 0.001m, 2);

    public static readonly IReadOnlyList<LongbridgeMarket> All = [Us, Hk, Sg];
}

/// <summary>
/// JSON conventions for the REST routes.
///
/// Longbridge sends almost every number as a STRING (<c>"quantity": "100"</c>, <c>"submitted_at":
/// "1562761893"</c>), while a few fields are real numbers, and which is which is not documented
/// consistently. Every numeric-looking field is therefore declared as a string and parsed in one place,
/// and <see cref="LenientStringConverter"/> accepts a JSON number where a string was expected. A DTO that
/// guessed a field's JSON type wrong would fail the whole response.
/// </summary>
internal static class LongbridgeJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
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
                JsonTokenType.Number => Encoding(ref reader),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Expected a string or a number, found {reader.TokenType}."),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);

        private static string Encoding(ref Utf8JsonReader reader) =>
            System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
    }
}

/// <summary>The <c>{ code, message, data }</c> envelope every REST route answers with. Code 0 is success.</summary>
internal sealed record LongbridgeEnvelope<T>
{
    [JsonPropertyName("code")]
    public int Code { get; init; } = -1;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

/// <summary>A response whose data is empty or absent: replace, cancel.</summary>
internal sealed record LongbridgeEmpty
{
    public static readonly LongbridgeEmpty Instance = new();
}

/// <summary>Numbers as Longbridge writes them: decimal strings, sometimes empty.</summary>
internal static class LongbridgeNumber
{
    public static decimal? Decimal(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && decimal.TryParse(value.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public static decimal DecimalOrZero(string? value) => Decimal(value) ?? 0m;

    /// <summary>Outbound: plain decimal notation with no exponent and no trailing zeros.</summary>
    public static string Wire(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}

/// <summary>Timestamps, which Longbridge sends as Unix seconds in strings or integers.</summary>
internal static class LongbridgeTime
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

    public static DateTimeOffset? FromUnixSeconds(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? FromUnixSeconds(seconds)
            : null;

    /// <summary>Zero means "not set" in these payloads, and 1970 on a fill is worse than nothing.</summary>
    public static DateTimeOffset? FromUnixSeconds(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    public static DateOnly MarketDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary><c>yyyyMMdd</c>, the date form of the quote protocol.</summary>
    public static string Compact(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary><c>yyyy-MM-dd</c>, the date form of the REST routes.</summary>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseCompact(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                ? date
                : null;

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
