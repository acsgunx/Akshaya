# Tiger Brokers connector

**Status:** written from Tiger's official Python SDK (`tigeropen`) and its OpenAPI documentation, read
2026-09-12. **No authenticated request has ever been sent.** The smoke test below has not been run.

| | |
|---|---|
| Connector id | `tiger` |
| Project | `src/connectors/Akshaya.Connector.Tiger` |
| Hosting | In process |
| Auth | RSA-signed: every request carries a SHA1withRSA signature; there is no login and no token to expire |
| Venues | XNYS, XNAS, XASE, ARCX, BATS (US), XHKG, XSES |
| Asset classes | Equity (an ETF is a stock at Tiger), Option |
| Transport | JSON over HTTPS, one POST per method |
| Streaming | None — see Not offered |

## Before linking

1. **Create an application on the Tiger OpenAPI console** and register the PUBLIC half of an RSA key
   pair with it. The console shows the application's **tiger id**.
2. **Note the account** to trade — a standard, prime or paper account id — and the **licence** the
   account is held under (`TBSG`, `TBNZ`, `TBHK`, `TBAU`, `TBUS`). The licence decides which host serves
   it.
3. **Quote permissions are separate from trading** and are granted per market on the console. A market
   without them answers Tiger's permission error, which this connector reports as NotSupported.
4. **Link** with the tiger id, the account, the private key, and — if the licence needs one — the
   two-factor token.

## Configuration

Settings are read from `Connectors:Settings:tiger` (ADR 0008):

| Setting | Default | Effect |
|---|---|---|
| `serverUrl` | from the licence | Overrides the trading endpoint |
| `quoteServerUrl` | from the licence | Overrides the quote endpoint |
| `requestTimeoutSeconds` | `15` | Per-request timeout |
| `deviceId` | `akshaya` | Sent as `device_id` |
| `language` | `en_US` | Sent as `lang`, so Tiger's messages come back classifiable |
| `verifyResponseSignature` | `true` | Checks Tiger's signature on every answer. Leave it on. |

## Protocol

Every method is one POST to one endpoint; the method is a parameter, not a path:

```json
{
  "tiger_id": "20150001",
  "method": "place_order",
  "charset": "UTF-8",
  "version": "2.0",
  "sign_type": "RSA",
  "timestamp": "2026-09-12 14:03:11",
  "device_id": "akshaya",
  "biz_content": "{\"account\":\"…\",\"symbol\":\"AAPL\",…}",
  "sign": "base64…"
}
```

- **The signature** covers every parameter above it: sorted by name, joined as `k=v` with `&`, signed
  SHA1withRSA (PKCS#1 v1.5) and base64-encoded. SHA-1 would be the wrong choice for anything designed
  today; it is what Tiger's gateway verifies.
- **`biz_content` is a string**, not an object — the method's own arguments, serialised. The same string
  that was signed is what is sent.
- **The answer is `{ code, message, data, timestamp, sign }`**. Code 0 is success; anything else is a
  failure even with HTTP 200. `data` is usually an object and occasionally a JSON string, which is
  parsed either way.
- **Tiger signs the answer** — its signature covers the request's own timestamp — and this connector
  verifies it with Tiger's public key. An answer that does not verify is discarded rather than parsed.
- **The timestamp is sent in Hong Kong time**, where Tiger's gateway runs, so its meaning does not
  change with where the platform is deployed.
- A licence that needs a two-factor token sends it as the `Authorization` header.

| Licence | Trading | Quotes |
|---|---|---|
| `TBSG`, `TBNZ` | `https://openapi.tigerfintech.com/hkg/gateway` | `https://openapi.tigerfintech.com/hkg-quote/gateway` |
| `TBUS` | `https://openapi.tradeup.com/gateway` | same |
| `TBHK`, `TBAU`, unset | `https://openapi.tigerfintech.com/gateway` | same |
| any, paper | `https://openapi-sandbox.tigerfintech.com…` | same |

Tiger's own SDKs discover these hosts from a configuration service at start-up. This connector uses the
published defaults and lets an operator override either endpoint.

## Linking

```text
BeginAsync / ContinueAsync (the same walk; there is no interactive step)
  prime_assets { account } ─ refused ─► InvalidCredentials, with Tiger's words
  ─► Completed(session)
```

Signing a real request is the only way to know the key, the tiger id and the account agree. The
session's access token **is the private key**: Tiger issues nothing else, and a request is authenticated
by being signed. Links live in memory only, and the key is never sent — it signs, and the signature
travels. There is nothing to refresh or revoke; unlinking discards the platform's copy.

## Symbology

| Canonical key | Tiger |
|---|---|
| `XNAS:AAPL:Equity` | symbol `AAPL`, market `US` |
| `XHKG:00700:Equity` | symbol `00700`, market `HK` |
| `XSES:D05:Equity` | symbol `D05`, market `SG` |
| `XNAS:AAPL:OPT:2026-01-16:150:Call` | identifier `AAPL  260116C00150000` |

Symbols are spelled the same way the canonical key spells them, so there is no translation table. An
option is an OCC identifier: the underlying padded to six characters, `yyMMdd`, `C` or `P`, and the
strike in thousandths over eight digits.

**A US row does not carry its listing venue.** Tiger repeats the contract on every order, position and
execution — symbol, market, currency, and for an option expiry, strike, right and multiplier — which is
enough to describe it except for which US exchange it lists on. That is looked up once per symbol
through the contract method and cached for the process. Hong Kong and Singapore have one listing venue
each, so they need no lookup.

## Orders

| Canonical | Tiger | Prices |
|---|---|---|
| Market | `MKT` | — |
| Limit | `LMT` | `limit_price` |
| Stop | `STP` | `aux_price` = stop |
| StopLimit | `STP_LMT` | `limit_price` = limit, `aux_price` = stop |
| TrailingStop | not supported | Tiger trails by an amount or a percentage, which the contract does not carry |

- **Time in force:** Day, GTC, GTD. A GTD order sends `expire_time` as the market's own end of day in
  epoch milliseconds.
- **Position effect:** Delivery, with ShortSell for a short. Tiger has no product field.
- **Orders are sent `outside_rth: false`** — the sessions the platform's calendars model.
- **The ClientOrderId travels in `user_mark`**, which Tiger echoes on every order row. **Tiger offers no
  idempotency key**, so a placement that times out is recovered by reading the book back and matching on
  that marker, never by resending.
- **Modify** restates the whole ticket, as Tiger's modify expects; changing the type or time in force is
  not supported.
- **Cancel** reads the order back, because Tiger acknowledges the request rather than the cancellation.
- **Cancel-all** cancels each of the account's active orders; there is no method for it.
- **Baskets** are a loop of up to 5 orders, every leg built and its contract resolved first.
- **Estimates** come from `preview_order`: the initial margin the order would require, and Tiger's
  commission. Exchange and regulatory fees may be charged on top.
- Orders and executions are **paged**; a query walks up to 20 pages.

| Tiger status | Canonical |
|---|---|
| `Initial` (-1), `PendingSubmit` (8) | Submitted |
| `Submitted` (5), `PendingCancel` (3) | Open |
| `PartiallyFilled`, or Submitted with a filled quantity | PartiallyFilled |
| `Filled` (6) | Filled |
| `Cancelled` (4) | Cancelled |
| `Inactive` (7) | Rejected |
| `Invalid` (-2) | Expired |

Statuses arrive as a name or as the number behind it; both are mapped.

## Portfolio

Positions are one list, partitioned: a long stock is a holding; options and shorts are positions. A
position in something outside the declared scope — a future, a warrant — is skipped with a log rather
than priced as if it were a share.

Balances come from the prime-assets view, one row per currency the **securities** segment holds:
available cash to trade, the cash balance, and — on the segment's own currency row — buying power,
initial margin and P&L. A commodities segment belongs to futures and is not reported.

## Market data

- **Stock quotes** come from `quote_real_time`, up to 50 symbols a request, with the best bid and ask.
- **Option quotes** come from `option_brief`, each contract named by underlying, expiry, right and strike.
- **Candles** come from `kline`, unadjusted, walked through Tiger's page tokens.
- **Depth** is `quote_depth`, up to ten levels a side.
- **Option chains** are one request per expiry: Tiger answers every strike's call and put together, on
  service version 3.0.
- **Reference:** Tiger's symbol list carries no venue, currency or lot, so `GetInstrumentsAsync` yields
  nothing and contracts are described on demand. Search matches a symbol exactly — Tiger's contract
  lookup is not a free-text search.

## Not offered

| Capability | Why |
|---|---|
| Streaming prices and order updates | Tiger's push feed is a protocol of its own; the manifest declares no streaming |
| Position conversion | No product types |
| Trailing stops | Tiger trails by an amount or percentage the contract does not carry |
| Futures, warrants, funds, FX | Outside the declared venues and asset classes |
| Instrument master | Tiger's symbol list carries none of what a definition needs |
| Free-text search | The contract lookup matches a symbol exactly |
| Pre- and post-market sessions | Orders are sent regular hours only |
| Fractional shares | Not offered through this connector |

## Quirks

- `data` is an object on most methods and a JSON string on a few.
- Numbers arrive as numbers or as strings depending on the method; both are accepted.
- The answer's signature covers the REQUEST's timestamp, not the answer's body.
- The paper environment signs with a different key, which this connector selects automatically.
- A licence, not a setting, decides which host serves an account.

## Smoke test — not yet run

1. Register an application and its public key on the Tiger console; link a paper account.
2. `GET /api/connectors/tiger/health` — session valid.
3. Resolve `XNAS:AAPL:Equity` and `XHKG:00700:Equity`; confirm the Hong Kong lot size and that AAPL's
   venue comes back as XNAS.
4. Quote both, and an option on AAPL; compare with the Tiger app.
5. Place a limit order far from the market. **Confirm the ClientOrderId comes back in `user_mark`** —
   if Tiger truncates or refuses a 32-character marker, that is the first thing this connector needs to
   change.
6. Modify its price, then cancel it. Confirm each status.
7. Place a **stop** order and confirm in the Tiger app that the stop price is the one sent — this
   settles that `aux_price` carries the stop.
8. Compare positions, holdings and balances with the app.
9. Ask for an order preview; confirm the margin and commission fields are the ones this connector reads.
10. Fetch daily candles for a month and compare a few closes with the app.

## Open questions

- Whether `user_mark` accepts 32 characters, and what it truncates to if not.
- Whether `orders` with an `id` answers a single order object, as this connector assumes, or a page.
- The exact field names in the order preview (`initMargin`, `commission`, `marginCurrency`).
- Tiger's error-code space, which the SDK does not enumerate: failures are classified from the message.
- Whether `contracts` requires the `account` parameter it is sent.
- Whether a GTD order's `expire_time` is read in the market's zone, as this connector sends it.
- Whether Hong Kong symbols are always five digits in every method's answer.
