#!/usr/bin/env bash
# Compact `dotnet test` wrapper.
#
# Prints only the pass/fail summary and failure detail, so a test run costs a
# few lines of context instead of the hundreds `dotnet test` normally emits
# (per-project restore/build chatter, analyzer warnings, per-test lines).
#
#   scripts/t.sh                              # whole solution
#   scripts/t.sh tests/Akshaya.Trading.Tests # one project (fastest, cheapest)
#   scripts/t.sh OrderStateMachine           # name filter -> --filter FullyQualifiedName~OrderStateMachine
#
# Assumes the solution already builds. If you changed non-test code, run
#   dotnet build Akshaya.sln
# yourself first, then call this with --no-build for a near-zero-cost re-run:
#   scripts/t.sh --no-build tests/Akshaya.Trading.Tests
set -uo pipefail
cd "$(dirname "$0")/.."

extra=()
target="Akshaya.sln"
for arg in "$@"; do
  case "$arg" in
    --no-build|--no-restore) extra+=("$arg") ;;
    */*|*.csproj)            target="$arg" ;;
    -*)                      extra+=("$arg") ;;
    *)                       extra+=(--filter "FullyQualifiedName~$arg") ;;
  esac
done

dotnet test "$target" \
  --nologo -v q --no-restore \
  --logger "console;verbosity=minimal" \
  "${extra[@]}" 2>&1 \
| grep -E '^(Passed!|Failed!|Test Run|  +(Failed|Passed|Skipped)|\[xUnit)|error [A-Z]|\[FAIL\]|Assert\.|Expected:|Actual:|^\s+at Akshaya' \
| head -n 150
rc=${PIPESTATUS[0]}

echo "--- dotnet test exit=$rc ---"
exit "$rc"
