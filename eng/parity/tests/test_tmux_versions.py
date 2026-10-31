"""Prove the version check notices a list that stopped agreeing."""

from __future__ import annotations

import json
import pathlib
import runpy
import shutil
import typing as t

PLAN = pathlib.Path(__file__).parents[2] / "tmux" / "plan.py"
WORKFLOW = (
    "matrix: ${{ fromJSON(needs.plan.outputs.matrix) }}\n"
    "rejected-version: ${{ fromJSON(needs.plan.outputs.rejected) }}\n"
)


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
        "rejected": ["3.0a", "3.1c", "3.2a", "3.3"],
    }
    files = {
        "eng/tmux/versions.json": json.dumps(manifest),
        ".github/workflows/dotnet-tmux.yml": WORKFLOW,
        "eng/tmux/run-matrix.sh": "REQUIRED_VERSIONS=({})\ntmuxVersions:[{}]".format(
            " ".join(supported),
            ",".join(f'"{version}"' for version in supported),
        ),
        "eng/tmux/build-version.sh": "<{}|master>".format(
            "|".join(["3.0a", "3.1c", "3.2a", "3.3", *checker["buildable"](supported, transition)])
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
    shutil.copyfile(PLAN, root / "eng" / "tmux" / "plan.py")

    return root


VERSIONS = ["3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b", "3.7c", "3.8"]


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


def test_a_rejection_lane_that_stopped_reading_the_plan_is_reported(
    tmp_path: pathlib.Path,
) -> None:
    """The versions the workflow proves are refused come from the plan."""
    root = write(tmp_path, VERSIONS)
    (root / ".github" / "workflows" / "dotnet-tmux.yml").write_text(
        "matrix: ${{ fromJSON(needs.plan.outputs.matrix) }}\n"
        "rejected-version: ['3.2a']\n",
        encoding="utf-8",
    )

    assert verify(root) == [
        ".github/workflows/dotnet-tmux.yml: does not carry "
        "fromJSON(needs.plan.outputs.rejected)"
    ]


def test_a_matrix_that_lists_its_own_versions_is_reported(tmp_path: pathlib.Path) -> None:
    """A second list in the workflow is the drift this check exists to prevent."""
    root = write(tmp_path, VERSIONS)
    (root / ".github" / "workflows" / "dotnet-tmux.yml").write_text(
        "tmux: ['3.3a', '3.8']\n"
        "rejected-version: ${{ fromJSON(needs.plan.outputs.rejected) }}\n",
        encoding="utf-8",
    )

    assert verify(root) == [
        ".github/workflows/dotnet-tmux.yml: does not carry "
        "fromJSON(needs.plan.outputs.matrix)"
    ]


def test_a_plan_that_hard_codes_the_edges_is_reported(tmp_path: pathlib.Path) -> None:
    """A push must cover the manifest's floor and newest release, whatever they are."""
    root = write(tmp_path, [*VERSIONS, "3.9"])
    path = root / "eng" / "tmux" / "plan.py"
    source = path.read_text(encoding="utf-8")
    broken = source.replace(
        "    return list(dict.fromkeys([*supported[:1], *supported[-1:]]))",
        '    return ["3.3a", "3.8"]',
    )
    assert broken != source
    path.write_text(broken, encoding="utf-8")

    assert verify(root) == [
        "eng/tmux/plan.py: a push covers 3.3a, 3.8, not the floor and the newest "
        "tested release, 3.3a, 3.9"
    ]


def test_a_plan_whose_full_scope_drops_a_version_is_reported(tmp_path: pathlib.Path) -> None:
    """The full scope is the whole table and every refusal lane."""
    root = write(tmp_path, VERSIONS)
    path = root / "eng" / "tmux" / "plan.py"
    source = path.read_text(encoding="utf-8")
    broken = source.replace("        versions = list(supported)", "        versions = list(supported[1:])")
    assert broken != source
    path.write_text(broken, encoding="utf-8")

    assert any("the full scope covers" in line for line in verify(root))


def test_a_plan_that_forgets_the_refusal_lanes_is_reported(tmp_path: pathlib.Path) -> None:
    root = write(tmp_path, VERSIONS)
    path = root / "eng" / "tmux" / "plan.py"
    source = path.read_text(encoding="utf-8")
    broken = source.replace(
        '"rejected": list(rejected) if full else [],', '"rejected": [],'
    )
    assert broken != source
    path.write_text(broken, encoding="utf-8")

    assert any("the full scope refuses" in line for line in verify(root))


def test_a_missing_plan_is_reported(tmp_path: pathlib.Path) -> None:
    root = write(tmp_path, VERSIONS)
    (root / "eng" / "tmux" / "plan.py").unlink()

    assert verify(root) == ["eng/tmux/plan.py: missing, so what CI covers cannot be checked"]
