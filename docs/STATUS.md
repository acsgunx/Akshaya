# Status: what is verified and what is not

**Last updated: 2026-09-02**

Read this before trusting anything in this repository.

## The headline

This codebase was written in an environment with **no .NET SDK and no access to NuGet or npm**.
That means:

- **No C# file here has ever been compiled.**
- **No test has ever been run.**
- **The Angular app has never been installed, built, or served.**

Everything was written to be correct by inspection, cross-checked against the contract files, and
run through a static consistency checker. That is not the same as a green build, and it would be
dishonest to present it as one. Expect a first `dotnet build` to surface real errors — missing
usings, signature drift between projects written in parallel, and package versions that need
pinning to what actually exists on your feed.

## What *has* been verified

| Check | Tool | What it proves |
|---|---|---|
| No broker name outside `src/connectors/` | `scripts/verify-structure.py --check leakage` | The plug-and-play invariant holds today |
| No ambient `DateTime.Now` | `--check time` | Backtests can control the clock |
| Manifests are valid and self-consistent | `--check manifests`, `scripts/validate-manifests.py` | The UI can render from them |
| Project reference graph is layered and acyclic | `--check refs` | The contract stays dependency-free |
| Every connector ships a manifest and implements the contract | `--check connectors` | The host can load them |
| Constructed types are declared somewhere | `--check types` | Catches an invented helper class |

Run them all with `python3 scripts/verify-structure.py`. **All six pass as of 2026-09-02**, and
`scripts/validate-manifests.py` validates all four manifests against the schema.

Two real defects were found and fixed by those checks while writing this, which is roughly the
point of having them:

- `ConnectorFactory` constructed a `GrpcConnectorProxy` that did not exist — a straightforward
  compile error the type-resolution check caught without a compiler.
- One connector's manifest used a different vocabulary for `orders.modifiable` than the other
  three. The UI disables fields by that name, so the mismatch would have silently disabled the
  wrong controls on the order ticket.

## What has NOT been verified

- **Compilation.** The single biggest unknown.
- **Package versions.** Versions in `.csproj` and `package.json` were chosen from knowledge, not
  from a live feed. Some will not resolve. `scripts/bootstrap.sh` reports which.
- **The mStock connector against the live API.** Endpoint paths, headers and the auth flow come
  from the published Type A documentation, but no request has ever been sent. Response shapes in
  `MStockDtos.cs` are the most likely place reality differs from this code.
- **Charge schedules.** The rate constants in `Akshaya.Connector.Paper/Charges/` are marked
  `REVIEW:` and must be checked against current published schedules before anyone trusts a
  backtest's net P&L.
- **The Angular app's runtime behaviour.** Templates, bindings and imports were checked by hand
  and by grep, not by the compiler.

## Recommended order of work

1. `scripts/bootstrap.sh` — resolve the toolchain and fix package versions until restore succeeds.
2. `dotnet build` — fix compile errors. Expect the seams between projects written in parallel
   (Sdk ↔ connectors, Modules ↔ Api) to need the most attention.
3. `dotnet test tests/Akshaya.Architecture.Tests` — these encode the design's rules; get them
   green early so later work cannot quietly violate them.
4. `dotnet test tests/Akshaya.Connectors.TestKit` — the conformance suite against the two fake
   connectors. Green here means the abstraction spans two genuinely different brokers.
5. `dotnet test` — everything else.
6. `cd apps/web && npm install && npm run build`.
7. Only then: point the mStock connector at the sandbox and run the manual smoke test in
   [`connectors/mstock.md`](connectors/mstock.md).

## Coverage against the build plan

| Phase | Scope | State |
|---|---|---|
| 0 | Foundations, SharedKernel, solution, CI, architecture tests | Written |
| 1 | Identity, tenancy, 2FA | **Not started** — the API uses a clearly-marked dev auth stub |
| 2 | Connector contract, SDK, host, conformance kit, two fakes | Written |
| 3 | mStock connector | Written, unverified against the live API |
| 4 | Credential vault, envelope encryption, link flow | **Partial** — the link flow exists, the KMS-backed vault does not |
| 5 | Order state machine, risk gate, portfolio, reconciliation | Written |
| 6 | Market data, SignalR fan-out, candles | **Partial** — the hub and conflation exist; TimescaleDB storage does not |
| 7 | Strategy engine, backtester | **Not started** — the Paper connector and charge schedules are the groundwork |
| 8 | Second and third connectors | **Not started** — this is the phase that tests whether the design worked |
| 9–10 | IBKR, SaaS hardening | **Not started** |

## Known gaps worth naming

- **Persistence is in-memory.** `Modules/Trading/Infrastructure/InMemory/` is dev-only and says so.
  Nothing survives a restart. EF Core + Postgres is Phase 5 work.
- **No identity.** Every request runs as a dev user. Do not deploy this anywhere reachable.
- **The out-of-process gRPC binding is not written.** `broker_connector.proto` is complete and
  `GrpcConnectorProxy` implements `IBrokerConnector` against an `IRemoteConnectorTransport` seam,
  but no gRPC transport implements that seam. A connector declaring `"hosting": "OutOfProcess"`
  therefore loads, shows as unhealthy, and returns `BrokerUnavailable` with an actionable message
  rather than failing opaquely. Until a connector actually runs in another process — ideally the
  reference Python one — "any language" is a design property, not a demonstrated one.
- **No instrument master service.** Market data endpoints currently answer through a specific
  broker link rather than a canonical, cross-broker instrument store.
- **Gateway supervision is a seam, not an implementation.** `IGatewayRuntime` has a null
  implementation; running an actual OpenD or Client Portal Gateway container is unbuilt.
