using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>Keys this connector writes into <see cref="BrokerSession.Extras"/>.</summary>
internal static class TigerSessionKeys
{
    public const string TigerId = "tigerId";

    public const string License = "license";

    public const string Environment = "environment";

    /// <summary>The two-factor token some licences require on every request.</summary>
    public const string Token = "token";
}

/// <summary>
/// What every Tiger request needs.
///
/// THE PRIVATE KEY IS THE SESSION'S TOKEN. Tiger has no login and issues nothing: a request is authenticated by
/// being signed, so the key is what a session carries, in <see cref="BrokerSession.AccessToken"/>, exactly where a
/// bearer token would live. Links are held in memory only, and the key is never sent anywhere — it signs, and the
/// signature travels.
/// </summary>
internal sealed record TigerCredentials(string TigerId, string Account, string PrivateKey, string? License, string? Token, bool Paper)
{
    /// <summary>Keeps the key out of logs and exception messages.</summary>
    public override string ToString() =>
        $"TigerCredentials {{ TigerId = {TigerId}, Account = {Account}, License = {License ?? "(default)"}, Paper = {Paper} }}";
}

/// <summary>A linked Tiger account, validated out of the session once.</summary>
internal sealed record TigerAccount(TigerCredentials Credentials)
{
    public string AccountId => Credentials.Account;

    public static Result<TigerAccount> FromSession(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Extras.GetValueOrDefault(TigerSessionKeys.TigerId) is not { Length: > 0 } tigerId)
        {
            return Result<TigerAccount>.Failure(TigerErrors.MalformedSession("no tiger id"));
        }

        if (string.IsNullOrWhiteSpace(session.AccountId))
        {
            return Result<TigerAccount>.Failure(TigerErrors.MalformedSession("no account"));
        }

        if (string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return Result<TigerAccount>.Failure(TigerErrors.MalformedSession("no signing key"));
        }

        var paper = string.Equals(
            session.Extras.GetValueOrDefault(TigerSessionKeys.Environment),
            TigerMaps.EnvironmentPaper,
            StringComparison.OrdinalIgnoreCase);

        return new TigerAccount(new TigerCredentials(
            tigerId,
            session.AccountId.Trim(),
            session.AccessToken,
            session.Extras.GetValueOrDefault(TigerSessionKeys.License),
            session.Extras.GetValueOrDefault(TigerSessionKeys.Token),
            paper));
    }

    public static Result<TigerAccount> Require(Func<Result<BrokerSession>> requireSession)
    {
        var session = requireSession();
        return session.IsFailure ? Result<TigerAccount>.Failure(session.Error) : FromSession(session.Value);
    }
}

/// <summary>
/// The two endpoints a link talks to: trading and quotes.
///
/// Which host each is depends on the account's licence — Singapore and New Zealand accounts trade through a gateway
/// of their own and quote from a third host — so the endpoint is derived per credential rather than fixed per
/// connector. An operator can override either in settings.
/// </summary>
internal sealed class TigerChannel(TigerOptions options, TigerErrorMapper errors, IClock clock, ILogger logger)
{
    public TigerErrorMapper Errors { get; } = errors;

    public TigerApi Trading(TigerCredentials credentials) =>
        TigerApi.Create(options.ServerUrl ?? TigerEndpoints.Trading(credentials.License, credentials.Paper), options, Errors, clock, logger);

    public TigerApi Quote(TigerCredentials credentials) =>
        TigerApi.Create(options.QuoteServerUrl ?? TigerEndpoints.Quote(credentials.License, credentials.Paper), options, Errors, clock, logger);
}
