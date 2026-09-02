#!/usr/bin/env python3
"""
Kill any running Akshaya app, clean-build it, and run it again.

Cross-platform (macOS / Linux / Windows). This is the reference implementation;
scripts/rerun.sh and scripts/rerun.ps1 are thin equivalents for people who would
rather not depend on Python.

    scripts/rerun.py                 # clean-build + run API (:5080) and web (:4200), foreground
    scripts/rerun.py --api-only      # backend only
    scripts/rerun.py --web-only      # frontend only
    scripts/rerun.py --detached      # start in the background, write .run/*.log, exit
    scripts/rerun.py --no-clean      # skip the clean step (kill + build + run)
    scripts/rerun.py --reinstall     # also wipe bin/obj and reinstall web deps (npm ci)
    scripts/rerun.py --relaxed       # build with TreatWarningsAsErrors/NuGetAudit off
    scripts/rerun.py --kill          # only kill what is running, then stop

Ctrl+C in foreground mode stops everything. `scripts/rerun.py --kill` (or
`--detached` then `--kill`) tears down a detached run using .run/*.pid.
"""

from __future__ import annotations

import argparse
import os
import platform
import shutil
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
API_PROJECT = "src/Akshaya.Api"
API_PORT = 5080
API_URL = f"http://localhost:{API_PORT}"
WEB_DIR = "apps/web"
WEB_PORT = 4200
RUN_DIR = REPO_ROOT / ".run"

IS_WINDOWS = platform.system() == "Windows"
NPM = "npm.cmd" if IS_WINDOWS else "npm"

# Filled in from --relaxed: turns the repo's warnings-as-errors / NuGet audit gate
# off so a local dev build survives advisory-only package warnings.
RELAXED_PROPS: list[str] = []


# ── output ──────────────────────────────────────────────────────────────────────
def say(msg: str) -> None:
    print(f"\033[36m==>\033[0m {msg}" if not IS_WINDOWS else f"==> {msg}", flush=True)


def warn(msg: str) -> None:
    print(f"\033[33m warn\033[0m {msg}" if not IS_WINDOWS else f" warn {msg}", flush=True)


def die(msg: str) -> None:
    print(f"\033[31mfatal\033[0m {msg}" if not IS_WINDOWS else f"fatal {msg}", flush=True)
    sys.exit(1)


# ── kill ────────────────────────────────────────────────────────────────────────
def pids_on_port(port: int) -> list[int]:
    try:
        if IS_WINDOWS:
            out = subprocess.run(
                ["netstat", "-ano", "-p", "tcp"],
                capture_output=True, text=True, check=False,
            ).stdout
            found = set()
            for line in out.splitlines():
                parts = line.split()
                if len(parts) >= 5 and (parts[1].endswith(f":{port}")) and parts[3] == "LISTENING":
                    found.add(int(parts[4]))
            return sorted(found)
        out = subprocess.run(
            ["lsof", "-ti", f"tcp:{port}", "-sTCP:LISTEN"],
            capture_output=True, text=True, check=False,
        ).stdout
        return [int(x) for x in out.split()]
    except FileNotFoundError:
        return []


def kill_pid(pid: int, hard: bool = False) -> None:
    try:
        if IS_WINDOWS:
            subprocess.run(
                ["taskkill", "/PID", str(pid), "/T"] + (["/F"] if hard else []),
                capture_output=True, check=False,
            )
        else:
            os.kill(pid, signal.SIGKILL if hard else signal.SIGTERM)
    except (ProcessLookupError, PermissionError):
        pass


def kill_by_cmdline(needle: str) -> None:
    """Best-effort kill of stray `dotnet run` / `ng serve` processes."""
    try:
        if IS_WINDOWS:
            ps = (
                "Get-CimInstance Win32_Process | "
                f"Where-Object {{ $_.CommandLine -like '*{needle}*' }} | "
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
            )
            subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                           capture_output=True, check=False)
        else:
            subprocess.run(["pkill", "-f", needle], capture_output=True, check=False)
    except FileNotFoundError:
        pass


def stop_recorded() -> None:
    if not RUN_DIR.exists():
        return
    for pidfile in RUN_DIR.glob("*.pid"):
        try:
            pid = int(pidfile.read_text().strip())
        except (ValueError, OSError):
            pidfile.unlink(missing_ok=True)
            continue
        kill_pid(pid, hard=True)
        pidfile.unlink(missing_ok=True)


def kill_everything(scope_api: bool, scope_web: bool) -> None:
    say("Stopping anything already running")
    stop_recorded()
    ports = ([API_PORT] if scope_api else []) + ([WEB_PORT] if scope_web else [])
    for port in ports:
        for pid in pids_on_port(port):
            warn(f"port {port} held by pid {pid} — killing")
            kill_pid(pid, hard=True)
    if scope_api:
        kill_by_cmdline("Akshaya.Api")
    if scope_web:
        kill_by_cmdline("ng serve")
        kill_by_cmdline("angular/cli")
    # give the OS a moment to release the sockets
    deadline = time.time() + 5
    while time.time() < deadline and any(pids_on_port(p) for p in ports):
        time.sleep(0.25)


# ── clean + build ───────────────────────────────────────────────────────────────
def run(cmd: list[str], cwd: Path, env: dict | None = None) -> None:
    printable = " ".join(cmd)
    say(printable)
    result = subprocess.run(cmd, cwd=str(cwd), env=env)
    if result.returncode != 0:
        die(f"`{printable}` exited {result.returncode}")


def clean(scope_api: bool, scope_web: bool, reinstall: bool) -> None:
    say("Cleaning")
    if scope_api:
        subprocess.run(["dotnet", "clean", API_PROJECT, "-v", "quiet", "--nologo"],
                       cwd=str(REPO_ROOT), check=False)
        if reinstall:
            for root in ("src", "tests"):
                for d in (REPO_ROOT / root).rglob("bin"):
                    shutil.rmtree(d, ignore_errors=True)
                for d in (REPO_ROOT / root).rglob("obj"):
                    shutil.rmtree(d, ignore_errors=True)
    if scope_web:
        for sub in (".angular", "dist"):
            shutil.rmtree(REPO_ROOT / WEB_DIR / sub, ignore_errors=True)


def build(scope_api: bool, scope_web: bool, reinstall: bool) -> None:
    if scope_api:
        run(["dotnet", "build", API_PROJECT, "-c", "Debug", "--nologo", *RELAXED_PROPS], REPO_ROOT)
    if scope_web:
        node_modules = REPO_ROOT / WEB_DIR / "node_modules"
        if reinstall or not node_modules.exists():
            lock = REPO_ROOT / WEB_DIR / "package-lock.json"
            run([NPM, "ci" if lock.exists() else "install"], REPO_ROOT / WEB_DIR)
        else:
            say("web deps present — skipping npm ci (use --reinstall to force)")


# ── run ─────────────────────────────────────────────────────────────────────────
def api_env() -> dict:
    env = os.environ.copy()
    env.setdefault("ASPNETCORE_ENVIRONMENT", "Development")
    env["ASPNETCORE_URLS"] = API_URL
    return env


def wait_for_port(port: int, timeout: float = 60) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.settimeout(1)
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.5)
    return False


def spawn(cmd: list[str], cwd: Path, env: dict, log: Path | None):
    kwargs: dict = {"cwd": str(cwd), "env": env}
    if log is not None:
        log.parent.mkdir(parents=True, exist_ok=True)
        handle = open(log, "w")
        kwargs["stdout"] = handle
        kwargs["stderr"] = subprocess.STDOUT
    if IS_WINDOWS:
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    return subprocess.Popen(cmd, **kwargs)


def api_cmd() -> list[str]:
    return ["dotnet", "run", "--project", API_PROJECT, "-c", "Debug", "--no-build", *RELAXED_PROPS]


def web_cmd() -> list[str]:
    return [NPM, "start", "--", "--port", str(WEB_PORT)]


def run_foreground(scope_api: bool, scope_web: bool) -> int:
    procs: list[tuple[str, subprocess.Popen]] = []

    def shutdown(*_):
        say("Shutting down")
        for _name, p in procs:
            if p.poll() is None:
                if IS_WINDOWS:
                    p.send_signal(signal.CTRL_BREAK_EVENT)
                else:
                    os.killpg(os.getpgid(p.pid), signal.SIGTERM)
        for _name, p in procs:
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill()
        sys.exit(0)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    if scope_api:
        say(f"Starting API on {API_URL}")
        procs.append(("api", spawn(api_cmd(), REPO_ROOT, api_env(), None)))
        if scope_web:
            say("Waiting for the API to accept connections")
            if not wait_for_port(API_PORT):
                warn("API did not open its port in time — starting web anyway")

    if scope_web:
        say(f"Starting web on http://localhost:{WEB_PORT}")
        procs.append(("web", spawn(web_cmd(), REPO_ROOT / WEB_DIR, os.environ.copy(), None)))

    # Exit when any child exits; propagate its code.
    while True:
        for name, p in procs:
            code = p.poll()
            if code is not None:
                warn(f"{name} exited with {code} — stopping the rest")
                shutdown()
                return code
        time.sleep(0.5)


def run_detached(scope_api: bool, scope_web: bool) -> int:
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    started = []
    if scope_api:
        log = RUN_DIR / "api.log"
        p = spawn(api_cmd(), REPO_ROOT, api_env(), log)
        (RUN_DIR / "api.pid").write_text(str(p.pid))
        started.append(("API", API_URL, log, p.pid))
    if scope_web:
        log = RUN_DIR / "web.log"
        p = spawn(web_cmd(), REPO_ROOT / WEB_DIR, os.environ.copy(), log)
        (RUN_DIR / "web.pid").write_text(str(p.pid))
        started.append(("web", f"http://localhost:{WEB_PORT}", log, p.pid))

    print()
    for name, url, log, pid in started:
        say(f"{name:3s}  {url}  (pid {pid})  logs: {log.relative_to(REPO_ROOT)}")
    print()
    say(f"Tail:  tail -f {RUN_DIR.relative_to(REPO_ROOT)}/*.log")
    say(f"Stop:  {Path(__file__).relative_to(REPO_ROOT)} --kill")
    return 0


# ── main ────────────────────────────────────────────────────────────────────────
def main() -> int:
    parser = argparse.ArgumentParser(
        description="Kill, clean-build, and re-run the Akshaya app.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("--api-only", action="store_true", help="backend API only")
    parser.add_argument("--web-only", action="store_true", help="Angular frontend only")
    parser.add_argument("--detached", "-d", action="store_true",
                        help="run in the background, log to .run/, then exit")
    parser.add_argument("--no-clean", action="store_true", help="skip the clean step")
    parser.add_argument("--reinstall", action="store_true",
                        help="also wipe bin/obj and run npm ci")
    parser.add_argument("--relaxed", action="store_true",
                        help="build with TreatWarningsAsErrors=false and NuGetAudit=false")
    parser.add_argument("--kill", action="store_true", help="only stop what is running")
    args = parser.parse_args()

    if args.api_only and args.web_only:
        die("--api-only and --web-only are mutually exclusive")

    if args.relaxed:
        RELAXED_PROPS.extend(["-p:TreatWarningsAsErrors=false", "-p:NuGetAudit=false"])

    scope_api = not args.web_only
    scope_web = not args.api_only

    if shutil.which("dotnet") is None and scope_api:
        die("dotnet not found on PATH")
    if shutil.which(NPM) is None and scope_web:
        die("npm not found on PATH")

    kill_everything(scope_api, scope_web)
    if args.kill:
        say("Done (kill only)")
        return 0

    if not args.no_clean:
        clean(scope_api, scope_web, args.reinstall)
    build(scope_api, scope_web, args.reinstall)

    if args.detached:
        return run_detached(scope_api, scope_web)
    return run_foreground(scope_api, scope_web)


if __name__ == "__main__":
    sys.exit(main())
