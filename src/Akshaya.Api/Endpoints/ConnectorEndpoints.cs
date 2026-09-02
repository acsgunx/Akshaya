using Akshaya.Api.Infrastructure;
using Akshaya.Connectors.Abstractions;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// Read-only discovery of what brokers this host can talk to.
///
/// Every response here comes straight from a <see cref="ConnectorManifest"/> — the API adds no
/// opinions of its own about what a connector can do. This is what lets the link wizard, the
/// order ticket and the risk gate all agree with each other: they are reading the same document.
/// </summary>
public static class ConnectorEndpoints
{
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/connectors").WithTags("Connectors");

        // Sorted so the response is stable across calls — a UI that renders a broker list in a
        // different order every refresh reads as broken even though nothing is wrong.
        group.MapGet("/", (IConnectorFactory connectors) => Results.Ok(
            connectors.GetAllManifests()
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .ToArray()));

        group.MapGet("/{id}", (string id, IConnectorFactory connectors) =>
            connectors.GetManifest(id).ToHttp());

        // Health needs no session: it is asked before a user has ever linked this connector, so
        // a trader can see a broker is having an outage before wasting a login attempt on it.
        group.MapGet("/{id}/health", async (string id, IConnectorFactory connectors, CancellationToken ct) =>
        {
            var connectorResult = connectors.CreateUnauthenticated(id);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;
            var health = await connector.CheckHealthAsync(ct);
            return health.ToHttp();
        });

        return app;
    }
}
