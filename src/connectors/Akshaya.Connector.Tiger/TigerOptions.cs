namespace Akshaya.Connector.Tiger;

/// <summary>
/// Everything about Tiger's OpenAPI endpoints an operator might change without a redeploy. The defaults are the
/// production hosts; which one a link uses is decided by its licence (see <see cref="TigerEndpoints"/>).
/// </summary>
public sealed record TigerOptions
{
    /// <summary>Configuration section this binds from: <c>Connectors:Settings:tiger</c>.</summary>
    public const string SectionName = "Connectors:Settings:tiger";

    /// <summary>Overrides the licence-derived trading endpoint entirely. Null uses the licence's own host.</summary>
    public Uri? ServerUrl { get; init; }

    /// <summary>Overrides the licence-derived quote endpoint entirely.</summary>
    public Uri? QuoteServerUrl { get; init; }

    /// <summary>Sent as <c>charset</c> and used for signing.</summary>
    public string Charset { get; init; } = "UTF-8";

    /// <summary>Sent as <c>sign_type</c>. Tiger signs with SHA1withRSA under this name.</summary>
    public string SignType { get; init; } = "RSA";

    /// <summary>Sent as <c>version</c> on every request but the option chain.</summary>
    public string Version { get; init; } = "2.0";

    /// <summary>The option chain is served by a newer service version.</summary>
    public string OptionChainVersion { get; init; } = "3.0";

    /// <summary>Sent as <c>lang</c>, so Tiger's messages come back classifiable.</summary>
    public string Language { get; init; } = "en_US";

    /// <summary>
    /// Sent as <c>device_id</c>. Tiger's own SDKs send a machine identifier; a fixed value per deployment is
    /// equivalent and does not leak the host's MAC address.
    /// </summary>
    public string DeviceId { get; init; } = "akshaya";

    /// <summary>
    /// Tiger's public key, which signs every response. Used to prove an answer came from Tiger and not from
    /// whatever else answered the address.
    /// </summary>
    public string TigerPublicKey { get; init; } =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDNF3G8SoEcCZh2rshUbayDgLLrj6rKgzNMxDL2HS"
        + "nKcB0+GPOsndqSv+a4IBu9+I3fyBp5hkyMMG2+AXugd9pMpy6VxJxlNjhX1MYbNTZJUT4nudki4uh+LM"
        + "OkIBHOceGNXjgB+cXqmlUnjlqha/HgboeHSnSgpM3dKSJQlIOsDwIDAQAB";

    /// <summary>The sandbox signs with its own key.</summary>
    public string SandboxTigerPublicKey { get; init; } =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCbm21i11hgAENGd3/f280PSe4g9YGkS3TEXBY"
        + "7no2Ryv0v2cUNJoQWMcFPiUSGDNUkFFbPPZ2cn0aTbNRWSJXnAGCXO1aM0BgMSXwTJSqLDeOFqOAsXfhb"
        + "kQoPzY8CtXVXOFgxgdTZbcFHzWNhOAhjJhCynHtDDQqdjnc0BLSQIDAQAB";

    /// <summary>False turns off checking the signature Tiger puts on every answer. Leave it on.</summary>
    public bool VerifyResponseSignature { get; init; } = true;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Symbols per quote, bar or contract request.</summary>
    public int MaxSymbolsPerRequest { get; init; } = 50;

    /// <summary>Bars requested per history page. Tiger caps a page at 1,200.</summary>
    public int HistoryPageSize { get; init; } = 1000;

    /// <summary>Safety cap on history paging.</summary>
    public int MaxHistoryPages { get; init; } = 50;

    /// <summary>Pages of orders or executions read for one query.</summary>
    public int MaxOrderPages { get; init; } = 20;

    /// <summary>Orders sent for one basket: there is no basket method, so this is a loop.</summary>
    public int MaxBasketLegs { get; init; } = 5;

    /// <summary>Book levels asked for; Tiger's depth answers up to ten a side.</summary>
    public int DepthLevels { get; init; } = 10;
}
