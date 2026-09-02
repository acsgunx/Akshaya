using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// The one JSON configuration the connector stack uses, for manifests and for vendor
/// payloads alike.
///
/// Why it is centralised: a connector that deserialises its manifest with different options
/// than the host validates it with will pass validation and then behave differently at
/// runtime. One options instance removes that entire class of bug.
/// </summary>
public static class ConnectorJson
{
    /// <summary>
    /// Shared, frozen-on-first-use options. camelCase property names, case-insensitive
    /// reads, enums as their C# member names (<c>"StopLimit"</c>, <c>"OAuth2"</c>) rather
    /// than camelCased or numeric.
    ///
    /// Enum member names — not camelCase — because <c>AuthModel.OAuth2</c> camelCases to
    /// the unreadable <c>"oAuth2"</c>, and because a manifest is written by a human and
    /// reviewed in a pull request. Reads are case-insensitive, so <c>"oauth2"</c> also works.
    /// Integer enum values are rejected: an ordinal in a manifest silently re-points at a
    /// different member the moment someone inserts an enum case.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = Create();

    /// <summary>A fresh, mutable copy for connectors that need vendor-specific converters.</summary>
    public static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };
}
