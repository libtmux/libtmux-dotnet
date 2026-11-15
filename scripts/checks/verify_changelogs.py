"""Hold every published package to a changelog of its own.

A package that shares one version with seven others still has its own history:
a reader of ``LibTmux.Mcp`` should not have to read ``LibTmux.Workspace``'s
entries to find what changed in theirs. Each packable project keeps a
``CHANGELOG.md`` beside its project file, the pack step ships it, and this
check keeps the file from going missing, being pasted over by another
package's, or losing the section the next release lands under.

What a package ships is inspected on the packed archive by the engineering
tool; this check reads the checked-in tree, so a pull request that deletes a
changelog fails before anything is packed.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

REPOSITORY_ROOT = pathlib.Path(__file__).parents[2]

#: The sentence a changelog opens with. It names the package, so a changelog
#: pasted over another package's fails where it was pasted.
OPENING = "Notable changes to `{name}`."

UNRELEASED = "## [Unreleased]"

FENCE = re.compile(r"^ {0,3}(```|~~~)")
PACKABLE = re.compile(r"<IsPackable>\s*true\s*</IsPackable>")
PACKAGE_ID = re.compile(r"<PackageId>\s*([^<\s]+)\s*</PackageId>")


def packages(root: pathlib.Path) -> dict[str, pathlib.Path]:
    """Return each packable project's package id and directory.

    Parameters
    ----------
    root : pathlib.Path
        Repository root.

    Returns
    -------
    dict[str, pathlib.Path]
        Package id mapped to the directory holding its project file.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as tmp:
    ...     root = pathlib.Path(tmp)
    ...     (root / "src" / "A").mkdir(parents=True)
    ...     _ = (root / "src" / "A" / "A.csproj").write_text(
    ...         "<IsPackable>true</IsPackable><PackageId>A</PackageId>",
    ...         encoding="utf-8",
    ...     )
    ...     [(name, path.name) for name, path in packages(root).items()]
    [('A', 'A')]
    """
    found: dict[str, pathlib.Path] = {}
    for project in sorted((root / "src").glob("*/*proj")):
        text = project.read_text(encoding="utf-8")
        if not PACKABLE.search(text):
            continue
        identifier = PACKAGE_ID.search(text)
        found[identifier.group(1) if identifier else project.stem] = project.parent
    return found


def outside_fences(text: str) -> list[str]:
    """Return the lines of ``text`` that are not inside a code fence.

    Parameters
    ----------
    text : str
        Markdown source.

    Returns
    -------
    list[str]
        Lines outside every fenced block, without line endings.

    Examples
    --------
    >>> outside_fences("a\\n```\\nb\\n```\\nc")
    ['a', 'c']
    """
    kept: list[str] = []
    fenced = False
    for line in text.splitlines():
        if FENCE.match(line):
            fenced = not fenced
        elif not fenced:
            kept.append(line)
    return kept


def check_changelog(path: pathlib.Path, relative: str, name: str | None) -> list[str]:
    """Return what is wrong with one changelog.

    Parameters
    ----------
    path : pathlib.Path
        The changelog on disk.
    relative : str
        Its path relative to the repository root, for messages.
    name : str | None
        The package it belongs to, or ``None`` for the main changelog.

    Returns
    -------
    list[str]
        One message per problem, empty when the file meets the policy.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as tmp:
    ...     path = pathlib.Path(tmp) / "CHANGELOG.md"
    ...     _ = path.write_text(
    ...         "Notable changes to `A`.\\n\\n## [Unreleased]\\n", encoding="utf-8"
    ...     )
    ...     check_changelog(path, "CHANGELOG.md", "A")
    []
    """
    if not path.is_file():
        return [f"{relative}: missing; every published package keeps a changelog"]
    lines = outside_fences(path.read_text(encoding="utf-8"))
    problems: list[str] = []
    if name is not None and OPENING.format(name=name) not in lines:
        problems.append(f'{relative}: does not open with "{OPENING.format(name=name)}"')
    if UNRELEASED not in lines:
        problems.append(f'{relative}: has no "{UNRELEASED}" section')
    return problems


def verify(root: pathlib.Path) -> list[str]:
    """Return one message per missing or misplaced changelog.

    Parameters
    ----------
    root : pathlib.Path
        Repository root.

    Returns
    -------
    list[str]
        Violations, empty when the main changelog and every packable
        project's changelog meet the policy.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as tmp:
    ...     verify(pathlib.Path(tmp))
    ['CHANGELOG.md: missing; every published package keeps a changelog', 'src: no packable project found']
    """
    problems = check_changelog(root / "CHANGELOG.md", "CHANGELOG.md", None)
    found = packages(root)
    if not found:
        problems.append("src: no packable project found")
    for name, directory in found.items():
        relative = (directory / "CHANGELOG.md").relative_to(root).as_posix()
        problems.extend(check_changelog(directory / "CHANGELOG.md", relative, name))
    return problems


def main() -> int:
    """Validate the checked-in changelogs.

    Returns
    -------
    int
        Process exit code.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, default=REPOSITORY_ROOT)
    args = parser.parse_args()

    problems = verify(args.root)
    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
