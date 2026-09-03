using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Reads a JSON scalar of ANY type into a string.
///
/// WHY THIS EXISTS. mStock does not hold its JSON types stable across builds or across routes:
/// the same logical field arrives as <c>"1111"</c> on one endpoint and <c>1111</c> on another,
/// and a flag documented as a string turns up as a bare <c>true</c>. System.Text.Json treats
/// any such mismatch as fatal for the WHOLE document — so a field this connector never reads
/// can, and did, fail an otherwise perfectly good login.
///
/// The leniency is deliberately one-directional and scalar-only. An object or an array where a
/// string was expected is a genuine contract break, not a formatting quirk, and still throws:
/// there is no sensible string to make of them, and silently accepting one would hide a real
/// change in what the field MEANS rather than merely how it is typed.
/// </summary>
internal sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",

            // Preserve the vendor's own formatting rather than round-tripping through a
            // numeric type, which would turn "1560.50" into "1560.5" and quietly change a
            // value we may hand back to the broker verbatim.
            JsonTokenType.Number => ReadRawNumber(ref reader),

            _ => throw new JsonException(
                $"Expected a scalar for a string field but found {reader.TokenType}."),
        };

    /// <summary>The number's source text, exactly as the vendor wrote it.</summary>
    private static string ReadRawNumber(ref Utf8JsonReader reader)
    {
        // ValueSequence when the token straddles a read buffer boundary, ValueSpan otherwise.
        // BuffersExtensions.ToArray is named explicitly: the unqualified ToArray() binds to
        // System.Linq's ImmutableArray overload instead and does not compile.
        return reader.HasValueSequence
            ? Encoding.UTF8.GetString(BuffersExtensions.ToArray(reader.ValueSequence))
            : Encoding.UTF8.GetString(reader.ValueSpan);
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Reads a JSON boolean, or the many things a vendor sends when it means one.
///
/// Accepts <c>true</c>/<c>false</c>, the strings "true"/"false"/"yes"/"no"/"y"/"n"/"1"/"0", and
/// the numbers 1/0. Anything else — including an empty string — reads as null rather than
/// throwing: these fields are advisory (KYC status, activation status) and none of them is
/// worth failing a login over. A connector that needs one to be certain must check for null.
/// </summary>
internal sealed class LenientBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
                return false;

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) ? number != 0 : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                return text.Trim().ToUpperInvariant() switch
                {
                    "TRUE" or "YES" or "Y" or "1" => true,
                    "FALSE" or "NO" or "N" or "0" => false,
                    _ => null,
                };

            default:
                // An object or array here is a contract break, not a formatting quirk.
                throw new JsonException(
                    $"Expected a boolean-ish scalar but found {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteBooleanValue(value.Value);
        }
    }
}

/// <summary>
/// Reads an integer from a number, a numeric string, a boolean, or null.
///
/// Same rationale as the two above: these are status/sequence fields nothing branches on, and
/// none of them justifies rejecting a login that otherwise succeeded.
/// </summary>
internal sealed class LenientIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.TryGetInt32(out var value) ? value : null,
            JsonTokenType.True => 1,
            JsonTokenType.False => 0,
            JsonTokenType.String => int.TryParse(
                reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,
            _ => throw new JsonException($"Expected a numeric scalar but found {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}
