"""Tests for guarded spike deletion proof."""

from __future__ import annotations

import importlib
import pathlib
import sys
import typing as t

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent))
record_deletion = t.cast(t.Any, importlib.import_module("record_deletion"))


def _repository(tmp_path: pathlib.Path) -> pathlib.Path:
    root = tmp_path / "repo"
    root.mkdir()
    record_deletion.run_git(root, "init")
    record_deletion.run_git(root, "config", "user.email", "test@example.invalid")
    record_deletion.run_git(root, "config", "user.name", "Test User")
    (root / "solution.slnx").write_text("<Solution />\n", encoding="utf-8")
    record_deletion.run_git(root, "add", "solution.slnx")
    record_deletion.run_git(root, "commit", "-m", "seed")
    return root


def test_deletion_refuses_present_directory(tmp_path: pathlib.Path) -> None:
    """Refuse proof while an absent path still exists."""
    root = _repository(tmp_path)
    (root / "candidate").mkdir()

    with pytest.raises(record_deletion.DeletionProofError, match="still exists"):
        record_deletion.build_proof(
            repository=root,
            solution=pathlib.Path("solution.slnx"),
            absent=[pathlib.Path("candidate")],
            absent_globs=[],
            tracked_prefixes=[],
            project_tokens=[],
            project_count=None,
        )


def test_deletion_refuses_tracked_prefix(tmp_path: pathlib.Path) -> None:
    """Refuse proof while the index retains a removed project prefix."""
    root = _repository(tmp_path)
    tracked = root / "candidate" / "file.cs"
    tracked.parent.mkdir()
    tracked.write_text("tracked\n", encoding="utf-8")
    record_deletion.run_git(root, "add", "candidate/file.cs")

    with pytest.raises(record_deletion.DeletionProofError, match="tracked prefix"):
        record_deletion.build_proof(
            repository=root,
            solution=pathlib.Path("solution.slnx"),
            absent=[],
            absent_globs=[],
            tracked_prefixes=[pathlib.Path("candidate")],
            project_tokens=[],
            project_count=None,
        )


def test_deletion_refuses_solution_project_token(tmp_path: pathlib.Path) -> None:
    """Refuse proof while the solution retains a removed project token."""
    root = _repository(tmp_path)
    (root / "solution.slnx").write_text(
        '<Solution><Project Path="Candidate.csproj" /></Solution>\n',
        encoding="utf-8",
    )

    with pytest.raises(record_deletion.DeletionProofError, match="project token"):
        record_deletion.build_proof(
            repository=root,
            solution=pathlib.Path("solution.slnx"),
            absent=[],
            absent_globs=[],
            tracked_prefixes=[],
            project_tokens=["Candidate"],
            project_count=None,
        )


def test_deletion_rejects_absolute_paths(tmp_path: pathlib.Path) -> None:
    """Keep checkout paths out of durable deletion evidence."""
    root = _repository(tmp_path)

    with pytest.raises(record_deletion.DeletionProofError, match="repository-relative"):
        record_deletion.build_proof(
            repository=root,
            solution=pathlib.Path("solution.slnx"),
            absent=[tmp_path / "candidate"],
            absent_globs=[],
            tracked_prefixes=[],
            project_tokens=[],
            project_count=None,
        )


def test_deletion_refuses_wrong_project_count(tmp_path: pathlib.Path) -> None:
    """Refuse proof when the solution project count differs from the gate."""
    root = _repository(tmp_path)
    (root / "solution.slnx").write_text(
        '<Solution><Project Path="Kept.csproj" /></Solution>\n',
        encoding="utf-8",
    )

    with pytest.raises(record_deletion.DeletionProofError, match="project count"):
        record_deletion.build_proof(
            repository=root,
            solution=pathlib.Path("solution.slnx"),
            absent=[],
            absent_globs=[],
            tracked_prefixes=[],
            project_tokens=[],
            project_count=0,
        )


def test_deletion_proof_records_recheckable_exact_schema(
    tmp_path: pathlib.Path,
) -> None:
    """Record every repository-relative claim needed by final validation."""
    root = _repository(tmp_path)

    proof = record_deletion.build_proof(
        repository=root,
        solution=pathlib.Path("solution.slnx"),
        absent=[pathlib.Path("removed")],
        absent_globs=["csharp/spikes/Rejected.*"],
        tracked_prefixes=[pathlib.Path("csharp/spikes/Rejected")],
        project_tokens=["Rejected"],
        project_count=0,
    )

    assert set(proof) == {
        "absentDirectories",
        "absentGlobs",
        "evaluatedCommit",
        "expectedSolutionProjectCount",
        "passed",
        "projectTokens",
        "remainingSolutionProjects",
        "solution",
        "trackedPrefixes",
    }
    assert proof["expectedSolutionProjectCount"] == 0
    assert proof["projectTokens"] == ["Rejected"]
    assert proof["solution"] == "solution.slnx"


def test_project_count_cli_defaults_to_null() -> None:
    """Keep project-count optional while always emitting its schema field."""
    arguments = record_deletion.parse_args(
        ["--solution", "solution.slnx", "--output", "deletion.json"]
    )

    assert arguments.project_count is None


def test_project_count_cli_accepts_task_15_literal() -> None:
    """Accept Task 15's exact zero-project deletion gate."""
    arguments = record_deletion.parse_args(
        [
            "--solution",
            "solution.slnx",
            "--project-count",
            "0",
            "--output",
            "deletion.json",
        ]
    )

    assert arguments.project_count == 0
