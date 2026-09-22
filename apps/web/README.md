# Akshaya — web frontend

Angular frontend for the Akshaya multi-broker trading platform. Standalone
components only, zoneless change detection, signals for all state, NgRx
SignalStore for feature state, typed reactive forms, Angular Material.

## The one rule that matters

**There is no broker-specific code in this application.** The order ticket
(`libs/orders/feature-order-ticket`) and the broker-link wizard
(`libs/account/feature-broker-link`) are each a single component that renders
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

An [Nx](https://nx.dev) workspace: one thin application, and the code in libraries under
`libs/`, grouped by domain. `npm run graph` draws the dependency graph.

```
src/                        the application — bootstrap, routes, providers, and the shell
  app/shell/                top bar chrome: kill switch, appearance menu, activity bar,
                            auth guard, boot splash
  styles/                   design tokens, Material theme, global CSS, DESIGN.md
libs/
  shared/
    models/                 wire-format mirrors of the backend contracts
    util/                   pure helpers: labels, money/quantity/instrument pipes, layout
    data-access/            API client, auth, broker links, connector manifests,
                            SignalR market data, venue calendar, kill switch, interceptors
    ui/                     presentational pieces: empty/loading states, tabs, banners,
                            confirm dialog, connection status, chart link
  portfolio/                data-access (the blended snapshot), feature-dashboard,
                            feature-positions, feature-holdings
  orders/                   data-access (orders, fills), feature-orders, feature-fills,
                            feature-order-ticket
  market/                   data-access (watchlist), feature-watchlist, feature-chart,
                            ui-price-chart (the only code that touches lightweight-charts)
  account/                  feature-account, feature-connectors, feature-broker-link
```

Every library is imported through its alias — `@akshaya/<domain>/<name>`, declared in
`tsconfig.json` — and exports only what other projects use, from `src/index.ts`.

### The rules, and what enforces them

`npm run lint` fails on any import that breaks these (`@nx/enforce-module-boundaries`,
configured in `eslint.config.js`):

- **Type:** `app > feature > ui > data-access > util`, each depending only on what is to
  its right. A feature never imports another feature — every one is a lazy route and loads
  on its own.
- **Domain:** a domain sees itself and `shared`. `market` may also read `portfolio` and
  `orders` data-access, because the chart marks your positions and working orders.
- **No deep imports** into another library's `src/lib`, and no static import of a library
  a route loads lazily.
- **`lightweight-charts`** is importable only from `market/ui-price-chart`, so the ~50 kB
  engine stays behind the chart route.

`package.json` declares `"sideEffects": false`. That is what lets the build drop the parts
of a library barrel a chunk does not use; without it the shell's import of
`@akshaya/shared/data-access` pulls SignalR into the first download. Keep library code
free of import-time side effects (no top-level calls, no bare `import './x'`).

### Adding a library

1. Create `libs/<domain>/<name>/src/lib/` and `src/index.ts` exporting its public API.
2. Add `libs/<domain>/<name>/project.json` with a name and its two tags — copy a sibling's.
3. Add the alias to `paths` in `tsconfig.json`.

A new feature route then imports the library lazily from `src/app/app.routes.ts`.

## Wire format notes

`@akshaya/shared/models` mirrors the C# contracts field-for-field, including the JSON
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
`libs/shared/models` — that is the only place a wire-format drift should ever need
a fix.

## Backend endpoints assumed

At the time this frontend was built, `Akshaya.Api` had contracts
(`Akshaya.Api.Contracts`) but no wired-up minimal-API endpoints yet.
`libs/shared/data-access/src/lib/api.service.ts` assumes REST routes that follow those DTOs directly
(`GET/POST /api/connectors`, `POST /api/links`, `POST /api/orders`, etc.) and
a SignalR hub at `/hubs/market-data` pushing `tick` and `orderUpdate`
messages. Adjust the string literals in `api.service.ts` and
`market-data.service.ts`, not their method signatures or call sites, if the
backend lands on different paths.
