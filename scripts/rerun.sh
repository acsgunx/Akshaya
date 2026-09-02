#!/usr/bin/env bash
#
# Kill any running Akshaya app, clean-build it, and run it again.
#
# Works on macOS, Linux, and Windows via Git Bash / WSL. For native Windows
# PowerShell use scripts/rerun.ps1; both mirror scripts/rerun.py.
#
#   scripts/rerun.sh                # clean-build + run API (:5080) and web (:4200), foreground
#   scripts/rerun.sh --api-only     # backend only
#   scripts/rerun.sh --web-only     # frontend only
#   scripts/rerun.sh --detached     # start in the background, log to .run/, exit
#   scripts/rerun.sh --no-clean     # skip the clean step
#   scripts/rerun.sh --reinstall    # also wipe bin/obj and run npm ci
#   scripts/rerun.sh --relaxed      # build with TreatWarningsAsErrors/NuGetAudit off
#   scripts/rerun.sh --kill         # only kill what is running, then stop
#
# Ctrl+C in foreground mode stops everything.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_PROJECT="src/Akshaya.Api"
API_PORT=5080
API_URL="http://localhost:${API_PORT}"
WEB_DIR="apps/web"
WEB_PORT=4200
RUN_DIR="${REPO_ROOT}/.run"

SCOPE_API=1
SCOPE_WEB=1
DETACHED=0
NO_CLEAN=0
REINSTALL=0
KILL_ONLY=0
RELAXED_PROPS=()

for arg in "$@"; do
  case "$arg" in
    --api-only)  SCOPE_WEB=0 ;;
    --web-only)  SCOPE_API=0 ;;
    -d|--detached) DETACHED=1 ;;
    --no-clean)  NO_CLEAN=1 ;;
    --reinstall) REINSTALL=1 ;;
    --relaxed)   RELAXED_PROPS=(-p:TreatWarningsAsErrors=false -p:NuGetAudit=false) ;;
    --kill)      KILL_ONLY=1 ;;
    -h|--help)   sed -n '2,21p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m warn\033[0m %s\n' "$*"; }
die()  { printf '\033[31mfatal\033[0m %s\n' "$*" >&2; exit 1; }

pids_on_port() {
  local port="$1"
  if command -v lsof >/dev/null 2>&1; then
    lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true
  elif command -v fuser >/dev/null 2>&1; then
    fuser "${port}/tcp" 2>/dev/null | tr -s ' ' '\n' | grep -E '^[0-9]+$' || true
  fi
}

kill_recorded() {
  [ -d "$RUN_DIR" ] || return 0
  local f pid
  for f in "$RUN_DIR"/*.pid; do
    [ -e "$f" ] || continue
    pid="$(cat "$f" 2>/dev/null || true)"
    [ -n "${pid:-}" ] && kill -9 "$pid" 2>/dev/null || true
    rm -f "$f"
  done
}

kill_everything() {
  say "Stopping anything already running"
  kill_recorded
  local ports=() port pid
  [ "$SCOPE_API" = 1 ] && ports+=("$API_PORT")
  [ "$SCOPE_WEB" = 1 ] && ports+=("$WEB_PORT")
  for port in "${ports[@]}"; do
    for pid in $(pids_on_port "$port"); do
      warn "port ${port} held by pid ${pid} — killing"
      kill -9 "$pid" 2>/dev/null || true
    done
  done
  [ "$SCOPE_API" = 1 ] && pkill -f "Akshaya.Api" 2>/dev/null || true
  if [ "$SCOPE_WEB" = 1 ]; then
    pkill -f "ng serve" 2>/dev/null || true
    pkill -f "angular/cli" 2>/dev/null || true
  fi
  # let the sockets drain
  local tries=0
  while [ "$tries" -lt 20 ]; do
    local held=0
    for port in "${ports[@]}"; do
      [ -n "$(pids_on_port "$port")" ] && held=1
    done
    [ "$held" = 0 ] && break
    sleep 0.25; tries=$((tries + 1))
  done
}

clean_step() {
  say "Cleaning"
  if [ "$SCOPE_API" = 1 ]; then
    dotnet clean "${REPO_ROOT}/${API_PROJECT}" -v quiet --nologo || true
    if [ "$REINSTALL" = 1 ]; then
      find "${REPO_ROOT}/src" "${REPO_ROOT}/tests" -type d \( -name bin -o -name obj \) \
        -prune -exec rm -rf {} + 2>/dev/null || true
    fi
  fi
  if [ "$SCOPE_WEB" = 1 ]; then
    rm -rf "${REPO_ROOT}/${WEB_DIR}/.angular" "${REPO_ROOT}/${WEB_DIR}/dist"
  fi
}

build_step() {
  if [ "$SCOPE_API" = 1 ]; then
    say "dotnet build ${API_PROJECT}"
    dotnet build "${REPO_ROOT}/${API_PROJECT}" -c Debug --nologo \
      ${RELAXED_PROPS[@]+"${RELAXED_PROPS[@]}"}
  fi
  if [ "$SCOPE_WEB" = 1 ]; then
    if [ "$REINSTALL" = 1 ] || [ ! -d "${REPO_ROOT}/${WEB_DIR}/node_modules" ]; then
      if [ -f "${REPO_ROOT}/${WEB_DIR}/package-lock.json" ]; then
        ( cd "${REPO_ROOT}/${WEB_DIR}" && npm ci )
      else
        ( cd "${REPO_ROOT}/${WEB_DIR}" && npm install )
      fi
    else
      say "web deps present — skipping npm ci (use --reinstall to force)"
    fi
  fi
}

wait_for_port() {
  local port="$1" tries=0
  while [ "$tries" -lt 120 ]; do
    if (exec 3<>"/dev/tcp/127.0.0.1/${port}") 2>/dev/null; then exec 3>&- 3<&-; return 0; fi
    sleep 0.5; tries=$((tries + 1))
  done
  return 1
}

API_CMD=(dotnet run --project "${REPO_ROOT}/${API_PROJECT}" -c Debug --no-build
         ${RELAXED_PROPS[@]+"${RELAXED_PROPS[@]}"})
web_cmd() { ( cd "${REPO_ROOT}/${WEB_DIR}" && npm start -- --port "${WEB_PORT}" ); }

run_foreground() {
  local pids=()
  cleanup() {
    say "Shutting down"
    for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
    wait 2>/dev/null || true
    exit 0
  }
  trap cleanup INT TERM

  if [ "$SCOPE_API" = 1 ]; then
    say "Starting API on ${API_URL}"
    ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}" \
      ASPNETCORE_URLS="${API_URL}" "${API_CMD[@]}" &
    pids+=($!)
    if [ "$SCOPE_WEB" = 1 ]; then
      say "Waiting for the API to accept connections"
      wait_for_port "$API_PORT" || warn "API did not open its port in time — starting web anyway"
    fi
  fi

  if [ "$SCOPE_WEB" = 1 ]; then
    say "Starting web on http://localhost:${WEB_PORT}"
    web_cmd &
    pids+=($!)
  fi

  wait -n 2>/dev/null || wait
  cleanup
}

run_detached() {
  mkdir -p "$RUN_DIR"
  if [ "$SCOPE_API" = 1 ]; then
    ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}" \
      ASPNETCORE_URLS="${API_URL}" nohup "${API_CMD[@]}" \
      >"${RUN_DIR}/api.log" 2>&1 &
    echo $! > "${RUN_DIR}/api.pid"
    say "API  ${API_URL}  (pid $(cat "${RUN_DIR}/api.pid"))  logs: .run/api.log"
  fi
  if [ "$SCOPE_WEB" = 1 ]; then
    ( cd "${REPO_ROOT}/${WEB_DIR}" && nohup npm start -- --port "${WEB_PORT}" \
      >"${RUN_DIR}/web.log" 2>&1 & echo $! > "${RUN_DIR}/web.pid" )
    say "web  http://localhost:${WEB_PORT}  (pid $(cat "${RUN_DIR}/web.pid"))  logs: .run/web.log"
  fi
  echo
  say "Tail:  tail -f .run/*.log"
  say "Stop:  scripts/rerun.sh --kill"
}

# ── go ──────────────────────────────────────────────────────────────────────────
[ "$SCOPE_API" = 1 ] && ! command -v dotnet >/dev/null 2>&1 && die "dotnet not found on PATH"
[ "$SCOPE_WEB" = 1 ] && ! command -v npm    >/dev/null 2>&1 && die "npm not found on PATH"

kill_everything
if [ "$KILL_ONLY" = 1 ]; then say "Done (kill only)"; exit 0; fi

[ "$NO_CLEAN" = 0 ] && clean_step
build_step

if [ "$DETACHED" = 1 ]; then run_detached; else run_foreground; fi
