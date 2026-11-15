"""Record repository-relative proof that rejected spike code is absent."""

# Precise proof failures identify the absence gate that still fails.
# ruff: noqa: EM101, EM102, TRY003

from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import typing as t


class DeletionProofError(ValueError):
    """Requested deletion cannot yet be proven."""


def run_git(repository: pathlib.Path, *arguments: str) -> str:
    """Run Git in one repository and return stdout.

    Parameters
    ----------
    repository : pathlib.Path
        Repository root.
    *arguments : str
        Git arguments.

    Returns
    -------
    str
        Standard output without trailing whitespace.

    Examples
    --------
    >>> run_git(pathlib.Path.cwd(), "rev-parse", "--is-inside-work-tree")
    'true'
    """
    return subprocess.run(
        ["git", "-C", str(repository), *arguments],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def require_relative(path: pathlib.Path, label: str) -> pathlib.Path:
    """Validate a repository-relative proof path.

    Parameters
    ----------
    path : pathlib.Path
        Candidate path.
    label : str
        Diagnostic label.

    Returns
    -------
    pathlib.Path
        Validated path.
    """
    if path.is_absolute() or ".." in path.parts or path == pathlib.Path():
        raise DeletionProofError(f"{label} must be repository-relative")
    return path


def build_proof(
    *,
    repository: pathlib.Path,
    solution: pathlib.Path,
    absent: t.Sequence[pathlib.Path],
    absent_globs: t.Sequence[str],
    tracked_prefixes: t.Sequence[pathlib.Path],
    project_tokens: t.Sequence[str],
    project_count: int | None,
) -> dict[str, t.Any]:
    """Build deletion proof only after all absence gates pass.

    Parameters
    ----------
    repository : pathlib.Path
        Repository root.
    solution : pathlib.Path
        Repository-relative solution path.
    absent : Sequence[pathlib.Path]
        Paths that must not exist.
    absent_globs : Sequence[str]
        Globs that must match nothing.
    tracked_prefixes : Sequence[pathlib.Path]
        Index prefixes that must be empty.
    project_tokens : Sequence[str]
        Tokens that must be absent from the solution.
    project_count : int | None
        Required number of remaining solution projects.

    Returns
    -------
    dict[str, Any]
        Sanitized proof object.
    """
    repository = repository.resolve()
    solution = require_relative(solution, "solution")
    checked_absent = [require_relative(path, "absent path") for path in absent]
    checked_prefixes = [
        require_relative(path, "tracked prefix") for path in tracked_prefixes
    ]
    for path in checked_absent:
        if (repository / path).exists():
            raise DeletionProofError(f"absent path still exists: {path.as_posix()}")
    for pattern in absent_globs:
        pure = pathlib.PurePath(pattern)
        if pure.is_absolute() or ".." in pure.parts:
            raise DeletionProofError("absent glob must be repository-relative")
        if any(repository.glob(pattern)):
            raise DeletionProofError(f"absent glob still matches: {pattern}")
    for prefix in checked_prefixes:
        tracked = run_git(repository, "ls-files", "--cached", "--", prefix.as_posix())
        if tracked:
            raise DeletionProofError(
                f"tracked prefix still contains entries: {prefix.as_posix()}"
            )
    solution_path = repository / solution
    if not solution_path.is_file():
        raise DeletionProofError("solution does not exist")
    solution_text = solution_path.read_text(encoding="utf-8")
    for token in project_tokens:
        if token in solution_text:
            raise DeletionProofError(f"project token remains in solution: {token}")
    remaining_projects = sorted(re.findall(r'<Project\s+Path="([^"]+)"', solution_text))
    if project_count is not None:
        if project_count < 0:
            raise DeletionProofError("project count cannot be negative")
        if len(remaining_projects) != project_count:
            raise DeletionProofError("solution project count does not match")
    return {
        "absentDirectories": [path.as_posix() for path in checked_absent],
        "absentGlobs": list(absent_globs),
        "evaluatedCommit": run_git(repository, "rev-parse", "HEAD"),
        "expectedSolutionProjectCount": project_count,
        "passed": True,
        "projectTokens": list(project_tokens),
        "remainingSolutionProjects": remaining_projects,
        "solution": solution.as_posix(),
        "trackedPrefixes": [path.as_posix() for path in checked_prefixes],
    }


def write_proof(output: pathlib.Path, proof: dict[str, t.Any]) -> None:
    """Atomically write a validated deletion proof.

    Parameters
    ----------
    output : pathlib.Path
        Destination path.
    proof : dict[str, Any]
        Validated proof.
    """
    output.parent.mkdir(parents=True, exist_ok=True)
    candidate = output.with_name(f".{output.name}.tmp")
    candidate.write_text(
        json.dumps(proof, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    candidate.replace(output)


def parse_args(argv: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse deletion proof arguments.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    argparse.Namespace
        Parsed arguments.
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--solution", required=True, type=pathlib.Path)
    parser.add_argument("--absent", action="append", default=[], type=pathlib.Path)
    parser.add_argument("--absent-glob", action="append", default=[])
    parser.add_argument(
        "--tracked-prefix", action="append", default=[], type=pathlib.Path
    )
    parser.add_argument("--project-token", action="append", default=[])
    parser.add_argument("--project-count", type=int)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    return parser.parse_args(argv)


def main(argv: t.Sequence[str] | None = None) -> int:
    """Validate absences and write ``deletion.json``.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    int
        Process status.
    """
    arguments = parse_args(argv)
    repository = pathlib.Path(
        run_git(pathlib.Path.cwd(), "rev-parse", "--show-toplevel")
    )
    proof = build_proof(
        repository=repository,
        solution=arguments.solution,
        absent=arguments.absent,
        absent_globs=arguments.absent_glob,
        tracked_prefixes=arguments.tracked_prefix,
        project_tokens=arguments.project_token,
        project_count=arguments.project_count,
    )
    output = require_relative(arguments.output, "output")
    write_proof(repository / output, proof)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
