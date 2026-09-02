# ADR 0004 — `Result<T>` at the connector boundary, not exceptions

- **Status:** Accepted
- **Date:** 2026-09-02

## Context

Broker calls fail constantly and predictably: expired sessions, closed markets, insufficient
funds, rate limits, risk rejections at the broker's end, instruments the broker cannot trade.
These are normal Tuesday outcomes, not exceptional conditions.

## Decision

Every method on every connector facet returns `Result<T>` (or `Task<Result<T>>`, or
`IAsyncEnumerable<T>` for streams). Failure carries:

- a **canonical** `ConnectorErrorCode` from a closed set, which the retry policy, the HTTP status
  mapper and the UI all switch on, and
- the **raw vendor code and message**, preserved verbatim.

Exceptions are reserved for programmer error — an illegal order state transition, a null argument.
Those should crash.

## Why both halves of the error matter

The canonical code is what makes the platform broker-agnostic. `SessionExpired` maps to HTTP 401
and a re-auth prompt regardless of whether the broker said `TOKEN_INVALID`, `Session Expired` or
`errorCode: 3001`.

The vendor text is what makes support possible. When a trader asks why their order was rejected,
"connector.order_rejected" is useless and the exchange's own message — "RMS: margin shortfall for
NRML" — is the answer. Discarding it to keep the abstraction tidy trades the user's problem for
the architect's aesthetics.

`NotSupported` is deliberately in the set as a first-class outcome, so a connector can decline a
capability cleanly rather than throwing.

`ConnectorErrorCodes.Retryable` is a small explicit set — rate limited, timeout, broker
unavailable, gateway unavailable. Nothing else is retried automatically, because retrying an
`InsufficientFunds` only wastes the user's time, and retrying a placement creates a duplicate
order.

## Consequences

- Call sites are more verbose. `Result` has `Map`, `Bind` and `Match` to keep chains readable.
- The API needs exactly one place that turns a canonical code into an HTTP status, and it has one:
  `ProblemDetailsMapper`.
- Connectors must not throw for expected failures. An architecture test asserts every facet method
  is `Result`-shaped, which stops the pattern from eroding one convenient `throw` at a time.
- Adding a code to the canonical set is a contract change and needs an ADR, because the UI, the
  retry policy and the risk engine all depend on the set being closed.
