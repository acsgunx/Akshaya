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

## Running the app

`scripts/rerun.sh` (or `.ps1` / `.py`) — clean-build + run API (:5080) and
web (:4200). Flags: `-ApiOnly`, `-WebOnly`, `-Detached`, `-NoClean`, `-Relaxed`.
