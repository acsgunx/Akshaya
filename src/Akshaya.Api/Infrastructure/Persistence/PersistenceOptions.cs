namespace Akshaya.Api.Infrastructure.Persistence;

/// <summary>
/// Where the identity module's tables live.
///
/// Identity is the ONLY persisted store in the application — orders, positions, risk policies
/// and the kill switch are all in-memory and rebuilt from the broker on restart. That is what
/// makes this switch worth having: choosing <see cref="Sqlite"/> removes the last piece of
/// managed infrastructure from a deployment, and a managed Postgres instance is usually the
/// single largest line on the bill for an application this size.
/// </summary>
public enum PersistenceMode
{
    /// <summary>
    /// A SQLite file on local disk. The default.
    ///
    /// Durable across restarts wherever the file lives on a mounted volume, costs nothing, and
    /// — unlike EF's in-memory provider — it is a real relational provider, so the unique index
    /// on the normalised email that registration's concurrency safety depends on is actually
    /// enforced rather than silently ignored.
    /// </summary>
    Sqlite = 0,

    /// <summary>
    /// SQLite held entirely in process memory. Nothing is written to disk and every restart is
    /// a blank slate.
    ///
    /// For demos, throwaway preview environments and hosts with no writable filesystem. It is
    /// still SQLite — same provider, same constraint enforcement — so behaviour matches
    /// <see cref="Sqlite"/> exactly up to durability.
    /// </summary>
    InMemory = 1,

    /// <summary>
    /// PostgreSQL. The enterprise mode: the EF migrations in this assembly are Postgres DDL and
    /// are applied on startup, so schema changes are versioned and reviewable rather than
    /// inferred from the model.
    /// </summary>
    Postgres = 2,
}

/// <summary>
/// Binds the <c>Persistence</c> configuration section.
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Persistence";

    /// <summary>Which store backs the identity module. Defaults to <see cref="PersistenceMode.Sqlite"/>.</summary>
    public PersistenceMode Mode { get; set; } = PersistenceMode.Sqlite;

    /// <summary>
    /// Where the SQLite file lives in <see cref="PersistenceMode.Sqlite"/> mode. A relative path
    /// is resolved against the content root.
    ///
    /// On a container host this should point INTO the mounted volume — a path on the container's
    /// own layer works until the first redeploy and then quietly loses every account.
    /// </summary>
    public string SqlitePath { get; set; } = "App_Data/akshaya-identity.db";

    /// <summary>
    /// The first account to create when the database is empty. See <see cref="SeedUserOptions"/>.
    /// </summary>
    public SeedUserOptions SeedUser { get; set; } = new();
}

/// <summary>
/// An account created at startup when — and only when — the users table is empty.
///
/// This exists because the local-database modes are the ones that get wiped: a redeploy onto a
/// host without a volume, or any restart at all in <see cref="PersistenceMode.InMemory"/>, hands
/// you an application whose every endpoint returns 401 and whose only route out is a sign-up
/// form. Seeding makes a fresh deployment immediately usable.
/// </summary>
public sealed class SeedUserOptions
{
    /// <summary>
    /// Whether to seed at all.
    ///
    /// Null — the default — means "decide from the mode": on for the local-database modes, off
    /// for Postgres, where an unexpected account appearing in a shared database is a security
    /// event rather than a convenience. Set it explicitly to override either way.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>The address the seeded account signs in with.</summary>
    public string Email { get; set; } = "demo@akshaya.local";

    /// <summary>
    /// The seeded account's password.
    ///
    /// LEAVE THIS EMPTY unless you have somewhere safe to put it. Empty means a fresh random
    /// password is generated at startup and written to the log once, which is strictly better
    /// than a default credential that ships in a config file: nobody can look up what a given
    /// deployment's password is without access to its logs.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Display name for the seeded account.</summary>
    public string DisplayName { get; set; } = "Demo";
}
