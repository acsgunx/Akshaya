#!/usr/bin/env bash
#
# First-run setup. Checks the toolchain, restores packages, and — importantly — reports
# precisely which package versions failed to resolve.
#
# That last part matters more than usual here: this repository was written without access to a
# package feed, so the versions in the .csproj and package.json files were chosen from knowledge
# rather than from a live registry. Some of them will not exist. This script surfaces which ones
# instead of leaving you to read a wall of restore errors.
#
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[0;33m'; BOLD=$'\033[1m'; NC=$'\033[0m'
FAILURES=0

section() { printf '\n%s==> %s%s\n' "$BOLD" "$1" "$NC"; }
ok()      { printf '  %s✓%s %s\n' "$GREEN" "$NC" "$1"; }
warn()    { printf '  %s!%s %s\n' "$YELLOW" "$NC" "$1"; }
fail()    { printf '  %s✗%s %s\n' "$RED" "$NC" "$1"; FAILURES=$((FAILURES + 1)); }

# ---------------------------------------------------------------------------------------------
section "Toolchain"

if command -v dotnet >/dev/null 2>&1; then
  SDK_VERSION="$(dotnet --version 2>/dev/null || echo unknown)"
  SDK_MAJOR="${SDK_VERSION%%.*}"
  if [[ "$SDK_MAJOR" =~ ^[0-9]+$ ]] && (( SDK_MAJOR >= 10 )); then
    ok ".NET SDK $SDK_VERSION"
  else
    fail ".NET SDK $SDK_VERSION found, but this solution targets net10.0."
    printf '      Install from https://dotnet.microsoft.com/download, or lower TargetFramework\n'
    printf '      in Directory.Build.props if you deliberately want an older runtime.\n'
  fi
else
  fail ".NET SDK not found. Install the .NET 10 SDK."
fi

if command -v node >/dev/null 2>&1; then
  NODE_MAJOR="$(node -v | sed 's/^v//' | cut -d. -f1)"
  if (( NODE_MAJOR >= 20 )); then
    ok "Node $(node -v)"
  else
    fail "Node $(node -v) is too old; Angular needs 20+."
  fi
else
  fail "Node not found."
fi

command -v docker >/dev/null 2>&1 \
  && ok "Docker $(docker --version | cut -d' ' -f3 | tr -d ,)" \
  || warn "Docker not found — scripts/dev-up.sh will not work, but the API runs without it if you point it at your own Postgres and Redis."

command -v python3 >/dev/null 2>&1 \
  && ok "Python $(python3 -V | cut -d' ' -f2)" \
  || warn "Python 3 not found — the verification scripts will not run."

# ---------------------------------------------------------------------------------------------
section "NuGet restore"

if command -v dotnet >/dev/null 2>&1; then
  RESTORE_LOG="$(mktemp)"
  if dotnet restore Akshaya.sln >"$RESTORE_LOG" 2>&1; then
    ok "All packages restored"
  else
    fail "Restore failed. Packages that could not be resolved:"
    # NU1101 = package not found; NU1102 = version not found.
    grep -oE "(NU1101|NU1102)[^']*'[^']+'[^.]*\." "$RESTORE_LOG" | sort -u | sed 's/^/      /' \
      || sed 's/^/      /' "$RESTORE_LOG" | tail -20
    printf '\n      Fix: find the real latest version with\n'
    printf '        dotnet package search <PackageId> --exact-match\n'
    printf '      then update the PackageReference. See docs/STATUS.md.\n'
  fi
  rm -f "$RESTORE_LOG"
else
  warn "Skipped (no dotnet)"
fi

# ---------------------------------------------------------------------------------------------
section "Web dependencies"

if [[ -d apps/web ]] && command -v npm >/dev/null 2>&1; then
  if (cd apps/web && npm install --no-audit --no-fund >/dev/null 2>&1); then
    ok "apps/web dependencies installed"
  else
    fail "npm install failed in apps/web — run it directly to see which package is at fault."
  fi
else
  warn "Skipped (no apps/web or no npm)"
fi

# ---------------------------------------------------------------------------------------------
section "Static verification"

if command -v python3 >/dev/null 2>&1; then
  python3 scripts/verify-structure.py && ok "Structure checks passed" || fail "Structure checks failed (see above)"
else
  warn "Skipped (no python3)"
fi

# ---------------------------------------------------------------------------------------------
printf '\n'
if (( FAILURES == 0 )); then
  printf '%s✓ Ready.%s Next:\n' "$GREEN" "$NC"
  printf '    scripts/dev-up.sh\n'
  printf '    dotnet run --project src/Akshaya.Api\n'
  printf '    cd apps/web && npm start\n'
else
  printf '%s✗ %d problem(s).%s Fix them before building — docs/STATUS.md explains what is expected\n' \
    "$RED" "$FAILURES" "$NC"
  printf '  to fail on a first run and in what order to work through it.\n'
fi

exit $(( FAILURES > 0 ? 1 : 0 ))
