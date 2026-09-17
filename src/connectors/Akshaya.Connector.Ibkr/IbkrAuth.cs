using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Linking an IBKR account through a Client Portal Gateway, expressed as the contract's <see cref="AuthStep"/> walk.
///
/// There is no login here. The gateway holds the brokerage session: the user signs in to the gateway itself, in a
/// browser on the machine it runs on, with their IBKR username and second factor, and every client of that gateway
/// acts as that username. Linking checks that the gateway is signed in and chooses which account to trade:
///
/// <code>
///   BeginAsync / ContinueAsync (the same walk; the wizard's "check again" re-runs it)
///     POST /iserver/auth/status ─ unreachable, signed out, competing ─► GatewayRequired(what to do)
///     GET  /iserver/accounts    ─► pick the account
///     ─► Completed(session naming the account)
/// </code>
///
/// The session is kept alive by the host calling <see cref="KeepAliveAsync"/> — <c>POST /tickle</c> — on the
/// manifest's interval, because the gateway's brokerage session times out after a few idle minutes.
/// </summary>
public sealed class IbkrAuth : IConnectorAuth
{
    public const string ConnectorId = "ibkr";

    /// <summary>The gateway id the manifest declares and the host's configuration keys on.</summary>
    public const string GatewayId = "ibkr-cpgw";

    public const string AccountIdField = "account_id";

    private readonly IbkrOptions _options;
    private readonly GatewayAddress? _gateway;
    private readonly IbkrErrorMapper _errors;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _sessionLifetime;

    internal IbkrAuth(
        IbkrOptions options,
        GatewayAddress? gateway,
        IbkrErrorMapper errors,
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
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) => LinkAsync(context, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The response is ignored. After a <see cref="AuthStep.GatewayRequired"/> step the wizard continues with an
    /// empty response meaning "check again", and there is no other step this connector returns.
    /// </remarks>
    public Task<Result<AuthStep>> ContinueAsync(AuthContext context, string response, CancellationToken ct = default) =>
        LinkAsync(context, ct);

    /// <inheritdoc />
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result<BrokerSession>.Failure(ConnectorErrors.NotSupported(
            "silent session refresh. The Client Portal Gateway holds the IBKR session, so there is nothing for the "
            + "platform to refresh; linking again re-checks the gateway and the account")));

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately does not log the gateway out. The gateway belongs to the operator and may serve other tools —
    /// logging it out when one link is removed would end their session too.
    /// </remarks>
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public async Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default)
    {
        if (_gateway is null)
        {
            return Result.Failure(IbkrErrors.NoGatewayAddress());
        }

        var tickle = await IbkrApi.Create(_gateway, _options, _errors, _logger)
            .PostAsync<IbkrTickle>("tickle", body: null, ct)
            .ConfigureAwait(false);

        if (tickle.IsFailure)
        {
            return Result.Failure(tickle.Error);
        }

        return tickle.Value.Server?.AuthStatus is { Authenticated: true }
            ? Result.Success()
            : Result.Failure(IbkrErrors.SignedOut(_gateway));
    }

    private async Task<Result<AuthStep>> LinkAsync(AuthContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_gateway is null)
        {
            return Result<AuthStep>.Failure(IbkrErrors.NoGatewayAddress());
        }

        var api = IbkrApi.Create(_gateway, _options, _errors, _logger);

        var status = await api.PostAsync<IbkrAuthStatus>("iserver/auth/status", body: null, ct).ConfigureAwait(false);
        if (status.IsFailure)
        {
            // Nothing answering, a refused certificate, a signed-out gateway: all are fixed at the gateway, which is
            // exactly what GatewayRequired exists to say.
            return status.Error.Code is ConnectorErrorCodes.GatewayUnavailable
                or ConnectorErrorCodes.ReauthRequired
                or ConnectorErrorCodes.SessionExpired
                or ConnectorErrorCodes.Timeout
                or ConnectorErrorCodes.BrokerUnavailable
                ? GatewayRequired($"{StartInstructions()} ({status.Error.Message})")
                : Result<AuthStep>.Failure(status.Error);
        }

        if (!status.Value.Authenticated)
        {
            return GatewayRequired(StartInstructions());
        }

        if (status.Value.Competing)
        {
            return GatewayRequired(
                "Another session is using this IBKR username — Trader Workstation, IBKR Mobile or another gateway. IBKR "
                + "allows one brokerage session per username; close the other one, then check again.");
        }

        if (!status.Value.Connected)
        {
            return GatewayRequired(
                $"The Client Portal Gateway at {_gateway} is signed in but not connected to IBKR's servers"
                + (string.IsNullOrWhiteSpace(status.Value.Message) ? "." : $": {status.Value.Message}.")
                + " Check its network, then check again.");
        }

        var accounts = await api.GetAsync<IbkrAccounts>("iserver/accounts", ct).ConfigureAwait(false);
        if (accounts.IsFailure)
        {
            return Result<AuthStep>.Failure(accounts.Error);
        }

        var picked = PickAccount(accounts.Value, context.Credentials.GetOrDefault(AccountIdField));
        if (picked.IsFailure)
        {
            return Result<AuthStep>.Failure(picked.Error);
        }

        var paper = accounts.Value.IsPaper == true;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{ConnectorId}: linked IBKR account {AccountId} ({Environment}) through the gateway at {Gateway}.",
                ConnectorId,
                picked.Value,
                paper ? "paper" : "live",
                _gateway.ToString());
        }

        return new AuthStep.Completed(new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = picked.Value,

            // The gateway holds the real session; this names what the link is bound to, for logs and keying.
            AccessToken = $"cpgw:{picked.Value}",
            RefreshToken = null,
            ExpiresAt = _clock.UtcNow + _sessionLifetime,
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IbkrSessionKeys.Paper] = paper ? bool.TrueString : bool.FalseString,
            },
        });
    }

    /// <summary>
    /// The account named in the link form, which must be one the gateway lists; or the only account there is. Several
    /// accounts and none named is a failure, never a guess — an order in the wrong account is a real loss.
    /// </summary>
    private static Result<string> PickAccount(IbkrAccounts accounts, string? wanted)
    {
        var listed = (accounts.Accounts ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (listed.Count == 0)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "The Client Portal Gateway is signed in, but the username has no account it may trade."));
        }

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = listed.FirstOrDefault(a => string.Equals(a, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            return match is not null
                ? match
                : Result<string>.Failure(new Error(
                    ConnectorErrorCodes.InvalidCredentials,
                    $"The signed-in IBKR username cannot trade account '{wanted.Trim()}'. It can trade: {string.Join(", ", listed)}."));
        }

        return listed.Count == 1
            ? listed[0]
            : Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                $"The signed-in IBKR username can trade {listed.Count} accounts ({string.Join(", ", listed)}). Enter the account id to link."));
    }

    private string StartInstructions() =>
        $"Start the IBKR Client Portal Gateway so it listens at {_gateway}, open https://{_gateway}/ in a browser on "
        + "the machine it runs on, and sign in with the IBKR username for this account. Then check again.";

    private static Result<AuthStep> GatewayRequired(string instructions) =>
        new AuthStep.GatewayRequired(GatewayId, instructions);
}
