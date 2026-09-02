using System.Diagnostics;
using System.Diagnostics.Metrics;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>
/// The single <see cref="ActivitySource"/> and <see cref="Meter"/> for the connector layer.
///
/// Static and shared: an OpenTelemetry pipeline subscribes by NAME, so a source created per
/// connector instance would silently emit nothing. The names are part of the operational
/// contract — changing them breaks dashboards and alerts, which is a production change, not a
/// refactor.
/// </summary>
public static class ConnectorTelemetry
{
    public const string SourceName = "Akshaya.Connectors";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>Every connector call, tagged by connector, facet, method and outcome.</summary>
    public static readonly Counter<long> Calls =
        Meter.CreateCounter<long>("akshaya.connector.calls", "call", "Connector calls by outcome.");

    /// <summary>
    /// Latency in milliseconds. A histogram, not a gauge: broker latency is long-tailed and the
    /// mean hides exactly the p99 that decides whether an order made the auction.
    /// </summary>
    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("akshaya.connector.duration", "ms", "Connector call duration.");

    /// <summary>
    /// Failures broken out by canonical code. Separate from the outcome tag on
    /// <see cref="Calls"/> so an alert can fire on <c>connector.session_expired</c>
    /// specifically without a high-cardinality join.
    /// </summary>
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("akshaya.connector.failures", "failure", "Connector failures by canonical code.");

    /// <summary>Stream events observed, tagged by connector. The staleness alert reads this.</summary>
    public static readonly Counter<long> StreamEvents =
        Meter.CreateCounter<long>("akshaya.connector.stream.events", "event", "Stream events received.");
}

/// <summary>
/// Emits a span and metrics for every connector call.
///
/// Placed OUTSIDE resilience so one span covers the whole logical operation including its
/// retries — which is what an operator wants to see; a span per attempt makes a retried call
/// look like several unrelated fast calls. The retry count is visible as a span tag rather
/// than as sibling spans.
///
/// Errors are recorded as span tags and a failed status, NOT as exceptions: a broker rejecting
/// an order is an expected outcome, and recording it as an exception would drown genuine
/// faults in noise.
/// </summary>
public sealed class TracingConnector(IBrokerConnector inner) : InterceptingConnector(inner)
{
    public override async Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        var connectorId = Manifest.Id;

        // Client kind: from the platform's perspective every connector call is an outbound
        // call to someone else's service.
        using var activity = ConnectorTelemetry.ActivitySource.StartActivity(
            operation.FullName,
            ActivityKind.Client);

        activity?.SetTag("akshaya.connector.id", connectorId);
        activity?.SetTag("akshaya.connector.vendor", Manifest.Vendor);
        activity?.SetTag("akshaya.connector.facet", operation.Facet.ToString());
        activity?.SetTag("akshaya.connector.method", operation.Method);
        activity?.SetTag("akshaya.connector.rate_limit_scope", operation.RateLimitScope);
        activity?.SetTag("akshaya.connector.order_affecting", operation.IsOrderAffecting);

        var start = Stopwatch.GetTimestamp();
        var outcome = "ok";
        string? errorCode = null;

        try
        {
            var result = await next(ct);

            if (result.IsFailure)
            {
                outcome = "error";
                errorCode = result.Error.Code;

                activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
                activity?.SetTag("akshaya.error.code", result.Error.Code);

                // The vendor's own code is the thing support pastes into a broker ticket.
                if (result.Error.VendorCode is { } vendorCode)
                {
                    activity?.SetTag("akshaya.error.vendor_code", vendorCode);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled call is neither a success nor a broker failure. Tagging it
            // separately keeps error-rate alerts from firing when users navigate away.
            outcome = "cancelled";
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // A connector that throws is a bug in that connector — it should have returned a
            // Result. Record it properly so it is visible, then let it propagate: swallowing
            // it here would hide the bug and hand the caller a meaningless failure.
            outcome = "exception";
            errorCode = ex.GetType().Name;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start);

            var tags = new TagList
            {
                { "connector", connectorId },
                { "facet", operation.Facet.ToString() },
                { "method", operation.Method },
                { "outcome", outcome },
            };

            ConnectorTelemetry.Calls.Add(1, tags);
            ConnectorTelemetry.Duration.Record(elapsed.TotalMilliseconds, tags);

            if (errorCode is not null)
            {
                ConnectorTelemetry.Failures.Add(
                    1,
                    new TagList
                    {
                        { "connector", connectorId },
                        { "facet", operation.Facet.ToString() },
                        { "method", operation.Method },
                        { "code", errorCode },
                    });
            }
        }
    }

    /// <summary>
    /// Counts stream events as they flow. Deliberately a counter and not a span: a span per
    /// tick would be tens of thousands of spans a second and would cost more than the feed.
    /// </summary>
    protected internal override IAsyncEnumerable<StreamEvent> InterceptEvents(
        IAsyncEnumerable<StreamEvent> events)
    {
        var connectorId = Manifest.Id;

        return events.Tap(streamEvent =>
        {
            ConnectorTelemetry.StreamEvents.Add(
                1,
                new TagList
                {
                    { "connector", connectorId },
                    { "kind", streamEvent.GetType().Name },
                });

            // A connection state change is rare and operationally important, so it gets an
            // event on the ambient activity where one exists.
            if (streamEvent is StreamEvent.ConnectionChanged changed)
            {
                Activity.Current?.AddEvent(new ActivityEvent(
                    "stream.connection_changed",
                    tags: new ActivityTagsCollection
                    {
                        { "state", changed.State.ToString() },
                        { "reason", changed.Reason },
                    }));
            }
        });
    }
}
