#!/usr/bin/env python3
"""
Validate every connector.manifest.json against the connector manifest JSON schema.

A manifest is not documentation — the order ticket, the link wizard, the risk gate and the
conformance suite all read it at runtime. A manifest that lies produces a broken order form for
a real trader, so this runs in CI and as a pre-commit check.

Uses the `jsonschema` package when it is available (full draft 2020-12 validation) and falls
back to a built-in structural check when it is not, so the script is useful on a machine with no
Python packages installed.

    pip install jsonschema        # for full validation
    python3 scripts/validate-manifests.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCHEMA_PATH = REPO / "src/Akshaya.Connectors.Abstractions/connector.manifest.schema.json"

GREEN, RED, YELLOW, BOLD, NC = "\033[0;32m", "\033[0;31m", "\033[0;33m", "\033[1m", "\033[0m"


def find_manifests() -> list[Path]:
    return sorted(
        p for p in REPO.rglob("*connector.manifest.json")
        if "node_modules" not in p.parts and "bin" not in p.parts and "obj" not in p.parts
    )


def validate_with_jsonschema(schema: dict, manifests: list[Path]) -> list[str]:
    import jsonschema  # type: ignore[import-not-found]

    validator_cls = jsonschema.validators.validator_for(schema)
    validator_cls.check_schema(schema)
    validator = validator_cls(schema)

    errors: list[str] = []
    for path in manifests:
        rel = path.relative_to(REPO)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            errors.append(f"{rel}: invalid JSON — {exc}")
            continue

        for error in sorted(validator.iter_errors(data), key=lambda e: list(e.path)):
            location = "/".join(str(p) for p in error.path) or "(root)"
            errors.append(f"{rel}: {location}: {error.message}")
    return errors


def validate_structurally(manifests: list[Path]) -> list[str]:
    """
    Fallback when jsonschema is not installed. Checks the invariants that actually break the
    product if violated, rather than trying to reimplement JSON Schema.
    """
    required = [
        "id", "displayName", "vendor", "contractVersion", "connectorVersion",
        "jurisdictions", "venues", "currencies", "assetClasses", "auth", "orders", "marketData",
    ]

    errors: list[str] = []
    seen_ids: dict[str, str] = {}

    for path in manifests:
        rel = str(path.relative_to(REPO))
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            errors.append(f"{rel}: invalid JSON — {exc}")
            continue

        for key in required:
            if key not in data:
                errors.append(f"{rel}: missing required field '{key}'")

        connector_id = data.get("id", "")
        if connector_id:
            if connector_id in seen_ids:
                errors.append(
                    f"{rel}: id '{connector_id}' is already used by {seen_ids[connector_id]} — "
                    f"ids are used as API route and cache-key segments and must be unique"
                )
            seen_ids[connector_id] = rel

        for mic in data.get("venues", []):
            if not (isinstance(mic, str) and len(mic) == 4 and mic.isupper()):
                errors.append(f"{rel}: venue '{mic}' is not a 4-character upper-case MIC")

        for code in data.get("currencies", []):
            if not (isinstance(code, str) and len(code) == 3 and code.isupper()):
                errors.append(f"{rel}: currency '{code}' is not a 3-letter ISO 4217 code")

        auth = data.get("auth", {})
        if not auth.get("credentialFields"):
            errors.append(f"{rel}: auth.credentialFields is empty — the link wizard renders from it")
        if auth.get("expiresAtVenueMidnight") and not auth.get("venueMidnightTimeZone"):
            errors.append(
                f"{rel}: expiresAtVenueMidnight is true but venueMidnightTimeZone is missing — "
                f"the session monitor cannot compute the real expiry, so the re-auth prompt "
                f"would fire hours after the token died"
            )

        orders = data.get("orders", {})
        for field in ("types", "timeInForce", "positionEffects"):
            if not orders.get(field):
                errors.append(f"{rel}: orders.{field} is empty — the order ticket would render nothing")

        basket = orders.get("basket", {})
        if basket.get("supported") and not basket.get("maxLegs"):
            errors.append(f"{rel}: basket.supported is true but maxLegs is missing")

        market = data.get("marketData", {})
        if market.get("streaming") and not market.get("streamModes"):
            errors.append(f"{rel}: marketData.streaming is true but streamModes is empty")

        if data.get("hosting") == "gateway" and not data.get("gateway"):
            errors.append(f"{rel}: hosting is 'gateway' but there is no gateway block to supervise")

        for limit in data.get("rateLimits", []):
            if not any(k in limit for k in ("perSecond", "perMinute", "perDay")):
                errors.append(
                    f"{rel}: rateLimits entry for scope '{limit.get('scope')}' sets no limit"
                )

    return errors


def main() -> int:
    if not SCHEMA_PATH.exists():
        print(f"{RED}Schema not found at {SCHEMA_PATH.relative_to(REPO)}{NC}")
        return 1

    manifests = find_manifests()
    if not manifests:
        print(f"{RED}No connector manifests found.{NC}")
        return 1

    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))

    try:
        errors = validate_with_jsonschema(schema, manifests)
        mode = "full schema validation"
    except ImportError:
        errors = validate_structurally(manifests)
        mode = "structural checks only (pip install jsonschema for full validation)"
        print(f"{YELLOW}jsonschema not installed — {mode}{NC}\n")

    print(f"{BOLD}Validated {len(manifests)} manifest(s) — {mode}{NC}")
    for path in manifests:
        print(f"  {path.relative_to(REPO)}")

    if errors:
        print(f"\n{RED}{len(errors)} problem(s):{NC}")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"\n{GREEN}All manifests valid.{NC}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
