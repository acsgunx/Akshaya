# Longbridge connector

**Status:** written from the Longbridge OpenAPI documentation and the official SDK source
(`longbridge/openapi`, Rust) and protobuf definitions (`longbridge/openapi-protobufs`), read
2026-09-11. **No authenticated request has ever been sent.** The smoke test below has not been run.

| | |
|---|---|
| Connector id | `longbridge` |
| Project | `src/connectors/Akshaya.Connector.Longbridge` |
| Hosting | In process |
| Auth | OAuth 2.0 authorization code with PKCE; access token lives 30 days; silent refresh |
| Venues | XNYS, XNAS, XASE, ARCX, BATS (US), XHKG, XSES |
| Asset classes | Equity (ETFs decode as Equity), Index, US options |
| Transports | REST for trading and assets; binary WebSocket gateways for quotes and pushes |
| Streaming | Quote and depth pushes; order-change pushes |

## Before linking

1. **Enable OpenAPI on the Longbridge account**, for trading and for quotes. OpenAPI quote
   permissions are separate from the app's: Hong Kong real-time quotes over the API need the LV1
   OpenAPI quote card, and Singapore quotes are not served over the API at all.
2. **Register an OAuth client once per deployment.** Registration normally returns a public client
   (a `client_id` and no secret), which signs in with PKCE:

   ```bash
   curl -X POST https://openapi.longbridge.com/oauth2/register \
     -H "Content-Type: application/json" \
     -d '{"client_name":"akshaya","redirect_uris":["https://YOUR-DEPLOYMENT/connectors/longbridge/link"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"]}'
   ```

   The redirect URI is the link wizard's own page, `<origin>/connectors/longbridge/link`
   (`http://localhost:4200/connectors/longbridge/link` in local development). It must match
   character for character.
3. **Link through the wizard** with the `client_id`, the `client_secret` only if registration
   returned one, and `environment` — `real` (the default) or `paper`.

## Configuration

Settings are read from `Connectors:Settings:longbridge` (see ADR 0008):

```json
"Connectors": {
  "Settings": {
    "longbridge": { "region": "cn", "requestTimeoutSeconds": "20" }
  }
}
```

| Setting | Default | Effect |
|---|---|---|
| `region` | `global` | `cn` switches every endpoint to the `*.longbridge.cn` hosts, which differ only in their domain |
| `requestTimeoutSeconds` | `15` | REST and socket request timeout |

## Protocol

| Purpose | Transport | Route or command |
|---|---|---|
| Authorize (browser) | HTTPS | `GET /oauth2/authorize?response_type=code&client_id&redirect_uri&scope=3&state&code_challenge&code_challenge_method=S256` |
| Token, refresh | HTTPS, form | `POST /oauth2/token` |
| Revoke | HTTPS, form | `POST /oauth2/revoke` |
| Socket one-time password | REST | `GET /v1/socket/token` |
| Submit, replace, cancel, detail | REST | `POST`, `PUT`, `DELETE`, `GET /v1/trade/order` |
| Orders | REST | `GET /v1/trade/order/today`, `GET /v1/trade/order/history` |
| Fills | REST | `GET /v1/trade/execution/today`, `GET /v1/trade/execution/history` |
| Positions | REST | `GET /v1/asset/stock` |
| Balances | REST | `GET /v1/asset/account` |
| Sign-in, identity | quote socket | 2 `Auth`, 4 `QueryUserQuoteProfile` (member id) |
| Static information | quote socket | 10 |
| Quotes | quote socket | 11 securities, 12 options |
| Depth | quote socket | 14 |
| Option chain strikes | quote socket | 21 |
| Candles | quote socket | 27 |
| Subscribe, unsubscribe | quote socket | 6, 7; pushes 101 quote, 102 depth |
| Order pushes | trade socket | 16 subscribe topic `private`; 18 notification carrying JSON `order_changed_lb` |

REST answers `{ code, message, data }` and a failure can arrive as HTTP 200 with a non-zero code, so
the envelope, not the status, decides. Almost every number is a string.

The sockets carry a binary packet inside each WebSocket binary message. All integers are
**big-endian** (the opposite of moomoo OpenD):

```text
request   [header][cmd][request_id u32][timeout_ms u16][body_len u24][body]
response  [header][cmd][request_id u32][status u8][body_len u24][body]
push      [header][cmd][body_len u24][body]

header    low four bits: 1 request, 2 response, 3 push
          0x20  body is gzipped
          0x10  a 24-byte nonce and signature follow the body
```

Bodies are protobuf, encoded and decoded by hand in `LongbridgeProtobuf.cs` (only the fields this
connector reads). A socket opens as: one-time password from REST, WebSocket upgrade with
`?version=1&codec=1&platform=9`, then `Auth` with the password. The password is single-use, so every
reconnect asks for a new one, and the token route reports how many of the account's socket
connections are already open — the connector refuses to open one past the limit rather than evict
another tool's connection.

## Sign-in

```text
BeginAsync(client_id, environment, redirect_uri)
  -> RedirectRequired(authorize URL, state)
       the user signs in at Longbridge; the wizard page receives ?code=…&state=…
ContinueAsync(code)
  POST /oauth2/token  grant_type=authorization_code, client_id, redirect_uri, code, code_verifier
  quote socket: Auth, QueryUserQuoteProfile -> member_id
  -> Completed(BrokerSession)
```

- **The PKCE verifier is derived, not stored.** The contract carries one value across the redirect,
  the state, and the verifier must not ride with it: the state comes back on the same URL as the
  code. The verifier is an HMAC-SHA256 of the state under a key generated when the process starts.
  A sign-in begun before an API restart, or on a different instance of a scaled-out deployment,
  therefore fails its token exchange and must be started again; the error says so.
- **The account identity is the member id** the quote gateway reports. No REST route names the
  account. A paper link's `AccountId` is suffixed `-paper`, because paper trading shares the login
  but is a different book.
- **Session lifetime** is the token response's `expires_in` (30 days in the documentation), falling
  back to the manifest's 30 days.
- **Refresh** is silent for a public client: the refresh grant needs only the client id and the
  refresh token. A confidential client's secret is not kept after linking, so Longbridge refuses its
  refresh and the platform prompts for a new sign-in once a month.
- **Revoke** revokes the refresh token, which ends the grant.
- The session carries `clientId`, `environment` and `memberId` in its extras. Every REST call sends
  `Authorization: Bearer`, `X-Api-Key: <client id>`, `X-Timestamp`, `x-dc-region` (`us` for a token
  prefixed `us_`, else `ap`) and, for paper, `x-papertrading: true`.

## Rate limits

The pages read publish no per-route limits. REST codes 429001/429002 and socket code 301606 map to
`RateLimited`. The manifest declares conservative guesses — orders 1/s and 30/min, quotes 5/s, data
5/s and 120/min — until measured. The account's subscription cap (500 securities) is counted by the
stream, which refuses past it with an explanation.

## Symbology

| Canonical key | Longbridge symbol |
|---|---|
| `XNAS:AAPL:Equity` | `AAPL.US` |
| `XNYS:BRK.B:Equity` | `BRK.B.US` |
| `XHKG:00700:Equity` | `700.HK` |
| `XSES:D05:Equity` | `D05.SG` |
| `XNAS:AAPL:OPT:2026-01-16:150:Call` | `AAPL260116C150000.US` |

- Hong Kong codes lose their leading zeros on the way out and are padded back to five digits on the
  way in.
- A US symbol names no listing exchange, so decoding one needs static information, cached per
  connector. Every US MIC encodes to the same `.US` symbol.
- A US option symbol is OCC-shaped with the strike in thousandths. An option's venue is its
  underlying's listing venue. Its multiplier is the one Longbridge quoted when its chain was loaded,
  otherwise the standard 100.

| Static `exchange` | MIC | Confirmed |
|---|---|---|
| `NASD` | XNAS | In Longbridge's examples |
| `SEHK` | XHKG | In Longbridge's examples |
| `NYSE` | XNYS | Standard short name |
| `AMEX` | XASE | Standard short name |
| `ARCA` | ARCX | Standard short name |
| `BATS` | BATS | Standard short name |
| `SGX` | XSES | Standard short name |

An exchange not in this table makes the security out of scope, never a guess.

| Board | Asset class |
|---|---|
| `USMain`, `HKEquity`, `SGMain` | Equity |
| `USDJI`, `USNSDQ`, `USSector`, `SPXIndex`, `VIXIndex`, `HKHS`, `HKSector`, `STI`, `SGSector` | Index |
| `USOption`, `USOptionS` | Option |
| `USPink`, `HKPreIPO`, `HKWarrant`, China Connect boards | Out of scope |
| anything else, including `Unknown` | Unrecognised: out of scope |

Longbridge has no ETF board, so an ETF decodes as Equity everywhere it appears. Hong Kong and
Singapore definitions carry the board lot as `LotSize`.

## Orders

| Canonical | US | Hong Kong | Singapore |
|---|---|---|---|
| Market | `MO` | `MO` | `MO` |
| Limit | `LO` | `ELO` | `LO` |
| Stop | `MIT` | `MIT` | not supported |
| StopLimit | `LIT` | `LIT` | not supported |
| TrailingStop | not supported | not supported | not supported |

- **Stops are touched orders.** Longbridge has no plain stop; its market-if-touched and
  limit-if-touched orders trigger when the price reaches the trigger from either side, which is a
  stop when the trigger sits beyond the market. Longbridge's own attached stop-losses are placed as
  these types. `MarketIfTouched` is therefore not declared: an `MIT` read back could not say which of
  the two was meant.
- **A Hong Kong limit is the enhanced limit order**, which fills at the limit or better, as a limit
  does everywhere else. HKEX's plain `LO` fills only at the price.
- **Time in force:** Day, GTC, GTD (with `expire_date`).
- **Position effect:** Delivery, with ShortSell to open or cover a short. Margin use is the account's.
  Orders read back carry Delivery; Longbridge reports no short flag.
- **US orders are sent `outside_rth: RTH_ONLY`**, the sessions the platform's calendars model.
- **Idempotency is the broker's.** The ClientOrderId goes in `client_request_id` — a repeat within ten
  minutes returns the original order — and in `remark`, which every order row echoes, so a timed-out
  placement is found in the book after the cache expires.
- **Modify** restates the order: the replace route requires the quantity every time, so the order is
  read first. Changing the type or time in force is not supported.
- **Cancel** reads the order back, because a cancel passes through `WaitToCancel` before it is
  confirmed.
- **Cancel-all** cancels each of today's working orders; there is no route for it.
- **Baskets** are a loop of up to 10 orders, validated in full before the first is sent.
- **Margin and charges estimates** are not supported.

| Longbridge status | Canonical |
|---|---|
| `NotReported`, `ReplacedNotReported`, `ProtectedNotReported`, `WaitToNew` | Submitted |
| `VarietiesNotReported` (a conditional order waiting at the broker) | Open |
| `NewStatus`, `WaitToReplace`, `PendingReplaceStatus`, `ReplacedStatus`, `WaitToCancel`, `PendingCancelStatus` | Open |
| `PartialFilledStatus` | PartiallyFilled |
| `FilledStatus` | Filled |
| `RejectedStatus` | Rejected |
| `CanceledStatus`, `PartialWithdrawal` | Cancelled |
| `ExpiredStatus` | Expired |

Book reads merge today's routes with the history routes by id when the query reaches back before
today, and filter by the market date of each row.

## Portfolio

Positions are one list, grouped by account channel. The contract's two lists are a partition: a long
equity is a holding; options and shorts are positions. A security held in more than one channel is
combined with a quantity-weighted cost. Longbridge reports no last price or P&L on a position.

Balances are one per currency with cash, from `cash_infos`: `AvailableToTrade` is `available_cash`.
Buying power and initial margin are account-wide figures in the account's base currency, so they
appear on that currency's row only.

## Market data and reference

- **Quotes** need no subscription and take up to 500 symbols a request. The pull quote has no bid or
  ask, so those are empty.
- **Candles** are unadjusted and regular-session. A date query is walked forward page by page when
  its last candle falls short of the window. The candle timestamp is read as the bar's open.
- **Depth** is up to 10 levels.
- **Option chains** are for US underlyings: the strikes request names the contracts, and option
  quotes price them with open interest and the contract multiplier. A contract with a non-standard
  root is left out rather than decoded into the wrong key.
- **Reference:** Longbridge publishes no security master, so `GetInstrumentsAsync` yields nothing.
  Securities are resolved on demand. Search tries a query as a US ticker, a Hong Kong number and a
  Singapore code.

## Streaming

- The stream owns two sockets: quotes and depth on the quote gateway, order pushes on the trade
  gateway's `private` topic.
- The desired subscription set is replayed on every reconnect. Either socket dropping reconnects
  both. A trade socket that cannot be opened at all leaves prices running and the state Degraded.
- A token Longbridge refuses stops the stream (state Disconnected) instead of retrying.
- Order pushes carry no time in force, so an update reads as Day until the book is next read; and no
  trade id, so fills are reconciled from the execution routes rather than pushed.

## Not offered

| Capability | Why |
|---|---|
| Margin and charges estimates | Longbridge estimates maximum quantity, not the margin or charges of an order |
| Position conversion | No product types |
| Trailing stops | Longbridge trails by an amount or percentage the contract does not carry |
| Pre- and post-market sessions | Orders are sent regular hours only |
| Fractional shares | Not offered through this connector |
| Hong Kong options | Symbology not mapped |
| China Connect, OTC, warrants, grey market | Outside the declared venues |
| Instrument master | Longbridge publishes none |
| Singapore quotes | Not served over OpenAPI |

## Quirks

- A failure can be HTTP 200 with a non-zero `code`.
- Numbers arrive as strings, sometimes as numbers; the JSON converter accepts both.
- A zero timestamp means "not set".
- The data-centre header is inferred from the token prefix.
- The socket framing is big-endian; OpenD's is little-endian.

## Smoke test — not yet run

1. Register an OAuth client with this deployment's redirect URI. Link a paper account.
2. `GET /api/connectors/longbridge/health`: session valid, expiry about 30 days out.
3. Search `AAPL`, `700` and `D05`; resolve `XNAS:AAPL:Equity` and `XHKG:00700:Equity`.
4. Quote both; compare with the Longbridge app.
5. Place a limit order far from the market. Confirm the order appears with its ClientOrderId.
   Modify the price, then cancel. Confirm each status.
6. Submit the same ClientOrderId again within ten minutes; confirm the same order id comes back.
7. Compare positions, holdings and balances with the app.
8. Subscribe `XNAS:AAPL:Equity` in Full mode; confirm ticks and depth. Place and cancel a paper
   order; confirm `OrderUpdated` events.
9. Refresh the session; confirm a new expiry. Unlink; confirm the token is revoked.

## Open questions

- The static-information exchange strings for NYSE American, NYSE Arca and Cboe BZX; only `NASD` and
  `SEHK` are confirmed.
- Whether today's orders include GTC orders placed on earlier days, which decides what cancel-all
  reaches.
- Whether a candle's timestamp is its open or its close.
- Real per-route rate limits.
- What `scope=3` grants; it is the documentation's example and a setting on `LongbridgeOptions`.
- Whether the member id is the same for a login's real and paper accounts.
- Whether the trade socket opened for a paper link pushes paper orders.
