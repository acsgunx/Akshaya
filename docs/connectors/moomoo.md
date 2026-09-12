# Connector: moomoo

- **Id:** `moomoo`
- **Market:** US listing venues (NYSE, Nasdaq, NYSE American, NYSE Arca, Cboe BZX) and HKEX — equities,
  ETFs and options; Hong Kong indices for quotes
- **API:** Futu OpenAPI through **OpenD**, a gateway daemon — TCP with a binary frame and JSON bodies
- **Docs:** https://openapi.moomoo.com/moomoo-api-doc/en/ · protocol definitions:
  https://github.com/FutunnOpen/py-futu-api/tree/master/futu/common/pb
- **Hosting:** gateway — an OpenD the operator runs (see [ADR 0008](../adr/0008-operator-run-gateways-keepalive-and-new-venues.md))
- **Status:** implemented from the published `.proto` definitions, **never run against a live OpenD**.

> ⚠️ Every protocol id, field name and enum value here was read from the Futu API `.proto` files and the
> Python SDK's framing code on **2026-09-11**, and from the protocol and quota pages of the documentation
> on the same day. The connector compiles; no frame has been exchanged with a real OpenD. The JSON shapes
> in `MoomooDtos.cs` are the most likely place reality differs.
>
> Run the smoke test at the end of this file before trusting anything else here.

## Running OpenD

OpenD holds the moomoo session. The connector never sees a moomoo password; it talks to an OpenD that is
already signed in.

1. Download OpenD from the moomoo API page. **Use the command-line build** for anything unattended: the
   window build refuses to unlock trading over the API (see *Unlocking* below).
2. In `OpenD.xml`, set the login account, the API port (`api_port`, default **11111**), and a bind
   address the API host can reach. Leave `rsa_private_key` **unset** — this connector speaks the
   unencrypted protocol, so run OpenD on loopback or a private network only.
3. Start OpenD and complete the first-login steps it asks for: a phone verification code, and — once per
   moomoo login — the **API questionnaire and agreements**. OpenD serves nothing until they are done.
4. Tell the platform where it is, unless it is on the API's own machine at port 11111:

   ```json
   "Connectors": {
     "Gateways": {
       "moomoo-opend": { "Host": "10.0.0.5", "Port": 11111 }
     }
   }
   ```

   With nothing configured the host looks for OpenD on `127.0.0.1` at the manifest's port, which is what a
   developer running both on one machine wants.

`perCredential` is true: one OpenD is one moomoo login. In a multi-user deployment a per-account address
goes under `Credentials:<account id>`, but the link handshake runs before the account id is known and
always uses the default address — see ADR 0008's limits.

## The protocol

Every message is a 44-byte header followed by a body. The header is **little-endian**, which the SDK's
`"<1s1sI2B2I20s8s"` struct format confirms.

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | `"FT"` |
| 2 | 4 | protocol id |
| 6 | 1 | body format: 0 protobuf, **1 JSON** |
| 7 | 1 | protocol version, 0 |
| 8 | 4 | serial number, incrementing |
| 12 | 4 | body length |
| 16 | 20 | SHA-1 of the **plaintext** body |
| 36 | 8 | reserved |

Bodies are the protobuf messages in the **proto3 JSON mapping**, which OpenD accepts in both directions —
so there is no protobuf runtime and no vendor SDK here. Three consequences of that mapping are handled in
`MoomooWireFormat.cs`:

- **64-bit integers are JSON strings.** Account, order and fill ids exceed 2^53, and a reader that parsed
  them as doubles would round an order id to a neighbouring one and cancel the wrong order.
- **Doubles can be `"NaN"`.** Read, then discarded as absent, rather than failing a whole order book.
- **A real proto enum renders as its name.** Almost every enum in these protocols is declared `int32`, but
  `ProgramStatus.type` is a genuine enum and may arrive as `"ProgramStatusType_Ready"`.

The connection (`MoomooConnection.cs`) sends `InitConnect`, then a `KeepAlive` at the interval OpenD
announces, correlates responses by serial number, and routes pushes **by protocol id, never by serial** —
OpenD numbers its pushes itself, and those numbers can collide with a serial we are waiting on.

A SHA-1 mismatch is reported as "OpenD is most likely configured with an RSA key": the digest covers the
plaintext, so an encrypted body can never match it.

### Protocols used

| Purpose | Id | Name |
|---|---|---|
| Handshake | 1001 | `InitConnect` |
| Login state | 1002 | `GetGlobalState` |
| Status pushes | 1003 | `Notify` |
| Heartbeat | 1004 | `KeepAlive` |
| Accounts | 2001 | `Trd_GetAccList` |
| Unlock trading | 2005 | `Trd_UnlockTrade` — `pwdMD5`, the lowercase hex MD5 of the trade password |
| Order and fill pushes | 2008 | `Trd_SubAccPush` → 2208 `Trd_UpdateOrder`, 2218 `Trd_UpdateOrderFill` |
| Funds | 2101 | `Trd_GetFunds` |
| Positions | 2102 | `Trd_GetPositionList` |
| Today's orders | 2201 | `Trd_GetOrderList` |
| Place | 2202 | `Trd_PlaceOrder` |
| Modify, cancel, cancel-all | 2205 | `Trd_ModifyOrder` — op 1 modify, op 2 cancel, `forAll` for cancel-all |
| Today's fills | 2211 | `Trd_GetOrderFillList` |
| History | 2221, 2222 | `Trd_GetHistoryOrderList`, `Trd_GetHistoryOrderFillList` |
| Subscribe | 3001 | `Qot_Sub` → pushes 3005 `Qot_UpdateBasicQot`, 3013 `Qot_UpdateOrderBook` |
| Order book | 3012 | `Qot_GetOrderBook` |
| Candles | 3103 | `Qot_RequestHistoryKL`, paged by `nextReqKey` |
| Security lists and lookups | 3202 | `Qot_GetStaticInfo` |
| Quotes | 3203 | `Qot_GetSecuritySnapshot`, up to 400 securities |
| Option chain | 3209 | `Qot_GetOptionChain` |

Every trade write carries a `packetID` of the connection id and a serial number — OpenD's replay
protection. The frame carries the same serial.

## Auth flow

```
BeginAsync / ContinueAsync (one walk; the wizard's "check again" re-runs it)
  connect + InitConnect      ─ fails ─► GatewayRequired("start OpenD and sign in")
  GetGlobalState             ─ not signed in ─► GatewayRequired(OpenD's own status text)
  Trd_GetAccList             ─► pick the account
  live account + password    ─► Trd_UnlockTrade
  ─► Completed(session naming the account)
```

**Choosing the account.** Candidates are active accounts in the requested environment (`real` or `paper`)
authorised for the US or Hong Kong. An `account_id` credential wins. Otherwise one candidate is taken, and
among several the one reaching the most in-scope markets (a universal account over a single-market one)
when that choice is unique. Anything else is returned to the user with the candidates listed, because it
is a decision about whose money moves.

**The session** carries no token — OpenD holds the real one — so `AccessToken` is an opaque, non-secret
`opend:<account id>` marker. The trading environment and the authorised markets travel in `Extras`.

### Unlocking

Two sentences in OpenD's documentation decide this:

- *"As long as one connection is unlocked, all other connections can call the transaction interface."*
  One unlock at link time serves every request connection and the stream.
- *"The GUI version of OpenD does not support unlocking via the unlock interface."*

So when a `trade_password` is supplied for a live account, the link unlocks. A refusal that mentions the
password fails the link with `InvalidCredentials`; any other refusal — the window build, most often — is
logged and the link completes, because the trader unlocks in OpenD's window. An order placed while trading
is still locked fails with `ReauthRequired` and a message naming both routes.

Paper accounts need no unlock.

### Why a one-day session

OpenD's session has no expiry of its own — it lasts until OpenD stops — and `SessionMonitor` treats a
session with no declared expiry as dead. The manifest declares `sessionLifetime: 1.00:00:00` as a
deliberate daily re-check: relinking confirms OpenD is still signed in to the same account and re-unlocks
after an overnight OpenD restart. `refreshSupported` is false (the loader forbids it for
`GatewaySession`), and `RevokeAsync` does not lock trading, because the unlock is shared with every other
client of that OpenD.

## Rate limits

From each protocol's "Interface Limitations":

| Protocol | Limit |
|---|---|
| Place order | 15 per 30 s per account, ≥ 0.02 s apart |
| Modify / cancel | 20 per 30 s per account, ≥ 0.04 s apart |
| Funds, positions, orders, fills | 10 per 30 s — **only when `refreshCache` is set**, which this connector never sets |
| Snapshot | 60 per 30 s, 400 securities each |
| Option chain | 10 per 30 s, window ≤ 30 days |
| Unlock | 10 per 30 s per user |

The manifest's buckets are per second and per minute, which cannot express a thirty-second window, so they
are set conservatively: `orders` 1/s and 15/min, `quotes` and `data` 2/s and 60/min. Under-claiming costs
throughput; over-claiming gets requests refused.

**Subscription quota** is separate and per account: 100 units under HKD 10,000 in assets, rising to 2,000.
A security's quote is one unit and its order book another. The stream counts units against
`MoomooOptions.SubscriptionQuota` (setting `subscriptionQuota`), and the manifest declares the smallest
tier, 100.

## Symbology

The native symbol is the SDK's `MARKET.CODE` notation.

| Kind | Native | Canonical |
|---|---|---|
| US equity / ETF | `US.AAPL`, `US.BRK.B` | `XNAS:AAPL:Equity`, `XNYS:BRK.B:Equity` |
| HK equity | `HK.00700` | `XHKG:00700:Equity` |
| HK index | `HK.800000` | `XHKG:800000:Index` |
| US option | `US.AAPL260918C200000` | `XNAS:AAPL:OPT:2026-09-18:200:Call` |

- **Split at the first dot.** `US.BRK.B` is market `US`, code `BRK.B`.
- **Hong Kong codes are five digits**, `00700` not `700`, so the same share keys identically whichever
  connector saw it first.
- **The listing venue is not in the symbol.** It comes from `exchType` in static information — so decoding
  a US security always needs the cache, and the facets fill it from OpenD before they map a row.
  Encoding is structural: OpenD addresses a US security by symbol alone, so any US MIC reaches it.
- **An option keys under its underlying's listing venue** — the rule every connector here follows.
- **Option contract codes come only from the option chain.** Placing, quoting or streaming an option loads
  its chain for that expiry first.

### A known asymmetry: NYSE Arca and Cboe BZX

OpenD's exchange list has NYSE, Nasdaq and NYSE American, but not NYSE Arca or Cboe BZX. An ETF listed on
those arrives labelled as one of the three, so this connector decodes SPY under whatever OpenD calls it
rather than `ARCX`. Orders route correctly either way. What differs is the canonical key another broker
gives the same fund, and the blended portfolio cannot merge the two without an ISIN, which OpenD does not
provide.

## Mapping

Everything lives in `MoomooMaps.cs`.

| Canonical | OpenD |
|---|---|
| `Market` | `OrderType_Market` (2) |
| `Limit` | `OrderType_Normal` (1) — HKEX's **enhanced** limit order in Hong Kong |
| `Stop` / `StopLimit` | 10 / 11 |
| `MarketIfTouched` | 12 |
| `Day` / `Gtc` | 0 / 1 — OpenD's GTC lapses after 90 calendar days |

**Position effects are `Delivery` and `ShortSell` only.** OpenD has no product field: margin use is
decided by the account type, and a sell beyond the long position on a margin account is a short sale.
Accepting `Margin` would promise a funding choice this connector cannot make. OpenD reports a short sale's
side as `SellShort` and a cover as `BuyBack`; both map to the canonical side plus `ShortSell`.

Statuses that are deliberate rather than obvious:

| OpenD | Canonical | Why |
|---|---|---|
| `TimeOut` (4) | `Unknown` | Documented as "result unknown". The order may be live; `Rejected` would invite a duplicate |
| `Cancelling_All` (13) | `Open` | A cancel in flight against an order that can still fill |
| `Cancelling_Part` (12) | `PartiallyFilled` | Same, with shares already filled |
| `Disabled` (22) | `Unknown` | A Hong Kong order the user switched off, which can be switched back on. No terminal status is honest |
| `FillCancelled` (24) | `Rejected` | moomoo voided the fills |
| `WaitingSubmit`, `Submitting` | `Submitted` | Accepted by moomoo, not yet at the exchange |

The order book returns every type the account has used, including ones this connector never sends, so the
read side folds Hong Kong's absolute and special limit orders onto `Limit`, auction orders onto `Market`,
limit-if-touched onto `StopLimit` and both trailing types onto `TrailingStop`.

The **ClientOrderId travels whole** in `remark` (64 bytes) as 32 hex characters and comes back on every
order row and push — no truncation and no local index.

## Portfolio

- **Positions and holdings are a partition.** OpenD returns one position list. Long cash equities and ETFs
  are holdings; options and shorts are positions. Reporting a stock in both would double its unrealised
  P&L, because the blended portfolio adds the two lists together.
- **One balance, not one per currency.** For a universal account OpenD requires the caller to name a
  currency and converts the whole account into it; it reports no per-currency cash for securities
  accounts. The balance is requested in USD when the account trades the US, otherwise HKD.
- **Average cost** is `averageCostPrice`, the documented replacement for the deprecated `costPrice`.
- **`PledgedQuantity` is zero.** OpenD's `canSellQty` is lowered by open sell orders as well as by
  unsettled sales, so reading it as "unsettled" would misstate both.

## Market data

- **Quotes use snapshots**, not basic quotes: no subscription, best bid and ask included, 400 securities a
  request.
- **Depth needs a subscription.** OpenD serves an order book only for a subscribed security, so a depth
  read subscribes first, and that unit of quota is held until the connector's connection closes.
- **Candles are unadjusted**, like every connector here, and paged by `nextReqKey`. The window is sent as a
  naive time in the market's zone.
- **The option chain is assembled**: `Qot_GetOptionChain` names the contracts, one snapshot prices them
  with open interest. OpenD does not serve expired chains.

## Streaming

A dedicated OpenD connection, separate from the request one, so pushes never buffer on a connection whose
request has returned.

- The desired subscription set is replayed after every reconnect; nothing on the push path adds to it.
- The quota is counted locally and refused with an explanation before OpenD refuses it.
- **OpenD will not unsubscribe within a minute of subscribing.** A refused unsubscribe is retried once the
  minute has passed.
- Order and fill pushes are filtered by account id, so another client of the same OpenD cannot put its
  orders in this trader's blotter.
- `Notify` pushes turn OpenD's own trouble — signed out, disconnected from moomoo's servers, kicked out by a
  login elsewhere — into `Degraded`, because a signed-out OpenD keeps the socket open and just stops sending.

## Not offered, and why

| Capability | Manifest | Why |
|---|---|---|
| Trailing stops | not in `types` | moomoo trails by `trailType` and `trailValue`; the contract carries neither |
| Margin and charges estimates | `false` | OpenD prices fees only after placement, and has no equity margin estimate |
| Position conversion | `false` | No product types to convert between |
| Fractional shares | `false` | Not confirmed for the API; under-claimed |
| Fills on paper accounts | returns `NotSupported` | OpenD serves no deal list for paper trading |
| China Connect, Singapore, Japan, Australia, Canada, Malaysia | not in `venues` | No trading calendars for them yet |
| Warrants, CBBCs, futures | not in `assetClasses` | No canonical class for Hong Kong structured products; futures need their own calendars |
| Encrypted OpenD | not supported | The RSA key would have to live in the session; run OpenD on a private network instead |

## Quirks and gotchas

- **`orederCount`** — the proto spells the order-book field that way. The DTO keeps the misspelling.
- **Unknown codes are not errors** in `Qot_GetStaticInfo`: OpenD returns a row marked `delisting` named
  "unknown stock". The factory treats that as not found.
- **Securities outside the declared venues** — an OTC name, a warrant — are remembered as out of scope,
  and order and position reads skip their rows. A security OpenD cannot describe at all still fails the
  read, loudly.
- **OpenD's messages are localised.** Error classification matches English and Chinese phrases, narrowly,
  and defaults a trade write to `OrderRejected` — never to anything the host would retry.
- **`Trd_ModifyOrder` re-sends quantity and price together**, the unchanged one at its current value,
  because that is what the SDK sends. So modify and cancel read the order back first — which is also how
  they learn its market.

## Smoke test — run this before trusting anything

Record the date and result here.

1. Start OpenD (command-line build), sign in, complete the questionnaire. Confirm the platform's gateway
   status for `moomoo-opend` shows it running.
2. Link a **paper** account (`environment: paper`). Confirm the account id and markets in the session.
3. Quote `XNAS:AAPL:Equity` and `XHKG:00700:Equity`. Compare with the moomoo app.
4. Ingest the security lists. Record the counts. Check that `SPY` decodes, and note which venue OpenD gives it.
5. Place a 1-share limit buy far from the market on the paper account. Confirm the order id, find it in the
   moomoo app, and confirm the ClientOrderId comes back in the order book.
6. Modify its price, then its quantity alone. Cancel it. Place two and cancel-all.
7. Connect the stream; subscribe to AAPL in Full mode; confirm ticks, a book, and an order update when an
   order is placed.
8. Link a **live** account with the trade password on the command-line OpenD. Confirm the unlock, then read
   positions, holdings and the balance and reconcile them with the app.
9. Repeat the link against the window build of OpenD without a password; confirm an order fails with the
   "unlock trading" message until trading is unlocked in the window.
10. Load an option chain for AAPL at the next monthly expiry.

| Date | Who | Result |
|---|---|---|
| — | — | Not yet run against a live OpenD |

## Open questions

- **Does OpenD emit lowerCamelCase or original field names in JSON?** The DTOs use the proto names, which are
  already lowerCamelCase for every field read here, so either works — unless a field with an underscore is
  ever needed.
- **Is `clientVer` 1000 acceptable** to current OpenD builds? OpenD refuses clients it considers too old.
- **Does a market order need `price: 0`**, as the SDK sends, or can it be omitted? This connector sends 0.
- **Are history filter times in the market's zone?** Assumed, matching every other time string OpenD sends.
- **What exchange does OpenD report for NYSE Arca ETFs?** Step 4 of the smoke test answers it.
