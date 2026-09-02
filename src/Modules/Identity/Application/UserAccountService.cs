using System.Globalization;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Identity.Application;

/// <summary>
/// Registration and sign-in.
///
/// The two rules that matter here are both about not leaking who has an account:
///
///  * Sign-in returns ONE error for "no such address" and "wrong password".
///  * Sign-in hashes a throwaway password when the address is unknown, so the miss costs the
///    same wall-clock time as the hit. Without it the endpoint answers "is this address
///    registered?" in a few hundred milliseconds regardless of what the message says — and
///    the addresses registered here are the ones holding broker credentials.
/// </summary>
public sealed class UserAccountService(
    IUserAccountStore store,
    IPasswordHasher hasher,
    IClock clock,
    ILogger<UserAccountService> logger)
{
    /// <summary>
    /// Long enough that a short password is not trivially reversible, short enough that the
    /// field is usable. Length is the only requirement worth enforcing server-side — composition
    /// rules (a digit, a symbol) measurably push people toward weaker, more predictable
    /// passwords, and NIST SP 800-63B has recommended against them since 2017.
    /// </summary>
    public const int MinimumPasswordLength = 10;

    /// <summary>A syntactically plausible address. Deliverability is a confirmation email's job, not a regex's.</summary>
    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at > 0
               && at < email.Length - 1
               && email.IndexOf('@', at + 1) < 0
               && !email.Any(char.IsWhiteSpace);
    }

    public async Task<Result<UserAccount>> RegisterAsync(
        string email,
        string password,
        string? displayName,
        CancellationToken ct = default)
    {
        var trimmed = (email ?? string.Empty).Trim();

        if (!LooksLikeEmail(trimmed))
        {
            return Result<UserAccount>.Failure(
                IdentityErrors.InvalidRequest("That does not look like an email address."));
        }

        if ((password ?? string.Empty).Length < MinimumPasswordLength)
        {
            return Result<UserAccount>.Failure(IdentityErrors.InvalidRequest(
                $"Choose a password of at least {MinimumPasswordLength.ToString(CultureInfo.InvariantCulture)} characters."));
        }

        var now = clock.UtcNow;
        var userId = Guid.NewGuid().ToString("N");

        var account = new UserAccount
        {
            Id = userId,
            // One tenant per user for now — see UserAccount's doc comment.
            TenantId = userId,
            Email = trimmed,
            NormalisedEmail = UserAccount.Normalise(trimmed),
            PasswordHash = hasher.Hash(password!),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedAt = now,
        };

        // Let the unique index arbitrate rather than reading first: two sign-ups with the same
        // address land milliseconds apart and a read-then-write loses that race silently.
        if (!await store.TryCreateAsync(account, ct))
        {
            return Result<UserAccount>.Failure(IdentityErrors.EmailAlreadyRegistered());
        }

        logger.LogInformation("Registered account {UserId}.", account.Id);
        return Result<UserAccount>.Success(account);
    }

    public async Task<Result<UserAccount>> SignInAsync(
        string email,
        string password,
        CancellationToken ct = default)
    {
        var account = await store.FindByEmailAsync((email ?? string.Empty).Trim(), ct);

        if (account is null)
        {
            // Burn the same work the real path would, so the timing does not answer the
            // question the error message deliberately refuses to.
            hasher.Verify(password ?? string.Empty, DecoyHash(hasher));
            return Result<UserAccount>.Failure(IdentityErrors.InvalidCredentials());
        }

        var verification = hasher.Verify(password ?? string.Empty, account.PasswordHash);
        if (!verification.IsValid)
        {
            logger.LogInformation("Failed sign-in for account {UserId}.", account.Id);
            return Result<UserAccount>.Failure(IdentityErrors.InvalidCredentials());
        }

        if (!account.IsActive)
        {
            // Checked AFTER the password, so a disabled account is not an enumeration oracle
            // for anyone who does not already know the password.
            return Result<UserAccount>.Failure(IdentityErrors.AccountDisabled());
        }

        var updated = account with { LastSignedInAt = clock.UtcNow };

        // The one moment the plaintext exists and the cost parameters can be upgraded.
        if (verification.NeedsRehash)
        {
            logger.LogInformation("Upgrading password hash cost for account {UserId}.", account.Id);
            updated = updated with { PasswordHash = hasher.Hash(password!) };
        }

        await store.UpdateAsync(updated, ct);
        return Result<UserAccount>.Success(updated);
    }

    public Task<UserAccount?> FindAsync(string userId, CancellationToken ct = default) =>
        store.FindByIdAsync(userId, ct);

    private static string? _decoyHash;

    /// <summary>
    /// A real hash, in the injected hasher's own format, of a fixed string nobody can sign in
    /// with. Verifying against it costs what verifying a real account costs, which is the
    /// entire point.
    ///
    /// Cached in a static rather than built per call so an unknown address costs one
    /// verification, not two. The race between two threads computing it first is benign —
    /// both produce an equally valid decoy and one harmlessly overwrites the other.
    /// </summary>
    private static string DecoyHash(IPasswordHasher hasher) =>
        _decoyHash ??= hasher.Hash("this password verifies nothing");
}
