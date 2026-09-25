# Akshaya — working notes for Claude

.NET 10 trading-system monorepo. `src/` = services, `apps/web` = Angular UI (an Nx
workspace — see below), `tests/` = xUnit projects, `libs/` shared. Local build has warnings-as-errors
off (see `Directory.Build.props`); CI turns it on.

## Git workflow

Every change goes through a pull request — never commit or push directly to
`main`, no matter how small the diff. Branch off `main`, commit, push the
branch, open the PR with `gh`, and report the URL.

## Testing — keep it cheap

`dotnet test` on this repo is expensive in context (restore + build chatter,
~750 analyzer warnings, per-test lines, stack traces). Follow these rules:

- **Do not write new test cases unless the user explicitly asks.** When adding
  production code, don't reflexively add a matching test file.
- **Do not run the test suite unless the user explicitly asks**, or unless you
  changed test code / test-covered logic *and* the user asked you to verify.
- When you do run tests, **use `scripts/t.sh`**, never a bare `dotnet test`.
  It emits only the summary and failures.
  - Narrowest scope always: `scripts/t.sh tests/Akshaya.Trading.Tests` or
    `scripts/t.sh SomeTestName` (name filter). Whole-solution runs are a last
    resort.
  - If you already ran `dotnet build Akshaya.sln`, add `--no-build`:
    `scripts/t.sh --no-build tests/Akshaya.Trading.Tests`.
- Don't paste full test output back to the user — report the summary line and
  the specific failures only.
- CI (`.github/workflows/ci.yml`) runs the full suite on every push, so local
  full runs are rarely needed for correctness gating.

A `PreToolUse` hook in `.claude/settings.json` enforces this: any assistant Bash
call containing `dotnet test` is blocked with a pointer to `scripts/t.sh`. It
matches the literal string `dotnet test`, so a command that only *mentions* it
(an `echo`, a `grep`) is blocked too — reword or run it in a plain terminal.
Running `dotnet test` yourself outside the assistant is unaffected.

## Running the app

`scripts/rerun.sh` (or `.ps1` / `.py`) — clean-build + run API (:5080) and
web (:4200). Flags: `-ApiOnly`, `-WebOnly`, `-Detached`, `-NoClean`, `-Relaxed`.

**No database server is needed.** Identity (accounts, the encrypted saved-credential
vault, and broker links with their sealed sessions) is the only persisted store;
everything else is in-memory. `Persistence:Mode`
selects `Sqlite` (default — a file under `src/Akshaya.Api/App_Data/`), `InMemory`, or
`Postgres`. The API creates or migrates its own schema on startup in every mode, so
there is no `dotnet ef database update` step; `scripts/dev-up.sh` is only for the
Postgres mode. On an empty store the API seeds one account and logs its generated
password once at `Warning`.

## Deploying

`deploy/Dockerfile` builds the Angular app into the API's `wwwroot` — one container,
one origin, no CORS, no database. `deploy/<target>/` holds ready-made configs for
Fly.io, Azure App Service, Azure Container Apps, Render and Railway; `deploy/README.md`
compares them. Wiring lives in `src/Akshaya.Api/Infrastructure/Persistence/`.

**Azure App Service** is the documented step-by-step target and deploys from
`.github/workflows/deploy-azure.yml` — a *code* deploy on App Service's built-in .NET 10 stack, so
it ignores `deploy/Dockerfile` and needs no container registry. Auth is OIDC against a user-assigned
managed identity; there is no secret in the repo. `deploy/azure-app-service/setup.sh` provisions
everything from Cloud Shell. One setting there is load-bearing:
`Persistence__SqlitePath=/home/data/akshaya-identity.db`. A zip deploy replaces `/home/site/wwwroot`
wholesale, and the app's default path is relative to it — leave it on the default and every deploy
deletes every account. The workflow refuses to run if that setting is wrong. The Bicep container
route in `deploy/azure-app-service/main.bicep` still works and is documented alongside it.

**MonsterASP.NET is the exception** — Windows/IIS, no container. It deploys from
`.github/workflows/deploy-monsterasp.yml` (win-x86 publish + Angular bundle, synced
over Web Deploy). Two things there are load-bearing and must not be "tidied up":
`target-delete: false` and `skip-directory-paths: App_Data`, without which a plain
msdeploy sync deletes the SQLite database and every account in it. See
`deploy/monsterasp/README.md`.

## Web app structure

`apps/web` is an Nx workspace rooted there, not at the repo root, so CI, the Dockerfile and
both deploy workflows still just run `npm ci` / `npm run build` and read
`dist/akshaya-web`. The app (`src/`) is a thin shell; code lives in libraries under
`apps/web/libs/<domain>/<type>-<name>`, imported only via `@akshaya/<domain>/<name>`.
`apps/web/README.md` has the map and how to add a library.

`npm run lint` enforces the graph (`@nx/enforce-module-boundaries`): features never import
features, deep and relative cross-library imports fail, and `lightweight-charts` is banned
outside `market/ui-price-chart`. Fix a violation by moving code to the right library, not
by loosening `depConstraints`.

`"sideEffects": false` in `apps/web/package.json` is load-bearing: it lets barrels
tree-shake. Without it the shell's `@akshaya/shared/data-access` import puts SignalR in the
initial bundle and the production build fails its budget.

`@nx/angular` is not installed: its optional peers pull Angular 21 tooling that conflicts
with Angular 22. Nx runs the `@angular/build` builders from `project.json` directly.

For final verification of web-library edits, run `npm run build -- --skip-nx-cache` from
`apps/web`, plus `npm run lint`. A stale cached build was observed after holdings-library
edits; bypassing the cache ensures the current Angular templates and TypeScript are compiled.
Also run `python3 scripts/verify-structure.py` from a clean checkout: its broker-leakage check
covers UI copy and URLs, not just conditional logic. Nested local worktrees are scanned too,
so use a separate clean worktree when they produce unrelated failures; do not weaken the check.

The holdings calculator's illustrative delivery defaults were checked against
https://zerodha.com/charges/ and https://zerodha.com/brokerage-calculator/#tab-equities
(including https://zerodha.com/static/js/brokerage.js) on 24 Sep 2026. Keep this source
attribution in project notes, not hard-coded broker branding or URLs in the generic UI.
The defaults are user-editable estimates, not tariffs inferred from a connector.

## Chart workspace

`libs/market/feature-chart` owns broker history, symbol search, replay and the
watchlist sidebar. `libs/market/ui-price-chart` owns chart rendering, study calculations
and drawing primitives. Keep this dependency behind the lazy chart route.

Indicators are DATA, not code paths. `chart-indicators.ts` holds one `IndicatorDefinition`
per study — label, category, parameter metadata, defaults and a `build` that turns bars
into plots — and the picker dialog, the per-study settings sheet, the legend, the panes and
the saved preferences all read that list. Adding an indicator means appending one object
there and nothing else; do not add a `StudyId` union, a switch arm or a menu entry. Its
numeric parts belong in `indicator-math.ts`, whose warm-up contract (same length as the
input, `NaN` where there is no value yet, never shifted) is what lets the legend read a
value at the cursor by index and a tick update the last slot in place.

Studies are INSTANCES with ids, parameters and colours, so two SMAs at different lengths
are two records of the same `kind`. `normalizeParams` clamps every parameter to the range
its definition declares, on the way in and on the way out of the settings sheet — periods
arrive from `localStorage` and from a number field.

Indicators are computed on the REAL bars even when the price is drawn as Heikin Ashi, and
the O/H/L/C readout stays on the real bars too: an averaged open is not a price anything
traded at. Volume-based studies hold their last historical value on a forming bar, because
`ChartBar.volume` is 0 for a bar built from ticks (a tick's quantity is session-cumulative
on several connectors). A plot's forward `offset` is clamped at the last bar — Ichimoku's
cloud is not projected, for the same reason `BUCKET_SECONDS` omits the daily frames: the
chart does not invent session times.

Lightweight Charts does not parse CSS `color(srgb ...)` returned by `color-mix()`.
Resolve theme tokens to sRGB `rgb()`/`rgba()` before passing them to the library;
`PriceChartComponent.token()` handles this using a cached canvas conversion.

Every screen that lists an instrument links to the chart through `ChartLinkComponent`
(`@akshaya/shared/ui`), which picks the row's first history-capable account and never a
link the row doesn't name. The chart draws the user's positions, holdings and working
orders for that instrument as price lines, read from the root `DashboardStore` and
`OrdersStore`.

Chart preferences and drawings are device-local, not server-persisted. Replay only
uses loaded historical bars; volume is historical because live tick volume may be
session-cumulative rather than per-bar. Range shortcuts zoom within loaded history
and must not bypass the connector's declared history retention or timeframes.
