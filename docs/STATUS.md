# Status: what is verified and what is not

**Last updated: 2026-09-04**

Read this before trusting anything in this repository.

## The headline

**This section's original claim is now out of date, and that is good news.** It said no C# file
had ever been compiled, no test had ever run, and the Angular app had never been built. All
three are now false:

- `dotnet build Akshaya.sln` **succeeds**, 0 errors.
- `dotnet test Akshaya.sln` **passes**: 535 tests across six projects.
- `cd apps/web && npm run build && npm test && npm run lint` all **pass** (26 tests).
- The API and the Angular app have been **run together** and exercised through a browser —
  place, amend, cancel, cancel-all, fills, positions, square-off and the risk gate.

That last one matters most, because compiling proves less than running. Four defects survived
every static check and were only found by using the app:

- `GET /api/orders` declared its filters as non-nullable `bool`, which makes them **required**
  query parameters. The web client sends only one of them, so the blotter 500'd on every load
  and showed "Could not load orders". **The order screen had never worked.**
- The web client called `PUT /api/orders/{id}` and `DELETE /api/orders/{id}`; the API exposes
  `POST /{id}/modify` and `POST /{id}/cancel`. Both old paths return 405 — **cancelling from
  the blotter had never worked either.**
- An accepted amendment appended an event but never updated `Order.Request`, so the blotter,
  `PendingQuantity` and the risk gate all kept reporting the pre-amendment numbers.
- `orders.modifiable` carries **values**, not property names, so the API's camelCase policy
  leaves the manifests' PascalCase alone. Every call site compared against camelCase, so the
  order ticket's disclosed-quantity field had never rendered. This is the *same class of bug*
  this file already records catching once (see below) — it was caught in the manifest and
  missed in the comparison.

The lesson to carry forward: the static checks in this repo are worth keeping, but "it passes
the checker" and "a person used it" are very different claims.

## What *has* been verified

| Check | Tool | What it proves |
|---|---|---|
| No broker name outside `src/connectors/` | `scripts/verify-structure.py --check leakage` | The plug-and-play invariant holds today |
| No ambient `DateTime.Now` | `--check time` | Backtests can control the clock |
| Manifests are valid and self-consistent | `--check manifests`, `scripts/validate-manifests.py` | The UI can render from them |
| Project reference graph is layered and acyclic | `--check refs` | The contract stays dependency-free |
| Every connector ships a manifest and implements the contract | `--check connectors` | The host can load them |
| Constructed types are declared somewhere | `--check types` | Catches an invented helper class |

Run them all with `python3 scripts/verify-structure.py`. Five of the six pass as of 2026-09-04;
`type-resolution` reports pre-existing false positives on BCL and NuGet types (`AesGcm`,
`Claim`, `PriorityQueue`, `SqliteConnection`) that predate this checker's `known_external` list.
**A real compiler is now available and is the better check** — `dotnet build` is authoritative.
`scripts/validate-manifests.py` validates all four manifests against the schema.

Two real defects were found and fixed by those checks while writing this, which is roughly the
point of having them:

- `ConnectorFactory` constructed a `GrpcConnectorProxy` that did not exist — a straightforward
  compile error the type-resolution check caught without a compiler.
- One connector's manifest used a different vocabulary for `orders.modifiable` than the other
  three. The UI disables fields by that name, so the mismatch would have silently disabled the
  wrong controls on the order ticket.

## What has NOT been verified

- ~~**Compilation.**~~ Resolved — the solution builds and the tests pass. See the headline.
- ~~**Package versions.**~~ Resolved — restore succeeds. Two advisories are outstanding
  (`Microsoft.OpenApi` 2.0.0, high; `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.12.0,
  moderate) and should be bumped.
- **The mStock connector against the live API.** Endpoint paths, headers and the auth flow come
  from the published Type A documentation, but no request has ever been sent beyond login.
  Response shapes in `MStockDtos.cs` are the most likely place reality differs from this code.

  The order, margin, position and error pages **were re-read from the vendor's site on
  2026-09-04** and several real mismatches were corrected — placement and modification are
  form-encoded rather than JSON, modify is a replace rather than a patch, cancel-all reports no
  count, `/trades` needs a date window, and two documented statuses (`TRIGGERED`, `PENDING`)
  were unmapped. mStock also publishes a margin-and-charges calculator the manifest had declared
  absent. See [`features/orders.md`](features/orders.md) and
  [`connectors/mstock.md`](connectors/mstock.md). The docs host refuses plain HTTP clients but
  serves a real browser — re-check through one rather than assuming it is unreachable.

- **The Zerodha Kite connector against a live account.** Written from the Kite Connect v3
  documentation read on 2026-09-06. Two parts **are** verified: the instrument master (all four
  exchange dumps parsed, 60,089 instruments, zero unparseable rows, 5,000 round-tripped through the
  symbol translator both ways) and the binary tick protocol (LTP, quote, full and index packets
  built byte-for-byte from the documented layout and fed through the real decoder). No
  authenticated request has been sent; the response shapes in `ZerodhaDtos.cs` are the most likely
  place reality differs. The smoke test in [`connectors/zerodha.md`](connectors/zerodha.md) has
  **not** been run.

  One thing there is deliberate rather than unfinished, and is written up in that file: Kite's
  access token expires at **06:00 IST**, not at venue midnight, and `AuthSpec` has no way to
  express an hour-of-day. The connector declares `expiresAtVenueMidnight: false` and computes the
  expiry itself; declaring it true would have `SessionMonitor` clamp every session six hours early.
  Generalising that field is a contract change that needs an ADR.

- **The FYERS connector against a live account.** Written from the v3 documentation read on
  2026-09-06. The symbol master **is** verified — all NSE/BSE files were downloaded and parsed,
  and 4,000 instruments round-tripped through the symbol translator in both directions without
  loss — but no authenticated request has ever been sent. The response shapes in `FyersDtos.cs`
  are the most likely place reality differs. The smoke test in
  [`connectors/fyers.md`](connectors/fyers.md) has **not** been run.

  Two things there are deliberate rather than unfinished, and both are written up in that file:
  the connector declares `refreshSupported: false` because FYERS is withdrawing refresh tokens
  and the route needs the user's trading PIN, and it exposes **no live feed** because the FYERS
  market-data socket speaks a proprietary binary protocol with no published wire format. The
  order socket is documented JSON and could be wired, but `marketData.streaming` is a single
  flag covering both feeds — splitting it is a contract change that needs an ADR.

- **The moomoo, Longbridge, IBKR and Tiger connectors against a live account.** All four were written
  from vendor documentation and official SDK sources read 2026-09-11 and 2026-09-12, and **not one
  authenticated request has been sent** through any of them. Each has a smoke test in
  `connectors/<id>.md` that has **not** been run, and each ends with the open questions its sources did
  not answer — the places reality is most likely to differ:
  - [`connectors/moomoo.md`](connectors/moomoo.md) — OpenD's framing is hand-written from the published
    protocol, and the gateway is run by the operator, one per credential.
  - [`connectors/longbridge.md`](connectors/longbridge.md) — the quote and trade sockets' protobuf is
    hand-encoded from the published `.proto` files, and the declared rate limits are conservative
    guesses because Longbridge publishes none.
  - [`connectors/ibkr.md`](connectors/ibkr.md) — whether a stop's trigger travels in `price` or
    `auxPrice` is the first thing its smoke test settles. Order reply messages are never confirmed
    automatically: each is one of the username's own precautions.
  - [`connectors/tiger.md`](connectors/tiger.md) — whether Tiger accepts a 32-character `user_mark` is
    the first thing its smoke test settles, because that marker is how a timed-out order is recovered.

- **Order behaviour beyond the Paper connector.** The whole order flow has been exercised
  end to end against `paper`, which implements the same contract. The mStock smoke test in
  [`features/orders.md`](features/orders.md) §13 has **not** been run.
- **Charge schedules.** The rate constants in `Akshaya.Connector.Paper/Charges/` are marked
  `REVIEW:` and must be checked against current published schedules before anyone trusts a
  backtest's net P&L.
- **The Angular app's runtime behaviour**, beyond the order flow. The order, fills and positions
  screens have been driven in a browser against a live API. The dashboard, watchlist, chart,
  holdings and broker-link wizard have been compiled and type-checked but not exercised the
  same way — and the four defects in the headline are what that difference is worth.

## Recommended order of work

Steps 1–6 of the original list are done: the solution restores, builds and tests green, and the
web app builds. What is left:

1. **Run the app and use the screens that have not been used.** Four defects hid behind a green
   build; there is no reason to think the remaining screens are cleaner than the order screens
   were. `./scripts/dev-up.sh`, or `ASPNETCORE_ENVIRONMENT=Development dotnet run --project
   src/Akshaya.Api` alongside `npm --prefix apps/web start`.
2. **Durable persistence.** Everything is in-memory; nothing survives a restart. This blocks
   GTT, and it blocks trusting anything overnight.
3. **Identity.** Every request still runs as a dev user.
4. **Point the mStock connector at a real account** and run the smoke test in
   [`features/orders.md`](features/orders.md) §13 — it is the longer and more current of the
   two, covering orders, amendments, stops, cancel-all, AMO, history and conversion.

## Coverage against the build plan

| Phase | Scope | State |
|---|---|---|
| 0 | Foundations, SharedKernel, solution, CI, architecture tests | Written |
| 1 | Identity, tenancy, 2FA | **Not started** — the API uses a clearly-marked dev auth stub |
| 2 | Connector contract, SDK, host, conformance kit, two fakes | Written |
| 3 | mStock connector | Written; re-checked against the published docs 2026-09-04, still unverified against the live API beyond login |
| 3 | Zerodha Kite connector | Written from the v3 docs 2026-09-06; instrument master and binary tick protocol verified, everything else unverified against a live account |
| 3 | FYERS connector | Written from the v3 docs 2026-09-06; symbol master verified against live files, everything else unverified against a live account |
| 4 | Credential vault, envelope encryption, link flow | **Partial** — the link flow exists, the KMS-backed vault does not |
| 5 | Order state machine, risk gate, portfolio, reconciliation | Written and **exercised end to end** against the Paper connector; persistence still in-memory |
| 6 | Market data, SignalR fan-out, candles | **Partial** — the hub and conflation exist; TimescaleDB storage does not |
| 7 | Strategy engine, backtester | **Not started** — the Paper connector and charge schedules are the groundwork |
| 3 | moomoo, Longbridge, IBKR and Tiger connectors | Written from vendor docs and SDK sources 2026-09-11/12; entirely unverified against live accounts |
| 8 | Second and third connectors | Written — seven connectors now implement the contract across India, the US, Hong Kong and Singapore. The design held; the four platform gaps it exposed are recorded in ADR 0008 |
| 9–10 | IBKR, SaaS hardening | **Partial** — the IBKR connector is written against the Client Portal Gateway; SaaS hardening is not started |

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
- **Gateways are run by the operator, not supervised by the host.** `ConfiguredGatewayRuntime` resolves
  a gateway's address from configuration and probes it (ADR 0008); the host does not start, stop or
  restart one. An operator runs OpenD or the Client Portal Gateway — one per credential — and tells the
  host where it listens. A gateway that is not answering surfaces as `GatewayRequired` in the link
  wizard and as unhealthy in connector health, rather than as an opaque failure.
