# Connector: m.Stock (Mirae Asset Capital Markets)

- **Id:** `mstock`
- **Market:** India — NSE, BSE, cash and F&O
- **API:** Type A REST + WebSocket
- **Docs:** https://tradingapi.mstock.com/docs/v1/typeA/
- **Hosting:** in-process
- **Status:** implemented. The **login leg is verified against a live account**; every leg after
  it is still documentation-only — see below.

> ⚠️ Most of this file comes from the published documentation. The **order, margin, position
> and error pages were re-read from <https://tradingapi.mstock.com/docs/v1/typeA/> on
> 2026-09-04** and the corrections are folded in below; the docs host refuses plain HTTP
> clients (`AccessDenied`) but serves a real browser, so re-checking is possible — do it
> through one. The remaining response shapes in `MStockDtos.cs` are still the most likely place
> reality differs from this code.
>
> **This has already bitten five times.** A successful login was rejected as unparseable; the
> session `checksum` was computed as a SHA-256 hash when mStock wants the literal string `L`;
> the fund summary expected an object where mStock sends an array; logout treated its own
> success as malformed; and `"scrip"` matching inside `"subscription"` reported an expired API
> key as "instrument not found". All five are written up, with the documented payloads, in
> [`mstock-login-response.md`](mstock-login-response.md).
>
> **Read it before touching any DTO here.** Every one of those faults came from this connector
> being shaped like Zerodha Kite, which mStock resembles closely enough to be dangerous. The
> order, position and market-data DTOs have not yet been re-checked the same way.

## Endpoints

| Purpose | Method and path |
|---|---|
| Base | `https://api.mstock.trade` |
| Streaming | `wss://ws.mstock.trade` |
| Login | `POST /openapi/typea/connect/login` |
| Session token (OTP) | `POST /openapi/typea/session/token` |
| Verify TOTP | `POST /openapi/typea/session/verifytotp` |
| Logout | `GET /openapi/typea/logout` |
| Place order | `POST /openapi/typea/orders/{variety}` — `reg` or `amo` — **form-encoded** |
| Modify | `PUT /openapi/typea/orders/regular/{orderId}` — **form-encoded, and a REPLACE** |
| Cancel | `DELETE /openapi/typea/orders/regular/{orderId}` |
| Cancel all | `POST /openapi/typea/orders/cancelall` — **reports no count** |
| Order book | `GET /openapi/typea/orders` |
| Order details | `GET /openapi/typea/order/details?order_no=&segment=` (`E` / `D`) |
| Trade book (today) | `GET /openapi/typea/tradebook` |
| Trade history | `GET /openapi/typea/trades?fromdate=&todate=` |
| Margin + charges | `POST /openapi/typea/margins/orders` — **JSON, not form** |
| Convert position | `POST /openapi/typea/portfolio/convertposition` — form-encoded |
| Script master | `GET /openapi/typea/instruments/scriptmaster` (CSV) |
| LTP | `GET /openapi/typea/instruments/quote/ltp` |
| OHLC | `GET /openapi/typea/instruments/quote/ohlc` |
| Candles (all intervals) | `GET /openapi/typea/instruments/historical/{exchange}/{token}/{interval}?from=&to=` |

Headers on every call: `X-Mirae-Version: 1`, `Authorization: token {api_key}:{access_token}`,
`X-PrivateKey: {api_key}`. JSON generally; login and session endpoints are
`application/x-www-form-urlencoded`.

## Auth flow

```
POST /connect/login (username, password)
        │
        ├── TOTP enabled ──► POST /session/verifytotp (api_key, totp) ──► Completed
        │
        └── otherwise ─────► OTP sent to registered mobile
                             POST /session/token (api_key, request_token=OTP, checksum)
                             └─► Completed
```

Returns `access_token`, `refresh_token`, `public_token`, `enctoken` plus the profile's permitted
exchanges, products and order types.

## The other thing to get right: SMS or authenticator, never both

mStock documents it in one line on the User page: **"If TOTP is enabled, OTP will not be
triggered for login trading API requests."**

So an account with an authenticator app enabled receives **no SMS at all**, and the login
response says nothing about which mode the account is in (`flag` is undocumented and
deliberately unmapped — see [`mstock-login-response.md`](mstock-login-response.md)). The two
codes go to different endpoints:

| The user holds | Endpoint | Field |
|---|---|---|
| An SMS code | `POST /session/token` | `request_token` |
| An authenticator code | `POST /session/verifytotp` | `totp` |

A code sent to the wrong one is always rejected, however correct it is — and the rejection
looks identical to a mistyped code. The connector therefore does **not** guess: the wizard
offers "No SMS? Use my authenticator app code instead", which sets
`state["challenge"] = "totp"` and routes to `verifytotp`.

Storing a `totp_secret` still works and skips the prompt entirely, but it is now an
optimisation rather than the only way in — requiring someone to hand over their TOTP seed just
to log in defeats much of the point of having one.

## The thing to get right: token expiry

The token dies after **~12 hours OR at midnight IST, whichever comes first**. API keys last about
13 months.

`MStockAuth.ComputeExpiry` takes the minimum, and the manifest sets
`expiresAtVenueMidnight: true` with `venueMidnightTimeZone: "Asia/Kolkata"`. A token minted at
15:00 IST expires at midnight — not at 03:00. Getting this wrong means the re-auth prompt fires
hours after the token died, and the trader discovers it as a rejected order at the next open.
See ADR 0005.

## Rate limits (encoded in the manifest)

| Scope | Per second | Per minute |
|---|---|---|
| Orders | 30 | 250 |
| Data | 1 | 1,000 |
| Quotes | 20 | — |

The data bucket at 1/sec is the binding constraint on instrument-master refresh and history
backfill. Cache aggressively.

## Mapping

All of it lives in `MStockMaps.cs`, tested exhaustively in both directions.

| Canonical | mStock |
|---|---|
| `PositionEffect.Delivery` | `CNC` |
| `PositionEffect.Intraday` | `MIS` |
| `PositionEffect.Margin` | `MTF` |
| `PositionEffect.CarryForward` | `NRML` |
| `OrderType.Market` | `MARKET` |
| `OrderType.Limit` | `LIMIT` |
| `OrderType.Stop` | `SL-M` |
| `OrderType.StopLimit` | `SL` |
| `TimeInForce.Day` / `Ioc` | `DAY` / `IOC` |
| `OrderVariety.Regular` / `AfterMarket` | `reg` / `amo` |
| `Venue XNSE` / `XBOM` | `NSE` / `BSE` (`NFO` / `BFO` for derivatives) |

An unmapped value returns a `Result` failure. It never falls through to a default — a silent
default is how a rejected order shows as open.

## Quirks and gotchas

- **Writes are form-encoded, not JSON.** Placement, modification and position conversion all
  take `application/x-www-form-urlencoded`. The margin calculator is the one documented
  exception and takes JSON. Combined with the HTTP-200 error convention below, sending JSON to
  a form route does not fail loudly — it comes back looking like a rejected order.
- **Modify is a REPLACE, not a patch.** It documents the full order context alongside the
  changed fields, so sending only deltas lets the broker refill the rest from its own defaults —
  a price amendment on a CNC order can come back as MIS and square off at 15:20 unasked. The
  connector reads the live order first and carries the unchanged values through.
- **Cancel-all reports no count.** The documented payload is a single `{"order_id": ...}`.
  Reading a count gives zero, and "0 cancelled" after a successful sweep is indistinguishable
  from a sweep that did nothing. The connector counts what left the book instead.
- **`/trades` is history and takes `fromdate`/`todate`; `/tradebook` is today only.** The date
  window picks the route.
- **`TRIGGERED` means working, not filled.** A fired stop is live at the exchange. Both it and
  bare `PENDING` appear in mStock's own samples.
- **Business failures arrive as HTTP 200** with `status: "error"` in the envelope. `MStockApi`
  unwraps and checks the envelope centrally, once, so no facet can forget.
- **Symbols carry a series suffix on NSE cash** (`INFY-EQ`) but not on BSE. The translator prefers
  the script master and falls back to structural rules while the master is still ingesting — a
  cold start must not look like an outage.
- **The socket and the chart routes identify instruments by numeric token only**, so neither works
  without the script master. The master is held once per process (`SharedInstrumentMaster` in the
  SDK), not per connector instance — connectors are request-scoped, and a per-instance cache was
  empty on every request, so charts and live prices never worked. The first chart, live-price
  subscription or search after a restart downloads it; everything after that reads memory. It is
  reloaded after twelve hours, and a failed reload keeps serving the previous copy.
- **Charts are one route for every interval, and it was wrong until September 2026.** The
  connector called `instruments/historicalchart/{token}` and `instruments/intradaychart/…`, which
  do not exist, so every chart failed with "mStock rejected the request as invalid". The documented
  route is `instruments/historical/{exchange}/{token}/{interval}` with `from`/`to` as
  `yyyy-MM-dd HH:mm:ss` IST. Intervals are spelled `minute` (not `1minute`), `3minute`, `5minute`,
  `10minute`, `15minute`, `30minute`, `60minute` and `day`.
- **At most 1000 candles per request.** Five days of one-minute candles is about 1900, so the
  connector moves `from` forward to the most recent 1000, counting weekdays. It does not split the
  window, because the data limit is one request a second and a second request would be refused.
- **Candle timestamps carry a truncated offset**: `2024-01-01T09:15:00+05`. Read literally that
  is +05:00 and every candle lands half an hour late; `MStockTime.Parse` treats a bare `+05` as
  IST (+05:30).
- **The script master is a large CSV.** It is streamed and parsed, not buffered. Skipped rows are
  counted and surfaced in health — a non-zero count is worth an alert.
- **Orders must come from an IP registered on the API key.** SEBI's retail-algo rules make brokers
  restrict API order placement to a static IP. Market data is not restricted: the script master,
  quotes and charts have been seen working from App Service's unregistered shared addresses, while
  an order from the same app was refused. An order from an unregistered address fails with
  `APIKeyException` / "Primary and Secondary IP Address are not matching with current IP
  address." Despite the exception type, **the key is fine**: regenerating it changes nothing. The
  fix is to register the public IP of the machine running Akshaya as the primary or secondary IP
  in the mStock API portal. The error mapper recognises this text and says so rather than
  reporting an expired key.
  - Running locally: that is your connection's public IP, which most home ISPs change from time to
    time.
  - On Azure App Service: outbound traffic leaves from one of several shared addresses, more than
    the two mStock accepts, and they are not reserved for you. A fixed egress IP needs VNet
    integration with a NAT gateway on a static public IP. The steps, costs and checks are in
    [`deploy/azure-app-service/STATIC-IP.md`](../../deploy/azure-app-service/STATIC-IP.md).

## Smoke test — run this before trusting anything

Automated conformance uses recorded fixtures and proves the mapping, not the API. Only this proves
the API.

1. Link an account through the wizard (OTP and, separately, TOTP).
2. Place a small limit order well away from the market so it rests.
3. Modify its price. Confirm the change **in mStock's own UI**.
4. Cancel it. Confirm in mStock's UI.
5. Fetch order book, trade book, positions, holdings, funds; compare against the UI.
6. Subscribe to two instruments and confirm ticks arrive with sane prices.
7. Leave the session until after midnight IST and confirm the re-auth prompt appears **before**
   the first failed call.

| Date | Tester | Result |
|---|---|---|
| — | — | **Not yet performed** |

## Not offered by Type A

- **GTT (good-till-triggered).** There is no such section in the documentation at all. A
  resting multi-day order must be synthesised by the platform — which is blocked on durable
  persistence, not on this connector. See [`../features/orders.md`](../features/orders.md) §7.
- **Bracket and cover orders**, iceberg orders, GTC/GTD validity.
- **Atomic basket placement.** The "Basket APIs" create *saved* baskets (create / fetch /
  rename / delete / calculate), not multi-leg atomic placement, so `PlaceBasketAsync` loops and
  the manifest keeps `basket.atomic: false`.

## Open questions

- Whether the candle route includes the current session's candles up to now. Kite's does, and
  mStock's Type A mirrors Kite; if it turns out not to, today's bars would have to come from the
  separate `/instruments/intraday/{exchangeCode}/{token}/{interval}` route (current day only,
  exchange as a number: 1 NSE, 2 NFO, 4 BSE, 5 BFO), which means a second data request.
- Whether `cancelall` is atomic or best-effort. The manifest currently claims non-atomic basket
  behaviour, which is the safe assumption.
- Whether `/margins/orders` really wants JSON: the prose says form-encoded, the cURL sample sets
  `Content-Type: application/json` and sends a JSON body. The sample is followed.
- Whether `/trades` accepts its date window as a query string. The docs show `--data-urlencode`
  on a GET; the connector sends a query string, which is the conventional reading.
- Sandbox availability and whether it needs a separate API key.
