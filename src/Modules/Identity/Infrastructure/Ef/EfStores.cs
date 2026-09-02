using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Ports;
using Microsoft.EntityFrameworkCore;

namespace Akshaya.Modules.Identity.Infrastructure.Ef;

/// <summary>EF Core implementation of <see cref="IUserAccountStore"/>.</summary>
public sealed class EfUserAccountStore(IdentityDbContext db) : IUserAccountStore
{
    public async Task<UserAccount?> FindByIdAsync(string userId, CancellationToken ct = default)
    {
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return row?.ToDomain();
    }

    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        // Normalise here, not at the call site: the unique index is on NormalisedEmail, so a
        // caller that normalised differently would look up rows the index would never let exist.
        var normalised = UserAccount.Normalise(email ?? string.Empty);

        var row = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalisedEmail == normalised, ct);

        return row?.ToDomain();
    }

    public async Task<bool> TryCreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        db.Users.Add(UserAccountRow.From(account));

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique index on NormalisedEmail rejected it. That is the expected outcome of
            // two simultaneous sign-ups for one address, not an error worth propagating — the
            // caller turns it into "an account already exists".
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task UpdateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == account.Id, ct);
        if (row is null)
        {
            return;
        }

        // Id, TenantId, CreatedAt and NormalisedEmail are deliberately not copied: changing an
        // account's tenant or the address its unique index is built on is an operation with
        // its own rules, not a side effect of recording a sign-in.
        row.Email = account.Email;
        row.PasswordHash = account.PasswordHash;
        row.DisplayName = account.DisplayName;
        row.LastSignedInAt = account.LastSignedInAt;
        row.IsActive = account.IsActive;

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>EF Core implementation of <see cref="ISavedCredentialStore"/>.</summary>
public sealed class EfSavedCredentialStore(IdentityDbContext db) : ISavedCredentialStore
{
    public async Task<SavedBrokerCredential?> GetAsync(string id, CancellationToken ct = default)
    {
        var row = await db.SavedCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<SavedBrokerCredential>> ListForUserAsync(
        string userId,
        CancellationToken ct = default)
    {
        var rows = await db.SavedCredentials.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);

        return Newest(rows);
    }

    public async Task<IReadOnlyList<SavedBrokerCredential>> ListForConnectorAsync(
        string userId,
        string connectorId,
        CancellationToken ct = default)
    {
        var rows = await db.SavedCredentials.AsNoTracking()
            .Where(c => c.UserId == userId && c.ConnectorId == connectorId)
            .ToListAsync(ct);

        return Newest(rows);
    }

    /// <summary>
    /// Orders newest-first IN MEMORY rather than in SQL.
    ///
    /// Not an oversight: SQLite cannot ORDER BY a DateTimeOffset, and pushing the sort into the
    /// database would tie this store to Postgres and silently break the module's own test
    /// suite. The filter — a single user, usually a single connector — has already reduced this
    /// to a handful of rows by the time it lands here, so there is nothing to gain from sorting
    /// server-side and a portability guarantee to lose.
    /// </summary>
    private static IReadOnlyList<SavedBrokerCredential> Newest(IEnumerable<SavedCredentialRow> rows) =>
        [.. rows.OrderByDescending(r => r.UpdatedAt).Select(r => r.ToDomain())];

    public async Task SaveAsync(SavedBrokerCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var existing = await db.SavedCredentials.FirstOrDefaultAsync(c => c.Id == credential.Id, ct);
        var incoming = SavedCredentialRow.From(credential);

        if (existing is null)
        {
            db.SavedCredentials.Add(incoming);
        }
        else
        {
            existing.Nickname = incoming.Nickname;
            existing.RememberedKeys = incoming.RememberedKeys;
            existing.KeyId = incoming.KeyId;
            existing.WrappedDataKey = incoming.WrappedDataKey;
            existing.Payload = incoming.Payload;
            existing.UpdatedAt = incoming.UpdatedAt;
            existing.LastUsedAt = incoming.LastUsedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await db.SavedCredentials.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }
}
