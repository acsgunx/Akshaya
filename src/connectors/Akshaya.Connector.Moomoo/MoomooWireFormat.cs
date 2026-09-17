using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// The OpenD protocol ids this connector uses, transcribed from the SDK's <c>ProtoId</c> table.
/// </summary>
internal static class MoomooProtoId
{
    public const int InitConnect = 1001;
    public const int GetGlobalState = 1002;
    public const int Notify = 1003;
    public const int KeepAlive = 1004;

    public const int TrdGetAccList = 2001;
    public const int TrdUnlockTrade = 2005;
    public const int TrdSubAccPush = 2008;
    public const int TrdGetFunds = 2101;
    public const int TrdGetPositionList = 2102;
    public const int TrdGetOrderList = 2201;
    public const int TrdPlaceOrder = 2202;
    public const int TrdModifyOrder = 2205;
    public const int TrdUpdateOrder = 2208;
    public const int TrdGetOrderFillList = 2211;
    public const int TrdUpdateOrderFill = 2218;
    public const int TrdGetHistoryOrderList = 2221;
    public const int TrdGetHistoryOrderFillList = 2222;

    public const int QotSub = 3001;
    public const int QotUpdateBasicQot = 3005;
    public const int QotUpdateKL = 3007;
    public const int QotUpdateRT = 3009;
    public const int QotUpdateTicker = 3011;
    public const int QotGetOrderBook = 3012;
    public const int QotUpdateOrderBook = 3013;
    public const int QotUpdateBroker = 3015;
    public const int QotUpdatePriceReminder = 3019;
    public const int QotRequestHistoryKL = 3103;
    public const int QotGetStaticInfo = 3202;
    public const int QotGetSecuritySnapshot = 3203;
    public const int QotGetOptionChain = 3209;

    /// <summary>
    /// Pushes arrive with OpenD's own serial numbers, which can collide with ours. They are therefore
    /// routed by protocol id, never by serial number — matching a push to a pending request would hand
    /// a caller a quote where it expected an order acknowledgement.
    /// </summary>
    public static bool IsPush(int protoId) => protoId is Notify or TrdUpdateOrder or TrdUpdateOrderFill
        or QotUpdateBasicQot or QotUpdateKL or QotUpdateRT or QotUpdateTicker or QotUpdateOrderBook
        or QotUpdateBroker or QotUpdatePriceReminder;

    /// <summary>A trade write: an unknown failure on one of these is a rejection, not a mystery.</summary>
    public static bool IsTradeWrite(int protoId) => protoId is TrdPlaceOrder or TrdModifyOrder;

    public static string Name(int protoId) => protoId switch
    {
        InitConnect => "InitConnect",
        GetGlobalState => "GetGlobalState",
        Notify => "Notify",
        KeepAlive => "KeepAlive",
        TrdGetAccList => "Trd_GetAccList",
        TrdUnlockTrade => "Trd_UnlockTrade",
        TrdSubAccPush => "Trd_SubAccPush",
        TrdGetFunds => "Trd_GetFunds",
        TrdGetPositionList => "Trd_GetPositionList",
        TrdGetOrderList => "Trd_GetOrderList",
        TrdPlaceOrder => "Trd_PlaceOrder",
        TrdModifyOrder => "Trd_ModifyOrder",
        TrdUpdateOrder => "Trd_UpdateOrder",
        TrdGetOrderFillList => "Trd_GetOrderFillList",
        TrdUpdateOrderFill => "Trd_UpdateOrderFill",
        TrdGetHistoryOrderList => "Trd_GetHistoryOrderList",
        TrdGetHistoryOrderFillList => "Trd_GetHistoryOrderFillList",
        QotSub => "Qot_Sub",
        QotUpdateBasicQot => "Qot_UpdateBasicQot",
        QotGetOrderBook => "Qot_GetOrderBook",
        QotUpdateOrderBook => "Qot_UpdateOrderBook",
        QotRequestHistoryKL => "Qot_RequestHistoryKL",
        QotGetStaticInfo => "Qot_GetStaticInfo",
        QotGetSecuritySnapshot => "Qot_GetSecuritySnapshot",
        QotGetOptionChain => "Qot_GetOptionChain",
        _ => protoId.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>One decoded OpenD frame.</summary>
internal readonly record struct MoomooFrame(int ProtoId, uint SerialNo, byte FormatType, byte[] Body);

/// <summary>
/// OpenD's 44-byte frame header, read and written byte for byte against the published layout:
///
/// <code>
///   offset  size  field
///        0     2  szHeaderFlag   "FT"
///        2     4  nProtoID       little-endian
///        6     1  nProtoFmtType  0 protobuf, 1 JSON
///        7     1  nProtoVer      0
///        8     4  nSerialNo      little-endian
///       12     4  nBodyLen       little-endian
///       16    20  arrBodySHA1    SHA-1 of the PLAINTEXT body
///       36     8  arrReserved    zero
/// </code>
///
/// Two facts from the documentation carry the whole class. The header is LITTLE-endian — unusual for
/// a wire protocol, and the SDK's <c>"&lt;1s1sI2B2I20s8s"</c> struct format confirms it. And the SHA-1
/// covers the body BEFORE encryption, so a digest that does not match what arrived is the signature of
/// an OpenD configured with an RSA key, which this connector does not speak. See <see cref="ReadAsync"/>.
/// </summary>
internal static class MoomooFrameCodec
{
    public const int HeaderLength = 44;

    public const byte FormatJson = 1;

    private const int ProtoIdOffset = 2;
    private const int FormatOffset = 6;
    private const int VersionOffset = 7;
    private const int SerialOffset = 8;
    private const int LengthOffset = 12;
    private const int DigestOffset = 16;
    private const int DigestLength = 20;

    /// <summary>Builds a complete frame: header plus JSON body.</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "OpenD's frame header carries a SHA-1 of the body as an integrity check. The protocol fixes the algorithm; nothing here relies on SHA-1 for secrecy or authenticity.")]
    public static byte[] Encode(int protoId, uint serialNo, ReadOnlySpan<byte> body)
    {
        var frame = new byte[HeaderLength + body.Length];
        var span = frame.AsSpan();

        span[0] = (byte)'F';
        span[1] = (byte)'T';
        BinaryPrimitives.WriteUInt32LittleEndian(span[ProtoIdOffset..], (uint)protoId);
        span[FormatOffset] = FormatJson;
        span[VersionOffset] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span[SerialOffset..], serialNo);
        BinaryPrimitives.WriteUInt32LittleEndian(span[LengthOffset..], (uint)body.Length);
        SHA1.HashData(body, span.Slice(DigestOffset, DigestLength));

        // The eight reserved bytes stay zero.
        body.CopyTo(span[HeaderLength..]);
        return frame;
    }

    /// <summary>
    /// Reads exactly one frame. Throws <see cref="InvalidDataException"/> for anything that is not a
    /// well-formed, unencrypted OpenD frame; the connection turns that into GatewayUnavailable.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Verifies OpenD's protocol-mandated SHA-1 body checksum; see Encode.")]
    public static async Task<MoomooFrame> ReadAsync(Stream stream, int maxBodyBytes, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (header[0] != (byte)'F' || header[1] != (byte)'T')
        {
            throw new InvalidDataException(
                "The gateway sent something that is not an OpenD frame. Check the configured port is OpenD's "
                + "API port (11111 by default) and not its WebSocket or telnet port.");
        }

        var protoId = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ProtoIdOffset));
        var format = header[FormatOffset];
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(SerialOffset));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(LengthOffset));

        if (length > (uint)maxBodyBytes)
        {
            throw new InvalidDataException(
                $"OpenD announced a {length.ToString(CultureInfo.InvariantCulture)}-byte body, past the "
                + $"{maxBodyBytes.ToString(CultureInfo.InvariantCulture)}-byte limit. The stream is out of step.");
        }

        var body = new byte[length];
        if (length > 0)
        {
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }

        if (!SHA1.HashData(body).AsSpan().SequenceEqual(header.AsSpan(DigestOffset, DigestLength)))
        {
            // The digest covers the plaintext, so an encrypted body can never match it. That makes a
            // mismatch the clearest available sign of an OpenD with rsa_private_key configured.
            throw new InvalidDataException(
                "An OpenD frame failed its SHA-1 check. OpenD is most likely configured with an RSA key "
                + "(rsa_private_key), which encrypts every frame; this connector speaks the unencrypted "
                + "protocol. Run OpenD without encryption on loopback or a private network.");
        }

        if (format != FormatJson)
        {
            throw new InvalidDataException(
                $"OpenD answered {MoomooProtoId.Name(protoId)} in protobuf, not JSON. This connector asks for "
                + "JSON pushes in InitConnect; set push_proto_type to 1 in OpenD's configuration if it persists.");
        }

        return new MoomooFrame(protoId, serial, format, body);
    }
}

/// <summary>
/// JSON conventions for OpenD bodies, which are protobuf messages in the proto3 JSON mapping.
///
/// That mapping writes 64-bit integers as JSON STRINGS. Account ids, order ids and fill ids are all
/// uint64 and routinely exceed 2^53, so a reader that parsed them as doubles would quietly round an
/// order id to a neighbouring one — and cancel the wrong order. They are read from either form and
/// written as strings, which the mapping also accepts.
/// </summary>
internal static class MoomooJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Doubles can arrive as "NaN" or "Infinity" in the proto3 mapping. They are read and then
            // discarded by MoomooNumber, rather than failing a whole order book over one field.
            NumberHandling = JsonNumberHandling.AllowReadingFromString
                             | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        options.Converters.Add(new UInt64AsStringConverter());
        options.Converters.Add(new Int64AsStringConverter());
        return options;
    }

    private sealed class UInt64AsStringConverter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? ulong.Parse(reader.GetString() ?? "0", NumberStyles.None, CultureInfo.InvariantCulture)
                : reader.GetUInt64();

        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class Int64AsStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? long.Parse(reader.GetString() ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                : reader.GetInt64();

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Prices and quantities cross the wire as doubles. The documentation says to round them to the
/// field's precision before use, so every inbound number goes through here once.
/// </summary>
internal static class MoomooNumber
{
    /// <summary>
    /// Six places covers every precision OpenD documents for securities (three for most prices, four
    /// for sub-dollar US quotes) while shaving the binary noise a double carries.
    /// </summary>
    public static decimal? Decimal(double? value, int decimals = 6)
    {
        if (value is not { } v || !double.IsFinite(v) || Math.Abs(v) >= 7.9e27)
        {
            return null;
        }

        return Math.Round((decimal)v, decimals, MidpointRounding.ToEven);
    }

    public static decimal DecimalOrZero(double? value, int decimals = 6) => Decimal(value, decimals) ?? 0m;

    /// <summary>Outbound: decimal to the double the protocol wants. The shortest round-trip form is what gets written.</summary>
    public static double Wire(decimal value) => (double)value;
}

/// <summary>
/// Timestamps. OpenD sends most times twice: a naive string in the MARKET's local time and a Unix
/// timestamp in seconds as a double. The timestamp is authoritative whenever it is present; the string
/// is parsed in the market's zone only as a fallback, because binding it naively on a UTC container
/// would shift every New York fill by four or five hours.
/// </summary>
internal static class MoomooTime
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    ];

    private static readonly Lazy<TimeZoneInfo> LazyNewYork = new(
        () => ResolveZone("America/New_York", "Eastern Standard Time", TimeSpan.FromHours(-5)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<TimeZoneInfo> LazyHongKong = new(
        () => ResolveZone("Asia/Hong_Kong", "China Standard Time", TimeSpan.FromHours(8)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static TimeZoneInfo NewYork => LazyNewYork.Value;

    public static TimeZoneInfo HongKong => LazyHongKong.Value;

    public static DateTimeOffset? FromUnixSeconds(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s) || s <= 0d)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(s * 1000d));
    }

    /// <summary>A naive market-local string, pinned to the market's zone.</summary>
    public static DateTimeOffset? ParseLocal(string? value, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParseExact(
                value.Trim(),
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    public static DateTimeOffset? Best(double? timestamp, string? local, TimeZoneInfo zone) =>
        FromUnixSeconds(timestamp) ?? ParseLocal(local, zone);

    public static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateOnly.TryParseExact(
            value.Trim().Length >= 10 ? value.Trim()[..10] : value.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    /// <summary>Formats an instant as the naive market-local string OpenD's filters and history expect.</summary>
    public static string FormatLocal(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly MarketDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// IANA id first, then the Windows id, then a fixed offset. A globalization-invariant container
    /// has no time-zone database, and refusing to read an order book over that would be absurd; the
    /// fixed offset is wrong by an hour across US daylight saving, which is why it is the last resort.
    /// </summary>
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
