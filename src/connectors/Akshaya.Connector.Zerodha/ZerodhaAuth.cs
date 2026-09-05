using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Kite's login, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// It is an OAuth-shaped authorization-code flow with one Kite-specific twist — the token
/// exchange authenticates the app with a hash rather than by sending the secret:
///
/// <code>
///   BeginAsync
///     -> RedirectRequired(https://kite.zerodha.com/connect/login?v=3&amp;api_key=…&amp;redirect_params=…)
///          user signs in at Kite; the app's REGISTERED redirect URI receives ?request_token=…
///     |
///   ContinueAsync(request_token)
///     POST /session/token  { api_key, request_token, checksum = SHA-256(api_key+request_token+api_secret) }
///       -> access_token, user_id, and the account's entitlements
///       -> Completed(BrokerSession)
/// </code>
///
/// Two things differ from a textbook OAuth2 flow and both bite:
///
/// * THE REDIRECT URI IS NOT SENT. Kite uses the one registered against the api_key on the
///   developer console and ignores anything passed in the URL. <see cref="AuthContext.RedirectUri"/>
///   is therefore checked for presence but never transmitted — it exists so the host can tell the
///   user which URI must be registered.
/// * THERE IS NO <c>state</c> PARAMETER. Kite has <c>redirect_params</c> instead: an opaque,
///   URL-encoded query string echoed back to the redirect URI. The anti-CSRF state rides in there.
/// </summary>
public sealed class ZerodhaAuth : IConnectorAuth
{
    private readonly ZerodhaOptions _options;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;
    private readonly Func<BrokerSession?, ZerodhaApi> _apiFactory;

    /// <summary>The connector id stamped into every session this facet issues.</summary>
    public const string ConnectorId = "zerodha";

    /// <summary>Credential key carrying the public API key.</summary>
    public const string ApiKeyField = "api_key";

    /// <summary>Credential key carrying the API secret. Only ever used to compute the checksum.</summary>
    public const string ApiSecretField = "api_secret";

    /// <summary>
    /// Key under which the anti-CSRF state is carried across the redirect. The host puts the
    /// value it received back here before calling <see cref="ContinueAsync"/>.
    /// </summary>
    public const string StateKey = "state";

    /// <summary>Creates the auth facet.</summary>
    internal ZerodhaAuth(
        ZerodhaOptions options,
        ZerodhaErrorMapper errors,
        IClock clock,
        Func<BrokerSession?, ZerodhaApi>? apiFactory = null)
    {
        _options = options;
        _clock = clock;
        _venueZone = ZerodhaTime.ResolveZone(options.VenueTimeZoneId);
        _apiFactory = apiFactory ?? (session => ZerodhaApi.Create(options, errors, session));
    }

    /// <summary>
    /// When a Kite access token actually stops working.
    ///
    /// Kite is unusually precise about this, and the precision is worth honouring exactly: the
    /// token "will expire at 6 AM on the next day (regulatory requirement)". Not midnight, and
    /// not a rolling lifetime from issue.
    ///
    /// Both plausible approximations are wrong in a way that costs something. Midnight prompts
    /// the trader to sign in again six hours before they need to, every single day. A rolling
    /// twenty-four hours leaves a token that died at 06:00 looking alive until 15:00 — so the
    /// first thing the trader learns about it is a rejected order at the open, which is the worst
    /// possible moment.
    ///
    /// A token issued at 05:00 IST expires at 06:00 the SAME morning, an hour later. That is not
    /// a bug in this calculation; it is what "expires at 6 AM" means, and the session monitor
    /// prompting for a re-login at 06:00 is the correct behaviour.
    /// </summary>
    public static DateTimeOffset ComputeExpiry(DateTimeOffset issuedAt, TimeZoneInfo venueZone, int expiryHour) =>
        ZerodhaTime.NextVenueHour(issuedAt, venueZone, expiryHour);

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var apiKey = context.Credentials.GetOrDefault(ApiKeyField);
        var apiSecret = context.Credentials.GetOrDefault(ApiSecretField);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return Failed(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Kite needs an API key and an API secret before it can start a session. Both are "
                + "issued when you create an app on the Kite developer console."));
        }

        // A caller resuming a partially built flow may already have a state value it intends to
        // check against; honour it rather than issuing a second one it will never see again.
        var state = context.State.GetValueOrDefault(StateKey) is { Length: > 0 } supplied
            ? supplied
            : GenerateState();

        // redirect_params is Kite's stand-in for `state`: an opaque query string echoed back to
        // the registered redirect URI. It has to be encoded ONCE as a query string and then again
        // as a parameter value, which is why it is built by hand rather than passed through the
        // query builder twice.
        var redirectParams = $"{StateKey}={Uri.EscapeDataString(state)}";

        var query = new ZerodhaQuery()
            .Add("v", _options.ApiVersion)
            .Add("api_key", apiKey)
            .Add("redirect_params", redirectParams);

        var url = _options.LoginUrl + query.ToQueryString();

        return Task.FromResult<Result<AuthStep>>(new AuthStep.RedirectRequired(url, state));
    }

    /// <inheritdoc />
    public async Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The request token arrives either as the step response or on the context, depending on
        // how the host chose to carry it back from the redirect. Accept both.
        var requestToken = FirstNonEmpty(response, context.CallbackCode);
        if (requestToken is null)
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "No request token came back from the Kite login."));
        }

        var apiKey = context.Credentials.GetOrDefault(ApiKeyField);
        var apiSecret = context.Credentials.GetOrDefault(ApiSecretField);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The Kite API key or secret is missing; the login cannot be completed."));
        }

        await using var api = _apiFactory(null);

        var session = await api.PostFormAsync<KiteSession>(
            _options.SessionTokenPath,
            [
                new KeyValuePair<string, string>("api_key", apiKey),
                new KeyValuePair<string, string>("request_token", requestToken),
                new KeyValuePair<string, string>("checksum", Checksum(apiKey, requestToken, apiSecret)),
            ],
            ct).ConfigureAwait(false);

        if (session.IsFailure)
        {
            return Result<AuthStep>.Failure(session.Error);
        }

        return BuildSession(session.Value, apiKey)
            .Map<AuthStep>(built => new AuthStep.Completed(built));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kite's session response has a <c>refresh_token</c> field, and it is empty for almost
    /// everyone: the documentation says it is "only available to certain approved platforms".
    /// A connector that declared refresh support would leave the session monitor retrying a
    /// refresh that returns nothing, every time, and never falling through to prompting the user —
    /// so the session would quietly stop working with no dialog.
    ///
    /// It would buy nothing anyway. The access token dies at 06:00 IST by regulation, so a
    /// refresh could not carry a session past a boundary the trader has to cross with a fresh
    /// login regardless.
    ///
    /// The manifest declares <c>refreshSupported: false</c> to match. Returning NotSupported —
    /// rather than a transient failure — is what tells the session monitor to stop asking.
    /// </remarks>
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result<BrokerSession>.Failure(ConnectorErrors.NotSupported(
            "silent session refresh. Kite issues refresh tokens only to platforms it has specifically "
            + "approved, and its access token expires at 6 AM India time by regulation, so signing in "
            + "again is the only route")));

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var apiKey = session.Extras.GetValueOrDefault(ZerodhaSessionKeys.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Nothing to revoke with. The session is already unusable by us, which is what the
            // caller wanted; reporting a failure would only block its cleanup.
            return Result.Success();
        }

        await using var api = _apiFactory(session);

        // The logout route takes its parameters as QUERY parameters on a DELETE, not as a body —
        // the one place Kite's "POST and PUT are form-encoded" rule does not apply.
        var result = await api.DeleteAsync<System.Text.Json.JsonElement>(
            _options.SessionTokenPath,
            new ZerodhaQuery()
                .Add("api_key", apiKey)
                .Add("access_token", session.AccessToken),
            ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result.Success();
        }

        // An already-dead token cannot be revoked again, and reporting that as a failure would
        // leave the platform unable to clean up a session Kite has already discarded.
        return result.Error.Code is ConnectorErrorCodes.SessionExpired
            or ConnectorErrorCodes.ReauthRequired
            or ConnectorErrorCodes.InvalidRequest
            ? Result.Success()
            : Result.Failure(result.Error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kite does not drop idle sessions and publishes no tickle endpoint, so this is a no-op
    /// success and the manifest declares no keepAliveInterval. Polling something just to look
    /// busy would only consume the ten-requests-per-second budget the trader needs for quotes.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// The <c>checksum</c> the token exchange requires: the lowercase hex SHA-256 of
    /// <c>api_key + request_token + api_secret</c>, concatenated with NO separator.
    ///
    /// The absence of a separator is the detail worth stating out loud, because the neighbouring
    /// broker in this repository — mStock, which is a Kite-lineage API — does not use a hash here
    /// at all, and a third uses a colon-separated one. Getting it wrong produces a
    /// perfectly valid-looking 64-character digest that Kite rejects with a bare
    /// <c>TokenException</c>, at the step immediately after a login that appeared to work.
    /// </summary>
    public static string Checksum(string apiKey, string requestToken, string apiSecret) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + requestToken + apiSecret)));

    private Result<BrokerSession> BuildSession(KiteSession data, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(data.AccessToken))
        {
            return Result<BrokerSession>.Failure(
                ZerodhaErrors.MissingField(_options.SessionTokenPath, "access_token"));
        }

        if (string.IsNullOrWhiteSpace(data.UserId))
        {
            return Result<BrokerSession>.Failure(
                ZerodhaErrors.MissingField(_options.SessionTokenPath, "user_id"));
        }

        var issuedAt = ZerodhaTime.ParseOr(data.LoginTime, _clock.UtcNow);

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ZerodhaSessionKeys.ApiKey] = apiKey,
        };

        AddIfPresent(extras, ZerodhaSessionKeys.PublicToken, data.PublicToken);
        AddIfPresent(extras, ZerodhaSessionKeys.UserName, data.UserShortName ?? data.UserName);

        // The entitlement lists come back on the session call and nowhere else. Carrying them
        // means the order ticket can refuse an NFO order from an account with no derivative
        // entitlement locally, instead of learning about it from an exchange rejection.
        AddIfPresent(extras, ZerodhaSessionKeys.Exchanges, Join(data.Exchanges));
        AddIfPresent(extras, ZerodhaSessionKeys.Products, Join(data.Products));
        AddIfPresent(extras, ZerodhaSessionKeys.OrderTypes, Join(data.OrderTypes));

        return new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = data.UserId,
            AccessToken = data.AccessToken,

            // Deliberately not stored. Kite returns the field empty for every app it has not
            // specifically approved, and this connector cannot redeem one — see RefreshAsync.
            RefreshToken = null,

            ExpiresAt = ComputeExpiry(issuedAt, _venueZone, _options.TokenExpiryHourVenueTime),
            Extras = extras,
        };
    }

    /// <summary>
    /// 256 bits of cryptographic randomness, hex encoded.
    ///
    /// <see cref="RandomNumberGenerator"/> rather than <c>Random</c>: this value is the only thing
    /// tying the browser that started the login to the callback that finishes it, and a
    /// predictable one lets an attacker complete the flow with their own request token and bind
    /// their Kite account to this user's platform login.
    /// </summary>
    private static string GenerateState() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static Task<Result<AuthStep>> Failed(Error error) =>
        Task.FromResult(Result<AuthStep>.Failure(error));

    private static void AddIfPresent(Dictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(',', values) : null;

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
}
