using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

/// <summary>
/// How a broker authenticates. This enum exists so the LINK WIZARD can render itself; the
/// auth flow itself is driven by the <see cref="AuthStep"/> state machine below, which is
/// what actually keeps the core broker-agnostic.
/// </summary>
public enum AuthModel
{
    /// <summary>Zerodha, Fyers, Upstox, Saxo, IBKR (first-party).</summary>
    OAuth2,

    /// <summary>IBKR third-party: every request is signed, there is no bearer token.</summary>
    OAuth1a,

    /// <summary>mStock: password, then an OTP to the registered mobile.</summary>
    PasswordOtp,

    /// <summary>Angel One: password plus an authenticator code.</summary>
    PasswordTotp,

    /// <summary>Dhan: a long-lived token pasted by the user. The easy case.</summary>
    StaticToken,

    /// <summary>Tiger: requests signed with an RSA private key.</summary>
    RsaSigned,

    /// <summary>Moomoo OpenD, IBKR Client Portal Gateway: a local daemon holds the session.</summary>
    GatewaySession,
}

public enum ChallengeKind
{
    SmsOtp,
    EmailOtp,
    Totp,
    SecurityQuestion,

    /// <summary>User must approve on a phone or in a browser window (IBKR, some OAuth flows).</summary>
    DeviceApproval,
}

/// <summary>
/// The result of an authentication step. Every broker's login — OAuth redirect, OTP, TOTP,
/// RSA signing, a gateway handshake — is expressible as a walk through these cases. This is
/// the single most important type for plug-and-play: if a new broker needs a new method on
/// <see cref="IConnectorAuth"/>, the design has failed; it should only ever need a new case
/// here, and even that should be rare.
/// </summary>
public abstract record AuthStep
{
    /// <summary>Authentication finished; the session is usable.</summary>
    public sealed record Completed(BrokerSession Session) : AuthStep;

    /// <summary>Send the user to <paramref name="Url"/>; the callback returns with a code.</summary>
    public sealed record RedirectRequired(string Url, string State) : AuthStep;

    /// <summary>Ask the user for a code and call ContinueAsync with it.</summary>
    public sealed record ChallengeRequired(
        ChallengeKind Kind,
        string Prompt,
        string? MaskedDestination = null,
        TimeSpan? ExpiresIn = null) : AuthStep;

    /// <summary>
    /// A gateway process must be running and authenticated before we can proceed
    /// (Moomoo OpenD, IBKR Client Portal Gateway). The host supervises it; the UI shows
    /// its state rather than a bare failure.
    /// </summary>
    public sealed record GatewayRequired(string GatewayId, string Instructions) : AuthStep;
}

/// <summary>
/// Credentials as the user supplied them, keyed by the field names the manifest declared.
/// The connector knows which keys it asked for; the core never inspects them, only encrypts
/// them. Values are cleared from memory as soon as the login call returns.
/// </summary>
public sealed class AuthCredentials(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public string Require(string key) => Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new InvalidOperationException($"Required credential field '{key}' was not supplied.");

    public string? GetOrDefault(string key) => Values.GetValueOrDefault(key);
}

/// <summary>
/// A live broker session. <see cref="Extras"/> carries broker-specific tokens the connector
/// needs on subsequent calls (Angel One's feed token, mStock's enctoken, an IBKR session id)
/// without any of them appearing in the shared contract.
/// </summary>
public sealed record BrokerSession
{
    public required string ConnectorId { get; init; }

    /// <summary>The broker's account identifier, as several brokers allow multiple accounts per login.</summary>
    public required string AccountId { get; init; }

    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    /// <summary>
    /// When this session stops working. For brokers whose tokens die at venue midnight
    /// regardless of issue time (mStock, most Indian brokers), this is that midnight, not
    /// issue-time plus lifetime. Getting this wrong loses orders.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public IReadOnlyDictionary<string, string> Extras { get; init; }
        = new Dictionary<string, string>();

    public bool IsExpired(IClock clock) => clock.UtcNow >= ExpiresAt;

    /// <summary>Warn the user before it actually dies — mid-trade re-auth is unacceptable.</summary>
    public bool IsExpiringSoon(IClock clock, TimeSpan window) => clock.UtcNow >= ExpiresAt - window;
}

public sealed record AuthContext
{
    public required AuthCredentials Credentials { get; init; }

    /// <summary>OAuth callback URL for this deployment; ignored by non-OAuth connectors.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>Set when resuming a flow: the code/token returned by a redirect.</summary>
    public string? CallbackCode { get; init; }

    /// <summary>Opaque state carried across a multi-step flow. The connector owns its meaning.</summary>
    public IReadOnlyDictionary<string, string> State { get; init; }
        = new Dictionary<string, string>();
}

public interface IConnectorAuth
{
    /// <summary>Begin authentication. Returns the next step, which may already be Completed.</summary>
    Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default);

    /// <summary>Supply a challenge response (OTP, TOTP, OAuth code) and get the next step.</summary>
    Task<Result<AuthStep>> ContinueAsync(AuthContext context, string response, CancellationToken ct = default);

    /// <summary>
    /// Refresh without user interaction. Connectors whose manifest says refreshSupported=false
    /// return NotSupported, and the session monitor prompts for re-auth instead.
    /// </summary>
    Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default);

    Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default);

    /// <summary>
    /// Keepalive for brokers that drop idle sessions (IBKR's /tickle). The host calls this on
    /// the interval declared in the manifest; connectors without one return Success.
    /// </summary>
    Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default);
}
