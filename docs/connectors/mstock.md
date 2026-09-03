# Connector: m.Stock (Mirae Asset Capital Markets)

- **Id:** `mstock`
- **Market:** India — NSE, BSE, cash and F&O
- **API:** Type A REST + WebSocket
- **Docs:** https://tradingapi.mstock.com/docs/v1/typeA/
- **Hosting:** in-process
- **Status:** implemented. The **login leg is verified against a live account**; every leg after
  it is still documentation-only — see below.

> ⚠️ Most of this file comes from the published documentation, and that documentation could not
> be re-verified while writing this (the docs host returns `AccessDenied` to automated clients).
> The response shapes in `MStockDtos.cs` remain the most likely place reality differs from this
> code.
>
> **This has already bitten once.** The first login leg returned a perfectly good
> `{"status":"success", …}` that the connector rejected, because three flags documented as
> strings arrive as booleans — and one type mismatch aborts the whole document. The observed
> payload, the fix, and the parsing rule it forced are written up in
> [`mstock-login-response.md`](mstock-login-response.md). **Read it before touching any DTO in
> this connector**; the next unverified leg (`session/token`) is wide open to the same fault.

## Endpoints

| Purpose | Method and path |
|---|---|
| Base | `https://api.mstock.trade` |
| Streaming | `wss://ws.mstock.trade` |
| Login | `POST /openapi/typea/connect/login` |
| Session token (OTP) | `POST /openapi/typea/session/token` |
| Verify TOTP | `POST /openapi/typea/session/verifytotp` |
| Logout | `GET /openapi/typea/logout` |
| Place order | `POST /openapi/typea/orders/{variety}` — `reg` or `amo` |
| Modify | `PUT /openapi/typea/orders/regular/{orderId}` |
| Cancel | `DELETE /openapi/typea/orders/regular/{orderId}` |
| Cancel all | `POST /openapi/typea/orders/cancelall` |
| Order book | `GET /openapi/typea/orders` |
| Order details | `GET /openapi/typea/order/details?order_no=&segment=` (`E` / `D`) |
| Trade book | `GET /openapi/typea/tradebook` |
| Trades | `GET /openapi/typea/trades` |
| Script master | `GET /openapi/typea/instruments/scriptmaster` (CSV) |
| LTP | `GET /openapi/typea/instruments/quote/ltp` |
| OHLC | `GET /openapi/typea/instruments/quote/ohlc` |

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

- **Business failures arrive as HTTP 200** with `status: "error"` in the envelope. `MStockApi`
  unwraps and checks the envelope centrally, once, so no facet can forget.
- **Symbols carry a series suffix on NSE cash** (`INFY-EQ`) but not on BSE. The translator prefers
  the script master and falls back to structural rules while the master is still ingesting — a
  cold start must not look like an outage.
- **The socket identifies instruments by numeric token only**, so streaming cannot work at all
  until the script master is loaded. The connector's health check reports this as degraded, not
  unhealthy: an un-ingested master still permits trading through the structural fallback.
- **The script master is a large CSV.** It is streamed and parsed, not buffered. Skipped rows are
  counted and surfaced in health — a non-zero count is worth an alert.

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

## Open questions

- Exact response shape of the historical/intraday chart endpoints — the DTOs are a best reading of
  the docs.
- Whether `cancelall` is atomic or best-effort. The manifest currently claims non-atomic basket
  behaviour, which is the safe assumption.
- Sandbox availability and whether it needs a separate API key.
