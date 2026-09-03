# mStock login response — observed shape, and the parsing rule it forced

**Status:** resolved. The fault below was reproduced from a live account, fixed, and pinned by
`tests/Akshaya.Connector.MStock.Tests/MStockLoginResponseTests.cs`.

This document exists because the bug was not really "three fields had the wrong type". It was
"any field having an unexpected type fails the whole login, including fields nothing reads" —
and that is a mistake worth never making twice, in this connector or the next one.

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

`MStockLoginData` was written from the published Type A documentation, which this repo has
never been able to verify (the docs host returns `AccessDenied` to any automated client, and
still did while this was being fixed). It declared:

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

---

## Known remaining gaps

- **`mobile` is never sent**, so the OTP screen cannot say which number the code went to. The
  wizard degrades gracefully (it omits the line), but if a build does start sending it, the
  field is already mapped and it will appear with no code change.
- **The OTP leg is still unverified against a real account.** The login leg is now confirmed
  against live output; `/openapi/typea/session/token` and `/session/verifytotp` are not. Their
  DTOs (`MStockSessionData`) were written from the same unverifiable documentation and are the
  next most likely place this exact class of fault is hiding. `MStockSessionData` currently
  declares everything as `string?`/`IReadOnlyList<string>?` — safer than the login DTO was, but
  a bare `true` on any of them would fail identically.
- **`ugid` is captured but not yet echoed** on the token call. Some builds reportedly require
  it; if the OTP step fails with an input error, that is the first thing to try.

## Diagnosing the next one

1. Read the log line — it now names the JSON path (`$.data.<field>`).
2. Compare that field's JSON type against its declaration in `MStockDtos.cs`.
3. If the connector does not read the field: give it the matching lenient converter and move on.
4. If it does: fix the type, and add the real payload to `MStockLoginResponseTests` so it stays
   fixed.

## Tests

`tests/Akshaya.Connector.MStock.Tests/MStockLoginResponseTests.cs` — 29 cases. The first parses
the exact body above, verbatim; the rest pin the *tolerance* rather than the three specific
fields, including that a genuine error envelope is still recognised as a failure and that an
object where a string belongs still throws.
