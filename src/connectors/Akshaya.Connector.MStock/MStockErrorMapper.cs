using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Translates mStock's failure vocabulary into the canonical <see cref="ConnectorErrorCodes"/>
/// set, without ever discarding what the broker actually said.
///
/// Two rules govern everything here:
///
/// 1. The vendor's own code and message are copied verbatim into <see cref="Error.VendorCode"/>
///    and <see cref="Error.VendorMessage"/>. Support answers "what did mStock say" from the
///    audit log, and a canonical code alone cannot answer it.
/// 2. The canonical code decides whether the host retries. Getting that wrong in the
///    optimistic direction is expensive: an <c>OrderException</c> classified as retryable
///    becomes a duplicate order. When in doubt this maps to a non-retryable code.
///
/// mStock's taxonomy is the Kite lineage — TokenException, InputException, MarginException and
/// friends — and it is carried in the JSON body of an otherwise ordinary-looking response, so
/// the body is inspected even when the HTTP status is 200.
/// </summary>
public sealed class MStockErrorMapper : IVendorErrorMapper
{
    /// <summary>How much of an unparseable body to keep. Enough to identify a WAF or proxy page.</summary>
    private const int MaxBodySnippet = 512;

    // mStock / Kite-lineage error types, exactly as they appear in the payload.
    private const string TokenException = "TokenException";
    private const string UserException = "UserException";
    private const string TwoFactorException = "TwoFAException";
    private const string InputException = "InputException";
    private const string OrderException = "OrderException";
    private const string MarginException = "MarginException";
    private const string HoldingException = "HoldingException";
    private const string PermissionException = "PermissionException";
    private const string NetworkException = "NetworkException";
    private const string DataException = "DataException";
    private const string GeneralException = "GeneralException";

    /// <summary>Maps a non-success HTTP response.</summary>
    public Error MapHttp(int statusCode, string? responseBody)
    {
        var payload = ReadPayload(responseBody);

        // The body is more specific than the status line whenever it parses, so it wins.
        if (payload.ErrorType is not null || payload.Message is not null)
        {
            return FromPayload(payload, statusCode);
        }

        return FromStatusCode(statusCode, payload.RawSnippet);
    }

    /// <summary>Maps a transport-level exception thrown before any response was read.</summary>
    public Error MapException(Exception exception) => exception switch
    {
        // TaskCanceledException derives from OperationCanceledException, so the base type
        // covers both the HttpClient timeout and an ambient cancellation.
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            "mStock did not respond in time.",
            exception.GetType().Name,
            exception.Message),

        // A socket-level failure is the broker or the network being unavailable, and it is
        // safe to retry a READ. The host's resilience decorator is what knows that a WRITE
        // (an order placement) must instead be reconciled against the order book.
        HttpRequestException or SocketException or WebSocketException => new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            "Could not reach mStock.",
            exception.GetType().Name,
            exception.Message),

        JsonException => new Error(
            ConnectorErrorCodes.Unknown,
            "mStock returned a response this connector could not parse.",
            exception.GetType().Name,
            exception.Message),

        _ => new Error(
            ConnectorErrorCodes.Unknown,
            "An unexpected failure occurred while talking to mStock.",
            exception.GetType().Name,
            exception.Message),
    };

    /// <summary>
    /// Maps an HTTP 200 whose envelope says <c>status: "error"</c>. This is mStock's usual way
    /// of reporting a business failure — a rejected order comes back as a 200 — so a connector
    /// that only inspects status codes would report every rejection as a success.
    /// </summary>
    public Error MapEnvelope(string? status, string? errorType, string? message, int statusCode = 200) =>
        FromPayload(new VendorPayload(errorType, message, status, null), statusCode);

    private static Error FromPayload(VendorPayload payload, int statusCode)
    {
        var vendorCode = payload.ErrorType ?? statusCode.ToString(CultureInfo.InvariantCulture);
        var vendorMessage = payload.Message ?? payload.RawSnippet;
        var message = payload.Message ?? "mStock reported an error.";

        var code = payload.ErrorType switch
        {
            TokenException => statusCode is 403 or 401
                // 401/403 + TokenException is the everyday case: the access token died, either
                // because twelve hours elapsed or because IST midnight passed. Both need a
                // fresh interactive login — mStock's refresh token is itself day-bound.
                ? ConnectorErrorCodes.SessionExpired
                : ConnectorErrorCodes.ReauthRequired,

            UserException => ConnectorErrorCodes.InvalidCredentials,
            TwoFactorException => ConnectorErrorCodes.ChallengeFailed,
            InputException => ConnectorErrorCodes.InvalidRequest,
            MarginException => ConnectorErrorCodes.InsufficientFunds,
            HoldingException => ConnectorErrorCodes.OrderRejected,
            OrderException => ClassifyOrderException(payload.Message),
            PermissionException => ConnectorErrorCodes.NotSupported,
            NetworkException or GeneralException => ConnectorErrorCodes.BrokerUnavailable,
            DataException => ConnectorErrorCodes.Unknown,
            _ => ClassifyFromMessage(payload.Message) ?? FromStatusCodeOnly(statusCode),
        };

        return new Error(code, message, vendorCode, vendorMessage);
    }

    /// <summary>
    /// <c>OrderException</c> is mStock's catch-all for anything the RMS or the exchange did not
    /// like, and the distinctions the platform cares about are only in the free text. Getting
    /// these apart matters: an insufficient-funds rejection is shown to the trader as a funding
    /// problem, a market-closed rejection is shown as a timing problem, and neither is retried.
    /// </summary>
    private static string ClassifyOrderException(string? message) =>
        ClassifyFromMessage(message) ?? ConnectorErrorCodes.OrderRejected;

    private static string? ClassifyFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        if (ContainsAny(message, "insufficient fund", "insufficient balance", "not enough", "margin shortfall"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (ContainsAny(message, "market is closed", "market closed", "outside market hours", "trading session"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (ContainsAny(message, "rms", "risk", "blocked for trading", "not allowed to trade", "square off"))
        {
            return ConnectorErrorCodes.RiskRejected;
        }

        if (ContainsAny(message, "order not found", "invalid order", "order does not exist", "no such order"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        if (ContainsAny(message, "instrument", "symbol not found", "invalid tradingsymbol", "scrip"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (ContainsAny(message, "too many requests", "rate limit", "throttl"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        if (ContainsAny(message, "session expired", "invalid token", "token expired", "invalid session"))
        {
            return ConnectorErrorCodes.SessionExpired;
        }

        if (ContainsAny(message, "invalid otp", "incorrect otp", "invalid totp", "otp expired"))
        {
            return ConnectorErrorCodes.ChallengeFailed;
        }

        if (ContainsAny(message, "invalid password", "invalid user", "invalid credential", "wrong password"))
        {
            return ConnectorErrorCodes.InvalidCredentials;
        }

        return null;
    }

    private static Error FromStatusCode(int statusCode, string? bodySnippet) => new(
        FromStatusCodeOnly(statusCode),
        DescribeStatus(statusCode),
        statusCode.ToString(CultureInfo.InvariantCulture),
        bodySnippet);

    private static string FromStatusCodeOnly(int statusCode) => statusCode switch
    {
        400 => ConnectorErrorCodes.InvalidRequest,
        401 => ConnectorErrorCodes.SessionExpired,
        403 => ConnectorErrorCodes.ReauthRequired,
        404 => ConnectorErrorCodes.InvalidRequest,
        408 => ConnectorErrorCodes.Timeout,
        429 => ConnectorErrorCodes.RateLimited,
        >= 500 and < 600 => ConnectorErrorCodes.BrokerUnavailable,
        >= 200 and < 300 => ConnectorErrorCodes.Unknown,
        _ => ConnectorErrorCodes.Unknown,
    };

    private static string DescribeStatus(int statusCode) => statusCode switch
    {
        400 => "mStock rejected the request as malformed.",
        401 => "The mStock session is no longer valid.",
        403 => "mStock refused the request; sign in again.",
        404 => "mStock has no such resource.",
        408 => "mStock did not respond in time.",
        429 => "mStock rate limit exceeded.",
        >= 500 and < 600 => $"mStock returned {statusCode}; the broker is unavailable.",
        _ => $"mStock returned an unexpected HTTP {statusCode}.",
    };

    private static bool ContainsAny(string haystack, params ReadOnlySpan<string> needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pulls the vendor fields out of a body that may or may not be JSON. A gateway timeout
    /// often arrives as an HTML page; keeping a snippet of it is what lets an operator tell a
    /// broker outage apart from our own proxy misbehaving.
    /// </summary>
    private static VendorPayload ReadPayload(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new VendorPayload(null, null, null, null);
        }

        var snippet = body.Length > MaxBodySnippet ? body[..MaxBodySnippet] : body;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new VendorPayload(null, null, null, snippet);
            }

            var root = document.RootElement;
            return new VendorPayload(
                ReadString(root, "error_type") ?? ReadString(root, "errorcode") ?? ReadString(root, "errorCode"),
                ReadString(root, "message") ?? ReadString(root, "errorMessage") ?? ReadString(root, "error"),
                ReadString(root, "status"),
                snippet);
        }
        catch (JsonException)
        {
            // Not JSON. That is information in itself — keep the snippet and fall back to the
            // status line.
            return new VendorPayload(null, null, null, snippet);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private readonly record struct VendorPayload(
        string? ErrorType,
        string? Message,
        string? Status,
        string? RawSnippet);
}

/// <summary>
/// Convenience wrappers so callers can build canonical errors without repeating the mapper's
/// vocabulary. Kept next to the mapper so the two never drift apart.
/// </summary>
internal static class MStockErrors
{
    public static Error MissingField(string route, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"mStock's response to {route} omitted the required field '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route"] = route,
            ["field"] = field,
        });

    public static Error NoSession() => new(
        ConnectorErrorCodes.SessionExpired,
        "This mStock connector was created without a session; only the authentication facet is usable.");

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"mStock has no order {brokerOrderId} in either the equity or the derivative segment.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brokerOrderId"] = brokerOrderId,
        });

    public static Error Http(HttpStatusCode statusCode, string message) => new(
        statusCode switch
        {
            HttpStatusCode.Unauthorized => ConnectorErrorCodes.SessionExpired,
            HttpStatusCode.Forbidden => ConnectorErrorCodes.ReauthRequired,
            HttpStatusCode.TooManyRequests => ConnectorErrorCodes.RateLimited,
            HttpStatusCode.RequestTimeout => ConnectorErrorCodes.Timeout,
            _ => ConnectorErrorCodes.BrokerUnavailable,
        },
        message,
        ((int)statusCode).ToString(CultureInfo.InvariantCulture),
        statusCode.ToString());
}
