using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Client Portal failures to canonical <see cref="ConnectorErrorCodes"/>, keeping IBKR's words.
///
/// IBKR publishes no error-code table for the Web API: a failure is an HTTP status and an <c>error</c> sentence.
/// So the status decides first, narrowly, and the sentence second. A 401 from a gateway is a signed-out brokerage
/// session — nothing a token refresh can mend, so ReauthRequired rather than the SDK's default SessionExpired.
/// </summary>
public sealed class IbkrErrorMapper : IVendorErrorMapper
{
    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        if (context.HttpStatus == 401)
        {
            return ConnectorErrorCodes.ReauthRequired;
        }

        return FromText(context.VendorMessage ?? context.RawBody);
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.ReauthRequired => "The Client Portal Gateway is not signed in to IBKR. Sign in to the gateway again.",
        ConnectorErrorCodes.SessionExpired => "The IBKR brokerage session has ended. Sign in to the Client Portal Gateway again.",
        ConnectorErrorCodes.GatewayUnavailable => WithBrokerWords("The Client Portal Gateway is not reachable or not connected to IBKR.", context),
        ConnectorErrorCodes.RateLimited => "Too many requests to IBKR; wait and retry. Repeated breaches put the address in a ten-minute penalty box.",
        ConnectorErrorCodes.BrokerUnavailable => "IBKR is currently unavailable.",
        ConnectorErrorCodes.InsufficientFunds => WithBrokerWords("The IBKR account does not have the funds or margin for this order.", context),
        ConnectorErrorCodes.MarketClosed => WithBrokerWords("The market is closed for this instrument.", context),
        ConnectorErrorCodes.OrderNotFound => "IBKR has no working order with that id in this brokerage session.",
        ConnectorErrorCodes.InstrumentNotFound => WithBrokerWords("IBKR does not recognise that contract.", context),
        ConnectorErrorCodes.NotSupported => WithBrokerWords("IBKR does not permit this for this username or account.", context),
        ConnectorErrorCodes.OrderRejected => WithBrokerWords("IBKR rejected the order.", context),
        ConnectorErrorCodes.InvalidRequest => WithBrokerWords("IBKR rejected the request as invalid.", context),
        ConnectorErrorCodes.Timeout => "IBKR did not respond in time. For an order, check the order book before trying again.",
        _ => string.IsNullOrWhiteSpace(context.VendorMessage) ? "IBKR reported an error." : context.VendorMessage,
    };

    /// <summary>An order route's refusal sentence: classified where possible, an order rejection otherwise.</summary>
    internal Error MapOrderText(string message, string path)
    {
        var canonical = FromText(message) ?? ConnectorErrorCodes.OrderRejected;
        var context = new VendorErrorContext(null, null, message, path);
        return new Error(canonical, DescribeCanonicalCode(canonical, context), VendorCode: null, VendorMessage: message);
    }

    internal static string? FromText(string? message)
    {
        var text = message?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Contains(text, "not authenticated", "no session", "not logged in", "please log in", "session is not", "competing"))
        {
            return ConnectorErrorCodes.ReauthRequired;
        }

        if (Contains(text, "no bridge", "gateway is not"))
        {
            return ConnectorErrorCodes.GatewayUnavailable;
        }

        if (Contains(text, "insufficient"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "doesn't exist", "does not exist", "order not found", "unknown order", "cannot find order", "no such order"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        if (Contains(text, "invalid conid", "no security definition", "contract not found", "invalid contract"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (Contains(text, "market is closed", "exchange is closed", "not open for trading", "outside of regular trading hours"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(text, "trading permission", "not permitted", "not allowed", "no market data permission"))
        {
            return ConnectorErrorCodes.NotSupported;
        }

        if (Contains(text, "too many requests", "rate limit", "pacing"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        return null;
    }

    internal static Error MapException(Exception exception, string what) => exception switch
    {
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            $"The Client Portal Gateway did not answer {what} in time.",
            exception.GetType().Name,
            exception.Message),
        AuthenticationException or HttpRequestException { InnerException: AuthenticationException } => new Error(
            ConnectorErrorCodes.GatewayUnavailable,
            "The Client Portal Gateway's certificate was refused. A gateway on another machine presents a self-signed "
            + "certificate; install a trusted one, or set the IBKR allowUntrustedCertificate setting knowingly.",
            exception.GetType().Name,
            exception.Message),
        HttpRequestException or SocketException or WebSocketException or IOException => new Error(
            ConnectorErrorCodes.GatewayUnavailable,
            $"Could not reach the Client Portal Gateway ({what}).",
            exception.GetType().Name,
            exception.Message),
        JsonException or InvalidDataException => new Error(
            ConnectorErrorCodes.Unknown,
            $"The Client Portal Gateway's answer to {what} could not be read.",
            exception.GetType().Name,
            exception.Message),
        _ => new Error(
            ConnectorErrorCodes.Unknown,
            $"An unexpected failure occurred talking to the Client Portal Gateway ({what}).",
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
internal static class IbkrErrors
{
    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);

    public static Error MissingField(string route, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"The Client Portal Gateway's answer to {route} did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["route"] = route, ["field"] = field });

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"IBKR has no order with id '{brokerOrderId}' in this brokerage session.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["brokerOrderId"] = brokerOrderId });

    public static Error NoGatewayAddress() => new(
        ConnectorErrorCodes.GatewayUnavailable,
        "No Client Portal Gateway address was resolved for this link. Configure Connectors:Gateways:ibkr-cpgw, or run "
        + "the gateway on this machine on its default port, 5000. See ADR 0008.");

    public static Error SignedOut(GatewayAddress gateway) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"The Client Portal Gateway at {gateway} is not signed in to IBKR. Sign in to it again.");

    public static Error MalformedSession(string detail) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"This IBKR link's session is incomplete ({detail}). Link the account again.");

    /// <summary>An order reply message this connector is not configured to confirm. The order is not working.</summary>
    public static Error UnconfirmedPrompt(string text, IReadOnlyList<string> messageIds, string? reason)
    {
        var ids = string.Join(",", messageIds);

        return new Error(
            ConnectorErrorCodes.OrderRejected,
            reason is null
                ? $"IBKR asked for confirmation before working this order, and it was not given: {text} The order is not "
                  + $"working. To let this connector confirm this kind of question, add '{ids}' to the IBKR "
                  + "autoConfirmMessageIds setting."
                : $"{reason}: {text}",
            VendorCode: ids,
            VendorMessage: text,
            Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["messageIds"] = ids });
    }
}
