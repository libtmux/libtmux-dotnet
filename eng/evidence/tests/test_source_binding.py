"""Tests for binding retained evidence to one exact source commit."""

from __future__ import annotations

import json
import pathlib
import subprocess
import zipfile

import pytest

from eng.evidence import verify_source_binding

EVIDENCE_ROOT = "csharp/docs/parity/evidence/0001"
FINAL_EVIDENCE_ROOT = "csharp/docs/parity/evidence/final"
DELTA_PATH = "csharp/docs/parity/version-deltas.json"


def run_git(repository: pathlib.Path, *arguments: str) -> str:
    """Run one Git command inside a fixture repository."""
    return subprocess.run(
        ["git", "-C", str(repository), *arguments],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def write_environment(repository: pathlib.Path, commit: str) -> None:
    """Write an environment document naming one evaluated commit and tree."""
    evidence = repository / EVIDENCE_ROOT
    evidence.mkdir(parents=True, exist_ok=True)
    (evidence / "environment.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "evaluatedCommitTree": run_git(
                    repository, "rev-parse", f"{commit}^{{tree}}"
                ),
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    (evidence / "results.ndjson").write_text("{}\n", encoding="utf-8")


@pytest.fixture
def repository(tmp_path: pathlib.Path) -> pathlib.Path:
    """Return a seeded single-commit repository."""
    run_git(tmp_path, "init", "-q")
    run_git(tmp_path, "config", "user.email", "binding@example.invalid")
    run_git(tmp_path, "config", "user.name", "Binding")
    source = tmp_path / "csharp" / "src"
    source.mkdir(parents=True)
    (source / "Server.cs").write_text("// server\n", encoding="utf-8")
    (tmp_path / DELTA_PATH).parent.mkdir(parents=True, exist_ok=True)
    (tmp_path / DELTA_PATH).write_text('{"capabilities": []}\n', encoding="utf-8")
    run_git(tmp_path, "add", "-A")
    run_git(tmp_path, "commit", "-qm", "source")
    return tmp_path


def precommit(repository: pathlib.Path) -> int:
    """Run the pre-commit binding arguments."""
    return verify_source_binding.main(
        [
            "--evidence",
            str(repository / EVIDENCE_ROOT),
            "--repository",
            str(repository),
            "--require-evaluated-commit",
            "HEAD",
            "--allow-dirty-root",
            str(repository / EVIDENCE_ROOT),
            "--fingerprint-mode",
            "evaluated-commit-tree",
        ]
    )


def postcommit(repository: pathlib.Path) -> int:
    """Run the post-commit binding arguments."""
    return verify_source_binding.main(
        [
            "--evidence",
            str(repository / EVIDENCE_ROOT),
            "--repository",
            str(repository),
            "--require-evaluated-commit",
            "HEAD^",
            "--require-descendant-root",
            str(repository / EVIDENCE_ROOT),
            "--require-descendant-path",
            str(repository / DELTA_PATH),
            "--fingerprint-mode",
            "evaluated-commit-tree",
        ]
    )


def bind_final(repository: pathlib.Path) -> int:
    """Run the binding arguments the closing matrix run is retained under."""
    return verify_source_binding.main(
        [
            "--evidence",
            str(repository / FINAL_EVIDENCE_ROOT),
            "--repository",
            str(repository),
            "--require-evaluated-commit",
            "HEAD",
            "--allow-dirty-root",
            str(repository / FINAL_EVIDENCE_ROOT),
            "--fingerprint-mode",
            "evaluated-commit-tree",
        ]
    )


def commit_evidence(repository: pathlib.Path) -> None:
    """Stage and commit exactly the retained evidence and its reconciliation."""
    run_git(repository, "add", "--", EVIDENCE_ROOT, DELTA_PATH)
    run_git(repository, "commit", "-qm", "evidence")


def test_precommit_accepts_evidence_written_over_a_clean_source_commit(
    repository: pathlib.Path,
) -> None:
    """Accept an unstaged evidence root over an otherwise clean worktree."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))

    assert precommit(repository) == 0


def test_precommit_rejects_source_edited_after_the_evaluated_commit(
    repository: pathlib.Path,
) -> None:
    """Reject evidence captured while tracked source is still mutable."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    (repository / "csharp" / "src" / "Server.cs").write_text(
        "// edited\n", encoding="utf-8"
    )

    assert precommit(repository) == 1


def test_precommit_rejects_a_commit_the_evidence_does_not_name(
    repository: pathlib.Path,
) -> None:
    """Reject evidence whose evaluated commit is not the current commit."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    (repository / "csharp" / "src" / "Window.cs").write_text("//\n", encoding="utf-8")
    run_git(repository, "add", "-A")
    run_git(repository, "commit", "-qm", "later")

    assert precommit(repository) == 1


def test_precommit_rejects_a_tree_the_evidence_does_not_name(
    repository: pathlib.Path,
) -> None:
    """Reject evidence recording a tree that the commit does not carry."""
    commit = run_git(repository, "rev-parse", "HEAD")
    write_environment(repository, commit)
    document = repository / EVIDENCE_ROOT / "environment.json"
    payload = json.loads(document.read_text(encoding="utf-8"))
    payload["evaluatedCommitTree"] = "0" * 40
    document.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    assert precommit(repository) == 1


def test_postcommit_accepts_an_evidence_only_descendant(
    repository: pathlib.Path,
) -> None:
    """Accept a child commit that changes only retained evidence roots."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    (repository / DELTA_PATH).write_text('{"capabilities": [1]}\n', encoding="utf-8")
    commit_evidence(repository)

    assert postcommit(repository) == 0


def test_postcommit_rejects_a_descendant_that_also_changes_source(
    repository: pathlib.Path,
) -> None:
    """Reject a child commit that smuggles source changes beside evidence."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    (repository / "csharp" / "src" / "Server.cs").write_text(
        "// smuggled\n", encoding="utf-8"
    )
    run_git(repository, "add", "-A")
    run_git(repository, "commit", "-qm", "evidence and source")

    assert postcommit(repository) == 1


def test_postcommit_rejects_an_unclean_worktree(repository: pathlib.Path) -> None:
    """Reject a bound evidence commit left beside uncommitted source."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    commit_evidence(repository)
    (repository / "csharp" / "src" / "Server.cs").write_text(
        "// trailing\n", encoding="utf-8"
    )

    assert postcommit(repository) == 1


def test_postcommit_rejects_a_commit_that_is_not_a_direct_child(
    repository: pathlib.Path,
) -> None:
    """Reject evidence separated from its source commit by another commit."""
    write_environment(repository, run_git(repository, "rev-parse", "HEAD"))
    commit_evidence(repository)
    (repository / "csharp" / "src" / "Pane.cs").write_text("//\n", encoding="utf-8")
    run_git(repository, "add", "-A")
    run_git(repository, "commit", "-qm", "later source")

    assert postcommit(repository) == 1


def test_final_matrix_matches_the_closing_source_tree(
    repository: pathlib.Path,
) -> None:
    """Bind the closing matrix run to the tree that produced it.

    The retained closure run is the one claim that the library works on every
    supported tmux version, and it is only a claim about the source somebody
    ships if the tree it ran against is the tree being closed. Every other
    binding test uses the capability cohort's root; this one uses the closure
    root, which is the one the final run writes and the only one whose drift
    would leave a released library resting on a matrix of something else.
    """
    commit = run_git(repository, "rev-parse", "HEAD")
    final = repository / FINAL_EVIDENCE_ROOT
    final.mkdir(parents=True, exist_ok=True)
    (final / "environment.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "evaluatedCommitTree": run_git(
                    repository, "rev-parse", f"{commit}^{{tree}}"
                ),
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    (final / "results.ndjson").write_text("{}\n", encoding="utf-8")

    assert bind_final(repository) == 0

    # A source edit after the run leaves evidence describing a tree nobody is
    # closing, which is the failure this binding exists to catch.
    (repository / "csharp" / "src" / "Server.cs").write_text(
        "// closed after the matrix ran\n", encoding="utf-8"
    )

    assert bind_final(repository) == 1


def test_usage_requires_exactly_one_binding_mode(repository: pathlib.Path) -> None:
    """Reject invocations that request neither or both binding modes."""
    main = verify_source_binding.main
    common = [
        "--evidence",
        str(repository / EVIDENCE_ROOT),
        "--repository",
        str(repository),
        "--require-evaluated-commit",
        "HEAD",
        "--fingerprint-mode",
        "evaluated-commit-tree",
    ]

    assert main(common) == 2
    assert (
        main(
            [
                *common,
                "--allow-dirty-root",
                str(repository / EVIDENCE_ROOT),
                "--require-descendant-root",
                str(repository / EVIDENCE_ROOT),
            ]
        )
        == 2
    )


def test_usage_requires_a_fingerprint_mode(repository: pathlib.Path) -> None:
    """Reject invocations that do not declare how the tree is bound."""
    main = verify_source_binding.main

    assert (
        main(
            [
                "--evidence",
                str(repository / EVIDENCE_ROOT),
                "--repository",
                str(repository),
                "--require-evaluated-commit",
                "HEAD",
                "--allow-dirty-root",
                str(repository / EVIDENCE_ROOT),
            ]
        )
        == 2
    )


def test_a_package_naming_its_commit_passes(tmp_path: pathlib.Path) -> None:
    """A package that says which commit built it can be stepped into."""
    package = write_package(
        tmp_path, commit="a" * 40, url="https://example.invalid/repo"
    )

    assert verify_source_binding.package_source_binding(package) == []


def test_a_package_without_a_commit_is_reported(tmp_path: pathlib.Path) -> None:
    """A report against a released version needs the source it was built from."""
    package = write_package(tmp_path, commit="", url="https://example.invalid/repo")

    assert verify_source_binding.package_source_binding(package) == [
        "package names no exact commit: none"
    ]


def test_a_package_without_a_repository_is_reported(tmp_path: pathlib.Path) -> None:
    """Naming a commit is no use without saying which repository holds it."""
    package = write_package(tmp_path, commit="b" * 40, url="")

    assert verify_source_binding.package_source_binding(package) == [
        "package names no repository url"
    ]


def write_package(directory: pathlib.Path, *, commit: str, url: str) -> pathlib.Path:
    """Write a package shaped the way SourceLink leaves one."""
    repository = (
        f'<repository type="git" url="{url}" commit="{commit}" />'
        if url or commit
        else ""
    )
    specification = (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
        f"<metadata><id>LibTmux</id>{repository}</metadata></package>"
    )
    package = directory / "LibTmux.1.0.0.nupkg"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("LibTmux.nuspec", specification)

    return package
