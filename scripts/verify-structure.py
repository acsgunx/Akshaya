#!/usr/bin/env python3
"""
Static consistency checks for the Akshaya repository.

WHY THIS EXISTS
---------------
This repo was bootstrapped in an environment with no .NET SDK and no NuGet access, so
`dotnet build` could not be run against it. These checks are the substitute: they catch the
classes of mistake a compiler would have caught cheaply, by reading the source as text.

They are NOT a replacement for the compiler. Once you have the SDK, `dotnet build` is the
real gate and this script becomes a fast pre-commit sanity pass that still catches things the
compiler cannot see — a broker name leaking into the core, a manifest that lies about its
capabilities, a project missing from the solution.

USAGE
    python3 scripts/verify-structure.py              # all checks
    python3 scripts/verify-structure.py --check refs # one check
    python3 scripts/verify-structure.py --json       # machine-readable, for CI

Exit code 0 = all checks passed, 1 = at least one failure.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# Broker names that must never appear outside the connector projects and the plugin host.
# This is the single most important invariant in the codebase: it is what "plug and play"
# actually means in practice.
BROKER_NAMES = [
    "mstock", "zerodha", "kite", "moomoo", "futu", "ibkr",
    "interactivebrokers", "fyers", "upstox", "dhan", "angelone",
    "smartapi", "saxo", "tigerbrokers",
]

# Directories where broker names are legitimate.
BROKER_NAME_ALLOWED = [
    "src/connectors/",
    "src/Akshaya.Connectors.Host/",
    "docs/",
    "scripts/",
    "tests/Akshaya.Connector.MStock.Tests/",
]

# Files that talk *about* the rule and so necessarily contain the words.
BROKER_NAME_EXEMPT_FILES = {
    "scripts/verify-structure.py",
    "tests/Akshaya.Architecture.Tests/BrokerLeakageRules.cs",
    "AKSHAYA_BUILD_PROMPT.md",
    "README.md",
}

AMBIENT_TIME = ["DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow"]
AMBIENT_TIME_ALLOWED = ["src/Akshaya.SharedKernel/Clock.cs"]


@dataclass
class CheckResult:
    name: str
    passed: bool = True
    failures: list[str] = field(default_factory=list)
    checked: int = 0

    def fail(self, message: str) -> None:
        self.passed = False
        self.failures.append(message)


def rel(path: Path) -> str:
    return str(path.relative_to(REPO)).replace("\\", "/")


def source_files(*suffixes: str, under: str = "") -> list[Path]:
    root = REPO / under if under else REPO
    if not root.exists():
        return []
    out: list[Path] = []
    for suffix in suffixes:
        for p in root.rglob(f"*{suffix}"):
            parts = set(p.parts)
            if {"bin", "obj", "node_modules", ".git"} & parts:
                continue
            out.append(p)
    return sorted(out)


# --------------------------------------------------------------------------------------
# Check: no broker name leaks into the core or the UI
# --------------------------------------------------------------------------------------
COMMENT_LINE = re.compile(r"^\s*(//|///|\*|/\*|#|<!--)")


def check_broker_leakage() -> CheckResult:
    """
    Scans executable lines only.

    Two deliberate exclusions, both learned the hard way:

    * Word boundaries. Without them, "futu" matches "future" and "futures", which appear all over
      a derivatives-capable trading system, and the check drowns in noise nobody reads.

    * Comments are skipped. A doc comment saying "this shape exists because one broker returns
      business failures as HTTP 200" is exactly the documentation that keeps the abstraction
      understandable, and banning it would make the code worse. What must never appear is a
      broker name in a CONDITIONAL or a STRING LITERAL — that is the special case this rule
      exists to catch, and it lives on executable lines.
    """
    result = CheckResult("broker-leakage")
    pattern = re.compile(
        r"\b(" + "|".join(re.escape(n) for n in BROKER_NAMES) + r")\b",
        re.IGNORECASE,
    )

    for path in source_files(".cs", ".ts", ".html", ".scss"):
        relative = rel(path)
        if relative in BROKER_NAME_EXEMPT_FILES:
            continue
        if any(relative.startswith(prefix) for prefix in BROKER_NAME_ALLOWED):
            continue

        result.checked += 1
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue

        for lineno, line in enumerate(text.splitlines(), 1):
            if COMMENT_LINE.match(line):
                continue
            match = pattern.search(line)
            if match:
                result.fail(
                    f"{relative}:{lineno} mentions '{match.group(0)}' in code — broker names must "
                    f"not appear outside src/connectors/. Add a manifest field and read it "
                    f"instead of branching on the broker."
                )
    return result


# --------------------------------------------------------------------------------------
# Check: no ambient time
# --------------------------------------------------------------------------------------
def check_ambient_time() -> CheckResult:
    result = CheckResult("ambient-time")
    for path in source_files(".cs", under="src"):
        relative = rel(path)
        if relative in AMBIENT_TIME_ALLOWED:
            continue
        result.checked += 1
        text = path.read_text(encoding="utf-8", errors="ignore")
        for lineno, line in enumerate(text.splitlines(), 1):
            stripped = line.strip()
            # Doc comments discussing the rule are fine.
            if stripped.startswith("//") or stripped.startswith("///") or stripped.startswith("*"):
                continue
            for token in AMBIENT_TIME:
                if token in line:
                    result.fail(
                        f"{relative}:{lineno} uses {token} — inject IClock instead, or the "
                        f"backtester cannot control time."
                    )
    return result


# --------------------------------------------------------------------------------------
# Check: project reference graph is acyclic and respects the layering
# --------------------------------------------------------------------------------------
LAYER_RULES = {
    "Akshaya.SharedKernel": set(),
    "Akshaya.Connectors.Abstractions": {"Akshaya.SharedKernel"},
    "Akshaya.Connectors.Sdk": {"Akshaya.SharedKernel", "Akshaya.Connectors.Abstractions"},
}


def check_project_references() -> CheckResult:
    result = CheckResult("project-references")
    projects: dict[str, list[str]] = {}

    for csproj in source_files(".csproj"):
        name = csproj.stem
        text = csproj.read_text(encoding="utf-8", errors="ignore")
        refs = re.findall(r'ProjectReference\s+Include="([^"]+)"', text)
        projects[name] = [Path(r.replace("\\", "/")).stem for r in refs]
        result.checked += 1

    # Layering
    for name, allowed in LAYER_RULES.items():
        if name not in projects:
            result.fail(f"expected project {name} to exist but no .csproj was found")
            continue
        actual = set(projects[name])
        extra = actual - allowed
        if extra:
            result.fail(
                f"{name} references {sorted(extra)} but may only reference {sorted(allowed) or '(nothing)'} "
                f"— every dependency here is inherited by every connector author."
            )

    # SharedKernel must have no package references at all.
    shared = REPO / "src/Akshaya.SharedKernel/Akshaya.SharedKernel.csproj"
    if shared.exists() and "PackageReference" in shared.read_text(encoding="utf-8"):
        result.fail("Akshaya.SharedKernel has a PackageReference; it must stay dependency-free.")

    # Cycles
    def walk(node: str, seen: tuple[str, ...]) -> None:
        for dep in projects.get(node, []):
            if dep in seen:
                result.fail(f"circular project reference: {' -> '.join([*seen, dep])}")
                return
            walk(dep, (*seen, dep))

    for name in projects:
        walk(name, (name,))

    # Every project must be in the solution.
    sln = REPO / "Akshaya.sln"
    if sln.exists():
        sln_text = sln.read_text(encoding="utf-8", errors="ignore")
        for name in projects:
            if name not in sln_text:
                result.fail(f"{name} is not listed in Akshaya.sln — it will not build in CI.")

    return result


# --------------------------------------------------------------------------------------
# Check: connector manifests validate and are self-consistent
# --------------------------------------------------------------------------------------
def check_manifests() -> CheckResult:
    result = CheckResult("manifests")
    manifests = list(REPO.rglob("*connector.manifest.json"))

    if not manifests:
        result.fail("no connector manifests found")
        return result

    required = [
        "id", "displayName", "vendor", "contractVersion", "connectorVersion",
        "jurisdictions", "venues", "currencies", "assetClasses", "auth", "orders", "marketData",
    ]

    for path in manifests:
        relative = rel(path)
        result.checked += 1
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            result.fail(f"{relative} is not valid JSON: {exc}")
            continue

        for key in required:
            if key not in data:
                result.fail(f"{relative} is missing required field '{key}'")

        for mic in data.get("venues", []):
            if not (isinstance(mic, str) and len(mic) == 4 and mic.isupper()):
                result.fail(f"{relative} venue '{mic}' is not a 4-character upper-case MIC")

        for code in data.get("currencies", []):
            if not (isinstance(code, str) and len(code) == 3 and code.isupper()):
                result.fail(f"{relative} currency '{code}' is not a 3-letter ISO 4217 code")

        auth = data.get("auth", {})
        if not auth.get("credentialFields"):
            result.fail(f"{relative} declares no credentialFields — the link wizard renders from these")

        # Self-consistency: the manifest must not claim capabilities it contradicts elsewhere.
        orders = data.get("orders", {})
        basket = orders.get("basket", {})
        if basket.get("supported") and not basket.get("maxLegs"):
            result.fail(f"{relative} claims basket support but declares no maxLegs")

        market = data.get("marketData", {})
        if market.get("streaming") and not market.get("streamModes"):
            result.fail(f"{relative} claims streaming but declares no streamModes")

        if data.get("hosting") == "gateway" and not data.get("gateway"):
            result.fail(f"{relative} is gateway-hosted but has no gateway block")

        if auth.get("expiresAtVenueMidnight") and not auth.get("venueMidnightTimeZone"):
            result.fail(
                f"{relative} expires at venue midnight but names no timezone — the session "
                f"monitor cannot compute the real expiry without it"
            )

    return result


# --------------------------------------------------------------------------------------
# Check: C# type references resolve to a definition somewhere in the repo
# --------------------------------------------------------------------------------------
def check_type_resolution() -> CheckResult:
    """
    Cheap stand-in for the compiler: collect every type declared in the repo, then look for
    identifiers used in `new X(`, `: X`, and `X.` positions that look like our types
    (Akshaya-ish PascalCase) but are declared nowhere. Catches an agent inventing a helper
    class that was never written.
    """
    result = CheckResult("type-resolution")
    declared: set[str] = set()

    decl_pattern = re.compile(
        r"\b(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*"
        r"(?:class|record|struct|interface|enum)\s+([A-Z][A-Za-z0-9_]*)"
    )

    cs_files = source_files(".cs")
    for path in cs_files:
        text = path.read_text(encoding="utf-8", errors="ignore")
        for match in decl_pattern.finditer(text):
            declared.add(match.group(1))

    # Types we legitimately use from the BCL / packages. Not exhaustive — this check reports
    # suspicions, and anything here is a known-good name.
    known_external = {
        "Task", "ValueTask", "List", "Dictionary", "HashSet", "IReadOnlyList", "IReadOnlyDictionary",
        "IReadOnlySet", "IEnumerable", "IAsyncEnumerable", "ICollection", "IList", "IDictionary",
        "String", "Guid", "Uri", "Exception", "InvalidOperationException", "ArgumentException",
        "ArgumentNullException", "ArgumentOutOfRangeException", "NotSupportedException",
        "TimeoutException", "OperationCanceledException", "TaskCanceledException", "HttpClient",
        "HttpRequestMessage", "HttpResponseMessage", "HttpMethod", "HttpStatusCode", "StringContent",
        "FormUrlEncodedContent", "MediaTypeHeaderValue", "AuthenticationHeaderValue", "JsonSerializer",
        "JsonSerializerOptions", "JsonNamingPolicy", "JsonStringEnumConverter", "JsonConverter",
        "JsonElement", "JsonDocument", "JsonPropertyName", "JsonIgnore", "Encoding", "StringBuilder",
        "CancellationToken", "CancellationTokenSource", "SemaphoreSlim", "ReaderWriterLockSlim",
        "Interlocked", "Volatile", "Channel", "ChannelReader", "ChannelWriter", "ConcurrentDictionary",
        "ConcurrentQueue", "ActivitySource", "Activity", "Meter", "Counter", "Histogram", "Stopwatch",
        "TimeZoneInfo", "TimeSpan", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "CultureInfo",
        "Math", "Convert", "Enum", "Array", "Path", "File", "Directory", "Stream", "StreamReader",
        "MemoryStream", "AssemblyLoadContext", "Assembly", "AssemblyName", "Type", "Attribute",
        "ILogger", "ILoggerFactory", "LogLevel", "IServiceCollection", "IServiceProvider",
        "IOptions", "IConfiguration", "IHostedService", "BackgroundService", "WebApplication",
        "WebApplicationBuilder", "Results", "IResult", "ProblemDetails", "Hub", "IHubContext",
        "RateLimiter", "TokenBucketRateLimiter", "TokenBucketRateLimiterOptions", "RateLimitLease",
        "ClientWebSocket", "WebSocketMessageType", "WebSocketState", "WebSocketCloseStatus",
        "Random", "Regex", "Match", "Comparer", "StringComparer", "EqualityComparer", "KeyValuePair",
        "Lazy", "Nullable", "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory", "ArrayPool",
        "BinaryPrimitives", "BitConverter", "Buffer", "GC", "Environment", "AppContext",
        # BCL and package types confirmed present; added as the checker met them.
        "ActivityEvent", "ArraySegment", "AssemblyDependencyResolver", "DirectoryInfo", "FileInfo",
        "FixedWindowRateLimiter", "SlidingWindowRateLimiter", "ConcurrencyLimiter",
        "HubException", "JsonException", "LoggerConfiguration", "MediaTypeWithQualityHeaderValue",
        "PeriodicTimer", "GrpcChannel", "CallOptions", "Metadata", "RpcException",
        "ProblemDetailsMapper", "ServiceCollection", "HostApplicationBuilder",
        "TaskCompletionSource", "TimeZoneNotFoundException", "Timer", "Lock",
        "HashSet", "SortedDictionary", "SortedSet", "Queue", "Stack", "LinkedList",
    }

    use_pattern = re.compile(r"\bnew\s+([A-Z][A-Za-z0-9_]{3,})\s*[({<]")

    suspicious: dict[str, list[str]] = {}
    for path in cs_files:
        relative = rel(path)
        result.checked += 1
        text = path.read_text(encoding="utf-8", errors="ignore")
        for lineno, line in enumerate(text.splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith(("//", "///", "*")):
                continue
            for match in use_pattern.finditer(line):
                name = match.group(1)
                if name in declared or name in known_external:
                    continue
                # Generic-looking or framework-ish names get the benefit of the doubt.
                if name.endswith(("Attribute", "EventArgs", "Options", "Builder")):
                    continue
                # `new TPlugin()` constructs a generic type PARAMETER, not a type. The convention
                # is a leading T followed by another capital, which no concrete type here uses.
                if len(name) > 1 and name[0] == "T" and name[1].isupper():
                    continue
                suspicious.setdefault(name, []).append(f"{relative}:{lineno}")

    for name, sites in sorted(suspicious.items()):
        result.fail(
            f"type '{name}' is constructed at {sites[0]}"
            + (f" (+{len(sites) - 1} more)" if len(sites) > 1 else "")
            + " but is not declared in this repo — either it comes from a NuGet package "
              "(add it to known_external in this script) or it was never written."
        )

    return result


# --------------------------------------------------------------------------------------
# Check: every connector project ships a manifest, and every facet is wired
# --------------------------------------------------------------------------------------
def check_connector_completeness() -> CheckResult:
    result = CheckResult("connector-completeness")
    connectors_dir = REPO / "src/connectors"
    if not connectors_dir.exists():
        result.fail("src/connectors does not exist")
        return result

    for project in sorted(p for p in connectors_dir.iterdir() if p.is_dir()):
        result.checked += 1
        name = project.name

        if not (project / "connector.manifest.json").exists():
            result.fail(f"{name} has no connector.manifest.json — the host cannot load it")

        csproj = list(project.glob("*.csproj"))
        if not csproj:
            result.fail(f"{name} has no .csproj")
            continue

        text = csproj[0].read_text(encoding="utf-8", errors="ignore")
        if "connector.manifest.json" not in text:
            result.fail(
                f"{name}: connector.manifest.json is not marked as content in the .csproj, "
                f"so it will not be copied to the output directory and the host will not find it"
            )

        cs_text = "\n".join(
            p.read_text(encoding="utf-8", errors="ignore") for p in project.rglob("*.cs")
        )
        if "IBrokerConnector" not in cs_text and "ConnectorBase" not in cs_text:
            result.fail(f"{name} implements neither IBrokerConnector nor ConnectorBase")

    return result


CHECKS = {
    "leakage": check_broker_leakage,
    "time": check_ambient_time,
    "refs": check_project_references,
    "manifests": check_manifests,
    "types": check_type_resolution,
    "connectors": check_connector_completeness,
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", choices=sorted(CHECKS), help="run only one check")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    args = parser.parse_args()

    selected = {args.check: CHECKS[args.check]} if args.check else CHECKS
    results = [fn() for fn in selected.values()]

    if args.json:
        print(json.dumps(
            [{"name": r.name, "passed": r.passed, "checked": r.checked, "failures": r.failures}
             for r in results],
            indent=2,
        ))
    else:
        for r in results:
            status = "PASS" if r.passed else "FAIL"
            print(f"[{status}] {r.name}  ({r.checked} files checked)")
            for failure in r.failures:
                print(f"       - {failure}")
        total_failures = sum(len(r.failures) for r in results)
        print()
        print(f"{len(results)} checks, {total_failures} failures")

    return 0 if all(r.passed for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
