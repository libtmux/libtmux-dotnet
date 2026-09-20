"""Render public source declarations from the Roslyn API inventory."""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import typing as t
from xml.etree import ElementTree

CSHARP_ROOT = pathlib.Path(__file__).parents[2]
INVENTORY_PATH = CSHARP_ROOT / "artifacts/api-inventory.json"
OUTPUT_PATH = CSHARP_ROOT / "docs" / "api" / "README.md"
KIND_TITLES = {
    "T": "Types",
    "M": "Methods",
    "P": "Properties",
    "F": "Fields",
    "E": "Events",
}


def public_member_ids(path: pathlib.Path = INVENTORY_PATH) -> frozenset[str]:
    """Read externally visible, explicitly declared core symbols from Roslyn."""
    return frozenset(
        member["id"] for member in json.loads(path.read_text())["members"]
        if member["package"] == "LibTmux" and member["visibility"] == "public"
        and not member["implicitDeclaration"] and not member["accessor"]
    )


def flatten(node: ElementTree.Element | None) -> str:
    """Return one documentation node as a single line of text."""
    if node is None:
        return ""

    return " ".join("".join(node.itertext()).split())


def read_members(
    path: pathlib.Path | ElementTree.Element,
    approved_members: frozenset[str],
) -> dict[str, str]:
    """Return each documented member identifier and its summary."""
    root = path if isinstance(path, ElementTree.Element) else ElementTree.parse(path).getroot()
    members: dict[str, str] = {}
    for member in root.findall("./members/member"):
        name = member.get("name")
        if name is None or name not in approved_members:
            continue

        summary = flatten(member.find("summary"))
        if summary or member.find("inheritdoc") is not None:
            members[name] = summary or "Inherits the base member contract."

    missing = approved_members - members.keys()
    if missing:
        raise ValueError("Missing documentation: " + ", ".join(sorted(missing)))
    return members


def render(members: dict[str, str]) -> str:
    """Return the reference page for every documented member."""
    grouped: dict[str, list[tuple[str, str]]] = {}
    for name, summary in sorted(members.items()):
        grouped.setdefault(name[:1], []).append((name, summary))

    lines = [
        "# API reference",
        "",
        "Generated from compiler symbols and their XML summaries. Only public",
        "source declarations render. Regenerate with",
        "`uv run python eng/docs/render_api_reference.py`.",
        "",
        "See [choosing a mode](../modes/matrix.md) for how the three execution",
        "modes differ.",
    ]
    for kind, title in KIND_TITLES.items():
        entries = grouped.get(kind)
        if not entries:
            continue

        lines.extend(["", f"## {title}", "", "| Member | Summary |", "|---|---|"])
        for name, summary in entries:
            escaped = summary.replace("|", "\\|")
            lines.append(f"| {code_span(name[2:])} | {escaped} |")

    return "\n".join(lines) + "\n"


def code_span(value: str) -> str:
    """Render metadata names containing generic-arity backticks as valid Markdown."""
    longest_run = 0
    current_run = 0
    for character in value:
        current_run = current_run + 1 if character == "`" else 0
        longest_run = max(longest_run, current_run)
    delimiter = "`" * (longest_run + 1)
    return f"{delimiter}{value}{delimiter}"


def main(arguments: t.Sequence[str] | None = None) -> int:
    """Write the reference, or check the written one is current."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parsed = parser.parse_args(arguments)

    inventory = json.loads(INVENTORY_PATH.read_text())
    root = ElementTree.Element("doc")
    nodes = ElementTree.SubElement(root, "members")
    for member in inventory["members"]:
        if member["documentation"]:
            nodes.append(ElementTree.fromstring(member["documentation"]))
    rendered = render(read_members(root, public_member_ids(INVENTORY_PATH)))
    if parsed.check:
        current = (
            OUTPUT_PATH.read_text(encoding="utf-8") if OUTPUT_PATH.exists() else ""
        )
        if current != rendered:
            print(
                "api reference differs from the built XML documentation",
                file=sys.stderr,
            )
            return 1

        return 0

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(rendered, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
