using System.Diagnostics;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>
/// Records every order-affecting call to an <see cref="IConnectorAuditSink"/>.
///
/// It is the OUTERMOST decorator, and that placement is the whole design:
///
///  * One audit row per LOGICAL operation. Sitting outside resilience means a retried read
///    is one row, not four, and the row's duration is the wall-clock time the caller actually
///    waited — which is what a complaint about a slow order is really about.
///  * Rate-limit rejections are recorded. A rejected order never reached the broker, but
///    "we refused to send your order" is exactly the kind of thing that must be provable
///    afterwards. Sitting inside the limiter would lose those rows entirely.
///
/// Which calls are recorded is decided by <see cref="ConnectorOperation.IsOrderAffecting"/>,
/// declared once in <see cref="ConnectorOperations"/> — never re-derived from a method name
/// here, so a new order method cannot slip through un-audited.
///
/// Reads are NOT audited. A portfolio refresh every five seconds per user would swamp the
/// trail and bury the twenty rows a day that matter.
/// </summary>
public sealed class AuditingConnector : InterceptingConnector
{
    private readonly IConnectorAuditSink _sink;
    private readonly string _credentialId;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="inner">The connector being wrapped.</param>
    /// <param name="sink">Where rows go. See <see cref="IConnectorAuditSink"/> for its contract.</param>
    /// <param name="credentialId">
    /// Broker account the calls are attributed to. For the unauthenticated connector used
    /// during login there is none, so pass a stable placeholder rather than an empty string —
    /// an audit row with no subject is worse than no row.
    /// </param>
    /// <param name="logger">Used only to report a failing sink.</param>
    /// <param name="timeProvider">Injectable for deterministic tests.</param>
    public AuditingConnector(
        IBrokerConnector inner,
        IConnectorAuditSink sink,
        string credentialId,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        ArgumentNullException.ThrowIfNull(logger);

        _sink = sink;
        _credentialId = credentialId;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public override async Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (!operation.IsOrderAffecting)
        {
            return await next(ct);
        }

        var startedAt = _time.GetUtcNow();
        var timestamp = Stopwatch.GetTimestamp();

        Result<T> result;
        try
        {
            result = await next(ct);
        }
        catch (Exception ex)
        {
            // A throwing connector still produced an attempt at the broker. Record it before
            // letting the exception through, or the one call most likely to have left an
            // orphaned order at the broker is the one call with no audit row.
            await RecordAsync(
                operation,
                subject,
                startedAt,
                Stopwatch.GetElapsedTime(timestamp),
                succeeded: false,
                errorCode: ex.GetType().Name,
                vendorCode: null,
                errorMessage: ex.Message,
                brokerOrderId: null,
                ct);
            throw;
        }

        var duration = Stopwatch.GetElapsedTime(timestamp);

        await RecordAsync(
            operation,
            subject,
            startedAt,
            duration,
            result.IsSuccess,
            result.IsSuccess ? null : result.Error.Code,
            result.IsSuccess ? null : result.Error.VendorCode,
            result.IsSuccess ? null : result.Error.Message,
            // Pull the broker's id out of the ack where the call produced one; it is the other
            // half of the reconciliation join and is not known before the call.
            BrokerOrderIdOf(result, subject),
            ct);

        return result;
    }

    private static string? BrokerOrderIdOf<T>(Result<T> result, ConnectorCallSubject subject)
    {
        if (!result.IsSuccess)
        {
            return subject.BrokerOrderId;
        }

        return result.Value switch
        {
            OrderAck ack => ack.BrokerOrderId,

            // A basket produces several. Joining them keeps the row single-valued rather than
            // inventing a row shape only baskets use.
            IReadOnlyList<OrderAck> acks => string.Join(',', acks.Select(a => a.BrokerOrderId)),

            _ => subject.BrokerOrderId,
        };
    }

    private async Task RecordAsync(
        ConnectorOperation operation,
        ConnectorCallSubject subject,
        DateTimeOffset startedAt,
        TimeSpan duration,
        bool succeeded,
        string? errorCode,
        string? vendorCode,
        string? errorMessage,
        string? brokerOrderId,
        CancellationToken ct)
    {
        var auditEvent = new ConnectorAuditEvent
        {
            ConnectorId = Manifest.Id,
            CredentialId = _credentialId,
            Operation = operation.FullName,
            StartedAt = startedAt,
            Duration = duration,
            Succeeded = succeeded,
            ErrorCode = errorCode,
            VendorCode = vendorCode,
            ErrorMessage = errorMessage,
            ClientOrderId = subject.ClientOrderId,
            BrokerOrderId = brokerOrderId,
            TraceId = Activity.Current?.TraceId.ToString(),
        };

        try
        {
            // CancellationToken.None, deliberately: if the caller cancels mid-order the audit
            // row is MORE important, not less, because a cancelled request can still have
            // reached the broker. A sink is required not to block, so this cannot hang.
            await _sink.RecordAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A broken audit sink must never fail a trade. Log loudly — a silent audit outage
            // is a compliance problem that surfaces months later — and carry on.
            _logger.LogError(
                ex,
                "Audit sink failed for {Operation} on {ConnectorId}/{CredentialId}. "
                + "The call itself was unaffected; the audit trail has a hole.",
                operation.FullName,
                Manifest.Id,
                _credentialId);
        }

        // The parameter is kept for symmetry with the rest of the SDK and to make the
        // deliberate use of CancellationToken.None above visible at the call site.
        _ = ct;
    }
}
