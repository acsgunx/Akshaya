# Buy and sell orders

Everything about placing, amending, cancelling and reconciling an order, with mStock
(Mirae Asset) as the worked example.

**Scope of this document.** The order path from a click in the browser to a fill at the
exchange, every option the platform exposes, exactly which of them mStock accepts, and the
failure modes each one has. It is written to be read before touching order code, and it names
the mistakes that have already been made here rather than only the rules.

> **Verification status.** The mStock endpoint shapes below were read from the vendor's
> published Type A documentation at <https://tradingapi.mstock.com/docs/v1/typeA/> on
> **2026-09-04**. They have **not** been exercised against a live mStock account — only the
> login leg has (see [`../connectors/mstock.md`](../connectors/mstock.md)). The Paper connector
> path *has* been exercised end to end, including place, amend, cancel, cancel-all, fills and
> the risk gate. Treat every mStock-specific claim as documentation-derived until the smoke
> test at the end of this file has been run and signed.

---

## 1. The shape of the thing

```
Browser                    API                     Trading module              Connector           Broker
───────                    ───                     ──────────────              ─────────           ──────
order ticket ─── POST /api/orders ──► PlaceOrderHandler ──► risk gate
                                            │                   │
                                            │            10 rules, fail-closed
                                            │                   │
                                      persist PendingSubmit ◄────┘
                                            │
                                            └──────────────► IConnectorOrders.PlaceAsync ──► mStock
                                                                      │
                                            ◄─────────────────── OrderAck (Submitted)
                                            │
blotter ◄─── SignalR nudge ─── OrderStateChanged
   │
   └── refetch GET /api/orders  ◄── reconciliation ◄── the broker's order book (the truth)
```

Three properties hold at every step, and each is load-bearing:

1. **The client order id is persisted BEFORE the broker call.** A placement that times out is
   ambiguous — the order may or may not exist. Retrying blindly is how you end up with two
   positions. Instead the platform re-reads the order book and matches on the id it already
   wrote down. See `PlaceOrderRequest.ClientOrderId`.
2. **The broker's order book is the source of truth, not our copy.** Reconciliation adopts the
   broker's version whenever the two disagree (`Order.ReconcileWith` — "THE BROKER WINS,
   always").
3. **There is no optimistic UI.** The ticket shows a spinner from confirm until the broker
   actually acknowledges. The long comment in `order-ticket.store.ts` explains why at length;
   the short version is that a trader who believes a rejected order is live can hedge against a
   position that does not exist.

---

## 2. Every option, and who supports it

The platform's vocabulary is canonical and broker-neutral. Each connector maps it to its own
wire words in exactly one file (`MStockMaps.cs` for mStock) and declares in its manifest which
values it accepts. **The UI renders from the manifest**, so a broker that supports four order
types shows four; nothing in the frontend knows which broker it is looking at.

### 2.1 Side

| Canonical | mStock | Notes |
|---|---|---|
| `Buy` | `BUY` | |
| `Sell` | `SELL` | On Indian equities a short is a `Sell` under an intraday product, not a separate flag. |

### 2.2 Order type

| Canonical | mStock | Needs limit? | Needs trigger? |
|---|---|---|---|
| `Market` | `MARKET` | — | — |
| `Limit` | `LIMIT` | yes | — |
| `Stop` | **`SL-M`** | — | yes |
| `StopLimit` | **`SL`** | yes | yes |
| `MarketIfTouched` | *not supported* | | |
| `TrailingStop` | *not supported* | | |

> **The crossover that catches everyone once.** mStock's `SL` is a stop-**limit** (it carries
> both a trigger and a price) and `SL-M` is a stop-**market**. The canonical names read the
> other way round. `OrderType.Stop` therefore maps to `SL-M`, and `OrderType.StopLimit` maps to
> `SL`. Getting this backwards turns a protective stop into a limit order that never fills.

A price on a `Market` order is not merely redundant — some exchange gateways read it as a
protection price and reject the order. The connector sends only the price fields the order type
actually needs.

### 2.3 Product (position effect)

`PositionEffect` is a `[Flags]` enum because the concept fragments by market. India's products
are mutually exclusive, so exactly one product bit may be set.

| Canonical | mStock | What it means |
|---|---|---|
| `Delivery` | `CNC` | Cash and carry. Settles into the demat account; no leverage. |
| `Intraday` | `MIS` | Margin intraday square-off. **Auto-squared by the broker near the close.** |
| `Margin` | `MTF` | Margin trading facility. |
| `CarryForward` | `NRML` | Normal, for F&O positions held overnight. |
| `ShortSell` | — | Accepted only alongside `Intraday` or `CarryForward`, where it is redundant. |

`Delivery | ShortSell` and `Margin | ShortSell` are **rejected**: you cannot short a delivery
position in India, and quietly turning that into a CNC sell would liquidate a holding the
trader still has.

> **mStock speaks a different vocabulary on the way out than on the way in.** An order placed
> with `product=MIS` comes back from the order book as `"product": "INTRADAY"`, and the same
> order read through `/order/details` comes back as `"CNC"`. Only the four codes above are
> valid inbound. An empty or null product (positions and holdings both send one) falls back to
> `Delivery` — the *conservative* choice, not the neutral one: a trader who wrongly believes a
> position will square off by itself is in a far worse place than one who wrongly believes it
> will persist.

### 2.4 Validity (time in force)

| Canonical | mStock | Notes |
|---|---|---|
| `Day` | `DAY` | Also arrives back as `NORMAL` from some builds. |
| `Ioc` | `IOC` | Immediate or cancel. |
| `Gtc`, `Gtd` | *not supported* | Indian exchanges do not accept good-till-cancelled. See §7. |
| `Fok`, `AtTheOpen`, `AtTheClose` | *not supported* | |

### 2.5 Variety

| Canonical | mStock (placement) | mStock (margin route) | Notes |
|---|---|---|---|
| `Regular` | `reg` | `regular` | |
| `AfterMarket` | `amo` | `amo` | Queues for the next open. |
| `Cover`, `Bracket`, `Iceberg`, `GoodTillTriggered` | *not supported* | | |

> The margin calculator spells the regular variety **`regular`** while the placement route
> wants **`reg`**. Same vendor, same concept, two spellings — exactly the sort of thing that
> gets fixed at one call site and missed at the other, which is why both live in `MStockMaps`.

### 2.6 Other order fields

| Field | mStock | Notes |
|---|---|---|
| `DisclosedQuantity` | `disclosed_quantity` | mStock's docs say to keep it **above 30%** of the order. The ticket warns below that; it does not block, because the venue is the authority. |
| `Tag` | `tag` | **One short free-text slot, and the ClientOrderId has it.** See §6. |
| `GoodTillDate` | — | Only meaningful with `Gtd`, which mStock does not accept. |
| `AlgoId` | — | Persisted locally. SEBI algo identification runs through the exchange-registered strategy attached to the API key, not an order tag. |
| Fractional quantity | **no** | `fractionalQuantity: false`. Rejected before the broker sees it. |

---

## 3. mStock's Type A order surface

Every route is configurable in `MStockOptions` — mStock has moved paths under `/openapi/typea`
before, and when a vendor renames a route at 09:10 on a trading morning the fix must be a config
push, not a release.

| Purpose | Method | Path | Content type |
|---|---|---|---|
| Place | `POST` | `/openapi/typea/orders/{variety}` | **form-urlencoded** |
| Modify | `PUT` | `/openapi/typea/orders/regular/{order_id}` | **form-urlencoded** |
| Cancel | `DELETE` | `/openapi/typea/orders/regular/{order_id}` | — |
| Cancel all | `POST` | `/openapi/typea/orders/cancelall` | — |
| Order book | `GET` | `/openapi/typea/orders` | — |
| Order details | `GET` | `/openapi/typea/order/details?order_no=&segment=` | `segment` = `E` or `D` |
| Trade book (today) | `GET` | `/openapi/typea/tradebook` | — |
| Trade history | `GET` | `/openapi/typea/trades?fromdate=&todate=` | dates `yyyy-MM-dd` |
| **Margin + charges** | `POST` | `/openapi/typea/margins/orders` | **JSON** |
| **Convert position** | `POST` | `/openapi/typea/portfolio/convertposition` | **form-urlencoded** |

Headers on every call: `X-Mirae-Version: 1`, `Authorization: token {api_key}:{access_token}`,
`X-PrivateKey: {api_key}`.

### 3.1 Four things about this API that have already caused bugs

**Writes are form-encoded, not JSON.** Placement, modification and position conversion all take
`application/x-www-form-urlencoded`. The margin calculator is the single documented exception
and takes JSON. This matters more than it sounds, because of the next item.

**Business failures arrive as HTTP 200.** An error is `{"status": "error", "message": ...}`
inside a 200 response. So sending JSON to a form route does not fail loudly — it comes back
looking like a *rejected order* rather than a malformed request. `MStockApi` unwraps and checks
the envelope centrally, once, so no facet can forget.

**Modify is a REPLACE, not a patch.** The route documents the full order context — variety,
tradingsymbol, exchange, transaction_type, product — alongside the fields being changed. Send
only the deltas and the broker fills the rest from its own defaults. A trader amending the price
of a CNC (delivery) order could get an MIS (intraday) order back, and discover it auto-squared
at 15:20 having never asked for an intraday position. `MStockOrders.ModifyAsync` therefore reads
the live order first and carries every unchanged value through verbatim, plus `modqty_remng`
(the still-working quantity) for part-filled orders.

**Cancel-all does not report a count.** The documented success payload is a single
`{"order_id": "..."}` — the same shape as an ordinary cancel. Reading a count out of that gives
zero, and "0 cancelled" after a successful panic-button press is the most dangerous thing this
connector could say: it is indistinguishable from "the sweep did nothing", and a trader who
believes that will start cancelling by hand while the sweep is still settling. The connector now
snapshots the book before and after and reports how many working orders actually stopped
working.

### 3.2 Status vocabulary

| mStock | Canonical | Why |
|---|---|---|
| `COMPLETE`, `COMPLETED`, `FILLED`, `EXECUTED` | `Filled` | |
| `REJECTED`, `AMO REJECTED` | `Rejected` | |
| `CANCELLED`, `CANCELLED AMO`, … | `Cancelled` | |
| `OPEN`, `TRIGGER PENDING`, `MODIFIED`, `CANCEL PENDING`, … | `Open` | Resting at the exchange. |
| **`TRIGGERED`** | `Open` | A stop that has **fired and is now working** — *not* filled. Booking it as `Filled` would create a position that does not exist. |
| **`PENDING`** (with `status_message: "CONFIRMED"`) | `Open` | Confirmed and resting. Distinct from the "… PENDING" states below. |
| `PUT ORDER REQ RECEIVED`, `VALIDATION PENDING` | `Submitted` | **In flight between us and the exchange, NOT acknowledged.** Treating these as `Open` is how a trader believes they are in the market when they are not. |
| `PARTIALLY FILLED`, `PARTIAL` | `PartiallyFilled` | |
| `EXPIRED`, `LAPSED` | `Expired` | |
| anything else | `Unknown` | Degrades loudly — see below. |

Reading the whole order book must not fail because mStock introduced one new status overnight;
that would blank the blotter and hide the nineteen orders we *do* understand. So an unmapped
status degrades to `Unknown` and the raw vendor text is shown verbatim. `Unknown` is
non-terminal and non-working, so the risk gate refuses to act on it and the UI shows
"Checking with your broker…" with no resubmit button.

Single-order reads use the strict mapping, where failing is the right answer.

mStock also reports a partially executed order as `OPEN` with a non-zero filled quantity rather
than with its own status; the connector derives `PartiallyFilled` so the risk engine can see the
exposure that already exists.

---

## 4. Order lifecycle

`OrderState` (platform) is richer than `OrderStatus` (canonical broker status). The blotter
colours on state.

```
PendingSubmit ──► RiskChecked ──► Submitted ──► Acknowledged ──► PartiallyFilled ──► Filled
      │                │              │              │                  │
      │                └──► Rejected  │              ├──► Cancelled     └──► Cancelled
      │                               │              └──► Rejected / Expired
      └──► Rejected (risk gate)       └──► Unknown  ◄── a timeout: state genuinely not known
```

`PendingSubmit` exists so a crash mid-send is recoverable. `Unknown` exists because pretending a
status is known when it is not is how phantom orders happen.

**An accepted amendment moves the order itself**, not just its event log. `Order.Request` holds
the order's *current* terms and is updated by `RecordAmendment` when the broker accepts. A
blocked or failed amendment leaves it alone, because the order is genuinely still working on the
old terms. Every attempt — accepted or not — appends an event, so the audit trail replays the
order's whole life.

---

## 5. The risk gate

Ten rules run before any order leaves the platform. They are **fail-closed**: a rule that cannot
reach its data denies rather than allows.

| Rule | What it stops |
|---|---|
| `KillSwitchRule` | Everything, while the tenant's kill switch is engaged. |
| `CapabilitySupportedRule` | Options the broker's manifest does not declare. |
| `FractionalQuantityRule` | Fractional quantities at brokers that trade whole units. |
| `MaxQuantityRule` | Fat-finger quantities. |
| `MaxOrderValueRule` | Fat-finger notionals. |
| `PriceBandSanityRule` | A limit price far from the last traded price. |
| `MaxOpenPositionsRule` | Breaching the account's position count. |
| `DailyLossLimitRule` | Trading on after the day's loss limit. |
| `InstrumentAllowDenyRule` | Instruments the tenant has excluded. |
| `VenueMarketHoursRule` | Orders outside the venue's session. |

Reducing exposure is never blocked (`IsReducingExposure`), and cancel-all never consults the
gate at all. **An amendment is never treated as reducing exposure** — the safe direction is to
apply every rule.

Position conversion runs the kill-switch check only. The position already exists; the quantity,
price-band and order-value rules are about *opening* exposure and have nothing to say about a
settlement basis.

---

## 6. The ClientOrderId, and why it lives in the tag

mStock has **no client-order-id field**. The only free text it will carry is `tag`, which is
short and rejects punctuation — a 36-character GUID does not fit.

So the platform folds the ClientOrderId into the tag as 20 hex characters (80 bits: far more
than enough to be unique across a trading day) and keeps a process-local index mapping it back.
The trader's own `Tag` and the `AlgoId` are persisted locally against the ClientOrderId instead.

That index is a **convenience, not the system of record**. The durable
ClientOrderId → BrokerOrderId mapping belongs to the order store above the connector, which
persists it *before* the placement call — precisely so a timeout can be reconciled against the
order book rather than retried into a duplicate.

The `tag` field also arrives in three different shapes: a string in the placement request, `[]`
in the order book, and `null` in `/order/details`. A lenient converter handles all three.

---

## 7. What mStock cannot do

Named explicitly, because "the button is missing" is a worse answer than "the broker does not
offer it".

| Feature | Status |
|---|---|
| **GTT (good-till-triggered)** | **Not on the Type A surface at all.** The documentation has no such section. A resting multi-day order would have to be synthesised by the platform. |
| Good-till-cancelled / good-till-date | Indian exchanges do not accept them. |
| Bracket and cover orders | `bracket: false`, `cover: false`. |
| Iceberg orders | Not offered. Disclosed quantity is the nearest thing. |
| Atomic basket placement | mStock's "Basket APIs" create *saved* baskets (create / fetch / rename / delete / calculate), not atomic multi-leg placement. `PlaceBasketAsync` therefore loops, `basket.atomic: false`, and the ticket warns that a partial fill leaving you half-hedged is possible. On a mid-basket failure it stops and returns the ids of the legs that *did* go through, because leaving live orders the platform has no record of is by far the worse failure. |
| Market protection | The order book returns a `market_protection` field; nothing sets it. |

### Why GTT was not synthesised

It would need a durable store, a trigger evaluator and a scheduler. **Persistence is currently
in-memory** (`Modules/Trading/Infrastructure/InMemory/`, dev-only and says so) — nothing
survives a restart. A GTT order that silently disappears on a deploy is materially worse than no
GTT at all, because the trader believes their stop is armed. This is blocked on the EF Core +
Postgres work, not on the connector.

---

## 8. Pre-trade estimates

`POST /openapi/typea/margins/orders` returns **both** the margin the broker will block *and* an
itemised charge schedule. The manifest declares `marginEstimate: true` and
`chargesEstimate: true`.

```jsonc
{
  "additional": 1699.37,
  "charges": {
    "brokerage": 80.92,
    "transaction_tax": 0, "transaction_tax_type": "stt",
    "exchange_turnover_charge": 0, "sebi_turnover_charge": 0,
    "stamp_duty": 0,
    "gst": { "igst": 0, "cgst": 0, "sgst": 0, "total": 0 },
    "total": 80.92
  },
  "total": 1699.37        // margin BLOCKED — not the sum of the charges
}
```

> `total` and `charges.total` are different things and both are called "total". `total` is
> capital blocked; `charges.total` is what the trade costs. **Adding them together overstates
> the cost of every trade.**

The route reports what is *required* but not what is *available*, so `MarginEstimate.Available`
is null — which the contract defines as "unknown, do not block the trader", not as an assertion
that the funds are there. Buying power comes from the fund summary, a different route.

Zero-valued charge lines are dropped: mStock returns the full schedule with zeros for charges
that do not apply, and a confirmation screen listing six charges of ₹0.00 buries the one that is
real. The transaction tax is labelled with the broker's own word (`STT` on equity, `CTT` on
commodities) so a commodity trader is not shown a line saying "STT".

These are the **broker's** numbers, deliberately. The Paper connector ships a local SEBI/STT/GST
schedule for backtesting, but reporting that under mStock's name would diverge the moment a rate
changed.

---

## 9. Position conversion

Moves an open position between margin products — the intraday-to-delivery rescue, taken at 15:15
by someone who has decided not to be squared off.

**It is not an order.** Nothing trades, the position keeps its size and entry price, and no fill
is generated. Modelling it as an order would put a phantom fill in the blotter and double the
position in every P&L that reads from trades. It *is* order-affecting, so it is audited,
respects the kill switch, and is never retried automatically — converting twice converts twice.

The direction matters: **intraday → delivery increases the capital required**, because delivery
is not leveraged. It can fail on margin, and it fails at exactly the moment a trader most wants
it. The dialog warns before the button rather than after the error.

`POST /openapi/typea/portfolio/convertposition` takes `tradingsymbol`, `exchange`,
`transaction_type`, `position_type` (only `DAY` is documented — an overnight position has
already settled into its product), `quantity`, `old_product`, `new_product`. It answers with an
empty 200: no id, nothing to bind. The API therefore returns `204 No Content` and the caller
confirms by re-reading positions.

`from` is required rather than looked up, because a hedged account can hold the same instrument
under two products at once and a conversion that guesses converts the wrong position.

**The Paper connector does not simulate this** (`positionConversion: false`). Worth naming as a
*rehearsal gap*: a strategy that converts intraday positions to delivery cannot be fully
exercised on paper before it touches real money.

---

## 10. HTTP API

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/orders` | Place. |
| `POST` | `/api/orders/{id}/modify` | Amend. |
| `POST` | `/api/orders/{id}/cancel` | Cancel one. |
| `POST` | `/api/orders/cancel-all` | The panic button. Omitting `brokerLinkId` sweeps **every** link. |
| `GET` | `/api/orders` | Blotter. Filters: `brokerLinkId`, `instrument`, `openOnly`, `unresolvedOnly`, `from`, `to`, `limit`. |
| `GET` | `/api/orders/{id}` | One order, with its full event history. |
| `GET` | `/api/orders/trades` | Fills. Filters: `brokerLinkId`, `instrument`, `from`, `to`. |
| `POST` | `/api/orders/estimate` | Margin and itemised charges. |
| `POST` | `/api/portfolio/positions/convert` | Move a position between products. |

> **Filter parameters are nullable.** A non-nullable `bool` in a minimal API is a *required*
> query parameter — `GET /api/orders?openOnly=false` used to throw "Required parameter bool
> unresolvedOnly was not provided" and return a 500, so the blotter never loaded at all.
> Omitting a filter means "do not filter", never "reject the request".

Identity never travels in a body. The tenant and user come from the authenticated principal; a
body-supplied tenant id is an authorisation bypass with extra steps.

`/api/orders/trades` returns partial results with named `warnings` when a link cannot be read,
rather than one error page. Someone reconciling five accounts is far better served by four of
them plus a named gap. `isPartial` must be surfaced.

---

## 11. The UI

Every screen renders from the manifest. **There is exactly one permitted branch on a connector
id in the whole frontend** — the lookup that fetches the manifest. If a screen needs to know
something about a broker, the fix is a manifest field on the backend and a read of it, never a
conditional on which broker this is.

| Surface | What it offers |
|---|---|
| **Order ticket** (`/trade/:brokerLinkId/:instrument`) | Side, order type, product, validity, variety, quantity, prices, disclosed quantity — each list straight off the manifest. `B` / `S` / `Esc` shortcuts, scoped to the panel. |
| **Blotter** (`/orders`) | Live orders, canonical state *and* the broker's own words verbatim. Per-row amend and cancel; cancel-all. |
| **Modify dialog** | Fields gated on `orders.modifiable`. Sends **only what changed**. |
| **Fills** (`/fills`) | Executions, chunk by chunk, with a date range. |
| **Positions** (`/positions`) | Square off, add to, convert — per **leg**. |
| **Watchlist / Holdings** | Buy and Sell entry points; holdings caps the quantity at the unpledged amount. |

### Entry points prefill; they never place

Square-off, Buy and Sell all open the ticket with side, quantity and product staged, and stop
there. A square-off is a real trade at a real price, so review-then-confirm still applies. A
one-click trade from a scrolling list is exactly the mis-click this design refuses to allow.

Square-off carries the **product** across too: exiting an intraday position with a delivery
order does not close it — it opens a second, opposite delivery position and leaves the intraday
one to square off on its own.

### Actions live on the leg, not the blended row

A blended position is an analytical sum across brokers; you cannot square off "the sum", because
no single account holds it. Every action hangs off a leg, which names exactly one broker link
and one product — the two things an order or a conversion needs.

### Client-side checks

Checked in the form *as well as* at the broker. A rejection from the exchange costs a round
trip, arrives as vendor text nobody can act on ("RMS rule: quantity"), and on a fast-moving
instrument arrives after the price the trader wanted has gone.

| Check | Behaviour |
|---|---|
| Lot size | **Blocks.** Suggests the nearest valid multiple. Binding on F&O, where the lot is the contract size. |
| Tick size | **Blocks.** Suggests the nearest tick. Uses integer arithmetic — `1250.15 % 0.05` is not zero in IEEE-754, and a validator that rejects a good price is worse than none. |
| Disclosed quantity | **Warns** below 30%. Venue policy can change and the broker is the authority; refusing an order the exchange would have taken is the worse failure. |
| Market closed | **Offers** to switch to an after-market order, where the broker has one. |

### `orders.modifiable` is case-insensitive — always use `canModifyField()`

It is a list of **values**, not property names, so the API's camelCase policy does not touch it:
the manifests ship `"LimitPrice"` and it arrives as `"LimitPrice"`, while call sites naturally
reach for `'limitPrice'`. The mismatch does not throw and does not warn — the field simply never
renders, and a trader quietly loses the ability to amend a price. This is the exact failure
[`../STATUS.md`](../STATUS.md) records catching once already, and it was still live on the
ticket's disclosed-quantity field. **Never call `.includes()` on `modifiable` directly.**

---

## 12. Failure modes worth knowing

| Symptom | Cause | Where it is handled |
|---|---|---|
| Placement times out | Ack lost; order may or may not exist | ClientOrderId persisted first; reconcile against the book, never retry |
| Order shows "Checking with your broker…" | `state == Unknown` | No status shown, no resubmit offered — the trader must not act on a guess |
| Cancel-all reports fewer than requested | Partial sweep, or a broker with no atomic cancel-all | `isPartial` surfaced prominently and held until dismissed |
| An order status we do not recognise | Vendor vocabulary changed | Degrades to `Unknown` with the raw text shown; the rest of the book still loads |
| Amendment refused | Risk gate or broker | Order stays on its old terms — which are the ones still live |
| A basket leg fails | No atomic basket at mStock | Stops immediately; returns the ids of the legs already placed |
| Charges show "—" | Broker does not report per-trade charges | An em dash, never a zero: "not reported" and "cost nothing" are different claims |

---

## 13. Smoke test

Automated conformance uses recorded fixtures and proves the *mapping*, not the API. Only this
proves the API. Run it against a real mStock account, out of hours where possible, with the
smallest quantity that will trade.

- [ ] Link an mStock account (OTP and, separately, TOTP).
- [ ] `POST /api/orders/estimate` — confirm margin and charges return, and that the margin
      figure is not the charges figure.
- [ ] Place a **limit** order well away from the market so it rests. Confirm it in mStock's own
      UI.
- [ ] Amend its **price**. Confirm in mStock's UI — and confirm **the product did not change**
      (§3.1).
- [ ] Amend its **quantity** on a part-filled order; check `modqty_remng` behaviour.
- [ ] Place an **SL-M** order; confirm `OrderType.Stop` produced `SL-M`, not `SL`.
- [ ] Let a stop trigger; confirm the status maps to `Open`, **not** `Filled`.
- [ ] Cancel the order. Confirm in mStock's UI.
- [ ] Place three resting orders and hit **cancel-all**. Confirm the reported count matches
      reality and that a partial sweep says so.
- [ ] Place an order after the close as an **AMO**; confirm it queues.
- [ ] Fetch the order book, today's trade book, and a **multi-day** trade history; compare
      against the UI.
- [ ] Open an intraday position and **convert it to delivery**; confirm in mStock's UI and
      confirm no phantom fill appears in the blotter.
- [ ] Leave the session until after midnight IST and confirm the re-auth prompt appears
      **before** the first failed call.

| Date | Tester | Result |
|---|---|---|
| — | — | **Not yet performed** |

---

## 14. Where the code is

| Concern | File |
|---|---|
| Canonical vocabulary | `src/Akshaya.SharedKernel/TradingEnums.cs` |
| Order contract | `src/Akshaya.Connectors.Abstractions/Orders.cs` |
| Capability declaration | `src/Akshaya.Connectors.Abstractions/ConnectorManifest.cs` |
| **mStock mapping — the only place its wire words appear** | `src/connectors/Akshaya.Connector.MStock/MStockMaps.cs` |
| mStock order routes | `src/connectors/Akshaya.Connector.MStock/MStockOrders.cs` |
| Place / modify / cancel / cancel-all / convert | `src/Modules/Trading/Application/` |
| Risk rules | `src/Modules/Trading/Domain/Rules/` |
| Order aggregate and state machine | `src/Modules/Trading/Domain/Order.cs`, `OrderStateMachine.cs` |
| HTTP surface | `src/Akshaya.Api/Endpoints/OrderEndpoints.cs`, `PortfolioEndpoints.cs` |
| Ticket, blotter, fills, dialogs | `apps/web/src/app/features/` |

### Related

- [`../connectors/mstock.md`](../connectors/mstock.md) — the connector's own notes and quirks
- [`../connectors/mstock-login-response.md`](../connectors/mstock-login-response.md) — read
  before touching any mStock DTO
- [`../STATUS.md`](../STATUS.md) — what is verified and what is not
- [`../ADDING-A-BROKER.md`](../ADDING-A-BROKER.md) — the checklist for the next connector
- [`../compliance/india.md`](../compliance/india.md) — SEBI algo approval
