using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Linking a moomoo account, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// There is no login here in the usual sense. OpenD holds the moomoo session: the user signs in to
/// OpenD itself — with their moomoo account, a verification code if it asks, and the API questionnaire
/// the first time — and every client of that OpenD acts as that user. So "authenticating" is checking
/// that OpenD is ready and choosing which of its accounts to trade:
///
/// <code>
///   BeginAsync / ContinueAsync (the same walk; the wizard's "check again" re-runs it)
///     connect + InitConnect ─ fails ─► GatewayRequired("start OpenD and sign in")
///     GetGlobalState ─ not signed in ─► GatewayRequired(OpenD's own status text)
///     Trd_GetAccList ─► pick the account (see PickAccount)
///     real account + trade password ─► Trd_UnlockTrade
///     ─► Completed(session naming the account)
/// </code>
///
/// Two things about unlocking decide the shape of the last step. OpenD's documentation says "as long as
/// one connection is unlocked, all other connections can call the transaction interface", so one unlock
/// here serves every request connection and the stream. And "the GUI version of OpenD does not support
/// unlocking via the unlock interface": on that build the trader clicks Unlock in the window, so a
/// refused unlock that is not a wrong password does not fail the link. An order placed while trading is
/// still locked fails with a clear ReauthRequired instead.
/// </summary>
public sealed class MoomooAuth : IConnectorAuth
{
    public const string ConnectorId = "moomoo";

    /// <summary>The gateway id the manifest declares and the host's configuration keys on.</summary>
    public const string GatewayId = "moomoo-opend";

    public const string AccountIdField = "account_id";
    public const string TradePasswordField = "trade_password";
    public const string EnvironmentField = "environment";

    private readonly MoomooOptions _options;
    private readonly GatewayAddress? _gateway;
    private readonly MoomooErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _sessionLifetime;

    internal MoomooAuth(
        MoomooOptions options,
        GatewayAddress? gateway,
        MoomooErrorMapper errors,
        IClock clock,
        ILogger logger,
        TimeSpan sessionLifetime)
    {
        _options = options;
        _gateway = gateway;
        _errors = errors;
        _clock = clock;
        _logger = logger;
        _sessionLifetime = sessionLifetime;
    }

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) =>
        LinkAsync(context, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The response is ignored. After a <see cref="AuthStep.GatewayRequired"/> step the wizard continues
    /// with an empty response meaning "check again", and there is no other step this connector returns.
    /// </remarks>
    public Task<Result<AuthStep>> ContinueAsync(AuthContext context, string response, CancellationToken ct = default) =>
        LinkAsync(context, ct);

    /// <inheritdoc />
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result<BrokerSession>.Failure(ConnectorErrors.NotSupported(
            "silent session refresh. OpenD holds the moomoo session, so there is nothing for the platform to "
            + "refresh; linking again re-checks OpenD and the account")));

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately does not lock trading. Unlocking is state on OpenD shared by every client connected to
    /// it, so locking on unlink would stop the trader's other tools — the moomoo desktop app talking to
    /// the same OpenD, a second strategy — at the moment they remove one link here.
    /// </remarks>
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    /// <remarks>
    /// OpenD keeps its own moomoo session alive; the per-connection KeepAlive the protocol requires is
    /// sent by each connection itself. The manifest therefore declares no keepAliveInterval.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    private async Task<Result<AuthStep>> LinkAsync(AuthContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var environment = ParseEnvironment(context.Credentials.GetOrDefault(EnvironmentField));
        if (environment.IsFailure)
        {
            return Result<AuthStep>.Failure(environment.Error);
        }

        ulong? wantedAccount = null;
        var accountText = context.Credentials.GetOrDefault(AccountIdField)?.Trim();
        if (!string.IsNullOrEmpty(accountText))
        {
            if (!ulong.TryParse(accountText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return Result<AuthStep>.Failure(new Error(
                    ConnectorErrorCodes.InvalidCredentials,
                    "The moomoo account id must be the numeric id OpenD reports, for example 281756455981234567."));
            }

            wantedAccount = parsed;
        }

        if (_gateway is null)
        {
            return Result<AuthStep>.Failure(MoomooErrors.NoGatewayAddress());
        }

        var opened = await MoomooConnection
            .OpenAsync(_gateway, _options, _errors, _clock, _logger, receivePushes: false, ct)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            // Nothing listening, or something that is not an unencrypted OpenD. Either way the user has a
            // daemon to start or fix, which is exactly what GatewayRequired exists to say.
            return GatewayRequired(opened.Error.Message);
        }

        await using var connection = opened.Value;

        var state = await connection.RequestAsync<OpenDGetGlobalStateC2S, OpenDGetGlobalStateS2C>(
            MoomooProtoId.GetGlobalState,
            new OpenDGetGlobalStateC2S(),
            ct).ConfigureAwait(false);

        if (state.IsFailure)
        {
            return GatewayRequired(state.Error.Message);
        }

        if (!state.Value.TrdLogined || !state.Value.QotLogined || state.Value.ProgramStatus is { IsReady: false })
        {
            var status = state.Value.ProgramStatus is { } program
                ? $"OpenD reports status {program.TypeText}{(string.IsNullOrWhiteSpace(program.Description) ? string.Empty : $": {program.Description}")}."
                : "OpenD is running but is not yet signed in to moomoo's trade and quote servers.";

            return GatewayRequired(status);
        }

        var accounts = await connection.RequestAsync<OpenDGetAccListC2S, OpenDGetAccListS2C>(
            MoomooProtoId.TrdGetAccList,
            new OpenDGetAccListC2S
            {
                TrdCategory = MoomooMaps.TrdCategorySecurity,
                NeedGeneralSecAccount = true,
            },
            ct).ConfigureAwait(false);

        if (accounts.IsFailure)
        {
            return Result<AuthStep>.Failure(accounts.Error);
        }

        var picked = PickAccount(accounts.Value.AccList ?? [], environment.Value, wantedAccount);
        if (picked.IsFailure)
        {
            return Result<AuthStep>.Failure(picked.Error);
        }

        var (account, markets) = picked.Value;

        var password = context.Credentials.GetOrDefault(TradePasswordField);
        if (environment.Value == MoomooMaps.TrdEnvReal && !string.IsNullOrEmpty(password))
        {
            var unlocked = await connection.RequestAsync<OpenDUnlockTradeC2S, OpenDEmpty>(
                MoomooProtoId.TrdUnlockTrade,
                new OpenDUnlockTradeC2S
                {
                    Unlock = true,
                    PwdMd5 = TradePasswordHash(password),
                    SecurityFirm = account.SecurityFirm,
                },
                ct).ConfigureAwait(false);

            if (unlocked.IsFailure)
            {
                if (IsWrongPassword(unlocked.Error.VendorMessage))
                {
                    return Result<AuthStep>.Failure(new Error(
                        ConnectorErrorCodes.InvalidCredentials,
                        "moomoo did not accept the trade password.",
                        unlocked.Error.VendorCode,
                        unlocked.Error.VendorMessage));
                }

                // Most often the GUI build of OpenD, which refuses an unlock over the API by design. The
                // trader unlocks in its window; see the class remarks.
                _logger.LogWarning(
                    "{ConnectorId}: OpenD refused to unlock trading over the API ({Message}); linking anyway. "
                    + "Orders will fail until trading is unlocked in OpenD.",
                    ConnectorId,
                    unlocked.Error.VendorMessage ?? unlocked.Error.Message);
            }
        }

        return new AuthStep.Completed(BuildSession(account, markets, environment.Value, connection.LoginUserId));
    }

    /// <summary>
    /// Which account to link.
    ///
    /// Only an active account in the requested environment that can trade at least one in-scope market is
    /// a candidate. An explicit account id wins. Otherwise a single candidate is taken, and among several
    /// the one reaching the most in-scope markets — a universal account over a market-specific one — when
    /// that choice is unique. Anything else is a real decision about whose money moves, and it is handed
    /// back to the user with the candidates listed rather than guessed.
    /// </summary>
    internal static Result<(OpenDTrdAcc Account, IReadOnlyList<MoomooMarket> Markets)> PickAccount(
        IReadOnlyList<OpenDTrdAcc> accounts,
        int trdEnv,
        ulong? wantedAccount)
    {
        var environment = trdEnv == MoomooMaps.TrdEnvReal ? "live" : "paper";

        var candidates = accounts
            .Where(a => a.TrdEnv == trdEnv && (a.AccStatus ?? MoomooMaps.TrdAccStatusActive) == MoomooMaps.TrdAccStatusActive)
            .Select(a => (Account: a, Markets: InScopeMarkets(a)))
            .Where(c => c.Markets.Count > 0)
            .ToList();

        if (wantedAccount is { } wanted)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Account.AccId == wanted)
                {
                    return candidate;
                }
            }

            return Result<(OpenDTrdAcc, IReadOnlyList<MoomooMarket>)>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"Account {wanted.ToString(CultureInfo.InvariantCulture)} is not an active {environment} account on "
                + $"this OpenD that can trade US or Hong Kong securities.{Describe(candidates)}"));
        }

        if (candidates.Count == 0)
        {
            return Result<(OpenDTrdAcc, IReadOnlyList<MoomooMarket>)>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"OpenD is signed in, but this moomoo login has no active {environment} securities account that can "
                + "trade US or Hong Kong securities."));
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var widest = candidates.Max(c => c.Markets.Count);
        var best = candidates.Where(c => c.Markets.Count == widest).ToList();

        return best.Count == 1
            ? best[0]
            : Result<(OpenDTrdAcc, IReadOnlyList<MoomooMarket>)>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"This moomoo login has several {environment} securities accounts. Enter the account id to link."
                + Describe(candidates)));
    }

    private static List<MoomooMarket> InScopeMarkets(OpenDTrdAcc account)
    {
        var markets = new List<MoomooMarket>(2);
        foreach (var code in account.TrdMarketAuthList ?? [])
        {
            var market = MoomooMaps.MarketForTrdMarket(code);
            if (market.IsSuccess && !markets.Contains(market.Value))
            {
                markets.Add(market.Value);
            }
        }

        // Always in the same order, so a session's market list does not depend on OpenD's ordering.
        return [.. MoomooMarket.All.Where(markets.Contains)];
    }

    private static string Describe(IEnumerable<(OpenDTrdAcc Account, List<MoomooMarket> Markets)> candidates)
    {
        var lines = candidates
            .Select(c => $"{c.Account.AccId.ToString(CultureInfo.InvariantCulture)} "
                         + $"({string.Join('/', c.Markets.Select(m => m.Prefix))}"
                         + $"{(CardSuffix(c.Account.CardNum) is { } suffix ? $", card ending {suffix}" : string.Empty)})")
            .ToList();

        return lines.Count == 0 ? string.Empty : $" Accounts available: {string.Join("; ", lines)}.";
    }

    private BrokerSession BuildSession(OpenDTrdAcc account, IReadOnlyList<MoomooMarket> markets, int trdEnv, ulong loginUserId)
    {
        var issuedAt = _clock.UtcNow;

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MoomooSessionKeys.TrdEnv] = trdEnv.ToString(CultureInfo.InvariantCulture),
            [MoomooSessionKeys.Environment] = trdEnv == MoomooMaps.TrdEnvReal ? MoomooMaps.EnvironmentReal : MoomooMaps.EnvironmentPaper,
            [MoomooSessionKeys.Markets] = string.Join(',', markets.Select(m => m.Prefix)),
            [MoomooSessionKeys.LoginUserId] = loginUserId.ToString(CultureInfo.InvariantCulture),
        };

        if (account.AccType is MoomooMaps.TrdAccTypeCash or MoomooMaps.TrdAccTypeMargin)
        {
            extras[MoomooSessionKeys.AccountType] = account.AccType == MoomooMaps.TrdAccTypeCash ? "cash" : "margin";
        }

        if (account.SecurityFirm is { } firm)
        {
            extras[MoomooSessionKeys.SecurityFirm] = firm.ToString(CultureInfo.InvariantCulture);
        }

        if (CardSuffix(account.CardNum) is { } suffix)
        {
            extras[MoomooSessionKeys.CardNumberSuffix] = suffix;
        }

        return new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = account.AccId.ToString(CultureInfo.InvariantCulture),

            // OpenD holds the real session, so there is no token to carry. The contract requires one, and
            // an opaque, non-secret marker is more honest than an empty string that looks like a bug.
            AccessToken = $"opend:{account.AccId.ToString(CultureInfo.InvariantCulture)}",
            RefreshToken = null,

            // OpenD's session has no expiry of its own; it lasts until OpenD stops. A daily bound is a
            // deliberate re-check: linking again confirms OpenD is still signed in to the same account and
            // re-unlocks after an overnight restart. See docs/connectors/moomoo.md.
            ExpiresAt = issuedAt + _sessionLifetime,
            Extras = SessionMonitor.WithIssuedAt(extras, issuedAt),
        };
    }

    private Result<AuthStep> GatewayRequired(string detail) =>
        new AuthStep.GatewayRequired(
            GatewayId,
            $"Start OpenD at {_gateway?.ToString() ?? "its configured address"} and sign in with your moomoo account — "
            + "complete any verification code and, the first time, the API questionnaire it shows — then check again. "
            + detail);

    private static Result<int> ParseEnvironment(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or MoomooMaps.EnvironmentReal => MoomooMaps.TrdEnvReal,
        MoomooMaps.EnvironmentPaper => MoomooMaps.TrdEnvSimulate,
        _ => Result<int>.Failure(new Error(
            ConnectorErrorCodes.InvalidCredentials,
            $"The moomoo environment must be '{MoomooMaps.EnvironmentReal}' or '{MoomooMaps.EnvironmentPaper}'.")),
    };

    /// <summary>
    /// OpenD's documented unlock credential: the lowercase hex MD5 of the trade password. The protocol
    /// fixes the algorithm; the password itself never leaves this method.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Trd_UnlockTrade's pwdMD5 field is defined by the OpenD protocol. MD5 here is a wire format, not a protection this connector chose.")]
    internal static string TradePasswordHash(string password) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password)));

    private static bool IsWrongPassword(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("密码", StringComparison.Ordinal));

    private static string? CardSuffix(string? cardNumber)
    {
        var digits = cardNumber?.Trim();
        return string.IsNullOrEmpty(digits) ? null : digits.Length <= 4 ? digits : digits[^4..];
    }
}
