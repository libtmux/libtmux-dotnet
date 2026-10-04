"""Check that the F# guides name only functions the facade declares."""

from __future__ import annotations

import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[3]
BASELINE = ROOT / "src/LibTmux.FSharp/PublicAPI.json"
DOCUMENTS = (ROOT / "src/LibTmux.FSharp/README.md", *sorted((ROOT / "docs/fsharp").glob("*.md")))

# A call written as `Module.function` outside a code block. Code blocks are
# compiled from examples/, so only prose and tables can name a call that
# does not exist.
CALL = re.compile(r"`([A-Z][A-Za-z]*)\.([a-z][A-Za-z]*)\b")
FENCE = re.compile(r"```.*?```", re.DOTALL)


def declared() -> tuple[set[str], set[str]]:
    members = json.loads(BASELINE.read_text(encoding="utf-8"))["members"]
    modules: set[str] = set()
    calls: set[str] = set()
    for member in members:
        # F# compiles a module that shares a type's name, such as Query, as QueryModule.
        module = member["declaringType"].removeprefix("T:LibTmux.FSharp.").removesuffix("Module")
        modules.add(module)
        calls.add(f"{module}.{member['name']}")
    return modules, calls


def test_guides_name_only_declared_facade_functions():
    modules, calls = declared()
    unknown = sorted(
        f"{document.relative_to(ROOT)}: {module}.{function}"
        for document in DOCUMENTS
        for module, function in CALL.findall(FENCE.sub("", document.read_text(encoding="utf-8")))
        if module in modules and f"{module}.{function}" not in calls
    )
    assert unknown == []
