# Akshaya — Multi-Broker Trading Platform: Master Build Prompt

> **How to use this file.** Keep it at the repo root as `AKSHAYA_BUILD_PROMPT.md` and also copy §1–§12 into `CLAUDE.md` so every agent session inherits the rules. To start work, paste this whole file into the agent, then add one line: *"Execute Phase 0. Stop at the phase gate and show me the acceptance evidence."* Do one phase per session. Never say "build the whole app."

---

## 0. Role and mission

You are a senior full-stack engineer and solution architect building **Akshaya**, a broker-agnostic retail/prosumer trading platform for Indian markets.

The single most important architectural requirement: **no broker-specific type, string, or HTTP call may ever escape the adapter layer.** The first broker is **mStock (Mirae Asset Capital Markets)**; Zerodha Kite, Angel One SmartAPI, Upstox, Dhan and Fyers follow. If a feature can only be expressed in mStock's vocabulary, that is a design bug — fix the abstraction, don't leak the vendor.

Work phase by phase (§13). Do not start a phase until the previous phase's gate passes.

---

## 1. Product definition

Akshaya lets a user connect one or more broker accounts and, from a single Angular web app:

1. **Core trading** — link a broker, search instruments, watch live quotes, place/modify/cancel orders (equity delivery, intraday, F&O, MTF, AMO, basket), and see order book, trade book, positions, holdings, funds and realised/unrealised P&L across all linked brokers in one blended view.
2. **Market data & charts** — historical OHLCV candles, intraday charts, TradingView-grade charting with indicators and drawings, multiple watchlists, live tick streaming, option chain, top gainers/losers.
3. **Strategy automation & backtesting** — a rule-based strategy engine (entry/exit/stop/target/position-size), an event-driven backtester over historical candles, a paper-trading broker that is indistinguishable from a live one to the rest of the system, and supervised live execution with hard risk limits and a kill switch.
4. **Multi-user SaaS** — registration, roles (Owner/Admin/Trader/Viewer/Auditor), organisations, per-user envelope-encrypted broker credentials, subscription plans and entitlements, quota metering, and a tamper-evident audit log of every order-affecting action.

**Non-goals for v1:** mobile apps, options strategy builder with payoff charts, social/copy trading, and any form of investment advice or recommendation.

---

## 2. Prescribed stack — follow exactly, do not substitute

**Backend**

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# 14, nullable + implicit usings on, `TreatWarningsAsErrors` |
| API | ASP.NET Core Minimal APIs grouped by module, OpenAPI via built-in `Microsoft.AspNetCore.OpenApi` + Scalar UI |
| Style | Modular monolith, Clean Architecture per module, vertical slices inside modules |
| Mediation | No MediatR — plain handler classes registered by convention (keeps the dependency graph readable and licence-free) |
| Validation | FluentValidation, wired as an endpoint filter |
| Persistence | EF Core 10 → **PostgreSQL 17** (Npgsql). Migrations per module, separate schemas |
| Time-series | **TimescaleDB** hypertables for candles and ticks (it is a Postgres extension — same instance, no second database) |
| Cache / pub-sub | **Redis 7** via StackExchange.Redis — quote cache, session cache, distributed locks, SignalR backplane |
| Messaging | In-process channels for v1 behind `IEventBus`; the interface must allow swapping in RabbitMQ later without touching handlers |
| Realtime | **SignalR** hub with Redis backplane and MessagePack protocol |
| Background work | .NET `BackgroundService` + **Quartz.NET** for cron (instrument master refresh, EOD reconciliation, session pre-warm) |
| Resilience | Microsoft.Extensions.Http.Resilience (Polly v8) — retry with jitter, timeout, circuit breaker, rate limiter per broker |
| Auth | ASP.NET Core Identity + JWT access (15 min) & rotating refresh (30 d), TOTP 2FA mandatory for any account with a live broker link |
| Secrets | Envelope encryption: per-tenant DEK wrapped by a KEK in Azure Key Vault / AWS KMS; `IKeyVault` abstraction with a dev-only file provider |
| Mapping | Hand-written mapping methods or Mapperly (source-generated). No AutoMapper |
| Logging | Serilog → structured JSON, OpenTelemetry traces/metrics, correlation id + tenant id on every log line |
| Testing | xUnit v3, FluentAssertions, NSubstitute, Testcontainers (Postgres + Redis), WireMock.Net for broker HTTP fakes, Verify for snapshot tests |

**Frontend**

| Concern | Choice |
|---|---|
| Framework | Angular (latest stable), **standalone components only**, no NgModules |
| Change detection | Zoneless, `ChangeDetectionStrategy.OnPush` everywhere, signals for all component state |
| State | **NgRx SignalStore** — one store per feature; no global god-store |
| Data fetching | `httpResource` / `resource()` for reads; explicit services for commands |
| Routing | Lazy standalone routes, functional guards and resolvers |
| UI | Angular Material + CDK, custom dark-first theme via CSS custom properties, Tailwind v4 for layout utilities only |
| Charts | **lightweight-charts** (TradingView) for price/candles; **ECharts** for analytics and P&L visuals |
| Realtime | `@microsoft/signalr` wrapped in a `MarketDataService` that exposes signals, not observables, to components |
| Tables | AG Grid Community for order book / positions / holdings (virtualised, 10k+ rows) |
| Forms | Typed reactive forms; a single `OrderTicketComponent` driven by broker capability metadata |
| i18n / format | `Intl` with `en-IN`, INR currency, lakh/crore formatting, IST everywhere |
| Testing | Vitest + Angular Testing Library, Playwright for E2E |
| Tooling | Standalone ESLint flat config, Prettier, Nx workspace (`apps/web`, `libs/*`) |

**Infra**

Docker Compose for local (postgres+timescale, redis, backend, web, otel-collector, seq). GitHub Actions CI: build → unit → integration (Testcontainers) → E2E → container image → deploy. Health checks at `/health/live` and `/health/ready`. Feature flags via a simple `IFeatureManager` implementation backed by the DB.

---

## 3. Solution layout

```
Akshaya.sln
src/
  Akshaya.Api/                      # composition root: DI, endpoints, middleware, SignalR hubs
  Akshaya.SharedKernel/             # Result<T>, Money, Quantity, Price, Symbol, Instrument ids, DomainEvent, IClock
  Akshaya.Brokers.Abstractions/     # ← THE CONTRACT. Zero third-party refs. See §4
  Akshaya.Brokers.MStock/           # mStock Type A adapter (§5)
  Akshaya.Brokers.Paper/            # simulated broker used by backtest + paper trading
  Akshaya.Brokers.Zerodha/          # Phase 8 — proves the abstraction
  Modules/
    Identity/                       # users, orgs, roles, 2FA, plans, entitlements
    BrokerLink/                     # credential vault, session lifecycle, capability discovery
    Trading/                        # orders, executions, order state machine, risk gate
    Portfolio/                      # positions, holdings, funds, blended P&L
    MarketData/                     # instrument master, quotes, candles, option chain, tick fan-out
    Strategy/                       # rule engine, signals, live supervision
    Backtest/                       # historical replay, metrics, reports
    Notifications/                  # in-app, email, webhook, push
    Audit/                          # append-only immutable log
  Akshaya.Workers/                  # Quartz jobs, tick ingestion, reconciliation
apps/web/                           # Angular (Nx)
libs/                               # ui-kit, data-access, chart-kit, util-format
tests/                              # unit / integration / e2e / architecture
docs/adr/                           # one ADR per significant decision
```

Each `Modules/<X>` contains `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`. Modules talk to each other **only** through published integration events or a thin public contracts project — never by referencing another module's `Domain`. Enforce this with NetArchTest architecture tests in `tests/Architecture`; a violating build must fail.

---

## 4. The broker abstraction — design this first, and get it right

Put this in `Akshaya.Brokers.Abstractions`. It must reference nothing but the SharedKernel.

```csharp
public interface IBrokerAdapter
{
    BrokerId Id { get; }                       // "mstock", "zerodha", ...
    BrokerCapabilities Capabilities { get; }   // what this broker can actually do

    IBrokerAuth Auth { get; }
    IBrokerOrders Orders { get; }
    IBrokerPortfolio Portfolio { get; }
    IBrokerMarketData MarketData { get; }
    IBrokerStream? Stream { get; }             // null when the broker has no live feed
}
```

Split by concern so a broker can implement a subset:

- `IBrokerAuth` — `StartLoginAsync`, `SubmitChallengeAsync` (OTP / TOTP / request-token), `RefreshAsync`, `LogoutAsync`, and a `BrokerSession` carrying `AccessToken`, `ExpiresAt`, `RefreshToken?`, `Extras`.
- `IBrokerOrders` — `PlaceAsync`, `ModifyAsync`, `CancelAsync`, `CancelAllAsync`, `GetOrderBookAsync`, `GetTradeBookAsync`, `GetOrderAsync`, `PlaceBasketAsync`, `CalculateMarginAsync`.
- `IBrokerPortfolio` — `GetPositionsAsync`, `GetHoldingsAsync`, `GetFundsAsync`.
- `IBrokerMarketData` — `GetInstrumentsAsync`, `GetLtpAsync`, `GetOhlcAsync`, `GetQuoteAsync`, `GetHistoricalAsync`, `GetIntradayAsync`, `GetOptionChainAsync`.
- `IBrokerStream` — `ConnectAsync`, `SubscribeAsync(IReadOnlyCollection<InstrumentKey>, StreamMode)`, `UnsubscribeAsync`, `IAsyncEnumerable<Tick> Ticks`, plus a `ConnectionState` signal.

**Canonical domain vocabulary** (never a broker's strings):

```csharp
public enum Exchange { NSE, BSE, NFO, BFO, MCX, CDS }
public enum Segment  { Equity, Derivatives, Currency, Commodity }
public enum Side     { Buy, Sell }
public enum OrderType { Market, Limit, StopLoss, StopLossMarket }
public enum ProductType { Delivery, Intraday, Margin, NormalCarryForward }  // CNC/MIS/MTF/NRML
public enum Validity { Day, ImmediateOrCancel, GoodTillTriggered }
public enum OrderVariety { Regular, AfterMarket, Cover, Bracket, Iceberg }
public enum OrderStatus { PendingSubmit, Open, PartiallyFilled, Filled, Cancelled, Rejected, Expired, Unknown }

public readonly record struct InstrumentKey(Exchange Exchange, string TradingSymbol, long? Token);
```

Rules the adapter layer must obey:

1. **`Result<T>` everywhere, never exceptions for control flow.** Every failure maps to a `BrokerError` with a canonical `BrokerErrorCode` (`InvalidCredentials`, `SessionExpired`, `InsufficientFunds`, `RateLimited`, `MarketClosed`, `InstrumentNotFound`, `RiskRejected`, `BrokerUnavailable`, `Unknown`) plus the raw vendor code/message preserved for support.
2. **Capabilities are data, not `if (broker == "mstock")`.** `BrokerCapabilities` declares supported exchanges, product types, order types, varieties, whether GTT / basket / margin-calculator / streaming / historical data exist, max basket size, and tick modes. The order ticket UI and the risk gate read this; the frontend must render only what the linked broker supports.
3. **Idempotency.** `PlaceAsync` takes a caller-generated `ClientOrderId` (GUID). Persist intent *before* the HTTP call, reconcile after. On timeout, never blind-retry a place — re-fetch the order book and match on `ClientOrderId`/timestamp.
4. **Rate limiting is per broker and per credential**, enforced inside the adapter with `System.Threading.RateLimiting`, configured from `BrokerCapabilities.RateLimits`, and shared across app instances via a Redis token bucket.
5. **Every adapter ships a contract test suite.** Write `BrokerAdapterContractTests<TAdapter>` once in a shared test library; each broker's test project inherits it. A new broker is "done" when that suite is green against a WireMock recording of real responses.
6. **Registry + factory.** `IBrokerRegistry.Resolve(BrokerId)` returns a scoped adapter bound to a decrypted `BrokerSession`. Adapters are registered by assembly scanning so adding a broker is one new project plus one DI line.

---

## 5. mStock adapter (first implementation)

Target the **Type A** REST API. Verify every detail against the live docs at `https://tradingapi.mstock.com/docs/v1/typeA/` before coding — treat the notes below as a starting map, not gospel, and record any drift in `docs/adr/`.

- **Base URL** `https://api.mstock.trade`, streaming `wss://ws.mstock.trade`.
- **Headers on every call:** `X-Mirae-Version: 1`, `Authorization: token {api_key}:{access_token}`, `X-PrivateKey: {api_key}`, `Content-Type: application/json` (login/session calls are `application/x-www-form-urlencoded`).
- **Auth flow:** `POST /openapi/typea/connect/login` (username, password) → triggers OTP to registered mobile → `POST /openapi/typea/session/token` (api_key, request_token = OTP, checksum) → returns `access_token`, `refresh_token`, `public_token`, `enctoken` and the profile's allowed exchanges/products/order types. If the user has TOTP enabled, `POST /openapi/typea/session/verifytotp` (api_key, totp) replaces the OTP step. `GET /openapi/typea/logout` destroys the token.
- **Token lifetime:** access token expires in ~12 hours *or* at midnight IST, whichever is first; API keys last ~13 months. Model this explicitly: a `BrokerSessionMonitor` that surfaces "re-auth needed" to the UI well before expiry, plus a nightly job that marks all live sessions stale at 00:00 IST. **Never** silently drop orders because a token died mid-session.
- **Orders:** `POST /openapi/typea/orders/{variety}` (variety = `reg` | `amo`), `PUT|DELETE /openapi/typea/orders/regular/{orderId}`, `POST /openapi/typea/orders/cancelall`, `GET /openapi/typea/orders`, `GET /openapi/typea/tradebook`, `GET /openapi/typea/trades`, `GET /openapi/typea/order/details?order_no=&segment=` (segment `E`/`D`). Payload fields: `tradingsymbol` (e.g. `INFY-EQ`), `exchange`, `transaction_type`, `order_type`, `quantity`, `product`, `validity`, `price`, `trigger_price`, `disclosed_quantity`.
- **Market data:** `GET /openapi/typea/instruments/scriptmaster` (CSV instrument master — cache in Postgres, refresh via Quartz before market open daily and diff it), `.../instruments/quote/ltp`, `.../instruments/quote/ohlc`, plus historical and intraday chart endpoints and option chain.
- **Rate limits** (encode into `BrokerCapabilities`): orders ≈ 30/sec and 250/min; data ≈ 1/sec and 1000/min; quotes ≈ 20/sec.
- **Enum mapping tables** live in one file, `MStockMaps.cs`, tested exhaustively both directions: `Delivery↔CNC`, `Intraday↔MIS`, `Margin↔MTF`, `NormalCarryForward↔NRML`, `StopLoss↔SL`, `StopLossMarket↔SL-M`, `Regular↔reg`, `AfterMarket↔amo`.
- **Never let the API key or credentials reach the browser.** All broker traffic is server-side; the Angular app only ever talks to Akshaya's own API.
- Build the adapter against **WireMock.Net fixtures captured from the sandbox**, so the contract suite runs in CI with no live credentials.

---

## 6. Security and secrets

- Broker credentials (username, password, api_key, TOTP seed) are stored **envelope-encrypted**: AES-256-GCM with a per-tenant DEK, DEK wrapped by a KMS-held KEK. Plaintext exists only in memory for the duration of a login call. Decryption is audited.
- Access tokens live in Redis with a TTL matching real expiry, keyed `session:{tenant}:{userId}:{brokerId}`, never in the browser and never in the DB in plaintext.
- The web app holds the Akshaya access token in memory only; the refresh token is an `HttpOnly; Secure; SameSite=Strict` cookie. Full CSRF protection on cookie-authenticated routes.
- TOTP 2FA is **mandatory** before a user can link a live broker or arm a strategy.
- Every order-affecting action writes an append-only `AuditEvent` (actor, tenant, ip, ua, before/after, correlation id) with a hash chain so tampering is detectable.
- Standard hardening: strict CSP, HSTS, rate limiting per IP and per user, no PII or credentials in logs (Serilog destructuring policy that redacts by attribute), dependency scanning in CI.

---

## 7. Trading module rules

- Orders are an explicit **state machine**: `Draft → RiskChecked → Submitted → Acknowledged → (PartiallyFilled) → Filled | Cancelled | Rejected | Expired`. Illegal transitions throw. Persist every transition with the broker's raw payload.
- A **pre-trade risk gate** runs before every submission and is configurable per user/org: max order value, max quantity per instrument, max open positions, daily loss limit, instrument allow/deny list, market-hours check, price-band sanity (reject a limit >X% off LTP — the classic fat-finger guard), and a per-tenant **global kill switch** that blocks all new orders instantly.
- **Reconciliation** is not optional: a job polls the broker order book on an interval (and after every stream reconnect), diffs against local state, and raises `OrderDrifted` events. The broker is always the source of truth; local state yields.
- Broker-side rejections are surfaced verbatim to the user *alongside* the canonical code — traders need the raw exchange message.
- Market hours, holidays and the NSE/BSE trading calendar live in a `TradingCalendar` service, seeded from a maintained data file and overridable per exchange.

---

## 8. Market data and realtime

- **Instrument master:** ingest the CSV script master daily into Postgres with full-text + trigram search; expose `/api/instruments/search?q=` returning ranked results in <50 ms. Handle F&O expiry rollovers and symbol changes.
- **Tick pipeline:** one upstream broker WebSocket per credential (never one per browser tab) → normalise to `Tick` → publish to Redis → SignalR fan-out to subscribed clients. **Conflate**: emit at most 4 updates/sec per instrument per client; drop intermediate ticks rather than queueing them. Backpressure must never stall the ingest loop.
- Clients subscribe to instrument sets, not to "everything"; the server tracks per-connection subscriptions and unsubscribes upstream when the last subscriber leaves.
- **Candles:** store OHLCV in a TimescaleDB hypertable with continuous aggregates for 1m→5m→15m→1h→1d rollups. Backfill from the broker's historical endpoint on demand, gap-detect, and cache aggressively — historical data endpoints are the tightest rate limit you have.
- Reconnect with exponential backoff + jitter; on reconnect, re-subscribe and **re-sync order book and positions** before showing the UI as "live". A stale-data banner is mandatory whenever the feed is degraded.

---

## 9. Strategy engine and backtesting

- A strategy is a **declarative rule set** (JSON-serialisable, versioned, stored per user): universe, timeframe, indicators, entry conditions, exit conditions, stop-loss, target, trailing rules, position sizing, and max concurrent positions. Provide a C# `IStrategy` interface too, but the UI-authored rule DSL is the primary path for v1.
- Indicators: implement SMA, EMA, WMA, RSI, MACD, Bollinger, ATR, ADX, Stochastic, VWAP, Supertrend as **incremental/streaming** calculators so the same code runs in backtest and live. Property-test them against a reference implementation.
- The **backtester is event-driven**, bar-by-bar, with an explicit clock. Model slippage, brokerage (per-broker fee schedule), STT, stamp duty, exchange charges and GST — an Indian-market backtest without charges is a lie. Enforce a strict **no-lookahead** rule: a bar's close is unavailable until that bar closes; assert this in tests.
- Report: equity curve, CAGR, max drawdown, Sharpe, Sortino, Calmar, win rate, profit factor, expectancy, average holding period, trade list, monthly returns heatmap. Export CSV and PDF.
- `Akshaya.Brokers.Paper` implements the *same* `IBrokerAdapter` with a simulated matching engine driven by live or replayed ticks. Backtest → paper → live must require **zero strategy code changes** — this is the acceptance test for the whole abstraction.
- **Live execution is supervised by default:** a strategy emits a `Signal`; unless the user has explicitly armed auto-execution (2FA-gated, with a mandatory daily loss cap and kill switch), the signal becomes a notification with a one-click order ticket, not an order.
- **Compliance:** India's regulator has specific rules for algorithmic order flow by retail investors — broker-approved and exchange-registered strategies, unique algo IDs, and per-second order thresholds. Do not hardcode assumptions: put algo-execution behind a feature flag, tag automated orders with an algo identifier field in the adapter contract, keep the full audit trail, and add a `docs/compliance.md` noting that live automated execution requires the broker's approval before enablement. Never present output as investment advice; ship a disclaimer surface.

---

## 10. Multi-tenant SaaS

- `Organisation → Users → BrokerLinks`. Every query is tenant-scoped by an EF Core global query filter; an architecture test asserts no entity is queried without it.
- Roles: Owner, Admin, Trader, Viewer, Auditor — enforced with policy-based authorisation, one policy per capability (`Orders.Place`, `Strategy.Arm`, `Audit.Read`), never role-name checks scattered in endpoints.
- Plans and entitlements as data: max broker links, max strategies, max backtests/month, live-execution on/off, data retention. A metering service counts usage and returns `402`-style domain errors when a quota is exhausted.
- Billing integration behind an `IBillingProvider` seam (Razorpay/Stripe implementation is Phase 9; the seam exists from Phase 1).

---

## 11. Frontend specification

Screens: Login/2FA · Broker Link wizard · Dashboard (blended P&L, funds, open positions, market summary) · Watchlists (multiple, drag-reorder, live LTP with flash-on-change) · Instrument detail + chart · Order Ticket (drawer, driven by `BrokerCapabilities`) · Orders (book + history) · Trades · Positions · Holdings · Funds · Strategy Builder (visual rule editor) · Backtest runner + report · Paper trading console · Alerts · Audit log · Settings/Org/Billing.

Rules:

- The order ticket is **one component** for all brokers. It renders product types, order types, validities and varieties from the capability object. Adding a broker must not touch it.
- Trading-critical UI states are explicit and always visible: connection status, session-expiry countdown, kill-switch state, stale-data banner. A trader must never be unsure whether what they see is live.
- Confirm-before-submit on every order, with a summary of estimated value and charges. Destructive actions (cancel-all, square-off-all) need typed confirmation.
- Optimistic UI is **banned** for orders. Show `Submitting…` until the broker acknowledges.
- Dark theme is the default. Green = buy/up, red = sell/down, and provide a colour-blind-safe alternate palette. Numbers use tabular figures and `en-IN` grouping; never let a price column jitter on update.
- Accessibility: WCAG 2.2 AA, full keyboard operation, keyboard shortcuts for the order ticket (`B` buy, `S` sell, `Esc` cancel).
- Performance budgets: initial route JS < 250 KB gzipped, LCP < 2 s on a 4G profile, a 500-row live grid updating without dropped frames.

---

## 12. Working agreements for the agent

1. **Ask before assuming.** If a broker behaviour, a regulatory rule or a product decision is ambiguous, stop and ask rather than inventing it. Trading bugs cost real money.
2. **Tests first for anything touching money** — order mapping, risk gate, P&L, charges, indicators. TDD those. Target >85% coverage on Domain and Application layers.
3. **Small, reviewable commits**, conventional-commit messages, one logical change each. Never a 4,000-line commit.
4. **Write an ADR** in `docs/adr/NNNN-title.md` for every significant decision, including the ones this prompt already made (record the reasoning, not just the choice).
5. **No secrets in the repo.** `.env.example` only; real config via user-secrets locally and the vault in deployed environments.
6. **No silent catch.** Every `catch` either handles meaningfully or logs with context and rethrows.
7. **Verify before claiming done.** Run build, tests, and lint. Show the actual output. "Should work" is not acceptance evidence.
8. **Prefer deleting to adding.** If a feature can be expressed with existing primitives, don't add a new one.

---

## 13. Phased build plan

Each phase ends at a **gate**. Produce the acceptance evidence, stop, and wait for review.

**Phase 0 — Foundations.** Solution + Nx workspace, Docker Compose (postgres/timescale, redis, otel, seq), SharedKernel (`Result<T>`, `Money`, value objects, `IClock`), Serilog + OpenTelemetry, health checks, CI pipeline, architecture-test project, ADR 0001 recording the stack.
*Gate:* `docker compose up` runs; `/health/ready` green; CI passes on an empty test suite; architecture tests execute.

**Phase 1 — Identity & tenancy.** Users, orgs, roles, JWT + refresh rotation, TOTP 2FA, policy-based authorisation, tenant query filters, audit log skeleton, `IBillingProvider` seam. Angular shell: login, 2FA, layout, auth interceptor, route guards.
*Gate:* a user registers, enables TOTP, logs in, and is denied a capability their role lacks — proven by integration and Playwright tests.

**Phase 2 — Broker abstraction.** `Akshaya.Brokers.Abstractions` complete per §4, plus the reusable `BrokerAdapterContractTests<T>` suite and a trivial fake adapter that passes it. No real broker yet.
*Gate:* contract suite green against the fake; architecture test proves the Abstractions project has zero external references.

**Phase 3 — mStock adapter.** Auth (OTP + TOTP), session lifecycle and expiry monitoring, orders, portfolio, market data, script master ingest, enum maps, rate limiter, resilience policies, WireMock fixtures.
*Gate:* the Phase-2 contract suite passes against the mStock adapter using WireMock fixtures; a documented manual smoke test places and cancels a real order in the sandbox.

**Phase 4 — Credential vault & broker link.** Envelope encryption, KMS abstraction, link/unlink flow, capability discovery, session monitor, re-auth prompts. Angular broker-link wizard.
*Gate:* a user links mStock end to end; credentials are unreadable in the DB; forcing token expiry produces a re-auth prompt and never a lost order.

**Phase 5 — Core trading.** Order state machine, risk gate, place/modify/cancel/basket, order & trade book, positions, holdings, funds, blended P&L, reconciliation job, kill switch. Angular: instrument search, order ticket, orders/positions/holdings grids.
*Gate:* full order lifecycle works against WireMock and sandbox; risk gate blocks each configured limit with a test per rule; killing the app mid-place leaves no phantom order after reconciliation.

**Phase 6 — Market data & charts.** Tick ingestion, Redis fan-out, SignalR hub with conflation, subscription management, TimescaleDB candles + continuous aggregates, historical backfill, option chain, gainers/losers. Angular: watchlists, lightweight-charts with indicators and drawings, live LTP.
*Gate:* 500 instruments streaming to 50 simulated clients with stable memory and no dropped frames; reconnect re-syncs cleanly; a load test proves the conflation limit holds.

**Phase 7 — Strategy, backtest, paper trading.** Rule DSL + versioning, streaming indicators, event-driven backtester with the full Indian charge model, metrics and reports, `Brokers.Paper`, supervised live signals with arming flow. Angular: strategy builder, backtest report, paper console.
*Gate:* one strategy runs unchanged across backtest → paper → live-supervised; no-lookahead test passes; charge model reconciles against a hand-computed worked example.

**Phase 8 — Second broker (Zerodha Kite).** Implement the adapter. **You may not modify `Akshaya.Brokers.Abstractions` except by adding capability flags.** Any other change means the Phase-2 design failed — write an ADR explaining why and what you changed.
*Gate:* contract suite green for Zerodha; the order ticket and every trading screen work with zero component changes; a user with two linked brokers sees a correct blended portfolio.

**Phase 9 — SaaS hardening & launch.** Plans, entitlements, metering, billing provider, notifications (email/webhook/push), full audit UI, admin console, rate limits, backups, DR runbook, pen-test fixes, docs, onboarding.
*Gate:* a paid signup runs end to end in a staging environment; quota exhaustion degrades gracefully; the security checklist in `docs/security.md` is fully ticked.

---

## 14. Start here

Confirm you have understood §4 (the broker contract) and §13 (the phases). Then:

1. Restate the Phase 0 deliverables in your own words and flag anything you'd design differently — I want the disagreement before the code.
2. List any decisions you need from me that this document doesn't settle.
3. On my go-ahead, execute **Phase 0 only** and present the gate evidence.

Do not write application features before the foundations gate passes.
