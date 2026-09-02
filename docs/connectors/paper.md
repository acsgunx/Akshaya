# Connector: Paper

- **Id:** `paper`
- **Market:** any — it declares broad capability on purpose
- **Hosting:** in-process
- **Status:** implemented, never run

The simulated broker. It implements the same `IBrokerConnector` as every real connector, which is
the point: **backtest → paper → live must require zero strategy code changes.** If a strategy needs
to know it is running on Paper, the abstraction has failed.

## What it is for

1. **Development without credentials.** It is registered by default, so `dotnet run` gives you a
   working order path with no broker account.
2. **Paper trading**, driven by a live tick feed.
3. **Backtest execution**, driven by replayed ticks — the same matching engine, a different
   `IMarketDataSource`.

## The matching engine

`MatchingEngine.cs` is where the real work is.

- An in-memory book per instrument, fed by an injected `IMarketDataSource`.
- **Market** orders fill at the touch with configurable slippage.
- **Limit** orders fill when a tick crosses the limit.
- **Stop / StopLimit** arm on trigger, then behave as Market / Limit.
- **Partial fills** are capped per tick, so a large order does not unrealistically fill on one
  print — a backtest that fills 50,000 shares at the touch is a backtest that lies.
- Positions carry weighted-average cost and realised/unrealised P&L, including the awkward case of
  a position crossing through flat: buy 100 then sell 150 closes the 100 long and opens 50 short,
  and realised P&L is computed on the closed 100 only.

**Determinism is a requirement, not a nicety.** Given the same tick sequence and seed, the engine
must produce identical fills. A backtest you cannot reproduce is a backtest you cannot debug, and
a strategy that behaves differently on re-run cannot be trusted. This is why the engine takes an
injected clock and seed, and avoids unordered iteration.

## Charge schedules

`Charges/` holds `IndiaChargeSchedule`, `SingaporeChargeSchedule` and `UsChargeSchedule`. A
backtest without transaction costs is fiction — in Indian intraday equity the charges are
routinely a meaningful fraction of a scalping strategy's gross edge, and a strategy that looks
profitable gross is frequently unprofitable net.

Each schedule returns an itemised `ChargesEstimate` so the UI can show a trader where the money
went:

| Market | Lines |
|---|---|
| India | Brokerage, STT/CTT, exchange transaction charges, SEBI turnover fee, stamp duty, GST, DP charges on delivery sells |
| Singapore | Brokerage with minimum, SGX clearing fee, trading fee, settlement, GST |
| US | Commission, SEC fee (sells only), FINRA TAF, per-contract option fees |

> ⚠️ **Every rate constant is marked `REVIEW:` and dated.** They were written from knowledge, not
> from a live published schedule, and rates change. Verify them against the current official
> schedules before anyone trusts a net-P&L figure. This is the single most likely place for this
> connector to be quietly wrong.

One cost that is easy to forget and often the largest: **FX conversion spread** when trading a
currency you do not hold. Cross-border strategies must account for it.

## Deliberate fakery, documented

- `PaperAuth` always returns `Completed` and the session never expires. This is the one place in
  the codebase where expiry is fake, and it is called out in the source so nobody copies the
  pattern into a real connector.
- The manifest claims broad capability — multi-currency, fractional quantities, streaming, bracket
  orders — so Paper can stand in for any broker in the order ticket.

## What it does not simulate

Worth knowing before trusting a paper result:

- Queue position. A limit order at the touch fills when price trades there; in reality you are
  behind everyone who was already resting.
- Market impact. Your order does not move the book.
- Broker-side risk rejections, margin calls, and the exchange's own throttles.
- Circuit breakers, halts and auction phases.

Paper trading proves your logic and your plumbing. It does not prove your edge.
