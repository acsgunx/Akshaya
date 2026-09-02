using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// The Paper connector's authentication, which is the one part of this connector that is
/// allowed to be fake.
///
/// <b>THIS IS THE ONE PLACE EXPIRY IS FAKE, AND IT IS DELIBERATE.</b> Everywhere else the
/// Paper connector goes out of its way to behave exactly like a live broker — it slips, it
/// partially fills, it charges — because a rehearsal that is easier than the real thing is a
/// rehearsal for the wrong thing. Session expiry is the exception: there is no upstream to
/// expire against, no token anyone issued, and nothing a re-authentication could refresh.
/// Inventing an expiry here would produce a re-auth prompt for a session that cannot die,
/// which trains users to click through re-auth prompts. That is a worse outcome than the
/// asymmetry.
///
/// The consequence, stated plainly so nobody is surprised by it: a strategy that runs happily
/// on Paper for a week has NOT been exercised against session expiry, and session expiry is
/// one of the two or three things most likely to break it on a live broker. Test that path
/// against a real connector, or against the conformance suite's expired-session case, before
/// trusting it.
///
/// <b>A contract trap worth knowing.</b> <see cref="FarFuture"/> is not
/// <see cref="DateTimeOffset.MaxValue"/>. <c>SessionMonitor.ComputeEffectiveExpiry</c> reads
/// MaxValue as "nothing was declared" and answers with the ISSUE time — so a session claiming
/// to last forever would be treated as already dead. A very large but finite instant is the
/// way to say "does not expire" through this contract.
/// </summary>
public sealed class PaperAuth(IClock clock) : IConnectorAuth
{
    /// <summary>The connector id stamped into every session this facet issues.</summary>
    public const string ConnectorId = "paper";

    /// <summary>Credential key the manifest declares. It is a label, not a secret.</summary>
    public const string AccountLabelField = "account_label";

    /// <summary>
    /// The instant a paper session "expires". Far enough away to be never, finite enough to
    /// survive the SessionMonitor trap described in the class remarks.
    /// </summary>
    public static readonly DateTimeOffset FarFuture = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    /// <remarks>
    /// Always completes on the first step. There is no challenge, no redirect and no gateway:
    /// the credential field exists so the link wizard has something to render and so the user
    /// can name the account, not because anything is verified.
    /// </remarks>
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var label = context.Credentials.GetOrDefault(AccountLabelField);
        return Task.FromResult(Result<AuthStep>.Success(new AuthStep.Completed(CreateSession(label))));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing ever asks for a challenge, so reaching this means the caller is driving a flow
    /// that does not exist here. Completing anyway is the friendlier answer than failing, and
    /// it keeps a generic wizard that always calls Continue from breaking on this connector.
    /// </remarks>
    public Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var label = context.Credentials.GetOrDefault(AccountLabelField);
        return Task.FromResult(Result<AuthStep>.Success(new AuthStep.Completed(CreateSession(label))));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Declines, matching <c>auth.refreshSupported: false</c> in the manifest. A connector
    /// whose manifest says one thing and whose code does another is exactly what the
    /// conformance suite exists to catch, and a paper session has nothing to refresh anyway.
    /// </remarks>
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<BrokerSession>("session refresh (a paper session does not expire)");

    /// <inheritdoc />
    /// <remarks>Nothing to revoke upstream, and the desired end state already holds.</remarks>
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    /// <remarks>
    /// Success rather than NotSupported: the host calls this on a timer for every connector,
    /// and a paper session is permanently in the state a keepalive is trying to achieve.
    /// </remarks>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// Builds a session for a paper account. Public so a backtest harness can mint one without
    /// walking the auth flow — a backtest that had to "log in" would be pure ceremony.
    /// </summary>
    public BrokerSession CreateSession(string? accountLabel)
    {
        var account = string.IsNullOrWhiteSpace(accountLabel) ? "PAPER" : accountLabel.Trim();

        return new BrokerSession
        {
            ConnectorId = ConnectorId,
            AccountId = account,
            // Not a credential and not secret. Named so that anything which logs it makes the
            // absence of a real token obvious rather than looking like a leaked token.
            AccessToken = "paper-no-token",
            RefreshToken = null,
            ExpiresAt = FarFuture,
            Extras = SessionMonitor.WithIssuedAt(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["accountLabel"] = account },
                clock.UtcNow),
        };
    }
}
