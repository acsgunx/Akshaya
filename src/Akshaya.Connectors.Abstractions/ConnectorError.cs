using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

/// <summary>
/// The closed set of failure reasons the core understands. Every broker's error vocabulary
/// collapses onto this; the raw vendor code and message ride along in the <see cref="Error"/>
/// so support can still answer "what did the broker actually say".
///
/// Adding a member here is a contract change and needs an ADR — the UI, the retry policy and
/// the risk engine all switch on these.
/// </summary>
public static class ConnectorErrorCodes
{
    // --- authentication and session ---
    public const string InvalidCredentials = "connector.invalid_credentials";
    public const string ChallengeFailed = "connector.challenge_failed";
    public const string SessionExpired = "connector.session_expired";

    /// <summary>Distinct from SessionExpired: the user must interactively re-authenticate.</summary>
    public const string ReauthRequired = "connector.reauth_required";

    /// <summary>A gateway-hosted connector (Moomoo OpenD, IBKR Client Portal) is not reachable.</summary>
    public const string GatewayUnavailable = "connector.gateway_unavailable";

    // --- request problems ---
    public const string InvalidRequest = "connector.invalid_request";
    public const string InstrumentNotFound = "connector.instrument_not_found";
    public const string OrderNotFound = "connector.order_not_found";

    /// <summary>The connector genuinely cannot do this. Not an error to retry — a capability gap.</summary>
    public const string NotSupported = "connector.not_supported";

    // --- broker/venue state ---
    public const string InsufficientFunds = "connector.insufficient_funds";
    public const string MarketClosed = "connector.market_closed";
    public const string RiskRejected = "connector.risk_rejected";
    public const string OrderRejected = "connector.order_rejected";

    // --- transport ---
    public const string RateLimited = "connector.rate_limited";
    public const string Timeout = "connector.timeout";
    public const string BrokerUnavailable = "connector.broker_unavailable";
    public const string Unknown = "connector.unknown";

    /// <summary>
    /// Errors worth retrying automatically. Anything not in this set is surfaced to the user
    /// immediately — retrying an InsufficientFunds or a RiskRejected just wastes their time,
    /// and blind-retrying an order placement is how duplicate orders get created.
    /// </summary>
    public static readonly IReadOnlySet<string> Retryable = new HashSet<string>
    {
        RateLimited,
        Timeout,
        BrokerUnavailable,
        GatewayUnavailable,
    };

    public static bool IsRetryable(string code) => Retryable.Contains(code);
}

public static class ConnectorErrors
{
    public static Error NotSupported(string capability) => new(
        ConnectorErrorCodes.NotSupported,
        $"This broker does not support {capability}.");

    public static Error SessionExpired(string connectorId) => new(
        ConnectorErrorCodes.SessionExpired,
        $"The {connectorId} session has expired.");

    public static Error ReauthRequired(string connectorId) => new(
        ConnectorErrorCodes.ReauthRequired,
        $"Sign in to {connectorId} again to continue trading.");

    public static Error GatewayUnavailable(string connectorId, string detail) => new(
        ConnectorErrorCodes.GatewayUnavailable,
        $"The {connectorId} gateway is not responding: {detail}");

    public static Error InstrumentNotFound(InstrumentKey key) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"{key} is not tradable through this broker.");

    public static Error Vendor(string canonicalCode, string message, string? vendorCode, string? vendorMessage) =>
        new(canonicalCode, message, vendorCode, vendorMessage);
}
