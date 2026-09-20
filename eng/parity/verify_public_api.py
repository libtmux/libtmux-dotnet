"""Check parity destinations and explicit API policy against compiler symbols."""

from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).parents[2]
API_PATH = ROOT / "docs/public-api.json"
LEDGER_PATH = ROOT / "docs/parity/parity-ledger.json"
INVENTORY_PATH = ROOT / "artifacts/api-inventory.json"
TMUX_MAX_VERSION_ADAPTATION = (
    "Semantic adaptation: map Python TMUX_MAX_VERSION 3.7 to "
    "MaximumTestedTmuxVersion 3.7c, the highest required tested version"
)


def load_document(path: pathlib.Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


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
            for token in policy.get("forbiddenPublicTokens", []):
                if token in symbol["signature"]:
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
        if ownership == "borrowed" and disposable:
            violations.append(f"borrowed type is disposable: {identifier}")
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
        if rule.get("processBacked") and not rule.get("portable") and symbol["visibility"] == "public":
            attributes = symbol["attributes"] + symbols.get(symbol.get("declaringType"), {}).get("attributes", [])
            annotated = any(a["type"] == "System.Runtime.Versioning.UnsupportedOSPlatformAttribute" and a["arguments"] == ["windows"] for a in attributes)
            if not annotated:
                violations.append(f"missing Windows annotation: {identifier}")
    return violations


def main() -> int:
    violations = validate(load_document(API_PATH), load_document(LEDGER_PATH), load_document(INVENTORY_PATH))
    for violation in violations:
        print(violation, file=sys.stderr)
    return bool(violations)


if __name__ == "__main__":
    raise SystemExit(main())
