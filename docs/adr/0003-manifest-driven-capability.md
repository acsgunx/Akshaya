# ADR 0003 — Broker differences are declared in a manifest, never coded around

- **Status:** Accepted
- **Date:** 2026-09-02

## Context

Brokers differ in almost every dimension: which order types they accept, whether they do
fractional quantities, whether a basket is atomic, how many instruments a socket may subscribe to,
what their rate limits are, whether their token dies at midnight.

The core and the UI need to know these things. There are two ways to let them know: ask the
broker's connector at runtime through a widening interface, or read a declaration.

## Decision

Every connector ships `connector.manifest.json`, validated against a JSON schema at load time and
in CI. It declares venues, currencies, asset classes, the auth model and its credential form,
supported order types / time-in-force / position effects, fractional-quantity support, basket
atomicity and max legs, streaming modes and subscription caps, rate limits, sandbox availability,
and jurisdiction and compliance flags.

The manifest is served at `GET /api/connectors`. The Angular order ticket and link wizard render
from it. The risk gate validates against it. The conformance suite checks the connector actually
behaves the way its manifest claims.

**The rule:** if the core or the UI needs to know something about a broker, it becomes a field in
the manifest. It never becomes `if (connectorId == "...")`.

## Why

The failure mode this prevents is gradual and quiet. Nobody adds thirty special cases in one
commit. Someone adds one, on a Friday, because the release is Monday. Six months later "add a
broker" means touching thirty files and nobody can point at when it stopped being easy.

Declaration also makes the UI honest for free. A trader using a broker that has no bracket orders
should not see a bracket-order tab that fails on submit. Rendering from capability means the
impossible option is simply absent.

## Enforcement

- `BrokerLeakageRules` scans `src/Modules`, `src/Akshaya.Api` and `apps/web` for broker names —
  including in comments and log messages, which is where a special case starts — and fails the
  build on a hit.
- `ConnectorConformanceTests` asserts declared-vs-actual in both directions: a claimed capability
  must work, an unclaimed one must return `NotSupported` rather than throwing or silently
  substituting something else.

## Consequences

- Adding a manifest field is a contract change: schema, C# record, TypeScript model, and the UI
  that reads it. That friction is intentional — it makes people ask whether the difference is real.
- An over-claiming manifest ships a broken order form. Conformance is what catches it, so a
  connector without a green conformance run is not finished.
- Third parties can write connectors without reading our source, because the manifest plus the
  contract is the whole interface.
