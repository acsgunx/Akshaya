# Internals — theory and mechanism

`docs/ARCHITECTURE.md` says **what** the shape is. This document says **why that shape and not
another**, and **how it is actually built**, on both sides of the wire.

It is written for someone who has to change this system and needs to know which properties are
load-bearing. Every section states the theory, then the mechanism, then the file where the
mechanism lives. Where the implementation does not yet live up to the theory, that is said
plainly in [Part VI](#part-vi--where-theory-and-implementation-diverge).

**Contents**

- [Part 0 — The thesis](#part-0--the-thesis)
- [Part I — The shared vocabulary](#part-i--the-shared-vocabulary)
- [Part II — The connector substrate](#part-ii--the-connector-substrate)
- [Part III — The trading core](#part-iii--the-trading-core)
- [Part IV — The API surface](#part-iv--the-api-surface)
- [Part V — The Angular client](#part-v--the-angular-client)
- [Part VI — Where theory and implementation diverge](#part-vi--where-theory-and-implementation-diverge)

---

## Part 0 — The thesis

### The failure mode being designed against

A multi-broker trading platform has one characteristic way of dying, and it is not dramatic.

The first broker's vocabulary becomes the platform's vocabulary, because there is nothing else
to generalise from. The second broker mostly fits — a couple of conditionals. The third needs a
different auth flow, so a branch appears in the login handler. By the fifth broker, "add a
broker" means touching thirty files, the order ticket has a `switch` on broker id in it, and
nobody can say which commit was the point of no return.

The decay is **monotonic and locally rational**. Every individual special case is the correct
short-term decision. That is what makes it dangerous: you cannot prevent it with taste or code
review, because each diff looks fine on its own.

### The two structural bets

**Bet 1 — variation becomes data, not code.**

Everything that differs between brokers is declared in a `ConnectorManifest`
([`src/Akshaya.Connectors.Abstractions/ConnectorManifest.cs`](../src/Akshaya.Connectors.Abstractions/ConnectorManifest.cs))
rather than branched on. The order ticket renders from it, the risk gate validates against it,
the link wizard builds its form from it, and the conformance suite checks the connector actually
behaves the way its manifest claims.

This is the open/closed principle made operational: the system is open to a new broker (add a
project and a manifest) and closed to modification (no existing file changes). The mechanism is
the standard one — introduce a level of indirection — but with a specific twist: the indirection
is *declarative data validated at load time*, not a polymorphic interface. An interface would let
a connector author express arbitrary behaviour that the core cannot reason about ahead of time. A
manifest can be read by the risk gate *before* a call is made, which is what lets the platform
refuse an unsupported order for free instead of discovering the problem from a broker rejection.

**Bet 2 — the invariant is enforced by a test, not by discipline.**

[`tests/Akshaya.Architecture.Tests/BrokerLeakageRules.cs`](../tests/Akshaya.Architecture.Tests/BrokerLeakageRules.cs)
greps the core, the API and the Angular source for a list of broker names and fails the build on
a match outside a connector project. Comment lines are exempt — a doc comment explaining *"this
shape exists because one broker reports business failures as HTTP 200"* is the documentation that
keeps the abstraction comprehensible. What must never exist is a broker name in a **conditional**
or a **string literal**, and those live on executable lines.

This is an *architectural fitness function* in the sense of Ford, Parsons and Kua: an executable,
objective assessment of an architectural characteristic. The insight it encodes is that
architecture decays through accumulation of individually-defensible exceptions, so the only
reliable defence is a mechanical tripwire that fires on the **first** one, when reverting is
still cheap.

The full set:

| Test | Property held |
|---|---|
| `Core_and_api_must_not_mention_any_broker_by_name` | Bet 1 has not been abandoned server-side |
| `Frontend_must_not_mention_any_broker_by_name` | …or client-side |
| `Every_connector_project_ships_a_manifest` | A connector cannot exist without declaring itself |
| `SharedKernel_depends_on_nothing_but_the_framework` | The vocabulary is a source, not a sink |
| `SharedKernel_csproj_declares_no_package_references` | …and cannot acquire a dependency by accident |
| `Abstractions_depends_only_on_the_SharedKernel` | The contract is not coupled to an implementation |
| `Abstractions_contains_no_http_or_serialisation_types` | The contract does not assume a transport |
| `Trading_and_Portfolio_modules_do_not_reference_each_other` | Module boundaries are real, not aspirational |
| `No_project_reaches_a_connector_implementation_except_the_host_and_the_api` | The factory is the only door |
| `Contract_types_never_expose_a_bare_decimal_as_money` | Currency cannot be dropped on the wire |
| `Nothing_outside_the_clock_reads_the_ambient_time` | Determinism is preserved |
| `Connector_facet_methods_return_Result_rather_than_throwing` | The failure channel is uniform |

Read as a set, these encode the **acyclic dependency principle** (no module cycles), the
**stable-dependencies principle** (`SharedKernel` is maximally stable and depends on nothing),
and **dependency inversion** (the core depends on `IBrokerConnector`, an abstraction it owns;
concrete connectors depend on the same abstraction from the other side).

---

## Part I — The shared vocabulary

`Akshaya.SharedKernel` is the deepest layer: it depends on nothing but the BCL, and a test
enforces that it also declares no NuGet package references. Everything else in the system speaks
its types.

### 1.1 Making illegal states unrepresentable

The design principle is **parse, don't validate** — push correctness into the type so that once a
value exists, it is known good, and downstream code has no validation left to forget.

**`Money` is an amount *and* its currency**
([`Money.cs`](../src/Akshaya.SharedKernel/Money.cs)). Arithmetic between different currencies
throws rather than silently producing a number:

```csharp
public static Money operator +(Money a, Money b)
{
    Assert(a, b, "add");                      // throws on currency mismatch
    return new Money(a.Amount + b.Amount, a.Currency);
}
```

Adding SGD to INR is the single most expensive class of bug in a cross-border trading system, and
it is a bug the type system can eliminate entirely. Note what is *not* provided: there is no
ambient "current FX rate" lookup. Conversion requires an explicitly supplied rate —

```csharp
public Money ConvertTo(Currency target, decimal rate)
```

— because historic P&L converted at today's rate is a reporting bug, and making the rate a
parameter forces the caller to answer *which rate, as of when*. This is deliberate friction: the
API is designed so the wrong thing is hard to express, not merely discouraged.

**`Quantity` is `decimal`, not `int`.** Fractional shares are real at most US brokers, and an
`int` would silently truncate them. Connectors that cannot do fractions declare
`fractionalQuantity: false` and `FractionalQuantityRule` rejects the order *before it is sent*.
The type is permissive; the manifest constrains it per broker. That split matters — the core type
must model the union of what brokers can do, and the manifest narrows it per instance.

**`Venue` is a value, not an enum**
([ADR 0002](adr/0002-venue-as-data-not-enum.md)). An enum of exchanges is exactly how a platform
accidentally becomes single-market: adding SGX becomes a recompile plus an exhaustiveness sweep
of every `switch`. `Venue` wraps an ISO 10383 MIC string. Convenience handles exist
(`Venue.Nse`, `Venue.Nasdaq`) but nothing in the core may switch on them.

**`InstrumentKey` is a structured, round-trippable identity.** A readonly record struct of
`(Venue, Symbol, AssetClass, Expiry?, Strike?, Right?)` with a canonical `ToString`/`TryParse`
pair, so the same value is usable as a dictionary key, a cache key, a URL segment and a wire
field with no separate DTO. Its `ToString` is *stable* — that is a contract, because it is
persisted in the watchlist's `localStorage` and used as a routing parameter.

### 1.2 Errors as values, not control flow

[ADR 0004](adr/0004-result-not-exceptions.md). The rule is a distinction between two categories
that mainstream exception-based code tends to conflate:

- **Expected outcomes** — an expired session, a closed market, a risk rejection, a rate limit.
  These are not exceptional. They are the *normal* result of talking to a broker, they happen
  thousands of times a day, and every one of them is something a caller must handle. They are
  modelled as `Result<T>` ([`Result.cs`](../src/Akshaya.SharedKernel/Result.cs)) with a structured
  `Error(Code, Message, VendorCode?, VendorMessage?, Context?)`.
- **Programmer error** — cancelling an already-filled order, reading `.Value` off a failed
  `Result`. These throw, loudly, and should surface in a test.

`Result<T>` carries the usual algebraic surface — `Map` (functor), `Bind` (monadic composition),
`Match` (catamorphism), `ValueOr`. It is a `readonly struct`, so the happy path allocates nothing;
on a critical path that runs per order and per tick, that is not incidental.

The theoretical claim is that a function returning `Result<T>` is **total**: its codomain contains
its failure modes, so its signature is honest about what can happen. An exception-throwing
function is partial, and its true signature is invisible at the call site. In a system whose
dominant failure mode is *"we did not handle what the broker said"*, making failure visible in the
type is worth the syntactic cost.

The `VendorCode`/`VendorMessage` fields matter more than they look. The canonical code is what the
platform reasons about; the vendor's original text is what a trader and a support engineer need to
correlate against the broker's own dashboard. Erasing it during normalisation destroys the only
evidence that a mapping is wrong.

### 1.3 Time is a dependency

`IClock` ([`Clock.cs`](../src/Akshaya.SharedKernel/Clock.cs)) with `SystemClock` and `ManualClock`,
and an architecture test banning `DateTime.Now` / `DateTimeOffset.Now` / `DateTime.UtcNow`
anywhere else.

Ambient time is a hidden input, and a function with a hidden input is not reproducible. Three
concrete consequences follow, and all three are load-bearing here:

1. A backtester must control the clock, or it is not a backtester.
2. "Is the venue open?" must be answerable *at an arbitrary instant*, not only now — that is what
   makes `VenueMarketHoursRule` testable at all.
3. Session-expiry logic must be testable without waiting for a session to expire.

The Paper connector's matching engine goes further: its notion of "now" is *the highest tick
timestamp seen so far*, falling back to the injected clock only before the first tick
([`MatchingEngine.cs`](../src/connectors/Akshaya.Connector.Paper/MatchingEngine.cs)). Time in a
replay is derived from the tape, not from the wall — replay the same tape and every timestamp on
every order and trade is byte-identical.

---

## Part II — The connector substrate

### 2.1 The contract

`IBrokerConnector`
([`IBrokerConnector.cs`](../src/Akshaya.Connectors.Abstractions/IBrokerConnector.cs)) is a
*facade over facets*: `Auth`, `Orders`, `Portfolio`, `MarketData`, `Reference`, and a **nullable**
`Stream`.

The nullability is a design statement. Not every broker has a live feed, and the alternative
designs are worse:

- A `Stream` that throws → the caller learns at runtime, on the critical path.
- A `Stream` returning empty → indistinguishable from a quiet market, which is the exact
  ambiguity this system spends the most effort eliminating.
- `bool SupportsStreaming` beside a non-null `Stream` → two sources of truth that can disagree.

`IConnectorStream?` forces the question at compile time. This is the same reasoning as `Money`
carrying its currency: put the fact in the type, not in a convention.

The counterpart is `ConnectorHealth`, which is deliberately not a boolean. It reports
`IsHealthy`, `StreamState`, `SessionValid`, `SessionExpiresAt`, `GatewayRunning`, `Detail`,
`Latency` — because *"connected"*, *"connected but the session expires in 90 seconds"* and
*"connected but the gateway daemon died"* are three different operational situations with three
different remedies, and collapsing them is how a trader ends up surprised.

### 2.2 The manifest as a capability model

[ADR 0003](adr/0003-manifest-driven-capability.md). The manifest declares jurisdictions, venues,
currencies, asset classes, an `AuthSpec`, an `OrderSpec`, a `MarketDataSpec`, `RateLimitSpec[]`, a
`SandboxSpec` and a `ComplianceSpec`.

The rule that makes plug-and-play real is stated in the type's own doc comment:

> If the core or the UI needs to know something about a broker, it becomes a field here. It never
> becomes an `if (connectorId == ...)`.

Two second-order consequences are worth naming:

**Capability checks are free and early.** Because the manifest is data available before any call,
`CapabilitySupportedRule` can refuse an unsupported order type without spending a network round
trip, a rate-limit permit or a quote. Contrast the alternative — discovering the limitation from
a broker rejection — which costs latency, burns quota, and produces a vendor error message the
user cannot act on.

**The UI and the risk gate cannot disagree.** Both read the same manifest, and
`PlaceOrderHandler` runs *the same `CapabilitySupportedRule` object* the risk gate uses rather
than a copy. Two implementations of "can this broker do this" would eventually diverge, and the
divergence surfaces as an order the UI offered and the broker refused — the worst possible place
to find out.

**Contract versioning.** `ConnectorContract.CurrentVersion` is `"1.0"`, and `IsCompatible`
accepts the current major or exactly one behind
([`ManifestLoader.cs`](../src/Akshaya.Connectors.Sdk/ManifestLoader.cs)). Minors are
additive-only by policy. The point is that a third-party connector built last quarter must not
break on our deploy — the deprecation window is a version, not a date.

### 2.3 Authentication as a state machine

Broker auth is not one flow. It is OAuth2, OAuth1a request signing, password + SMS OTP, password
+ TOTP, a pasted static token, RSA-signed requests, or a local gateway daemon holding the session
([`Auth.cs`](../src/Akshaya.Connectors.Abstractions/Auth.cs)).

Modelling that as "a method per broker" is how the login handler acquires a `switch`. Instead the
connector returns an `AuthStep`, a closed sum type with exactly four cases:

```csharp
public abstract record AuthStep
{
    public sealed record Completed(BrokerSession Session)                       : AuthStep;
    public sealed record RedirectRequired(string Url, string State)             : AuthStep;
    public sealed record ChallengeRequired(ChallengeKind Kind, string Prompt,
                                           string? MaskedDestination = null, …) : AuthStep;
    public sealed record GatewayRequired(string GatewayId, string Instructions) : AuthStep;
}
```

The client drives a loop over four wire-level cases and has no idea which broker it is talking to.
[`broker-link-wizard.component.html`](../apps/web/src/app/features/broker-link/broker-link-wizard.component.html)
is a `@switch` on `step.type` with four arms — and that is the entire multi-broker login UI.

This is the value of a **closed** sum: seven auth models collapse into four *interaction shapes*,
because the question the UI must answer is not "which broker is this" but "what do I need from
the human next". The abstraction is chosen at the level of the interaction, not the vendor.

Session expiry takes the **minimum** of the nominal lifetime and venue midnight
([ADR 0005](adr/0005-session-expiry-takes-the-minimum.md)) — several Indian brokers invalidate at
venue midnight regardless of when you logged in, and assuming the nominal lifetime means a trader
discovers the expiry from a rejected order.

### 2.4 Three hosting models, one interface

[ADR 0006](adr/0006-three-hosting-models.md). `ConnectorHosting` is `InProcess`, `OutOfProcess`
(gRPC, any language) or `Gateway` (a supervised vendor daemon —
[`GatewaySupervisor.cs`](../src/Akshaya.Connectors.Host/GatewaySupervisor.cs)). Nothing above
`IBrokerConnector` can tell which it is holding; `ConnectorFactory.ActivateRemote` returns a
`GrpcConnectorProxy` that satisfies the same interface.

This is a straightforward application of the **proxy** pattern to achieve **location
transparency**, and it buys a specific thing: a broker whose only usable SDK is Python costs a
gRPC service, not a rewrite of the platform.

### 2.5 The decorator chain — a non-commutative composition

`ConnectorFactory.Decorate`
([`ConnectorFactory.cs`](../src/Akshaya.Connectors.Host/ConnectorFactory.cs)) wraps every raw
connector. The order is the interesting part, and it is documented in the class remarks as
load-bearing:

```
  caller
    │
    ▼
┌──────────────────┐  outermost
│ AuditingConnector│  one row per LOGICAL operation, including calls the limiter refused
└────────┬─────────┘
         ▼
┌──────────────────┐
│ TracingConnector │  one span per logical operation, retries nested inside it
└────────┬─────────┘
         ▼
┌────────────────────┐
│ ResilienceConnector│  retries — idempotent reads only, NEVER a write
└────────┬───────────┘
         ▼
┌──────────────────────┐
│ RateLimitingConnector│  each ATTEMPT takes a permit
└────────┬─────────────┘
         ▼
  raw connector
```

Decorator composition is **not commutative**, and each adjacency here encodes a requirement:

- **Audit outermost** — an audit row must exist even for a call that never got a span because
  tracing was disabled, and even for one the limiter refused. *"We would not send your order"* is
  a fact that must be provable after the incident.
- **Tracing outside resilience** — a span should measure what the *caller experienced*. A retried
  call then reads as one slow operation rather than four unrelated fast ones, which is the shape
  that makes a latency histogram meaningful.
- **Resilience outside rate limiting** — a retry must take a *fresh permit*. The other order lets
  a retry storm past a limiter that already refused the first attempt, which is precisely how a
  credential gets banned rather than throttled.
- **Rate limiting innermost** — closest to the wire, counting exactly what reaches it. The broker
  meters attempts, not logical operations, so the limiter must sit where attempts are.

The construction path is also a security property: `IConnectorFactory` is documented as the only
supported way to obtain a connector, and an architecture test enforces that no project reaches a
connector implementation directly. Constructing one elsewhere bypasses rate limiting and the
audit trail.

A small but instructive detail: the unauthenticated instance used for the login handshake is
given the credential id `"unauthenticated"` — a constant rather than an empty string — so login
attempts *share one rate-limit bucket* instead of being unmetered. An unmetered pre-auth path is
exactly what a credential-stuffing attempt would exploit.

### 2.6 Retry theory, and why writes are never retried

This is the most consequential single decision in the backend.
[`ResilienceConnector.cs`](../src/Akshaya.Connectors.Sdk/Decorators/ResilienceConnector.cs)
retries only when **both** hold:

1. `ConnectorOperation.IsIdempotentRead` is true, **and**
2. the error code is in `ConnectorErrorCodes.Retryable` = `{ RateLimited, Timeout,
   BrokerUnavailable, GatewayUnavailable }`.

`PlaceAsync`, `ModifyAsync`, `CancelAsync`, `CancelAllAsync` and `PlaceBasketAsync` are never
retried. Not on a timeout, not on a 502, not once.

The reasoning is the Two Generals problem in its practical form. **A timeout means the response
was lost, not that the request was.** The broker may have accepted the order and failed to tell
us. At this layer the information needed to distinguish the two cases does not exist and cannot
be obtained — no backoff strategy, idempotency header or "it's probably fine" changes that.

The cost asymmetry settles it:

| | Do not retry | Retry |
|---|---|---|
| Request was lost | Order not placed; reconciliation finds nothing; user retries deliberately | Order placed once (good) |
| Response was lost | Order is live; reconciliation adopts it | **Two real positions.** Real money. Trader is double-long and does not know it |

A missing order is bounded and recoverable. A duplicate order is unbounded in cost. Where the
downside is asymmetric and the information is unavailable, the correct policy is **at-most-once
delivery plus reconciliation**, not at-least-once plus deduplication.

The system is built end to end around that choice:

- `PlaceOrderRequest.ClientOrderId` is generated and **persisted before the send**, so a timed-out
  order is discoverable afterwards.
- `OrderState.Unknown` exists to represent *"we do not know"* as a first-class state rather than
  a guess.
- `ReconciliationService` resolves `Unknown` against the broker's order book.
- `PlaceOrderHandler` step 8 states explicitly that it must not undo this at a higher level.

Exactly-once semantics are unavailable across a network boundary you do not control. What is
available is at-most-once plus an out-of-band repair process, and that is what this is.

The retry budget itself is worth noting: `MaxRetries = 3`, but also `TotalBudget = 10s`. The hard
time budget matters more than the attempt count — three retries honouring a 5-second
`Retry-After` each is fifteen seconds of a trader watching a spinner. And `HonourRetryAfter`
defaults to true because the broker knows when its own window reopens.

### 2.7 Rate limiting: the bucket identity is the design

[`ConnectorRateLimiter.cs`](../src/Akshaya.Connectors.Sdk/ConnectorRateLimiter.cs).
`RateLimitKey` is `(ConnectorId, CredentialId, Scope)`.

**Per credential, not per app and not per tenant**, because that is what the broker meters. Two
users of the same broker must not be able to consume each other's budget, and one user's two
accounts each get their own. Including the connector id keeps two brokers' `orders` buckets
apart.

The scope vocabulary is **closed** — `orders`, `data`, `quotes`, `global` — and `ManifestLoader`
rejects a manifest using an unknown scope. A manifest that invented a scope name would declare a
limit nothing enforces, which is strictly worse than declaring none: it reads as protection that
does not exist.

The scopes reflect how brokers actually meter: order placement is nearly always the tightest
bucket, quotes are metered separately and generously, and `global` applies on top of everything
else.

### 2.8 Conformance: "plug and play" made falsifiable

[`ConnectorConformanceTests.cs`](../tests/Akshaya.Connectors.TestKit/ConnectorConformanceTests.cs)
is an abstract suite. A connector's own test project subclasses it, supplies a manifest, a
`ManualClock` and a factory, and inherits every test.

The claim being made: a broker integration is not done when it returns data. It is done when it
**behaves the way its manifest says it does**, **fails the way the core expects**, and **does not
leak**.

Error mapping is verified from recorded fixtures (`VendorErrorFixture`) rather than live calls,
because vendors add error codes without announcing them and the only way to keep up is to paste
the payload into a fixture and let CI hold the line. Every test carries a note about the real bug
it catches — a conformance test whose failure nobody can interpret gets deleted the first time it
goes red on a Friday.

Two deliberately dissimilar fake brokers (`AlphaFakeConnector`, `BetaFakeConnector`) exist to keep
the abstraction honest: an abstraction validated against one implementation is a description of
that implementation.

---

## Part III — The trading core

### 3.1 The order lifecycle as a transition table

[`OrderStateMachine.cs`](../src/Modules/Trading/Domain/OrderStateMachine.cs).

`OrderState` is deliberately **richer** than `OrderStatus`, the canonical vocabulary connectors
speak. Two states have no broker equivalent:

- **`RiskChecked`** — the pre-trade gate passed but nothing has left the building. Separating it
  from `PendingSubmit` is what makes *"we rejected it"* and *"we never got that far"*
  distinguishable after an incident.
- **`Acknowledged`** — the broker confirmed the order is live *at the venue*, as opposed to merely
  having accepted our HTTP request. Collapsing the two is how a trader ends up believing an order
  is working when it was never routed.

This is a general modelling point: the platform's internal state space must be at least as fine
as the questions it needs to answer. Reusing the external vocabulary because it is *nearly* right
destroys distinctions you will need during an incident, which is exactly when they are expensive
to reconstruct.

**Why a table and not a `switch`.** The legal graph is a `IReadOnlyDictionary<OrderState,
IReadOnlySet<OrderState>>`. The stated reason: the whole graph has to be readable *in one place,
by a reviewer, in under a minute*. A state machine spread across nine methods is a state machine
nobody audits, and an unaudited order lifecycle is where "cancelled orders that later filled"
come from.

Terminal states appear as **empty sets rather than absent keys**, so a missing key is a bug in
the table rather than an accidentally-permissive default. That is a fail-closed default applied
to a data structure.

**Two relations, not one.** This is the subtle part.

```csharp
public static bool CanTransition(OrderState from, OrderState to)   // what OUR code may do
public static bool CanReconcile (OrderState from, OrderState to)   // what the BROKER may tell us
```

`Allowed` is narrow: violating it is programmer error and throws. `CanReconcile` is almost
entirely permissive, because the broker is the authority and our local state can be wrong in ways
our own code would never produce — a mis-sequenced stream event, a fill recorded from a partial
payload, a cancel that raced a fill.

There is exactly **one** refusal: a `Filled` order may not be resurrected into a working state.

> A fill is a real event at a real venue with a real counterparty; it does not un-happen. If a
> broker reports one of our filled orders as working, the mismatch is an **identity** problem — we
> matched the wrong two records — and quietly adopting it would corrupt the position. That case
> must surface as drift for a human, not as a state change.

The general principle: when reconciling against an authority, *accept everything except what is
physically impossible*, and treat the impossible as evidence of a **matching** bug rather than a
state bug. Absorbing it silently converts a detectable error into a corrupted position.

### 3.2 The risk gate — ordered short-circuit evaluation, fail-closed

[`RiskGate.cs`](../src/Modules/Trading/Domain/RiskGate.cs) plus ten rules in
[`Domain/Rules/`](../src/Modules/Trading/Domain/Rules/). It is a chain of responsibility, run in
declared order, short-circuiting on the first denial:

| Order | Rule | What it stops |
|---|---|---|
| 10 | `KillSwitch` | Everything, when the tenant has halted trading |
| 20 | `InstrumentAllowDenyList` | Instruments this tenant may not trade |
| 30 | `CapabilitySupported` | Orders the broker's manifest says it cannot place |
| 40 | `FractionalQuantityAllowed` | Fractions at a whole-units broker; non-multiples of lot size |
| 50 | `MaxQuantity` | Oversized single order by quantity |
| 60 | `VenueMarketHours` | Orders the venue cannot possibly accept right now |
| 70 | `MaxOpenPositions` | Too many concurrent positions |
| 80 | `MaxOrderValue` | Oversized single order by notional |
| 90 | `DailyLossLimit` | New exposure after the day's loss limit |
| 100 | `PriceBandSanity` | The fat-finger guard — price far from last traded |

The ordering is **by cost, cheapest first** — the same reasoning a query planner uses when
ordering predicates. `KillSwitch` is a flag lookup; `PriceBandSanity` needs a live quote. Running
the expensive check first would spend a quote on an order the kill switch was going to refuse
anyway.

Three properties are worth naming:

**Fail-closed.** A rule that *throws* produces a denial, not a pass:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Risk rule {Rule} threw; failing closed.", rule.Name);
    return RiskDecision.Deny(rule.Name,
        $"The {rule.Name} check could not be completed, so the order was not sent.");
}
```

Default-deny is the only defensible posture for a pre-trade gate. An availability problem in a
risk check must never become permission to trade.

**Every denial names its rule.** `RiskDecision` requires `RuleName`, a plain-language `Reason`
written to be shown to the trader unedited, an `ErrorCode`, and a machine-readable `Context`
carrying the limit and the observed value. The doc comment is blunt about why:

> "Order rejected by risk" with no rule name is the single most useless message a trading platform
> can emit: the trader cannot fix it, support cannot explain it, and the operator cannot tell
> whether the policy is even working.

**Disabled rules are logged, not silently skipped.** A rule switched off for a tenant emits a
debug line, so *"why did this get through"* is answerable from the logs alone.

`RiskEvaluationContext` is assembled **once** by the handler and passed to every rule, so ten
rules do not make ten quote calls and — more importantly — every rule judges **the same instant**.
Rules that each fetched their own quote could disagree about the price, and a gate whose rules
disagree is not a gate.

### 3.3 The critical path

[`PlaceOrderHandler.cs`](../src/Modules/Trading/Application/PlaceOrderHandler.cs) is documented
step by step. The ordering is an argument, not a convention:

1. **Validate the request.** Structural problems cost nothing to detect and are the caller's to
   fix. First, so a malformed order never consumes a rate-limit permit, a quote or an FX lookup.
2. **Resolve the connector.** Also proves the link exists, belongs to this tenant and has a live
   session. Before the risk gate, because step 3 needs the manifest — and it fails fast on the
   most common operational problem (an expired session) with the one error the user can act on.
3. **Check manifest capability.** Before any market-data call, because an unsupported order type
   is refusable for free.
4. **Run the risk gate.** After the cheap checks, before anything irreversible. The last moment at
   which refusing is free.
5. **Persist as `PendingSubmit`, with its `ClientOrderId`, before the place call.** The single
   most important line in the file.
6. **Call the connector.**
7. **On success**, `Submitted` → `Acknowledged` as two steps, because *"the broker took my HTTP
   request"* and *"the order is working at the exchange"* are different facts.
8. **On timeout, do not retry.** Mark `Unknown`, hand to reconciliation.

Step 5 deserves its billing. It establishes the **write-ahead invariant**: local durable intent
exists before any externally-visible effect. If the process dies between the write and the
broker's answer, reconciliation has a record with an idempotency key and can ask the broker what
became of it. Without the write, the order may be live at the venue and *no part of the platform
knows it exists* — an unrecoverable state, because you cannot reconcile against a record you never
made.

Idempotency is closed at the front too: a repeated `ClientOrderId` returns the original order
unchanged rather than placing a second one. That is why `ClientOrderId` is the *caller's* to
choose — an idempotency key the server generates is not an idempotency key.

### 3.4 Reconciliation — primary-copy convergence

[`ReconciliationService.cs`](../src/Modules/Trading/Application/ReconciliationService.cs).

> **The broker is the source of truth.** Our order aggregate is a cache with opinions. The broker's
> order book is what the venue acted on, what the counterparty saw, and what settlement will use.

Where the two disagree the broker wins — unconditionally, with no heuristic about which looks more
plausible, and with the correction recorded as an `OrderDrifted` event. The justification is
sharp: *a reconciler that sometimes prefers the local copy is a reconciler that hides the bug it
exists to expose.*

This is a **primary-copy** replication model, not a CRDT one. There is no merge function, because
there is no symmetric conflict — one replica is authoritative by construction. The local copy
exists for latency and for the questions the broker cannot answer (`RiskChecked`), not for
authority.

**Two triggers, both necessary:**

- **On an interval** (30s default) — streams drop updates, and an order placed during a deployment
  has nobody listening for its fill. The interval is a compromise: shorter burns a broker's data
  quota that the trader's own quotes need; longer leaves a mis-sequenced fill on screen for an
  uncomfortable time.
- **After every stream reconnect** — the disconnect-to-reconnect gap is *exactly* the window in
  which updates were missed, and the reconnect is the only moment we know that window closed.
  Polling alone leaves a stale blotter for up to a full interval after the socket returns.

**Matching, in descending order of confidence:**

1. `ClientOrderId` — exact. This is what step 5 of the place path bought.
2. `BrokerOrderId` — exact, once the broker has told us one.
3. `(instrument, side, quantity)` within a 60-second window — a **heuristic**, for brokers that do
   not round-trip a client id.

The heuristic is reported *as* a heuristic on every event it produces, and it **refuses to match
when two candidates fit**. That refusal is the important half: a wrong match corrupts two orders,
whereas no match leaves one uncertain. Under ambiguity, prefer a known unknown to a confident
error.

`AdoptBrokerOnlyOrders` defaults to on — a user who places an order in their broker's own app and
then looks at our position screen must not see a lie. `UnaccountedAfter` (2 minutes) must be
comfortably longer than a broker's order-book propagation delay, or every freshly placed order
raises an alarm and the alarm becomes noise.

A structural note: the service is *shaped* like a `BackgroundService` but deliberately does not
derive from one, so the Trading assembly does not depend on `Microsoft.Extensions.Hosting`. The
API wraps `ExecuteAsync` in a hosted service; tests call `ReconcileLinkAsync` directly with no
host at all. Hosting is an adapter concern.

### 3.5 Blended portfolio — scatter-gather with partial failure

[`BlendedPortfolioService.cs`](../src/Modules/Portfolio/BlendedPortfolioService.cs). Four rules:

**1. Fetch every broker in parallel.** `Task.WhenAll` over links. Five brokers at 400 ms is 400 ms,
not two seconds. Latency is `max`, not `sum`. A dashboard that takes two seconds is a dashboard
people stop opening.

**2. Aggregate natively per currency first; convert only for display.** Every blended row holds
exactly one currency; converted totals are computed last, from the native figures, with the rates
recorded alongside. Converting early and summing afterwards compounds rounding into the headline
number and makes it impossible to say which rate produced it. This is the aggregation-order
argument for floating-point-adjacent arithmetic, with an audit requirement layered on top: the
platform must be able to *show its working*.

**3. Group by ISIN or FIGI when available, canonical key otherwise.** This is entity resolution,
and the precision/recall trade-off is decided explicitly in favour of precision:

> The fallback is deliberately conservative: it will show two rows where one was possible, but it
> will never merge two instruments that are not provably the same — an over-merged portfolio
> reports exposure the user does not have.

A false split is visible and annoying. A false merge is invisible and reports a position that does
not exist. Asymmetric cost again decides the default.

**4. One dead broker must not blank the dashboard.** Every link's outcome is captured in a
`PortfolioSourceStatus` and the snapshot returns whatever succeeded, with `IsPartial` and
`FailedSources` set. The client renders a banner stating that the figures **exclude** those
accounts, *"they are not zero"*
([`dashboard.component.html`](../apps/web/src/app/features/dashboard/dashboard.component.html)).

That distinction — *excluded* versus *zero* — is the whole point. Graceful degradation is only
graceful if the degradation is legible; a partial total presented as a complete one is worse than
an error page.

### 3.6 Market data — fan-out and conflation

[`SubscriptionRegistry.cs`](../src/Akshaya.Api/Hubs/SubscriptionRegistry.cs).

**One upstream stream per broker link, not per connection.** Two constraints force this. First,
`MarketDataSpec.MaxStreamSubscriptions` caps how many instruments the *broker* accepts on one
connection — a socket per browser tab would blow the cap the moment two people watch the same five
stocks. Second, a broker session is a metered resource; multiplying it by browser tabs is
gratuitous.

The registry therefore maintains a `LinkStream` per link with at least one live subscriber
anywhere, a `SemaphoreSlim` per link serialising every mutation to its subscriber set (including
creation and teardown), and a reverse index from connection id to touched links so a disconnect
cleans up without being told.

**Conflation, not backpressure.** Ticks are flushed at most four times per second per instrument;
intermediate ticks are dropped.

This is the correct choice for this data because a price tick is a **latest-value-wins** signal,
not an event log. A subscriber that misses intermediate ticks has lost *price history it did not
ask for*; the next flush still carries the current price. Backpressure would be actively wrong
here: propagating one slow browser tab's pressure upstream would stall ingest **for every user
sharing that link's socket**. In a shared-fan-out topology, backpressure couples consumers that
have no business being coupled, so bounded staleness with load shedding beats guaranteed delivery.

The client mirrors this with reference-counted subscriptions
([`market-data.service.ts`](../apps/web/src/app/core/market-data.service.ts)): a watchlist row and
an open order ticket watching the same symbol share one server-side subscription, and unsubscribing
is a no-op until the last watcher goes away. That is what lets each component subscribe in its own
`effect` without coordinating with any other feature.

### 3.7 Identity and the credential vault

[`AesGcmCredentialCipher.cs`](../src/Modules/Identity/Infrastructure/AesGcmCredentialCipher.cs).

**Envelope encryption.** Per record: a fresh 256-bit data key encrypts the payload; the master key
encrypts the data key. Two properties follow:

- Rotating the master key rewraps N 32-byte data keys instead of re-encrypting every credential
  blob. Rotation cost is O(records) in cheap operations rather than O(bytes) in expensive ones.
- A leaked data key costs exactly one record — the blast radius is bounded by construction.

`CredentialProtectionOptions.Keys` is a **dictionary** with a separate `ActiveKeyId` precisely
because rotation needs both keys live simultaneously: records sealed yesterday must keep opening
while new records seal under today's key.

**AES-GCM, not CBC.** These payloads are read back and fed into a broker login, so they must be
*authenticated*. Unauthenticated ciphertext is malleable, and a credential blob an attacker can
flip bits in is a credential blob they can steer. A failed tag is a hard refusal.

**Nonce handling.** 12 random bytes per operation, stored ahead of the tag and ciphertext as
`nonce(12) || tag(16) || ciphertext`. Random nonces are safe *here specifically* because each data
key encrypts exactly one payload — the birthday bound that makes random GCM nonces dangerous needs
on the order of 2³² messages under a single key, and no key here ever sees a second message. That
argument depends on the envelope scheme; it would not hold under a single shared key.

Sessions are HttpOnly cookies (`SameSite=Lax`, `Secure` outside development). Passwords use
PBKDF2 ([`Pbkdf2PasswordHasher.cs`](../src/Modules/Identity/Infrastructure/Pbkdf2PasswordHasher.cs)).
Saved credential values are never returned to the browser — the API exposes only *metadata*: which
field keys were remembered, when it was last used. The profile screen says so in the UI, because a
security property nobody knows about provides no reassurance.

---

## Part IV — The API surface

### 4.1 Result → HTTP

[`ProblemDetailsMapper.cs`](../src/Akshaya.Api/Infrastructure/ProblemDetailsMapper.cs) is the
single translation from the canonical error taxonomy to status codes, emitting RFC 7807
`ProblemDetails`:

| Canonical code | HTTP | Why |
|---|---|---|
| `invalid_request`, `challenge_failed` | 400 | Caller's to fix |
| `invalid_credentials`, `session_expired`, `reauth_required` | 401 | Re-authenticate |
| `instrument_not_found`, `order_not_found` | 404 | No such resource |
| `market_closed` | 409 | Correct request, wrong state of the world |
| `risk_rejected`, `insufficient_funds`, `order_rejected` | 422 | Well-formed, semantically refused |
| `rate_limited` | 429 | Back off |
| `not_supported` | 501 | This broker cannot, ever |
| `broker_unavailable`, `gateway_unavailable` | 503 | Try later |
| `timeout` | 504 | Upstream did not answer |

The distinction between 422 and 400 carries real information: 400 means *the request is malformed,
fix it*; 422 means *the request is well-formed and was refused on its merits*. A client can retry
one after editing and must not retry the other blindly. 501 versus 503 is the same shape —
*never* versus *not now*.

`VendorCode` and `VendorMessage` ride along in the problem body, and the Angular error interceptor
surfaces them: `"Insufficient margin — broker said: \"RMS: margin exceeds available\""`. The
canonical code is for the machine; the vendor text is for the human correlating against their
broker's dashboard.

### 4.2 Money on the wire

[`JsonConverters.cs`](../src/Akshaya.Api/Contracts/JsonConverters.cs). `Money` serialises as
`{ "amount": "1234.56", "currency": "INR" }`. Two decisions:

**An object, not a bare number.** A monetary amount without its currency is the most expensive
kind of missing field in a cross-border system. The object shape makes the currency structurally
impossible to omit — no client can accidentally add SGD to INR, no endpoint can quietly assume a
default. An architecture test (`Contract_types_never_expose_a_bare_decimal_as_money`) keeps it that
way.

**The amount as a string.** JSON numbers are IEEE-754 doubles in every browser, and `JSON.parse`
silently rounds a decimal it cannot represent. For prices and P&L that is a real loss of
information *at the exact moment the user is checking our arithmetic against their broker's*.
Reading accepts a JSON number too, because hand-written clients and curl sessions send them and
rejecting those helps nobody — a deliberate asymmetry between the strict producer and the lenient
consumer.

The client honours this: `Money.amount` stays a string all the way to
[`money.pipe.ts`](../apps/web/src/app/core/money.pipe.ts), which is the single place it is parsed
to a `number` — at the point of display, exactly where the precision loss of a formatted string
stops mattering.

### 4.3 Route surface

Minimal APIs grouped by concern in [`src/Akshaya.Api/Endpoints/`](../src/Akshaya.Api/Endpoints/):

```
/api/connectors            GET /           GET /{id}      GET /{id}/health
/api/account               POST /register  POST /sign-in  POST /sign-out  GET /me
/api/account/credentials   GET /           DELETE /{id}
/api/links                 GET /           POST /         POST /{id}/continue   DELETE /{id}
/api/orders                POST /   POST /{id}/modify   POST /{id}/cancel
                           POST /cancel-all   GET /   GET /{id}   POST /estimate
/api/portfolio             GET /   GET /positions   GET /holdings   GET /balances
/api/risk                  GET|PUT /policy   GET /kill-switch
                           POST /kill-switch/engage   POST /kill-switch/disengage
/api/market-data           GET /instruments/search   GET /instruments/resolve
                           GET /quote   POST /ltp   GET /history   GET /option-chain
/hubs/market-data          SignalR
```

`/api/links` is where the `AuthStep` loop lives: `POST /` begins, `POST /{id}/continue` supplies a
challenge response or redirect code. Both are the *only* endpoints that ever see raw broker
credentials.

### 4.4 The composition root

[`Program.cs`](../src/Akshaya.Api/Program.cs) contains zero broker names — connectors are
discovered through `IConnectorFactory` / `ConnectorCatalog`, never by naming a concrete type. The
one exception is Paper, the platform's own simulator, wired in-process exactly the way an operator
would wire any first-party connector.

One subtlety documented at length there: `BrokerLinkResolver` creates a fresh connector per call
and requires the caller to dispose it — correct for every real broker, where state lives at the
venue and the connector is just a client. Paper is the exception: its `MatchingEngine` holds the
entire simulated book in process memory, so a fresh instance per HTTP request would reset the
account to zero on every call. The fix is one `PaperConnector` per account behind a
`NonDisposingConnectorProxy`, so the caller's `await using` still runs without tearing down shared
state. The general lesson: when a lifetime contract has one legitimate exception, satisfy the
contract with a proxy rather than weakening the contract for everyone.

---

## Part V — The Angular client

### 5.1 The client renders capabilities, not brokers

The frontend is subject to the same leakage test as the backend
(`Frontend_must_not_mention_any_broker_by_name`). Concretely:

- The **order ticket** iterates `manifest.orders.types`, `.timeInForce`, `.positionEffects`,
  `.varieties`. A broker supporting three order types renders three options; one supporting six
  renders six. The file has no idea which broker it is.
- The **link wizard** builds its form from `manifest.auth.credentialFields` and switches on four `AuthStep`
  cases.
- The **catalogue** renders venues, currencies, asset classes and capability flags straight from
  manifests.

Route parameters are opaque ids — `connectorId`, `brokerLinkId`, the canonical `InstrumentKey`
string. `brokerLinkId` identifies a *linked account*, not a broker type, because a user can hold
two links for the same connector; the ticket resolves the manifest from the link's `connectorId`,
never from the route.

### 5.2 Zoneless change detection and signal reactivity

The app runs `provideZonelessChangeDetection()`
([`app.config.ts`](../apps/web/src/app/app.config.ts)) with no `zone.js` in the polyfills.

**What zone.js did and why dropping it is a real change.** Zone.js monkey-patches every async
primitive in the browser — `setTimeout`, `Promise`, `addEventListener`, XHR — so the framework can
notice "something happened" and re-check the entire component tree. It is *implicit, ambient
invalidation*: correct by over-approximation, and expensive by construction, because the framework
cannot tell what changed and must assume everything did.

Signals invert this. A signal is a node in an explicit dependency graph; reading one inside a
`computed` or a template registers an edge, and writing one marks exactly its transitive dependents
dirty. Invalidation becomes **precise** rather than conservative, and change detection walks only
the affected subtree.

Angular's signal graph is **glitch-free**: derived values are recomputed in topological order, so
an observer never sees an inconsistent intermediate state where one input has updated and another
has not. This matters concretely here — `changePercent` and `changeIsUp` in
[`watchlist-row.component.ts`](../apps/web/src/app/features/watchlist/watchlist-row.component.ts)
derive from the same tick, and a glitchy system could momentarily render a positive percentage in
the "down" colour.

The engineering obligation that follows is stated in `app.config.ts`: under zoneless, *every* piece
of state that must update the view has to be a signal. A `setInterval` writing to a signal is
zoneless-safe; a DOM callback mutating a plain field is not — it will silently stop updating the
view. That is a real hazard and the reason the note exists. `market-data.service.ts` deliberately
exposes signals rather than Observables so there is one conversion point instead of N `toSignal`
calls at the call sites.

### 5.3 State: SignalStore, and where a store lives

`@ngrx/signals` throughout. The scoping decision is the interesting one.

**Root-provided** — `AuthStore`, `ConnectorStore`, `BrokerLinksStore`, `KillSwitchStore`. These are
genuinely global facts. Connector manifests load **once**, in the app shell, and every feature
downstream reads them; none re-fetches.

**Component-provided** — `OrderTicketStore`. Its doc comment is explicit:

> …because a ticket's state — which instrument, which draft, whether it's mid-submit — belongs to
> that one open ticket, not the whole app; opening a second ticket for a different instrument must
> not share or clobber the first one's in-flight submit.

The general rule: **state's lifetime should match the lifetime of the thing it describes.** A
root-scoped ticket store is a singleton modelling something that is not a singleton, and the bug
that produces is a submit for instrument A landing in the ticket for instrument B.

`AuthStore` carries one detail worth copying — a `restoring` flag that starts `true`, which the
route guard awaits. Without it a hard refresh on `/positions` races `/me` and bounces an
authenticated user to sign-in for the split second before their cookie is checked. Distinguishing
*"not signed in"* from *"don't know yet"* is the same three-valued-logic discipline as the backend's
`OrderState.Unknown`.

### 5.4 The typed wire boundary

[`api.service.ts`](../apps/web/src/app/core/api.service.ts) is the only file that touches
`HttpClient`. Every response type is one of the wire mirrors in `core/models`, so a shape drift
against the backend contracts fails to compile rather than surfacing as `undefined` deep in a
template.

The file is honest about the limit of that guarantee:

> ENDPOINT NOTE: the paths below must match the minimal-API routes in `Akshaya.Api/Endpoints`
> exactly. They are plain strings on both sides, so nothing catches a drift at compile time — a
> wrong path is a 404 at runtime, which surfaces as a feature that silently does nothing.

**Shapes are checked; paths are not.** See [Part VI](#part-vi--where-theory-and-implementation-diverge).

Two interceptors, each with a single responsibility:

- **`authInterceptor`** adds `withCredentials`. Kept as an interceptor rather than sprinkled
  through `ApiService` so a future switch to bearer tokens is a one-file change. It attaches
  nothing broker-specific — broker credentials pass through the link wizard's one-time
  begin/continue calls and are never cached client-side.
- **`errorInterceptor`** surfaces the RFC 7807 body as a toast, **then rethrows unchanged**. It
  never swallows and never retries, because retry policy differs by endpoint and that decision
  belongs to the caller — the same at-most-once discipline the backend enforces, mirrored on the
  client so a well-meaning global retry cannot reintroduce duplicate orders.

### 5.5 The order ticket refuses optimistic UI

`OrderTicketStore` is a five-phase machine: `form → reviewing → submitting → submitted | failed`.
`submitting` shows a spinner and **nothing that looks like a placed order** until the broker
actually acknowledges.

The doc comment pre-empts the "fix":

> It would be easy to make the ticket feel snappier by immediately showing "Order placed" and
> reconciling in the background — but a trading order is not a todo-list item. […] a user who
> believes an order is live when it was actually rejected (insufficient funds, market closed,
> risk-gate refusal) can act on that false belief — hedge against a position that doesn't exist, or
> skip placing the SAME order elsewhere because "it's already in". The half-second of a spinner is
> a strictly better failure mode than a confident lie.

Optimistic UI is correct when the operation is *nearly always successful* and *cheaply reversible*.
An order placement is neither. This is the same asymmetric-cost reasoning as the no-retry rule,
applied at the presentation layer — and the two must agree, or the UI would undo the guarantee the
backend paid for.

The same principle governs the orders blotter: an order in `state === 'unknown'` shows *"Checking
with broker…"* and offers **no** resubmit or cancel action. The trader must not act on a guess.

### 5.6 The design system

**Theme.** [`_theme.scss`](../apps/web/src/styles/_theme.scss) is one `mat.theme()` call plus
`--ak-*` custom properties. Material 22's CSS-first theming emits system variables rather than
per-component rules, and light/dark come from **one** set of `light-dark()` declarations switched
by `color-scheme` — there is no second theme block to keep in step. Dark is the default; light and
the colour-blind-safe pair are stored preferences applied by `AppearanceStore` as `[data-theme]`
and `[data-cvd-safe]` on `<html>`.

`prefers-color-scheme` is deliberately **ignored**. A trader's brightness choice for other apps
must not silently flip a trading terminal's chrome mid-session.

**Primitives.** [`styles.scss`](../apps/web/src/styles/styles.scss) holds everything that appeared
in more than one feature stylesheet: `.ak-page-head`, `.ak-card`, `.ak-badge` (+ five tints),
`.ak-banner`, `.ak-loading`, `.ak-field`, and the `.ak-thead` / `.ak-trow` / `.ak-tleg` grid-table
set. The four table screens are the same structure four times, and each now declares exactly one
thing — `--ak-cols`, the grid template its header, rows and expanded broker legs all share.

That single declaration is doing real work. Sharing the grid via a custom property means a leg row
**cannot** fall out of alignment with the header it sits under, because there is one definition and
custom properties inherit down the DOM. The alternative — a sub-table with its own widths — makes
the reader work out which number is which, which is the opposite of what expanding a row is for.

**Domain-specific visual rules**, documented in [`DESIGN.md`](../apps/web/src/styles/DESIGN.md):

- **Buy/sell are blue and amber, not red and green.** Around 8% of men have a red-green colour
  vision deficiency, and red-on-dark versus green-on-dark is close to the worst possible pair —
  both desaturate toward a similar dull olive. Blue and amber separate on *both* wavelength and
  luminance, so they survive deuteranopia, protanopia and tritanopia. A luminance-separated
  alternate (cyan/violet) is available as an opt-in setting, because there is no platform signal
  for colour vision.
- **Prices must never jitter.** `font-variant-numeric: tabular-nums` globally on `td`/`th` and the
  `.ak-col-*` classes, plus fixed `min-width` in `ch` units sized for the longest realistic value.
  A tick from 1,482.30 to 1,479.85 cannot move the decimal point or nudge a neighbouring column.
- **Flash-on-change is background colour only**, never weight, size or transform — those are
  exactly the properties that alter a glyph's advance width and would reintroduce the jitter the
  first two rules eliminated. Under `prefers-reduced-motion` the flash becomes a static outline, so
  the *information* survives even when the animation does not.
- **Never colour alone.** The side toggle carries a glyph (▲/▼) and a text label as well as a
  colour; the connection badge pairs its dot with a text label.

**Connection state has four values, not two.** `live` / `degraded` / `stale` / `disconnected`,
mapped straight onto the semantic tokens they mean, with no intermediate alias layer to fall out of
step. The distinction is load-bearing: *connection state* (is the pipe up) and *data freshness* (is
what is on screen current) fail **independently**. A degraded stream can still deliver ticks late;
a connected stream that has pushed nothing in ten seconds on a liquid instrument is itself a
symptom. A number that stopped updating and still looks live is worse than an error screen — it is
a wrong number presented with full confidence.

### 5.7 Build and test toolchain

| | |
|---|---|
| Angular | 22, standalone, zoneless, all routes `loadComponent` |
| Builder | `@angular/build:application` (esbuild + vite) |
| Tests | Vitest via `@angular/build:unit-test`, headless jsdom |
| Lint | ESLint 9 flat config + `angular-eslint`, including template a11y rules |
| Deferred | The appearance menu is `@defer (on idle)` |

The `@defer` is a worked example of the bundle-boundary trade-off: the menu is the only thing in
the app shell needing Material's menu and checkbox, and eagerly importing them cost roughly 98 kB
of *initial* bundle to render one icon button. `on idle` rather than `on interaction` because the
latter spends the user's first click on the download.

Every runtime dependency is MIT, Apache-2.0 or 0BSD.

---

## Part VI — Where theory and implementation diverge

An honest inventory. These are observations from reading the code, not a to-do list anyone has
committed to.

**1. The client/server route contract is unchecked.** `api.service.ts` says so itself: paths are
plain strings on both sides. Response *shapes* are compile-checked against hand-written wire
mirrors in `core/models`, but those mirrors are also hand-written — nothing generates them from the
backend contracts, so a renamed field is caught only if someone remembers to update both. Emitting
an OpenAPI document and generating the client types would close both gaps; the endpoints are
already `WithTags`-annotated for it.

**2. Several ports have development-only implementations.** `InMemoryRateLimitStore` means rate
limits are per-process — correct for a single instance, wrong the moment the API is scaled out,
since each replica would enforce the full budget independently and the broker would see N times the
declared rate. `RateLimitKey.ToString` already documents the intended Redis key shape.
`NullGatewayRuntime`, `LoggingConnectorAuditSink` and the dev trading calendars are in the same
category. The *seams* are right; the adapters are placeholders.

**3. Reconciliation's heuristic matcher is unavoidable but unmeasured.** Tier 3
`(instrument, side, quantity)` matching is only reachable for brokers that do not round-trip a
client id. It refuses ambiguous matches, which is the right call, but nothing currently reports how
often tier 3 fires versus tiers 1 and 2 — and that ratio is the health metric for the whole
matching strategy.

**4. Frontend test coverage is thin.** Three spec files, fifteen tests, covering the two formatting
pipes and the appearance store. The backend has a genuine conformance suite, architecture fitness
functions and focused unit suites (`RiskGateTests` alone is ~950 lines). The parts of the client
that would most repay tests are the ones with real logic and real consequences: `OrderTicketStore`'s
phase machine, `market-data.service.ts`'s subscription refcounting, and the auth guard's
`restoring` race.

**5. No end-to-end test crosses the wire.** The two halves are each tested in isolation. Nothing
exercises place-order from browser to matching engine, which is exactly where a route drift (gap 1)
would be caught.

**6. The kill switch is enforced only at the pre-trade gate.** `KillSwitchRule` runs first in the
risk gate, which stops *new* orders. It does not cancel working orders — correct and documented
behaviour, and the UI says so ("Working orders already at a broker are NOT cancelled"), but it is a
narrower guarantee than the phrase "kill switch" suggests to most people.

**7. The Paper connector is a singleton per account, in process memory.** Documented and correct
for development, but it means paper state does not survive a restart and does not exist for a
second API replica. A real deployment would persist the matching engine's book.

---

## Reading order for a new engineer

**Backend**

1. [`SharedKernel/Money.cs`](../src/Akshaya.SharedKernel/Money.cs) and
   [`Result.cs`](../src/Akshaya.SharedKernel/Result.cs) — the vocabulary everything speaks.
2. [`Abstractions/IBrokerConnector.cs`](../src/Akshaya.Connectors.Abstractions/IBrokerConnector.cs)
   and [`ConnectorManifest.cs`](../src/Akshaya.Connectors.Abstractions/ConnectorManifest.cs) — the
   contract and the capability model.
3. [`Host/ConnectorFactory.cs`](../src/Akshaya.Connectors.Host/ConnectorFactory.cs) — the decorator
   chain and why its order is fixed.
4. [`Sdk/Decorators/ResilienceConnector.cs`](../src/Akshaya.Connectors.Sdk/Decorators/ResilienceConnector.cs)
   — the no-retry-on-write argument, in full.
5. [`Trading/Domain/OrderStateMachine.cs`](../src/Modules/Trading/Domain/OrderStateMachine.cs) —
   the lifecycle, and the two transition relations.
6. [`Trading/Application/PlaceOrderHandler.cs`](../src/Modules/Trading/Application/PlaceOrderHandler.cs)
   — the critical path, step by step.
7. [`Trading/Application/ReconciliationService.cs`](../src/Modules/Trading/Application/ReconciliationService.cs)
   — how the platform stays honest.
8. [`tests/Akshaya.Architecture.Tests/`](../tests/Akshaya.Architecture.Tests/) — the rules that keep
   all of the above true.

**Frontend**

1. [`styles/DESIGN.md`](../apps/web/src/styles/DESIGN.md) — the visual rules and their reasoning.
2. [`styles/styles.scss`](../apps/web/src/styles/styles.scss) — the primitives every screen is
   built from.
3. [`core/api.service.ts`](../apps/web/src/app/core/api.service.ts) — the whole wire boundary.
4. [`core/market-data.service.ts`](../apps/web/src/app/core/market-data.service.ts) — signals and
   subscription refcounting.
5. [`features/order-ticket/`](../apps/web/src/app/features/order-ticket/) — manifest-driven
   rendering, and the anti-optimistic-UI argument.
6. [`features/broker-link/broker-link-wizard.component.html`](../apps/web/src/app/features/broker-link/broker-link-wizard.component.html)
   — four `AuthStep` cases, every broker.

**Related documents**

- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) — the shape, in overview.
- [`docs/ADDING-A-BROKER.md`](ADDING-A-BROKER.md) — the procedure this design exists to keep short.
- [`docs/adr/`](adr/) — the seven decisions with the most consequences.
- [`apps/web/src/styles/DESIGN.md`](../apps/web/src/styles/DESIGN.md) — the frontend's own rationale.
