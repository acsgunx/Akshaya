# Connector: Zerodha Kite

- **Id:** `zerodha`
- **Market:** India — NSE and BSE, cash and equity derivatives
- **API:** Kite Connect v3, REST + WebSocket
- **Docs:** https://kite.trade/docs/connect/v3/
- **Hosting:** in-process
- **Status:** implemented from the published documentation, **never run against a live account**.

> ⚠️ Every endpoint, field name, enum value and limit below was read from the Kite Connect v3
> documentation on **2026-09-06**. No authenticated request has been sent, so the response shapes
> in `ZerodhaDtos.cs` are the most likely place reality differs from this code.
>
> Two parts HAVE been verified against real data and are not guesses:
>
> - **The instrument master.** All four exchange dumps were downloaded and parsed — 60,089
>   instruments, zero unparseable rows — and 5,000 of them round-tripped through the symbol
>   translator in both directions without loss.
> - **The binary tick protocol.** LTP, quote, full and index packets were built byte-for-byte from
>   the documented layout and fed through the real decoder; every field, both sides of the book and
>   the exchange timestamp came back correct.
>
> Run the smoke test at the end of this file before trusting anything else here.

## Endpoints

| Purpose | Method and path |
|---|---|
| Base | `https://api.kite.trade` |
| Login (browser) | `https://kite.zerodha.com/connect/login?v=3&api_key=…&redirect_params=…` |
| Token exchange | `POST /session/token` — `api_key`, `request_token`, `checksum` |
| Logout | `DELETE /session/token?api_key=…&access_token=…` — **query params, not a body** |
| Profile | `GET /user/profile` |
| Funds | `GET /user/margins` |
| Place order | `POST /orders/{variety}` |
| Modify | `PUT /orders/{variety}/{order_id}` |
| Cancel | `DELETE /orders/{variety}/{order_id}` |
| Order book | `GET /orders` |
| One order's history | `GET /orders/{order_id}` — **returns a state history, not an order** |
| Trade book | `GET /trades` |
| Margin **and charges** | `POST /margins/orders` — **JSON, unlike every other write** |
| Positions | `GET /portfolio/positions`; `PUT` converts one between products |
| Holdings | `GET /portfolio/holdings` |
| Quotes (≤500) | `GET /quote?i=NSE:INFY&i=…` |
| LTP | `GET /quote/ltp?i=…` |
| Candles | `GET /instruments/historical/{instrument_token}/{interval}?from=&to=` |
| Instrument master | `GET /instruments/{NSE,BSE,NFO,BFO}` — gzipped CSV |
| WebSocket | `wss://ws.kite.trade?api_key=…&access_token=…` |

Headers on every call: `X-Kite-Version: 3` and `Authorization: token {api_key}:{access_token}`.
Unlike some brokers, `token` genuinely *is* the scheme here, so a normal
`AuthenticationHeaderValue` renders it correctly.

**POST and PUT bodies are form-encoded**; GET and DELETE take query parameters. The margin
calculator is the single documented exception and takes JSON.

There is **no cancel-all route** and **no batch placement route**. Both are loops here, and the
manifest says so with `basket.atomic: false`.

## Auth flow

```
BeginAsync(api_key, api_secret)
  -> RedirectRequired(https://kite.zerodha.com/connect/login?v=3&api_key=…&redirect_params=state%3D…)
       user signs in at Kite; the app's REGISTERED redirect URI receives ?request_token=…
     |
ContinueAsync(request_token)
  POST /session/token { api_key, request_token, checksum = SHA-256(api_key + request_token + api_secret) }
    -> access_token, user_id, exchanges, products, order_types
    -> Completed(BrokerSession)
```

Three details differ from a textbook OAuth2 flow and all three bite:

- **The redirect URI is not sent.** Kite uses the one registered against the `api_key` on the
  developer console and ignores anything in the URL. `AuthContext.RedirectUri` is checked for
  presence but never transmitted; it exists so the host can tell the user which URI to register.
- **There is no `state` parameter.** Kite has `redirect_params` instead — an opaque, URL-encoded
  query string echoed back to the redirect URI. The anti-CSRF state rides in there, and
  `ZerodhaAuth` generates 256 bits of it from `RandomNumberGenerator`.
- **The checksum has no separator.** It is `SHA-256(api_key + request_token + api_secret)`,
  concatenated directly. Worth stating out loud because the neighbouring connectors in this
  repository differ: mStock is a Kite-lineage API that does not hash here at all, and FYERS uses a
  colon-separated hash. Getting it wrong produces a perfectly valid-looking 64-character digest
  that Kite rejects with a bare `TokenException`, at the step right after a login that appeared to
  work.

## The thing to get right: token expiry at 6 AM, not midnight

Kite is unusually precise, and the precision is worth honouring exactly. The access token
*"will expire at 6 AM on the next day (regulatory requirement)"*.

Both plausible approximations are wrong in a way that costs something:

| Assumption | What it costs |
|---|---|
| Venue **midnight** (what mStock and FYERS do) | Prompts for a re-login six hours before it is needed, every single day |
| A rolling **24 hours** from issue | A token that died at 06:00 looks alive until 15:00, so the first sign of trouble is a rejected order at the open |

`ZerodhaAuth.ComputeExpiry` computes the next 06:00 in `Asia/Kolkata` after issue. A token issued
at 05:00 IST therefore expires at 06:00 the **same** morning, an hour later — that is not a bug,
it is what "expires at 6 AM" means.

### A contract gap worth knowing about

`AuthSpec` has `expiresAtVenueMidnight` and a `venueMidnightTimeZone`, but no way to express "a
different wall-clock hour". So this connector declares **`expiresAtVenueMidnight: false`** and
relies on the `BrokerSession.ExpiresAt` it computes itself.

Declaring it `true` would be worse than leaving it off: `SessionMonitor` takes the *minimum* of
every constraint, so it would clamp every session to 00:00 IST — six hours earlier than the truth
and in direct contradiction of the expiry the auth facet just computed. Generalising that field to
an hour-of-day is a contract change and needs an ADR; it is not something to sneak in behind a
broker.

The manifest also declares **`sessionLifetime: null`**, because there genuinely is no nominal
lifetime — the boundary is a wall-clock time, not a duration from issue.

> A note for whoever edits that manifest next: `"24:00:00"` is **not** a parseable `TimeSpan`
> (hours must be 0–23; a whole day is `"1.00:00:00"`). The JSON schema accepts it happily because
> it is just a string, and the failure only appears at startup, as a type-initializer exception
> from `ZerodhaPlugin`. `scripts/validate-manifests.py` passing does not mean the host can load it.

## Why `refreshSupported: false`

Kite's session response has a `refresh_token` field and it is empty for almost everyone — the
documentation says it is "only available to certain approved platforms". A connector declaring
refresh support would leave the session monitor retrying a refresh that returns nothing, forever,
and never falling through to prompting the user, so the session would quietly stop working with no
dialog.

It would buy nothing anyway: the 06:00 expiry is regulatory, so a refresh could not carry a session
past a boundary the trader must cross with a fresh login regardless.

## Rate limits (encoded in the manifest)

| Scope | Per second | Per minute | Per day |
|---|---|---|---|
| quotes | **1** | — | — |
| data (historical) | **3** | — | — |
| orders | 10 | 400 | 5,000 |
| global | 10 | — | — |

The quote limit is the tight one and it shapes `ZerodhaMarketData`: one request per second, against
ten for everything else. That is why quotes are batched up to Kite's 500-instrument maximum and why
the option chain is assembled from a single bulk quote rather than per-contract calls.

Kite also caps **modifications at 25 per order**, after which the order must be cancelled and
replaced. Nothing here enforces that yet; a strategy that amends aggressively will meet it.

## Symbology

Kite keeps the exchange and the trading symbol in separate fields on its order routes but joins
them with a colon for quotes. This connector uses the **joined form as its native symbol**, because
it is the only form that decodes unambiguously on its own — INFY trades on both NSE and BSE — and
it is exactly what the quote routes already want. `ZerodhaInstrument.TrySplit` puts it back into
two fields for placement.

| Kind | Format | Example |
|---|---|---|
| Equity / ETF | `{EX}:{SYMBOL}` | `NSE:INFY`, `BSE:INFY`, `NSE:IDEA-BE` |
| Index | `{EX}:{SYMBOL}` | `NSE:NIFTY 50` (the space is real) |
| Future | `{SEG}:{SYM}{YY}{MMM}FUT` | `NFO:NIFTY26SEPFUT` |
| Option (monthly) | `{SEG}:{SYM}{YY}{MMM}{STRIKE}{CE\|PE}` | `NFO:NIFTY26SEP23900CE` |
| Option (weekly) | `{SEG}:{SYM}{YY}{M}{DD}{STRIKE}{CE\|PE}` | `NFO:NIFTY2690823900CE` |

Derivatives go to the **NFO** and **BFO** segments, never to `NSE`/`BSE`. Weekly expiries encode
the month as one character: `1`–`9`, then `O`, `N`, `D`.

**Cash symbology is the pleasant surprise.** Kite's plain rolling-settlement series carries no
suffix at all — `INFY`, not `INFY-EQ` — and the non-plain series carry theirs as part of the symbol
(`IDEA-BE`, `GOLDSTAR-SM`). So the canonical symbol is the trading symbol verbatim, nothing is
stripped, and cash translation is **total and lossless in both directions with no master loaded**.
Contrast FYERS, where a BSE settlement series cannot be derived and encoding needs the master.

One cross-broker nuance: for a non-plain series, mStock and FYERS strip the suffix (`IDEA`) while
this connector keeps it (`IDEA-BE`), so the canonical keys differ for those instruments. Plain-EQ
names — which is almost everything — agree across all three. The blended portfolio matches on ISIN
regardless.

The one asymmetry is **monthly derivatives**: the symbol names the expiry month but not the day,
and NSE has moved its expiry weekday more than once, so decoding one needs the master. Weekly
symbols carry a full date and decode structurally. A monthly symbol with no master fails with
`InstrumentNotFound` naming the master rather than guessing a date.

### Instrument master

Four CSV dumps, gzipped, publicly readable. Verified 2026-09-06:

| Segment | Rows kept | Rows skipped |
|---|---|---|
| `NSE` | 10,262 | 0 |
| `BSE` | 12,990 | 0 |
| `NFO` | 32,655 | 0 |
| `BFO` | 4,182 | 0 |

Two things this ingest does that the FYERS one cannot:

- **Columns are located by NAME from the header row.** Kite ships one, so a vendor inserting a
  column costs nothing instead of silently turning every lot size into a tick size.
- **The `name` column is quote-aware parsed.** Kite quotes it (`"NIFTY 50"`), and a company name
  containing a comma is a matter of when, not if.

The response is gzipped, and whether it *arrives* that way depends on the `HttpClient`'s
decompression setting — which a caller-supplied client controls. The stream is sniffed for the gzip
magic bytes rather than assumed, because feeding a CSV parser a binary blob produces a hundred
thousand skipped rows and no error.

**Zero skipped rows** is expected here and is not the same as FYERS, where the cash files are full
of debt instruments. Kite segregates those into segments this connector does not read.

## Mapping

Everything lives in `ZerodhaMaps.cs`. Two that will catch you:

**`SL` is a stop-LIMIT and `SL-M` is a stop-MARKET.** The canonical names read the other way round,
so `OrderType.Stop` maps to `SL-M` and `OrderType.StopLimit` maps to `SL`. Swapping them sends a
protective stop as a limit order that never fills, or an intended limit as a market order that
fills instantly.

| Canonical | Kite |
|---|---|
| `Delivery` | `CNC` |
| `Intraday` | `MIS` |
| `Margin` | `MTF` |
| `CarryForward` | `NRML` |

**Kite has no partially-filled status.** A partly executed order stays `OPEN` with a non-zero
`filled_quantity`, so `ZerodhaOrderMapper` derives `PartiallyFilled`. Without it, a half-filled
order reads as merely resting and anything sizing off it works from a position that is already
half on.

Interim statuses matter more than they look: `PUT ORDER REQ RECEIVED`, `VALIDATION PENDING` and
`OPEN PENDING` mean the order is between the OMS and the exchange and has **not** been
acknowledged, so they map to `Submitted`. `TRIGGER PENDING` is the opposite — a stop genuinely
resting at the exchange — so it is `Open`.

Kite's own documentation says of the status field: *"There may be other values as well."* The
order-book path therefore degrades an unmapped status to `Unknown` and keeps the raw text, rather
than blanking the blotter.

## Streaming

This is the capability that most distinguishes Kite from the other Indian brokers here: **its wire
protocol is fully published**, so the feed is implemented against the documentation rather than a
vendor SDK. Order postbacks arrive on the same socket, seconds before the order book would show
them — which is why `ZerodhaOrderTagIndex` is shared between the orders facet and the stream, so a
fill can carry the `ClientOrderId` of the order that caused it.

- Subscribe: `{"a":"subscribe","v":[tokens]}`, then `{"a":"mode","v":["full",[tokens]]}`.
- Up to **3,000 instruments per socket**, and **3 sockets per api_key**.
- Prices are integer **paise** — divide by 100. (Currency derivatives divide by 10,000,000; out of
  scope here, and the connector never subscribes to one.)
- A **1-byte binary frame is a heartbeat** and is ignored.

Packet shapes are told apart by **length**: 8 = LTP, 44 = quote, 184 = full, and 28/32 = an
**index** packet, whose OHLC block is in a different order (high, low, open, close). Reading an
index packet as a tradable instrument's yields a plausible and wrong price.

`ZerodhaStream.Layout` transcribes the documented byte offsets as named constants, so each line can
be checked against the table in Kite's docs without doing arithmetic. That is deliberate: an
index-times-four scheme reads just as plausibly while being wrong, and a decoder that runs off the
end of a packet throws where the pump does not catch it — which kills the feed silently.

`DispatchBinary` is `internal` rather than private so a test can feed it recorded frames and assert
on the events without standing up a socket.

## Not offered, and why

| Capability | Manifest | Why |
|---|---|---|
| GTT orders | `gtt: false` | A separate endpoint family, not yet wired |
| Cover orders | `cover: false` | Two-legged: the first leg spawns a second whose lifetime the order store does not model yet (`parent_order_id`) |
| Bracket orders | `bracket: false` | Withdrawn by Zerodha |
| Iceberg orders | not in `varieties` | Needs a leg count and per-leg quantity; the shared order contract has no field for either |
| `TTL` validity | not in `timeInForce` | A lifetime in minutes has no canonical equivalent, and mapping it to `Day` would leave the platform believing a lapsed order was still working |
| MCX, currency | not in `venues` | The platform has no commodity trading calendar or charge schedule yet |
| ETF as a distinct class | `Etf` **is** declared | Kite files ETFs as ordinary `EQ` rows with no flag, so one decodes as `Equity`. The class is declared because Kite genuinely trades ETFs and an Etf order routes correctly; what is missing is the reference data to label one, not the ability to trade it |

### The option chain is assembled, not fetched

Kite publishes no option-chain endpoint. This one is built locally, and that is a faithful
implementation rather than an invented capability: `OptionChainRow` asks for a strike, a call
quote, a put quote and two open-interest figures — nothing that needs a dedicated route. The
instrument master already lists every contract on an underlying for an expiry, and one quote call
covers 500 instruments with open interest included. So the chain is exactly "the contracts, plus
their quotes", and both halves are the broker's own data.

What is genuinely absent is greeks — and the contract has no field for them, so nothing is lost.
What it *does* depend on is the instrument master being ingested, which is why the failure says so.

## Quirks and gotchas

- **`GET /orders/{order_id}` returns a state HISTORY, not an order.** The current state is the last
  element; taking the first reports every order as it was when Kite first received it —
  permanently "pending", never filled.
- **Modify and cancel need the VARIETY**, and the shared contract carries only the broker order id.
  Both read the order back first to learn what it is. That is one extra round trip, spent
  deliberately: assuming `regular` silently fails to cancel every after-market order placed the
  previous evening — exactly the orders someone most wants gone before the open.
- **Placement is registration with the OMS, not receipt at the exchange.** Kite says so explicitly.
  `PlaceAsync` returns `Submitted`, never `Open`.
- **A cancel is acknowledged, not completed.** The order passes through `CANCEL PENDING` first, so
  `CancelAsync` returns `Submitted` — claiming it is cancelled while it can still fill is how a
  trader ends up with a position they believe they closed.
- **The positions route returns two sets**, `net` and `day`. Reading `day` as the position erases
  every overnight carry-forward and tells the risk engine the account is flat when it is not.
- **The funds route is segmented.** `equity` and `commodity` are separate ledgers with separate
  cash; this connector reports the equity one.
- **`OrderException` arrives as an HTTP 500** and is emphatically not a retryable server error. It
  defaults to the non-retryable `OrderRejected`, because a rejection misclassified as transient is
  retried by the host's resilience decorator, and a retried placement is a duplicate order.
- **HTTP 428 means the holdings need authorising at the depository** before a sell can go through,
  not that the request was malformed.
- **The tag is capped at 20 alphanumeric characters**, which is exactly the size of the
  `ClientOrderId` fragment this connector sends. There is no room for anything else in it.
- **The socket authenticates with query parameters**, not the `Authorization` header — the one
  place Kite's WebSocket differs from its REST surface.

## Smoke test — run this before trusting anything

No automated suite replaces it. Record the date and result here.

1. Create an app on the Kite developer console (it is a paid subscription). Set its redirect URL to
   this deployment's.
2. Link the account through the wizard. Confirm the browser lands back with a `request_token`, and
   that the `state` echoed in `redirect_params` matches what was sent.
3. `GET /api/connectors/zerodha/health` — session valid, and the expiry reads **06:00 IST tomorrow**
   (or today, if you linked before 06:00).
4. Ingest the instrument master. Confirm the counts against the table above.
5. Quote `NSE:INFY`. Confirm the last price against the Kite web terminal.
6. Connect the stream, subscribe to `NSE:INFY` in `full` mode, and confirm ticks arrive with a
   sensible price, a five-level book, and a timestamp within a second or two of now.
7. Place a **1-share limit buy well away from the market**. Confirm the order id, then find the same
   order in the Kite terminal — and confirm the order update arrives on the socket.
8. Modify its price. Modify its quantity **without** naming an order type — this is the path that
   reads the order back for its variety — and confirm the type did not change.
9. Cancel it. Confirm it is gone in the terminal.
10. Place an after-market order in the evening and cancel it. This is the path that proves the
    variety lookup works; a `regular` assumption fails here and only here.
11. Estimate the margin and charges for a small order. Reconcile the charge lines against Kite's own
    brokerage calculator.
12. Read positions, holdings and funds. Reconcile the available balance against the terminal.
13. Fetch daily candles for `NSE:INFY` over a month, and an option chain for `NSE:NIFTY 50`.

| Date | Who | Result |
|---|---|---|
| — | — | Not yet run against a live account |

## Open questions

- **Does the order postback payload match the order book row exactly?** This connector assumes so
  and maps both through `ZerodhaOrderMapper`. The documented postback shape looks identical, but it
  has not been seen live.
- **How does the socket behave at the 3,000-instrument cap?** Kite documents the limit but not what
  happens on the 3,001st subscribe. This connector refuses locally rather than finding out.
- **Is `average_price` on a trade really the fill price?** Read that way here, since a trade is
  atomic and has nothing to average, but worth confirming against a partially filled order.
- **Does `continuous=1` behave for BFO futures** the way it does for NFO? The documentation mentions
  NFO and MCX only.
