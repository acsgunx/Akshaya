# Architecture

## The problem this shape solves

A trading platform that supports many brokers usually rots the same way. The first broker's
vocabulary becomes the platform's vocabulary. The second broker mostly fits, with a few
conditionals. By the fifth, "add a broker" means touching thirty files, and the order ticket has
a `switch` statement in it.

Akshaya is arranged so that cannot happen quietly. Everything a broker can do is **declared**,
not **coded around**, and the rules that keep it that way are enforced by tests that fail the
build.

---

## Layers

```
                    ┌─────────────────────────────────────────┐
   Angular app  ──► │  Akshaya.Api  (minimal APIs, SignalR)    │
   renders from     └───────────────┬─────────────────────────┘
   manifests                        │
                    ┌───────────────┴─────────────────────────┐
                    │  Modules: Trading, Portfolio            │
                    │  order state machine, risk gate,        │
                    │  reconciliation, blended P&L            │
                    └───────────────┬─────────────────────────┘
                                    │  IConnectorFactory
                    ┌───────────────┴─────────────────────────┐
                    │  Akshaya.Connectors.Host                │
                    │  discovery · isolation · decorators     │
                    │  audit → tracing → resilience → limits  │
                    └───────────────┬─────────────────────────┘
                                    │  IBrokerConnector
        ┌───────────────────────────┼───────────────────────────┐
        │                           │                           │
   ┌────┴─────┐              ┌──────┴──────┐            ┌───────┴────────┐
   │ in-proc  │              │ gRPC proxy  │            │ gateway bridge │
   │ C#       │              │ any language│            │ OpenD, CP GW   │
   └──────────┘              └─────────────┘            └────────────────┘

   Akshaya.Connectors.Abstractions  ← the contract, depends only on SharedKernel
   Akshaya.SharedKernel             ← Result<T>, Money, Venue, InstrumentKey, IClock
```

The core cannot tell an in-process C# connector from a Python process on the other end of a gRPC
channel, or from a bridge to a vendor daemon. That is the point.

---

## The vocabulary (SharedKernel)

The v1 design of this platform had `enum Exchange { NSE, BSE, NFO, BFO }`. That single line makes
a platform Indian forever. The current vocabulary is deliberately open where the world is open and
closed where it genuinely is:

| Concept | Type | Why this shape |
|---|---|---|
| Venue | `record struct Venue(string Mic)` | ISO 10383. Adding SGX or NASDAQ is reference data, not a recompile |
| Currency | `record struct Currency(string Code)` | ISO 4217 |
| Money | `record struct Money(decimal, Currency)` | Adding SGD to INR is a runtime throw, not a wrong dashboard |
| Quantity | `record struct Quantity(decimal)` | Fractional shares are real; `int` silently truncates them |
| Instrument | `InstrumentKey(Venue, Symbol, AssetClass, Expiry?, Strike?, Right?)` | Broker-independent identity; ISIN/FIGI on the definition link cross-listings |
| Position semantics | `[Flags] PositionEffect` | India splits CNC/MIS/MTF/NRML, the US has cash/margin/short. Flags compose; one enum cannot |
| Time | `IClock` | The backtester must control "now" |

Two architecture tests protect this: no bare `decimal` named `*Price`/`*Amount` on contract types,
and no `DateTime.UtcNow` outside `Clock.cs`.

---

## The contract

`IBrokerConnector` splits into facets so a broker can implement a subset and decline the rest
cleanly:

```csharp
public interface IBrokerConnector : IAsyncDisposable
{
    ConnectorManifest Manifest { get; }
    IConnectorAuth       Auth       { get; }
    IConnectorOrders     Orders     { get; }
    IConnectorPortfolio  Portfolio  { get; }
    IConnectorMarketData MarketData { get; }
    IConnectorReference  Reference  { get; }
    IConnectorStream?    Stream     { get; }   // null when the broker has no feed
    Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default);
}
```

Four rules make it work:

1. **`Result<T>` everywhere.** A rejected order, an expired session and a closed market are
   outcomes, not exceptions. Every failure carries a canonical `ConnectorErrorCode` *and* the raw
   vendor code and message, because support needs to know what the broker actually said.
2. **`NotSupported` is a first-class outcome**, returned immediately and cleanly.
3. **Idempotency.** `PlaceAsync` takes a caller-generated `ClientOrderId`. The order is persisted
   before the network call. On a timeout we re-read the order book and match — we never retry a
   placement, because a retried placement is a duplicate order.
4. **Money and time never travel bare.**

### Authentication is a state machine, not a method per broker

The hardest thing to abstract across brokers is login. mStock wants password + SMS OTP. Zerodha
wants an OAuth-ish request token with a SHA-256 checksum. Angel One wants TOTP. Dhan wants a
pasted static token. Tiger signs every request with RSA. Moomoo needs a local daemon running.

All of it collapses into one walk:

```csharp
abstract record AuthStep
{
    record Completed(BrokerSession Session);
    record RedirectRequired(string Url, string State);
    record ChallengeRequired(ChallengeKind Kind, string Prompt, ...);
    record GatewayRequired(string GatewayId, string Instructions);
}
```

`BeginAsync` returns a step; `ContinueAsync` advances it. A new broker should need a new
`AuthStep` case only rarely, and a new method on `IConnectorAuth` never.

### Session expiry is modelled, not assumed

Most Indian brokers invalidate tokens at **midnight in the venue's timezone**, regardless of when
the token was issued. A session monitor that trusted issue-time-plus-lifetime would schedule its
re-auth prompt for 03:00 — hours after the token died — and the first the trader would hear of it
is a rejected order at the next open. `AuthSpec.ExpiresAtVenueMidnight` plus
`VenueMidnightTimeZone` make the real expiry computable, and `SessionMonitor` always takes the
minimum. Expiring early costs one extra login; expiring late costs orders.

---

## Plug-and-play mechanics

### The manifest is the only place broker differences live

`connector.manifest.json` declares venues, currencies, asset classes, the auth model and its
credential form, supported order types / TIFs / position effects, fractional-quantity support,
basket atomicity, streaming modes and subscription caps, rate limits, sandbox availability, and
jurisdiction/compliance flags. It is validated against
[`connector.manifest.schema.json`](../src/Akshaya.Connectors.Abstractions/connector.manifest.schema.json)
at load time and in CI.

The manifest is served at `GET /api/connectors`, and the Angular app renders itself from it.

### Loading and isolation

Each in-process connector loads into its own collectible `AssemblyLoadContext`, so one connector's
dependency versions cannot collide with another's or with the host's. The host wraps every
connector in a fixed decorator chain that connector authors never write themselves:

```
audit → tracing → resilience → rate limiting → the connector
```

Resilience retries only errors in `ConnectorErrorCodes.Retryable`, and only on idempotent reads.
It never retries a placement.

### Three hosting models

| `hosting` | What it is | Used for |
|---|---|---|
| `inProcess` | C# assembly in its own load context | Most REST brokers |
| `outOfProcess` | A separate process speaking gRPC (`broker_connector.proto`) | Brokers with Python-only SDKs, third-party connectors |
| `gateway` | A supervised vendor daemon | Moomoo OpenD, IBKR Client Portal Gateway |

Gateway connectors are the reason `GatewayRequired` and `GatewayUnavailable` exist. Their cost
model is per-credential, which the pricing model must account for.

### Symbol translation stays inside the connector

`ISymbolTranslator` converts canonical `InstrumentKey` ⇄ native symbology (`INFY-EQ`, a Kite
token, an IBKR `conid`, `SG.D05`). A failed translation is `InstrumentNotFound` — never a guess,
because a guessed symbol is an order on the wrong instrument.

### A connector is not "done" until conformance passes

`ConnectorConformanceTests` is inherited by every connector's test project and checks manifest
self-consistency, declared-vs-actual capability, symbol round-trips, error normalisation,
idempotency under timeout, rate-limit shaping, session lifecycle, and subscription hygiene.
Fixtures are recordings, so CI needs no credentials.

Two deliberately different fake connectors — one OAuth2/multi-currency/fractional/streaming, one
password-OTP/INR/lot-based/no-streaming — both pass the identical suite. That is the standing
proof that the abstraction spans real differences.

---

## Trading core

Orders are an explicit state machine with a table-driven transition graph. Illegal transitions
throw, because that is programmer error rather than a broker outcome. Every transition is
persisted with the raw broker payload.

The **pre-trade risk gate** is a list of individually configurable `IRiskRule` implementations:
max order value (normalised through FX), max quantity, max open positions, daily loss limit,
instrument allow/deny, venue market hours, price-band sanity (the fat-finger guard), fractional
quantity permitted by manifest, capability supported by manifest, and a per-tenant kill switch.

`PlaceOrderHandler` runs in a deliberate order — validate, resolve connector, check capability,
run risk, **persist**, then call the broker. The persist-before-call step is what makes a
mid-flight crash recoverable.

**Reconciliation** polls each link's order book and after every stream reconnect, diffs against
local state, and always lets the broker win. The broker is the source of truth; our copy is a
cache.

---

## Portfolio and money

Positions, holdings and balances are aggregated **natively per currency first**, then converted
for display, with the FX rate and its timestamp carried alongside. Realised P&L records the rate
at the time of the trade — converting historic P&L at today's rate is a reporting bug that hides
itself well.

Cross-listed instruments group by ISIN/FIGI where available. One broker being down produces a
`PartialResult` and a degraded view, never a blank dashboard.

---

## Market data

One upstream connection per credential, never one per browser tab. Ticks normalise, fan out
through Redis, and reach clients over SignalR **conflated to at most 4 updates per second per
instrument per client** — intermediates are dropped, never queued, because back-pressure at the
fan-out would stall ingest for every user on that shared connection.

Subscriptions are tracked per connection; the last subscriber leaving unsubscribes upstream.
Manifests declare `maxStreamSubscriptions` and the fan-out respects it.

---

## Frontend

Zoneless Angular, standalone components, signals throughout, NgRx SignalStore per feature.

Two components carry the architecture:

- **`order-ticket`** renders order types, TIFs, position effects (labelled generically —
  "Delivery", not "CNC"), quantity precision and currency entirely from the manifest.
- **`broker-link-wizard`** builds its credential form from `auth.credentialFields` and drives the
  `AuthStep` machine — redirect, OTP, TOTP, device approval, gateway setup.

There is one of each, for every broker. Design decisions — the colour-blind-safe buy/sell pair,
the ban on optimistic UI for orders, price cells that structurally cannot jitter — are recorded in
[`apps/web/src/styles/DESIGN.md`](../apps/web/src/styles/DESIGN.md).

---

## Where the design is still unproven

Read [`STATUS.md`](STATUS.md) for the full list. The two that matter architecturally:

1. **Phase 8 has not happened.** The abstraction has been tested against two fakes and one real
   broker. Its real exam is Zerodha (different auth, binary socket, same market) and Moomoo
   (different market, currency, gateway daemon, protobuf) landing without changing
   `Abstractions`, `Modules/` or `apps/web`.
2. **The out-of-process gRPC path is specified, not built.** Until a connector actually runs in
   another process, "any language" is a claim rather than a demonstrated property.
