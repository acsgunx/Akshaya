# Akshaya — web frontend

Angular frontend for the Akshaya multi-broker trading platform. Standalone
components only, zoneless change detection, signals for all state, NgRx
SignalStore for feature state, typed reactive forms, Angular Material.

## The one rule that matters

**There is no broker-specific code in this application.** The order ticket
(`src/app/features/order-ticket`) and the broker-link wizard
(`src/app/features/broker-link`) are each a single component that renders
itself from the `ConnectorManifest` served by `GET /api/connectors`. If a
change to this app is about to add `if (connectorId === 'someBroker')`
anywhere, the correct fix is almost always a new field on
`ConnectorManifest` on the backend, read generically here — not a
conditional. See `src/styles/DESIGN.md` and the doc comments at the top of
both components for the reasoning.

## Requirements

- Node.js 20+
- npm 10+

## Install

```bash
npm install
```

## Run

The dev server proxies `/api` and `/hubs` (SignalR) to the backend — see
`proxy.conf.json`. Point it at your local Akshaya.Api instance:

```bash
npm start
# → http://localhost:4200, proxying to http://localhost:5080
```

Edit `proxy.conf.json` if your API runs on a different port.

## Build

```bash
npm run build            # production build → dist/akshaya-web
npm run watch             # development build, rebuilds on change
```

## Test / lint

```bash
npm test
npm run lint
```

## Project layout

```
src/
  styles/               design tokens, Angular Material theme, global CSS, DESIGN.md
  app/
    core/                cross-cutting singletons: API client, models (wire-format
                          mirrors of the backend contracts), pipes, SignalR wrapper,
                          venue calendar, connector/kill-switch SignalStores
    shared/               small reusable UI: connection-status, stale-banner,
                          kill-switch, venue-clock, confirm-dialog, empty-state
    features/
      order-ticket/       generic, manifest-driven order entry
      broker-link/         generic, manifest-driven broker login wizard
      connectors/          broker catalogue
      dashboard/           blended multi-currency portfolio
      positions/            virtualised, per-broker-leg breakdown
      orders/               virtualised blotter, broker's raw text alongside
                             the canonical status
      watchlist/             live LTP with flash-on-change
```

## Wire format notes

`core/models` mirrors the C# contracts field-for-field, including the JSON
converters in `Akshaya.Api.Contracts.JsonConverters`:

- `Money` → `{ amount: string; currency: string }` — amount is a **string**
  on the wire (avoids float rounding on prices/P&L), only ever parsed to a
  `number` at the point of display (`money.pipe.ts`).
- `Quantity` → a decimal **string**, same reasoning (fractional shares).
- `InstrumentKey` → its canonical string form, e.g. `XNSE:INFY:Equity` or
  `XNSE:NIFTY:OPT:2026-01-29:23000:Call`.
- Enums → camelCase strings (`JsonStringEnumConverter` with
  `JsonNamingPolicy.CamelCase`).

If the backend's DTOs change shape, update the matching file under
`core/models` — that is the only place a wire-format drift should ever need
a fix.

## Backend endpoints assumed

At the time this frontend was built, `Akshaya.Api` had contracts
(`Akshaya.Api.Contracts`) but no wired-up minimal-API endpoints yet.
`core/api.service.ts` assumes REST routes that follow those DTOs directly
(`GET/POST /api/connectors`, `POST /api/links`, `POST /api/orders`, etc.) and
a SignalR hub at `/hubs/market-data` pushing `tick` and `orderUpdate`
messages. Adjust the string literals in `api.service.ts` and
`market-data.service.ts`, not their method signatures or call sites, if the
backend lands on different paths.
