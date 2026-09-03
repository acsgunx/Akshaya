# mStock Type A — where this connector disagreed with the API

**Status:** resolved. Eleven faults, each verified against
[the official Type A documentation](https://tradingapi.mstock.com/docs/v1/typeA/User/)
(User, Orders, Portfolio and Position pages, retrieved 2026-09-03) and pinned by tests.

This started as one bug — a successful login rejected as unparseable — but reading the
documentation showed the same *kind* of mistake in ten more places, nearly all in code paths
nobody had run yet. They share one cause: **the connector was written to Zerodha Kite's
shapes**, which mStock resembles closely enough to be dangerous and differs from in specifics.

| # | What broke | Where | Would have surfaced as |
|---|---|---|---|
| 1 | `is_kyc`/`is_activate`/`is_password_reset` typed as `string?`, sent as booleans | login | ✅ reported — "response could not be understood" |
| 2 | `checksum` computed as `SHA256(key+token+secret)`; it is the literal source string `L` | OTP / TOTP session | every OTP rejected |
| 3 | Fund summary expected Kite's `{"equity":{…}}`; mStock sends an **array** of flat rows | fund summary | balances never loaded |
| 4 | Logout `data` is the string `"Success"`, deserialised into a class | logout | every logout reported failed |
| 5 | `"scrip"` matched inside `"subscription"` | error mapping | expired API key reported as *"instrument not found"* |
| 6 | Order book returns `product: "INTRADAY"`; only `CNC`/`MIS`/`MTF`/`NRML` were mapped | order book, positions | every row unmappable |
| 7 | `tag` typed `string?`; the order book sends `[]` | order book | whole order book failed to parse |
| 8 | `/order/details` stamps times `"23-01-2025 02:55:55 PM"` (12-hour); no such format was accepted | order details | orders with no usable timestamp |
| 9 | `/tradebook` is `SCREAMING_SNAKE`, read as snake_case | trade book | all-null rows, "trade_id is missing", working fallback never ran |
| 10 | Holdings send the **company name** as `tradingsymbol` with `exchange: null` | holdings | every holding unidentifiable |
| 11 | Positions send `product: ""`, holdings `product: null` | positions, holdings | every row rejected |

---

## The symptom

A user with valid credentials could not link their mStock account. The API returned:

```
The broker's response could not be understood.
— broker said: {"status":"success","data":{ … }}
```

The contradiction in that sentence is the whole story: **mStock said `"status":"success"`.**
The login had worked. We threw the result away.

## The response, verbatim

```json
{
  "status": "success",
  "data": {
    "ugid": "5544454f-5148-46f5-aca0-dee98ad5995c",
    "is_kyc": true,
    "is_activate": false,
    "is_password_reset": true,
    "is_error": false,
    "cid": "1111",
    "nm": "",
    "flag": 0
  }
}
```

## Root cause

`MStockLoginData` declared:

| Field | DTO declared | mStock actually sends | Result |
|---|---|---|---|
| `is_kyc` | `string?` | `true` — a **boolean** | 💥 fatal |
| `is_activate` | `string?` | `false` — a **boolean** | 💥 fatal |
| `is_password_reset` | `string?` | `true` — a **boolean** | 💥 fatal |
| `is_error` | *not mapped* | `false` | ignored |
| `nm` | *not mapped* (expected `nick_name`) | `""` | nickname silently lost |
| `flag` | *not mapped* | `0` | ignored |
| `cid` | `string?` | `"1111"` | ✅ |
| `ugid` | `string?` | `"5544454f-…"` | ✅ |
| `mobile` | `string?` | **never sent** | always null |

`System.Text.Json` will not read a JSON `true` into a `string`, and a single mismatch aborts
deserialisation of the **entire document**. `MStockJson.Options` sets
`JsonNumberHandling.AllowReadingFromString`, which is why quoted *numbers* were already
tolerated — but that setting is one-directional and numeric only. It does nothing for a boolean
arriving where a string was declared, and there is no built-in equivalent that does.

So the first of the three flags threw, and eight fields of a successful login became one opaque
error.

### The documentation and the live API disagree

This is the finding that shaped the whole fix. The official docs show those flags **quoted**:

```json
"is_kyc": "true", "is_activate": "true", "is_password_reset": "true", "is_error": "false"
```

A live account sends them **bare**:

```json
"is_kyc": true, "is_activate": false, "is_password_reset": true, "is_error": false
```

Both are real. Whatever the reason — a build difference, an undocumented change — a connector
that trusts either one exclusively is broken against the other half of the time. **So the fix
could not be "correct the declared types"**; it had to be "accept both", which is what the
lenient converters below do. Both shapes are pinned by tests: `MStockLoginResponseTests` uses
the live body, `MStockDocumentedShapeTests` the documented one.

### The part that makes it a design fault, not a typo

**This connector reads exactly one field out of that payload: `ugid`.** `is_kyc`,
`is_activate`, `is_password_reset`, `is_error` and `flag` are never branched on anywhere in the
codebase. A field with no consumer took down the login path.

That is the actual bug. Correcting three types fixes today's payload; it does not stop the next
build of a vendor API from retyping a fourth field nobody reads.

---

## The fix

### 1. Lenient scalar converters — `MStockJsonConverters.cs`

Three `JsonConverter`s that accept whatever scalar shape a value arrives in:

| Converter | Accepts | Yields |
|---|---|---|
| `LenientStringConverter` | string, number, `true`/`false`, null | the text; a number keeps the vendor's own formatting, so `1560.50` does **not** become `1560.5` |
| `LenientBoolConverter` | `true`/`false`, `"true"`/`"yes"`/`"y"`/`"1"`, `1`/`0`, null, `""` | `bool?` — anything uninterpretable is `null`, never an exception |
| `LenientIntConverter` | number, numeric string, boolean, null | `int?` |

They are applied **per property**, not globally, so each one documents "this vendor is
inconsistent about *this* field" rather than blanket-disabling type checking across the app.

**The leniency stops at scalars, deliberately.** An object or array where a string belongs is
a change in what the field *means*, not merely how it is typed, and still throws. Silently
swallowing that would hide a real contract break.

### 2. `MStockLoginData` rewritten against the observed payload

Correct types, both nickname spellings (`nm` and `nick_name`, exposed as one `DisplayName`),
and the previously unmapped `is_error` / `flag` mapped purely so they cannot break the parse.
Every field is optional.

### 3. Failures now name the field — `HttpConnectorClient`

`JsonException.Path` was being discarded. It is the entire diagnosis:

```
ex.Path    = $.data.is_kyc
ex.Message = The JSON value could not be converted to System.String.
             Path: $.data.is_kyc | LineNumber: 0 | BytePositionInLine: 87.
```

The error a caller sees is now:

> The broker's response could not be understood: **`$.data.is_kyc`** was not the expected type.

and the same path is logged. This applies to **every connector**, not just mStock — a vendor
silently retyping one field is the most common way these calls break, and the difference
between having that path and not is an afternoon of eyeballing a truncated payload.

---

## The four the User page then exposed

Reading the docs to confirm fault #1 turned up four more. Each is the same underlying error:
**a Kite-shaped assumption that mStock does not share.**

### 2. `checksum` is not a checksum — *this one blocked every login*

The connector computed the Kite recipe:

```csharp
SHA256(api_key + request_token + api_secret)   // 64 hex chars
```

mStock documents the field as:

| Field | Type | Description |
|---|---|---|
| `checksum` | string | A validation string to ensure the integrity of the request (Example: **L**) |

and its own mapping table spells it out: **`checksum` → `source (L)`**. It is a one-character
*source identifier*, not a hash of anything. We were sending a 64-character digest where the
broker expects `L`, so `/session/token` would have rejected **every OTP** — the step
immediately after the login that was already broken.

Now `MStockOptions.SessionSource`, defaulting to `"L"`, so a partner issued a different source
code changes configuration rather than waiting for a release.

> The name is genuinely misleading, and Kite really does use a SHA-256 checksum here. This was a
> reasonable guess. It was still wrong.

### 3. Fund summary is an array of flat rows

Expected (Kite):

```json
{"data": {"equity": {"net": 0, "available": {"cash": 0}}}}
```

Actual (mStock):

```json
{"data": [{"AVAILABLE_BALANCE":"299972678840.29","AMOUNT_UTILIZED":"27395824.71","SEG":"A", …}]}
```

An **array**, of **flat** rows, with **SCREAMING_SNAKE_CASE** keys and every monetary value a
**quoted string**. Deserialising an array into an object throws, so the fund summary could never
have worked. `MStockFundRow` now matches the documented shape — including the vendor's
misspelled `OPT_BUY_PRIMIUM_UTILIZE`, kept verbatim because "correcting" it would silently stop
it binding.

Two mapping decisions worth knowing:

- **`AVAILABLE_BALANCE`, not `CLEAR_BALANCE`, is "available to trade."** Clear balance excludes
  collateral and unsettled payins the account can already trade against; showing the smaller
  number would have a trader believe orders will bounce when they will not.
- **`UnrealisedPnl` is left null.** mStock reports `MTM_COMBINED`, a *combined* figure.
  Reporting it as unrealised would double-count realised profit. Null means "this broker does
  not report it", and the portfolio blender omits it rather than showing a wrong total.

### 4. Logout's `data` is a bare string

```json
{"status": "success", "data": "Success"}
```

That was being deserialised into `MStockIgnoredPayload`, an empty class — a JSON string into an
object, which throws. Every *successful* logout was reported as a malformed response. The route
now reads into a `JsonElement`, which accepts whatever the vendor puts there, which is the whole
point of a payload we have decided not to read.

### 5. `"scrip"` matches inside `"subscription"`

Testing the documented `APIKeyException` body found this:

> "API is suspended/expired for use. Please check your API **sub·scrip·tion** and try again."

The error mapper's free-text classifier looked for `"scrip"` (as in scrip code) with a plain
substring match. So **an expired API key was reported to the user as "instrument not found"** —
and nothing in that message would ever have led them to renew the key.

`ContainsAny` now requires a word boundary before the needle, so `scrip` still matches "scrips"
but not "subscription". `APIKeyException` is also now a recognised type mapping to
`ReauthRequired`, with a message that says what to actually do:

> Your mStock API key has expired or been suspended. Generate a new one in the mStock API portal.

This repository already knew this lesson — `BrokerLeakageRules` word-boundaries its own matches
precisely because "futu" otherwise matches "future". It was the same bug in a different file.

---

---

## The second sweep: Orders, Portfolio, Position

Reading the remaining pages found six more, all the same shape of mistake. The unifying finding
is worth stating plainly:

> **mStock is not internally consistent. The same logical field arrives with a different name, a
> different type, or a different vocabulary depending on which route answered.**

Three concrete demonstrations, each of which broke something:

**One field, two vocabularies.** An order placed with `product=MIS` comes back from the order
book as `"product": "INTRADAY"` — and the *same order* read through `/order/details` comes back
as `"CNC"`. Only the four glossary codes are valid on the way in; the aliases only ever appear
on the way out. `ToNativeProduct` must therefore keep emitting the strict set while
`ToCanonicalPositionEffect` accepts both.

**One field, three types.** `tag` is a string in the placement request, `[]` in the order book,
and `null` in `/order/details`. `modified` is `"false"` (string) in the order book and `0`
(number) in `/order/details`.

**One field, three date formats.** `order_timestamp` is `"30-09-2024 15:45:46"` in the order
book, `"2024-02-14 14:48:23"` in trade history, and `"23-01-2025 02:55:55 PM"` in
`/order/details`. Day-first 24-hour, ISO-ish, and day-first 12-hour with a meridiem.

### The trade book: two wrong shapes cancelling out

`/tradebook` and `/trades` both return "the day's fills" and share not one field name:

| `/trades` | `/tradebook` |
|---|---|
| `trade_id` | `TRADE_NUMBER` |
| `tradingsymbol` | `SYMBOL` (ticker) / `FULL_SYMBOL` (company name) |
| `average_price` | `PRICE` |
| `transaction_type`: `"SELL"` | `BUY_SELL`: `"Sell"` |

Both were read into the same snake_case DTO. That did not throw — unmapped members simply bind
to null — so the call **reported success** with a list of empty rows, mapping then failed on the
first one with "trade_id is missing", and **the `/trades` fallback that would have worked never
ran**, because nothing had reported a failure. Two wrong shapes cancelling into a
plausible-looking error is the worst kind of bug to chase, which is why the tests now pin the
null-binding behaviour explicitly.

### Holdings identify instruments by company name

```json
{"tradingsymbol": "BANK OF MAHARASHTRA", "exchange": null, "instrument_token": 11377, "isin": "INE457A01014"}
```

`tradingsymbol` holds the **company name**, not a ticker, and `exchange` is null — so neither
the script master nor the structural fallback can identify it, and every holding a user owns
failed to resolve. `instrument_token` is unambiguous and present on every row, so resolution now
tries the token first and falls back to symbol+exchange. (`isin` is also present and is the
obvious second fallback; the cache has no ISIN index yet.)

### An unstated product

Positions document `"product": ""` and holdings `"product": null`. `PositionEffect` is required
and non-nullable, so something has to be chosen. It falls back to **Delivery**, which is the
conservative reading rather than the neutral one: guessing "intraday" would tell the risk engine
and the UI that the exposure disappears at the square-off, and a trader who believes a position
will close itself and is wrong is in a far worse place than one who believes it will persist and
is wrong.

## The rule this establishes

> **A field the connector does not act on must never be able to fail a request.**

Concretely, when adding or reviewing a connector DTO:

1. **Ask what the code actually reads.** Everything else is diagnostic cargo. Map it (so it is
   visible in logs and debuggers) but type it leniently.
2. **Never trust a vendor's documented type for a flag.** `is_*` fields are the worst
   offenders: they show up as `true`, `"true"`, `"Y"`, and `1` across builds of the same API.
3. **Optional means optional.** `mobile` was documented and is simply absent here. A DTO field
   the vendor omits must read as null, and the UI must treat that as normal — the link wizard
   now shows no OTP destination rather than failing or inventing one.
4. **Prefer `bool?` over `bool`** for vendor flags. `null` = "the broker did not tell us",
   which is a real and different state from `false`.

And the one the other four faults add:

> **This API resembles Zerodha Kite. It is not Kite.**

`MStockErrorMapper` says so in its own header — "mStock's taxonomy is the Kite lineage" — and
that is true of the *error types*. It is not true of the session checksum, the fund-summary
shape, or the logout payload. When a shape here looks familiar from Kite, that is a reason to
check the mStock docs, not a reason to skip checking them.

---

## Known remaining gaps

- **`mobile` is never sent.** The documented login response has no such field, so the OTP screen
  cannot say which number the code went to. The wizard degrades gracefully (it omits the line),
  and if a build does start sending it, the field is already mapped.
- **Only the login leg has been seen live.** `/session/token` and `/session/verifytotp` now
  match the documentation and parse its sample bodies in tests, but no real OTP has been
  exchanged. That is the next thing to verify with a real account.
- **`ugid` is captured but not echoed** on the token call — the documented request takes only
  `api_key`, `request_token` and `checksum`. If the OTP step fails with an input error, adding
  it is the first thing to try.
- **`meta` and `silo` in the session response are unmapped.** Harmless (unmapped members are
  skipped), but `meta` is an *object*, so anyone tempted to map it as a string will get a throw
  — the lenient converters stop at scalars deliberately.
- **Market data, historical, option chain and basket routes are still unchecked.** Faults 6–11
  came from the Orders, Portfolio and Position pages. The remaining pages are the last sweep.
- **One bad row still fails a whole list.** `GetPositionsAsync` and `GetHoldingsAsync` return on
  the first mapping failure, so a single unrecognised instrument hides every other position.
  Left alone deliberately — silently dropping a position from a portfolio view would let a
  trader believe they are flat when they are not — but it means fault 6 or 11 blanked the entire
  screen rather than one row.

## Diagnosing the next one

1. Read the log line — it now names the JSON path (`$.data.<field>`).
2. Compare that field's JSON type against its declaration in `MStockDtos.cs`.
3. If the connector does not read the field: give it the matching lenient converter and move on.
4. If it does: fix the type, and add the real payload to the tests so it stays fixed.
5. **Check the docs for the surrounding route while you are there.** Every one of faults 2–5
   was found this way, in code nobody had run yet.

## Tests

| File | Covers |
|---|---|
| `MStockLoginResponseTests.cs` | 29 cases. The exact body a **live account** returned, plus the tolerance around it — every shape a flag can arrive in, that a genuine error envelope is still a failure, and that an object where a string belongs still throws. |
| `MStockDocumentedShapeTests.cs` | 14 cases. Every payload on the **documented** User page, verbatim: login (with its quoted flags), session token, TOTP, fund summary, logout, and all six documented failure envelopes. |

## Source

<https://tradingapi.mstock.com/docs/v1/typeA/User/> — retrieved 2026-09-03.

The host returns `AccessDenied` to `curl` and to `WebFetch`, including with a browser
user-agent; it loads in a real browser. If you need to re-read it, open it in one.
