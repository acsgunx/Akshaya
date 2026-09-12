using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>Keys this connector writes into <see cref="BrokerSession.Extras"/>.</summary>
internal static class IbkrSessionKeys
{
    /// <summary><c>true</c> when the gateway reported a paper account.</summary>
    public const string Paper = "paper";
}

/// <summary>What every call needs from a linked session.</summary>
internal sealed record IbkrAccount(string AccountId, bool Paper)
{
    public static Result<IbkrAccount> FromSession(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return string.IsNullOrWhiteSpace(session.AccountId)
            ? Result<IbkrAccount>.Failure(IbkrErrors.MalformedSession("no account id"))
            : new IbkrAccount(
                session.AccountId.Trim(),
                string.Equals(session.Extras.GetValueOrDefault(IbkrSessionKeys.Paper), bool.TrueString, StringComparison.OrdinalIgnoreCase));
    }

    public static Result<IbkrAccount> Require(Func<Result<BrokerSession>> requireSession)
    {
        var session = requireSession();
        return session.IsFailure ? Result<IbkrAccount>.Failure(session.Error) : FromSession(session.Value);
    }
}

/// <summary>
/// The gateway client for one connector, and the route priming IBKR requires.
///
/// IBKR requires <c>/iserver/accounts</c> before any other iserver route in a brokerage session, and
/// <c>/portfolio/accounts</c> before any other portfolio route. The host builds a connector per request, so the
/// fact that a gateway has been primed is kept per gateway for a minute — long enough that a burst of requests
/// primes once, short enough that a gateway restarted underneath is primed again before most requests notice.
/// </summary>
internal sealed class IbkrChannel
{
    private static readonly TimeSpan PrimeLifetime = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, DateTimeOffset> IServerPrimedAt = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<IbkrPortfolioAccount> Accounts)> PortfolioPrimed =
        new(StringComparer.Ordinal);

    private readonly GatewayAddress? _gateway;
    private readonly IbkrOptions _options;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private IbkrApi? _api;

    public IbkrChannel(GatewayAddress? gateway, IbkrOptions options, IbkrErrorMapper errors, IClock clock, ILogger logger)
    {
        _gateway = gateway;
        _options = options;
        Errors = errors;
        _clock = clock;
        _logger = logger;
    }

    public IbkrErrorMapper Errors { get; }

    public GatewayAddress? Gateway => _gateway;

    public Result<IbkrApi> Api()
    {
        if (_gateway is null)
        {
            return Result<IbkrApi>.Failure(IbkrErrors.NoGatewayAddress());
        }

        return _api ??= IbkrApi.Create(_gateway, _options, Errors, _logger);
    }

    /// <summary>The client, with the iserver routes primed.</summary>
    public async Task<Result<IbkrApi>> IServerAsync(CancellationToken ct)
    {
        var api = Api();
        if (api.IsFailure)
        {
            return api;
        }

        var key = api.Value.BaseAddress;
        if (IServerPrimedAt.TryGetValue(key, out var primedAt) && _clock.UtcNow - primedAt < PrimeLifetime)
        {
            return api;
        }

        var accounts = await api.Value.GetAsync<IbkrAccounts>("iserver/accounts", ct).ConfigureAwait(false);
        if (accounts.IsFailure)
        {
            return Result<IbkrApi>.Failure(accounts.Error);
        }

        IServerPrimedAt[key] = _clock.UtcNow;
        return api;
    }

    /// <summary>The portfolio accounts, which also primes the portfolio routes.</summary>
    public async Task<Result<IReadOnlyList<IbkrPortfolioAccount>>> PortfolioAccountsAsync(CancellationToken ct)
    {
        var api = Api();
        if (api.IsFailure)
        {
            return Result<IReadOnlyList<IbkrPortfolioAccount>>.Failure(api.Error);
        }

        var key = api.Value.BaseAddress;
        if (PortfolioPrimed.TryGetValue(key, out var primed) && _clock.UtcNow - primed.At < PrimeLifetime)
        {
            return Result<IReadOnlyList<IbkrPortfolioAccount>>.Success(primed.Accounts);
        }

        var accounts = await api.Value.GetAsync<List<IbkrPortfolioAccount>>("portfolio/accounts", ct).ConfigureAwait(false);
        if (accounts.IsFailure)
        {
            return Result<IReadOnlyList<IbkrPortfolioAccount>>.Failure(accounts.Error);
        }

        PortfolioPrimed[key] = (_clock.UtcNow, accounts.Value);
        return Result<IReadOnlyList<IbkrPortfolioAccount>>.Success(accounts.Value);
    }

    /// <summary>The account's base currency as the portfolio accounts list reports it.</summary>
    public async Task<Result<string?>> BaseCurrencyAsync(string accountId, CancellationToken ct)
    {
        var accounts = await PortfolioAccountsAsync(ct).ConfigureAwait(false);
        if (accounts.IsFailure)
        {
            return Result<string?>.Failure(accounts.Error);
        }

        var match = accounts.Value.FirstOrDefault(a =>
            string.Equals(a.AccountId ?? a.Id, accountId, StringComparison.OrdinalIgnoreCase));

        return Result<string?>.Success(match?.Currency);
    }
}
