namespace Akshaya.Modules.Trading.Ports;

/// <summary>
/// One recorded act by a human or by the platform on a human's behalf.
///
/// Deliberately flat and primitive-typed: audit rows outlive the code that wrote them and are
/// read years later by someone reconstructing an incident. Nothing here is a domain type that
/// a future refactor could rename out from under the historical rows.
/// </summary>
public sealed record AuditRecord
{
    public required DateTimeOffset At { get; init; }

    public required string TenantId { get; init; }

    /// <summary>Who did it. "system" for platform-initiated acts such as reconciliation.</summary>
    public required string Actor { get; init; }

    /// <summary>Dotted, stable verb: <c>order.place</c>, <c>killswitch.engage</c>, <c>risk.policy.update</c>.</summary>
    public required string Action { get; init; }

    /// <summary>What it was done to — an order id, a broker link id, a tenant id.</summary>
    public string? Subject { get; init; }

    public bool Succeeded { get; init; } = true;

    /// <summary>Free-text detail. Never credentials, never tokens.</summary>
    public string? Detail { get; init; }

    /// <summary>Small, flat key/value context for querying later.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Where audit records go.
///
/// Contract for implementers: this is called on the trading hot path.
///  * It must NOT throw — a broken audit sink must never fail a trade.
///  * It must NOT block — enqueue and drain on a background writer.
///  * It should be idempotent, because a retried handler may record the same act twice.
/// </summary>
public interface IAuditSink
{
    ValueTask RecordAsync(AuditRecord record, CancellationToken ct = default);
}
