using Akshaya.Modules.Identity.Application;
using Akshaya.Modules.Identity.Domain;
using FluentValidation;

namespace Akshaya.Api.Contracts;

/// <summary>Sign-up payload.</summary>
public sealed record RegisterRequestDto
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>Sign-in payload.</summary>
public sealed record SignInRequestDto
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

/// <summary>
/// The signed-in user, as the browser sees them.
///
/// No password hash, no tenant secrets — the tenant id IS included because the UI labels
/// tenant-scoped things (the kill switch, risk policy) with it, and it is not a credential.
/// </summary>
public sealed record UserProfileDto
{
    public required string Id { get; init; }

    public required string TenantId { get; init; }

    public required string Email { get; init; }

    public string? DisplayName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastSignedInAt { get; init; }

    public static UserProfileDto From(UserAccount account) => new()
    {
        Id = account.Id,
        TenantId = account.TenantId,
        Email = account.Email,
        DisplayName = account.DisplayName,
        CreatedAt = account.CreatedAt,
        LastSignedInAt = account.LastSignedInAt,
    };
}

/// <summary>
/// A saved broker login, as the browser sees it.
///
/// <see cref="RememberedKeys"/> is the ONLY thing said about the contents: which manifest
/// field keys are stored. The values are never serialised into this or any other response —
/// see <c>BrokerCredentialVault</c>'s access rule for why there is no "reveal" endpoint.
/// </summary>
public sealed record SavedCredentialDto
{
    public required string Id { get; init; }

    public required string ConnectorId { get; init; }

    public string? Nickname { get; init; }

    public required IReadOnlyList<string> RememberedKeys { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public static SavedCredentialDto From(SavedCredentialSummary summary) => new()
    {
        Id = summary.Id,
        ConnectorId = summary.ConnectorId,
        Nickname = summary.Nickname,
        RememberedKeys = summary.RememberedKeys,
        UpdatedAt = summary.UpdatedAt,
        LastUsedAt = summary.LastUsedAt,
    };
}

public sealed class RegisterRequestDtoValidator : AbstractValidator<RegisterRequestDto>
{
    public RegisterRequestDtoValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(320);

        // Length only, deliberately. See UserAccountService.MinimumPasswordLength for why
        // composition rules are not enforced.
        RuleFor(r => r.Password)
            .NotEmpty()
            .MinimumLength(UserAccountService.MinimumPasswordLength)
            .WithMessage($"Choose a password of at least {UserAccountService.MinimumPasswordLength} characters.")
            .MaximumLength(256);

        RuleFor(r => r.DisplayName).MaximumLength(120);
    }
}

public sealed class SignInRequestDtoValidator : AbstractValidator<SignInRequestDto>
{
    public SignInRequestDtoValidator()
    {
        // No minimum length on sign-in: the policy may have changed since the account was
        // created, and rejecting a valid old password locally would lock the user out of the
        // one flow that could upgrade it.
        RuleFor(r => r.Email).NotEmpty().MaximumLength(320);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(256);
    }
}
