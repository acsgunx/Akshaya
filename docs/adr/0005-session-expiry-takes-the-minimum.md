# ADR 0005 — Session expiry always takes the minimum of every constraint

- **Status:** Accepted
- **Date:** 2026-09-02

## Context

mStock publishes a twelve-hour access-token lifetime **and** invalidates every token at midnight
India time. Most Indian brokers behave the same way. Several global brokers instead drop sessions
after a period of inactivity and require a keepalive ping — IBKR's `/tickle` is the example.

A session monitor that trusts "issued at + published lifetime" gets a token minted at 15:00 IST
wrong in the most damaging possible way: it believes the session is good until 03:00, schedules
the re-auth prompt for then, and the first the trader hears about the dead token is a rejected
order at the next market open.

## Decision

`AuthSpec` in the manifest declares:

- `sessionLifetime` — the nominal rolling lifetime,
- `expiresAtVenueMidnight` plus `venueMidnightTimeZone` — the hard cutoff, if there is one,
- `keepAliveInterval` — non-null for brokers that drop idle sessions,
- `refreshSupported`.

`SessionMonitor` computes the effective expiry as the **minimum** of every applicable constraint,
and the host runs the keepalive on the declared interval. The UI shows a session-expiry countdown
and warns before expiry, rather than after.

The manifest schema requires `venueMidnightTimeZone` whenever `expiresAtVenueMidnight` is true,
because the pair is useless apart.

## Why the minimum, always

The two failure modes are not symmetric.

Expiring **early** costs one unnecessary login. It is mildly annoying and completely safe.

Expiring **late** means the platform believes it can trade when it cannot. Orders fail at the
worst moment — at the open, in a moving market — and the user finds out from a rejection rather
than from a prompt. In a system where the cost of being wrong is measured in money, the asymmetry
decides the default.

## Consequences

- Every connector must compute expiry honestly rather than echoing the broker's headline number.
  `MStockAuth.ComputeExpiry` is the worked example and carries the reasoning in a comment.
- Tests use `ManualClock`: a token minted at 15:00 IST must expire at midnight, not at 03:00.
- The UI needs a real place to show a session countdown and a re-auth prompt. This is not an edge
  case to hide — a trader must always know whether their broker link is live.
- Brokers with a keepalive impose a background cost per credential; the host owns it so no
  connector reimplements it.
