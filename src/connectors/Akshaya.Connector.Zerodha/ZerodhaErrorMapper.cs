using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Translates Kite's failure vocabulary into the canonical <see cref="ConnectorErrorCodes"/> set,
/// without ever discarding what the broker actually said.
///
/// Two rules govern everything here:
///
/// 1. The vendor's own code and message are copied verbatim into <see cref="Error.VendorCode"/>
///    and <see cref="Error.VendorMessage"/>. Support answers "what did Kite say" from the audit
///    log, and a canonical code alone cannot answer it.
/// 2. The canonical code decides whether the host retries. Getting that wrong in the optimistic
///    direction is expensive: an <c>OrderException</c> classified as retryable becomes a
///    duplicate order. When in doubt this maps to a non-retryable code.
///
/// Kite names the exception in an <c>error_type</c> field and pairs it with a real 4xx or 5xx
/// status line, which makes it one of the better-behaved broker APIs to map. The one place the
/// status alone is misleading is <c>OrderException</c>, which arrives as a 500 and is emphatically
/// not a "retry it" server error.
/// </summary>
public sealed class ZerodhaErrorMapper : IVendorErrorMapper
{
    /// <summary>How much of an unparseable body to keep. Enough to identify a WAF or proxy page.</summary>
    private const int MaxBodySnippet = 512;

    // Kite's documented exception names, exactly as they appear in error_type.

    /// <summary>Session expired or invalidated. Always preceded by a 403.</summary>
    public const string TokenException = "TokenException";

    /// <summary>User account related errors.</summary>
    public const string UserException = "UserException";

    /// <summary>Order placement failures and corrupt fetches.</summary>
    public const string OrderException = "OrderException";

    /// <summary>Missing required fields, bad parameter values.</summary>
    public const string InputException = "InputException";

    /// <summary>Insufficient funds for the order.</summary>
    public const string MarginException = "MarginException";

    /// <summary>Insufficient holdings to place the sell order.</summary>
    public const string HoldingException = "HoldingException";

    /// <summary>Kite could not reach its own order management system.</summary>
    public const string NetworkException = "NetworkException";

    /// <summary>Kite could not understand the OMS's response.</summary>
    public const string DataException = "DataException";

    /// <summary>Unclassified. Documented as rare.</summary>
    public const string GeneralException = "GeneralException";

    /// <summary>Not in the documented list, but returned when an app lacks a permission.</summary>
    public const string PermissionException = "PermissionException";

    /// <summary>
    /// HTTP 428, which Kite uses for one very specific thing: the sell needs the user to
    /// authorise the debit at the depository (CDSL) first.
    /// </summary>
    public const int HoldingsAuthorisationRequired = 428;

    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        if (IsUnregisteredIp(context.VendorMessage))
        {
            return ConnectorErrorCodes.NotSupported;
        }

        // A recognised exception name is the strongest signal there is.
        if (!string.IsNullOrWhiteSpace(context.VendorCode))
        {
            var typed = context.VendorCode switch
            {
                TokenException => ConnectorErrorCodes.SessionExpired,
                UserException => ConnectorErrorCodes.InvalidCredentials,
                InputException => ClassifyInputException(context.VendorMessage),
                MarginException => ConnectorErrorCodes.InsufficientFunds,
                HoldingException => ConnectorErrorCodes.OrderRejected,
                OrderException => ClassifyOrderException(context.VendorMessage),
                PermissionException => ConnectorErrorCodes.NotSupported,

                // Kite reaching its own OMS and failing is a transport problem on their side —
                // the one Kite exception it is genuinely safe to retry a READ against.
                NetworkException => ConnectorErrorCodes.BrokerUnavailable,

                // DataException means Kite could not understand its OMS. Retrying will not help
                // and the shape of the failure is unknown, so it stays non-retryable.
                DataException => ConnectorErrorCodes.Unknown,
                GeneralException => ClassifyGeneralException(context),
                _ => null,
            };

            if (typed is not null)
            {
                return typed;
            }
        }

        if (context.HttpStatus == HoldingsAuthorisationRequired)
        {
            return ConnectorErrorCodes.OrderRejected;
        }

        // No typed error, or one this mapper does not recognise: try the free text. Returning
        // null here is deliberate — it tells the caller to fall back to the transport mapping
        // rather than have this mapper guess.
        return ClassifyFromMessage(context.VendorMessage);
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.SessionExpired =>
            "The Kite session has expired; sign in again. Kite invalidates the access token at 6 AM "
            + "India time, and also when the user logs in to Kite elsewhere.",
        ConnectorErrorCodes.ReauthRequired => "Kite needs you to sign in again.",
        ConnectorErrorCodes.InvalidCredentials => WithBrokerWords(
            "Kite did not accept these credentials.", context),
        ConnectorErrorCodes.InsufficientFunds => WithBrokerWords(
            "The Kite account does not have enough margin for this order.", context),

        // THE BROKER'S REASON SURVIVES. Our sentence says an order was rejected; only the
        // exchange's own text says whether it was a circuit limit, a freeze quantity, a banned
        // F&O scrip or a closed market — and those need four different responses from the trader.
        ConnectorErrorCodes.OrderRejected => context.HttpStatus == HoldingsAuthorisationRequired
            ? "This sell needs the holdings authorised at the depository before it can go through. "
              + "Send the user through Kite's holdings authorisation flow and retry."
            : WithBrokerWords("Kite rejected the order.", context),
        ConnectorErrorCodes.OrderNotFound => "Kite has no record of that order.",
        ConnectorErrorCodes.MarketClosed => "The market is closed for this instrument.",
        ConnectorErrorCodes.RiskRejected => "Kite's risk checks blocked this order.",
        ConnectorErrorCodes.InstrumentNotFound => "Kite does not recognise that instrument.",
        ConnectorErrorCodes.RateLimited =>
            "Too many requests to Kite; wait and retry. Quotes are capped at one per second and "
            + "historical candles at three.",
        ConnectorErrorCodes.Timeout => "Kite did not respond in time.",
        ConnectorErrorCodes.BrokerUnavailable => "Kite is currently unavailable.",
        ConnectorErrorCodes.NotSupported => IsUnregisteredIp(context.VendorMessage)
            ? "Kite only accepts API orders from the static IP registered on your Kite Connect app, "
              + "and this app is connecting from a different one. In the Kite Connect developer "
              + "console, register the public IP of the machine or server running Akshaya, then try again."
            : WithBrokerWords("Kite does not permit this action on this account.", context),
        ConnectorErrorCodes.InvalidRequest => WithBrokerWords("Kite rejected the request as invalid.", context),

        // NO OPINION: SAY WHAT THE BROKER SAID. Our own wording is better than a vendor's ONLY
        // when we understood the failure well enough to write one; when we did not, the vendor's
        // text is the most useful thing available.
        _ => string.IsNullOrWhiteSpace(context.VendorMessage)
            ? "Kite reported an error."
            : context.VendorMessage,
    };

    /// <summary>Maps a non-success HTTP response, reading the envelope's error_type when it has one.</summary>
    public Error MapHttp(int statusCode, string? responseBody, string? path = null)
    {
        var payload = ReadPayload(responseBody);
        var context = new VendorErrorContext(
            statusCode,
            payload.ErrorType,
            payload.Message ?? payload.RawSnippet,
            path,
            payload.RawSnippet);

        var canonical = MapToCanonicalCode(context) ?? MapStatusCode(statusCode);

        return new Error(
            canonical,
            DescribeCanonicalCode(canonical, context),
            payload.ErrorType ?? statusCode.ToString(CultureInfo.InvariantCulture),
            payload.Message ?? payload.RawSnippet);
    }

    /// <summary>Maps a Kite envelope that reported <c>status: "error"</c>.</summary>
    public Error MapEnvelope(string? errorType, string? message, string? path = null, int statusCode = 200)
    {
        var context = new VendorErrorContext(statusCode, errorType, message, path, null);
        var canonical = MapToCanonicalCode(context) ?? MapStatusCode(statusCode);

        return new Error(canonical, DescribeCanonicalCode(canonical, context), errorType, message);
    }

    /// <summary>Maps a transport-level exception thrown before any response was read.</summary>
    public static Error MapException(Exception exception) => exception switch
    {
        // TaskCanceledException derives from OperationCanceledException, so the base type covers
        // both the HttpClient timeout and an ambient cancellation.
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            "Kite did not respond in time.",
            exception.GetType().Name,
            exception.Message),

        // A socket-level failure is the broker or the network being unavailable, and it is safe
        // to retry a READ. The host's resilience decorator is what knows that a WRITE (an order
        // placement) must instead be reconciled against the order book.
        HttpRequestException or SocketException or WebSocketException => new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            "Could not reach Kite.",
            exception.GetType().Name,
            exception.Message),

        JsonException => new Error(
            ConnectorErrorCodes.Unknown,
            "Kite returned a response this connector could not parse.",
            exception.GetType().Name,
            exception.Message),

        _ => new Error(
            ConnectorErrorCodes.Unknown,
            "An unexpected failure occurred while talking to Kite.",
            exception.GetType().Name,
            exception.Message),
    };

    /// <summary>
    /// <c>OrderException</c> covers everything from "the exchange rejected this" to "the OMS is
    /// having a bad day", and Kite returns it with a 500. The status line is therefore useless
    /// here, and the free text is the only discriminator.
    ///
    /// The default is <see cref="ConnectorErrorCodes.OrderRejected"/> — a NON-retryable code — on
    /// purpose. An order rejection misclassified as a transient server error is retried by the
    /// host's resilience decorator, and a retried placement is a duplicate order with real money
    /// behind it. The reverse mistake costs one manual retry.
    /// </summary>
    private static string ClassifyOrderException(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ConnectorErrorCodes.OrderRejected;
        }

        var text = message.ToUpperInvariant();

        if (Contains(text, "INSUFFICIENT FUND", "MARGIN EXCEEDS", "AVAILABLE MARGIN", "RMS:MARGIN"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "MARKET IS CLOSED", "MARKET CLOSED", "OUTSIDE MARKET HOURS", "TRADING NOT ALLOWED"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(text, "RMS:", "RISK", "BLOCKED FOR TRADING", "BAN PERIOD"))
        {
            return ConnectorErrorCodes.RiskRejected;
        }

        if (Contains(text, "ORDER NOT FOUND", "INVALID ORDER", "DOES NOT EXIST"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        return ConnectorErrorCodes.OrderRejected;
    }

    /// <summary>
    /// <c>InputException</c> is usually a genuine bad request, but Kite also uses it for an
    /// unknown tradingsymbol — and that is InstrumentNotFound, which the caller handles very
    /// differently from "you sent a malformed field".
    /// </summary>
    private static string ClassifyInputException(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            var text = message.ToUpperInvariant();

            if (Contains(text, "INVALID TRADINGSYMBOL", "INVALID INSTRUMENT", "INVALID `TRADINGSYMBOL`",
                    "SYMBOL NOT FOUND", "INVALID INSTRUMENT_TOKEN"))
            {
                return ConnectorErrorCodes.InstrumentNotFound;
            }

            if (Contains(text, "ORDER NOT FOUND", "INVALID ORDER_ID", "INVALID `ORDER_ID`"))
            {
                return ConnectorErrorCodes.OrderNotFound;
            }
        }

        return ConnectorErrorCodes.InvalidRequest;
    }

    /// <summary>
    /// <c>GeneralException</c> is Kite's "unclassified", so the status line is worth more than
    /// the name. A 5xx is the broker being unavailable; anything else has no honest mapping and
    /// defers to the transport rules.
    /// </summary>
    private static string? ClassifyGeneralException(VendorErrorContext context) => context.HttpStatus switch
    {
        >= 500 => ConnectorErrorCodes.BrokerUnavailable,
        _ => null,
    };

    /// <summary>
    /// Last resort, over the broker's free text. Deliberately conservative and deliberately
    /// narrow: only phrases whose meaning is unambiguous are matched, and everything else returns
    /// null so the transport mapping decides. A greedy matcher here is how "insufficient" in an
    /// unrelated sentence turns a rejected order into a funding prompt.
    /// </summary>
    private static string? ClassifyFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = message.ToUpperInvariant();

        if (Contains(text, "TOO MANY REQUESTS", "RATE LIMIT"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        if (Contains(text, "INSUFFICIENT FUND", "INSUFFICIENT MARGIN"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "INVALID SESSION", "TOKEN EXPIRED", "SESSION EXPIRED"))
        {
            return ConnectorErrorCodes.SessionExpired;
        }

        return null;
    }

    /// <summary>
    /// Whether the broker refused the request because of the IP address it came from.
    ///
    /// SEBI's retail-algo rules require API orders to come from a static IP the user has registered
    /// with the broker. The refusal arrives under whatever exception type the broker files it with,
    /// and treating it as a session or permission problem sends the user round a login loop that
    /// cannot help, so it is recognised from the text and checked before the type.
    /// </summary>
    private static bool IsUnregisteredIp(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && Contains(
            message.ToUpperInvariant(),
            "IP ADDRESS", "STATIC IP", "REGISTERED IP", "WHITELIST", "WHITE LIST", "WHITE-LIST");

    private static bool Contains(string haystack, params ReadOnlySpan<string> needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string MapStatusCode(int statusCode) => statusCode switch
    {
        400 => ConnectorErrorCodes.InvalidRequest,
        403 => ConnectorErrorCodes.SessionExpired,
        404 => ConnectorErrorCodes.InvalidRequest,
        405 => ConnectorErrorCodes.InvalidRequest,
        410 => ConnectorErrorCodes.NotSupported,
        428 => ConnectorErrorCodes.OrderRejected,
        429 => ConnectorErrorCodes.RateLimited,
        502 or 503 => ConnectorErrorCodes.BrokerUnavailable,
        504 => ConnectorErrorCodes.Timeout,
        >= 500 => ConnectorErrorCodes.BrokerUnavailable,
        >= 400 => ConnectorErrorCodes.InvalidRequest,
        _ => ConnectorErrorCodes.Unknown,
    };

    private static string WithBrokerWords(string ours, VendorErrorContext context) =>
        string.IsNullOrWhiteSpace(context.VendorMessage) ? ours : $"{ours} {context.VendorMessage}";

    private static VendorPayload ReadPayload(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new VendorPayload(null, null, null);
        }

        var snippet = body.Length <= MaxBodySnippet
            ? body
            : string.Concat(body.AsSpan(0, MaxBodySnippet), "…[truncated]");

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new VendorPayload(null, null, snippet);
            }

            return new VendorPayload(
                ReadString(document.RootElement, "error_type"),
                ReadString(document.RootElement, "message"),
                snippet);
        }
        catch (JsonException)
        {
            // Not JSON at all — a load balancer page or a proxy error. The snippet is what lets
            // support recognise that in the audit log.
            return new VendorPayload(null, null, snippet);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private readonly record struct VendorPayload(string? ErrorType, string? Message, string? RawSnippet);
}

/// <summary>Errors this connector raises itself, without the broker having said anything.</summary>
internal static class ZerodhaErrors
{
    /// <summary>
    /// A field the connector needs was absent from an otherwise successful response. Names the
    /// route and the field, because "the broker's response could not be understood" with no
    /// further detail is the single least actionable message a support ticket can carry.
    /// </summary>
    public static Error MissingField(string route, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"Kite's response from {route} did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route"] = route,
            ["field"] = field,
        });

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"Kite has no order with id '{brokerOrderId}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brokerOrderId"] = brokerOrderId,
        });

    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);
}
