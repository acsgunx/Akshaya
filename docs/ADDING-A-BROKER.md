# Adding a broker

The whole platform is arranged so this is a checklist rather than a project. If any step here
requires editing something outside `src/connectors/` and `docs/`, stop — that is a design bug, and
the fix belongs in the manifest or the contract, not in a special case.

Budget: a well-documented REST broker is a few days. A gateway-hosted one (Moomoo, IBKR) is
longer, and most of the extra time is infrastructure, not code.

---

## 0. Before you write anything

Answer these from the broker's documentation and write the answers into
`docs/connectors/<id>.md` as you go:

- **Auth model** — OAuth2? password + OTP? TOTP? static token? RSA-signed requests? A local
  gateway?
- **Session lifetime** — rolling, or dead at venue midnight? Is there a refresh? Does the session
  drop when idle (does it need a keepalive)?
- **Symbology** — what identifies an instrument? A symbol string, a numeric token, a `conid`? Is
  there an instrument master to ingest, and how big is it?
- **Rate limits** — per second, per minute, per day; separate buckets for orders / data / quotes?
- **Order vocabulary** — which product types, order types, validities? What does its order status
  vocabulary look like, including the awkward intermediate states?
- **Streaming** — WebSocket? Binary or JSON? Does it push order updates on the same socket?
- **Sandbox** — is there one, and what does it cost to get credentials?
- **Compliance** — does automated order flow need approval in this jurisdiction?

The last two decide your timeline. IBKR third-party access, for instance, needs a formal
compliance onboarding measured in months — start it before you write a line of code.

---

## 1. Create the project

```bash
mkdir -p src/connectors/Akshaya.Connector.<Name>
```

Copy `Akshaya.Connector.MStock.csproj` as a starting point. Note the item group that marks
`connector.manifest.json` as content — without it, the file is not copied to the output directory
and the host silently never sees your connector.

Add the project to `Akshaya.sln`.

---

## 2. Write the manifest first

`connector.manifest.json`, validated against
[`connector.manifest.schema.json`](../src/Akshaya.Connectors.Abstractions/connector.manifest.schema.json).

Write it **before** the code. It forces you to answer the questions in step 0, and everything
downstream — the order ticket, the link wizard, the risk gate, the conformance suite — reads it.

Be honest in it. A manifest that claims `bracket: true` when the connector returns `NotSupported`
fails conformance, which is exactly what that test is for. Under-claiming is safe;
over-claiming ships a broken order ticket.

```bash
python3 scripts/validate-manifests.py
```

---

## 3. Implement the facets

Start from `ConnectorBase`, which defaults every facet to `NotSupported` so you implement only
what the broker actually has.

| File | What goes in it |
|---|---|
| `<Name>Connector.cs` | Wiring only. If business logic appears here, a facet boundary is wrong |
| `<Name>Auth.cs` | The `AuthStep` walk. Compute session expiry honestly — see below |
| `<Name>Orders.cs` | Place / modify / cancel / books |
| `<Name>Portfolio.cs` | Positions, holdings, per-currency balances |
| `<Name>MarketData.cs` | Quotes, history, depth, option chain |
| `<Name>Reference.cs` | Instrument master ingest, streamed |
| `<Name>Stream.cs` | Socket, reconnect with backoff + jitter, re-subscribe |
| `<Name>Maps.cs` | **All** enum mapping, both directions, in one file |
| `<Name>SymbolTranslator.cs` | Canonical ⇄ native symbology |
| `<Name>ErrorMapper.cs` | Vendor error → canonical `ConnectorErrorCode` |
| `<Name>Dtos.cs` | Wire types with `JsonPropertyName` |

Non-negotiables:

- **Never throw for an expected broker failure.** Return `Result<T>` with a canonical code and the
  vendor's raw code and message preserved.
- **Never write your own retry loop.** The host's decorator chain owns retries, and a
  connector-level retry can duplicate an order.
- **Never guess a symbol.** An unmapped symbol is `InstrumentNotFound`.
- **Never return an unmapped enum as a default.** `OrderStatus.Unknown` exists so you do not have
  to pretend; a silent default is how a rejected order shows as open.
- **Session expiry takes the minimum** of every constraint that applies. If the token dies at
  venue midnight, say so in the manifest and compute it in the auth facet.

---

## 4. Write the tests

Create `tests/Akshaya.Connector.<Name>.Tests/` and inherit `ConnectorConformanceTests`. That
suite is most of your coverage; add on top of it:

- **Exhaustive mapping round-trips.** A `[Theory]` over *every* enum value in both directions.
  This is the cheapest test in the repository and it catches the most expensive bugs.
- **Session expiry**, with `ManualClock`, including the venue-midnight case if it applies.
- **Symbol translation**, including the unknown-symbol failure.
- **Error mapping**, over recorded vendor payloads.

Record fixtures from the sandbox with WireMock and check them in, so CI needs no credentials.

---

## 5. Register it

One line in the API's composition root registers the connector for local development. In
production the host discovers it from the plugin directory by its manifest.

---

## 6. Verify

```bash
python3 scripts/verify-structure.py          # leakage, manifests, references
dotnet test tests/Akshaya.Connector.<Name>.Tests
dotnet test tests/Akshaya.Architecture.Tests # proves you did not leak the broker's name
dotnet build                                 # proves apps/web needed no change
```

Then the manual smoke test, which no automated suite replaces: link the account, place a small
order, modify it, cancel it, and **verify each step in the broker's own UI**. Record the date and
result in `docs/connectors/<id>.md`.

---

## 7. The acceptance question

> Did adding this broker change anything in `src/Modules/`, `src/Akshaya.Api/`, or `apps/web/`?

`git diff --stat` should say no. If it says yes, do not merge it as-is. Work out whether the
change belongs in the manifest instead, and if the contract genuinely had to grow, write an ADR
in `docs/adr/` explaining what was missing and why — that record is how the next person
understands the shape of the abstraction.

---

## Special case: gateway-hosted brokers

Moomoo (OpenD) and IBKR (Client Portal Gateway) need a vendor daemon running per credential.

- Set `"hosting": "gateway"` and fill the `gateway` block.
- Your auth facet returns `AuthStep.GatewayRequired` when the daemon is not up.
- The host's `GatewaySupervisor` manages lifetime and health; `IGatewayRuntime` is the seam where
  the container actually gets started.
- Budget for the infrastructure cost: one process per user is a real line item, and the plan model
  needs to price it.
- Document the setup in `docs/connectors/<id>.md` in enough detail that an operator can stand one
  up without reading the vendor's docs.
