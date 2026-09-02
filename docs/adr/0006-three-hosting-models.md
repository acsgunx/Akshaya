# ADR 0006 — Three connector hosting models, including out-of-process

- **Status:** Accepted (in-process built; out-of-process and gateway designed, not built)
- **Date:** 2026-09-02

## Context

"Support any broker" runs into two brokers on the target list that cannot be reached by an HTTP
client in our process:

- **Moomoo / Futu** requires **OpenD**, a vendor gateway daemon running on your machine or server,
  exposing a TCP + protobuf interface. SDKs exist for Python, Java, C#, C++ and JavaScript.
- **IBKR** offers either a locally-run **Client Portal Gateway** (browser login, `/tickle`
  keepalive, session dies on idle) or **OAuth 1.0a** for third-party access, which requires a
  formal compliance onboarding typically measured in months.

Some future broker will have a Python-only SDK, or a protocol nobody wants to reimplement in C#.

## Decision

`ConnectorManifest.Hosting` has three values, and the core cannot tell them apart:

| Value | What it is | For |
|---|---|---|
| `inProcess` | A C# assembly in its own collectible `AssemblyLoadContext` | Most REST brokers |
| `outOfProcess` | A separate process or container speaking gRPC (`broker_connector.proto`) | Python-only SDKs, third-party connectors |
| `gateway` | A supervised vendor daemon that the connector talks to | Moomoo OpenD, IBKR Client Portal Gateway |

`GrpcConnectorProxy` implements `IBrokerConnector` over a channel, so an out-of-process connector
satisfies the same interface as an in-process one. `GatewaySupervisor` manages daemon lifetime and
health, with `IGatewayRuntime` as the seam where a container is actually started.

Two contract elements exist specifically for this: `AuthStep.GatewayRequired` and
`ConnectorErrorCodes.GatewayUnavailable`.

## Why

Without the out-of-process path, "broker-agnostic" quietly means "broker-agnostic among brokers
with a C#-friendly REST API", which is a much smaller claim. Defining the boundary as a wire
protocol rather than a .NET interface is what makes it true.

The load-context isolation matters even for in-process connectors: a connector that pins an old
JSON library must not break another one, and diagnosing that class of failure inside a plugin
system is unpleasant.

## Costs we are accepting

- **Gateway connectors cost real money per user.** `gateway.perCredential` is true for both named
  cases, meaning one supervised process per linked account. The pricing model has to know this
  before anyone sells the feature.
- **IBKR's onboarding is a schedule risk, not an engineering one.** It must be started months
  before the connector is needed. This is recorded here because it is the kind of dependency that
  is invisible until it is late.
- gRPC adds serialisation overhead on every call. Acceptable for orders and portfolio; the tick
  stream needs measuring before anyone runs a high-rate feed through it.

## Status

In-process is built and exercised by the mStock and Paper connectors.

`broker_connector.proto` is complete, and `GrpcConnectorProxy` implements `IBrokerConnector`
against an `IRemoteConnectorTransport` seam — so the shape is real and the core genuinely cannot
tell the hosting models apart. **The gRPC implementation of that seam is not written**, and nor is
a real `IGatewayRuntime`. A connector declaring `OutOfProcess` today loads, appears in the
catalogue as unhealthy, and returns `BrokerUnavailable` with an actionable message on every call.

That degradation is deliberate: failing construction would take the whole catalogue down over one
misconfigured connector, and silently registering something that fails opaquely on first use is
worse for an operator than a connector that says exactly what is missing.

Until a connector actually runs in another process, "any language" is a design property rather
than a demonstrated one. See `docs/STATUS.md`.
