# ADR 0001 — Modular monolith, .NET 10 and Angular

- **Status:** Accepted
- **Date:** 2026-09-02

## Context

Akshaya has to support many brokers across several markets, with real-time market data, order
execution, and eventually a strategy engine. The obvious temptation at this size is microservices,
because the domain decomposes so neatly: market data, orders, portfolio, strategy.

## Decision

A **modular monolith** with Clean Architecture per module, deployed as one process. Modules
communicate through integration events behind `IEventBus`, never by referencing each other's
`Domain` namespace, and an architecture test enforces that.

Stack: .NET 10 (LTS) with minimal APIs, EF Core on PostgreSQL 17 with TimescaleDB for time
series, Redis for cache and the SignalR backplane, Angular (latest stable) with standalone
components and signals.

## Why

The decomposition is neat but the coupling is not. An order placement touches the connector,
the risk gate, the portfolio snapshot and the audit log **synchronously**, in a path where added
latency is a real cost to the user. Splitting that across network hops buys operational
complexity and pays for it in the one place a trading system cannot afford it.

Meanwhile the actual scaling pressure is in exactly one place: tick fan-out. That is already
isolated behind `IEventBus` and the SignalR backplane, so it can be extracted first when it needs
to be, without dragging the order path with it.

TimescaleDB rather than a separate time-series database for the same reason: it is a Postgres
extension, so candles live in the same instance as everything else. One database to back up, one
set of credentials, one transaction boundary when a strategy needs to read both.

No MediatR: plain handler classes registered by convention keep the call graph greppable and the
dependency list shorter. No AutoMapper: mapping broker payloads is exactly where a silent
mis-mapping costs money, so it is written out by hand where it can be read and tested.

## Consequences

- One deployable, one debugger session, one transaction scope. Onboarding is cheaper.
- The module boundaries must be defended by tests, because nothing physical enforces them.
- Extracting a service later is a real piece of work, and we are betting it will only be needed
  for market data.
- If the strategy engine grows CPU-heavy backtests, it will want its own process sooner than the
  rest.
