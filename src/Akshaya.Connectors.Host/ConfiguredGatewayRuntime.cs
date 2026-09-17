using System.Diagnostics;
using System.Net.Sockets;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akshaya.Connectors.Host;

/// <summary>
/// A gateway runtime for daemons the OPERATOR runs. It starts nothing: it resolves where each
/// gateway listens from <see cref="ConnectorHostOptions.Gateways"/> and proves something is
/// listening there.
///
/// This is the smallest runtime that makes a gateway-hosted broker usable, and it is the right
/// shape for how these daemons are actually run today — a developer's own OpenD or Client Portal
/// Gateway on their machine, or a sidecar an operator stood up next to the API. A runtime that
/// launches containers per credential is still possible behind the same seam; see ADR 0008 for
/// why this one came first.
///
/// Two things it deliberately does NOT do:
///
///  * START OR STOP ANYTHING. The operator owns the daemon's lifetime, so <see cref="StopAsync"/>
///    succeeds without touching it. Unlinking an account must not kill a gateway the operator may
///    be using for something else.
///  * SPEAK THE VENDOR'S PROTOCOL. A probe is a TCP connect and nothing more. Whether the daemon is
///    logged in, unlocked or pointed at the right account is the connector's business — it knows
///    the protocol, and it reports that through its own health and auth facets. A host that
///    learned to parse one vendor's status page would be the first broker special case in it.
/// </summary>
public sealed class ConfiguredGatewayRuntime(
    IOptions<ConnectorHostOptions> options,
    ILogger<ConfiguredGatewayRuntime> logger) : IGatewayRuntime
{
    public Task<Result<GatewayEndpoint>> EnsureStartedAsync(
        GatewaySpec spec,
        string credentialId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var endpoint = options.Value.ResolveGatewayEndpoint(spec, credentialId);
        if (endpoint is null)
        {
            // Only reachable when neither the configuration nor the manifest names a port. Saying
            // exactly which key to set beats a probe against port zero.
            return Task.FromResult(Result<GatewayEndpoint>.Failure(ConnectorErrors.GatewayUnavailable(
                spec.Id,
                $"no address is configured for it. Set Connectors:Gateways:{spec.Id}:Host and "
                + $":Port.{SetupHint(spec)}")));
        }

        return Task.FromResult(Result<GatewayEndpoint>.Success(endpoint));
    }

    /// <summary>The operator started it and the operator stops it; see the class remarks.</summary>
    public Task<Result> StopAsync(GatewaySpec spec, string credentialId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public async Task<Result<GatewayProbe>> ProbeAsync(
        GatewaySpec spec,
        string credentialId,
        GatewayEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(endpoint);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, ct);

            return Result<GatewayProbe>.Success(
                new GatewayProbe(true, null, Stopwatch.GetElapsedTime(started)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The supervisor's probe timeout cancels this token, and it turns that into
            // "did not answer within" itself. Swallowing it here would lose the distinction.
            throw;
        }
        catch (SocketException ex)
        {
            logger.LogDebug(
                "Gateway {GatewayId} probe for {CredentialId} at {Endpoint} failed: {SocketError}.",
                spec.Id,
                credentialId,
                endpoint,
                ex.SocketErrorCode);

            // Refused, unreachable or unresolvable are all the same answer to a trader: nothing is
            // running there. The address is in the message because the fix is nearly always to
            // start the daemon or correct Connectors:Gateways.
            return Result<GatewayProbe>.Success(new GatewayProbe(
                false,
                $"nothing accepted a connection at {endpoint} ({ex.SocketErrorCode}). Start the gateway "
                + $"there, or point Connectors:Gateways:{spec.Id} at where it runs.{SetupHint(spec)}",
                Stopwatch.GetElapsedTime(started)));
        }
    }

    private static string SetupHint(GatewaySpec spec) =>
        spec.SetupInstructionsUrl is { } url ? $" Setup instructions: {url}" : string.Empty;
}
