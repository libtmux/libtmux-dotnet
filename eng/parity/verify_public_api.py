"""Check parity destinations and explicit API policy against compiler symbols."""

from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).parents[2]
API_PATH = ROOT / "docs/public-api.json"
LEDGER_PATH = ROOT / "docs/parity/parity-ledger.json"
INVENTORY_PATH = ROOT / "artifacts/api-inventory.json"
FSHARP_BASELINE_PATH = ROOT / "src/LibTmux.FSharp/PublicAPI.json"
TMUX_MAX_VERSION_ADAPTATION = (
    "Semantic adaptation: map Python TMUX_MAX_VERSION 3.7 to "
    "MaximumTestedTmuxVersion 3.7c, the highest required tested version"
)


def load_document(path: pathlib.Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def fsharp_contracts(inventory: dict) -> list[dict]:
    """Keep compiled F# shape separate from editable documentation prose."""
    return sorted(
        (
            {key: value for key, value in member.items() if key != "documentation"}
            for member in inventory["members"]
            if member["package"] == "LibTmux.FSharp"
        ),
        key=lambda member: member["id"],
    )


def validate_fsharp(inventory: dict, baseline: dict) -> list[str]:
    """Reject an absent, duplicated or changed compiled F# declaration."""
    if baseline.get("schema") != "libtmux-fsharp-api" or baseline.get("version") != 1:
        return ["invalid F# API baseline schema"]
    actual = fsharp_contracts(inventory)
    expected = baseline.get("members", [])
    if not actual or not expected:
        return ["F# compiled API or reviewed baseline is empty"]
    if any(len({member["id"] for member in rows}) != len(rows) for rows in (actual, expected)):
        return ["duplicate F# public API identity"]
    before = {member["id"]: member for member in expected}
    after = {member["id"]: member for member in actual}
    return [
        f"F# public API differs from reviewed baseline: {identifier}"
        for identifier in sorted(before.keys() | after.keys())
        if before.get(identifier) != after.get(identifier)
    ]


def validate(policy: dict, ledger: dict, inventory: dict) -> list[str]:
    violations = []
    if policy.get("schema") != "libtmux-api-policy" or policy.get("version") != 2:
        violations.append("invalid API policy schema")
    symbols = {member["id"]: member for member in inventory["members"]}
    if policy.get("sourceRevision") != ledger.get("sourceRevision"):
        violations.append("policy and ledger source revisions differ")
    for identifier, package in policy.get("packageAssignments", {}).items():
        if symbols.get(identifier, {}).get("package") != package:
            violations.append(f"incorrect package ownership: {identifier}")
    for symbol in symbols.values():
        if symbol["visibility"] == "public":
            if "exposedTypes" not in symbol:
                violations.append(f"compiler type metadata missing: {symbol['id']}")
                continue
            for token in policy.get("forbiddenPublicTokens", []):
                if any(
                    token in text
                    for text in [symbol["signature"], *symbol["exposedTypes"]]
                ):
                    violations.append(f"forbidden public API token: {token}")
    rows = ledger.get("rows", [])
    if {row.get("componentId") for row in rows} != set(range(1, 19)):
        violations.append("ledger component IDs are incomplete")
    if len({row.get("pythonSymbolId") for row in rows}) != len(rows):
        violations.append("duplicate parity ledger row IDs")
    for row in rows:
        source = row.get("pythonSymbolId")
        status = row.get("destinationStatus")
        destination = row.get("csharpDestination")
        symbol = symbols.get(destination)
        if status in {"approved", "internalized"}:
            if symbol is None:
                violations.append(f"unknown {status} destination: {source}: {destination}")
            elif symbol["visibility"] != ("public" if status == "approved" else "internal"):
                violations.append(f"destination visibility disagrees: {source}: {destination}")
        elif status == "excluded":
            if destination is not None or not row.get("exclusionReason") or not row.get("replacement"):
                violations.append(f"invalid exclusion: {source}")
            replacement = row.get("replacement", "")
            if replacement.startswith(("T:", "M:", "P:", "F:", "E:")) and replacement not in symbols:
                violations.append(f"unknown replacement: {source}: {replacement}")
        else:
            violations.append(f"unexpected parity disposition: {source}")

    for identifier, ownership in policy.get("ownership", {}).items():
        symbol = symbols.get(identifier)
        if symbol is None:
            violations.append(f"unknown ownership type: {identifier}")
            continue
        if ownership not in {"borrowed", "owned"}:
            violations.append(f"invalid ownership classification: {identifier}")
        interfaces = set(symbol["interfaces"])
        disposable = {"System.IDisposable", "System.IAsyncDisposable"} & interfaces
        if ownership == "borrowed":
            if disposable:
                violations.append(f"borrowed type is disposable: {identifier}")
            if any(
                member.get("declaringType") == identifier
                and member["visibility"] == "public"
                and member["id"].startswith("M:")
                and member["name"] in {"Dispose", "DisposeAsync"}
                for member in symbols.values()
            ):
                violations.append(f"borrowed type exposes disposal: {identifier}")
        if ownership == "owned" and symbol["visibility"] == "public" and "System.IAsyncDisposable" not in interfaces:
            violations.append(f"owned type lacks async disposal: {identifier}")

    for rule in policy.get("members", []):
        identifier = rule["id"]
        symbol = symbols.get(identifier)
        if symbol is None:
            violations.append(f"unknown policy member: {identifier}")
            continue
        if rule.get("performsIO"):
            parameters = symbol.get("parameters") or []
            if not symbol["name"].endswith("Async") or not (symbol.get("returnType") or "").startswith(("System.Threading.Tasks.Task", "System.Threading.Tasks.ValueTask")):
                violations.append(f"synchronous I/O member: {identifier}")
            if not parameters or parameters[-1]["type"] != "System.Threading.CancellationToken" or not parameters[-1]["optional"]:
                violations.append(f"invalid cancellation parameter: {identifier}")
        attributes = list(symbol["attributes"])
        container = symbols.get(symbol.get("declaringType"))
        while container is not None:
            attributes.extend(container["attributes"])
            container = symbols.get(container.get("declaringType"))
        if rule.get("portable") and any(
            attribute["type"] in {
                "System.Runtime.Versioning.SupportedOSPlatformAttribute",
                "System.Runtime.Versioning.UnsupportedOSPlatformAttribute",
            }
            for attribute in attributes
        ):
            violations.append(f"portable member has platform annotation: {identifier}")
        if rule.get("processBacked") and not rule.get("portable") and symbol["visibility"] == "public":
            annotated = any(a["type"] == "System.Runtime.Versioning.UnsupportedOSPlatformAttribute" and a["arguments"] == ["windows"] for a in attributes)
            if not annotated:
                violations.append(f"missing Windows annotation: {identifier}")
    return violations


def main() -> int:
    inventory = load_document(INVENTORY_PATH)
    violations = validate(load_document(API_PATH), load_document(LEDGER_PATH), inventory)
    violations.extend(validate_fsharp(inventory, load_document(FSHARP_BASELINE_PATH)))
    for violation in violations:
        print(violation, file=sys.stderr)
    return bool(violations)


if __name__ == "__main__":
    raise SystemExit(main())
