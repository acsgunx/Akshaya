# Akshaya.Connectors.Proto

The wire contract for **out-of-process connectors**: `broker_connector.proto`.

## What this is for

Some brokers cannot be reached comfortably from C#. A vendor ships a Python-only SDK. A protocol
is exotic enough that nobody wants to reimplement it. A daemon comes with its own client library
in a language we do not use.

A connector implementing this service — in Python, Go, Java, anything with a gRPC stack — is
**indistinguishable to the platform from a C# connector loaded in-process**. The host wraps both
in the same decorator chain (audit, tracing, resilience, rate limiting) and hands both to the core
as an `IBrokerConnector`.

That is what makes "any broker" a property of the design rather than a slogan. See
[ADR 0006](../../docs/adr/0006-three-hosting-models.md).

## Status

The `.proto` is complete and mirrors `Akshaya.Connectors.Abstractions` exactly.

`GrpcConnectorProxy` in `Akshaya.Connectors.Host/OutOfProcess/` implements `IBrokerConnector`
against an `IRemoteConnectorTransport` seam. **The gRPC binding for that seam is not written.**
Until it is, a connector declaring `"hosting": "OutOfProcess"` loads, appears in the catalogue,
reports itself unhealthy, and returns `BrokerUnavailable` with an actionable message on every
call — deliberately, rather than failing opaquely or taking the catalogue down.

## To implement the binding

1. Add `Grpc.Net.Client`, `Google.Protobuf` and `Grpc.Tools` to a new
   `Akshaya.Connectors.Proto.csproj`, with `<Protobuf Include="broker_connector.proto" GrpcServices="Client" />`.
2. Write `GrpcRemoteConnectorTransport : IRemoteConnectorTransport` mapping the canonical contract
   records to and from the generated messages.
3. Register `IRemoteConnectorTransportFactory` in the API's composition root.
4. Prove it: ship a reference connector in Python that passes the same
   `ConnectorConformanceTests` suite as the in-process ones. Until that exists, the claim is
   untested.

## Rules for anyone implementing the service

These are in the `.proto` too, and they matter more than the message shapes:

- **Never** return a gRPC error status for an ordinary broker outcome. A rejected order, an
  expired session and a closed market are `Result` values — set the `error` field. gRPC statuses
  are for transport failures only.
- **Always** preserve the broker's own code and message in `vendor_code` / `vendor_message`.
  Support cannot help a trader with a canonical code alone.
- Money always carries its currency. There is no implicit currency anywhere in this contract.
- Quantities and prices are **decimal strings**, never doubles. Fractional shares are real and
  binary floating point is not an acceptable representation of a position size.
