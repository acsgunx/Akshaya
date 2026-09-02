# Scripts

The scripts that were used while building this repository, kept because they are the same ones
you need to work on it.

| Script | What it does | When you run it |
|---|---|---|
| `bootstrap.sh` | Checks the toolchain, restores NuGet packages, installs web dependencies, and reports exactly which package versions failed to resolve | First thing, on a fresh clone |
| `dev-up.sh` / `dev-down.sh` | Brings the local infrastructure up and down (Postgres+TimescaleDB, Redis, OpenTelemetry collector, Seq) | Before running the API |
| `rerun.sh` / `rerun.ps1` / `rerun.py` | Kills any running app, clean-builds, and runs it again — API (`:5080`) and Angular web (`:4200`). Three equivalent implementations; pick the one for your shell. `--api-only` / `--web-only`, `--detached`, `--no-clean`, `--reinstall`, `--relaxed`, `--kill` | After a change, to restart from a clean build |
| `verify-structure.py` | Static consistency checks that stand in for a compiler: broker-name leakage, ambient time, manifest validity, project-reference layering, undeclared types, connector completeness | Pre-commit, and in CI |
| `validate-manifests.py` | Validates every `connector.manifest.json` against the JSON schema | After editing a manifest |
| `check-web.py` | Structural checks on the Angular sources: imports resolve, template and style paths exist, braces balance | After editing the frontend |
| `new-connector.sh` | Scaffolds a new connector project from the reference implementation | Adding a broker |

## Why a Python checker instead of just the compiler

This repository was written in an environment with no .NET SDK and no package-registry access, so
`dotnet build` could not be run against it. `verify-structure.py` was written to catch the classes
of mistake a compiler would have caught cheaply, by reading source as text.

Once you have the SDK, the compiler is the real gate — but keep the checker. It catches things the
compiler cannot see:

- a broker name leaking into the core, usually first in a comment or a log message
- a manifest that claims a capability the connector does not implement
- a project missing from the solution, so CI silently never builds it
- `DateTime.UtcNow` in a risk rule, which makes backtests quietly wrong

Those are the failures that do not announce themselves.

## Usage

```bash
# everything
python3 scripts/verify-structure.py

# one check
python3 scripts/verify-structure.py --check leakage
python3 scripts/verify-structure.py --check manifests

# machine-readable, for CI
python3 scripts/verify-structure.py --json
```

Exit code 0 means all checks passed.
