# Akshaya — working notes for Claude

.NET 10 trading-system monorepo. `src/` = services, `apps/web` = Angular UI,
`tests/` = xUnit projects, `libs/` shared. Local build has warnings-as-errors
off (see `Directory.Build.props`); CI turns it on.

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

**No database server is needed.** Identity (accounts + the encrypted saved-credential
vault) is the only persisted store; everything else is in-memory. `Persistence:Mode`
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
