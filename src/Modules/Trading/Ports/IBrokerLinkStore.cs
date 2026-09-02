using Akshaya.Connectors.Abstractions;

namespace Akshaya.Modules.Trading.Ports;

/// <summary>
/// One user's link to one broker account.
///
/// The connector id is an OPAQUE STRING throughout the core. Nothing above the connector host
/// ever compares it to a literal; it is a key into the catalog and a label in the UI, and that
/// is the whole reason a new broker is a deployment rather than a release.
/// </summary>
public sealed record BrokerLink
{
    /// <summary>Stable id the API and the order aggregate refer to.</summary>
    public required string Id { get; init; }

    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    /// <summary>Which connector serves this link. Opaque — never switched on.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>What the user calls this account, when they have renamed it.</summary>
    public string? Nickname { get; init; }

    /// <summary>
    /// The decrypted session, or null while the link is mid-authentication or has expired.
    /// Null is normal and every caller must handle it — a link whose session has died is still
    /// a link, and the UI needs to show it as one so the user can re-authenticate.
    /// </summary>
    public BrokerSession? Session { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastAuthenticatedAt { get; init; }

    /// <summary>Whether this link should be polled, streamed and reconciled.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>Usable for trading right now: active, with a session.</summary>
    public bool IsUsable => IsActive && Session is not null;
}

/// <summary>
/// Storage for broker links and their sessions.
///
/// PHASE NOTE: link lifecycle (encryption at rest, key rotation, per-tenant KMS scoping) belongs
/// to the BrokerLink module. The trading core needs only to resolve a link id to a session, so
/// that is all this port exposes. Sessions are handed out decrypted and must never be logged.
/// </summary>
public interface IBrokerLinkStore
{
    Task<BrokerLink?> GetAsync(string linkId, CancellationToken ct = default);

    Task<IReadOnlyList<BrokerLink>> ListAsync(string tenantId, string? userId = null, CancellationToken ct = default);

    /// <summary>Every link the platform should be polling and reconciling, across all tenants.</summary>
    Task<IReadOnlyList<BrokerLink>> ListActiveAsync(CancellationToken ct = default);

    Task SaveAsync(BrokerLink link, CancellationToken ct = default);

    Task RemoveAsync(string linkId, CancellationToken ct = default);
}
