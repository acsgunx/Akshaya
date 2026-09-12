using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Translates OpenD's failures into canonical <see cref="ConnectorErrorCodes"/>, keeping what OpenD
/// actually said.
///
/// OpenD is harder to map than an HTTP broker, for two reasons that shape everything here:
///
/// 1. THERE IS NO STABLE ERROR VOCABULARY. A failure is <c>retType</c> (-1 failed, -100 timed out,
///    -200 disconnected), a free-text <c>retMsg</c>, and an <c>errCode</c> the protocol documentation
///    says is "for logging only". So classification reads the message.
/// 2. THE MESSAGE IS LOCALISED. OpenD answers in the language it is configured with, which for a
///    Futu login is often Chinese. Every phrase is therefore matched in English AND Chinese, and the
///    set is deliberately narrow: only phrases whose meaning is unambiguous. Anything else falls to a
///    per-protocol default — never to a guess, because a rejection misread as transient is retried by
///    the host, and a retried order placement is a duplicate order.
/// </summary>
public sealed class MoomooErrorMapper : IVendorErrorMapper
{
    /// <inheritdoc />
    public string? MapToCanonicalCode(VendorErrorContext context)
    {
        var message = context.VendorMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var english = message.ToLowerInvariant();

        // Trading is locked. The fix is to unlock it in OpenD (or relink with the trade password on a
        // command-line OpenD), which is a sign-in-again action, not a retry.
        if (Contains(english, "unlock") || Contains(message, "解锁"))
        {
            return ConnectorErrorCodes.ReauthRequired;
        }

        if (Contains(english, "too frequent", "frequency", "too many requests", "rate limit")
            || Contains(message, "频率", "频繁"))
        {
            return ConnectorErrorCodes.RateLimited;
        }

        if (Contains(english, "not logged in", "not login", "please login", "login first", "logged out")
            || Contains(message, "未登录", "请先登录"))
        {
            return ConnectorErrorCodes.GatewayUnavailable;
        }

        if (Contains(english, "buying power", "insufficient fund", "insufficient cash", "insufficient balance",
                "not enough cash")
            || Contains(message, "购买力不足", "资金不足", "现金不足"))
        {
            return ConnectorErrorCodes.InsufficientFunds;
        }

        if (Contains(english, "market closed", "market is closed", "not in trading time", "non-trading hours",
                "outside trading hours")
            || Contains(message, "休市", "闭市", "收市", "非交易时段", "不在交易时间"))
        {
            return ConnectorErrorCodes.MarketClosed;
        }

        if (Contains(english, "order does not exist", "order not exist", "order not found", "no such order",
                "cannot find the order")
            || Contains(message, "订单不存在", "找不到订单"))
        {
            return ConnectorErrorCodes.OrderNotFound;
        }

        if (Contains(english, "unknown stock", "unknown security", "invalid stock code", "security not found",
                "unknown code")
            || Contains(message, "未知股票", "股票代码错误", "代码不存在"))
        {
            return ConnectorErrorCodes.InstrumentNotFound;
        }

        if (Contains(english, "no quote right", "no permission", "no authority", "not authorized")
            || Contains(message, "没有权限", "无权限", "暂不支持"))
        {
            return ConnectorErrorCodes.NotSupported;
        }

        if (Contains(english, "risk control") || Contains(message, "风控"))
        {
            return ConnectorErrorCodes.RiskRejected;
        }

        return null;
    }

    /// <inheritdoc />
    public string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context) => canonicalCode switch
    {
        ConnectorErrorCodes.ReauthRequired => WithBrokerWords(
            "Live trading is locked in OpenD. Unlock it in the OpenD window, or link the account again with "
            + "the trade password if OpenD is the command-line build.",
            context),
        ConnectorErrorCodes.GatewayUnavailable => WithBrokerWords(
            "OpenD is not available: it is not running, not reachable, or not signed in to moomoo.",
            context),
        ConnectorErrorCodes.RateLimited => WithBrokerWords(
            "Too many requests to OpenD; wait and retry. moomoo allows fifteen order placements per thirty "
            + "seconds per account.",
            context),
        ConnectorErrorCodes.InsufficientFunds => WithBrokerWords(
            "The moomoo account does not have the buying power for this order.",
            context),
        ConnectorErrorCodes.MarketClosed => WithBrokerWords("The market is closed for this instrument.", context),
        ConnectorErrorCodes.OrderNotFound => "moomoo has no record of that order.",
        ConnectorErrorCodes.InstrumentNotFound => WithBrokerWords("moomoo does not recognise that security.", context),
        ConnectorErrorCodes.NotSupported => WithBrokerWords(
            "moomoo does not permit this on this account, or the account lacks the quote right for it.",
            context),
        ConnectorErrorCodes.RiskRejected => WithBrokerWords("moomoo's risk checks blocked this order.", context),
        ConnectorErrorCodes.OrderRejected => WithBrokerWords("moomoo rejected the order.", context),
        ConnectorErrorCodes.Timeout => WithBrokerWords(
            "OpenD did not answer in time. For an order, check the order book before trying again — it may "
            + "have been placed.",
            context),

        // No opinion: the broker's own words are the most useful thing available.
        _ => string.IsNullOrWhiteSpace(context.VendorMessage)
            ? "OpenD reported an error."
            : context.VendorMessage,
    };

    /// <summary>Maps a response whose <c>retType</c> was not success.</summary>
    internal Error MapResponse(int protoId, int retType, int? errCode, string? retMsg)
    {
        var protocol = MoomooProtoId.Name(protoId);
        var vendorCode = errCode is { } code && code != 0
            ? code.ToString(CultureInfo.InvariantCulture)
            : retType.ToString(CultureInfo.InvariantCulture);

        var context = new VendorErrorContext(null, vendorCode, retMsg, protocol, null);

        var canonical = retType switch
        {
            // OpenD's own "timed out, result unknown". Not a rejection: the order may be live.
            MoomooRetType.TimeOut => ConnectorErrorCodes.Timeout,
            MoomooRetType.Disconnect => ConnectorErrorCodes.GatewayUnavailable,
            _ => MapToCanonicalCode(context) ?? DefaultFor(protoId),
        };

        return new Error(
            canonical,
            DescribeCanonicalCode(canonical, context),
            vendorCode,
            retMsg,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["protocol"] = protocol,
                ["retType"] = retType.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Maps a transport failure thrown before or instead of a response.</summary>
    internal static Error MapException(Exception exception, int? protoId)
    {
        var protocol = protoId is { } id ? MoomooProtoId.Name(id) : "connection";

        return exception switch
        {
            OperationCanceledException or TimeoutException => new Error(
                ConnectorErrorCodes.Timeout,
                $"OpenD did not answer {protocol} in time.",
                exception.GetType().Name,
                exception.Message),

            // A dead or refused socket is the gateway being unavailable. The host retries READS on this
            // code; writes are never retried and must be reconciled against the order book instead.
            SocketException or IOException or ObjectDisposedException => new Error(
                ConnectorErrorCodes.GatewayUnavailable,
                "The connection to OpenD was lost.",
                exception.GetType().Name,
                exception.Message),

            // Carries the reason in its message: not an OpenD frame, or an encrypted OpenD.
            InvalidDataException => new Error(
                ConnectorErrorCodes.GatewayUnavailable,
                exception.Message,
                exception.GetType().Name,
                exception.Message),

            JsonException => new Error(
                ConnectorErrorCodes.Unknown,
                $"OpenD's {protocol} response could not be read.",
                exception.GetType().Name,
                exception.Message),

            _ => new Error(
                ConnectorErrorCodes.Unknown,
                $"An unexpected failure occurred talking to OpenD ({protocol}).",
                exception.GetType().Name,
                exception.Message),
        };
    }

    /// <summary>
    /// What an unclassifiable failure means, by protocol. A trade write that failed for an unknown reason
    /// is a rejection — non-retryable on purpose — and a failed InitConnect is a gateway problem whatever
    /// OpenD said about it.
    /// </summary>
    private static string DefaultFor(int protoId) =>
        MoomooProtoId.IsTradeWrite(protoId) ? ConnectorErrorCodes.OrderRejected
        : protoId == MoomooProtoId.InitConnect ? ConnectorErrorCodes.GatewayUnavailable
        : ConnectorErrorCodes.Unknown;

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

/// <summary>Errors this connector raises itself, without OpenD having said anything.</summary>
internal static class MoomooErrors
{
    public static Error GatewayUnreachable(GatewayAddress address, string detail) => new(
        ConnectorErrorCodes.GatewayUnavailable,
        $"Could not connect to OpenD at {address}: {detail}. Start OpenD there and sign in with the moomoo "
        + "account, or point Connectors:Gateways:moomoo-opend at where it runs.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["gateway"] = address.ToString() });

    public static Error NoGatewayAddress() => new(
        ConnectorErrorCodes.GatewayUnavailable,
        "The host did not supply an OpenD address to this connector. It must be activated as a gateway-hosted "
        + "connector.");

    public static Error ConnectionClosed(GatewayAddress address, string detail) => new(
        ConnectorErrorCodes.GatewayUnavailable,
        $"The connection to OpenD at {address} closed: {detail}",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["gateway"] = address.ToString() });

    public static Error TimedOut(int protoId, TimeSpan timeout) => new(
        ConnectorErrorCodes.Timeout,
        $"OpenD did not answer {MoomooProtoId.Name(protoId)} within "
        + $"{timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["protocol"] = MoomooProtoId.Name(protoId) });

    public static Error Unreadable(int protoId, string? jsonPath) => new(
        ConnectorErrorCodes.Unknown,
        jsonPath is { Length: > 0 }
            ? $"OpenD's {MoomooProtoId.Name(protoId)} response could not be understood: {jsonPath} was not the expected type."
            : $"OpenD's {MoomooProtoId.Name(protoId)} response could not be understood.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["protocol"] = MoomooProtoId.Name(protoId) });

    public static Error MissingField(int protoId, string field) => new(
        ConnectorErrorCodes.Unknown,
        $"OpenD's {MoomooProtoId.Name(protoId)} response did not contain '{field}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["protocol"] = MoomooProtoId.Name(protoId),
            ["field"] = field,
        });

    public static Error InvalidRequest(string message) => new(ConnectorErrorCodes.InvalidRequest, message);

    public static Error OrderNotFound(string brokerOrderId) => new(
        ConnectorErrorCodes.OrderNotFound,
        $"moomoo has no order with id '{brokerOrderId}' on this account.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["brokerOrderId"] = brokerOrderId });

    public static Error MalformedSession(string detail) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"This moomoo link's session is incomplete ({detail}). Link the account again.");
}
