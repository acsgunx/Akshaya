# Interactive Brokers connector

**Status:** written from IBKR's Client Portal Web API OpenAPI description (IB REST API 2.38.0) and its
Web API documentation, read 2026-09-12. **No request has ever been sent to a gateway.** The smoke test
below has not been run.

| | |
|---|---|
| Connector id | `ibkr` |
| Project | `src/connectors/Akshaya.Connector.Ibkr` |
| Hosting | Gateway — an operator-run Client Portal Gateway, gateway id `ibkr-cpgw`, default port 5000 |
| Auth | GatewaySession: the gateway holds the IBKR session; the host tickles it every minute |
| Venues | XNYS, XNAS, XASE, ARCX, BATS (US), XHKG, XSES |
| Asset classes | Equity (an ETF is a stock at IBKR), US options |
| Transports | JSON over HTTPS; a JSON websocket for prices |
| Streaming | Prices only |

## Running the gateway

1. Run IBKR's Client Portal Gateway on a machine the operator controls. It serves
   `https://localhost:5000` with a self-signed certificate.
2. Open `https://localhost:5000` **in a browser on that machine** and sign in with the IBKR username
   and its second factor. The gateway holds that brokerage session; every client of the gateway acts
   as that username.
3. Tell the host where the gateway listens, per credential if each user has their own (ADR 0008):

   ```json
   "Connectors": {
     "Gateways": {
       "ibkr-cpgw": { "Host": "127.0.0.1", "Port": 5000 }
     }
   }
   ```

   With nothing configured, the gateway is looked for on loopback at port 5000.
4. **A username has one brokerage session at a time.** Signing in to Trader Workstation or IBKR
   Mobile competes with the gateway's session and can end it.
5. The brokerage session times out when idle. The manifest declares `keepAliveInterval: 00:01:00`,
   and the host's keepalive service calls `POST /tickle` on it. IBKR also ends sessions daily, so a
   link's session is given one day.

## Configuration

Settings are read from `Connectors:Settings:ibkr` (ADR 0008):

| Setting | Default | Effect |
|---|---|---|
| `requestTimeoutSeconds` | `15` | Per-request timeout |
| `allowUntrustedCertificate` | `false` | Accept a self-signed certificate from a gateway **not** on loopback. A loopback gateway's certificate is always accepted. |
| `autoConfirmMessageIds` | empty | Comma-separated order reply message ids this connector may confirm (see Orders) |
| `maxChainStrikes` | `30` | Strikes loaded per option chain, nearest the underlying's price |

## Linking

```text
BeginAsync / ContinueAsync (the same walk; the wizard's "check again" re-runs it)
  POST /iserver/auth/status ─ unreachable, signed out, competing, disconnected ─► GatewayRequired(what to do)
  GET  /iserver/accounts    ─► the account named in the form, or the only one
  ─► Completed(session naming the account)
```

- `account_id` is optional when the username can trade one account and required when it can trade
  several. The error lists the accounts it can trade.
- The session records whether the gateway reported a paper account.
- Refresh is not supported: there is nothing for the platform to refresh.
- Unlinking does not log the gateway out, because the gateway may serve other tools.

## Protocol

| Purpose | Route |
|---|---|
| Session status | `POST /iserver/auth/status` |
| Keepalive, websocket session | `POST /tickle` |
| Trading accounts (required before other iserver routes) | `GET /iserver/accounts` |
| Portfolio accounts (required before other portfolio routes) | `GET /portfolio/accounts` |
| Submit, preview | `POST /iserver/account/{account}/orders`, `…/orders/whatif` |
| Confirm a reply message | `POST /iserver/reply/{id}` |
| Modify, cancel | `POST`, `DELETE /iserver/account/{account}/order/{orderId}` |
| Live orders | `GET /iserver/account/orders` |
| One order | `GET /iserver/account/order/status/{orderId}` |
| Executions (up to 7 days) | `GET /iserver/account/trades?days=` |
| Positions (paged) | `GET /portfolio/{account}/positions/{page}` |
| Balances | `GET /portfolio/{account}/ledger`, `GET /portfolio/{account}/summary` |
| Contract definitions | `GET /trsrv/secdef?conids=` |
| Stock contracts by symbol | `GET /trsrv/stocks?symbols=` |
| Board lots | `GET /iserver/contract/{conid}/info-and-rules` |
| Option strikes and contracts | `GET /iserver/secdef/strikes`, `GET /iserver/secdef/info` |
| Search | `GET /iserver/secdef/search` |
| Snapshots | `GET /iserver/marketdata/snapshot` |
| Candles | `GET /iserver/marketdata/history` |
| Streaming | `wss://…/v1/api/ws` — `smd+CONID+{"fields":[…]}`, `umd+CONID+{}`, `tic` |

All routes are under `/v1/api/`. The gateway's HTTP client is shared by the process per gateway
address, and priming the accounts routes is remembered per gateway for a minute.

## Rate limits

IBKR enforces **10 requests a second per username**, gateway included; the manifest declares that as
a global limit. Beyond it: snapshots 10 a second, executions one request every five seconds, and five
concurrent history requests. A breach answers 429 and can put the address in a penalty box for ten
minutes.

## Contracts

Everything at IBKR is a **conid**, IBKR's global contract identifier. Conid definitions are
reference data, so they are cached once for the process and shared by every connector instance.

- **Decoding a conid** reads `/trsrv/secdef`: listing exchange, currency, and for an option its expiry,
  strike, right, multiplier and underlying conid. An option's venue is its underlying's listing
  venue.
- **Encoding a stock key** reads `/trsrv/stocks` and takes the contract on the key's listing exchange.
  A contract listed on a different venue is not found, never substituted.
- **Encoding an option key** resolves the underlying, then `/iserver/secdef/info` by month, strike and
  right, and takes the contract whose maturity is the key's expiry.
- **ETFs** are stocks at IBKR. They decode as Equity, and an Etf key matches the stock on the same
  venue and symbol.
- **Board lots** for Hong Kong and Singapore come from the contract rules' `sizeIncrement`. If that read
  fails, the definition carries a lot size of 1 and a warning is logged.

| Listing exchange | MIC |
|---|---|
| `NASDAQ` (orders add a tier: `NASDAQ.NMS`) | XNAS |
| `NYSE` | XNYS |
| `AMEX` | XASE |
| `ARCA` | ARCX |
| `BATS` | BATS |
| `SEHK` | XHKG |
| `SGX` | XSES |

Symbols: IBKR writes a US share class with a space (`BRK B`), which the canonical key writes with a
dot (`BRK.B`). Hong Kong codes are padded to five digits (`700` → `00700`).

## Orders

| Canonical | IBKR | Prices |
|---|---|---|
| Market | `MKT` | — |
| Limit | `LMT` | `price` = limit |
| Stop | `STP` | `price` = stop |
| StopLimit | `STOP_LIMIT` | `price` = limit, `auxPrice` = stop |
| TrailingStop | not supported | the contract carries no trailing amount |

- **Time in force:** Day, GTC, IOC. Orders are sent `outsideRTH: false`.
- **Position effect:** Delivery, with ShortSell for a short. IBKR has no product field.
- **Idempotency:** the ClientOrderId is sent as `cOID`, which IBKR requires to be unique for 24 hours.
  A resubmission after a timeout is refused, not doubled, and the id comes back as `order_ref`.
- **Order reply messages.** IBKR may answer a submission with a question — for example `o163`, a price
  outside the username's percentage precaution — and the order does not work until it is confirmed.
  **This connector confirms nothing by default.** A question whose ids are not all in
  `autoConfirmMessageIds` ends the order as `OrderRejected`, carrying IBKR's text and the ids, so the
  operator can decide. Each question is one of the username's own precautions; confirming them
  silently would switch them off.
- **Modify** restates the ticket from the live list with only the changed values different, as IBKR
  requires. Changing the type or time in force is not supported.
- **Cancel** reads the order back, because IBKR's answer only says the request was received.
- **Cancel-all** cancels each working order in the account's live list.
- **Baskets** are a loop of up to 5 orders; every leg's contract is resolved and validated first.
- **The book is this brokerage session's.** The live list holds orders working now or finished since
  the gateway signed in; an order not in it is read from the single-order status route. There is no
  route for older orders. The live list has no submission time, so an order that has not traded is
  stamped when it is read.
- **Executions** reach back at most seven days.
- **Estimates** come from the order preview. Margin: the initial margin the order adds, and the funds
  available before it (equity with loan value less initial margin), in the account's base currency.
  Charges: IBKR's commission only, because exchange, regulatory and clearing fees may be charged on
  top.

| IBKR status | Canonical |
|---|---|
| `PendingSubmit`, `ApiPending` | Submitted |
| `PreSubmitted`, `Submitted`, `PendingCancel`, `WarnState` | Open |
| `Filled` | Filled |
| `Cancelled`, `ApiCancelled` | Cancelled |
| `Inactive` | Unknown, with IBKR's words kept |

`Inactive` covers rejected orders, orders held for a margin or permission problem, and orders parked
by the exchange; nothing in the status says which. An order in the live list whose type this connector
does not map, such as one placed from Trader Workstation, is logged and left out of the book.

## Portfolio

Positions are read page by page and partitioned: a long stock is a holding; options and shorts are
positions. The average price is IBKR's `avgPrice`: per share, or per unit of the underlying for an
option, the scale a quote uses. A position in a contract outside the declared venues and asset
classes (cash, futures, crypto) is skipped; a position whose contract cannot be described fails the
read.

Balances come from the ledger, one per currency. Available funds, buying power and initial margin are
account-wide figures from the summary, in the base currency, so they appear on that currency's row
only.

## Market data

- **Snapshots need a pre-flight.** The first request for a conid only starts IBKR's stream for it and
  returns no prices. Empty rows are asked for again, twice, 600 ms apart; a conid still empty is left
  out of the answer.
- Fields: last (31, with a `C` or `H` prefix stripped), high 70, low 71, bid 84, ask 86, volume 7762
  (87 as a fallback), last size 7059, open 7295, prior close 7741, option open interest 7638.
- **Bid and ask sizes are not reported.** IBKR scales them for US stocks in a way its documentation
  does not pin down.
- **Candles** are regular-session trade bars, walked forward one page at a time.
- **Depth** is not supported: the Client Portal API serves the book only on its BookTrader websocket.
- **Option chains** are for US stocks and are **windowed** to the strikes nearest the underlying's
  price (`maxChainStrikes`). Each strike and right is one contract lookup, so a full chain is
  impractical.
- **Reference:** there is no downloadable master through this API, so `GetInstrumentsAsync` yields
  nothing. Search uses IBKR's symbol search for stocks.

## Streaming

- One websocket, opened after a tickle; the tickle's session is sent as the `api` cookie.
- `smd+CONID+{"fields":[…]}` subscribes and `umd+CONID+{}` unsubscribes. A push carries only the fields
  that changed, so the latest value of each is kept per conid and every push publishes a whole tick.
- `tic` is sent every 60 seconds. An `sts` message saying the session signed out makes the state
  Degraded.
- The desired set is replayed on every reconnect. A signed-out gateway is retried with backoff,
  because the operator can sign it in again.
- **Order updates are not streamed**: the gateway's order topic is not described in the documentation
  this was written from. Orders are read from the book.
- **Full mode is not offered**: the book is not streamed. Subscriptions are capped at 100, IBKR's
  default market data lines.

## Not offered

| Capability | Why |
|---|---|
| Market depth | BookTrader websocket only |
| Order updates on the stream | Topic undocumented in the sources used |
| Trailing stops, market-if-touched | Not mapped |
| GTD, at-the-open, at-the-close | Not mapped |
| Orders older than this brokerage session | No route |
| Silent session refresh | The gateway holds the session |
| Hong Kong and Singapore options, futures, FX, bonds, crypto | Outside the declared scope |
| Full option chains | Windowed; see above |
| Instrument master | Not available through this API |

## Quirks

- A gateway answers 401 when signed out; that maps to ReauthRequired, not SessionExpired.
- A conid is a number on some routes and a string on others; so is an order id.
- Search for an unknown symbol answers an object with an error, not an empty list.
- The live orders list can come back without a list on a brokerage session's first read, so it is
  read once more.
- The gateway's certificate is self-signed.

## Smoke test — not yet run

1. Run the gateway, sign in, and link a paper account.
2. `GET /api/connectors/ibkr/health`: gateway running, signed in, not competing.
3. Search `AAPL`; resolve `XNAS:AAPL:Equity` and `XHKG:00700:Equity`. Confirm the Hong Kong lot size.
4. Quote both twice; confirm the first may be empty and the second carries prices.
5. Place a limit order far from the market. If a reply message comes back, confirm the error names its
   id. Add that id to `autoConfirmMessageIds` and place again.
6. Confirm the order appears with its ClientOrderId. Modify the price, then cancel.
7. Place a **stop** order and confirm in Trader Workstation that the stop price is the one sent — this
   settles whether STP takes its stop in `price`.
8. Compare positions, holdings and balances with Client Portal.
9. Subscribe `XNAS:AAPL:Equity`; confirm ticks. Sign the gateway out; confirm the state turns Degraded
   or Reconnecting.
10. Leave the link idle for ten minutes; confirm the keepalive keeps the session signed in.

## Open questions

- Whether STP carries its stop in `price` or `auxPrice`.
- Whether position pages start at 0.
- Whether the live orders list carries `auxPrice` and `order_ref` for every order.
- Whether the gateway's websocket needs the `api` cookie, and the exact shape of `sts`.
- How US stock bid and ask sizes are scaled.
- Whether history volumes need the response's `volumeFactor` applied.
- The single-order status route's price field names (`limit_price`, `stop_price` are assumed).
- Whether `/trsrv/secdef` reports Hong Kong and Singapore listings as `SEHK` and `SGX`.
