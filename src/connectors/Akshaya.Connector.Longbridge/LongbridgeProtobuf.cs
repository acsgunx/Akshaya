using System.Buffers;
using System.Text;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// A minimal protobuf encoder: exactly the wire types the Longbridge gateway messages use, and no more.
///
/// Why hand-written rather than a protobuf package: a connector loads into its own
/// AssemblyLoadContext, and a protobuf runtime in it would be a second copy alongside the host's gRPC
/// one, pinned independently. The subset needed here — varints, length-delimited strings and
/// messages, and packed repeated enums — is a page of code that can be read against the encoding
/// specification line by line.
///
/// Proto3 omits scalar fields equal to their default, and so does this writer: a zero, a false or an
/// empty string is simply not written, which is byte-for-byte what a generated encoder produces.
/// </summary>
internal sealed class ProtoWriter
{
    private const int WireVarint = 0;
    private const int WireLengthDelimited = 2;

    private readonly ArrayBufferWriter<byte> _buffer = new();

    public ProtoWriter String(int field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            WriteLengthDelimited(field, Encoding.UTF8.GetBytes(value));
        }

        return this;
    }

    /// <summary>A repeated string writes every element, empty ones included: position is meaning.</summary>
    public ProtoWriter Strings(int field, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            WriteLengthDelimited(field, Encoding.UTF8.GetBytes(value));
        }

        return this;
    }

    /// <summary>int32 and enum fields. A negative value is sign-extended to ten bytes, as the specification requires.</summary>
    public ProtoWriter Int32(int field, int value)
    {
        if (value != 0)
        {
            Tag(field, WireVarint);
            Varint(unchecked((ulong)(long)value));
        }

        return this;
    }

    public ProtoWriter Int64(int field, long value)
    {
        if (value != 0)
        {
            Tag(field, WireVarint);
            Varint(unchecked((ulong)value));
        }

        return this;
    }

    public ProtoWriter Bool(int field, bool value)
    {
        if (value)
        {
            Tag(field, WireVarint);
            Varint(1);
        }

        return this;
    }

    /// <summary>
    /// A repeated int32 or enum. Proto3 encodes these PACKED by default — one length-delimited field
    /// holding all the varints — and a gateway that expects packed input may ignore unpacked ones.
    /// </summary>
    public ProtoWriter PackedInt32(int field, IReadOnlyCollection<int> values)
    {
        if (values.Count == 0)
        {
            return this;
        }

        var inner = new ProtoWriter();
        foreach (var value in values)
        {
            inner.Varint(unchecked((ulong)(long)value));
        }

        WriteLengthDelimited(field, inner._buffer.WrittenSpan);
        return this;
    }

    /// <summary>An embedded message, written even when empty so its presence is preserved.</summary>
    public ProtoWriter Message(int field, ProtoWriter message)
    {
        ArgumentNullException.ThrowIfNull(message);
        WriteLengthDelimited(field, message._buffer.WrittenSpan);
        return this;
    }

    /// <summary>One entry of a <c>map&lt;string,string&gt;</c>, which on the wire is a repeated key/value message.</summary>
    public ProtoWriter MapEntry(int field, string key, string value) =>
        Message(field, new ProtoWriter().String(1, key).String(2, value));

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    private void WriteLengthDelimited(int field, ReadOnlySpan<byte> bytes)
    {
        Tag(field, WireLengthDelimited);
        Varint((ulong)bytes.Length);
        _buffer.Write(bytes);
    }

    private void Varint(ulong value)
    {
        Span<byte> scratch = stackalloc byte[10];
        var length = 0;

        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            scratch[length++] = value == 0 ? next : (byte)(next | 0x80);
        }
        while (value != 0);

        _buffer.Write(scratch[..length]);
    }
}

/// <summary>
/// A minimal protobuf decoder. Unknown fields are skipped, so a gateway that adds a field tomorrow
/// costs nothing; a malformed buffer throws <see cref="InvalidDataException"/>, which the socket
/// turns into a failed request rather than a crashed pump.
/// </summary>
internal sealed class ProtoReader(ReadOnlyMemory<byte> data)
{
    private int _position;

    public const int WireVarint = 0;
    public const int WireFixed64 = 1;
    public const int WireLengthDelimited = 2;
    public const int WireFixed32 = 5;

    public bool TryReadTag(out int field, out int wireType)
    {
        if (_position >= data.Length)
        {
            field = 0;
            wireType = 0;
            return false;
        }

        var tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);

        if (field <= 0)
        {
            throw new InvalidDataException("A protobuf field number must be positive.");
        }

        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        var span = data.Span;

        while (true)
        {
            if (_position >= span.Length)
            {
                throw new InvalidDataException("A protobuf varint ran past the end of the buffer.");
            }

            if (shift >= 64)
            {
                throw new InvalidDataException("A protobuf varint was longer than ten bytes.");
            }

            var b = span[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    public long ReadInt64() => unchecked((long)ReadVarint());

    public int ReadInt32() => unchecked((int)ReadVarint());

    public bool ReadBool() => ReadVarint() != 0;

    public ReadOnlyMemory<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)(data.Length - _position))
        {
            throw new InvalidDataException("A protobuf length ran past the end of the buffer.");
        }

        var slice = data.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes().Span);

    public ProtoReader ReadMessage() => new(ReadBytes());

    /// <summary>A repeated int32 or enum, accepting both the packed and the unpacked encoding as parsers must.</summary>
    public void ReadInt32s(int wireType, List<int> into)
    {
        if (wireType == WireLengthDelimited)
        {
            var packed = new ProtoReader(ReadBytes());
            while (!packed.AtEnd)
            {
                into.Add(packed.ReadInt32());
            }
        }
        else
        {
            into.Add(ReadInt32());
        }
    }

    public bool AtEnd => _position >= data.Length;

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case WireVarint:
                ReadVarint();
                break;
            case WireFixed64:
                Advance(8);
                break;
            case WireLengthDelimited:
                ReadBytes();
                break;
            case WireFixed32:
                Advance(4);
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    private void Advance(int count)
    {
        if (_position + count > data.Length)
        {
            throw new InvalidDataException("A protobuf fixed-width field ran past the end of the buffer.");
        }

        _position += count;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  The gateway messages, transcribed from longbridge/openapi-protobufs. Field numbers are the proto
//  file's; each type encodes or decodes only the fields this connector uses.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Command codes. Control commands share the quote and trade sockets.</summary>
internal static class LongbridgeCommand
{
    public const byte Heartbeat = 1;
    public const byte Auth = 2;
    public const byte Reconnect = 3;

    // longbridge.quote.v1.Command
    public const byte Subscribe = 6;
    public const byte Unsubscribe = 7;
    public const byte QuerySecurityStaticInfo = 10;
    public const byte QuerySecurityQuote = 11;
    public const byte QueryOptionQuote = 12;
    public const byte QueryDepth = 14;
    public const byte QueryOptionChainDateStrikeInfo = 21;
    public const byte QueryHistoryCandlestick = 27;
    public const byte PushQuoteData = 101;
    public const byte PushDepthData = 102;

    // longbridge.trade.v1.Command
    public const byte TradeSubscribe = 16;
    public const byte TradeUnsubscribe = 17;
    public const byte TradeNotify = 18;

    public static string Name(byte command) => command switch
    {
        Heartbeat => "Heartbeat",
        Auth => "Auth",
        Reconnect => "Reconnect",
        Subscribe => "Subscribe",
        Unsubscribe => "Unsubscribe",
        QuerySecurityStaticInfo => "QuerySecurityStaticInfo",
        QuerySecurityQuote => "QuerySecurityQuote",
        QueryOptionQuote => "QueryOptionQuote",
        QueryDepth => "QueryDepth",
        QueryOptionChainDateStrikeInfo => "QueryOptionChainDateStrikeInfo",
        QueryHistoryCandlestick => "QueryHistoryCandlestick",
        PushQuoteData => "PushQuoteData",
        PushDepthData => "PushDepthData",
        TradeSubscribe => "TradeSubscribe",
        TradeUnsubscribe => "TradeUnsubscribe",
        TradeNotify => "TradeNotify",
        _ => command.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>longbridge.control.v1.AuthRequest.</summary>
internal static class LbAuthRequest
{
    public static byte[] Encode(string token, IReadOnlyDictionary<string, string> metadata)
    {
        var writer = new ProtoWriter().String(1, token);
        foreach (var (key, value) in metadata)
        {
            writer.MapEntry(2, key, value);
        }

        return writer.ToArray();
    }
}

/// <summary>longbridge.control.v1.AuthResponse.</summary>
internal sealed record LbAuthResponse(string SessionId, long ExpiresMillis, uint Limit, uint Online)
{
    public static LbAuthResponse Decode(ReadOnlyMemory<byte> body)
    {
        var reader = new ProtoReader(body);
        string sessionId = string.Empty;
        long expires = 0;
        uint limit = 0, online = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: sessionId = reader.ReadString(); break;
                case 2: expires = reader.ReadInt64(); break;
                case 3: limit = (uint)reader.ReadVarint(); break;
                case 4: online = (uint)reader.ReadVarint(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbAuthResponse(sessionId, expires, limit, online);
    }
}

/// <summary>longbridge.control.v1.Error, the body of a non-zero response status.</summary>
internal sealed record LbError(ulong Code, string Message)
{
    public static LbError? TryDecode(ReadOnlyMemory<byte> body)
    {
        try
        {
            var reader = new ProtoReader(body);
            ulong code = 0;
            var message = string.Empty;

            while (reader.TryReadTag(out var field, out var wire))
            {
                switch (field)
                {
                    case 1: code = reader.ReadVarint(); break;
                    case 2: message = reader.ReadString(); break;
                    default: reader.Skip(wire); break;
                }
            }

            return new LbError(code, message);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>SecurityRequest (one symbol) and MultiSecurityRequest (many).</summary>
internal static class LbSecurityRequest
{
    public static byte[] One(string symbol) => new ProtoWriter().String(1, symbol).ToArray();

    public static byte[] Many(IEnumerable<string> symbols) => new ProtoWriter().Strings(1, symbols).ToArray();
}

/// <summary>SubscribeRequest and UnsubscribeRequest. SubType: 1 QUOTE, 2 DEPTH.</summary>
internal static class LbSubscription
{
    public static byte[] Subscribe(IEnumerable<string> symbols, IReadOnlyCollection<int> subTypes, bool firstPush) =>
        new ProtoWriter().Strings(1, symbols).PackedInt32(2, subTypes).Bool(3, firstPush).ToArray();

    public static byte[] Unsubscribe(IEnumerable<string> symbols, IReadOnlyCollection<int> subTypes) =>
        new ProtoWriter().Strings(1, symbols).PackedInt32(2, subTypes).ToArray();
}

/// <summary>longbridge.trade.v1.Sub: topic subscription on the trade socket.</summary>
internal static class LbTradeSub
{
    public const string PrivateTopic = "private";

    public static byte[] Encode(IEnumerable<string> topics) => new ProtoWriter().Strings(1, topics).ToArray();
}

/// <summary>
/// SecurityHistoryCandlestickRequest. Query type 2 is by date (<c>YYYYMMDD</c> strings); type 1 is by
/// offset from a date and minute, which is how a range too long for one page is walked forward.
/// </summary>
internal static class LbHistoryRequest
{
    public const int QueryByOffset = 1;
    public const int QueryByDate = 2;
    public const int DirectionForward = 1;

    public static byte[] ByDate(string symbol, int period, string startDate, string endDate, int tradeSession) =>
        new ProtoWriter()
            .String(1, symbol)
            .Int32(2, period)
            .Int32(3, 0) // AdjustType NO_ADJUST
            .Int32(4, QueryByDate)
            .Message(6, new ProtoWriter().String(1, startDate).String(2, endDate))
            .Int32(7, tradeSession)
            .ToArray();

    public static byte[] ForwardFrom(string symbol, int period, string date, string minute, int count, int tradeSession) =>
        new ProtoWriter()
            .String(1, symbol)
            .Int32(2, period)
            .Int32(3, 0)
            .Int32(4, QueryByOffset)
            .Message(5, new ProtoWriter().Int32(1, DirectionForward).String(2, date).String(3, minute).Int32(4, count))
            .Int32(7, tradeSession)
            .ToArray();
}

/// <summary>OptionChainDateStrikeInfoRequest: the expiry is <c>YYYYMMDD</c>.</summary>
internal static class LbOptionChainRequest
{
    public static byte[] Encode(string underlyingSymbol, string expiryDate) =>
        new ProtoWriter().String(1, underlyingSymbol).String(2, expiryDate).ToArray();
}

/// <summary>StaticInfo, one element of SecurityStaticInfoResponse (field 1).</summary>
internal sealed record LbStaticInfo(
    string Symbol,
    string NameEn,
    string NameHk,
    string NameCn,
    string Exchange,
    string Currency,
    int LotSize,
    string Board)
{
    public static List<LbStaticInfo> DecodeResponse(ReadOnlyMemory<byte> body) =>
        DecodeRepeated(body, 1, Decode);

    private static LbStaticInfo Decode(ProtoReader reader)
    {
        string symbol = "", nameCn = "", nameEn = "", nameHk = "", exchange = "", currency = "", board = "";
        var lotSize = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: symbol = reader.ReadString(); break;
                case 2: nameCn = reader.ReadString(); break;
                case 3: nameEn = reader.ReadString(); break;
                case 4: nameHk = reader.ReadString(); break;
                case 6: exchange = reader.ReadString(); break;
                case 7: currency = reader.ReadString(); break;
                case 8: lotSize = reader.ReadInt32(); break;
                case 17: board = reader.ReadString(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbStaticInfo(symbol, nameEn, nameHk, nameCn, exchange, currency, lotSize, board);
    }

    internal static List<T> DecodeRepeated<T>(ReadOnlyMemory<byte> body, int repeatedField, Func<ProtoReader, T> decode)
    {
        var items = new List<T>();
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == repeatedField && wire == ProtoReader.WireLengthDelimited)
            {
                items.Add(decode(reader.ReadMessage()));
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return items;
    }
}

/// <summary>
/// SecurityQuote and OptionQuote share fields 1–10; OptionQuote adds the option extension in field
/// 11. Prices are STRINGS on this wire, which keeps decimal precision intact end to end.
/// </summary>
internal sealed record LbQuote(
    string Symbol,
    string LastDone,
    string PrevClose,
    string Open,
    string High,
    string Low,
    long Timestamp,
    long Volume,
    int TradeStatus,
    long? OpenInterest,
    string? ContractMultiplier,
    string? UnderlyingSymbol)
{
    public static List<LbQuote> DecodeResponse(ReadOnlyMemory<byte> body) => LbStaticInfo.DecodeRepeated(body, 1, Decode);

    private static LbQuote Decode(ProtoReader reader)
    {
        string symbol = "", last = "", prev = "", open = "", high = "", low = "";
        long timestamp = 0, volume = 0;
        var status = 0;
        long? openInterest = null;
        string? multiplier = null, underlying = null;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: symbol = reader.ReadString(); break;
                case 2: last = reader.ReadString(); break;
                case 3: prev = reader.ReadString(); break;
                case 4: open = reader.ReadString(); break;
                case 5: high = reader.ReadString(); break;
                case 6: low = reader.ReadString(); break;
                case 7: timestamp = reader.ReadInt64(); break;
                case 8: volume = reader.ReadInt64(); break;
                case 10: status = reader.ReadInt32(); break;
                case 11 when wire == ProtoReader.WireLengthDelimited:
                    {
                        var extension = reader.ReadMessage();
                        while (extension.TryReadTag(out var extField, out var extWire))
                        {
                            switch (extField)
                            {
                                case 2: openInterest = extension.ReadInt64(); break;
                                case 5: multiplier = extension.ReadString(); break;
                                case 10: underlying = extension.ReadString(); break;
                                default: extension.Skip(extWire); break;
                            }
                        }

                        break;
                    }

                default: reader.Skip(wire); break;
            }
        }

        return new LbQuote(symbol, last, prev, open, high, low, timestamp, volume, status, openInterest, multiplier, underlying);
    }
}

/// <summary>Depth: one book level.</summary>
internal sealed record LbDepthLevel(int Position, string Price, long Volume, long OrderCount)
{
    public static LbDepthLevel Decode(ProtoReader reader)
    {
        int position = 0;
        var price = string.Empty;
        long volume = 0, orders = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: position = reader.ReadInt32(); break;
                case 2: price = reader.ReadString(); break;
                case 3: volume = reader.ReadInt64(); break;
                case 4: orders = reader.ReadInt64(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbDepthLevel(position, price, volume, orders);
    }
}

/// <summary>
/// SecurityDepthResponse (symbol 1, ask 2, bid 3) and PushDepth (symbol 1, sequence 2, ask 3, bid 4):
/// the same content with the repeated fields renumbered, which is why one decoder takes the numbers.
/// </summary>
internal sealed record LbDepth(string Symbol, List<LbDepthLevel> Asks, List<LbDepthLevel> Bids)
{
    public static LbDepth DecodeResponse(ReadOnlyMemory<byte> body) => Decode(body, askField: 2, bidField: 3);

    public static LbDepth DecodePush(ReadOnlyMemory<byte> body) => Decode(body, askField: 3, bidField: 4);

    private static LbDepth Decode(ReadOnlyMemory<byte> body, int askField, int bidField)
    {
        var reader = new ProtoReader(body);
        var symbol = string.Empty;
        var asks = new List<LbDepthLevel>();
        var bids = new List<LbDepthLevel>();

        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1)
            {
                symbol = reader.ReadString();
            }
            else if (field == askField && wire == ProtoReader.WireLengthDelimited)
            {
                asks.Add(LbDepthLevel.Decode(reader.ReadMessage()));
            }
            else if (field == bidField && wire == ProtoReader.WireLengthDelimited)
            {
                bids.Add(LbDepthLevel.Decode(reader.ReadMessage()));
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return new LbDepth(symbol, asks, bids);
    }
}

/// <summary>Candlestick, repeated in field 2 of SecurityCandlestickResponse.</summary>
internal sealed record LbCandle(string Close, string Open, string Low, string High, long Volume, long Timestamp)
{
    public static List<LbCandle> DecodeResponse(ReadOnlyMemory<byte> body) => LbStaticInfo.DecodeRepeated(body, 2, Decode);

    private static LbCandle Decode(ProtoReader reader)
    {
        string close = "", open = "", low = "", high = "";
        long volume = 0, timestamp = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: close = reader.ReadString(); break;
                case 2: open = reader.ReadString(); break;
                case 3: low = reader.ReadString(); break;
                case 4: high = reader.ReadString(); break;
                case 5: volume = reader.ReadInt64(); break;
                case 7: timestamp = reader.ReadInt64(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbCandle(close, open, low, high, volume, timestamp);
    }
}

/// <summary>StrikePriceInfo, repeated in field 1 of OptionChainDateStrikeInfoResponse.</summary>
internal sealed record LbStrike(string Price, string CallSymbol, string PutSymbol, bool Standard)
{
    public static List<LbStrike> DecodeResponse(ReadOnlyMemory<byte> body) => LbStaticInfo.DecodeRepeated(body, 1, Decode);

    private static LbStrike Decode(ProtoReader reader)
    {
        string price = "", call = "", put = "";
        var standard = false;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: price = reader.ReadString(); break;
                case 2: call = reader.ReadString(); break;
                case 3: put = reader.ReadString(); break;
                case 4: standard = reader.ReadBool(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbStrike(price, call, put, standard);
    }
}

/// <summary>PushQuote.</summary>
internal sealed record LbPushQuote(
    string Symbol,
    string LastDone,
    string Open,
    string High,
    string Low,
    long Timestamp,
    long Volume,
    long CurrentVolume)
{
    public static LbPushQuote Decode(ReadOnlyMemory<byte> body)
    {
        var reader = new ProtoReader(body);
        string symbol = "", last = "", open = "", high = "", low = "";
        long timestamp = 0, volume = 0, current = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: symbol = reader.ReadString(); break;
                case 3: last = reader.ReadString(); break;
                case 4: open = reader.ReadString(); break;
                case 5: high = reader.ReadString(); break;
                case 6: low = reader.ReadString(); break;
                case 7: timestamp = reader.ReadInt64(); break;
                case 8: volume = reader.ReadInt64(); break;
                case 12: current = reader.ReadInt64(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbPushQuote(symbol, last, open, high, low, timestamp, volume, current);
    }
}

/// <summary>longbridge.trade.v1.Notification: a topic push whose data is JSON when content_type is 1.</summary>
internal sealed record LbNotification(string Topic, int ContentType, ReadOnlyMemory<byte> Data)
{
    public const int ContentJson = 1;

    public static LbNotification Decode(ReadOnlyMemory<byte> body)
    {
        var reader = new ProtoReader(body);
        var topic = string.Empty;
        var contentType = 0;
        ReadOnlyMemory<byte> data = default;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1: topic = reader.ReadString(); break;
                case 2: contentType = reader.ReadInt32(); break;
                case 4: data = reader.ReadBytes(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new LbNotification(topic, contentType, data);
    }
}
