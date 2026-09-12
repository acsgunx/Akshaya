using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// The Longbridge sign-in: OAuth 2.0 authorization code with PKCE, as the contract's <see cref="AuthStep"/> walk.
///
/// <code>
///   BeginAsync
///     -> RedirectRequired(https://openapi.longbridge.com/oauth2/authorize?response_type=code&amp;client_id=…
///                         &amp;redirect_uri=…&amp;scope=3&amp;state=…&amp;code_challenge=…&amp;code_challenge_method=S256)
///   ContinueAsync(code)
///     POST /oauth2/token   grant_type=authorization_code, client_id, redirect_uri, code, code_verifier
///       -> access_token, refresh_token, expires_in (thirty days)
///     quote socket: one-time password, AUTH, QueryUserQuoteProfile -> member_id, the account identity
///       -> Completed(BrokerSession)
/// </code>
///
/// THE PKCE VERIFIER IS DERIVED, NOT STORED. The contract carries one value across the redirect — the state —
/// and the verifier must not travel with it: the state comes back on the same URL as the code, so anyone placed
/// to steal the code would take a verifier riding beside it too. The verifier is instead an HMAC of the state
/// under a key generated when this process starts, recoverable on Continue and unknowable without the key. The
/// cost is small and visible: a sign-in begun before a restart, or on another instance of a scaled-out
/// deployment, fails its token exchange and has to be started again.
///
/// THE ACCOUNT IDENTITY COMES FROM THE QUOTE GATEWAY. No REST route names the account, and the trade pushes'
/// <c>account_no</c> arrives only with an order. The member id the quote gateway reports for the token is
/// stable per login, so it is what <see cref="BrokerSession.AccountId"/> carries — suffixed for paper trading,
/// which shares the login but is a different book.
/// </summary>
public sealed class LongbridgeAuth : IConnectorAuth
{
    /// <summary>The connector id stamped into every session this facet issues.</summary>
    public const string ConnectorId = "longbridge";

    public const string ClientIdField = "client_id";

    public const string ClientSecretField = "client_secret";

    public const string EnvironmentField = "environment";

    /// <summary>Key under which the host carries the OAuth state back into <see cref="ContinueAsync"/>.</summary>
    public const string StateKey = "state";

    /// <summary>Per-process key the PKCE verifier is derived under. See the type remarks.</summary>
    private static readonly byte[] VerifierKey = RandomNumberGenerator.GetBytes(32);

    private readonly LongbridgeOptions _options;
    private readonly LongbridgeErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _nominalLifetime;
    private readonly Func<LongbridgeCredentials?, LongbridgeApi> _apiFactory;

    internal LongbridgeAuth(
        LongbridgeOptions options,
        LongbridgeErrorMapper errors,
        IClock clock,
        ILogger logger,
        TimeSpan nominalLifetime,
        Func<LongbridgeCredentials?, LongbridgeApi> apiFactory)
    {
        _options = options;
        _errors = errors;
        _clock = clock;
        _logger = logger;
        _nominalLifetime = nominalLifetime;
        _apiFactory = apiFactory;
    }

    private string AuthorizeUrl => new Uri(_options.OAuthBaseUrl, "authorize").ToString();

    private string TokenUrl => new Uri(_options.OAuthBaseUrl, "token").ToString();

    private string RevokeUrl => new Uri(_options.OAuthBaseUrl, "revoke").ToString();

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var clientId = context.Credentials.GetOrDefault(ClientIdField)?.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            return Failed(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Longbridge needs the client id of an OAuth client registered with this deployment's callback URL."));
        }

        var paper = ReadPaper(context.Credentials);
        if (paper.IsFailure)
        {
            return Failed(paper.Error);
        }

        if (string.IsNullOrWhiteSpace(context.RedirectUri))
        {
            return Failed(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "Longbridge needs the callback URL this deployment registered its OAuth client with. It must match "
                + "one of the client's redirect_uris exactly, or Longbridge refuses the sign-in."));
        }

        // A caller resuming a partly built flow may already hold a state it intends to check; honour it.
        var state = context.State.GetValueOrDefault(StateKey) is { Length: > 0 } supplied
            ? supplied
            : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        var url = HttpConnectorPath.WithQuery(
            AuthorizeUrl,
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", context.RedirectUri),
            ("scope", _options.OAuthScope),
            ("state", state),
            ("code_challenge", CodeChallenge(CodeVerifier(state))),
            ("code_challenge_method", "S256"));

        return Task.FromResult<Result<AuthStep>>(new AuthStep.RedirectRequired(url, state));
    }

    /// <inheritdoc />
    public async Task<Result<AuthStep>> ContinueAsync(AuthContext context, string response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var code = FirstNonEmpty(response, context.CallbackCode);
        if (code is null)
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "No authorisation code came back from the Longbridge sign-in."));
        }

        var clientId = context.Credentials.GetOrDefault(ClientIdField)?.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The Longbridge client id is missing; the sign-in cannot be completed."));
        }

        var paper = ReadPaper(context.Credentials);
        if (paper.IsFailure)
        {
            return Result<AuthStep>.Failure(paper.Error);
        }

        if (context.State.GetValueOrDefault(StateKey) is not { Length: > 0 } state)
        {
            return Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "The sign-in state did not come back with the Longbridge callback, so the PKCE verifier cannot be "
                + "recovered. Start the link again."));
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("client_id", clientId),
            new("code", code),
            new("code_verifier", CodeVerifier(state)),
        };

        if (!string.IsNullOrWhiteSpace(context.RedirectUri))
        {
            form.Add(new("redirect_uri", context.RedirectUri));
        }

        if (context.Credentials.GetOrDefault(ClientSecretField) is { Length: > 0 } secret)
        {
            form.Add(new("client_secret", secret));
        }

        var token = await ExchangeAsync(form, ct).ConfigureAwait(false);
        if (token.IsFailure)
        {
            // invalid_grant on a code exchange is an expired or reused code — or a verifier this process cannot
            // reproduce, because the sign-in began before a restart or on another instance.
            return Result<AuthStep>.Failure(token.Error.Code == ConnectorErrorCodes.ReauthRequired
                ? token.Error with
                {
                    Code = ConnectorErrorCodes.ChallengeFailed,
                    Message = "Longbridge refused the sign-in code: it expired, was already used, or the sign-in was "
                              + "started before this server restarted. Start the link again.",
                }
                : token.Error);
        }

        var accessToken = token.Value.AccessToken!;
        var memberId = await ResolveMemberIdAsync(new LongbridgeCredentials(clientId, accessToken, paper.Value), ct)
            .ConfigureAwait(false);

        if (memberId.IsFailure)
        {
            return Result<AuthStep>.Failure(memberId.Error);
        }

        return new AuthStep.Completed(new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = paper.Value ? $"{memberId.Value}-paper" : memberId.Value,
            AccessToken = accessToken,
            RefreshToken = token.Value.RefreshToken,
            ExpiresAt = ExpiresAt(_clock.UtcNow, token.Value.ExpiresIn),
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LongbridgeSessionKeys.ClientId] = clientId,
                [LongbridgeSessionKeys.Environment] = paper.Value ? LongbridgeMaps.EnvironmentPaper : LongbridgeMaps.EnvironmentReal,
                [LongbridgeSessionKeys.MemberId] = memberId.Value,
            },
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Silent for the documented default, a public client: the refresh grant needs only the client id and the
    /// refresh token. A confidential client's secret is not kept after linking — it would be a second standing
    /// credential stored beside the tokens for the sole purpose of skipping one sign-in a month — so Longbridge
    /// refuses such a client's refresh, which surfaces as a sign-in prompt. A timeout or a 5xx stays a transient
    /// failure for the session monitor to retry; only a refusal becomes a prompt.
    /// </remarks>
    public async Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return Result<BrokerSession>.Failure(ConnectorErrors.ReauthRequired(ConnectorId));
        }

        if (session.Extras.GetValueOrDefault(LongbridgeSessionKeys.ClientId) is not { Length: > 0 } clientId)
        {
            return Result<BrokerSession>.Failure(LongbridgeErrors.MalformedSession("no client id"));
        }

        var token = await ExchangeAsync(
            [
                new("grant_type", "refresh_token"),
                new("client_id", clientId),
                new("refresh_token", session.RefreshToken),
            ],
            ct).ConfigureAwait(false);

        if (token.IsFailure)
        {
            return Result<BrokerSession>.Failure(token.Error);
        }

        return session with
        {
            AccessToken = token.Value.AccessToken!,

            // Refresh tokens may or may not rotate; keep the old one when a new one is not issued.
            RefreshToken = token.Value.RefreshToken is { Length: > 0 } rotated ? rotated : session.RefreshToken,
            ExpiresAt = ExpiresAt(_clock.UtcNow, token.Value.ExpiresIn),
        };
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Extras.GetValueOrDefault(LongbridgeSessionKeys.ClientId) is not { Length: > 0 } clientId)
        {
            // Nothing identifies the client to revoke for; the link is being removed regardless.
            return Result.Success();
        }

        // Revoking the refresh token ends the grant, and the access token with it (RFC 7009 section 2.1).
        var (token, hint) = session.RefreshToken is { Length: > 0 } refresh
            ? (refresh, "refresh_token")
            : (session.AccessToken, "access_token");

        var form = new List<KeyValuePair<string, string>>
        {
            new("token", token),
            new("token_type_hint", hint),
            new("client_id", clientId),
        };

        await using var api = _apiFactory(null);

        var result = await api.Transport
            .SendNoContentAsync(
                () => new HttpRequestMessage(HttpMethod.Post, RevokeUrl) { Content = new FormUrlEncodedContent(form) },
                ct)
            .ConfigureAwait(false);

        // A token Longbridge has already discarded cannot be revoked again; that is the outcome wanted.
        return result.IsSuccess
               || result.Error.Code is ConnectorErrorCodes.SessionExpired or ConnectorErrorCodes.ReauthRequired
                   or ConnectorErrorCodes.InvalidRequest
            ? Result.Success()
            : result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Longbridge's REST tokens do not idle out, so the manifest declares no keepalive interval. The sockets keep
    /// themselves alive with WebSocket pings; see <see cref="LongbridgeOptions.SocketKeepAlive"/>.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>The verifier for a state: 43 base64url characters, the RFC 7636 minimum length and alphabet.</summary>
    internal static string CodeVerifier(string state) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(VerifierKey, Encoding.UTF8.GetBytes(state)));

    /// <summary>S256: base64url of the SHA-256 of the verifier's ASCII bytes.</summary>
    internal static string CodeChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<Result<LbTokenResponse>> ExchangeAsync(List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        await using var api = _apiFactory(null);

        var response = await api.Transport.PostFormAsync<LbTokenResponse>(TokenUrl, form, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return response;
        }

        return string.IsNullOrWhiteSpace(response.Value.AccessToken)
            ? Result<LbTokenResponse>.Failure(LongbridgeErrors.MissingField("/oauth2/token", "access_token"))
            : response;
    }

    private async Task<Result<string>> ResolveMemberIdAsync(LongbridgeCredentials credentials, CancellationToken ct)
    {
        await using var api = _apiFactory(credentials);

        var socket = await LongbridgeSocket
            .OpenAsync(_options.QuoteSocketUrl, api, _options, _errors, _logger, receivePushes: false, ct)
            .ConfigureAwait(false);

        if (socket.IsFailure)
        {
            return Result<string>.Failure(Explain(socket.Error));
        }

        await using var quote = socket.Value;

        var member = await quote.QueryMemberIdAsync(ct).ConfigureAwait(false);
        return member.IsFailure
            ? Result<string>.Failure(Explain(member.Error))
            : member.Value.ToString(CultureInfo.InvariantCulture);

        static Error Explain(Error error) => error with
        {
            Message = $"Signed in to Longbridge, but could not read the account identity from its quote gateway: {error.Message}",
        };
    }

    private DateTimeOffset ExpiresAt(DateTimeOffset issuedAt, long? expiresInSeconds) =>
        issuedAt + (expiresInSeconds is > 0 ? TimeSpan.FromSeconds(expiresInSeconds.Value) : _nominalLifetime);

    private static Result<bool> ReadPaper(AuthCredentials credentials)
    {
        var environment = credentials.GetOrDefault(EnvironmentField)?.Trim();

        if (string.IsNullOrEmpty(environment)
            || string.Equals(environment, LongbridgeMaps.EnvironmentReal, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(environment, LongbridgeMaps.EnvironmentPaper, StringComparison.OrdinalIgnoreCase)
            ? true
            : Result<bool>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"'{environment}' is not a Longbridge environment: use real or paper."));
    }

    private static Task<Result<AuthStep>> Failed(Error error) => Task.FromResult(Result<AuthStep>.Failure(error));

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
