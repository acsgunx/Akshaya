using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Translates the FYERS failure vocabulary into the canonical <see cref="ConnectorErrorCodes"/>
/// set, without ever discarding what the broker actually said.
///
/// Two rules govern everything here:
///
/// 1. The vendor's own code and message are copied verbatim into <see cref="Error.VendorCode"/>
///    and <see cref="Error.VendorMessage"/>. Support answers "what did FYERS say" from the audit
///    log, and a canonical code alone cannot answer it.
/// 2. The canonical code decides whether the host retries. Getting that wrong in the optimistic
///    direction is expensive: a rejected order classified as retryable becomes a duplicate
///    order. When in doubt this maps to a non-retryable code.
///
/// FYERS reports failures as a NEGATIVE integer in the <c>code</c> field, alongside
/// <c>s: "error"</c> — and it does so on HTTP 200 as readily as on a 4xx, so the body is
/// inspected regardless of the status line.
/// </summary>
public sealed class FyersErrorMapper : IVendorErrorMapper
{
    /// <summary>How much of an unparseable body to keep. Enough to identify a WAF or proxy page.</summary>
    private const int MaxBodySnippet = 512;

    // The documented FYERS error codes, exactly as they appear in the `code` field.

    /// <summary>The access token has expired.</summary>
    public const int CodeTokenExpired = -8;

    /// <summary>The token supplied is not a valid one.</summary>
    public const int CodeInvalidToken = -15;

    /// <summary>FYERS could not authenticate the token at all.</summary>
    public const int CodeUnauthenticatedToken = -16;

    /// <summary>The token is either invalid or expired; FYERS does not say which.</summary>
    public const int CodeInvalidOrExpiredToken = -17;

    /// <summary>One or more parameters were rejected. The message names them.</summary>
    public const int CodeInvalidParameters = -50;

    /// <summary>An order id that FYERS has no record of.</summary>
    public const int CodeInvalidOrderId = -51;

    /// <summary>A position id that FYERS has no record of.</summary>
    public const int CodeInvalidPositionId = -53;

    /// <summary>Order placement was rejected. The message carries the exchange's reason.</summary>
    public const int CodeOrderRejected = -99;

    /// <summary>The symbol is not one FYERS trades.</summary>
    public const int CodeInvalidSymbol = -300;

    /// <summary>
    /// Two entirely different failures share this code: an invalid app id, and "no position
    /// available to exit". <see cref="VendorErrorContext.Path"/> is what tells them apart.
    /// </summary>
    public const int CodeAppIdOrNoPosition = -352;

    /// <summary>Rate limit exceeded — per second, per minute or per day.</summary>
    public const int CodeRateLimited = -429;

    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        if (TryReadVendorCode(context.VendorCode, out var code))
        {
            var mapped = MapVendorCode(code, context.Path);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        // No numeric code, or one this mapper does not recognise: try the free text. Returning
        // null here is deliberate — it tells the caller to fall back to the transport mapping
        // rather than have this mapper guess.
        return ClassifyFromMessage(context.VendorMessage);
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.SessionExpired => "The FYERS session has expired; sign in again.",
        ConnectorErrorCodes.ReauthRequired =>
            TryReadVendorCode(context.VendorCode, out var code) && code == CodeAppIdOrNoPosition
                ? "FYERS did not recognise this app id. Check the app in the FYERS API dashboard; it may "
                  + "have been deleted or its permissions changed."
                : "FYERS needs you to sign in again.",
        ConnectorErrorCodes.InvalidCredentials => WithBrokerWords(
            "FYERS did not accept these app credentials.", context),
        ConnectorErrorCodes.ChallengeFailed => WithBrokerWords(
            "FYERS did not accept the login response.", context),
        ConnectorErrorCodes.InsufficientFunds => "The FYERS account does not have enough funds for this order.",

        // THE BROKER'S REASON SURVIVES. Our sentence says an order was rejected; only the
        // exchange's own text says whether it was a circuit limit, a freeze quantity, a banned
        // F&O scrip or a closed market — and those need four different responses from the trader.
        ConnectorErrorCodes.OrderRejected => WithBrokerWords("FYERS rejected the order.", context),
        ConnectorErrorCodes.OrderNotFound => "FYERS has no record of that order.",
        ConnectorErrorCodes.MarketClosed => "The market is closed for this instrument.",
        ConnectorErrorCodes.RiskRejected => "FYERS' risk checks blocked this order.",
        ConnectorErrorCodes.InstrumentNotFound => "FYERS does not recognise that instrument.",
        ConnectorErrorCodes.RateLimited =>
            "Too many requests to FYERS; wait and retry. Exceeding the per-minute limit three times "
            + "in one day blocks the account for the rest of the day.",
        ConnectorErrorCodes.Timeout => "FYERS did not respond in time.",
        ConnectorErrorCodes.BrokerUnavailable => "FYERS is currently unavailable.",
        ConnectorErrorCodes.NotSupported => "FYERS does not permit this action on this account.",
        ConnectorErrorCodes.InvalidRequest => WithBrokerWords("FYERS rejected the request as invalid.", context),

        // NO OPINION: SAY WHAT THE BROKER SAID. Our own wording is better than a vendor's ONLY
        // when we understood the failure well enough to write one; when we did not, the vendor's
        // text is the most useful thing available.
        _ => string.IsNullOrWhiteSpace(context.VendorMessage)
            ? "FYERS reported an error."
            : context.VendorMessage,
    };

    /// <summary>Maps a non-success HTTP response, reading the body's code when it has one.</summary>
    public Error MapHttp(int statusCode, string? responseBody, string? path = null)
    {
        var payload = ReadPayload(responseBody);
        var context = new VendorErrorContext(
            statusCode,
            payload.Code,
            payload.Message ?? payload.RawSnippet,
            path,
            payload.RawSnippet);

        var canonical = MapToCanonicalCode(context) ?? MapStatusCode(statusCode);

        return new Error(
            canonical,
            DescribeCanonicalCode(canonical, context),
            payload.Code ?? statusCode.ToString(CultureInfo.InvariantCulture),
            payload.Message ?? payload.RawSnippet);
    }

    /// <summary>
    /// Maps a FYERS response envelope that reported a failure. This is the usual shape: a
    /// business failure arrives as an HTTP 200 carrying <c>s: "error"</c> and a negative code,
    /// so a connector that only inspected status codes would report every rejection as a success.
    /// </summary>
    public Error MapEnvelope(int? code, string? message, string? path = null, int statusCode = 200)
    {
        var vendorCode = code?.ToString(CultureInfo.InvariantCulture);
        var context = new VendorErrorContext(statusCode, vendorCode, message, path, null);
        var canonical = MapToCanonicalCode(context) ?? MapStatusCode(statusCode);

        return new Error(
            canonical,
            DescribeCanonicalCode(canonical, context),
            vendorCode,
            message);
    }

    /// <summary>Maps a transport-level exception thrown before any response was read.</summary>
    public static Error MapException(Exception exception) => exception switch
    {
        // TaskCanceledException derives from OperationCanceledException, so the base type covers
        // both the HttpClient timeout and an ambient cancellation.
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            "FYERS did not respond in time.",
            exception.GetType().Name,
            exception.Message),

        // A socket-level failure is the broker or the network being unavailable, and it is safe
        // to retry a READ. The host's resilience decorator is what knows that a WRITE (an order
        // placement) must instead be reconciled against the order book.
        HttpRequestException or SocketException => new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            "Could not reach FYERS.",
            exception.GetType().Name,
            exception.Message),

        JsonException => new Error(
            ConnectorErrorCodes.Unknown,
            "FYERS returned a response this connector could not parse.",
            exception.GetType().Name,
            exception.Message),

        _ => new Error(
            ConnectorErrorCodes.Unknown,
            "An unexpected failure occurred while talking to FYERS.",
            exception.GetType().Name,
            exception.Message),
    };

    private static string? MapVendorCode(int code, string? path) => code switch
    {
        CodeTokenExpired or CodeInvalidOrExpiredToken => ConnectorErrorCodes.SessionExpired,

        // An invalid token is not the same as an expired one: a fresh login is the only fix,
        // and the session monitor must not sit refreshing something that was never valid.
        CodeInvalidToken or CodeUnauthenticatedToken => ConnectorErrorCodes.ReauthRequired,

        CodeInvalidParameters => ConnectorErrorCodes.InvalidRequest,
        CodeInvalidOrderId => ConnectorErrorCodes.OrderNotFound,
        CodeInvalidPositionId => ConnectorErrorCodes.InvalidRequest,
        CodeOrderRejected => ConnectorErrorCodes.OrderRejected,
        CodeInvalidSymbol => ConnectorErrorCodes.InstrumentNotFound,
        CodeRateLimited => ConnectorErrorCodes.RateLimited,

        // ONE CODE, TWO MEANINGS. FYERS documents -352 as both "invalid App ID" and, on the exit
        // route specifically, "no position available to exit". Only the path can separate them,
        // and the difference is stark: one is a broken integration that needs a new app, the
        // other is a flat account and an entirely ordinary outcome. Reporting a flat account as
        // ReauthRequired would send the trader round the login loop for nothing.
        CodeAppIdOrNoPosition => IsPositionsRoute(path)
            ? ConnectorErrorCodes.InvalidRequest
            : ConnectorErrorCodes.ReauthRequired,

        _ => null,
    };

    private static bool IsPositionsRoute(string? path) =>
        path is not null && path.Contains("/positions", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Last resort, over the broker's free text.
    ///
    /// Deliberately conservative and deliberately narrow. Only phrases whose meaning is
    /// unambiguous are matched; everything else returns null so the transport mapping decides.
    /// A greedy matcher here is how "insufficient" in an unrelated sentence turns a rejected
    /// order into an InsufficientFunds the UI renders as a funding prompt.
    /// </summary>
    private static string? ClassifyFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = message.ToUpperInvariant();

        if (Contains(text, "RATE LIMIT", "TOO MANY REQUESTS"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        if (Contains(text, "INSUFFICIENT FUND", "INSUFFICIENT BALANCE", "INSUFFICIENT MARGIN"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "MARKET IS CLOSED", "MARKET CLOSED", "OUTSIDE MARKET HOURS", "TRADING SESSION"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(text, "INVALID SYMBOL", "SYMBOL IS INVALID", "SYMBOL NOT FOUND"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (Contains(text, "TOKEN IS EXPIRED", "TOKEN EXPIRED", "SESSION EXPIRED"))
        {
            return ConnectorErrorCodes.SessionExpired;
        }

        return null;
    }

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
        401 => ConnectorErrorCodes.SessionExpired,
        403 => ConnectorErrorCodes.SessionExpired,
        404 => ConnectorErrorCodes.InvalidRequest,
        408 => ConnectorErrorCodes.Timeout,
        429 => ConnectorErrorCodes.RateLimited,
        504 => ConnectorErrorCodes.Timeout,
        >= 500 => ConnectorErrorCodes.BrokerUnavailable,
        >= 400 => ConnectorErrorCodes.InvalidRequest,
        _ => ConnectorErrorCodes.Unknown,
    };

    private static bool TryReadVendorCode(string? vendorCode, out int code) =>
        int.TryParse(vendorCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);

    private static string WithBrokerWords(string ours, VendorErrorContext context) =>
        string.IsNullOrWhiteSpace(context.VendorMessage)
            ? ours
            : $"{ours} {context.VendorMessage}";

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

            var code = document.RootElement.TryGetProperty("code", out var codeElement)
                ? codeElement.ValueKind switch
                {
                    JsonValueKind.Number => codeElement.GetRawText(),
                    JsonValueKind.String => codeElement.GetString(),
                    _ => null,
                }
                : null;

            var message = document.RootElement.TryGetProperty("message", out var messageElement)
                             && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;

            return new VendorPayload(code, message, snippet);
        }
        catch (JsonException)
        {
            // Not JSON at all — a Cloudflare challenge page or a proxy error. The snippet is
            // what lets support recognise that in the audit log.
            return new VendorPayload(null, null, snippet);
        }
    }

    private readonly record struct VendorPayload(string? Code, string? Message, string? RawSnippet);
}

/// <summary>Errors this connector raises itself, without the broker having said anything.</summary>
internal static class FyersErrors
{
    /// <summary>
    /// A field the connector needs was absent from an otherwise successful response. Names the
    /// route and the field, because "the broker's response could not be understood" with no
    /// further detail is the single least actionable message a support ticket can carry.
    /// </summary>
    public static Error MissingField(string route, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"FYERS' response from {route} did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route"] = route,
            ["field"] = field,
        });

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"FYERS has no order with id '{brokerOrderId}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brokerOrderId"] = brokerOrderId,
        });

    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);
}
