"""Verify retained evidence is bound to one exact source commit."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys
import typing as t
import zipfile
from xml.etree import ElementTree

COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")
TREE_PATTERN = re.compile(r"^[0-9a-f]{40}$")
FINGERPRINT_MODES = ("evaluated-commit-tree",)


class SourceBindingError(ValueError):
    """Retained evidence cannot be bound to its source commit."""


def git(repository: pathlib.Path, *arguments: str) -> str:
    """Return trimmed stdout from one Git command.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.
    *arguments : str
        Git arguments after ``-C``.

    Returns
    -------
    str
        Trimmed standard output.

    Raises
    ------
    SourceBindingError
        When Git is unavailable or reports failure.
    """
    try:
        completed = subprocess.run(
            ["git", "-C", str(repository), *arguments],
            check=True,
            capture_output=True,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as exception:
        message = f"git {' '.join(arguments)} failed"
        raise SourceBindingError(message) from exception
    return completed.stdout.strip()


def resolve_commit(repository: pathlib.Path, revision: str) -> str:
    """Resolve one revision to a full commit object name.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.
    revision : str
        Revision such as ``HEAD`` or ``HEAD^``.

    Returns
    -------
    str
        Forty-character commit object name.

    Raises
    ------
    SourceBindingError
        When the revision does not name a commit.

    Examples
    --------
    >>> callable(resolve_commit)
    True
    """
    resolved = git(repository, "rev-parse", f"{revision}^{{commit}}")
    if COMMIT_PATTERN.fullmatch(resolved) is None:
        message = f"revision does not name a commit: {revision}"
        raise SourceBindingError(message)
    return resolved


def commit_tree(repository: pathlib.Path, commit: str) -> str:
    """Return the tree object name recorded by one commit.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.
    commit : str
        Commit object name.

    Returns
    -------
    str
        Forty-character tree object name.

    Raises
    ------
    SourceBindingError
        When the commit does not name a tree.

    Examples
    --------
    >>> callable(commit_tree)
    True
    """
    tree = git(repository, "rev-parse", f"{commit}^{{tree}}")
    if TREE_PATTERN.fullmatch(tree) is None:
        message = f"commit does not name a tree: {commit}"
        raise SourceBindingError(message)
    return tree


def load_environment(evidence: pathlib.Path) -> dict[str, t.Any]:
    """Read one evidence bundle environment document.

    Parameters
    ----------
    evidence : pathlib.Path
        Evidence bundle directory.

    Returns
    -------
    dict[str, typing.Any]
        Parsed environment document.

    Raises
    ------
    SourceBindingError
        When the document is missing or is not a JSON object.

    Examples
    --------
    >>> callable(load_environment)
    True
    """
    path = evidence / "environment.json"
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exception:
        message = f"environment document cannot be read: {path}"
        raise SourceBindingError(message) from exception
    if not isinstance(document, dict):
        message = f"environment document is not an object: {path}"
        raise SourceBindingError(message)
    return document


def relative_paths(
    repository: pathlib.Path,
    paths: t.Iterable[pathlib.Path],
) -> tuple[str, ...]:
    """Return repository-relative POSIX paths.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.
    paths : Iterable[pathlib.Path]
        Declared roots or files.

    Returns
    -------
    tuple[str, ...]
        Repository-relative POSIX paths.

    Raises
    ------
    SourceBindingError
        When a declared path lies outside the repository.

    Examples
    --------
    >>> relative_paths(pathlib.Path("/repo"), [pathlib.Path("/repo/a/b")])
    ('a/b',)
    """
    resolved_repository = repository.absolute()
    declared: list[str] = []
    for path in paths:
        try:
            relative = path.absolute().relative_to(resolved_repository)
        except ValueError as exception:
            message = f"declared path is outside the repository: {path}"
            raise SourceBindingError(message) from exception
        declared.append(relative.as_posix())
    return tuple(declared)


def covered_by(path: str, roots: t.Sequence[str], files: t.Sequence[str]) -> bool:
    """Report whether one path is inside a root or names an allowed file.

    Parameters
    ----------
    path : str
        Repository-relative POSIX path.
    roots : Sequence[str]
        Allowed directory roots.
    files : Sequence[str]
        Allowed exact files.

    Returns
    -------
    bool
        True when the path is allowed.

    Examples
    --------
    >>> covered_by("docs/evidence/a.json", ["docs/evidence"], [])
    True
    >>> covered_by("docs/evidence-other/a.json", ["docs/evidence"], [])
    False
    >>> covered_by("docs/deltas.json", [], ["docs/deltas.json"])
    True
    """
    return path in files or any(
        path == root or path.startswith(f"{root}/") for root in roots
    )


def worktree_paths(repository: pathlib.Path) -> tuple[str, ...]:
    """Return every path Git reports as changed or untracked.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.

    Returns
    -------
    tuple[str, ...]
        Repository-relative POSIX paths.

    Examples
    --------
    >>> callable(worktree_paths)
    True
    """
    raw = git(repository, "status", "--porcelain", "-z", "--untracked-files=all")
    entries = [entry for entry in raw.split("\0") if entry]
    paths: list[str] = []
    index = 0
    while index < len(entries):
        entry = entries[index]
        index += 1
        status, path = entry[:2], entry[3:]
        paths.append(path)
        # A rename or copy reports its origin in the next NUL-separated field,
        # which is a bare path rather than another status entry.
        if status[0] in {"R", "C"} or status[1] in {"R", "C"}:
            if index < len(entries):
                paths.append(entries[index])
            index += 1
    return tuple(paths)


def changed_paths(repository: pathlib.Path, base: str, head: str) -> tuple[str, ...]:
    """Return paths that differ between two commits.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree.
    base : str
        Older commit object name.
    head : str
        Newer commit object name.

    Returns
    -------
    tuple[str, ...]
        Repository-relative POSIX paths.

    Examples
    --------
    >>> callable(changed_paths)
    True
    """
    raw = git(repository, "diff", "--name-only", "-z", base, head)
    return tuple(path for path in raw.split("\0") if path)


def package_source_binding(package: pathlib.Path) -> list[str]:
    """Return one message per way a built package fails to name its source.

    A package that does not say which commit produced it cannot be stepped
    into, so a report against a released version has nothing to read. The
    specification carries the repository and commit that SourceLink embedded.
    """
    violations: list[str] = []
    with zipfile.ZipFile(package) as archive:
        specifications = [
            name for name in archive.namelist() if name.endswith(".nuspec")
        ]
        if len(specifications) != 1:
            return ["package does not carry exactly one specification"]

        with archive.open(specifications[0]) as stream:
            root = ElementTree.parse(stream).getroot()

    namespace = root.tag[: root.tag.index("}") + 1]
    repository = root.find(f"{namespace}metadata/{namespace}repository")
    if repository is None:
        return ["package names no repository"]

    commit = repository.attrib.get("commit", "")
    if not COMMIT_PATTERN.match(commit):
        violations.append(f"package names no exact commit: {commit or 'none'}")

    if not repository.attrib.get("url"):
        violations.append("package names no repository url")

    return violations


def verify(
    evidence: pathlib.Path,
    repository: pathlib.Path,
    required_revision: str,
    fingerprint_mode: str,
    allow_dirty_roots: t.Sequence[pathlib.Path],
    descendant_roots: t.Sequence[pathlib.Path],
    descendant_paths: t.Sequence[pathlib.Path],
) -> list[str]:
    """Return stable source-binding violations for one evidence bundle.

    Parameters
    ----------
    evidence : pathlib.Path
        Evidence bundle directory.
    repository : pathlib.Path
        Git worktree.
    required_revision : str
        Revision the evidence must name as its evaluated commit.
    fingerprint_mode : str
        Fingerprint binding mode.
    allow_dirty_roots : Sequence[pathlib.Path]
        Roots that may differ from the evaluated commit before staging.
    descendant_roots : Sequence[pathlib.Path]
        Roots the descendant commit may change.
    descendant_paths : Sequence[pathlib.Path]
        Exact files the descendant commit may change.

    Returns
    -------
    list[str]
        Stable violations, empty when the binding holds.

    Examples
    --------
    >>> callable(verify)
    True
    """
    if fingerprint_mode not in FINGERPRINT_MODES:
        return [f"unsupported fingerprint mode: {fingerprint_mode}"]
    violations: list[str] = []
    environment = load_environment(evidence)
    evaluated_commit = environment.get("evaluatedCommit")
    resolved = resolve_commit(repository, required_revision)
    if evaluated_commit != resolved:
        violations.append("evidence evaluated commit differs from the required commit")
        return violations
    recorded_tree = environment.get("evaluatedCommitTree")
    if recorded_tree != commit_tree(repository, resolved):
        violations.append("evidence tree fingerprint differs from the evaluated commit")
    allowed_dirty = relative_paths(repository, allow_dirty_roots)
    allowed_roots = relative_paths(repository, descendant_roots)
    allowed_files = relative_paths(repository, descendant_paths)
    if allowed_roots or allowed_files:
        head = resolve_commit(repository, "HEAD")
        if head == resolved:
            violations.append("evidence commit does not descend from the source commit")
            return violations
        if git(repository, "rev-parse", f"{head}^") != resolved:
            violations.append("evidence commit does not descend from the source commit")
            return violations
        descendant_diff = changed_paths(repository, resolved, head)
        if not descendant_diff:
            violations.append("evidence commit records no retained evidence")
        if any(
            not covered_by(path, allowed_roots, allowed_files)
            for path in descendant_diff
        ):
            violations.append("evidence commit changes source outside retained roots")
        if worktree_paths(repository):
            violations.append("worktree is not clean after the evidence commit")
        return violations
    outside = [
        path
        for path in worktree_paths(repository)
        if not covered_by(path, allowed_dirty, ())
    ]
    if outside:
        violations.append("source differs from the evaluated commit before staging")
    return violations


def main(argv: list[str] | None = None) -> int:
    """Verify one retained evidence bundle from the command line.

    Parameters
    ----------
    argv : list[str] | None
        Optional command-line arguments.

    Returns
    -------
    int
        Zero when bound, one for violations, or two for invalid usage.

    Examples
    --------
    >>> main([])
    2
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=pathlib.Path)
    parser.add_argument("--repository", type=pathlib.Path, default=pathlib.Path())
    parser.add_argument("--require-evaluated-commit")
    parser.add_argument("--fingerprint-mode", choices=FINGERPRINT_MODES)
    parser.add_argument("--allow-dirty-root", action="append", type=pathlib.Path)
    parser.add_argument("--require-descendant-root", action="append", type=pathlib.Path)
    parser.add_argument("--require-descendant-path", action="append", type=pathlib.Path)
    arguments = parser.parse_args(argv)
    dirty_roots = arguments.allow_dirty_root or []
    descendant_roots = arguments.require_descendant_root or []
    descendant_paths = arguments.require_descendant_path or []
    if (
        arguments.evidence is None
        or arguments.require_evaluated_commit is None
        or arguments.fingerprint_mode is None
        or bool(dirty_roots) == bool(descendant_roots)
        or (descendant_paths and not descendant_roots)
    ):
        parser.print_usage(sys.stderr)
        return 2
    try:
        violations = verify(
            arguments.evidence,
            arguments.repository,
            arguments.require_evaluated_commit,
            arguments.fingerprint_mode,
            dirty_roots,
            descendant_roots,
            descendant_paths,
        )
    except SourceBindingError as exception:
        print(str(exception), file=sys.stderr)
        return 1
    for violation in violations:
        print(violation, file=sys.stderr)
    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())
