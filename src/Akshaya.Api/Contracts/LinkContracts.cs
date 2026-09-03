using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using FluentValidation;

namespace Akshaya.Api.Contracts;

/// <summary>Start linking a broker account.</summary>
public sealed record BeginLinkRequestDto
{
    /// <summary>Which connector. Opaque to the client too — it comes from GET /api/connectors.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>What the user wants to call this account.</summary>
    public string? Nickname { get; init; }

    /// <summary>
    /// The credential fields the manifest asked for, keyed by its declared field names.
    ///
    /// The API never inspects these and never persists them: they are forwarded to the
    /// connector, used for the handshake, and dropped. Only the resulting session is stored.
    /// </summary>
    public IReadOnlyDictionary<string, string> Credentials { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Where the OAuth callback should return to. Ignored by non-OAuth connectors.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>
    /// A previously saved login to start from, if any.
    ///
    /// Its decrypted fields form the BASE of the credential set and anything in
    /// <see cref="Credentials"/> is layered on top, so a user whose saved record covers the API
    /// key and client code sends only the password. The plaintext never leaves the server: the
    /// browser sends an id, not the secrets it stands for.
    /// </summary>
    public string? SavedCredentialId { get; init; }

    /// <summary>
    /// Which credential field keys to remember once this login SUCCEEDS, from the ones supplied
    /// in <see cref="Credentials"/>.
    ///
    /// Opt-in and per-field, and acted on only after the broker has accepted the login —
    /// saving credentials that have never worked produces a one-click button that one-click
    /// fails, which is worse than no button.
    /// </summary>
    public IReadOnlyList<string> RememberFields { get; init; } = [];
}

public sealed class BeginLinkRequestDtoValidator : AbstractValidator<BeginLinkRequestDto>
{
    public BeginLinkRequestDtoValidator()
    {
        RuleFor(r => r.ConnectorId).NotEmpty().WithMessage("A connector id is required.");
        RuleFor(r => r.Nickname).MaximumLength(80);
    }
}

/// <summary>Answer a challenge, or hand back an OAuth code, and continue the flow.</summary>
public sealed record ContinueLinkRequestDto
{
    /// <summary>The OTP, TOTP or OAuth authorisation code the previous step asked for.</summary>
    public required string Response { get; init; }

    /// <summary>Opaque state echoed from the previous step. The connector owns its meaning.</summary>
    public IReadOnlyDictionary<string, string> State { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class ContinueLinkRequestDtoValidator : AbstractValidator<ContinueLinkRequestDto>
{
    public ContinueLinkRequestDtoValidator() =>
        RuleFor(r => r.Response).NotEmpty().WithMessage("A challenge response is required.");
}

/// <summary>
/// One step of a broker login, as a DISCRIMINATED payload the generic link wizard renders from.
///
/// THIS TYPE IS WHY THE WIZARD IS GENERIC. The Angular component switches on
/// <see cref="Type"/> — <c>completed</c>, <c>redirect</c>, <c>challenge</c>, <c>gateway</c> —
/// and renders the four cases. It has no idea which broker it is talking to, and adding a
/// broker with a different login shape does not touch it. The moment the UI needs to know a
/// broker's name to log in to it, plug-and-play has stopped being true.
///
/// A flat record with nullable fields rather than a polymorphic hierarchy, because it has to
/// deserialise cleanly in TypeScript, where a discriminated union is exactly this: one literal
/// tag and the fields that tag implies.
/// </summary>
public sealed record AuthStepDto
{
    /// <summary>One of <c>completed</c>, <c>redirect</c>, <c>challenge</c>, <c>gateway</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The link this flow belongs to. Send it back on the continue call.</summary>
    public required string LinkId { get; init; }

    // ── type = "redirect" ──

    /// <summary>Send the user here.</summary>
    public string? Url { get; init; }

    /// <summary>Opaque state to echo back with the authorisation code.</summary>
    public string? State { get; init; }

    // ── type = "challenge" ──

    /// <summary>smsOtp, emailOtp, totp, securityQuestion or deviceApproval.</summary>
    public string? ChallengeKind { get; init; }

    /// <summary>What to ask the user, in the connector's own words.</summary>
    public string? Prompt { get; init; }

    /// <summary>e.g. "+91 ••••• 43210". Already masked by the connector; never unmask it.</summary>
    public string? MaskedDestination { get; init; }

    /// <summary>How long the challenge is valid, for the countdown.</summary>
    public int? ExpiresInSeconds { get; init; }

    // ── type = "gateway" ──

    /// <summary>Which supervised gateway must be running.</summary>
    public string? GatewayId { get; init; }

    /// <summary>What the user has to do about it, in plain language.</summary>
    public string? Instructions { get; init; }

    // ── type = "completed" ──

    /// <summary>The broker's account identifier for the linked account.</summary>
    public string? AccountId { get; init; }

    /// <summary>When the session dies. Shown so a trader is never surprised mid-session.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Projects the connector's <see cref="AuthStep"/>. The single mapping point.</summary>
    public static AuthStepDto From(AuthStep step, string linkId)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step switch
        {
            AuthStep.Completed completed => new AuthStepDto
            {
                Type = "completed",
                LinkId = linkId,
                AccountId = completed.Session.AccountId,
                ExpiresAt = completed.Session.ExpiresAt,
            },

            AuthStep.RedirectRequired redirect => new AuthStepDto
            {
                Type = "redirect",
                LinkId = linkId,
                Url = redirect.Url,
                State = redirect.State,
            },

            AuthStep.ChallengeRequired challenge => new AuthStepDto
            {
                Type = "challenge",
                LinkId = linkId,
                ChallengeKind = ToCamelCase(challenge.Kind.ToString()),
                Prompt = challenge.Prompt,
                MaskedDestination = challenge.MaskedDestination,
                ExpiresInSeconds = challenge.ExpiresIn is { } expiry ? (int)expiry.TotalSeconds : null,
            },

            AuthStep.GatewayRequired gateway => new AuthStepDto
            {
                Type = "gateway",
                LinkId = linkId,
                GatewayId = gateway.GatewayId,
                Instructions = gateway.Instructions,
            },

            // A new AuthStep case reaching an old client would render nothing at all, so it is
            // a programmer error here rather than a silent blank screen there.
            _ => throw new NotSupportedException(
                $"Auth step '{step.GetType().Name}' has no wire representation. Add one to AuthStepDto."),
        };
    }

    private static string ToCamelCase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>A linked broker account, as the account screen shows it.</summary>
public sealed record BrokerLinkDto
{
    public required string Id { get; init; }

    /// <summary>Opaque connector id. Used to look up the manifest for icon, name and capabilities.</summary>
    public required string ConnectorId { get; init; }

    public string? Nickname { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>False means the user must sign in again before they can trade on this link.</summary>
    public required bool HasSession { get; init; }

    /// <summary>
    /// When the session dies. Surfaced so the UI can warn BEFORE it happens — discovering an
    /// expired session by having an order rejected is the worst possible time to find out.
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastAuthenticatedAt { get; init; }

    public static BrokerLinkDto From(BrokerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new BrokerLinkDto
        {
            Id = link.Id,
            ConnectorId = link.ConnectorId,
            Nickname = link.Nickname,
            IsActive = link.IsActive,
            HasSession = link.Session is not null,
            SessionExpiresAt = link.Session?.ExpiresAt,
            CreatedAt = link.CreatedAt,
            LastAuthenticatedAt = link.LastAuthenticatedAt,
        };
    }
}
