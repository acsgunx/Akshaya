using Akshaya.Modules.Identity.Domain;

namespace Akshaya.Modules.Identity.Ports;

/// <summary>Storage for user accounts.</summary>
public interface IUserAccountStore
{
    Task<UserAccount?> FindByIdAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Looks up by NORMALISED email. Callers pass a raw address; implementations normalise via
    /// <see cref="UserAccount.Normalise"/> so no caller can accidentally use a different rule
    /// from the one the unique index enforces.
    /// </summary>
    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new account. Returns false when the normalised email is already taken.
    ///
    /// A bool rather than a throw because the race is expected, not exceptional: two sign-ups
    /// with the same address land within milliseconds of each other and the loser must get the
    /// ordinary "already registered" response. Implementations MUST rely on the database's
    /// unique constraint rather than a read-then-write, which does not survive concurrency.
    /// </summary>
    Task<bool> TryCreateAsync(UserAccount account, CancellationToken ct = default);

    Task UpdateAsync(UserAccount account, CancellationToken ct = default);
}

/// <summary>Storage for the encrypted per-broker credential records.</summary>
public interface ISavedCredentialStore
{
    Task<SavedBrokerCredential?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Everything this user has saved, newest first.</summary>
    Task<IReadOnlyList<SavedBrokerCredential>> ListForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Saved logins for one connector. A list, not a single record: a user may legitimately
    /// hold two accounts with the same broker, which is exactly why the nickname exists.
    /// </summary>
    Task<IReadOnlyList<SavedBrokerCredential>> ListForConnectorAsync(
        string userId,
        string connectorId,
        CancellationToken ct = default);

    Task SaveAsync(SavedBrokerCredential credential, CancellationToken ct = default);

    Task RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// Password hashing, behind an interface so the algorithm can be changed without touching the
/// sign-in path, and so tests can substitute a fast hash instead of paying for the real one.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Returns a self-describing hash string that carries its own parameters and salt.</summary>
    string Hash(string password);

    /// <summary>
    /// Verifies, and says whether the stored hash used weaker parameters than the current
    /// policy. The caller re-hashes on a successful sign-in when it did — which is the only
    /// moment the plaintext is available to upgrade with.
    /// </summary>
    PasswordVerification Verify(string password, string hash);
}

/// <param name="IsValid">Whether the password matched.</param>
/// <param name="NeedsRehash">True when the hash is valid but below current cost parameters.</param>
public readonly record struct PasswordVerification(bool IsValid, bool NeedsRehash);

/// <summary>
/// Envelope encryption for credential payloads.
///
/// An interface, not a static helper, because the production implementation of this is
/// eventually a KMS/HSM call and the in-process AES one is the fallback — the vault above it
/// must not care which it got.
/// </summary>
public interface ICredentialCipher
{
    SealedSecret Seal(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Returns false rather than throwing when the payload will not authenticate. A record
    /// sealed under a rotated-away key is an operational fact the vault turns into "re-enter
    /// this", not an exception that takes down a page listing twenty other working ones.
    /// </summary>
    bool TryUnseal(SealedSecret sealedSecret, out byte[] plaintext);
}
