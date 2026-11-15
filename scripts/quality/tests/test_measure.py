"""Prove the quality measures count what they name, and that each can fall."""

from __future__ import annotations

import pathlib

import pytest

import measure

PINNED = "a" * 40


def write(root: pathlib.Path, name: str, text: str | bytes) -> None:
    """Create a file under ``root``, with its directories."""
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(text, bytes):
        path.write_bytes(text)
    else:
        path.write_text(text, encoding="utf-8")


@pytest.fixture
def tree(tmp_path: pathlib.Path) -> pathlib.Path:
    """Return a repository that satisfies every measure."""
    root = tmp_path
    for name in measure.STANDARD_FILES:
        write(root, name, "x\n")
    real = measure.ROOT
    write(root, "eng/tmux/versions.json", (real / "eng/tmux/versions.json").read_text("utf-8"))
    write(
        root, "scripts/tmux-plan/plan.py", (real / "scripts/tmux-plan/plan.py").read_text("utf-8")
    )
    write(
        root,
        ".github/workflows/dotnet.yml",
        f"steps:\n  - uses: actions/checkout@{PINNED}\n  - uses: actions/cache@v4\n"
        "  - run: uv run python scripts/checks/verify_x.py\n"
        "  - run: uv run python scripts/docs/sync.py --check\n",
    )
    write(root, "README.md", "```csharp\nx\n```\n```csharp run\ny\n```\n")
    write(root, "src/LibTmux/Pane.cs", "class Pane {}\n")
    write(root, "src/LibTmux/LibTmux.csproj", "<IsPackable>true</IsPackable>\n")
    write(
        root,
        "tests/LibTmux.UnitTests/Fuzzing/ParserFuzzTests.cs",
        "const int CasesPerTarget = 2_000;\n",
    )
    return root


def rows(root: pathlib.Path) -> dict[str, str]:
    measured, _ = measure.measure(root)
    return dict(measured)


def test_a_clean_tree_measures_what_it_holds(tree: pathlib.Path) -> None:
    measured, problems = measure.measure(tree)
    values = dict(measured)

    assert problems == []
    assert values["actions pinned to a commit SHA"] == "1/2"
    assert values["document validators"] == "2"
    assert values["doc examples compiled"] == "2"
    assert values["doc examples executed on live tmux"] == "1"
    assert values["fuzz cases per run"] == "2000"
    assert values["published packages"] == "1"
    assert values["tmux versions proven in CI"] == str(
        len(measure.tmux_plan(tree)["matrix"]["include"]) // 2
    )


def test_a_missing_project_file_is_a_problem(tree: pathlib.Path) -> None:
    (tree / "SECURITY.md").unlink()

    measured, problems = measure.measure(tree)

    assert dict(measured)["standard project files missing"] == "1"
    assert problems == ["MISSING: SECURITY.md"]


def test_a_file_under_dot_github_counts_as_present(tree: pathlib.Path) -> None:
    (tree / "SECURITY.md").unlink()
    write(tree, ".github/SECURITY.md", "x\n")

    assert measure.measure(tree)[1] == []


def test_a_control_byte_in_a_source_file_is_a_problem(tree: pathlib.Path) -> None:
    write(tree, "src/LibTmux/Bad.cs", b"class Bad {}\x07\n")
    write(tree, "src/LibTmux/obj/Skipped.cs", b"\x07")

    measured, problems = measure.measure(tree)

    assert dict(measured)["source files with control bytes"] == "1"
    assert problems == ["src/LibTmux/Bad.cs"]


def test_a_suppressed_documentation_warning_is_counted(tree: pathlib.Path) -> None:
    write(
        tree, "src/LibTmux/LibTmux.csproj", "<IsPackable>true</IsPackable><NoWarn>CS1591</NoWarn>\n"
    )

    assert rows(tree)["projects suppressing CS1591 (want 0)"] == "1"
