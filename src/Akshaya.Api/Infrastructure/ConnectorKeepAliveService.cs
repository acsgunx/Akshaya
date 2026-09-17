using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Api.Infrastructure;

/// <summary>
/// Calls <see cref="IConnectorAuth.KeepAliveAsync"/> for every linked session whose connector
/// declares <c>auth.keepAliveInterval</c>, on that interval.
///
/// The contract has always said the host does this. Until this service existed nothing did, which
/// was harmless while no connector declared an interval and stops being harmless the moment one
/// talks to a gateway that signs a session out after a few idle minutes: the trader's first sign
/// is an order rejected for an expired session they linked an hour ago. See ADR 0008.
///
/// It reads the manifest and nothing else, so it names no broker, and a connector that declares no
/// interval is never touched.
///
/// Two choices worth knowing:
///
///  * The last-sent time is stamped BEFORE the call. A gateway that hangs or refuses is then tried
///    again on its own interval rather than on every sweep, which is what keeps a dead daemon from
///    turning this loop into a tight retry against it.
///  * Links are kept alive one after another, not in parallel. The number of links with a
///    keepalive interval is the number of gateway sessions, which is small, and each call is
///    bounded by the supervisor's probe timeout and the connector's own request timeout.
/// </summary>
internal sealed class ConnectorKeepAliveService(
    IBrokerLinkStore links,
    IConnectorFactory connectors,
    IClock clock,
    ILogger<ConnectorKeepAliveService> logger) : BackgroundService
{
    /// <summary>
    /// How often links are examined. Shorter than any keepalive interval worth declaring, so a
    /// keepalive goes out within this long of falling due; each link's own interval decides whether
    /// one does.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>When each link last had a keepalive sent. Only the sweep loop touches it.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; nothing to report.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        IReadOnlyList<BrokerLink> active;
        try
        {
            active = await links.ListActiveAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unhandled exception in a BackgroundService stops the host. A link store that
            // failed one read must cost one sweep, not the API.
            logger.LogWarning(ex, "Keepalive sweep could not list broker links; skipping this sweep.");
            return;
        }

        var now = clock.UtcNow;
        var due = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in active)
        {
            if (link.Session is not { } session)
            {
                continue;
            }

            var manifest = connectors.GetManifest(link.ConnectorId);
            if (manifest.IsFailure || manifest.Value.Auth.KeepAliveInterval is not { } interval)
            {
                continue;
            }

            due.Add(link.Id);

            if (_lastSent.TryGetValue(link.Id, out var last) && now - last < interval)
            {
                continue;
            }

            _lastSent[link.Id] = now;
            await SendAsync(link, session, ct);
        }

        // Forget links that were unlinked, deactivated or lost their session, so the map cannot grow
        // for the lifetime of the process.
        foreach (var gone in _lastSent.Keys.Where(id => !due.Contains(id)).ToList())
        {
            _lastSent.Remove(gone);
        }
    }

    private async Task SendAsync(BrokerLink link, BrokerSession session, CancellationToken ct)
    {
        try
        {
            var created = await connectors.CreateAsync(link.ConnectorId, session, ct);
            if (created.IsFailure)
            {
                logger.LogWarning(
                    "Keepalive for link {LinkId} ({ConnectorId}) could not obtain a connector: {Error}",
                    link.Id,
                    link.ConnectorId,
                    created.Error.ToString());
                return;
            }

            await using var connector = created.Value;
            var result = await connector.Auth.KeepAliveAsync(session, ct);

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "Keepalive for link {LinkId} ({ConnectorId}) failed: {Error}",
                    link.Id,
                    link.ConnectorId,
                    result.Error.ToString());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Keepalive for link {LinkId} ({ConnectorId}) threw.",
                link.Id,
                link.ConnectorId);
        }
    }
}
