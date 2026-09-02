# ADR 0002 — Venues, currencies and quantities are open types, not enums

- **Status:** Accepted
- **Date:** 2026-09-02
- **Supersedes:** the first draft of the domain model

## Context

The first version of this design had:

```csharp
public enum Exchange { NSE, BSE, NFO, BFO, MCX, CDS }
public enum ProductType { Delivery, Intraday, Margin, NormalCarryForward }
```

and prices as bare `decimal`, with INR assumed throughout.

That is a perfectly good model of the Indian market, and it makes the platform Indian forever. The
requirement is Singapore and global brokers — Moomoo, IBKR, Saxo, Tiger — alongside the Indian
ones.

## Decision

| Concept | Before | Now | Why |
|---|---|---|---|
| Exchange | `enum Exchange` | `record struct Venue(string Mic)` | ISO 10383. Adding SGX is reference data, not a recompile |
| Money | `decimal` | `record struct Money(decimal, Currency)` | Adding SGD to INR throws instead of producing a wrong number |
| Quantity | `int` | `record struct Quantity(decimal)` | Fractional shares are real; `int` truncates them silently |
| Product | one enum | `[Flags] PositionEffect` | India splits CNC/MIS/MTF/NRML; the US has cash/margin plus a short flag. Flags compose |
| Instrument | symbol string | `InstrumentKey(Venue, Symbol, AssetClass, Expiry?, Strike?, Right?)` + ISIN/FIGI | Cross-listings must aggregate correctly |

`OrderType`, `TimeInForce` and `OrderStatus` stay closed enums: those vocabularies genuinely are
finite, and a connector maps its broker's spelling onto them.

## Why this specific split

The test is whether the set is closed *in the world*, not whether it is closed *today for us*.
There will always be another exchange and another currency; there will not be a seventh
fundamentally different way to express "fill this now at any price".

`Money` is the load-bearing one. A cross-border portfolio makes currency mistakes silent: the
number renders, the dashboard looks fine, and the total is wrong. Making it a type means the
mistake is a compile error or a runtime throw at the point of the bug rather than a reporting
discrepancy someone notices three weeks later.

## Consequences

- Reference data becomes load-bearing: an unknown MIC means the calendar cannot answer whether the
  venue is open, and the risk gate treats unknown as closed. Refusing to trade is the right
  default when reference data is missing.
- `Money` arithmetic throws across currencies. Callers must convert explicitly with a rate they
  chose, which is the intended friction — `Money.ConvertTo` deliberately takes the rate as a
  parameter rather than looking up an ambient one, because historic P&L converted at today's rate
  is a bug.
- Two architecture tests defend this: no bare `decimal` named `*Price`/`*Amount` on contract
  types, and no `DateTime.UtcNow` outside `Clock.cs`.
