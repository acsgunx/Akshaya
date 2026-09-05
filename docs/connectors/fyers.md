# Connector: FYERS

- **Id:** `fyers`
- **Market:** India — NSE and BSE, cash and equity derivatives
- **API:** FYERS API v3, REST
- **Docs:** https://myapi.fyers.in/docsv3
- **Hosting:** in-process
- **Status:** implemented from the published documentation, **never run against a live account**.

> ⚠️ Every endpoint, field name, enum value and limit below was read from the v3 documentation
> on **2026-09-06**. Nothing here has been confirmed against a real FYERS account, so the
> response shapes in `FyersDtos.cs` are the most likely place reality differs from this code.
> The documentation site is a JavaScript application behind Cloudflare — `curl` gets an empty
> shell, so re-check it through a real browser.
>
> The **symbol master** is the one part that HAS been verified against live data: all three
> NSE/BSE files were downloaded and parsed, and 4,000 instruments round-tripped through the
> symbol translator in both directions without loss. See "Symbology" below.
>
> Run the smoke test at the end of this file before trusting anything else here.

## Endpoints

| Purpose | Method and path |
|---|---|
| Base | `https://api-t1.fyers.in` |
| Authorize (browser) | `GET /api/v3/generate-authcode?client_id=&redirect_uri=&response_type=code&state=` |
| Token exchange | `POST /api/v3/validate-authcode` — `{grant_type, appIdHash, code}` |
| Refresh | `POST /api/v3/validate-refresh-token` — **documented, deliberately unused** |
| Logout | `POST /api/v3/logout` |
| Profile | `GET /api/v3/profile` |
| Place order | `POST /api/v3/orders/sync` |
| Modify | `PATCH /api/v3/orders/sync` — **`type` is mandatory even when unchanged** |
| Cancel | `DELETE /api/v3/orders/{orderId}/sync` |
| Basket (≤10) | `POST /api/v3/multi-order/sync` — **not atomic** |
| Order book | `GET /api/v3/orders` (`?id=` for one order) |
| Trade book | `GET /api/v3/tradebook` |
| Margin | `POST /api/v3/multiorder/margin` — `{data:[…]}` |
| Positions | `GET /api/v3/positions`; `POST` converts, `DELETE` exits, `PATCH` attaches a stop |
| Holdings | `GET /api/v3/holdings` |
| Funds | `GET /api/v3/funds` |
| Quotes (≤50) | `GET /data/quotes?symbols=` |
| Depth | `GET /data/depth?symbol=&ohlcv_flag=1` |
| History | `GET /data/history?symbol=&resolution=&date_format=&range_from=&range_to=` |
| Option chain | `GET /data/options-chain-v3?symbol=&strikecount=&timestamp=` |
| Symbol master | `GET https://public.fyers.in/sym_details/{NSE_CM,BSE_CM,NSE_FO,BSE_FO}.csv` |

**The `Authorization` header is not a bearer token.** It is the literal string
`{app_id}:{access_token}` with no scheme. `AuthenticationHeaderValue` always emits
`scheme parameter`, so using it produces `Authorization: Bearer XC…-100:eyJ…` and FYERS answers
401 without saying why. `FyersApi` adds the header without validation for exactly this reason.

There is **no cancel-all route**. `CancelAllAsync` reads the book and cancels each working order
in turn — safe, because a cancel is idempotent in a way a placement never is — and reports how
many succeeded if any fail.

## Auth flow

Ordinary OAuth 2 authorization code, with the app authenticated by a hash rather than by sending
the secret:

```
BeginAsync(app_id, app_secret, redirect_uri)
  -> RedirectRequired(https://api-t1.fyers.in/api/v3/generate-authcode?…&state=<random>)
       user signs in at FYERS; the redirect_uri receives ?auth_code=…&state=…
     |
ContinueAsync(auth_code)
  POST /api/v3/validate-authcode { grant_type, appIdHash = SHA-256("app_id:app_secret"), code }
    -> access_token
  GET  /api/v3/profile -> fy_id
    -> Completed(BrokerSession)
```

Two details that are easy to get wrong:

- **The colon in the hash is part of the input.** `SHA-256(app_id + ":" + app_secret)`. Hashing
  the two concatenated without it produces a perfectly valid-looking 64-character digest that
  FYERS rejects as "invalid App ID".
- **The `state` value must be checked on the way back.** It is the only thing tying the browser
  that started the login to the callback that finishes it. `FyersAuth` generates 256 bits from
  `RandomNumberGenerator` and returns it on the step; the host compares it.

The account id comes from `GET /api/v3/profile` (`fy_id`), which is in the Basic permission
template so every app that can authenticate at all can call it. The token's own claims are only a
fallback.

## The thing to get right: token expiry

FYERS publishes **no access-token lifetime**. That leaves three constraints and no authority, so
`FyersAuth.ComputeExpiry` takes the earliest of all three:

1. **The token's own `exp` claim.** The access token is a JWT and it says when it dies. This is
   the real answer whenever it is readable, and no documentation would beat it.
2. **Venue midnight (IST).** Indian broker tokens are day-bound. A token minted at 15:00 IST does
   not survive the night whatever its claim says.
3. **The configured `TokenLifetime`**, as the floor when the token is not a readable JWT — a
   shape change at the vendor must degrade to a conservative guess, not to "never expires".

Erring early costs one extra login. Erring late costs orders, because the first thing a trader
learns about a dead token is a rejection at the next open.

## Why `refreshSupported: false`

FYERS does publish a refresh route, and this connector deliberately does not use it:

1. FYERS has announced that **refresh tokens are discontinued from 1 April**, alongside the
   regulatory changes to API usage. Building the session monitor around a route the vendor is
   withdrawing buys a silent failure at the moment it is relied on.
2. The refresh call requires the user's **trading PIN** in the body. Supporting it means
   persisting a second standing secret — one that authorises trades on its own — to skip a login
   the user has to do once a day anyway.
3. The access token is day-bound regardless, so a refresh could never carry a session past the
   next venue midnight. The ceiling is the same either way.

`RefreshAsync` therefore returns `NotSupported`, which is what tells the session monitor to stop
asking and prompt for a fresh login instead.

## Rate limits (encoded in the manifest)

| Scope | Per second | Per minute | Per day |
|---|---|---|---|
| global | 10 | 200 | 100,000 |
| orders | 10 | — | — |

**Exceeding the per-minute limit three times in one day blocks the account for the rest of the
day.** That is a harsher penalty than most brokers apply and it is why the manifest declares the
limits rather than letting FYERS enforce them. FYERS Prime raises the per-minute limit to 600.

Order rate limiting returns HTTP 429 with `Retry-After` and `X-Retry-After-Ms` **headers**, not a
body field; the SDK's HTTP client already reads `Retry-After` and carries it into the error for
the resilience decorator.

## Symbology

FYERS symbols carry their own exchange prefix, which is a genuine convenience: there is never a
second "exchange" argument to get wrong.

| Kind | Format | Example |
|---|---|---|
| Equity / ETF | `{EX}:{SYM}-{SERIES}` | `NSE:SBIN-EQ`, `BSE:360ONE-A` |
| Index | `{EX}:{SYM}-INDEX` | `NSE:NIFTYBANK-INDEX` |
| Future | `{EX}:{SYM}{YY}{MMM}FUT` | `NSE:BANKNIFTY26SEPFUT` |
| Option (monthly) | `{EX}:{SYM}{YY}{MMM}{STRIKE}{CE\|PE}` | `NSE:NIFTY26SEP25000CE` |
| Option (weekly) | `{EX}:{SYM}{YY}{M}{DD}{STRIKE}{CE\|PE}` | `NSE:NIFTY2632423050PE` |

Weekly expiries encode the month as one character: `1`–`9`, then `O`, `N`, `D`.

Two asymmetries, both the same point — the symbol does not always carry everything the canonical
key needs, and the connector refuses to guess the rest:

- **Monthly derivatives** name their expiry month but not the day, and NSE has moved its expiry
  weekday twice. Decoding one needs the symbol master. Weekly symbols carry a full date and
  decode structurally.
- **BSE cash** carries a settlement series that cannot be derived — `360ONE` is `-A` and
  `3IINFOLTD` is `-T`. Encoding one needs the master. NSE cash does not: `-EQ` is the
  rolling-settlement series and the only one the platform routes.

Both refuse with `InstrumentNotFound` and a message naming the master, rather than inventing a
symbol — a guessed symbol is an order on the wrong instrument.

### Symbol master

Seven public CSV files; this connector reads the four for its declared venues. They are
**headerless and positional**, 21 columns — see `FyersReference.Column` for the indices. Verified
against live files on 2026-09-06:

| File | Rows kept | Rows skipped | What the skipped rows are |
|---|---|---|---|
| `NSE_CM.csv` | 3,647 | 6,354 | State development loans (`-SG`), debentures, T-bills, mutual funds |
| `BSE_CM.csv` | 978 | 11,994 | Privately placed debt (`-F`), instrument type 50 |
| `NSE_FO.csv` | 77,361 | 0 | — |

A high skip count on the cash files is **expected and correct**: FYERS lists debt and fund
instruments in the same segment files, and this connector declares only equity, ETF, index,
future and option. Filing a debenture under Equity would put an untradable name in the search
box. `ConnectorHealth` reports the count and says so.

## Mapping

Everything lives in `FyersMaps.cs`. The two that will catch you:

**`MARGIN` and `Margin` are not the same thing.** FYERS' `MARGIN` is the derivatives
carry-forward product — the NRML of every other Indian broker — and its margin-funding product is
`MTF`. The canonical vocabulary uses `Margin` for margin funding and `CarryForward` for overnight
derivatives, so the two names *cross*:

| Canonical | FYERS |
|---|---|
| `Delivery` | `CNC` |
| `Intraday` | `INTRADAY` |
| `Margin` | `MTF` |
| `CarryForward` | `MARGIN` |

Mapping by name rather than by meaning takes a trader's overnight futures position and places it
as funded equity. The symmetry of the mistake is what makes it survive a casual review.

**There is no partially-filled status.** FYERS statuses are `1` cancelled, `2` traded, `4`
transit, `5` rejected, `6` pending, `7` expired (`3` is documented as "for future use"). A partly
executed order stays at `6` with a non-zero `filledQty`, so `ToCanonicalOrderStatus` takes the
filled quantity and derives `PartiallyFilled`. Without that, a half-filled order reads as merely
resting and anything sizing off it works from a position that is already half on.

`4` (transit) means the order is between FYERS and the exchange and has **not** been
acknowledged, so it maps to `Submitted`, never `Open`.

## Quirks and gotchas

- **Code 201 on a placement is not an acceptance.** FYERS documents it as "the order request has
  been made but no acknowledgement has been received — check the orderbook before placing
  again". `PlaceAsync` returns the ack with `OrderStatus.Unknown` and the broker's message.
  Unknown is neither working nor terminal, so the risk gate will not act on it and the caller
  reconciles against the order book — which is exactly what FYERS asks for. Reporting it as
  `Submitted` would claim an order that may not exist; reporting it as a failure would invite the
  retry that creates the duplicate.
- **A modify must restate the order type.** `type` is mandatory on the PATCH. When the caller
  does not name one, `ModifyAsync` reads the order back and restates what it already is —
  guessing would convert a resting limit order into a market order that fills immediately.
- **FYERS prefixes every order tag it returns.** A tag you sent comes back as `1:{tag}`, and one
  FYERS generated as `2:{tag}`. Decoding without stripping the prefix finds nothing in the index
  and quietly defeats the reconciliation the tag exists for. See `FyersOrderTags`.
- **The order book spans every segment the ACCOUNT trades**, not every segment this connector
  declares. A user who also trades on MCX has commodity rows in the same response. Those are
  skipped on the symbol prefix, *before* translation, so the skip can never swallow an in-scope
  instrument that merely failed to resolve.
- **`fyToken` and `id` are documented as strings and sent as numbers** on several routes. A
  lenient string converter is registered globally in `FyersJson.Options`; without it one numeric
  id fails the whole document and holdings comes back as "the response could not be understood".
- **The funds route returns a ledger, not named fields** — ten rows with a numeric id, a display
  title and separate equity and commodity amounts. `FyersPortfolio` addresses rows by **id**;
  matching on the title would break silently, and as a zero balance, the day FYERS improves its
  wording.
- **Error `-352` means two different things**: an invalid app id, and "no position available to
  exit". Only the request path separates them, and one is a broken integration while the other is
  an entirely ordinary flat account.
- **The market-depth response spells the ask side `ask`, singular**, while bids are `bids`. Not a
  typo to be tidied up.
- **Prices come back as `0` for "not applicable"**, not as absent. Only positive values become a
  `Money`.
- **Symbols containing `&` must be URL-encoded** (`NSE:M&M-EQ` → `NSE%3AM%26M-EQ`). An unescaped
  one does not fail; it silently asks for a different symbol.

## Not offered, and why

| Capability | Manifest | Why |
|---|---|---|
| Live market data stream | `streaming: false` | The data socket speaks a proprietary binary protocol published only inside the FYERS language SDKs. There is no wire format to implement against. See below. |
| Charges estimate | `chargesEstimate: false` | FYERS reports charges only after execution, aggregated by day and segment. There is no pre-trade calculator, and a locally invented schedule presented as the broker's own would be worse than none. |
| Cover / bracket orders | `cover: false`, `bracket: false` | Deprecated by FYERS on 2 August 2026. |
| GTT orders | `gtt: false` | A separate endpoint family, not yet wired. |
| MCX and currency derivatives | not in `venues` | The platform has no commodity trading calendar or charge schedule yet, and claiming the venue without them would produce an order ticket that renders and then fails. |

### The stream, and the contract gap behind it

The **order** socket at `wss://socket.fyers.in/trade/v3` *is* fully documented JSON — connect
with `Authorization: {appId}:{accessToken}`, send
`{"T":"SUB_ORD","SLIST":["orders","trades","positions"],"SUB_T":1}`, ping every ten seconds — and
could be implemented today. It is not wired because `IConnectorStream` is reached through a
single manifest flag, `marketData.streaming`, which the conformance suite reads as "this
connector has a live feed" (and asserts `Stream` is null when false) while the fan-out layer
reads it as "this connector can be subscribed to for prices".

Declaring it `true` to get order updates would promise a price feed that does not exist and fail
`Streaming_reconnects_without_leaking_upstream_subscriptions` at the `SubscribeAsync` step.
Declaring it `false` and returning a stream anyway fails the same test's null check. So the
manifest says false, `FyersConnector.Stream` is null, and order state is reconciled by polling.

Having both would mean splitting that one flag into two — a market-data feed and an
order-update feed — in `MarketDataSpec` and `ConnectorManifest`. That is a contract change and
needs an ADR under `docs/adr/`; it is not something to sneak in behind a broker.

## Smoke test — run this before trusting anything

No automated suite replaces it. Record the date and result here.

1. Create an app in the FYERS API dashboard with the Order Placement and Market Data permission
   templates. Set its redirect URI to this deployment's.
2. Link the account through the wizard. Confirm the browser lands back with an `auth_code` and
   that the returned `state` matches what was sent.
3. `GET /api/connectors/fyers/health` — session valid, expiry within today.
4. Ingest the symbol master. Confirm the instrument count and the skipped-row count in health,
   and that both figures are close to the table above.
5. Quote `NSE:SBIN-EQ`. Confirm the last price against the FYERS web terminal.
6. Place a **1-share limit buy well away from the market**. Confirm the order id, then find the
   same order in the FYERS web terminal.
7. Modify its price. Modify its quantity **without** naming an order type — this is the path that
   reads the order back — and confirm the type did not change in the terminal.
8. Cancel it. Confirm it is gone in the terminal.
9. Place a second such order and use cancel-all. Confirm the count and the terminal.
10. Read positions, holdings and funds. Reconcile the available balance against the terminal.
11. Fetch daily candles for `NSE:SBIN-EQ` over a month, and an option chain for
    `NSE:NIFTY50-INDEX`. Confirm the expiry you asked for is the expiry you got.

| Date | Who | Result |
|---|---|---|
| — | — | Not yet run against a live account |

## Open questions

- **Does the access token's `exp` claim exist and mean what it looks like?** The expiry logic
  reads it and falls back safely if not, but confirming it turns a defensive guess into a fact.
- **Is the refresh route already withdrawn?** The documentation says "discontinued from 1st
  April" without naming a year, next to a banner about April 2026 regulatory changes.
- **What is the real `orderTag` length limit?** Not documented. This connector sends 20 hex
  characters, which is comfortably inside anything plausible, but a longer tag would let the full
  GUID travel and remove the need for the process-local index.
- **How many BSE equities does type 0 really cover?** 920 rows looks low against BSE's listed
  count; the rest are type 50 (privately placed debt), which is plausible but unverified.
