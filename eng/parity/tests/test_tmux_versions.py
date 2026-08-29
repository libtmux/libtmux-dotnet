"""Prove the version check notices a list that stopped agreeing."""

from __future__ import annotations

import json
import pathlib
import runpy
import typing as t


def load_checker() -> dict[str, t.Any]:
    """Load the version check as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_tmux_versions.py")
    )


def verify(root: pathlib.Path) -> list[str]:
    """Run the version check against one repository root."""
    checked: list[str] = load_checker()["verify"](root)
    return checked


def write(root: pathlib.Path, supported: list[str]) -> pathlib.Path:
    """Lay out a repository whose every version list agrees."""
    checker = load_checker()
    transition = "3.7"
    manifest = {
        "schemaVersion": 1,
        "minimum": supported[0],
        "maximumTested": supported[-1],
        "transition": transition,
        "supported": supported,
    }
    files = {
        "eng/tmux/versions.json": json.dumps(manifest),
        ".github/workflows/dotnet-tmux.yml": "tmux: [{}]".format(
            ", ".join(f"'{version}'" for version in supported)
        ),
        "eng/tmux/run-matrix.sh": "REQUIRED_VERSIONS=({})\ntmuxVersions:[{}]".format(
            " ".join(supported),
            ",".join(f'"{version}"' for version in supported),
        ),
        "eng/tmux/build-version.sh": "<{}|master>".format(
            "|".join(checker["buildable"](supported, transition))
        ),
    }
    both = f"{supported[0]} {supported[-1]}"
    for named in (
        "src/LibTmux/Constants/TmuxConstants.cs",
        "README.md",
        "src/LibTmux/README.md",
        "src/LibTmux.Mcp/README.md",
        "docs/README.md",
    ):
        files[named] = both
    files[".github/CONTRIBUTING.md"] = supported[0]
    files[".github/ISSUE_TEMPLATE/bug_report.yml"] = supported[-1]

    for relative, content in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    return root


VERSIONS = ["3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b", "3.7c"]


def test_the_repository_agrees_with_its_manifest() -> None:
    """The check has to pass on the tree it ships in, or it says nothing."""
    assert verify(pathlib.Path(__file__).parents[3]) == []


def test_a_version_no_consumer_carries_is_reported(tmp_path: pathlib.Path) -> None:
    """Adding a version to the manifest alone is exactly what this catches."""
    root = write(tmp_path, [*VERSIONS, "3.8"])
    (root / "eng" / "tmux" / "run-matrix.sh").write_text(
        "REQUIRED_VERSIONS=({})".format(" ".join(VERSIONS)), encoding="utf-8"
    )

    assert [line for line in verify(root) if "run-matrix.sh" in line] == [
        "eng/tmux/run-matrix.sh: does not carry REQUIRED_VERSIONS=({})".format(
            " ".join([*VERSIONS, "3.8"])
        ),
        'eng/tmux/run-matrix.sh: does not carry tmuxVersions:[{}]'.format(
            ",".join(f'"{version}"' for version in [*VERSIONS, "3.8"])
        ),
    ]
