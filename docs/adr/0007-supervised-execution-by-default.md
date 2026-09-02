# ADR 0007 — Strategy execution is supervised by default

- **Status:** Accepted
- **Date:** 2026-09-02

## Context

The product includes a strategy engine. The obvious build is: strategy fires, order goes to the
broker. That is also how a bug in an indicator becomes forty wrong orders before anyone notices,
and it is how a platform ends up in a regulatory conversation it did not plan for.

Automated retail order flow is regulated differently in every market on the target list. India's
framework for retail algorithmic trading involves broker approval, exchange registration, unique
algo identifiers and order-rate thresholds. Singapore and the US differ again. The rules also
change.

## Decision

**A strategy emits a `Signal`, not an order.** By default the signal becomes a notification with a
one-click pre-filled order ticket. A human confirms.

Automatic execution is possible but gated by all of:

1. an explicit per-strategy arming action, behind 2FA;
2. a mandatory daily loss cap set at arming time;
3. the per-tenant kill switch, checked on every order;
4. a per-connector compliance flag — `compliance.algoApprovalRequired` in the manifest — that an
   operator must clear deliberately for that broker and jurisdiction;
5. an `AlgoId` carried in the order contract and written to the audit log for every automated
   order.

## Why

The engineering reason: a strategy engine is the component most likely to be wrong in a way that
is expensive and fast. Backtests overfit, indicators have off-by-one bugs at bar boundaries, and
live data has gaps that historical data does not. A human in the loop is not friction, it is the
circuit breaker — and the ones who genuinely want full automation are precisely the users who will
read the arming dialog.

The compliance reason: putting approval in the manifest rather than in a wiki means the platform
cannot be configured into a violation by someone who did not know the rule existed. The flag
travels with the connector.

The honest reason to write it down: it is much easier to ship supervised-first and relax it later
than to ship auto-first and retrofit the guardrails after an incident.

## Consequences

- The notification path is on the critical path for the strategy feature, not an afterthought. A
  signal the user does not see in time is a missed trade, so latency there matters.
- `AlgoId` is in `PlaceOrderRequest` from the start, so no connector has to invent its own field
  for it later.
- `docs/compliance/<jurisdiction>.md` must be kept current, and it is a document to be reviewed by
  someone qualified — not by the engineering team alone.
- Nothing the platform produces is investment advice, and the UI carries a disclaimer surface.
