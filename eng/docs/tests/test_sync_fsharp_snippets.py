"""Exercise F# guide snippet registration and materialization."""

from __future__ import annotations

import pathlib
import runpy


def synchronizer() -> dict:
    """Load the F# snippet synchronizer without importing the repository."""
    root = pathlib.Path(__file__).parents[3]
    return runpy.run_path(str(root / "eng/docs/sync_fsharp_snippets.py"))


def write_region(path: pathlib.Path, name: str, body: str) -> None:
    """Write one F# comment-delimited source block that a document may publish."""
    path.write_text(
        f"// fsharp-snippet: {name}\n{body}\n// endfsharp-snippet\n",
        encoding="utf-8",
    )


def test_registered_snippet_materializes_and_checks(tmp_path: pathlib.Path) -> None:
    """A registered F# fence is copied from its compiled source region."""
    sources = tmp_path / "snippets"
    sources.mkdir()
    write_region(sources / "Guides.fs", "Golden", "let golden = 1")
    document = tmp_path / "guide.md"
    document.write_text(
        "<!-- fsharp-snippet: Golden run -->\nold\n<!-- endfsharp-snippet -->\n",
        encoding="utf-8",
    )

    run = synchronizer()["run"]

    assert run(sources, [document], check=False) == []
    assert document.read_text(encoding="utf-8") == (
        "<!-- fsharp-snippet: Golden run -->\n"
        "```fsharp run\n"
        "let golden = 1\n"
        "```\n"
        "<!-- endfsharp-snippet -->\n"
    )
    assert run(sources, [document], check=True) == []


def test_unregistered_fence_and_unused_region_are_rejected(tmp_path: pathlib.Path) -> None:
    """Every shipped F# fence and source region must have one counterpart."""
    sources = tmp_path / "snippets"
    sources.mkdir()
    write_region(sources / "Guides.fs", "Unused", "let unused = 1")
    document = tmp_path / "guide.md"
    document.write_text("```fsharp run\nlet loose = 1\n```\n", encoding="utf-8")

    errors = synchronizer()["run"](sources, [document], check=True)

    assert any("is not registered" in error for error in errors)
    assert any("published by no document" in error for error in errors)


def test_program_output_must_match_its_one_documented_block(tmp_path: pathlib.Path) -> None:
    """CI compares a program's real output with the block a document shows."""
    document = tmp_path / "README.md"
    document.write_text(
        "It prints:\n\n<!-- fsharp-output: Demo -->\n```text\nready: true\nexit 3\n```\n"
        "<!-- endfsharp-output -->\n",
        encoding="utf-8",
    )
    compare = synchronizer()["compare_output"]

    assert compare([document], "Demo", "ready: true  \nexit 3\n\n") == []
    assert compare([document], "Demo", "ready: false\nexit 3\n")[0].startswith("F# output Demo differs")
    assert compare([document], "Other", "") == ["F# output Other is documented 0 times; expected once"]


def test_output_block_must_name_a_source_block(tmp_path: pathlib.Path) -> None:
    """A renamed program cannot leave its documented output behind."""
    sources = tmp_path / "snippets"
    sources.mkdir()
    write_region(sources / "Guides.fs", "Golden", "let golden = 1")
    document = tmp_path / "guide.md"
    document.write_text(
        "<!-- fsharp-snippet: Golden -->\n```fsharp\nlet golden = 1\n```\n<!-- endfsharp-snippet -->\n"
        "<!-- fsharp-output: Renamed -->\n```text\n1\n```\n<!-- endfsharp-output -->\n",
        encoding="utf-8",
    )

    errors = synchronizer()["run"](sources, [document], check=True)

    assert errors == ["F# output Renamed names no F# source block"]
