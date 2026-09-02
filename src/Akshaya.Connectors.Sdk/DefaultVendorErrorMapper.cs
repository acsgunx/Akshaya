using Akshaya.Connectors.Abstractions;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// A table-driven <see cref="IVendorErrorMapper"/> that most connectors can use as-is.
///
/// Three layers, applied in order, most specific first:
///
///   1. EXACT vendor codes supplied by the connector author. Always right when present, and
///      the only layer that should ever produce a money-affecting code like
///      <see cref="ConnectorErrorCodes.InsufficientFunds"/> for a specific broker.
///   2. MESSAGE phrases supplied by the connector author, matched case-insensitively as
///      substrings. Necessary because a depressing number of brokers return HTTP 200 with
///      <c>{"status":"error","message":"Insufficient funds"}</c> and no code at all.
///   3. Built-in generic phrases. Deliberately conservative: they only ever produce codes
///      that are safe to be wrong about — session, rate-limit, market-closed, not-found —
///      and never <see cref="ConnectorErrorCodes.OrderRejected"/> or a fill-affecting one.
///
/// The built-in layer can be switched off entirely for brokers whose wording collides with it.
/// </summary>
public sealed class DefaultVendorErrorMapper : IVendorErrorMapper
{
    /// <summary>
    /// Generic English phrases seen across most broker APIs. Ordered most-specific first
    /// because matching is first-hit. Each entry is a phrase that, if wrong, produces an
    /// error the platform handles safely.
    /// </summary>
    private static readonly (string Phrase, string Code)[] BuiltInPhrases =
    [
        // --- session. Retrying these without re-auth is pointless, so mapping them is
        // strictly better than falling through to Unknown. ---
        ("invalid session", ConnectorErrorCodes.SessionExpired),
        ("session expired", ConnectorErrorCodes.SessionExpired),
        ("session key", ConnectorErrorCodes.SessionExpired),
        ("token expired", ConnectorErrorCodes.SessionExpired),
        ("invalid token", ConnectorErrorCodes.SessionExpired),
        ("access token", ConnectorErrorCodes.SessionExpired),
        ("unauthori", ConnectorErrorCodes.SessionExpired),          // unauthorised / unauthorized
        ("please login", ConnectorErrorCodes.ReauthRequired),
        ("re-login", ConnectorErrorCodes.ReauthRequired),

        // --- credentials and challenges, only during the auth handshake ---
        ("invalid password", ConnectorErrorCodes.InvalidCredentials),
        ("invalid user", ConnectorErrorCodes.InvalidCredentials),
        ("invalid api key", ConnectorErrorCodes.InvalidCredentials),
        ("invalid otp", ConnectorErrorCodes.ChallengeFailed),
        ("invalid totp", ConnectorErrorCodes.ChallengeFailed),
        ("otp expired", ConnectorErrorCodes.ChallengeFailed),
        ("incorrect otp", ConnectorErrorCodes.ChallengeFailed),

        // --- throttling ---
        ("rate limit", ConnectorErrorCodes.RateLimited),
        ("too many request", ConnectorErrorCodes.RateLimited),
        ("throttl", ConnectorErrorCodes.RateLimited),

        // --- venue state ---
        ("market is closed", ConnectorErrorCodes.MarketClosed),
        ("market closed", ConnectorErrorCodes.MarketClosed),
        ("outside trading hours", ConnectorErrorCodes.MarketClosed),
        ("trading is not allowed", ConnectorErrorCodes.MarketClosed),

        // --- lookups ---
        ("order not found", ConnectorErrorCodes.OrderNotFound),
        ("invalid order id", ConnectorErrorCodes.OrderNotFound),
        ("symbol not found", ConnectorErrorCodes.InstrumentNotFound),
        ("instrument not found", ConnectorErrorCodes.InstrumentNotFound),
        ("invalid symbol", ConnectorErrorCodes.InstrumentNotFound),
        ("invalid instrument", ConnectorErrorCodes.InstrumentNotFound),

        // --- funding. Included despite the rule above because every broker on earth words
        // this the same way and treating it as Unknown would make it retryable-looking to a
        // human reading the logs. ---
        ("insufficient fund", ConnectorErrorCodes.InsufficientFunds),
        ("insufficient balance", ConnectorErrorCodes.InsufficientFunds),
        ("insufficient margin", ConnectorErrorCodes.InsufficientFunds),

        // --- gateway daemons ---
        // Only the generic word. A specific daemon's name ("OpenD" and the like) is
        // broker-specific vocabulary and belongs in that connector's own mapper, not here —
        // the architecture tests enforce that the core names no broker.
        ("gateway", ConnectorErrorCodes.GatewayUnavailable),
    ];

    private readonly IReadOnlyDictionary<string, string> _byVendorCode;
    private readonly IReadOnlyList<(string Phrase, string Code)> _byPhrase;
    private readonly bool _useBuiltInPhrases;

    /// <param name="vendorCodes">Exact vendor code to canonical code. Matched case-insensitively.</param>
    /// <param name="messagePhrases">
    /// Vendor message substring to canonical code, evaluated in the order given. Put the
    /// specific ones first: "insufficient margin for MIS" before "insufficient".
    /// </param>
    /// <param name="useBuiltInPhrases">
    /// Set false for brokers whose wording collides with the generic table — for example one
    /// that says "gateway" in an unrelated routing message.
    /// </param>
    public DefaultVendorErrorMapper(
        IReadOnlyDictionary<string, string>? vendorCodes = null,
        IReadOnlyList<(string Phrase, string Code)>? messagePhrases = null,
        bool useBuiltInPhrases = true)
    {
        _byVendorCode = vendorCodes is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(vendorCodes, StringComparer.OrdinalIgnoreCase);

        _byPhrase = messagePhrases ?? [];
        _useBuiltInPhrases = useBuiltInPhrases;
    }

    /// <summary>A mapper with no vendor knowledge at all — status codes and generic phrases only.</summary>
    public static DefaultVendorErrorMapper Generic { get; } = new();

    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.VendorCode)
            && _byVendorCode.TryGetValue(context.VendorCode, out var mapped))
        {
            return mapped;
        }

        // Search the message AND the raw body: several brokers put the useful sentence in a
        // field the connector's payload probes did not know to look at.
        var haystack = context.VendorMessage ?? context.RawBody;
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return null;
        }

        foreach (var (phrase, code) in _byPhrase)
        {
            if (haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        if (!_useBuiltInPhrases)
        {
            return null;
        }

        foreach (var (phrase, code) in BuiltInPhrases)
        {
            if (haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        return null;
    }

    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) =>
        Describe(canonicalCode);

    /// <summary>
    /// The platform's own words for each canonical code. Static and shared so every connector
    /// says the same thing for the same condition — a trader using two brokers should not see
    /// two different sentences for one expired session.
    /// </summary>
    public static string Describe(string canonicalCode) => canonicalCode switch
    {
        ConnectorErrorCodes.InvalidCredentials => "Those sign-in details were not accepted by the broker.",
        ConnectorErrorCodes.ChallengeFailed => "That verification code was not accepted.",
        ConnectorErrorCodes.SessionExpired => "The broker session has expired.",
        ConnectorErrorCodes.ReauthRequired => "Sign in to the broker again to continue.",
        ConnectorErrorCodes.GatewayUnavailable => "The broker gateway is not responding.",
        ConnectorErrorCodes.InvalidRequest => "The broker rejected the request as invalid.",
        ConnectorErrorCodes.InstrumentNotFound => "The broker does not recognise that instrument.",
        ConnectorErrorCodes.OrderNotFound => "The broker has no record of that order.",
        ConnectorErrorCodes.NotSupported => "The broker does not support that.",
        ConnectorErrorCodes.InsufficientFunds => "There are not enough funds or margin for this order.",
        ConnectorErrorCodes.MarketClosed => "The market is closed for this instrument.",
        ConnectorErrorCodes.RiskRejected => "The broker's risk checks rejected this order.",
        ConnectorErrorCodes.OrderRejected => "The broker rejected this order.",
        ConnectorErrorCodes.RateLimited => "The broker is rate-limiting requests; this will retry shortly.",
        ConnectorErrorCodes.Timeout => "The broker did not respond in time.",
        ConnectorErrorCodes.BrokerUnavailable => "The broker is temporarily unavailable.",
        _ => "The broker returned an error.",
    };
}
