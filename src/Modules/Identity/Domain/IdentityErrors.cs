using Akshaya.SharedKernel;

namespace Akshaya.Modules.Identity.Domain;

/// <summary>
/// Canonical codes for identity failures, in the same closed-vocabulary style as the connector
/// contract's own codes. The API's ProblemDetails mapper turns these into statuses; nothing
/// invents a status locally.
/// </summary>
public static class IdentityErrorCodes
{
    public const string EmailAlreadyRegistered = "identity.email_already_registered";
    public const string InvalidCredentials = "identity.invalid_credentials";
    public const string AccountDisabled = "identity.account_disabled";
    public const string NotAuthenticated = "identity.not_authenticated";
    public const string InvalidRequest = "identity.invalid_request";
    public const string CredentialNotFound = "identity.credential_not_found";

    /// <summary>
    /// A saved record exists but will not decrypt — almost always a master key that was
    /// rotated or lost rather than tampering. Distinct from "not found" because the remedy is
    /// different: the user must re-enter and re-save, and an operator needs to know the key is
    /// wrong before every user discovers it one at a time.
    /// </summary>
    public const string CredentialUnreadable = "identity.credential_unreadable";
}

/// <summary>Ready-made errors, so the wording of a failure lives in one place.</summary>
public static class IdentityErrors
{
    public static Error EmailAlreadyRegistered() => new(
        IdentityErrorCodes.EmailAlreadyRegistered,
        "An account already exists for that email address.");

    /// <summary>
    /// ONE message for "no such user" and "wrong password", always.
    ///
    /// Distinguishing them turns the sign-in form into an account-enumeration oracle: an
    /// attacker learns which addresses are registered here, and those addresses hold broker
    /// credentials. The service also hashes a dummy password when the user does not exist, so
    /// the two paths cost the same wall-clock time and the timing does not leak what the
    /// message refuses to.
    /// </summary>
    public static Error InvalidCredentials() => new(
        IdentityErrorCodes.InvalidCredentials,
        "That email address and password do not match an account.");

    public static Error AccountDisabled() => new(
        IdentityErrorCodes.AccountDisabled,
        "This account has been disabled. Contact your administrator.");

    public static Error NotAuthenticated() => new(
        IdentityErrorCodes.NotAuthenticated,
        "You need to sign in to do that.");

    public static Error InvalidRequest(string message) => new(
        IdentityErrorCodes.InvalidRequest,
        message);

    public static Error CredentialNotFound() => new(
        IdentityErrorCodes.CredentialNotFound,
        "No saved login was found for that broker.");

    public static Error CredentialUnreadable() => new(
        IdentityErrorCodes.CredentialUnreadable,
        "Your saved login could not be read and must be entered again.");
}
