using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>One of the three markets this connector trades, as Tiger's <c>market</c> field names it.</summary>
/// <param name="Code">Tiger's market code: <c>US</c>, <c>HK</c>, <c>SG</c>.</param>
/// <param name="Currency">What the market's instruments are priced in.</param>
/// <param name="Zone">The market's own clock, for trading dates.</param>
/// <param name="TickSize">The smallest tick the venue uses.</param>
/// <param name="SettlementDays">T+1 in the US, T+2 in Hong Kong and Singapore.</param>
internal sealed record TigerMarket(string Code, Currency Currency, TimeZoneInfo Zone, decimal TickSize, int SettlementDays)
{
    public static readonly TigerMarket Us = new("US", Currency.Usd, TigerTime.NewYork, 0.01m, 1);

    public static readonly TigerMarket Hk = new("HK", Currency.Hkd, TigerTime.HongKong, 0.001m, 2);

    public static readonly TigerMarket Sg = new("SG", Currency.Sgd, TigerTime.Singapore, 0.001m, 2);

    public static readonly IReadOnlyList<TigerMarket> All = [Us, Hk, Sg];

    public bool Is(TigerMarket other) => string.Equals(Code, other.Code, StringComparison.Ordinal);
}

/// <summary>
/// JSON conventions for Tiger's OpenAPI.
///
/// Tiger answers numbers as numbers and prices as numbers, but a few fields arrive as strings depending on the
/// method. Every scalar a DTO reads is therefore declared as a string, parsed in one place, and
/// <see cref="LenientStringConverter"/> accepts whichever form arrives.
/// </summary>
internal static class TigerJson
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
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();

                case JsonTokenType.Number:
                    return System.Text.Encoding.UTF8.GetString(
                        reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);

                case JsonTokenType.True:
                    return "true";

                case JsonTokenType.False:
                    return "false";

                case JsonTokenType.Null:
                    return null;

                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}

/// <summary>Numbers as Tiger writes them.</summary>
internal static class TigerNumber
{
    public static decimal? Decimal(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && decimal.TryParse(value.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public static decimal DecimalOrZero(string? value) => Decimal(value) ?? 0m;

    public static long? Integer(string? value) =>
        Decimal(value) is { } number && number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue
            ? (long)number
            : null;

    /// <summary>Outbound: plain decimal notation, no exponent, no trailing zeros.</summary>
    public static string Wire(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}

/// <summary>Tiger's clocks: epoch milliseconds on the wire, <c>yyyyMMdd</c> expiries, and a signed request timestamp.</summary>
internal static class TigerTime
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
        TigerNumber.Integer(value) is { } milliseconds and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    public static string ToUnixMilliseconds(DateTimeOffset instant) =>
        instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>timestamp</c> every request carries and every answer signs back: <c>yyyy-MM-dd HH:mm:ss</c>. Tiger's
    /// own SDKs send the calling machine's local time; this connector sends Hong Kong time, where Tiger's gateway
    /// runs, so the value means the same thing wherever the platform is deployed.
    /// </summary>
    public static string RequestStamp(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, HongKong).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>An option's expiry as the contract fields carry it.</summary>
    public static string ExpiryStamp(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseExpiry(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
        {
            return compact;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dashed))
        {
            return dashed;
        }

        // Some methods answer an expiry as epoch milliseconds instead.
        return FromUnixMilliseconds(text) is { } instant ? DateOnly.FromDateTime(instant.UtcDateTime) : null;
    }

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

/// <summary>
/// Which host a link talks to, from its licence.
///
/// Tiger serves Singapore and New Zealand accounts from a gateway of their own, quotes from a third host, and
/// everything else from the common gateway. Tiger's SDKs discover these at start-up from a configuration service;
/// this connector uses the published defaults, which an operator can override per deployment.
/// </summary>
internal static class TigerEndpoints
{
    public const string GatewaySuffix = "/gateway";

    private const string Common = "https://openapi.tigerfintech.com";
    private const string CommonSandbox = "https://openapi-sandbox.tigerfintech.com";
    private const string Us = "https://openapi.tradeup.com";

    public static Uri Trading(string? license, bool paper) => new(Host(license, paper) + GatewaySuffix);

    public static Uri Quote(string? license, bool paper)
    {
        var code = Normalise(license);

        // The Singapore and New Zealand licences have a quote host of their own; every other licence quotes from
        // the host it trades through.
        return code is "TBSG" or "TBNZ" && !paper
            ? new Uri(Common + "/hkg-quote" + GatewaySuffix)
            : Trading(license, paper);
    }

    private static string Host(string? license, bool paper) => Normalise(license) switch
    {
        "TBSG" or "TBNZ" => paper ? CommonSandbox + "/hkg" : Common + "/hkg",
        "TBUS" => paper ? CommonSandbox : Us,
        _ => paper ? CommonSandbox : Common,
    };

    private static string Normalise(string? license) => license?.Trim().ToUpperInvariant() ?? string.Empty;
}
