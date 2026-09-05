using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// The FYERS login, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// It is an ordinary OAuth 2 authorization-code flow, with one FYERS-specific twist — the token
/// exchange authenticates the APP with a hash rather than by sending the secret:
///
/// <code>
///   BeginAsync
///     -> RedirectRequired(https://api-t1.fyers.in/api/v3/generate-authcode?client_id=…&amp;state=…)
///          user signs in at FYERS; the redirect_uri receives ?auth_code=…&amp;state=…
///     |
///   ContinueAsync(auth_code)
///     POST /api/v3/validate-authcode  { grant_type, appIdHash = SHA-256("app_id:app_secret"), code }
///       -> access_token (+ refresh_token, which this connector does not use)
///     GET  /api/v3/profile            -> fy_id, the account identifier
///       -> Completed(BrokerSession)
/// </code>
///
/// The <c>state</c> value is generated here and returned on the step so the caller can compare
/// it with what comes back on the redirect. FYERS echoes it verbatim, and checking it is the
/// only defence against a login CSRF that would bind the wrong FYERS account to this user.
/// </summary>
public sealed class FyersAuth : IConnectorAuth
{
    private readonly FyersOptions _options;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;
    private readonly Func<BrokerSession?, FyersApi> _apiFactory;

    /// <summary>The connector id stamped into every session this facet issues.</summary>
    public const string ConnectorId = "fyers";

    /// <summary>Credential key carrying the app id (the OAuth <c>client_id</c>).</summary>
    public const string AppIdField = "app_id";

    /// <summary>Credential key carrying the app secret. Only ever used to compute the hash.</summary>
    public const string AppSecretField = "app_secret";

    /// <summary>
    /// Key under which the anti-CSRF state is carried across the redirect. The host puts the
    /// value it received back here before calling <see cref="ContinueAsync"/>.
    /// </summary>
    public const string StateKey = "state";

    /// <summary>Creates the auth facet.</summary>
    internal FyersAuth(
        FyersOptions options,
        FyersErrorMapper errors,
        IClock clock,
        Func<BrokerSession?, FyersApi>? apiFactory = null)
    {
        _options = options;
        _clock = clock;
        _venueZone = FyersTime.ResolveZone(options.VenueTimeZoneId);
        _apiFactory = apiFactory ?? (session => FyersApi.Create(options, errors, session));
    }

    /// <summary>
    /// When a FYERS access token actually stops working.
    ///
    /// FYERS publishes no access-token lifetime, which leaves three constraints and no
    /// authority. So take all three and use the earliest:
    ///
    /// 1. THE TOKEN'S OWN <c>exp</c> CLAIM. The access token is a JWT and it says when it dies.
    ///    This is the real answer whenever it is readable, and no amount of documentation would
    ///    beat it.
    /// 2. VENUE MIDNIGHT. Indian broker tokens are day-bound; FYERS' is no exception in
    ///    practice. A token minted at 15:00 IST does not survive the night whatever its claim
    ///    says.
    /// 3. THE CONFIGURED NOMINAL LIFETIME, as the floor when the token is not a readable JWT —
    ///    a shape change at the vendor must degrade to a conservative guess, not to "never
    ///    expires".
    ///
    /// Erring early is cheap: expiring a session slightly early costs one extra login. Erring
    /// late costs orders, because the first thing the trader learns about a dead token is a
    /// rejection at the next open. So this always takes the minimum.
    /// </summary>
    public static DateTimeOffset ComputeExpiry(
        DateTimeOffset issuedAt,
        TimeSpan nominalLifetime,
        TimeZoneInfo venueZone,
        DateTimeOffset? tokenExpiry = null)
    {
        var earliest = issuedAt + nominalLifetime;

        var venueMidnight = FyersTime.NextVenueMidnight(issuedAt, venueZone);
        if (venueMidnight < earliest)
        {
            earliest = venueMidnight;
        }

        // A token whose stated expiry is already behind us is not a reason to issue a session
        // that is dead on arrival — but it IS a reason to trust the claim, so it still wins.
        if (tokenExpiry is { } claimed && claimed < earliest)
        {
            earliest = claimed;
        }

        return earliest;
    }

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var appId = context.Credentials.GetOrDefault(AppIdField);
        var appSecret = context.Credentials.GetOrDefault(AppSecretField);

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            return Failed(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "FYERS needs an app id and an app secret before it can start a session. Both are "
                + "issued when you create an app in the FYERS API dashboard."));
        }

        if (string.IsNullOrWhiteSpace(context.RedirectUri))
        {
            return Failed(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "FYERS needs the redirect URI this deployment is registered with. It must match the "
                + "one entered when the app was created, character for character, or FYERS refuses "
                + "the login before the user sees a password box."));
        }

        // A caller resuming a partially built flow may already have a state value it intends to
        // check against; honour it rather than issuing a second one it will never see again.
        var state = context.State.GetValueOrDefault(StateKey) is { Length: > 0 } supplied
            ? supplied
            : GenerateState();

        var url = new UriBuilder(new Uri(_options.BaseUrl, _options.AuthorizePath))
        {
            Query = new FyersQuery()
                .Add("client_id", appId)
                .Add("redirect_uri", context.RedirectUri)
                .Add("response_type", "code")
                .Add("state", state)
                .ToQueryString()
                .TrimStart('?'),
        }.Uri.ToString();

        return Task.FromResult<Result<AuthStep>>(new AuthStep.RedirectRequired(url, state));
    }

    /// <inheritdoc />
    public async Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The auth code arrives either as the step response or on the context, depending on how
        // the host chose to carry it back from the redirect. Accept both.
        var authCode = FirstNonEmpty(response, context.CallbackCode);
        if (authCode is null)
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "No authorisation code came back from the FYERS login."));
        }

        var appId = context.Credentials.GetOrDefault(AppIdField);
        var appSecret = context.Credentials.GetOrDefault(AppSecretField);

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The FYERS app id or secret is missing; the login cannot be completed."));
        }

        await using var api = _apiFactory(null);

        var token = await api.PostJsonAsync<FyersTokenResponse>(
            _options.TokenPath,
            new
            {
                grant_type = "authorization_code",
                appIdHash = AppIdHash(appId, appSecret),
                code = authCode,
            },
            ct).ConfigureAwait(false);

        if (token.IsFailure)
        {
            return Result<AuthStep>.Failure(token.Error);
        }

        var accessToken = token.Value.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result<AuthStep>.Failure(FyersErrors.MissingField(_options.TokenPath, "access_token"));
        }

        // The same client can now speak as the user, so the profile call below needs no second
        // round of configuration.
        api.UseSession(appId, accessToken);

        var identity = await ResolveIdentityAsync(api, accessToken, ct).ConfigureAwait(false);
        if (identity.IsFailure)
        {
            return Result<AuthStep>.Failure(identity.Error);
        }

        return BuildSession(appId, accessToken, identity.Value)
            .Map<AuthStep>(session => new AuthStep.Completed(session));
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS DOES publish a refresh route, and this connector deliberately does not use it. Three
    /// reasons, in order of weight:
    ///
    /// 1. FYERS has announced that refresh tokens are discontinued from 1 April, alongside the
    ///    regulatory changes to API usage. Building a session monitor around a route the vendor
    ///    is withdrawing buys a silent failure later, at the moment it is relied on.
    /// 2. The refresh call requires the user's trading PIN in the request body. Supporting it
    ///    means persisting a second standing secret — one that authorises trades on its own —
    ///    for the sole benefit of skipping a login the user has to do once a day anyway.
    /// 3. The access token is day-bound regardless, so a refresh could never carry a session past
    ///    the next venue midnight. The ceiling is the same either way.
    ///
    /// The manifest declares <c>refreshSupported: false</c> to match, so the session monitor
    /// prompts for a fresh interactive login rather than retrying something that cannot work.
    /// Returning NotSupported — rather than a transient failure — is what tells it to stop asking.
    /// </remarks>
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result<BrokerSession>.Failure(ConnectorErrors.NotSupported(
            "silent session refresh. FYERS is withdrawing refresh tokens and its access token expires "
            + "daily, so signing in again is the only route")));

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var api = _apiFactory(session);

        var result = await api
            .PostJsonAsync<FyersLogoutResponse>(_options.LogoutPath, new { }, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result.Success();
        }

        // An already-dead token cannot be revoked again, and reporting that as a failure would
        // leave the platform unable to clean up a session FYERS has already discarded.
        return result.Error.Code is ConnectorErrorCodes.SessionExpired
            or ConnectorErrorCodes.ReauthRequired
            or ConnectorErrorCodes.InvalidRequest
            ? Result.Success()
            : Result.Failure(result.Error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS does not drop idle sessions and publishes no tickle endpoint, so this is a no-op
    /// success and the manifest declares no keepAliveInterval. Polling something just to look
    /// busy would only consume the rate limit the trader needs for quotes — and FYERS blocks an
    /// account for the rest of the day after three per-minute breaches.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// The <c>appIdHash</c> the token exchange requires: the lowercase hex SHA-256 of
    /// <c>app_id:app_secret</c>.
    ///
    /// The colon is part of the input, not a separator this code invented — hashing the two
    /// values concatenated without it produces a perfectly valid-looking 64-character digest
    /// that FYERS rejects with an unhelpful "invalid App ID".
    /// </summary>
    public static string AppIdHash(string appId, string appSecret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}:{appSecret}")));

    /// <summary>
    /// Establishes who this token belongs to.
    ///
    /// The profile route is asked first because it is documented and because "Profile Details" is
    /// in the Basic permission template, so every app that can authenticate at all can call it.
    /// The token's own claims are a fallback only: the account id is a required field on
    /// <see cref="BrokerSession"/>, and failing an otherwise-successful login because one
    /// supplementary call was throttled would be a poor trade.
    /// </summary>
    private async Task<Result<FyersIdentity>> ResolveIdentityAsync(
        FyersApi api,
        string accessToken,
        CancellationToken ct)
    {
        var profile = await api
            .GetAsync<FyersProfileResponse>(_options.ProfilePath, query: null, ct)
            .ConfigureAwait(false);

        if (profile.IsSuccess && profile.Value.Data?.FyId is { Length: > 0 } fyId)
        {
            var data = profile.Value.Data;
            return new FyersIdentity(fyId, data.DisplayName ?? data.Name, data.MtfEnabled);
        }

        if (FyersToken.ReadClientId(accessToken) is { Length: > 0 } claimed)
        {
            return new FyersIdentity(claimed, null, null);
        }

        return Result<FyersIdentity>.Failure(profile.IsFailure
            ? profile.Error
            : FyersErrors.MissingField(_options.ProfilePath, "fy_id"));
    }

    private Result<BrokerSession> BuildSession(string appId, string accessToken, FyersIdentity identity)
    {
        var issuedAt = _clock.UtcNow;

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FyersSessionKeys.AppId] = appId,
            [FyersSessionKeys.ClientId] = identity.ClientId,
        };

        if (identity.DisplayName is { Length: > 0 } name)
        {
            extras[FyersSessionKeys.UserName] = name;
        }

        if (identity.MtfEnabled is { } mtf)
        {
            extras[FyersSessionKeys.MtfEnabled] = mtf ? bool.TrueString : bool.FalseString;
        }

        return new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = identity.ClientId,
            AccessToken = accessToken,

            // Deliberately not stored. FYERS does return a refresh token, but this connector
            // cannot use one without the trading PIN and the vendor is withdrawing the route —
            // see RefreshAsync. Persisting a credential nothing will ever redeem is pure risk.
            RefreshToken = null,

            ExpiresAt = ComputeExpiry(
                issuedAt,
                _options.TokenLifetime,
                _venueZone,
                FyersToken.ReadExpiry(accessToken)),
            Extras = extras,
        };
    }

    /// <summary>
    /// 256 bits of cryptographic randomness, hex encoded.
    ///
    /// <see cref="RandomNumberGenerator"/> rather than <c>Random</c>: this value is the only thing
    /// tying the browser that started the login to the callback that finishes it, and a
    /// predictable one lets an attacker complete the flow with their own authorisation code and
    /// bind their FYERS account to this user's platform login.
    /// </summary>
    private static string GenerateState() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static Task<Result<AuthStep>> Failed(Error error) =>
        Task.FromResult(Result<AuthStep>.Failure(error));

    private static string? FirstNonEmpty(params ReadOnlySpan<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    /// <summary>Who a token belongs to, as far as we could establish.</summary>
    private readonly record struct FyersIdentity(string ClientId, string? DisplayName, bool? MtfEnabled);
}

/// <summary>Logout answers with the standard envelope and no payload of its own.</summary>
internal sealed class FyersLogoutResponse : FyersResponse;
