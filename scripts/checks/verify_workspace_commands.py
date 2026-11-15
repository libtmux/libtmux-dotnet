"""Check the command tree that ``tmux-workspace --generate reference --json`` prints.

The packed tool describes its own commands, and the reference documents read
that description. A tool that lost a command, or an ``import`` that lost a
source format, would still start, so this counts what the packed tool reports.

```console
$ uv run python scripts/checks/verify_workspace_commands.py commands.json
```
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import typing as t

COMMAND_COUNT = 9
IMPORT_SOURCE_COUNT = 2


def verify(tree: dict[str, t.Any]) -> list[str]:
    """Return what differs between the reported command tree and the expected one.

    >>> verify({"commands": [{"name": "import", "commands": [{}, {}]}] + [{}] * 8})
    []
    >>> verify({"commands": []})
    ['expected 9 commands, found 0', "no 'import' command"]
    """
    commands = tree.get("commands", [])
    violations = []
    if len(commands) != COMMAND_COUNT:
        violations.append(f"expected {COMMAND_COUNT} commands, found {len(commands)}")
    imports = next((node for node in commands if node.get("name") == "import"), None)
    if imports is None:
        violations.append("no 'import' command")
    elif len(imports.get("commands", [])) != IMPORT_SOURCE_COUNT:
        found = len(imports.get("commands", []))
        violations.append(f"expected {IMPORT_SOURCE_COUNT} import sources, found {found}")
    return violations


def main(argv: list[str] | None = None) -> int:
    """Read the JSON a packed tool printed and report what is wrong with it."""
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("reference", type=pathlib.Path, help="the JSON the tool printed")
    arguments = parser.parse_args(argv)
    tree = json.loads(arguments.reference.read_text(encoding="utf-8"))
    violations = verify(tree)
    for violation in violations:
        print(violation, file=sys.stderr)
    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())
