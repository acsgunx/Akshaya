using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akshaya.SharedKernel;

namespace Akshaya.Api.Contracts;

/// <summary>
/// Wire format for <see cref="Money"/>: <c>{ "amount": "1234.56", "currency": "INR" }</c>.
///
/// TWO DECISIONS WORTH DEFENDING.
///
/// The object shape, rather than a bare number: a monetary amount without its currency is the
/// most expensive kind of missing field in a cross-border system. Making the currency
/// structurally impossible to omit means no client can accidentally add SGD to INR, and no
/// endpoint can quietly assume a default.
///
/// The amount as a STRING: JSON numbers are IEEE-754 doubles in every browser, and
/// <c>JSON.parse</c> silently rounds a decimal it cannot represent. For prices and P&amp;L that
/// is a real loss of information at the exact moment the user is checking our arithmetic
/// against their broker's. Reading accepts a JSON number too, because hand-written clients and
/// curl sessions send them and rejecting those helps nobody.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be an object of the form { \"amount\": \"1.23\", \"currency\": \"INR\" }.");
        }

        decimal? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, "amount", StringComparison.OrdinalIgnoreCase))
            {
                amount = ReadDecimal(ref reader);
            }
            else if (string.Equals(name, "currency", StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (amount is null || string.IsNullOrWhiteSpace(currency))
        {
            throw new JsonException("Money requires both 'amount' and 'currency'.");
        }

        return new Money(amount.Value, new Currency(currency));
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("amount", value.Amount.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("currency", value.Currency.Code);
        writer.WriteEndObject();
    }

    internal static decimal ReadDecimal(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDecimal(),
        JsonTokenType.String when decimal.TryParse(
            reader.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => throw new JsonException("Expected a decimal number, as a JSON number or a string."),
    };
}

/// <summary>
/// Wire format for <see cref="Quantity"/>: a STRING on the way out, a string or a number on the
/// way in.
///
/// Fractional quantities are real — US brokers routinely fill 0.1 of a share — and a JSON
/// number round-tripped through a browser is a double. Writing 0.1 as a number and reading it
/// back as 0.10000000000000000555 is how an order for a tenth of a share becomes an order the
/// broker rejects for precision. Strings survive.
/// </summary>
public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{
    public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(MoneyJsonConverter.ReadDecimal(ref reader));

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Wire format for <see cref="InstrumentKey"/>: its canonical string, e.g.
/// <c>"XNSE:INFY:Equity"</c> or <c>"XNSE:NIFTY:OPT:2026-01-29:23000:Call"</c>.
///
/// One opaque, round-trippable token rather than six fields, because it is used as a URL
/// segment, a cache key, a SignalR subscription key and a dictionary key in the client. A
/// structured object would have to be re-serialised identically in four places to work as a
/// key, and the day two of those disagree is the day a subscription silently stops matching.
/// </summary>
public sealed class InstrumentKeyJsonConverter : JsonConverter<InstrumentKey>
{
    public override InstrumentKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (raw is null || !InstrumentKey.TryParse(raw, out var key))
        {
            throw new JsonException($"'{raw}' is not a valid instrument key.");
        }

        return key;
    }

    public override void Write(Utf8JsonWriter writer, InstrumentKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    public override InstrumentKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, InstrumentKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString());
    }
}

/// <summary>Wire format for <see cref="Currency"/>: the bare ISO 4217 code.</summary>
public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A currency code is required."));

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Code);
    }

    public override Currency ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Code);
    }
}

/// <summary>Wire format for <see cref="Venue"/>: the bare ISO 10383 MIC.</summary>
public sealed class VenueJsonConverter : JsonConverter<Venue>
{
    public override Venue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A venue MIC is required."));

    public override void Write(Utf8JsonWriter writer, Venue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Mic);
    }
}

/// <summary>Registers every converter the platform's wire format needs.</summary>
public static class AkshayaJson
{
    /// <summary>
    /// Applied to both the HTTP JSON options and the SignalR hub protocol, so a Tick that
    /// arrives over the socket looks exactly like a Quote fetched over HTTP. Two serialisation
    /// configurations for one domain is how a client ends up with two parsers.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNameCaseInsensitive = true;

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new MoneyJsonConverter());
        options.Converters.Add(new QuantityJsonConverter());
        options.Converters.Add(new InstrumentKeyJsonConverter());
        options.Converters.Add(new CurrencyJsonConverter());
        options.Converters.Add(new VenueJsonConverter());
    }
}
