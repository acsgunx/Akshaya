# India (SEBI) — compliance notes

**Status: unverified starting point. Not legal advice.**
Written from general knowledge in September 2026 and **not** checked against current circulars.
Everything below must be confirmed with the broker and with a qualified adviser before automated
execution is enabled for any Indian connector.

## What we believe applies

India has a specific regulatory framework for algorithmic order flow originating from retail
investors, distinct from ordinary manual trading. In broad terms it involves:

- **Broker involvement.** Automated strategies are expected to be routed through, and approved by,
  the broker rather than run independently against a trading API.
- **Exchange registration** of the strategy, with an identifier attached to the resulting orders.
- **Order-rate thresholds**, above which additional obligations apply.
- **Audit and traceability** of automated orders.

The specifics — thresholds, the registration mechanics, which parties carry which obligation —
change, and differ by broker. **Do not implement against this paragraph.** Get the current
requirement from the broker in writing.

## What the platform already does about it

| Requirement shape | Platform control |
|---|---|
| Automation must be deliberately enabled | `compliance.algoApprovalRequired: true` in the mStock manifest; live automation stays behind a per-tenant flag an operator clears explicitly |
| Orders must be identifiable as automated | `PlaceOrderRequest.AlgoId`, carried in the contract so no connector invents its own field |
| Order rates must be bounded | `rateLimits` in the manifest, enforced per credential by the SDK's token bucket, and separately by the risk gate |
| Actions must be auditable | Append-only hash-chained audit log with actor, tenant, connector and correlation id |
| A human must be able to stop everything | Per-tenant kill switch, checked by the risk gate on every order |

The default posture is supervised execution: a strategy produces a signal and a one-click order
ticket, not an order. See ADR 0007 for why that is the default rather than a setting.

## Charges (separate from the algo rules)

`IndiaChargeSchedule` models brokerage, STT/CTT, exchange transaction charges, SEBI turnover fee,
stamp duty, GST, and DP charges on delivery sells. The rate constants are marked `REVIEW:` and
dated. They must be checked against current published schedules — a backtest's net P&L is only as
honest as these numbers, and in intraday equity they are large enough to flip a strategy from
profitable to not.

## Open items

- [ ] Confirm mStock's own process and requirements for API-based automated order flow.
- [ ] Confirm the current retail-algo thresholds and obligations from the source circulars.
- [ ] Confirm which field an algo identifier must occupy in the mStock order payload.
- [ ] Have a qualified adviser review this document and sign it off with a date.
- [ ] Verify every charge rate against the current published schedule.

Until these are closed, automated live execution stays off for Indian connectors.
