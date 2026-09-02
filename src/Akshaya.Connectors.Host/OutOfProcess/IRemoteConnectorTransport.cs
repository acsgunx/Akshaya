using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Host.OutOfProcess;

/// <summary>
/// The wire underneath an out-of-process connector.
///
/// <see cref="GrpcConnectorProxy"/> is written against this rather than directly against a
/// generated gRPC client for two reasons:
///
/// 1. It keeps the proxy's logic — request shaping, error normalisation, session handling —
///    testable without standing up a server.
/// 2. gRPC is the intended transport, not the only conceivable one. A connector reachable over a
///    unix socket or a message queue satisfies the same contract, and nothing above this
///    interface should have to care.
///
/// The canonical contract types (<see cref="PlaceOrderRequest"/>, <see cref="BrokerOrder"/> and
/// so on) are the payloads. They are plain records with no transport concerns, which is exactly
/// what makes them serialisable to protobuf or JSON without a parallel DTO layer.
/// </summary>
public interface IRemoteConnectorTransport : IAsyncDisposable
{
    /// <summary>Address of the remote connector process.</summary>
    Uri Address { get; }

    /// <summary>
    /// Unary call. <paramref name="method"/> names the RPC in broker_connector.proto — for
    /// example "PlaceOrder". Failures come back as <see cref="Result{T}"/> carrying a canonical
    /// <see cref="ConnectorErrorCodes"/> value, never as exceptions: a remote connector reporting
    /// "insufficient funds" is an outcome, and it must look identical to an in-process connector
    /// reporting the same thing.
    /// </summary>
    Task<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        BrokerSession? session,
        CancellationToken ct = default);

    /// <summary>
    /// Server-streaming call for the tick and order-update feed. The proxy forwards whatever the
    /// remote process yields; back-pressure is handled by the fan-out layer above, not here.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        string method,
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        BrokerSession? session,
        CancellationToken ct = default);

    Task<Result<ConnectorHealth>> HealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Creates a transport for a remote connector address. Registered in DI so a deployment that has
/// the gRPC binding installed gets it, and one that does not gets
/// <see cref="UnconfiguredRemoteTransport"/> and a clear error rather than a mystery.
/// </summary>
public interface IRemoteConnectorTransportFactory
{
    IRemoteConnectorTransport Create(Uri address, ConnectorManifest manifest);
}

/// <summary>
/// The default when no gRPC transport has been registered.
///
/// This exists because the alternative — throwing at startup, or silently registering an
/// out-of-process connector that fails opaquely on first use — both produce worse operator
/// experiences than a connector that is visibly present and honestly reports why it cannot be
/// used. The manifest still loads, the connector still appears in the catalogue marked
/// unhealthy, and every call returns BrokerUnavailable with an actionable message.
///
/// See docs/adr/0006-three-hosting-models.md: the wire protocol is specified in
/// src/Akshaya.Connectors.Proto/broker_connector.proto, and the gRPC binding is the remaining
/// work.
/// </summary>
public sealed class UnconfiguredRemoteTransport(Uri address, ConnectorManifest manifest)
    : IRemoteConnectorTransport
{
    private Error Unavailable => new(
        ConnectorErrorCodes.BrokerUnavailable,
        $"Connector '{manifest.Id}' is declared out-of-process at {address}, but no gRPC "
        + "transport is registered in this deployment. Register an IRemoteConnectorTransportFactory "
        + "during startup, or change the connector's hosting to InProcess.");

    public Uri Address { get; } = address;

    public Task<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        BrokerSession? session,
        CancellationToken ct = default) =>
        Task.FromResult(Result<TResponse>.Failure(Unavailable));

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        string method,
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        BrokerSession? session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Report the connection state rather than yielding nothing: a stream that silently ends
        // is indistinguishable from a quiet market, and a trader must be able to tell those apart.
        yield return new StreamEvent.ConnectionChanged(
            StreamState.Disconnected, Unavailable.Message);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<Result<ConnectorHealth>> HealthAsync(CancellationToken ct = default) =>
        Task.FromResult<Result<ConnectorHealth>>(new ConnectorHealth
        {
            IsHealthy = false,
            StreamState = StreamState.Disconnected,
            SessionValid = false,
            Detail = Unavailable.Message,
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Default factory: hands back the unconfigured transport.</summary>
public sealed class UnconfiguredRemoteTransportFactory : IRemoteConnectorTransportFactory
{
    public IRemoteConnectorTransport Create(Uri address, ConnectorManifest manifest) =>
        new UnconfiguredRemoteTransport(address, manifest);
}
