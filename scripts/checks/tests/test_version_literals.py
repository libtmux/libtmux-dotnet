"""Prove the version-literal check notices a hand-rolled comparison."""

from __future__ import annotations

import pathlib
import runpy
import typing as t

REPOSITORY_ROOT = pathlib.Path(__file__).parents[3]
SOURCE_ROOT = REPOSITORY_ROOT / "src" / "LibTmux"


def load_checker() -> dict[str, t.Any]:
    """Load the version-literal check as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_version_literals.py")
    )


def test_checked_in_source_has_no_hand_rolled_comparison() -> None:
    """Keep every version comparison routed through TmuxCapabilities."""
    violations = load_checker()["verify"](SOURCE_ROOT)

    assert violations == []


def test_a_reintroduced_hand_rolled_gate_is_rejected(tmp_path: pathlib.Path) -> None:
    """Reject exactly the shape that shipped: a duplicated 3.3a check."""
    (tmp_path / "Pane.Display.cs").write_text(
        "if (request.TargetClient is not null\n"
        "    && owner.Version is TmuxVersion version\n"
        '    && version < TmuxVersion.Parse("3.3a"))\n'
        "{\n"
        "    throw new TmuxVersionTooLowException();\n"
        "}\n",
        encoding="utf-8",
    )

    violations = load_checker()["verify"](tmp_path)

    assert len(violations) == 1
    assert "Pane.Display.cs:3" in violations[0]
    assert 'version < TmuxVersion.Parse("3.3a")' in violations[0]


def test_a_reversed_comparison_is_also_rejected(tmp_path: pathlib.Path) -> None:
    """Catch the literal on either side of the operator."""
    (tmp_path / "Window.Display.cs").write_text(
        'if (TmuxVersion.Parse("3.8") <= owner.Version) { }\n',
        encoding="utf-8",
    )

    violations = load_checker()["verify"](tmp_path)

    assert len(violations) == 1
    assert "Window.Display.cs:1" in violations[0]


def test_the_capability_table_itself_is_exempt(tmp_path: pathlib.Path) -> None:
    """The table defines boundaries; it does not duplicate them."""
    versioning = tmp_path / "Versioning"
    versioning.mkdir()
    (versioning / "TmuxCapabilities.cs").write_text(
        'if (version < TmuxVersion.Parse("3.3a")) { }\n',
        encoding="utf-8",
    )

    violations = load_checker()["verify"](tmp_path)

    assert violations == []


def test_an_unreviewed_third_layout_check_is_still_rejected(
    tmp_path: pathlib.Path,
) -> None:
    """The layout allowlist is exact text, not a blanket file exemption."""
    (tmp_path / "Window.Layout.cs").write_text(
        'if (owner.Version is TmuxVersion version\n'
        '    && version >= TmuxVersion.Parse("3.9"))\n'
        "{\n"
        "}\n",
        encoding="utf-8",
    )

    violations = load_checker()["verify"](tmp_path)

    assert len(violations) == 1
    assert "Window.Layout.cs:2" in violations[0]
