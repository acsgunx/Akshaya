using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Identity.Domain;
using Akshaya.Modules.Identity.Infrastructure.Ef;
using Akshaya.Modules.Identity.Ports;
using Akshaya.Modules.Trading.Ports;
using Microsoft.EntityFrameworkCore;

namespace Akshaya.Api.Infrastructure.Persistence;

/// <summary>
/// Durable <see cref="IBrokerLinkStore"/> on top of the identity store.
///
/// The in-memory default loses every link on restart, which on a venue-midnight broker is
/// survivable but on a long-lived session means a deploy signs the user out of their broker.
/// Rows persist the whole link; the session inside is sealed with <see cref="ICredentialCipher"/>
/// — the same envelope encryption the saved-credential vault uses — so the database never holds
/// a broker token in the clear, and a key rotation drops old sessions gracefully rather than
/// corrupting them.
///
/// Degradation is deliberate: a row whose session can no longer be unsealed (the master key it
/// was sealed under is gone, or the record was altered) comes back as a sessionless link, NOT
/// as an error. The link survives, the UI offers re-authentication, and one bad secret cannot
/// take down a sweep of every other tenant's links.
///
/// Singleton that opens a scope per call to borrow the scoped <see cref="IdentityDbContext"/>:
/// the port is consumed by hosted services that outlive any request scope, so holding a scoped
/// context would either fail validation or pin a change tracker for the process lifetime.
/// </summary>
internal sealed class EfBrokerLinkStore(
    IServiceScopeFactory scopeFactory,
    ICredentialCipher cipher,
    ILogger<EfBrokerLinkStore> logger) : IBrokerLinkStore
{
    public async Task<BrokerLink?> GetAsync(string linkId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var row = await db.BrokerLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<BrokerLink>> ListAsync(
        string tenantId,
        string? userId = null,
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var rows = await db.BrokerLinks.AsNoTracking()
            .Where(l => l.TenantId == tenantId && (userId == null || l.UserId == userId))
            .ToListAsync(ct);

        return [.. rows.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<BrokerLink>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var rows = await db.BrokerLinks.AsNoTracking()
            .Where(l => l.IsActive && l.SessionPayload != null)
            .ToListAsync(ct);

        // A row whose sealed session no longer opens is filtered here, after mapping: the
        // port's contract is "links the platform can poll", and one it cannot decrypt is not one.
        return [.. rows.Select(ToDomain).Where(l => l.Session is not null)];
    }

    public async Task SaveAsync(BrokerLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var existing = await db.BrokerLinks.FirstOrDefaultAsync(l => l.Id == link.Id, ct);

        if (existing is null)
        {
            db.BrokerLinks.Add(ToRow(link));
        }
        else
        {
            existing.Nickname = link.Nickname;
            existing.LastAuthenticatedAt = link.LastAuthenticatedAt;
            existing.IsActive = link.IsActive;
            SealSession(link, existing);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string linkId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.BrokerLinks.Where(l => l.Id == linkId).ExecuteDeleteAsync(ct);
    }

    private BrokerLinkRow ToRow(BrokerLink link)
    {
        var row = new BrokerLinkRow
        {
            Id = link.Id,
            TenantId = link.TenantId,
            UserId = link.UserId,
            ConnectorId = link.ConnectorId,
            Nickname = link.Nickname,
            CreatedAt = link.CreatedAt,
            LastAuthenticatedAt = link.LastAuthenticatedAt,
            IsActive = link.IsActive,
        };
        SealSession(link, row);
        return row;
    }

    private void SealSession(BrokerLink link, BrokerLinkRow row)
    {
        if (link.Session is null)
        {
            // A save can deliberately strip a session (re-auth restart, session revoked); the
            // three columns must clear together so a later read never sees half a secret.
            row.SessionKeyId = null;
            row.SessionWrappedDataKey = null;
            row.SessionPayload = null;
            return;
        }

        var sealedSecret = cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(link.Session));
        row.SessionKeyId = sealedSecret.KeyId;
        row.SessionWrappedDataKey = sealedSecret.WrappedDataKey;
        row.SessionPayload = sealedSecret.Payload;
    }

    private BrokerLink ToDomain(BrokerLinkRow row)
    {
        return new BrokerLink
        {
            Id = row.Id,
            TenantId = row.TenantId,
            UserId = row.UserId,
            ConnectorId = row.ConnectorId,
            Nickname = row.Nickname,
            Session = UnsealSession(row),
            CreatedAt = row.CreatedAt,
            LastAuthenticatedAt = row.LastAuthenticatedAt,
            IsActive = row.IsActive,
        };
    }

    private BrokerSession? UnsealSession(BrokerLinkRow row)
    {
        if (row.SessionPayload is not { Length: > 0 } payload
            || row.SessionWrappedDataKey is not { } wrapped
            || row.SessionKeyId is not { Length: > 0 } keyId)
        {
            return null;
        }

        if (!cipher.TryUnseal(new SealedSecret(keyId, wrapped, payload), out var plaintext))
        {
            logger.LogWarning(
                "Broker link {LinkId} carries a session that cannot be unsealed "
                + "(master key '{KeyId}' unavailable or record altered); treating it as sessionless.",
                row.Id,
                keyId);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BrokerSession>(plaintext);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Broker link {LinkId} has an unsealed session that does not deserialise; "
                + "treating it as sessionless.",
                row.Id);
            return null;
        }
    }
}
