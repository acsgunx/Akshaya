using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Linking a Tiger account, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// There is no login and no token to fetch: Tiger authenticates every request by its RSA signature, so linking is
/// checking that the key signs something Tiger accepts, and that the account it names can be read:
///
/// <code>
///   BeginAsync / ContinueAsync (the same walk; there is no interactive step)
///     prime_assets { account } ─ refused ─► InvalidCredentials, with Tiger's words
///     ─► Completed(session carrying the signing key)
/// </code>
///
/// The session's AccessToken is the private key itself — see <see cref="TigerCredentials"/> for why that is the
/// right place for it.
/// </summary>
public sealed class TigerAuth : IConnectorAuth
{
    public const string ConnectorId = "tiger";

    public const string TigerIdField = "tiger_id";
    public const string AccountField = "account";
    public const string PrivateKeyField = "private_key";
    public const string LicenseField = "license";
    public const string TokenField = "token";
    public const string EnvironmentField = "environment";

    private static readonly string[] KnownLicenses = ["TBNZ", "TBSG", "TBHK", "TBAU", "TBUS"];

    private readonly TigerOptions _options;
    private readonly TigerChannel _channel;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _sessionLifetime;

    internal TigerAuth(TigerOptions options, TigerChannel channel, IClock clock, ILogger logger, TimeSpan sessionLifetime)
    {
        _options = options;
        _channel = channel;
        _clock = clock;
        _logger = logger;
        _sessionLifetime = sessionLifetime;
    }

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) => LinkAsync(context, ct);

    /// <inheritdoc />
    /// <remarks>The response is ignored: this connector returns no challenge, so a continue simply re-runs the check.</remarks>
    public Task<Result<AuthStep>> ContinueAsync(AuthContext context, string response, CancellationToken ct = default) =>
        LinkAsync(context, ct);

    /// <inheritdoc />
    /// <remarks>
    /// There is nothing to refresh: an RSA key does not expire, and Tiger issues no session. A key that stops working
    /// has been revoked on the Tiger console, and the link has to be made again with a new one.
    /// </remarks>
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result<BrokerSession>.Failure(ConnectorErrors.NotSupported(
            "silent session refresh. Tiger authenticates each request by its signature, so there is no session to refresh")));

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is revoked at Tiger: the key belongs to the application, not to this link, and other tools may be
    /// signing with it. Removing the link discards the platform's copy, which is what unlinking means here.
    /// </remarks>
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    /// <remarks>Tiger drops nothing when idle, so the manifest declares no keepalive interval.</remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    private async Task<Result<AuthStep>> LinkAsync(AuthContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var credentials = Read(context.Credentials);
        if (credentials.IsFailure)
        {
            return Result<AuthStep>.Failure(credentials.Error);
        }

        // Signing a real request is the only way to know the key, the tiger id and the account agree.
        var api = _channel.Trading(credentials.Value);
        var biz = TigerBiz.New()
            .Add("account", credentials.Value.Account)
            .Add("lang", _options.Language);

        var assets = await api.CallAsync(TigerMaps.MethodPrimeAssets, biz, credentials.Value, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (assets.IsFailure)
        {
            return Result<AuthStep>.Failure(assets.Error);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{ConnectorId}: linked Tiger account {AccountId} ({Environment}) through {Endpoint}.",
                ConnectorId,
                credentials.Value.Account,
                credentials.Value.Paper ? "paper" : "live",
                api.Endpoint.ToString());
        }

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TigerSessionKeys.TigerId] = credentials.Value.TigerId,
            [TigerSessionKeys.Environment] = credentials.Value.Paper ? TigerMaps.EnvironmentPaper : TigerMaps.EnvironmentReal,
        };

        if (credentials.Value.License is { Length: > 0 } license)
        {
            extras[TigerSessionKeys.License] = license;
        }

        if (credentials.Value.Token is { Length: > 0 } token)
        {
            extras[TigerSessionKeys.Token] = token;
        }

        return new AuthStep.Completed(new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = credentials.Value.Account,

            // The signing key IS the credential; there is nothing else to carry.
            AccessToken = credentials.Value.PrivateKey,
            RefreshToken = null,
            ExpiresAt = _clock.UtcNow + _sessionLifetime,
            Extras = extras,
        });
    }

    private static Result<TigerCredentials> Read(AuthCredentials credentials)
    {
        var tigerId = credentials.GetOrDefault(TigerIdField)?.Trim();
        if (string.IsNullOrEmpty(tigerId))
        {
            return Result<TigerCredentials>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Tiger needs the tiger id of the application whose public key is registered on the Tiger console."));
        }

        var account = credentials.GetOrDefault(AccountField)?.Trim();
        if (string.IsNullOrEmpty(account))
        {
            return Result<TigerCredentials>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Tiger needs the account to trade."));
        }

        var privateKey = credentials.GetOrDefault(PrivateKeyField);
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return Result<TigerCredentials>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Tiger needs the RSA private key whose public half is registered on the Tiger console."));
        }

        var license = credentials.GetOrDefault(LicenseField)?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(license) && !KnownLicenses.Contains(license, StringComparer.Ordinal))
        {
            return Result<TigerCredentials>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"'{license}' is not a Tiger licence. Use one of {string.Join(", ", KnownLicenses)}, or leave it blank."));
        }

        var environment = credentials.GetOrDefault(EnvironmentField)?.Trim();
        var paper = string.Equals(environment, TigerMaps.EnvironmentPaper, StringComparison.OrdinalIgnoreCase);

        if (!paper
            && !string.IsNullOrEmpty(environment)
            && !string.Equals(environment, TigerMaps.EnvironmentReal, StringComparison.OrdinalIgnoreCase))
        {
            return Result<TigerCredentials>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"'{environment}' is not a Tiger environment: use real or paper."));
        }

        return new TigerCredentials(
            tigerId,
            account,
            privateKey.Trim(),
            string.IsNullOrEmpty(license) ? null : license,
            credentials.GetOrDefault(TokenField)?.Trim() is { Length: > 0 } token ? token : null,
            paper);
    }
}
