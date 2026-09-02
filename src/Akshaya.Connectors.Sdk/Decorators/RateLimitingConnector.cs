using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>
/// Applies the manifest's declared rate limits to every call, per credential.
///
/// It sits INSIDE the resilience decorator (closer to the raw connector) so that a rate-limit
/// rejection is a retryable error the resilience layer can back off on, rather than something
/// the caller has to handle. Put the other way round, retries would be counted against the
/// bucket by a limiter that had already refused the first attempt — the backoff would never
/// converge.
///
/// Health checks bypass the limiter entirely: the UI polls health, and queueing a health probe
/// behind an order-rate bucket reports the connector as slow precisely when it is not.
/// </summary>
public sealed class RateLimitingConnector : InterceptingConnector
{
    private readonly ConnectorRateLimiter _limiter;
    private readonly string _credentialId;

    /// <param name="inner">The connector being wrapped.</param>
    /// <param name="limiter">Built from <c>inner.Manifest.RateLimits</c>.</param>
    /// <param name="credentialId">
    /// The broker credential — normally <c>BrokerSession.AccountId</c>. Brokers meter the
    /// logged-in account, so this and not a tenant id is what buckets are keyed by.
    /// </param>
    public RateLimitingConnector(IBrokerConnector inner, ConnectorRateLimiter limiter, string credentialId)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        _limiter = limiter;
        _credentialId = credentialId;
    }

    public override async Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (operation.Facet == ConnectorFacet.Health)
        {
            return await next(ct);
        }

        var permit = await _limiter.AcquireAsync(_credentialId, operation.RateLimitScope, permits: 1, ct);

        // The permit is consumed whether or not the call succeeds — the broker counted the
        // request, not the outcome, and so must we.
        return permit.IsSuccess ? await next(ct) : Result<T>.Failure(permit.Error);
    }
}
