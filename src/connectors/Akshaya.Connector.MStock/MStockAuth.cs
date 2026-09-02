using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// mStock's login, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// The flow has two shapes and they share a first leg:
///
/// <code>
///   BeginAsync
///     POST /openapi/typea/connect/login          (username, password)   -> mStock texts an OTP
///     |
///     +-- no authenticator configured  -> ChallengeRequired(SmsOtp)
///     |     ContinueAsync(otp)
///     |       POST /openapi/typea/session/token  (api_key, request_token, checksum)
///     |         -> Completed(BrokerSession)
///     |
///     +-- authenticator secret stored  -> POST /openapi/typea/session/verifytotp (api_key, totp)
///           -> Completed(BrokerSession)
/// </code>
///
/// The login leg runs in both shapes even when a TOTP is available: mStock will not accept a
/// second factor for a session it has not started.
/// </summary>
public sealed class MStockAuth : IConnectorAuth
{
    private readonly MStockOptions _options;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;
    private readonly Func<BrokerSession?, MStockApi> _apiFactory;

    /// <summary>The connector id stamped into every session this facet issues.</summary>
    public const string ConnectorId = "mstock";

    /// <summary>Credential key carrying an authenticator secret, when the user has one.</summary>
    public const string TotpSecretField = "totp_secret";

    /// <summary>
    /// Set <c>context.State["challenge"] = "totp"</c> to make <see cref="ContinueAsync"/> send
    /// the response to the TOTP route instead of the OTP route. That is the case where the
    /// user has an authenticator app but has (correctly) not handed us its seed.
    /// </summary>
    public const string ChallengeStateKey = "challenge";

    /// <summary>Value for <see cref="ChallengeStateKey"/> selecting the authenticator route.</summary>
    public const string TotpChallenge = "totp";

    /// <summary>Creates the auth facet.</summary>
    internal MStockAuth(
        MStockOptions options,
        MStockErrorMapper errors,
        IClock clock,
        Func<BrokerSession?, MStockApi>? apiFactory = null)
    {
        _options = options;
        _clock = clock;
        _venueZone = MStockTime.ResolveZone(options.VenueTimeZoneId);
        _apiFactory = apiFactory ?? (session => MStockApi.Create(options, errors, session));
    }

    /// <summary>
    /// When an mStock access token actually stops working.
    ///
    /// mStock publishes a twelve-hour token lifetime AND invalidates every token at midnight
    /// India time. The effective expiry is whichever comes first, and the difference is not
    /// academic: a token minted at 15:00 IST has nine hours of nominal life left at midnight
    /// and none of it is real. A session monitor that trusted issue-time-plus-twelve-hours
    /// would schedule its re-auth prompt for 03:00, hours after the token died — so the first
    /// thing the trader would learn about it is a rejected order at the next open.
    ///
    /// Erring the other way is cheap: expiring a session slightly early costs one extra login.
    /// Erring late costs orders. So this always takes the minimum.
    /// </summary>
    public static DateTimeOffset ComputeExpiry(
        DateTimeOffset issuedAt,
        TimeSpan nominalLifetime,
        TimeZoneInfo venueZone)
    {
        var nominal = issuedAt + nominalLifetime;
        var venueMidnight = MStockTime.NextVenueMidnight(issuedAt, venueZone);
        return nominal < venueMidnight ? nominal : venueMidnight;
    }

    /// <inheritdoc />
    public async Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        var credentials = context.Credentials;

        var apiKey = credentials.GetOrDefault("api_key");
        var username = credentials.GetOrDefault("username");
        var password = credentials.GetOrDefault("password");

        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "mStock needs an API key, a client code and a password before it can start a session."));
        }

        await using var api = _apiFactory(null);

        // The login leg is form-encoded, unlike every other route on this API.
        var login = await api.PostFormAsync<MStockLoginData>(
            _options.LoginPath,
            [
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
            ],
            ct).ConfigureAwait(false);

        if (login.IsFailure)
        {
            return Result<AuthStep>.Failure(login.Error);
        }

        var totpSecret = credentials.GetOrDefault(TotpSecretField);
        if (!string.IsNullOrWhiteSpace(totpSecret))
        {
            var code = MStockTotp.Generate(totpSecret, _clock.UtcNow);
            if (code.IsFailure)
            {
                return Result<AuthStep>.Failure(code.Error);
            }

            return await CompleteWithTotpAsync(api, apiKey, username, code.Value, ct).ConfigureAwait(false);
        }

        // No authenticator: mStock has just sent an SMS. Hand control back to the wizard.
        var masked = login.Value.MaskedMobile;

        return Result<AuthStep>.Success(new AuthStep.ChallengeRequired(
            ChallengeKind.SmsOtp,
            "Enter the one-time password mStock has sent to your registered mobile number.",
            masked,
            // mStock's OTPs are short-lived. Telling the wizard how long it has lets it show a
            // countdown and offer a resend rather than failing silently at the far end.
            TimeSpan.FromMinutes(5)));
    }

    /// <inheritdoc />
    public async Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "No one-time password was supplied."));
        }

        var apiKey = context.Credentials.GetOrDefault("api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The mStock API key is missing; the login cannot be completed."));
        }

        var username = context.Credentials.GetOrDefault("username");
        var code = response.Trim();

        await using var api = _apiFactory(null);

        if (string.Equals(
                context.State.GetValueOrDefault(ChallengeStateKey),
                TotpChallenge,
                StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteWithTotpAsync(api, apiKey, username, code, ct).ConfigureAwait(false);
        }

        var checksum = ComputeChecksum(apiKey, code, context.Credentials.GetOrDefault("api_secret"));

        var session = await api.PostFormAsync<MStockSessionData>(
            _options.SessionTokenPath,
            [
                new KeyValuePair<string, string>("api_key", apiKey),
                new KeyValuePair<string, string>("request_token", code),
                new KeyValuePair<string, string>("checksum", checksum),
            ],
            ct).ConfigureAwait(false);

        if (session.IsFailure)
        {
            return Result<AuthStep>.Failure(session.Error);
        }

        return BuildSession(session.Value, apiKey, username)
            .Map<AuthStep>(s => new AuthStep.Completed(s));
    }

    /// <inheritdoc />
    public async Task<Result<BrokerSession>> RefreshAsync(
        BrokerSession session,
        CancellationToken ct = default)
    {
        var apiKey = session.Extras.GetValueOrDefault(MStockSessionKeys.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return Result<BrokerSession>.Failure(ConnectorErrors.ReauthRequired(ConnectorId));
        }

        // A refresh cannot outlive venue midnight, and mStock invalidates the refresh token at
        // the same instant as the access token. Once that boundary has passed there is nothing
        // to refresh, so do not spend a network call — and, more importantly, do not let the
        // session monitor sit in a refresh loop while the trader waits for a login prompt.
        if (_clock.UtcNow >= session.ExpiresAt)
        {
            return Result<BrokerSession>.Failure(new Error(
                ConnectorErrorCodes.ReauthRequired,
                "The mStock session has passed its expiry. mStock invalidates the access token and "
                + "the refresh token together at midnight India time, so a fresh interactive login "
                + "is the only way forward."));
        }

        await using var api = _apiFactory(session);

        var refreshed = await api.PostFormAsync<MStockSessionData>(
            _options.SessionTokenPath,
            [
                new KeyValuePair<string, string>("api_key", apiKey),
                new KeyValuePair<string, string>("refresh_token", session.RefreshToken),
                new KeyValuePair<string, string>(
                    "checksum",
                    ComputeChecksum(apiKey, session.RefreshToken, null)),
            ],
            ct).ConfigureAwait(false);

        if (refreshed.IsFailure)
        {
            return Result<BrokerSession>.Failure(refreshed.Error);
        }

        return BuildSession(
            refreshed.Value,
            apiKey,
            session.Extras.GetValueOrDefault(MStockSessionKeys.UserName) ?? session.AccountId);
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default)
    {
        await using var api = _apiFactory(session);

        // mStock's logout is a GET with no body and an empty data payload, so the envelope's
        // `data` is often absent. A missing payload is not a failure here.
        // The logout route answers with a bare {"status":"success"} and no data payload, so it
        // is sent through the void variant; asking for a payload that is never there would
        // report every successful logout as a malformed response.
        var result = await api.GetVoidAsync(_options.LogoutPath, query: null, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result.Success();
        }

        // An already-dead token cannot be revoked again, and reporting that as a failure would
        // leave the platform unable to clean up a session mStock has already discarded.
        return result.Error.Code is ConnectorErrorCodes.SessionExpired
            or ConnectorErrorCodes.ReauthRequired
            or ConnectorErrorCodes.InvalidRequest
            ? Result.Success()
            : Result.Failure(result.Error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// mStock does not drop idle sessions and publishes no tickle endpoint, so this is a
    /// no-op success and the manifest declares no keepAliveInterval. Polling something just
    /// to look busy would only consume the data rate limit the trader needs for quotes.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    private async Task<Result<AuthStep>> CompleteWithTotpAsync(
        MStockApi api,
        string apiKey,
        string? username,
        string code,
        CancellationToken ct)
    {
        var verified = await api.PostFormAsync<MStockSessionData>(
            _options.VerifyTotpPath,
            [
                new KeyValuePair<string, string>("api_key", apiKey),
                new KeyValuePair<string, string>("totp", code),
            ],
            ct).ConfigureAwait(false);

        if (verified.IsFailure)
        {
            return Result<AuthStep>.Failure(verified.Error);
        }

        // Some mStock builds return the full session from verifytotp and some return only an
        // acknowledgement, expecting the TOTP to be replayed as the request_token on the
        // ordinary session route. Handle both rather than betting on one.
        if (!string.IsNullOrWhiteSpace(verified.Value.AccessToken))
        {
            return BuildSession(verified.Value, apiKey, username)
                .Map<AuthStep>(s => new AuthStep.Completed(s));
        }

        var session = await api.PostFormAsync<MStockSessionData>(
            _options.SessionTokenPath,
            [
                new KeyValuePair<string, string>("api_key", apiKey),
                new KeyValuePair<string, string>("request_token", code),
                new KeyValuePair<string, string>("checksum", ComputeChecksum(apiKey, code, null)),
            ],
            ct).ConfigureAwait(false);

        if (session.IsFailure)
        {
            return Result<AuthStep>.Failure(session.Error);
        }

        return BuildSession(session.Value, apiKey, username)
            .Map<AuthStep>(s => new AuthStep.Completed(s));
    }

    private Result<BrokerSession> BuildSession(MStockSessionData data, string apiKey, string? username)
    {
        if (string.IsNullOrWhiteSpace(data.AccessToken))
        {
            return Result<BrokerSession>.Failure(MStockErrors.MissingField(
                _options.SessionTokenPath,
                "access_token"));
        }

        var accountId = FirstNonEmpty(data.UserId, data.UserShortName, username);
        if (accountId is null)
        {
            return Result<BrokerSession>.Failure(MStockErrors.MissingField(
                _options.SessionTokenPath,
                "user_id"));
        }

        var issuedAt = MStockTime.ParseOr(data.LoginTime, _clock.UtcNow);

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MStockSessionKeys.ApiKey] = apiKey,
        };

        AddIfPresent(extras, MStockSessionKeys.EncToken, data.EncToken);
        AddIfPresent(extras, MStockSessionKeys.PublicToken, data.PublicToken);
        AddIfPresent(extras, MStockSessionKeys.UserName, data.UserName ?? username);

        // The entitlement lists come back on the session call and nowhere else. Carrying them
        // means the order ticket can refuse an NFO order from an account with no derivative
        // entitlement locally, instead of learning about it from an exchange rejection.
        AddIfPresent(extras, MStockSessionKeys.Exchanges, Join(data.Exchanges));
        AddIfPresent(extras, MStockSessionKeys.Products, Join(data.Products));
        AddIfPresent(extras, MStockSessionKeys.OrderTypes, Join(data.OrderTypes));

        return new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = accountId,
            AccessToken = data.AccessToken,
            RefreshToken = data.RefreshToken,
            ExpiresAt = ComputeExpiry(issuedAt, _options.TokenLifetime, _venueZone),
            Extras = extras,
        };
    }

    /// <summary>
    /// mStock signs the session exchange with a SHA-256 checksum over the api key, the
    /// request token and the api secret, in that order, hex-encoded lowercase — the same
    /// recipe the rest of the Kite-lineage APIs use.
    ///
    /// Not every Type A subscription is issued a secret. When there is none the checksum is
    /// computed over the first two components alone; mStock accepts that for those accounts.
    /// The secret is never logged and never leaves this method.
    /// </summary>
    internal static string ComputeChecksum(string apiKey, string requestToken, string? apiSecret)
    {
        var payload = string.IsNullOrEmpty(apiSecret)
            ? apiKey + requestToken
            : apiKey + requestToken + apiSecret;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static void AddIfPresent(IDictionary<string, string> target, string key, string? value)
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
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// RFC 6238 time-based one-time passwords.
///
/// Implemented here rather than taken as a dependency because the connector SDK is not
/// allowed to grow one for a single broker, and because it is thirty lines. The seed is only
/// ever present when the user has explicitly chosen to store it with us; the wizard's default
/// is to prompt for the code instead.
/// </summary>
internal static class MStockTotp
{
    private const int Digits = 6;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>Generates the current code for a base32-encoded seed.</summary>
    public static Result<string> Generate(string base32Secret, DateTimeOffset at)
    {
        var key = DecodeBase32(base32Secret);
        if (key is null)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The stored authenticator secret is not valid base32 and no code can be generated from it."));
        }

        var counter = at.ToUnixTimeSeconds() / (long)Step.TotalSeconds;
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counterBytes, hash);

        // Dynamic truncation, RFC 4226 section 5.4.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    private static byte[]? DecodeBase32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var cleaned = value.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (cleaned.Length == 0)
        {
            return null;
        }

        var output = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bitsFilled = 0;

        foreach (var c in cleaned)
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bitsFilled += 5;

            if (bitsFilled >= 8)
            {
                bitsFilled -= 8;
                output.Add((byte)((buffer >> bitsFilled) & 0xFF));
            }
        }

        return output.Count == 0 ? null : [.. output];
    }
}
