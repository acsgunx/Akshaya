using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Longbridge's failures to canonical <see cref="ConnectorErrorCodes"/>, keeping the vendor's words.
///
/// Two vocabularies arrive here. REST routes answer with a six-digit business code in the envelope,
/// documented on the error-codes page (401003 token expired, 429001/429002 rate limited, 403201 bad
/// signature…). The quote and trade sockets answer a failed request with a non-zero status byte and a
/// protobuf error whose code is from the 3016xx family (301606 rate limited, 301605 too many
/// subscriptions…). Both are mapped by code first; free text is consulted only for the order
/// rejections, whose codes are not published, and then narrowly.
/// </summary>
public sealed class LongbridgeErrorMapper : IVendorErrorMapper
{
    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        var byCode = context.VendorCode switch
        {
            "401003" => ConnectorErrorCodes.SessionExpired,
            "403201" or "403203" or "403205" => ConnectorErrorCodes.InvalidCredentials,
            "403202" => ConnectorErrorCodes.InvalidRequest,
            "429001" or "429002" or "301606" => ConnectorErrorCodes.RateLimited,
            "500000" or "301602" => ConnectorErrorCodes.BrokerUnavailable,
            "301600" or "301605" => ConnectorErrorCodes.InvalidRequest,

            // OAuth token endpoint errors (RFC 6749).
            "invalid_grant" => ConnectorErrorCodes.ReauthRequired,
            "invalid_client" or "unauthorized_client" => ConnectorErrorCodes.InvalidCredentials,
            "invalid_request" => ConnectorErrorCodes.InvalidRequest,
            _ => null,
        };

        if (byCode is not null)
        {
            return byCode;
        }

        var text = context.VendorMessage?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Contains(text, "insufficient fund", "insufficient buying power", "insufficient cash", "not enough cash",
                "insufficient balance"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "market closed", "market is closed", "not in trading hours", "non-trading", "outside trading hours"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(text, "order not found", "order does not exist", "order not exist"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        if (Contains(text, "security not found", "invalid symbol", "symbol not found", "unknown security"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (Contains(text, "no quote permission", "no permission", "permission denied", "not open openapi"))
        {
            return ConnectorErrorCodes.NotSupported;
        }

        if (Contains(text, "token expired", "invalid token", "access token"))
        {
            return ConnectorErrorCodes.SessionExpired;
        }

        return null;
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.SessionExpired => "The Longbridge access token has expired; it is refreshed automatically when possible.",
        ConnectorErrorCodes.ReauthRequired => "Longbridge needs you to sign in again.",
        ConnectorErrorCodes.InvalidCredentials => WithBrokerWords(
            "Longbridge did not accept these credentials, or this deployment's address is not allowed.", context),
        ConnectorErrorCodes.RateLimited => "Too many requests to Longbridge; wait and retry.",
        ConnectorErrorCodes.BrokerUnavailable => "Longbridge is currently unavailable.",
        ConnectorErrorCodes.InsufficientFunds => WithBrokerWords("The Longbridge account does not have the buying power for this order.", context),
        ConnectorErrorCodes.MarketClosed => "The market is closed for this instrument.",
        ConnectorErrorCodes.OrderNotFound => "Longbridge has no record of that order.",
        ConnectorErrorCodes.InstrumentNotFound => WithBrokerWords("Longbridge does not recognise that security.", context),
        ConnectorErrorCodes.NotSupported => WithBrokerWords(
            "Longbridge does not permit this on this account, or the OpenAPI permission for it is not enabled.", context),
        ConnectorErrorCodes.OrderRejected => WithBrokerWords("Longbridge rejected the order.", context),
        ConnectorErrorCodes.InvalidRequest => WithBrokerWords("Longbridge rejected the request as invalid.", context),
        ConnectorErrorCodes.Timeout => "Longbridge did not respond in time. For an order, check the order book before trying again.",
        _ => string.IsNullOrWhiteSpace(context.VendorMessage) ? "Longbridge reported an error." : context.VendorMessage,
    };

    /// <summary>An envelope with a non-zero code.</summary>
    internal Error MapEnvelope(int code, string? message, string path, bool isTradeWrite)
    {
        var vendorCode = code.ToString(CultureInfo.InvariantCulture);
        var context = new VendorErrorContext(null, vendorCode, message, path, null);
        var canonical = MapToCanonicalCode(context)
                        ?? (isTradeWrite ? ConnectorErrorCodes.OrderRejected : ConnectorErrorCodes.Unknown);

        return new Error(canonical, DescribeCanonicalCode(canonical, context), vendorCode, message);
    }

    /// <summary>A socket response with a non-zero status byte.</summary>
    internal Error MapSocket(byte command, byte status, LbError? detail)
    {
        var vendorCode = detail is { Code: > 0 } error
            ? error.Code.ToString(CultureInfo.InvariantCulture)
            : $"status {status.ToString(CultureInfo.InvariantCulture)}";

        var context = new VendorErrorContext(null, vendorCode, detail?.Message, LongbridgeCommand.Name(command), null);
        var canonical = MapToCanonicalCode(context) ?? ConnectorErrorCodes.Unknown;

        return new Error(
            canonical,
            DescribeCanonicalCode(canonical, context),
            vendorCode,
            detail?.Message,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["command"] = LongbridgeCommand.Name(command) });
    }

    internal static Error MapException(Exception exception, string what) => exception switch
    {
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            $"Longbridge did not answer {what} in time.",
            exception.GetType().Name,
            exception.Message),
        HttpRequestException or SocketException or WebSocketException or IOException or ObjectDisposedException => new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            $"Could not reach Longbridge ({what}).",
            exception.GetType().Name,
            exception.Message),
        InvalidDataException or JsonException => new Error(
            ConnectorErrorCodes.Unknown,
            $"Longbridge's {what} response could not be read.",
            exception.GetType().Name,
            exception.Message),
        _ => new Error(
            ConnectorErrorCodes.Unknown,
            $"An unexpected failure occurred talking to Longbridge ({what}).",
            exception.GetType().Name,
            exception.Message),
    };

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

    private static string WithBrokerWords(string ours, VendorErrorContext context) =>
        string.IsNullOrWhiteSpace(context.VendorMessage) ? ours : $"{ours} {context.VendorMessage}";
}

/// <summary>Errors this connector raises itself.</summary>
internal static class LongbridgeErrors
{
    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);

    public static Error MissingField(string route, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"Longbridge's response from {route} did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["route"] = route, ["field"] = field });

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"Longbridge has no order with id '{brokerOrderId}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["brokerOrderId"] = brokerOrderId });

    public static Error SocketClosed(string detail) => new(
        ConnectorErrorCodes.BrokerUnavailable,
        $"The Longbridge socket closed: {detail}");

    public static Error MalformedSession(string detail) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"This Longbridge link's session is incomplete ({detail}). Link the account again.");
}
