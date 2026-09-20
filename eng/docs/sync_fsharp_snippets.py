"""Materialize compiled F# guide blocks into the documents that publish them."""

from __future__ import annotations

import argparse
import pathlib
import re
import sys
import textwrap
import typing as t


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SNIPPETS = REPOSITORY / "examples" / "LibTmux.FSharp.Examples" / "Snippets"
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
) -> list[str]:
    """Synchronize F# guide snippets and return every contract violation."""
    errors: list[str] = []
    regions = read_regions(sources)
    used: list[str] = []

    for path in documents:
        before = path.read_text(encoding="utf-8")
        after = materialize(before, regions, used)
        errors.extend(validate_fences(path, after))
        if before == after:
            continue
        if check:
            errors.append(f"{path}: differs from its F# snippet source")
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
    return errors


def main(arguments: t.Sequence[str] | None = None) -> int:
    """Write F# guide snippets, or check that the published copies are current."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="report drift without writing",
    )
    parsed = parser.parse_args(arguments)
    errors = run(SNIPPETS, DOCUMENTS, check=parsed.check)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
