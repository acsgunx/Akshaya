using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// Turns a broker-link id into a live, decorated <see cref="IBrokerConnector"/>.
///
/// One place, because every handler needs the same four checks and getting any of them wrong
/// produces a confusing failure much later:
///
///  1. the link exists,
///  2. it belongs to the tenant asking (a link id is not a capability — cross-tenant access
///     must be refused here, not assumed away by the endpoint),
///  3. it has a session, and
///  4. the connector factory can activate it.
///
/// The returned connector is REQUEST-SCOPED and must be disposed by the caller. Caching one
/// across requests would hold a session that can expire underneath it, and would pin a plugin
/// load context that the host may want to unload.
/// </summary>
public sealed class BrokerLinkResolver(IBrokerLinkStore links, IConnectorFactory connectors)
{
    private readonly IBrokerLinkStore _links = links ?? throw new ArgumentNullException(nameof(links));
    private readonly IConnectorFactory _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));

    public async Task<Result<BrokerLink>> GetLinkAsync(string tenantId, string linkId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);

        var link = await _links.GetAsync(linkId, ct);

        // A link belonging to another tenant is reported as "not found", never as "forbidden":
        // distinguishing the two would confirm the id exists to someone who should not know.
        if (link is null || !string.Equals(link.TenantId, tenantId, StringComparison.Ordinal))
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"No broker link '{linkId}' exists for this account.");
        }

        if (!link.IsActive)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The broker link '{linkId}' is disabled.");
        }

        return link;
    }

    /// <summary>
    /// Activates the connector for a link. The caller owns the result and must
    /// <c>await using</c> it.
    /// </summary>
    public async Task<Result<IBrokerConnector>> ConnectAsync(BrokerLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.Session is not { } session)
        {
            // ReauthRequired rather than SessionExpired: there is nothing to refresh, the user
            // has to sign in again, and the UI shows a different prompt for each.
            return Result<IBrokerConnector>.Failure(ConnectorErrors.ReauthRequired(link.ConnectorId));
        }

        return await _connectors.CreateAsync(link.ConnectorId, session, ct);
    }

    /// <summary>Resolves and activates in one step, for callers that need both and own the disposal.</summary>
    public async Task<Result<IBrokerConnector>> ResolveAsync(
        string tenantId,
        string linkId,
        CancellationToken ct = default)
    {
        var link = await GetLinkAsync(tenantId, linkId, ct);
        return link.IsFailure
            ? Result<IBrokerConnector>.Failure(link.Error)
            : await ConnectAsync(link.Value, ct);
    }
}
