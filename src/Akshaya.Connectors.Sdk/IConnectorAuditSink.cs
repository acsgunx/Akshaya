using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// One order-affecting call, recorded.
///
/// Deliberately flat and primitive-typed: audit rows outlive the code that wrote them, get
/// shipped to a warehouse, and are read years later by someone reconstructing what happened.
/// Nothing here references a domain type that might be refactored.
///
/// What is NOT here, on purpose: credentials, tokens, and the order's economics. The audit
/// trail answers "who asked this broker to do what, when, and what did it say" — the order
/// itself is already persisted by the Trading module with far more fidelity, and duplicating
/// prices here would create a second source of truth that can disagree with the first.
/// </summary>
public sealed record ConnectorAuditEvent
{
    public required string ConnectorId { get; init; }

    /// <summary>Broker account the call was made against. The audit unit of account.</summary>
    public required string CredentialId { get; init; }

    /// <summary>e.g. <c>Orders.PlaceAsync</c>. See <see cref="Decorators.ConnectorOperation.FullName"/>.</summary>
    public required string Operation { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>Canonical code on failure; null on success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The broker's own code, so support can quote it back to the broker.</summary>
    public string? VendorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The caller-generated id from <c>PlaceOrderRequest.ClientOrderId</c> where the operation
    /// had one. This is the join key between the audit trail and the order store, and the key
    /// that makes timeout reconciliation provable after the fact.
    /// </summary>
    public Guid? ClientOrderId { get; init; }

    /// <summary>The broker's id, once known.</summary>
    public string? BrokerOrderId { get; init; }

    /// <summary>Trace id of the enclosing activity, to join audit rows to spans.</summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// Where audit events go. Implemented by the BrokerLink module against durable storage; the
/// SDK ships only the logging fallback below.
///
/// Contract for implementers: this is called on the trading hot path, inside the request that
/// is placing an order.
///
///  * It must NOT throw. <see cref="Decorators.AuditingConnector"/> catches anyway, because a broken
///    audit sink must never fail a trade, but a sink that throws per order is a silent
///    outage of the audit trail.
///  * It must NOT block. Enqueue to a channel and drain on a background writer; a synchronous
///    database insert adds its latency to every order.
///  * It should be idempotent on retry. The host may record the same operation twice if a
///    higher decorator retried it.
/// </summary>
public interface IConnectorAuditSink
{
    ValueTask RecordAsync(ConnectorAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// Writes audit events to <see cref="ILogger"/> at Information level.
///
/// The default so that a misconfigured host still leaves a trail somewhere, rather than
/// silently discarding one. It is NOT sufficient for a jurisdiction that requires a retained,
/// queryable audit trail — logs get sampled and rotated. Register a durable sink in
/// production.
/// </summary>
public sealed class LoggingConnectorAuditSink(ILogger<LoggingConnectorAuditSink> logger) : IConnectorAuditSink
{
    public ValueTask RecordAsync(ConnectorAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        logger.LogInformation(
            "AUDIT {ConnectorId}/{CredentialId} {Operation} {Outcome} in {DurationMs}ms "
            + "(clientOrderId={ClientOrderId}, brokerOrderId={BrokerOrderId}, error={ErrorCode}/{VendorCode})",
            auditEvent.ConnectorId,
            auditEvent.CredentialId,
            auditEvent.Operation,
            auditEvent.Succeeded ? "OK" : "FAILED",
            auditEvent.Duration.TotalMilliseconds,
            auditEvent.ClientOrderId,
            auditEvent.BrokerOrderId,
            auditEvent.ErrorCode,
            auditEvent.VendorCode);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Discards everything. For tests and for the unauthenticated connector used during login,
/// where there is no credential to attribute a call to.
/// </summary>
public sealed class NullConnectorAuditSink : IConnectorAuditSink
{
    public static readonly NullConnectorAuditSink Instance = new();

    public ValueTask RecordAsync(ConnectorAuditEvent auditEvent, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
