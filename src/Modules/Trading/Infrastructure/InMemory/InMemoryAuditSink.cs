using System.Collections.Concurrent;
using Akshaya.Modules.Trading.Ports;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Infrastructure.InMemory;

/// <summary>
/// DEVELOPMENT ONLY. Keeps a bounded ring of audit records in memory and mirrors each to the log.
///
/// PHASE 5 replaces this with an append-only, retained, queryable store. Until then this is NOT
/// an audit trail in any sense a regulator would accept: it is not durable, it is not tamper
/// evident, and it forgets the oldest record once the ring is full. It exists so that
/// development can see the same rows production will keep, and so the endpoints that read the
/// trail have something to read.
/// </summary>
public sealed class InMemoryAuditSink(ILogger<InMemoryAuditSink> logger) : IAuditSink
{
    /// <summary>Bounded so a long-running dev session cannot exhaust memory.</summary>
    public const int Capacity = 10_000;

    private readonly ConcurrentQueue<AuditRecord> _records = new();

    public IReadOnlyCollection<AuditRecord> Records => _records.ToArray();

    public ValueTask RecordAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records.Enqueue(record);
        while (_records.Count > Capacity && _records.TryDequeue(out _))
        {
            // Drop the oldest. A production sink must never do this.
        }

        logger.LogInformation(
            "AUDIT {Action} tenant={TenantId} actor={Actor} subject={Subject} ok={Succeeded} detail={Detail}",
            record.Action,
            record.TenantId,
            record.Actor,
            record.Subject,
            record.Succeeded,
            record.Detail);

        return ValueTask.CompletedTask;
    }
}
