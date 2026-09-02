# Akshaya

A broker-agnostic, market-agnostic trading platform. Link any broker account in any supported
market and trade from one interface.

**The prime directive:** adding a new broker requires **zero changes** to the core, the API, or
the Angular app — only a new connector project plus its manifest. Everything the platform knows
about a broker comes from a declarative capability manifest and one interface.

> ⚠️ **Read [`docs/STATUS.md`](docs/STATUS.md) before you do anything else.** This repository was
> written in an environment with no .NET SDK and no package-registry access, so **nothing here has
> ever been compiled.** That document says exactly what is verified, what is not, and what to run
> first.

---

## Quick start

```bash
# 1. Prerequisites: .NET 10 SDK, Node 22+, Docker
scripts/bootstrap.sh          # checks toolchain, restores packages, installs web deps

# 2. Infrastructure (Postgres+TimescaleDB, Redis, OpenTelemetry collector, Seq)
scripts/dev-up.sh

# 3. Backend  → http://localhost:5080 (Scalar API docs at /scalar)
dotnet run --project src/Akshaya.Api

# 4. Frontend → http://localhost:4200
cd apps/web && npm start
```

The Paper connector is registered by default, so you can place orders against the simulated
matching engine without any broker credentials.

---

## Repository layout

```
src/
  Akshaya.SharedKernel/            Result<T>, Money, Currency, Venue, InstrumentKey, IClock
  Akshaya.Connectors.Abstractions/ THE CONTRACT — references nothing but SharedKernel
  Akshaya.Connectors.Sdk/          Base classes, decorators, rate limiter, manifest loader
  Akshaya.Connectors.Host/         Discovery, AssemblyLoadContext isolation, gateway supervision
  connectors/
    Akshaya.Connector.MStock/      Reference implementation (India, NSE/BSE)
    Akshaya.Connector.Paper/       Simulated broker — paper trading and backtest execution
  Modules/
    Trading/                       Order state machine, risk gate, reconciliation
    Portfolio/                     Multi-currency blended portfolio
  Akshaya.Api/                     Minimal APIs, SignalR hub, composition root
apps/web/                          Angular app — renders itself from connector manifests
tests/
  Akshaya.Connectors.TestKit/      Conformance suite + two deliberately different fake brokers
  Akshaya.Architecture.Tests/      The rules that keep the design honest
  Akshaya.Trading.Tests/           Order state machine and risk gate
  Akshaya.Connector.MStock.Tests/  Mapping, session expiry, symbol translation
scripts/                           Dev and verification scripts (see scripts/README.md)
docs/                              Architecture, ADRs, per-connector notes, compliance
```

---

## The one idea worth understanding

Everything a broker can do is declared in a `connector.manifest.json`:

```jsonc
{
  "id": "example",
  "venues": ["XNSE", "XSES"],          // ISO 10383 MICs — not an enum anywhere
  "currencies": ["INR", "SGD"],
  "auth": { "model": "passwordOtp", "credentialFields": [ /* the link wizard's form */ ] },
  "orders": {
    "types": ["Market", "Limit", "Stop"],
    "positionEffects": ["Delivery", "Intraday"],
    "fractionalQuantity": false
  },
  "marketData": { "streaming": true, "streamModes": ["Ltp", "Quote"] },
  "rateLimits": [{ "scope": "orders", "perSecond": 30, "perMinute": 250 }]
}
```

The order ticket renders from it. The link wizard builds its form from it. The risk gate
validates against it. The conformance suite checks the connector actually behaves the way its
manifest claims.

**There is exactly one order ticket component and exactly one link wizard component, for every
broker in every market.** An architecture test scans the core, the API and the frontend for
broker names and fails the build on a hit. If you need per-broker behaviour, add a manifest
field — never a conditional.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for how it fits together and
[`docs/ADDING-A-BROKER.md`](docs/ADDING-A-BROKER.md) for the checklist.

---

## Supported and planned connectors

| Connector | Market | Auth | Transport | State |
|---|---|---|---|---|
| Paper | any | none | in-process | Implemented |
| m.Stock (Mirae Asset) | India | password + SMS OTP / TOTP | REST + WebSocket | Implemented, never run against the live API |
| Zerodha Kite | India | request token + checksum | REST + binary WS | Planned |
| Fyers, Upstox, Angel One, Dhan | India | OAuth2 / TOTP / static token | REST + WS | Planned |
| Moomoo (Futu) | SG, HK, US, JP, AU | local OpenD gateway | TCP + protobuf | Planned — needs a gateway sidecar per credential |
| IBKR | Global | OAuth 1.0a or Client Portal Gateway | REST + WS | Planned — third-party access needs a compliance onboarding measured in months, start it early |
| Saxo, Tiger | SG, Global | OAuth2 / RSA-signed | REST + streaming | Planned |

---

## Verification

There is no compiler in the environment this was written in, so a static checker stands in:

```bash
python3 scripts/verify-structure.py     # broker leakage, ambient time, manifests, refs, types
python3 scripts/validate-manifests.py   # manifests against the JSON schema
```

Once you have the SDK, the real gates are:

```bash
dotnet build                                  # the check that has never been run
dotnet test tests/Akshaya.Architecture.Tests  # the design's own guardrails
dotnet test                                   # everything
cd apps/web && npm run lint && npm test
```

---

## Safety and compliance

- Broker credentials are envelope-encrypted; plaintext exists only in memory during a login call.
- API keys and access tokens never reach the browser — all broker traffic is server-side.
- Live strategy automation is **supervised by default**: a strategy emits a signal, which becomes
  a notification with a one-click order ticket, not an order. Automatic execution is gated behind
  2FA, a daily loss cap, a kill switch, and a per-connector compliance flag.
- Automated order flow is regulated differently in every jurisdiction. India's rules for retail
  algorithmic trading require broker approval and exchange registration; Singapore and the US
  differ again. See [`docs/compliance/`](docs/compliance/) — and get approval before you enable
  anything.
- Nothing in this platform is investment advice.

---

## Licence

Not yet chosen. Decide before the first external contribution.
