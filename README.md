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
scripts/dev-up.sh             # REQUIRED — user accounts live in Postgres

# 3. Database schema
dotnet ef database update --context IdentityDbContext --project src/Akshaya.Api

# 4. Backend + frontend, in one command
scripts/rerun.sh              # or: dotnet run --project src/Akshaya.Api  +  cd apps/web && npm start
```

Then open http://localhost:4200 and **create an account** — the app requires one, and every API
endpoint except sign-up/sign-in rejects an unauthenticated caller.

The Paper connector is registered by default, so you can place orders against the simulated
matching engine without any broker credentials.

---

## Accounts and saved broker logins

You sign in to Akshaya with an email address and a password; the session is an HTTP-only cookie,
so nothing the browser runs can read it. Each account gets its own tenant, which is what every
tenant-scoped store below (risk policy, kill switch, broker links) is keyed by.

When you link a broker, each credential field carries a **"Remember this"** toggle. Whatever you
leave ticked is stored — encrypted — *after* the broker accepts the login, never before, so a
failed attempt saves nothing. Next time, the wizard offers that saved login and asks only for
what is missing; for a broker whose session dies at venue midnight, the daily relink becomes one
click instead of retyping an API key and a client code.

**How the secrets are held:**

- **Envelope encryption, AES-256-GCM.** A fresh 256-bit data key encrypts each record; the master
  key encrypts that data key. Rotating the master key rewraps small data keys instead of
  re-encrypting every blob, and a leaked data key costs exactly one record.
- **Authenticated, not just encrypted.** A payload with a single flipped bit fails to open rather
  than decrypting to something an attacker steered.
- **Never sent to the browser.** There is no endpoint that returns a saved value — not even to
  the account that saved it. The UI shows which *fields* are held ("API key, Client code"), so a
  stolen session cannot be turned into a credential dump. Secrets go from the vault into a broker
  login call inside one request and nowhere else.
- **Rotation is a config change.** `CredentialProtection:Keys` is a map, and `ActiveKeyId` names
  the one that seals new records. Keep an old key in the map and records sealed under it keep
  opening; drop it and those records report "re-enter this" rather than failing opaquely.

Configure the master key **outside** source control:

```bash
export CredentialProtection__ActiveKeyId=prod-1
export CredentialProtection__Keys__prod-1=$(openssl rand -base64 32)
```

`appsettings.Development.json` ships a throwaway key so a fresh clone runs. It is committed and
therefore public — **only ever save paper/sandbox broker credentials against it.**

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
    Identity/                      Accounts, sessions, and the encrypted saved-login vault
  Akshaya.Api/                     Minimal APIs, SignalR hub, composition root
apps/web/                          Angular app — renders itself from connector manifests
tests/
  Akshaya.Connectors.TestKit/      Conformance suite + two deliberately different fake brokers
  Akshaya.Architecture.Tests/      The rules that keep the design honest
  Akshaya.Trading.Tests/           Order state machine and risk gate
  Akshaya.Connector.MStock.Tests/  Mapping, session expiry, symbol translation
  Akshaya.Identity.Tests/          Password hashing, the credential cipher, vault isolation
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

- Broker credentials are envelope-encrypted (AES-256-GCM); plaintext exists only in memory during
  a login call. See "Accounts and saved broker logins" above.
- API keys and access tokens never reach the browser — all broker traffic is server-side, and no
  endpoint returns a saved credential value to any caller.
- Account passwords are PBKDF2-HMAC-SHA256 at 600k iterations with a per-hash salt, and the cost
  is stored per hash so it can be raised without locking anyone out.
- Sign-in gives one answer for "no such address" and "wrong password", and spends the same time
  on both, so the form cannot be used to discover which addresses hold credentials here.
- Every API endpoint requires a session by default (a fail-closed fallback authorization policy);
  sign-up and sign-in opt out explicitly.
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
