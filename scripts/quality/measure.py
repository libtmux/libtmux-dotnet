"""Print the numbers docs/quality-bar.md cites.

The exit status is the number of missing project files and source files with
control bytes.

```console
$ uv run python scripts/quality/measure.py
```
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import re
import sys
import typing as t

ROOT = pathlib.Path(__file__).resolve().parents[2]

USES = re.compile(r"uses: [^ ]+")
PINNED = re.compile(r"@[0-9a-f]{40}")
FUZZ_CASES = re.compile(r"CasesPerTarget = ([0-9_]+)")
VALIDATOR_LINE = re.compile(r"uv run.*(verify_.*\.py|--check)")

DOC_FILES = (
    "README.md",
    "src/LibTmux/README.md",
    "src/LibTmux.Query.Json/README.md",
    "src/LibTmux.Workspace/README.md",
    "src/LibTmux.Mcp/README.md",
    "docs/modes/one-shot.md",
    "docs/modes/control-mode.md",
    "docs/modes/chaining.md",
    "docs/modes/matrix.md",
)

# GitHub reads these from the root or from .github, so both count as present.
STANDARD_FILES = (
    "README.md",
    "LICENSE",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "CHANGELOG.md",
)


def read_lines(path: pathlib.Path) -> list[str]:
    """Return a file's lines, or none when it is absent.

    >>> read_lines(pathlib.Path("no-such-file"))
    []
    """
    try:
        return path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return []


def workflow_uses(root: pathlib.Path) -> list[str]:
    """Return every ``uses: ref`` in the workflows, one entry per match."""
    return [
        match
        for path in sorted((root / ".github" / "workflows").glob("*.yml"))
        for line in read_lines(path)
        for match in USES.findall(line)
    ]


def count_matching_lines(root: pathlib.Path, pattern: str, names: t.Iterable[str]) -> int:
    """Count lines matching ``pattern`` across ``names``, as ``rg -c`` summed them."""
    expression = re.compile(pattern)
    return sum(1 for name in names for line in read_lines(root / name) if expression.search(line))


def tmux_plan(root: pathlib.Path) -> dict[str, t.Any]:
    """Plan a scheduled run, the full scope a release proves.

    The workflow lists no versions: ``plan.py`` reads them from the manifest.
    """
    path = root / "scripts" / "tmux-plan" / "plan.py"
    spec = importlib.util.spec_from_file_location("measure_tmux_plan", path)
    if spec is None or spec.loader is None:
        message = f"cannot load {path}"
        raise ImportError(message)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    manifest = json.loads((root / "eng" / "tmux" / "versions.json").read_text(encoding="utf-8"))
    return t.cast("dict[str, t.Any]", module.plan(manifest, event="schedule"))


def fuzz_cases(root: pathlib.Path) -> str:
    """Return the last ``CasesPerTarget`` of the parser fuzz tests, as digits."""
    path = root / "tests" / "LibTmux.UnitTests" / "Fuzzing" / "ParserFuzzTests.cs"
    found = FUZZ_CASES.findall("\n".join(read_lines(path)))
    return found[-1].replace("_", "") if found else ""


def markdown_files(directory: pathlib.Path, *, recursive: bool) -> int:
    """Count ``.md`` files in a directory, one level or all levels."""
    pattern = "**/*.md" if recursive else "*.md"
    return sum(1 for path in directory.glob(pattern) if path.is_file())


def files_containing(paths: t.Iterable[pathlib.Path], text: str) -> int:
    """Count the files among ``paths`` whose content has ``text``."""
    return sum(1 for path in paths if any(text in line for line in read_lines(path)))


def control_byte_files(root: pathlib.Path) -> list[str]:
    """Name the C# files with a raw control byte.

    A raw control byte compiles, is invisible in review, and makes the file
    binary to git.
    """
    allowed = {9, 10, 13}
    forbidden = bytes(byte for byte in range(32) if byte not in allowed)
    found = []
    for tree in ("src", "tests"):
        for path in sorted((root / tree).rglob("*.cs")):
            parts = path.relative_to(root).parts
            if "obj" in parts or "bin" in parts:
                continue
            if any(byte in forbidden for byte in path.read_bytes()):
                found.append(path.relative_to(root).as_posix())
    return found


def measure(root: pathlib.Path) -> tuple[list[tuple[str, str]], list[str]]:
    """Return the labelled measures and the problems that make the run fail."""
    plan = tmux_plan(root)
    include = plan["matrix"]["include"]
    uses = workflow_uses(root)
    pinned = [each for each in uses if PINNED.search(each)]
    dotnet = root / ".github" / "workflows" / "dotnet.yml"
    packages = sorted((root / "src").glob("*/*.csproj"))
    suppressing = [
        root / "src" / "LibTmux" / "LibTmux.csproj",
        root / "src" / "LibTmux.Query.Json" / "LibTmux.Query.Json.csproj",
    ]
    missing = [
        name
        for name in STANDARD_FILES
        if not (root / name).exists() and not (root / ".github" / name).exists()
    ]
    control = control_byte_files(root)

    rows = [
        ("tmux versions proven in CI", str(len({each["tmux"] for each in include}))),
        ("target frameworks", " ".join(sorted({each["framework"] for each in include}))),
        ("doc examples compiled", str(count_matching_lines(root, "```csharp", DOC_FILES))),
        (
            "doc examples executed on live tmux",
            str(count_matching_lines(root, "```csharp run", DOC_FILES)),
        ),
        ("fuzz cases per run", fuzz_cases(root)),
        ("actions pinned to a commit SHA", f"{len(pinned)}/{len(uses)}"),
        # Counted from the workflow rather than from the scripts on disk: the
        # gate is what CI runs, and a script nobody invokes is not a check.
        (
            "document validators",
            str(sum(1 for line in read_lines(dotnet) if VALIDATOR_LINE.search(line))),
        ),
        ("decision records", str(markdown_files(root / "docs" / "decisions", recursive=False))),
        (
            "recorded benchmark runs",
            str(markdown_files(root / "docs" / "benchmarks" / "runs", recursive=True)),
        ),
        ("published packages", str(files_containing(packages, "<IsPackable>true"))),
        ("projects suppressing CS1591 (want 0)", str(files_containing(suppressing, "CS1591"))),
        ("standard project files missing", str(len(missing))),
        ("source files with control bytes", str(len(control))),
    ]
    problems = [f"MISSING: {name}" for name in missing] + control
    return rows, problems


def main() -> int:
    """Print the measures and answer the number of problems."""
    rows, problems = measure(ROOT)
    for label, value in rows:
        print(f"{label:<38} {value}")
    for problem in problems:
        print(problem, file=sys.stderr)
    return len(problems)


if __name__ == "__main__":
    raise SystemExit(main())
