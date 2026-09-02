# Compliance notes

**These are engineering notes, not legal advice.** They record what the platform does and what
must be confirmed before a feature is enabled. Every rule referenced here must be verified against
the current regulations and with your broker before anything is switched on in production. Rules
change, and a note written in 2026 is a starting point for a conversation with someone qualified,
not a substitute for one.

## What the platform enforces regardless of jurisdiction

| Control | Where |
|---|---|
| Strategies emit signals, not orders, unless explicitly armed | `Modules/Strategy` (planned), ADR 0007 |
| Arming requires 2FA and a mandatory daily loss cap | Identity + risk policy |
| Per-tenant kill switch, checked on every order | `Modules/Trading/Domain/Rules/KillSwitchRule.cs` |
| `AlgoId` carried on every order and written to the audit log | `PlaceOrderRequest.AlgoId` |
| Append-only, hash-chained audit of every order-affecting action | `Modules/Audit` |
| Per-connector automation gate | `ConnectorManifest.Compliance.AlgoApprovalRequired` |
| Nothing is presented as investment advice | UI disclaimer surface |

The per-connector gate is the important one architecturally: the approval requirement travels
**with the connector**, in its manifest, so the platform cannot be configured into a violation by
someone who did not know the rule existed for that market.

## Per-jurisdiction

- [India (SEBI)](india.md)
- Singapore (MAS) — to be written before the first Singapore connector ships
- United States — to be written before the first US connector ships

## Before enabling automated execution for any broker

1. Confirm with the broker, in writing, what their approval process for automated order flow is.
2. Confirm whether the strategy itself needs registration, and with whom.
3. Confirm whether orders must carry a specific identifier, and in which field.
4. Confirm the order-rate limits that apply, and encode them in the connector's manifest.
5. Record the outcome, with dates and names, in that jurisdiction's file.
6. Only then clear `algoApprovalRequired` for that connector and tenant.

If any of steps 1–4 cannot be answered, the answer to step 6 is no.
