using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>Retry policy for <see cref="ResilienceConnector"/>.</summary>
public sealed class ResilienceOptions
{
    /// <summary>
    /// Retries AFTER the first attempt. Three is chosen because broker outages are either
    /// momentary (one retry fixes it) or minutes long (no number of retries fixes it), and
    /// every extra attempt is latency the trader is waiting through.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>First backoff, doubled each attempt before jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Ceiling on any single backoff, jitter included.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Total time the whole retry sequence may consume. A hard budget matters more than the
    /// attempt count: three retries with a 5s Retry-After each is 15 seconds of a trader
    /// staring at a spinner.
    /// </summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Honour a broker-supplied Retry-After over the computed backoff. On by default: the
    /// broker knows when its own window reopens, and ignoring it is how a credential gets
    /// banned rather than throttled.
    /// </summary>
    public bool HonourRetryAfter { get; set; } = true;
}

/// <summary>
/// Retries transient failures with exponential backoff and jitter.
///
/// ═══════════════════════════════════════════════════════════════════════════════════════
///  READ THIS BEFORE CHANGING ANYTHING IN THIS FILE
///
///  A call is retried ONLY when BOTH hold:
///
///    1. <see cref="ConnectorOperation.IsIdempotentRead"/> is true, and
///    2. the error code is in <see cref="ConnectorErrorCodes.Retryable"/>.
///
///  PlaceAsync, ModifyAsync, CancelAsync, CancelAllAsync and PlaceBasketAsync are NOT
///  idempotent and are NEVER retried here. Not on a timeout. Not on a 502. Not once.
///
///  Why, concretely: a timeout means the response was lost, NOT that the request was.
///  The broker may have accepted the order and failed to tell us. Retrying then places a
///  SECOND order — a real position, real money, and a trader who is now double-long and
///  does not know it. No backoff strategy, idempotency header, or "it's probably fine"
///  changes this; the information needed to decide simply is not available at this layer.
///
///  The correct recovery for a timed-out write is RECONCILIATION, not retry: the caller
///  persisted <c>PlaceOrderRequest.ClientOrderId</c> before sending, so on a timeout it
///  re-reads the order book and matches on that id. Exactly one order exists or none does,
///  and either way the truth is discovered rather than guessed. That is what the
///  conformance suite's idempotency test proves, and it belongs in the Trading module's
///  saga, not here.
///
///  If you are here because a timed-out order "should have just retried" — it should not.
/// ═══════════════════════════════════════════════════════════════════════════════════════
///
/// Jitter is FULL jitter (uniform in [0, computed]) rather than a fixed fraction, because the
/// failure this guards against is correlated: a broker blip fails every in-flight request at
/// once, and identical backoffs re-synchronise them into a second thundering herd.
/// </summary>
public sealed class ResilienceConnector : InterceptingConnector
{
    private readonly ResilienceOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public ResilienceConnector(
        IBrokerConnector inner,
        ResilienceOptions options,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;

        // TimeProvider rather than IClock: this needs to DELAY, not just read the time, and
        // tests need to fast-forward those delays without waiting real seconds.
        _time = timeProvider ?? TimeProvider.System;
    }

    public override async Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        var result = await next(ct);

        // The gate. Note the order: the cheap capability check first, so a write path never
        // even evaluates the retry logic.
        if (result.IsSuccess || !IsRetryable(operation, result.Error))
        {
            return result;
        }

        var startedAt = _time.GetTimestamp();

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var delay = ComputeDelay(attempt, result.Error);
            var elapsed = _time.GetElapsedTime(startedAt);

            if (elapsed + delay > _options.TotalBudget)
            {
                _logger.LogWarning(
                    "{Operation}: giving up after {Attempts} attempt(s); the retry budget of {Budget} is spent. "
                    + "Last error {Code}.",
                    operation.FullName,
                    attempt,
                    _options.TotalBudget,
                    result.Error.Code);
                return result;
            }

            _logger.LogInformation(
                "{Operation}: retrying in {Delay} after {Code} (attempt {Attempt} of {Max}).",
                operation.FullName,
                delay,
                result.Error.Code,
                attempt,
                _options.MaxRetries);

            try
            {
                await Task.Delay(delay, _time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller gave up while we were waiting. Return the last error rather than
                // throwing: the caller already knows it cancelled.
                return result;
            }

            result = await next(ct);

            if (result.IsSuccess || !IsRetryable(operation, result.Error))
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>
    /// The whole policy, in one place, so it can be read and tested without a broker.
    /// Exposed for the conformance suite.
    /// </summary>
    public static bool IsRetryable(ConnectorOperation operation, Error error) =>
        operation.IsIdempotentRead && ConnectorErrorCodes.IsRetryable(error.Code);

    private TimeSpan ComputeDelay(int attempt, Error error)
    {
        if (_options.HonourRetryAfter
            && error.Context is { } context
            && context.TryGetValue(HttpConnectorClient.RetryAfterSecondsKey, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            // The broker told us when to come back. Still capped: a broker asking for an hour
            // gets the cap, and the caller gets a clean failure instead of a hang.
            var requested = TimeSpan.FromSeconds(seconds);
            return requested > _options.MaxDelay ? _options.MaxDelay : requested;
        }

        // Exponential: base * 2^(attempt-1), then FULL jitter over [0, that].
        var exponential = _options.BaseDelay * Math.Pow(2, attempt - 1);
        var capped = exponential > _options.MaxDelay ? _options.MaxDelay : exponential;

        return capped * Random.Shared.NextDouble();
    }
}
