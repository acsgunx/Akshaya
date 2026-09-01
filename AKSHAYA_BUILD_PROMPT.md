# Akshaya — Universal Multi-Broker Trading Platform: Master Build Prompt

**Version 2 — global, plug-and-play connector architecture**

> **How to use this file.** Keep it at the repo root as `AKSHAYA_BUILD_PROMPT.md` and copy §1–§14 into `CLAUDE.md` so every agent session inherits the rules. To start work, paste this whole file into the agent, then add one line: *"Execute Phase 0. Stop at the phase gate and show me the acceptance evidence."* One phase per session. Never say "build the whole app."

---

## 0. Role and mission

You are a senior full-stack engineer and solution architect building **Akshaya**, a broker-agnostic, market-agnostic trading platform. A user links any broker account in any supported market and trades from one interface.

**The prime directive:** adding a new broker must require **zero changes** to the core, the API surface, or the Angular application — only a new connector package plus its manifest. Everything the platform knows about a broker comes from a **capability manifest** and the connector contract. If a feature can only be expressed in one broker's vocabulary, or one market's assumptions, that is a design bug — fix the abstraction, don't leak the vendor.

Concretely, the platform must accommodate all of these without core changes:

| Market | Brokers |
|---|---|
| India | mStock (first), Zerodha Kite, Fyers, Upstox, Angel One, Dhan, ICICI Breeze, 5paisa |
| Singapore / Asia | Moomoo (Futu), Tiger Brokers, Saxo, IBKR, Phillip POEMS |
| Global | IBKR, Saxo, Alpaca, Tradier, TradeStation |
| Internal | Paper broker (simulation), Backtest broker (replay) |

These brokers differ in almost every dimension — auth model, transport, symbology, currency, asset classes, whether a local gateway daemon is required, even SDK language. §4–§7 exist to absorb all of that.

Work phase by phase (§16). Do not start a phase until the previous gate passes.

---

## 1. Product definition

Akshaya lets a user connect one or more broker accounts across one or more markets and, from a single Angular web app:

1. **Core trading** — link brokers, search instruments globally, watch live quotes, place/modify/cancel orders across asset classes (equity, ETF, futures, options, currency, commodity), and see order book, trade book, positions, holdings, funds and P&L **blended across all linked brokers, normalised into a chosen display currency**.
2. **Market data & charts** — historical OHLCV, intraday charts, TradingView-grade charting with indicators and drawings, multi-market watchlists, live tick streaming, option chains, screeners.
3. **Strategy automation & backtesting** — a rule-based strategy engine, an event-driven backtester with per-market cost models, a paper broker indistinguishable from a live one, and supervised live execution with hard risk limits and a kill switch.
4. **Multi-user SaaS** — organisations, roles, per-user envelope-encrypted broker credentials, plans and entitlements, quota metering, and a tamper-evident audit log.

**Non-goals for v1:** mobile apps, social/copy trading, options payoff builder, and any form of investment advice.

---

## 2. Prescribed stack — follow exactly, do not substitute

**Backend**

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# 14, nullable + implicit usings on, `TreatWarningsAsErrors` |
| API | ASP.NET Core Minimal APIs grouped by module, OpenAPI + Scalar UI |
| Style | Modular monolith, Clean Architecture per module, vertical slices inside modules |
| Mediation | No MediatR — plain handler classes registered by convention |
| Validation | FluentValidation as an endpoint filter |
| Persistence | EF Core 10 → **PostgreSQL 17** (Npgsql), migrations per module, schema per module |
| Time-series | **TimescaleDB** hypertables for candles/ticks (Postgres extension, same instance) |
| Cache / pub-sub | **Redis 7** (StackExchange.Redis) — quote cache, sessions, distributed locks, SignalR backplane, rate-limit buckets |
| Messaging | In-process channels behind `IEventBus`; swappable to RabbitMQ without touching handlers |
| Realtime | **SignalR** hub, Redis backplane, MessagePack protocol |
| Plugin host | `AssemblyLoadContext`-based in-process connector loading **plus** an out-of-process **gRPC** connector protocol (§5) |
| Background work | `BackgroundService` + **Quartz.NET** (instrument master refresh, session pre-warm, EOD reconciliation, FX rates) |
| Resilience | Microsoft.Extensions.Http.Resilience (Polly v8) — retry+jitter, timeout, circuit breaker, per-connector rate limiter |
| Auth | ASP.NET Core Identity + JWT access (15 min) & rotating refresh (30 d); TOTP 2FA mandatory before any live broker link |
| Secrets | Envelope encryption: per-tenant DEK wrapped by a KMS KEK; `IKeyVault` abstraction, dev-only file provider |
| Mapping | Hand-written or Mapperly (source-generated). No AutoMapper |
| Observability | Serilog structured JSON, OpenTelemetry traces/metrics; correlation id + tenant id + connector id on every log line |
| Testing | xUnit v3, FluentAssertions, NSubstitute, Testcontainers (Postgres+Redis), WireMock.Net, Verify |

**Frontend**

| Concern | Choice |
|---|---|
| Framework | Angular (latest stable), **standalone components only**, no NgModules |
| Change detection | Zoneless, `OnPush` everywhere, signals for all component state |
| State | **NgRx SignalStore**, one store per feature |
| Data | `httpResource`/`resource()` for reads, explicit services for commands |
| UI | Angular Material + CDK, dark-first theme via CSS custom properties, Tailwind v4 for layout only |
| Charts | **lightweight-charts** (TradingView) for price; **ECharts** for analytics |
| Realtime | `@microsoft/signalr` wrapped in a `MarketDataService` exposing signals |
| Grids | AG Grid Community, virtualised |
| i18n / format | `Intl` with per-user locale and **per-instrument currency**; never hardcode INR or `en-IN` |
| Testing | Vitest + Angular Testing Library, Playwright E2E |
| Tooling | Nx workspace (`apps/web`, `libs/*`), ESLint flat config, Prettier |

**Infra.** Docker Compose local (postgres+timescale, redis, backend, web, otel-collector, seq, plus gateway sidecars §5.4). GitHub Actions: build → unit → integration → connector conformance → E2E → image → deploy. Health at `/health/live`, `/health/ready`.

---

## 3. Solution layout

```
Akshaya.sln
src/
  Akshaya.Api/                          # composition root: DI, endpoints, middleware, SignalR hubs
  Akshaya.SharedKernel/                 # Result<T>, Money, Currency, Venue, InstrumentKey, IClock
  Akshaya.Connectors.Abstractions/      # ← THE CONTRACT. Zero third-party refs. §4
  Akshaya.Connectors.Sdk/               # base classes, helpers, manifest loader, test kit (§5.6)
  Akshaya.Connectors.Host/              # discovery, load, isolate, supervise, gRPC bridge (§5)
  Akshaya.Connectors.Proto/             # broker_connector.proto + generated gRPC (§5.3)
  connectors/                           # ONE PROJECT PER BROKER — nothing else in the tree changes
    Akshaya.Connector.MStock/
    Akshaya.Connector.Zerodha/
    Akshaya.Connector.Fyers/
    Akshaya.Connector.Moomoo/
    Akshaya.Connector.Ibkr/
    Akshaya.Connector.Paper/
  Modules/
    Identity/  BrokerLink/  Trading/  Portfolio/  MarketData/
    Strategy/  Backtest/  Reference/  Notifications/  Audit/
  Akshaya.Workers/                      # Quartz jobs, tick ingestion, reconciliation, FX
apps/web/                               # Angular (Nx)
libs/                                   # ui-kit, data-access, chart-kit, util-format
tests/                                  # unit / integration / conformance / e2e / architecture
docs/adr/  docs/connectors/             # one ADR per decision; one page per connector
```

Each `Modules/<X>` has `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`. Modules communicate only via integration events or a thin public contracts project — never by referencing another module's `Domain`. Enforce with NetArchTest in `tests/Architecture`; violations fail the build.

**The plug-and-play test, enforced by an architecture test:** nothing outside `connectors/` and `Connectors.Host` may reference a connector project or mention a broker name. Grep for broker names in `Modules/` and `apps/web/` in CI; any hit fails.

---

## 4. The connector contract — design this first, and get it right

Lives in `Akshaya.Connectors.Abstractions`. References nothing but SharedKernel. This is the most important code in the system; spend real time on it.

```csharp
public interface IBrokerConnector
{
    ConnectorManifest Manifest { get; }        // static, declarative — see §5.1
    IConnectorAuth Auth { get; }
    IConnectorOrders Orders { get; }
    IConnectorPortfolio Portfolio { get; }
    IConnectorMarketData MarketData { get; }
    IConnectorStream? Stream { get; }          // null when no live feed
    IConnectorReference Reference { get; }     // instrument master, contract lookup
}
```

Split by concern so a broker can implement a subset and declare the rest unsupported:

- **`IConnectorAuth`** — `BeginAsync(AuthContext)` → returns an `AuthStep` (one of: `Completed`, `RedirectRequired(url, state)`, `ChallengeRequired(kind)` where kind ∈ {SmsOtp, Totp, SecurityQuestion, DeviceApproval}, `GatewayRequired(gatewayId)`), `ContinueAsync(challengeResponse)`, `RefreshAsync`, `RevokeAsync`. This shape must cover: OAuth2 authorization-code (Zerodha, Fyers, Upstox, Saxo, IBKR OAuth2), OAuth1.0a signed requests (IBKR third-party), password+SMS-OTP (mStock), password+TOTP (Angel One), static long-lived token (Dhan), RSA-signed requests (Tiger), and local-gateway session (Moomoo OpenD, IBKR Client Portal Gateway). **Do not add a broker-specific method for any of these** — if one doesn't fit, extend the step machine, not the interface surface.
- **`IConnectorOrders`** — `PlaceAsync`, `ModifyAsync`, `CancelAsync`, `CancelAllAsync`, `GetOrdersAsync`, `GetTradesAsync`, `GetOrderAsync`, `PlaceBasketAsync`, `EstimateMarginAsync`, `EstimateChargesAsync`.
- **`IConnectorPortfolio`** — `GetPositionsAsync`, `GetHoldingsAsync`, `GetBalancesAsync` (returns balances **per currency**, not one number).
- **`IConnectorMarketData`** — `GetQuoteAsync`, `GetLtpAsync`, `GetOhlcAsync`, `GetHistoricalAsync(TimeFrame, DateRange)`, `GetIntradayAsync`, `GetOptionChainAsync`, `GetDepthAsync`.
- **`IConnectorReference`** — `GetInstrumentsAsync(Venue?, AssetClass?)` streaming `IAsyncEnumerable<InstrumentDefinition>`, `ResolveAsync(InstrumentKey)`, `SearchAsync(query)`.
- **`IConnectorStream`** — `ConnectAsync`, `SubscribeAsync(IReadOnlyCollection<InstrumentKey>, StreamMode)`, `UnsubscribeAsync`, `IAsyncEnumerable<StreamEvent> Events` (ticks, depth, **and order/execution updates** — many brokers push fills on the same socket), `ConnectionState`.

### 4.1 Canonical, market-neutral vocabulary

The v1 mistake to avoid: an `Exchange` enum with Indian values. Use open identifiers instead.

```csharp
// Venue is data, not an enum. Seeded from ISO 10383 MIC codes.
public readonly record struct Venue(string Mic);      // XNSE, XBOM, XSES, XNAS, XNYS, XHKG, XSGX, XTKS, XASX...
public readonly record struct Currency(string Code);  // ISO 4217: INR, SGD, USD, HKD, JPY

public enum AssetClass { Equity, Etf, Future, Option, Index, Currency, Commodity, Bond, Fund, Crypto }
public enum Side { Buy, Sell }
public enum OrderType { Market, Limit, Stop, StopLimit, MarketIfTouched, TrailingStop }
public enum TimeInForce { Day, Gtc, Ioc, Fok, Gtd, AtTheOpen, AtTheClose }

// Positioning / product semantics differ wildly by market. Model as flags, not one enum.
[Flags] public enum PositionEffect { None=0, Intraday=1, Delivery=2, Margin=4, CarryForward=8, ShortSell=16 }

public enum OrderStatus { PendingSubmit, Submitted, Open, PartiallyFilled, Filled,
                          Cancelled, Rejected, Expired, Unknown }

public readonly record struct Money(decimal Amount, Currency Currency);
public readonly record struct Quantity(decimal Value);   // decimal — fractional shares exist (IBKR, US brokers)

public readonly record struct InstrumentKey(
    Venue Venue,                 // XNSE
    string Symbol,               // INFY  |  AAPL  |  D05
    AssetClass AssetClass,
    string? Expiry = null,       // ISO date for derivatives
    decimal? Strike = null,
    OptionRight? Right = null);  // Call / Put
```

Additional identity fields on `InstrumentDefinition`: `Isin`, `Figi`, `LotSize`, `TickSize`, `Multiplier`, `Currency`, `TradingHoursId`, `SettlementDays`, and `BrokerSymbols` (the per-connector native symbol strings). See §8.

**Rules:**

1. **`Result<T>` everywhere, never exceptions for control flow.** Every failure maps to a canonical `ConnectorErrorCode` (`InvalidCredentials`, `SessionExpired`, `ReauthRequired`, `GatewayUnavailable`, `InsufficientFunds`, `RateLimited`, `MarketClosed`, `InstrumentNotFound`, `OrderNotFound`, `RiskRejected`, `NotSupported`, `BrokerUnavailable`, `Unknown`) with the raw vendor code and message preserved for support.
2. **`NotSupported` is a first-class outcome.** Any capability a broker lacks returns it — cleanly, immediately, and never as a crash.
3. **Capabilities are data, never `if (broker == "x")`.** §5.1.
4. **Idempotency.** `PlaceAsync` takes a caller-generated `ClientOrderId` (GUID). Persist intent before the call; reconcile after. On timeout never blind-retry — re-fetch orders and match.
5. **Rate limits are per connector and per credential**, enforced inside the SDK base class from manifest values, shared across instances via a Redis token bucket.
6. **Every connector passes the conformance suite** (§5.6) before it is considered to exist.
7. **Money never travels bare.** Any amount crossing a boundary carries its currency. An architecture test bans `decimal`-typed properties named `*Price`, `*Amount`, `*Value` on public contracts.
8. **Time never travels bare.** All timestamps are `DateTimeOffset` in UTC, with the venue's local time derived from the trading-hours calendar (§8.3). No `DateTime.Now` anywhere; an architecture test enforces `IClock`.

---

## 5. Plug-and-play mechanics — how a broker actually gets added

This section is the heart of v2. A new broker is: one manifest + one project (or one external process) + one row in the registry table. Nothing else.

### 5.1 The capability manifest

Each connector ships `connector.manifest.json`, validated against a JSON schema in `Connectors.Abstractions`, loaded at startup and exposed to the frontend at `GET /api/connectors`. The Angular app renders itself from this — the order ticket, the link wizard, the screens all read it.

```jsonc
{
  "id": "mstock",
  "displayName": "m.Stock (Mirae Asset)",
  "vendor": "Mirae Asset Capital Markets",
  "contractVersion": "1.0",
  "connectorVersion": "1.0.0",
  "hosting": "in-process",              // in-process | out-of-process | gateway
  "gateway": null,                       // see §5.4 when hosting=gateway
  "jurisdictions": ["IN"],
  "venues": ["XNSE", "XBOM", "XNSE-FO", "XBOM-FO"],
  "currencies": ["INR"],
  "assetClasses": ["Equity", "Etf", "Future", "Option", "Index"],
  "auth": {
    "model": "password-otp",             // oauth2 | oauth1a | password-otp | password-totp
                                          // | static-token | rsa-signed | gateway-session
    "challenges": ["SmsOtp", "Totp"],
    "sessionLifetime": "PT12H",
    "expiresAtVenueMidnight": true,
    "refreshSupported": true,
    "credentialFields": [
      { "key": "username",   "label": "Client ID", "secret": false },
      { "key": "password",   "label": "Password",  "secret": true  },
      { "key": "api_key",    "label": "API Key",   "secret": true  },
      { "key": "totp_seed",  "label": "TOTP Seed", "secret": true, "optional": true }
    ]
  },
  "orders": {
    "types": ["Market", "Limit", "Stop", "StopLimit"],
    "timeInForce": ["Day", "Ioc"],
    "positionEffects": ["Intraday", "Delivery", "Margin", "CarryForward"],
    "varieties": ["Regular", "AfterMarket"],
    "modifiable": ["quantity", "price", "triggerPrice", "orderType", "timeInForce"],
    "fractionalQuantity": false,
    "shortSellEquity": false,
    "basket": { "supported": true, "maxLegs": 20 },
    "bracket": false, "cover": false, "gtt": false,
    "marginEstimate": true, "chargesEstimate": false
  },
  "marketData": {
    "streaming": true, "streamModes": ["Ltp", "Quote", "Full"],
    "depthLevels": 5, "historical": true, "optionChain": true,
    "maxStreamSubscriptions": 1000
  },
  "rateLimits": [
    { "scope": "orders", "perSecond": 30, "perMinute": 250 },
    { "scope": "data",   "perSecond": 1,  "perMinute": 1000 },
    { "scope": "quotes", "perSecond": 20 }
  ],
  "sandbox": { "available": true, "notes": "Separate sandbox key required" },
  "compliance": { "algoApprovalRequired": true, "regulator": "SEBI" }
}
```

The manifest is **the only place** broker differences are expressed declaratively. If the UI or the core needs to branch on broker behaviour, the answer is always a new manifest field, never a hardcoded check.

### 5.2 Discovery and loading

- `Connectors.Host` scans a configured plugin directory plus referenced assemblies, reads each manifest, validates it, checks `contractVersion` compatibility (semver, host supports N and N-1), and registers the connector in a `ConnectorCatalog`.
- In-process connectors load into their own `AssemblyLoadContext` (collectible) so a connector's dependency versions cannot collide with the host's or with each other — Newtonsoft v11 in one connector must not break another.
- `IConnectorFactory.Create(connectorId, session)` returns a scoped instance bound to a decrypted session, wrapped in decorators applied by the host, not by the connector author: rate limiter → resilience → metrics/tracing → audit → error normalisation. **A connector author writes only broker logic.**
- A connector can be enabled/disabled per tenant and per plan from the DB, without redeploying.

### 5.3 Out-of-process connectors (the real plug-and-play escape hatch)

Some brokers cannot be reached from C# comfortably — a Python-only SDK, a native gateway, an exotic protocol. Define `broker_connector.proto` mirroring §4 exactly, and let a connector run as a **separate process or container** speaking gRPC to the host. Streaming RPCs carry ticks and order updates.

```proto
service BrokerConnector {
  rpc GetManifest    (Empty)              returns (Manifest);
  rpc BeginAuth      (AuthRequest)        returns (AuthStep);
  rpc ContinueAuth   (ChallengeRequest)   returns (AuthStep);
  rpc PlaceOrder     (PlaceOrderRequest)  returns (OrderAck);
  rpc ModifyOrder    (ModifyOrderRequest) returns (OrderAck);
  rpc CancelOrder    (CancelOrderRequest) returns (OrderAck);
  rpc GetOrders      (AccountRequest)     returns (OrderList);
  rpc GetPositions   (AccountRequest)     returns (PositionList);
  rpc GetBalances    (AccountRequest)     returns (BalanceList);
  rpc GetInstruments (InstrumentQuery)    returns (stream InstrumentDefinition);
  rpc GetHistorical  (HistoryRequest)     returns (CandleList);
  rpc Subscribe      (stream SubRequest)  returns (stream StreamEvent);
  rpc Health         (Empty)              returns (HealthStatus);
}
```

The host wraps a gRPC channel in the same `IBrokerConnector` interface, so **the core cannot tell an in-process connector from a remote one.** This is what makes the platform genuinely broker-agnostic: a third party can write a connector in Python, Java or Go and drop it in as a container. Ship a reference out-of-process connector in Python as proof.

### 5.4 Gateway-backed brokers (Moomoo, IBKR) — plan for this now

Two of the named targets do not offer a plain cloud REST API:

- **Moomoo / Futu OpenAPI** requires **OpenD**, a gateway daemon running on your machine or server, exposing a TCP/protobuf interface; SDKs exist for Python, Java, C#, C++ and JavaScript. Markets: HK, US, SG, CN-Connect, JP, AU.
- **IBKR** offers the Web API either through a locally-run **Client Portal Gateway** (browser-based login, `/tickle` keepalive ~1 req/sec, session dies on idle) or via **OAuth 1.0a** for third-party access, which requires a formal compliance onboarding that typically takes **8–14 weeks**. Individual first-party OAuth 2.0 is newer and still limited. Global rate limit around 10 req/sec, with order endpoints far stricter.

So the manifest supports `"hosting": "gateway"` with a `gateway` block declaring the container image, ports, health probe, credential injection, and **whether the gateway is per-tenant or shared**. The host runs one supervised gateway sidecar per credential, with lifecycle management, health checks, automatic restart, and a `GatewayUnavailable` error surfaced to the UI as an actionable state rather than a mystery failure.

Design implications to bake in from Phase 2, not retrofit:
- Auth must support `GatewayRequired` and `DeviceApproval` steps.
- Sessions must support **keepalive tasks** (`/tickle`) declared in the manifest.
- Connector health is a first-class concept, polled and shown in the UI.
- Capacity planning is per-credential, not per-app — a gateway per user is expensive; document the cost model in `docs/connectors/ibkr.md` before building it.

**Flag to the user before Phase 8:** IBKR third-party access needs the compliance onboarding started months ahead. Don't discover this at integration time.

### 5.5 Symbol translation

Each connector implements `ISymbolTranslator`: canonical `InstrumentKey` ⇄ the broker's native symbol/token. mStock wants `INFY-EQ` on `NSE`; Zerodha wants `INFY` plus an instrument token; IBKR wants a `conid`; Moomoo wants `HK.00700` / `US.AAPL` / `SG.D05`. Translation lives **only** in the connector, backed by the shared instrument master (§8.1). A translation failure is `InstrumentNotFound`, never a silently wrong order.

### 5.6 The conformance kit — a connector is not "done" until this is green

Build `Akshaya.Connectors.TestKit` once. Every connector project inherits `ConnectorConformanceTests<TConnector>`:

- **Manifest validation** — schema-valid, declared capabilities match implemented behaviour (a connector claiming `bracket: true` must implement it; claiming `false` must return `NotSupported`).
- **Round-trip mapping** — every declared order type, TIF, and position effect maps out and back losslessly. Property-based, not example-based.
- **Symbol round-trip** — canonical → native → canonical for a sampled 1,000 instruments from the real master.
- **Error normalisation** — recorded vendor error payloads map to the right canonical code. One golden file per vendor error.
- **Idempotency** — simulated timeout on place produces exactly one order after reconciliation.
- **Rate limiting** — bursts are shaped to manifest limits.
- **Session lifecycle** — expiry produces `ReauthRequired`, never a silent drop; refresh works where declared.
- **Streaming** — subscribe/unsubscribe/reconnect/resubscribe; no leaked upstream subscriptions.
- Fixtures are **WireMock recordings of real sandbox responses** (or recorded gRPC/protobuf frames), checked in, so CI needs no credentials.

Plus a documented manual **sandbox smoke test** per connector: link → place → modify → cancel → verify in the broker's own UI. Record the result in `docs/connectors/<id>.md` with a date.

---

## 6. Reference implementation: mStock (Type A)

Build this first, as the connector everything else is measured against. Verify against `https://tradingapi.mstock.com/docs/v1/typeA/` before coding; record any drift in an ADR.

- **Base** `https://api.mstock.trade`, streaming `wss://ws.mstock.trade`.
- **Headers:** `X-Mirae-Version: 1`, `Authorization: token {api_key}:{access_token}`, `X-PrivateKey: {api_key}`; JSON generally, `application/x-www-form-urlencoded` for login/session calls.
- **Auth:** `POST /openapi/typea/connect/login` (username, password) → OTP to registered mobile → `POST /openapi/typea/session/token` (api_key, request_token=OTP, checksum) → `access_token`, `refresh_token`, `public_token`, `enctoken` and the profile's allowed exchanges/products/order types. With TOTP enabled, `POST /openapi/typea/session/verifytotp` (api_key, totp) replaces the OTP step. `GET /openapi/typea/logout` destroys the token.
- **Token lifetime:** ~12 hours *or* midnight IST, whichever first; API keys ~13 months. Model explicitly — session monitor, pre-expiry warning in the UI, nightly staleness job. Never lose an order to a dead token.
- **Orders:** `POST /openapi/typea/orders/{variety}` (`reg` | `amo`), `PUT|DELETE /openapi/typea/orders/regular/{orderId}`, `POST /openapi/typea/orders/cancelall`, `GET /openapi/typea/orders`, `GET /openapi/typea/tradebook`, `GET /openapi/typea/trades`, `GET /openapi/typea/order/details?order_no=&segment=` (`E`/`D`). Fields: `tradingsymbol` (`INFY-EQ`), `exchange`, `transaction_type`, `order_type`, `quantity`, `product`, `validity`, `price`, `trigger_price`, `disclosed_quantity`.
- **Market data:** `GET /openapi/typea/instruments/scriptmaster` (CSV master — ingest daily pre-open, diff it), `.../instruments/quote/ltp`, `.../instruments/quote/ohlc`, historical/intraday chart endpoints, option chain.
- **Rate limits → manifest:** orders ≈ 30/s and 250/min; data ≈ 1/s and 1000/min; quotes ≈ 20/s.
- **Mapping table** in one file, `MStockMaps.cs`, exhaustively tested both directions: `Delivery↔CNC`, `Intraday↔MIS`, `Margin↔MTF`, `CarryForward↔NRML`, `Stop↔SL`, `Market+Stop↔SL-M`, `Regular↔reg`, `AfterMarket↔amo`, `XNSE↔NSE`, `XBOM↔BSE`.
- Credentials and API keys **never** reach the browser. All broker traffic is server-side.

Second and third connectors (Phase 8) are chosen to stress different axes on purpose: **Zerodha Kite** (OAuth-style request-token + checksum, binary WebSocket, same market) and **Moomoo** (different market, different currency, gateway daemon, protobuf). If the abstraction survives both, it will survive the rest.

---

## 7. Broker landscape — build the manifest, not the exception

Use this as the design target set. Confirm each against current vendor docs at implementation time; APIs change.

| Broker | Market | Auth model | Transport | Notable constraint |
|---|---|---|---|---|
| mStock | IN | password + SMS OTP / TOTP | REST + WS | Token dies at midnight IST |
| Zerodha Kite | IN | request-token + SHA-256 checksum | REST + binary WS | Paid monthly per app; daily re-login |
| Fyers | IN | OAuth2 auth-code + app-id hash | REST + WS | Order updates on socket |
| Upstox | IN | OAuth2 | REST + WS | Daily token expiry |
| Angel One SmartAPI | IN | password + TOTP → JWT + feed token | REST + binary WS | Separate feed token |
| Dhan | IN | static long-lived token | REST + WS | Simplest auth; treat as the easy case |
| Moomoo / Futu | SG, HK, US, JP, AU | local **OpenD** gateway session | TCP + protobuf | Gateway daemon required; multi-currency |
| IBKR | Global | OAuth1.0a (3rd-party, long onboarding) or Client Portal Gateway | REST + WS | `/tickle` keepalive; strict rate limits; fractional shares |
| Saxo | SG, Global | OAuth2 | REST + streaming | 24h token; sim environment available |
| Tiger | SG, Global | RSA-signed requests | REST + WS | Signature scheme, not bearer tokens |
| Paper | — | none | in-proc | Must be indistinguishable from live |

Every row above must be expressible purely as a manifest plus a connector project. When you design §4, hold each row against it and check.

---

## 8. Global market model

### 8.1 Instrument master and symbology

- A **single canonical instrument master** in Postgres, not one per broker. Keys: `Venue` (MIC) + `Symbol` + `AssetClass` (+ expiry/strike/right). Enrich with `Isin`, `Figi`, `LotSize`, `TickSize`, `Multiplier`, `Currency`.
- A `broker_symbol_map` table links canonical instruments to each connector's native identifiers (symbol string, token, conid). Populated by each connector's `GetInstrumentsAsync` ingest job, reconciled nightly, with unmatched instruments reported rather than silently dropped.
- Cross-listing is real: D05 on SGX and Infosys ADR on NYSE are different instruments; AAPL through IBKR and through Moomoo is the same one. The `Isin`/`Figi` link is what lets a blended portfolio aggregate correctly.
- Search: full-text + trigram, ranked, venue- and asset-class-filterable, `<50 ms`, returning canonical instruments annotated with which of the user's linked brokers can trade them.

### 8.2 Currency and FX

- Every `Money` carries its currency. Positions, holdings, balances and P&L are computed **natively per currency first**, then converted for display.
- A `IFxRateProvider` with a daily+intraday rate job (and manual override for testing) produces the display-currency view. Show both: native and converted, never converted alone.
- Realised P&L must record the FX rate at the time of the trade — converting historic P&L at today's rate is a bug that will silently misreport.

### 8.3 Trading calendars and time

- A `TradingCalendar` service per venue: session hours (including pre-open, post-close, and lunch breaks for HKEX/TSE), holidays, half-days, timezone with DST. Seed from a maintained data file per venue; allow per-venue override.
- Users see times in their own timezone with the venue's local time available. Market-open/closed state is per venue, shown in the UI, and enforced in the risk gate.
- All storage in UTC `DateTimeOffset`. `IClock` everywhere, no `DateTime.Now` — architecture-tested.

### 8.4 Charges and cost model

Fees are per market and per broker, and a backtest without them is a lie. Implement `IChargeSchedule` per connector, data-driven:

- **India** — brokerage, STT/CTT, exchange transaction charges, SEBI fees, stamp duty, GST, DP charges on delivery sells.
- **Singapore** — brokerage/minimum commission, SGX clearing fee, trading fee, SGX settlement, GST.
- **US** — commission (or zero), SEC fee on sells, FINRA TAF, per-contract options fees.
- Plus **FX conversion spread** when trading a currency you don't hold — often the largest hidden cost in cross-border trading, and routinely forgotten.

Every charge schedule needs a hand-computed worked example in its test file.

---

## 9. Security and secrets

- Broker credentials (usernames, passwords, api keys, secrets, TOTP seeds, RSA private keys) stored **envelope-encrypted**: AES-256-GCM with per-tenant DEK, DEK wrapped by a KMS-held KEK. Plaintext exists only in memory during a login call. Every decryption is audited.
- Access tokens live in Redis with a TTL matching real expiry, keyed `session:{tenant}:{user}:{connector}:{account}`; never in the browser, never plaintext in the DB.
- Web app holds the Akshaya access token in memory only; refresh token in an `HttpOnly; Secure; SameSite=Strict` cookie, with CSRF protection on cookie-auth routes.
- TOTP 2FA mandatory before linking a live broker or arming a strategy.
- Out-of-process and gateway connectors are **untrusted by default**: run in their own container with no ambient credentials, receive only the secrets they need over mTLS gRPC, have resource limits and network egress restricted to the broker's domains, and are health-supervised.
- Every order-affecting action writes an append-only `AuditEvent` (actor, tenant, connector, ip, ua, before/after, correlation id) with a hash chain.
- Strict CSP, HSTS, per-IP and per-user rate limits, Serilog redaction policy by attribute, dependency scanning in CI.

---

## 10. Trading module rules

- Orders are an explicit **state machine**: `Draft → RiskChecked → Submitted → Acknowledged → (PartiallyFilled) → Filled | Cancelled | Rejected | Expired`. Illegal transitions throw. Persist every transition with the raw broker payload.
- A **pre-trade risk gate**, configurable per user/org **and per venue**: max order value (in a normalised currency), max quantity, max open positions, daily loss limit, instrument allow/deny, venue market-hours check, price-band sanity vs LTP (fat-finger guard), and a per-tenant **global kill switch**.
- **Reconciliation** is mandatory: poll each connector's order book on an interval and after every stream reconnect, diff against local state, raise `OrderDrifted`. The broker is always the source of truth.
- Broker rejections surface verbatim alongside the canonical code.
- Multi-broker routing in v1 is **explicit, not smart**: the user picks which linked account an order goes to; when an instrument is tradable at several, show the choice with each account's buying power. No auto-routing, no best-execution claims — that is a regulated activity.

---

## 11. Market data and realtime

- **Tick pipeline:** one upstream connection per credential (never one per browser tab) → normalise to `Tick` → Redis → SignalR fan-out. **Conflate:** at most 4 updates/sec per instrument per client; drop intermediates, never queue. Backpressure must not stall ingest.
- Clients subscribe to instrument sets; the server tracks per-connection subscriptions and unsubscribes upstream when the last subscriber leaves. Respect each manifest's `maxStreamSubscriptions`.
- **Candles** in a TimescaleDB hypertable with continuous aggregates (1m→5m→15m→1h→1d), backfilled on demand from connectors, gap-detected, cached hard — historical endpoints are the tightest limits you have.
- Reconnect with exponential backoff + jitter; on reconnect re-subscribe **and re-sync orders and positions** before showing "live". A stale-data banner is mandatory when any feed is degraded, per broker.
- The dashboard shows multiple markets at different session states simultaneously — design for "SGX open, NSE closed, US pre-market" as the normal case, not an edge case.

---

## 12. Strategy engine and backtesting

- A strategy is a **declarative, versioned rule set** (JSON): universe (by venue/asset class/watchlist), timeframe, indicators, entry/exit conditions, stop, target, trailing, position sizing, max concurrent positions. A C# `IStrategy` exists too, but the UI-authored DSL is the primary path.
- Indicators (SMA, EMA, WMA, RSI, MACD, Bollinger, ATR, ADX, Stochastic, VWAP, Supertrend) are **incremental/streaming** so identical code runs in backtest and live. Property-test against a reference implementation.
- The **backtester is event-driven**, bar-by-bar, explicit clock, with slippage and the full §8.4 cost model for the instrument's market. Enforce **no lookahead** — a bar's close is unavailable until it closes; assert it in tests.
- Report: equity curve, CAGR, max drawdown, Sharpe, Sortino, Calmar, win rate, profit factor, expectancy, avg holding period, trade list, monthly heatmap, **and per-currency breakdown** for multi-market strategies. Export CSV and PDF.
- `Connector.Paper` implements the same `IBrokerConnector` with a simulated matching engine on live or replayed ticks. **Backtest → paper → live must need zero strategy code changes.** This is the acceptance test for the whole abstraction.
- **Live execution is supervised by default.** A strategy emits a `Signal`; unless auto-execution is explicitly armed (2FA-gated, mandatory daily loss cap, kill switch), the signal becomes a notification with a one-click order ticket.
- **Compliance is jurisdictional and belongs in the manifest.** India (SEBI) has specific requirements for retail algorithmic order flow — broker-approved and exchange-registered strategies, unique algo identifiers, order-rate thresholds. Singapore (MAS) and the US differ again. So: put live automation behind a per-connector feature flag driven by `compliance.algoApprovalRequired`, carry an algo-identifier field in the order contract, keep the full audit trail, and maintain `docs/compliance/<jurisdiction>.md`. Never present output as investment advice; ship a disclaimer surface.

---

## 13. Multi-tenant SaaS

- `Organisation → Users → BrokerLinks (n per user, across markets)`. Every query tenant-scoped by an EF Core global filter; an architecture test asserts no entity escapes it.
- Roles: Owner, Admin, Trader, Viewer, Auditor — policy-based authorisation, one policy per capability (`Orders.Place`, `Strategy.Arm`, `Connector.Link`, `Audit.Read`). Never role-name checks in endpoints.
- Plans and entitlements as data: max broker links, **which connectors are enabled**, max strategies, backtests/month, live execution on/off, data retention. A metering service enforces quotas as domain errors.
- Billing behind `IBillingProvider` (implementation in Phase 9; the seam exists from Phase 1). Note that a gateway-backed connector has a real per-user infrastructure cost — the plan model must be able to price that.

---

## 14. Frontend specification

Screens: Login/2FA · **Connector catalogue** (browse available brokers, rendered from manifests) · Broker Link wizard (**generic — renders credential fields, OAuth redirect, OTP/TOTP challenge, or gateway setup from the manifest's auth block**) · Dashboard (blended multi-currency P&L, per-market session status) · Watchlists · Instrument detail + chart · Order Ticket · Orders · Trades · Positions · Holdings · Funds (per currency) · Strategy Builder · Backtest report · Paper console · Alerts · Audit log · Settings/Org/Billing.

Rules:

- **The order ticket is one component for every broker in every market.** It renders order types, TIFs, position effects, varieties, quantity precision (fractional or not) and currency from the manifest. Adding a broker must not touch it. This is the single clearest test of whether the architecture worked.
- **The link wizard is one component too**, driven by the manifest's auth block. If adding Fyers means writing a Fyers login screen, the abstraction failed.
- Trading-critical state is always visible: per-connector connection status, session-expiry countdown, gateway health, kill-switch state, stale-data banner, venue open/closed.
- Confirm-before-submit on every order, with estimated value and charges **in the instrument's currency**. Destructive actions need typed confirmation.
- **Optimistic UI is banned for orders.** Show `Submitting…` until the broker acknowledges.
- Dark theme default. Green/red for up/down with a colour-blind-safe alternate. Tabular figures, locale-aware grouping (lakh/crore for INR, thousands elsewhere), currency symbol always shown. Price columns must not jitter on update.
- WCAG 2.2 AA, full keyboard operation, order-ticket shortcuts (`B` buy, `S` sell, `Esc` cancel).
- Budgets: initial route JS < 250 KB gzipped, LCP < 2 s on 4G, a 500-row live grid with no dropped frames.

---

## 15. Working agreements for the agent

1. **Ask before assuming.** Ambiguity in broker behaviour, market convention or regulation → stop and ask. Trading bugs cost real money.
2. **Tests first for anything touching money** — order mapping, risk gate, P&L, FX, charges, indicators. TDD those. >85% coverage on Domain and Application.
3. **Small, reviewable commits**, conventional messages, one logical change each.
4. **An ADR per significant decision** in `docs/adr/NNNN-title.md`, including the ones this document already made — record the reasoning, not just the choice.
5. **A page per connector** in `docs/connectors/<id>.md`: auth flow, quirks, rate limits, sandbox notes, last smoke-test date, known divergences from the contract.
6. **No secrets in the repo.** `.env.example` only.
7. **No silent catch.** Handle meaningfully or log with context and rethrow.
8. **Verify before claiming done.** Run build, tests, lint; show the real output. "Should work" is not evidence.
9. **When tempted to special-case a broker in the core, stop.** Add a manifest field or extend the contract instead, and write the ADR explaining why the contract needed to change.

---

## 16. Phased build plan

Each phase ends at a **gate**. Produce the evidence, stop, wait for review.

**Phase 0 — Foundations.** Solution + Nx workspace, Docker Compose (postgres/timescale, redis, otel, seq), SharedKernel (`Result<T>`, `Money`, `Currency`, `Venue`, `InstrumentKey`, `IClock`), Serilog + OTel, health checks, CI, architecture-test project, ADR 0001.
*Gate:* `docker compose up` runs; `/health/ready` green; CI passes; architecture tests execute; the no-`DateTime.Now` and no-bare-decimal-money rules already fail a deliberately planted violation.

**Phase 1 — Identity & tenancy.** Users, orgs, roles, JWT + refresh rotation, TOTP 2FA, policy authorisation, tenant filters, audit skeleton, `IBillingProvider` seam. Angular shell: login, 2FA, layout, interceptor, guards.
*Gate:* register → enable TOTP → login → denied a capability the role lacks, proven by integration and Playwright tests.

**Phase 2 — Connector contract & SDK.** `Connectors.Abstractions` per §4, manifest schema, `Connectors.Sdk` (base classes, decorators, rate limiter, error normaliser), `Connectors.Host` (discovery, `AssemblyLoadContext` isolation, catalogue, `/api/connectors`), `Connectors.TestKit` with the full conformance suite, and **two fake connectors that deliberately differ** — one OAuth2/multi-currency/fractional, one password-OTP/single-currency/lot-based — both passing conformance.
*Gate:* both fakes pass identical conformance tests; architecture test proves Abstractions has zero external references; `/api/connectors` returns both manifests; a deliberately invalid manifest is rejected with a clear error. **Review this phase hardest — everything else depends on it.**

**Phase 3 — mStock connector.** Full §6 implementation against the Phase-2 SDK, with WireMock fixtures.
*Gate:* conformance suite green with no connector-specific test exceptions; documented sandbox smoke test places and cancels a real order; `docs/connectors/mstock.md` written.

**Phase 4 — Credential vault & linking.** Envelope encryption, KMS abstraction, generic link/unlink flow, session monitor, keepalive tasks, re-auth prompts. Angular: connector catalogue + **manifest-driven** link wizard.
*Gate:* a user links mStock end to end through a wizard containing **no mStock-specific code**; credentials unreadable in the DB; forced token expiry produces a re-auth prompt and never a lost order.

**Phase 5 — Core trading.** Order state machine, risk gate, place/modify/cancel/basket, order & trade book, positions, holdings, balances, multi-currency blended P&L, reconciliation job, kill switch. Angular: search, order ticket, grids.
*Gate:* full lifecycle against fixtures and sandbox; a test per risk rule; killing the app mid-place leaves no phantom order after reconciliation; the order ticket contains zero broker names.

**Phase 6 — Market data & charts.** Canonical instrument master + broker symbol map, tick ingestion, Redis fan-out, SignalR with conflation, subscription management, Timescale candles + continuous aggregates, backfill, option chain. Angular: watchlists, lightweight-charts, live LTP, per-venue session status.
*Gate:* 500 instruments streaming to 50 simulated clients, stable memory, no dropped frames; reconnect re-syncs; conflation limit proven under load.

**Phase 7 — Strategy, backtest, paper.** Rule DSL + versioning, streaming indicators, event-driven backtester with per-market charge schedules, metrics and reports, `Connector.Paper`, supervised signals with arming flow. Angular: strategy builder, backtest report, paper console.
*Gate:* one strategy runs unchanged across backtest → paper → live-supervised; no-lookahead test passes; each charge schedule reconciles against a hand-computed worked example.

**Phase 8 — The abstraction trial: three connectors at once.** Implement **Zerodha** (same market, different auth and binary socket), **Moomoo** (different market, currency, gateway daemon, protobuf), and the **reference out-of-process Python connector** over gRPC. Also stand up the gateway sidecar supervisor (§5.4) and start IBKR compliance onboarding in parallel — it takes months.
*Gate — the real test of the whole design:* all three pass conformance; **`Connectors.Abstractions` changed only by adding manifest fields, and `apps/web` and `Modules/` did not change at all** (prove it with a diff); a user with linked mStock + Moomoo sees one correct blended multi-currency portfolio. Any other change means Phase 2 failed — write the ADR explaining what and why.

**Phase 9 — IBKR + global hardening.** IBKR connector (whichever auth path cleared), cross-listing aggregation via ISIN/FIGI, FX provider, full trading-calendar set, per-venue risk limits.
*Gate:* a portfolio spanning three currencies and four venues reports correctly against hand-computed figures.

**Phase 10 — SaaS hardening & launch.** Plans, entitlements (including per-connector enablement), metering, billing, notifications, audit UI, admin console, connector marketplace page, backups, DR runbook, pen-test fixes, docs, onboarding.
*Gate:* paid signup end to end in staging; quota exhaustion degrades gracefully; `docs/security.md` checklist fully ticked.

---

## 17. Start here

Confirm you have understood §4 (the contract), §5 (plug-and-play mechanics) and §16 (phases). Then:

1. Restate the Phase 0 deliverables in your own words and flag anything you'd design differently — **I want the disagreement before the code.**
2. Specifically stress-test §4 and §5 against the §7 broker table: name any broker whose auth model, transport, symbology or asset class does **not** fit the contract as written, and propose the fix now rather than at Phase 8.
3. List decisions this document doesn't settle.
4. On my go-ahead, execute **Phase 0 only** and present the gate evidence.

Do not write application features before the foundations gate passes.
