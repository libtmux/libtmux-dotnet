"""Materialize compiled F# guide blocks into the documents that publish them.

A document may also show what a program prints in an output block. A program
in the API example manifest has its output recorded there, and its block is
written from that record; any other program's block is its own record. CI
pipes the program's real output to ``--expect-output NAME`` to keep it true.
"""

from __future__ import annotations

import argparse
import difflib
import json
import pathlib
import re
import sys
import textwrap
import typing as t


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SNIPPETS = REPOSITORY / "examples"
MANIFEST = REPOSITORY / "examples" / "api" / "manifest.json"
DOCUMENTS = (
    REPOSITORY / "src" / "LibTmux.FSharp" / "README.md",
    *sorted((REPOSITORY / "docs" / "fsharp").glob("*.md")),
)
REGION = re.compile(
    r"^[ \t]*// fsharp-snippet: (?P<name>\S+)[ \t]*\n"
    r"(?P<body>.*?)"
    r"^[ \t]*// endfsharp-snippet[ \t]*$",
    re.MULTILINE | re.DOTALL,
)
ANCHOR = re.compile(
    r"(?P<open><!-- fsharp-snippet: (?P<name>\S+)(?P<run>[ \t]+run)? -->\n)"
    r"(?P<body>.*?)"
    r"(?P<close><!-- endfsharp-snippet -->)",
    re.DOTALL,
)
FENCE = re.compile(r"```fsharp(?:[ \t]+run)?\r?\n.*?```", re.DOTALL)
OUTPUT = re.compile(
    r"<!-- fsharp-output: (?P<name>\S+) -->\n"
    r"```text\n(?P<body>.*?)```\n"
    r"<!-- endfsharp-output -->",
    re.DOTALL,
)


def recorded_outputs(manifest: pathlib.Path) -> dict[str, str]:
    """Return each F# program's output as the API example manifest records it."""
    document = json.loads(manifest.read_text(encoding="utf-8"))
    return {
        example["id"].removeprefix("fsharp-"): example["output"]
        for example in document["examples"]
        if example["profile"] == "fsharp"
    }


def read_regions(sources: pathlib.Path) -> dict[str, str]:
    """Return the compiled F# source blocks keyed by their published names."""
    regions: dict[str, str] = {}
    for path in sorted(sources.rglob("*.fs")):
        for match in REGION.finditer(path.read_text(encoding="utf-8")):
            name = match.group("name")
            if name in regions:
                raise ValueError(f"{path}: F# snippet {name} is declared twice")
            regions[name] = textwrap.dedent(match.group("body")).strip("\n")
    return regions


def render(name: str, run: bool, regions: dict[str, str]) -> str:
    """Return the F# fence published for one compiled source region."""
    return f"```fsharp{' run' if run else ''}\n{regions[name]}\n```\n"


def materialize(text: str, regions: dict[str, str], used: list[str]) -> str:
    """Return a document with every valid F# anchor rewritten from source."""

    def replace(match: re.Match[str]) -> str:
        name = match.group("name")
        if name not in regions:
            return match.group(0)
        used.append(name)
        rendered = render(name, bool(match.group("run")), regions)
        return f"{match.group('open')}{rendered}{match.group('close')}"

    return ANCHOR.sub(replace, text)


def materialize_outputs(text: str, recorded: dict[str, str]) -> str:
    """Return a document whose output blocks show the outputs the manifest records."""

    def replace(match: re.Match[str]) -> str:
        name = match.group("name")
        if name not in recorded:
            return match.group(0)
        return f"<!-- fsharp-output: {name} -->\n```text\n{recorded[name]}```\n<!-- endfsharp-output -->"

    return OUTPUT.sub(replace, text)


def validate_fences(path: pathlib.Path, text: str) -> list[str]:
    """Reject an F# fence that is not exactly the body of an F# anchor."""
    errors: list[str] = []
    anchor_bodies = [match.span("body") for match in ANCHOR.finditer(text)]
    for fence in FENCE.finditer(text):
        if not any(start <= fence.start() and fence.end() <= end for start, end in anchor_bodies):
            errors.append(f"{path}: F# fence is not registered by an F# snippet anchor")
    for match in ANCHOR.finditer(text):
        if FENCE.fullmatch(match.group("body").strip()) is None:
            errors.append(f"{path}: F# snippet anchor {match.group('name')} must contain one F# fence")
    return errors


def run(
    sources: pathlib.Path,
    documents: t.Iterable[pathlib.Path],
    *,
    check: bool,
    recorded: dict[str, str] | None = None,
) -> list[str]:
    """Synchronize F# guide snippets and outputs and return every contract violation."""
    errors: list[str] = []
    regions = read_regions(sources)
    used: list[str] = []

    for path in documents:
        before = path.read_text(encoding="utf-8")
        after = materialize_outputs(materialize(before, regions, used), recorded or {})
        errors.extend(validate_fences(path, after))
        if before == after:
            continue
        if check:
            errors.append(f"{path}: differs from its F# snippet source or recorded output")
            continue
        path.write_text(after, encoding="utf-8")

    anchors = {
        match.group("name")
        for path in documents
        for match in ANCHOR.finditer(path.read_text(encoding="utf-8"))
    }
    unknown = sorted(anchors - set(regions))
    errors.extend(f"no F# source block named {name}" for name in unknown)

    duplicate = sorted(name for name in set(used) if used.count(name) > 1)
    errors.extend(f"F# source block {name} is published more than once" for name in duplicate)

    idle = sorted(set(regions) - set(used))
    errors.extend(f"F# source block {name} is published by no document" for name in idle)

    outputs = {
        match.group("name")
        for path in documents
        for match in OUTPUT.finditer(path.read_text(encoding="utf-8"))
    }
    errors.extend(
        f"F# output {name} names no F# source block" for name in sorted(outputs - set(regions))
    )
    return errors


def lines(text: str) -> list[str]:
    """Return text as lines without trailing spaces or trailing blank lines."""
    result = [line.rstrip() for line in text.splitlines()]
    while result and not result[-1]:
        result.pop()
    return result


def compare_output(
    documents: t.Iterable[pathlib.Path],
    name: str,
    actual: str,
    recorded: dict[str, str] | None = None,
) -> list[str]:
    """Return why a program's output differs from its recorded or documented output."""
    if recorded and name in recorded:
        expected = lines(recorded[name])
    else:
        blocks = [
            match.group("body")
            for path in documents
            for match in OUTPUT.finditer(path.read_text(encoding="utf-8"))
            if match.group("name") == name
        ]
        if len(blocks) != 1:
            return [f"F# output {name} is documented {len(blocks)} times; expected once"]
        expected = lines(blocks[0])
    printed = lines(actual)
    if expected == printed:
        return []
    return [
        f"F# output {name} differs from what the program printed:",
        *difflib.unified_diff(expected, printed, "documented", "printed", lineterm=""),
    ]


def main(arguments: t.Sequence[str] | None = None) -> int:
    """Write F# guide snippets, or check that the published copies are current."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="report drift without writing",
    )
    parser.add_argument(
        "--expect-output",
        metavar="NAME",
        help="compare standard input with the output recorded or documented for NAME",
    )
    parsed = parser.parse_args(arguments)
    recorded = recorded_outputs(MANIFEST)
    errors = (
        compare_output(DOCUMENTS, parsed.expect_output, sys.stdin.read(), recorded)
        if parsed.expect_output
        else run(SNIPPETS, DOCUMENTS, check=parsed.check, recorded=recorded)
    )
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
