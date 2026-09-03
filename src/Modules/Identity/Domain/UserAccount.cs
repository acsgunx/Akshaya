namespace Akshaya.Modules.Identity.Domain;

/// <summary>
/// A person who can sign in to the platform.
///
/// ONE TENANT PER USER, for now. Every store below the API is already tenant-scoped (risk
/// policies, the kill switch, broker links), and a personal trading account is exactly one
/// tenant with one member — so registration mints a tenant and puts the user in it. Sharing a
/// tenant between users (a desk, a family office) is a membership table this record is shaped
/// to accept later without moving anything: <see cref="TenantId"/> is already a separate
/// column rather than a computed property.
/// </summary>
public sealed record UserAccount
{
    public required string Id { get; init; }

    /// <summary>The tenant this user trades under. Every downstream store is scoped by it.</summary>
    public required string TenantId { get; init; }

    /// <summary>Display form, exactly as the user typed it. Never used for lookup.</summary>
    public required string Email { get; init; }

    /// <summary>
    /// Lookup form: trimmed and upper-cased invariantly.
    ///
    /// A separate column rather than an expression index because the uniqueness constraint and
    /// every lookup must agree on ONE normalisation, and a database that disagrees with the
    /// application about which two addresses are "the same" is how duplicate accounts happen.
    /// Upper-cased, not lower — lower-casing is locale-sensitive for a handful of scripts in a
    /// way upper-casing is not (the Turkish dotless i is the usual example).
    /// </summary>
    public required string NormalisedEmail { get; init; }

    /// <summary>Opaque, self-describing hash string. See <c>Ports.IPasswordHasher</c>.</summary>
    public required string PasswordHash { get; init; }

    public string? DisplayName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastSignedInAt { get; init; }

    /// <summary>
    /// False disables sign-in without deleting anything the user owns. Deleting an account has
    /// to cascade to saved credentials and broker links, so "off" and "gone" are separate
    /// states on purpose.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>Normalises an address for storage and lookup. The single definition of "same address".</summary>
    public static string Normalise(string email) => email.Trim().ToUpperInvariant();
}
