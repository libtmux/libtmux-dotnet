"""Materialize example regions into the documents that publish them.

Every published block is a ``#region`` inside a compiled example method. The
ordinary tmux suite runs its examples live in CI; platform previews can require
their documented manual harness. This copies the region in; ``--check`` fails
on drift instead of writing, which is what CI runs.

The copy is materialized rather than transcluded because these are package
READMEs, and nuget.org renders the markdown it is given without resolving
anything.

Anchors name one region or a ``+``-joined sequence of regions, and optionally
namespaces the document adds above the block that the snippet file does not
need::

    <!-- snippet: ConnectAndBuild usings: LibTmux -->
    ```csharp
    ...
    ```
    <!-- endsnippet -->
"""

from __future__ import annotations

import argparse
import difflib
import pathlib
import re
import sys
import textwrap

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SNIPPETS = REPOSITORY / "examples" / "LibTmux.Examples" / "Snippets"
DOCUMENTS = (
    "README.md",
    "src/LibTmux/README.md",
    "src/LibTmux.Query.Json/README.md",
    "src/LibTmux.Workspace/README.md",
    "src/LibTmux.Mcp/README.md",
    "docs/mcp/README.md",
    "docs/modes/one-shot.md",
    "docs/modes/control-mode.md",
    "docs/modes/chaining.md",
    "docs/modes/matrix.md",
    "docs/psmux.md",
)

REGION = re.compile(
    r"^[ \t]*#region[ \t]+(?P<name>\S+)[ \t]*\n(?P<body>.*?)^[ \t]*#endregion[ \t]*$",
    re.MULTILINE | re.DOTALL,
)
ANCHOR = re.compile(
    r"(?P<open><!-- snippet: (?P<name>\S+)(?P<options>[^>]*?) -->\n)"
    r"(?P<body>.*?)"
    r"(?P<close><!-- endsnippet -->)",
    re.DOTALL,
)
USINGS = re.compile(r"usings:\s*(?P<names>[\w., ]+)")


def read_regions() -> dict[str, str]:
    """Return every published region, keyed by name."""
    regions: dict[str, str] = {}
    for path in sorted(SNIPPETS.glob("*.cs")):
        for match in REGION.finditer(path.read_text(encoding="utf-8")):
            name = match.group("name")
            if name in regions:
                msg = f"{path.name}: #region {name} is declared twice"
                raise SystemExit(msg)
            regions[name] = textwrap.dedent(match.group("body")).strip("\n")
    return regions


def render(name: str, options: str, regions: dict[str, str], used: set[str]) -> str:
    """Return the fenced block a document should carry for named regions."""
    bodies: list[str] = []
    for component in name.split("+"):
        if component not in regions:
            known = ", ".join(sorted(regions)) or "none"
            msg = f"no #region named {component}. Published regions: {known}"
            raise SystemExit(msg)
        used.add(component)
        bodies.append(regions[component])

    body = "\n".join(bodies)
    using = USINGS.search(options)
    if using:
        namespaces = [part.strip() for part in re.split(r"[ ,]+", using.group("names")) if part.strip()]
        prologue = "".join(f"using {namespace};\n" for namespace in namespaces)
        body = f"{prologue}\n{body}"
    return f"```csharp\n{body}\n```\n"


def apply(text: str, regions: dict[str, str], used: set[str]) -> str:
    """Return the document with every anchored block rewritten from source."""

    def replace(match: re.Match[str]) -> str:
        name = match.group("name")
        block = render(name, match.group("options"), regions, used)
        return f"{match.group('open')}{block}{match.group('close')}"

    return ANCHOR.sub(replace, text)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="report drift and fail instead of writing",
    )
    arguments = parser.parse_args(argv)

    regions = read_regions()
    used: set[str] = set()
    drifted: list[str] = []
    written: list[str] = []

    for name in DOCUMENTS:
        path = REPOSITORY / name
        before = path.read_text(encoding="utf-8")
        after = apply(before, regions, used)
        if before == after:
            continue
        if arguments.check:
            drifted.append(name)
            diff = difflib.unified_diff(
                before.splitlines(keepends=True),
                after.splitlines(keepends=True),
                fromfile=f"{name} (checked in)",
                tofile=f"{name} (from Snippets/)",
            )
            sys.stdout.writelines(diff)
        else:
            path.write_text(after, encoding="utf-8")
            written.append(name)

    if drifted:
        print(
            f"\n{len(drifted)} document(s) no longer match the code they publish. "
            "Run: uv run python eng/docs/sync_snippets.py",
            file=sys.stderr,
        )
        return 1

    idle = sorted(set(regions) - used)
    if idle:
        print(
            "These regions are published by no document: " + ", ".join(idle),
            file=sys.stderr,
        )
        return 1

    if written:
        print("updated: " + ", ".join(written))
    else:
        print(f"{len(used)} snippet(s) already current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
