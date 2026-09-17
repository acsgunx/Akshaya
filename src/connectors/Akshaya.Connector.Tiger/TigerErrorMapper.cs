using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Tiger's failures to canonical <see cref="ConnectorErrorCodes"/>, keeping Tiger's words.
///
/// Tiger answers every method with <c>{ code, message, data }</c>; anything but code 0 is a failure, and the code
/// space is not published in the SDK this connector was written from. So the message decides, and it is matched in
/// English and in Chinese, because the gateway answers in the language the request asked for and falls back to
/// Chinese for some gateway-level refusals.
/// </summary>
public sealed class TigerErrorMapper : IVendorErrorMapper
{
    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        if (context.HttpStatus is 401 or 403)
        {
            return ConnectorErrorCodes.InvalidCredentials;
        }

        var text = context.VendorMessage?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Contains(text, "sign", "signature", "private key", "public key", "验签", "签名"))
        {
            return ConnectorErrorCodes.InvalidCredentials;
        }

        if (Contains(text, "token", "unauthorized", "not authorized", "登录", "未授权"))
        {
            return ConnectorErrorCodes.ReauthRequired;
        }

        if (Contains(text, "permission", "not open", "no quote", "权限", "未开通"))
        {
            return ConnectorErrorCodes.NotSupported;
        }

        if (Contains(text, "frequency", "too many", "rate limit", "限流", "频繁"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        if (Contains(text, "insufficient", "buying power", "not enough", "资金不足", "可用资金"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(text, "market closed", "not trading", "trading hours", "休市", "非交易"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(text, "order not found", "order does not exist", "no such order", "订单不存在"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        if (Contains(text, "contract not found", "symbol not found", "invalid symbol", "no contract", "合约不存在", "标的不存在"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (Contains(text, "timeout", "超时"))
        {
            return ConnectorErrorCodes.Timeout;
        }

        return null;
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.InvalidCredentials => WithBrokerWords(
            "Tiger did not accept this application's credentials or its signature. Check the tiger id, and that the "
            + "public half of this private key is registered on the Tiger console.", context),
        ConnectorErrorCodes.ReauthRequired => WithBrokerWords("Tiger needs this link's credentials renewed.", context),
        ConnectorErrorCodes.NotSupported => WithBrokerWords(
            "Tiger does not permit this for this account, or the OpenAPI permission for it is not granted.", context),
        ConnectorErrorCodes.RateLimited => "Too many requests to Tiger; wait and retry.",
        ConnectorErrorCodes.InsufficientFunds => WithBrokerWords("The Tiger account does not have the funds for this order.", context),
        ConnectorErrorCodes.MarketClosed => WithBrokerWords("The market is closed for this instrument.", context),
        ConnectorErrorCodes.OrderNotFound => "Tiger has no record of that order.",
        ConnectorErrorCodes.InstrumentNotFound => WithBrokerWords("Tiger does not recognise that contract.", context),
        ConnectorErrorCodes.OrderRejected => WithBrokerWords("Tiger rejected the order.", context),
        ConnectorErrorCodes.InvalidRequest => WithBrokerWords("Tiger rejected the request as invalid.", context),
        ConnectorErrorCodes.Timeout => "Tiger did not respond in time. For an order, check the order book before trying again.",
        ConnectorErrorCodes.BrokerUnavailable => "Tiger is currently unavailable.",
        _ => string.IsNullOrWhiteSpace(context.VendorMessage) ? "Tiger reported an error." : context.VendorMessage,
    };

    /// <summary>An answer whose code is not zero.</summary>
    internal Error MapEnvelope(long code, string? message, string method, bool isTradeWrite)
    {
        var vendorCode = code.ToString(CultureInfo.InvariantCulture);
        var context = new VendorErrorContext(null, vendorCode, message, method);
        var canonical = MapToCanonicalCode(context)
                        ?? (isTradeWrite ? ConnectorErrorCodes.OrderRejected : ConnectorErrorCodes.Unknown);

        return new Error(canonical, DescribeCanonicalCode(canonical, context), vendorCode, message);
    }

    internal static Error MapException(Exception exception, string what) => exception switch
    {
        OperationCanceledException or TimeoutException => new Error(
            ConnectorErrorCodes.Timeout,
            $"Tiger did not answer {what} in time.",
            exception.GetType().Name,
            exception.Message),
        HttpRequestException or SocketException or IOException => new Error(
            ConnectorErrorCodes.BrokerUnavailable,
            $"Could not reach Tiger ({what}).",
            exception.GetType().Name,
            exception.Message),
        JsonException or InvalidDataException => new Error(
            ConnectorErrorCodes.Unknown,
            $"Tiger's answer to {what} could not be read.",
            exception.GetType().Name,
            exception.Message),
        _ => new Error(
            ConnectorErrorCodes.Unknown,
            $"An unexpected failure occurred talking to Tiger ({what}).",
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
internal static class TigerErrors
{
    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);

    public static Error MissingField(string method, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"Tiger's answer to {method} did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = method, ["field"] = field });

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"Tiger has no order with id '{brokerOrderId}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["brokerOrderId"] = brokerOrderId });

    public static Error MalformedSession(string detail) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"This Tiger link's session is incomplete ({detail}). Link the account again.");

    /// <summary>An answer whose signature does not verify: something other than Tiger answered.</summary>
    public static Error UnverifiedResponse(string method) => new(
        ConnectorErrorCodes.BrokerUnavailable,
        $"Tiger's answer to {method} did not carry a valid signature, so it was discarded. Something other than Tiger "
        + "answered the endpoint, or the configured Tiger public key is wrong.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = method });
}
