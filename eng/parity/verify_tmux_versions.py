"""Check every list of supported tmux versions against the one manifest.

The same eight versions were written out in the workflow matrix, two shell
scripts, two validators, the runtime constants and six documents. Adding one
meant finding all of them, and the version that got missed was invisible until
a lane that never ran let a regression through.

`eng/tmux/versions.json` is now the only place the list is decided. Nothing
here rewrites a consumer: a build script that had to parse JSON before it could
build tmux would be worse than one that repeats a list a gate checks.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import typing as t

MANIFEST = pathlib.Path("eng/tmux/versions.json")


def load(root: pathlib.Path) -> dict[str, t.Any]:
    """Read the manifest every other list is measured against."""
    with (root / MANIFEST).open(encoding="utf-8") as handle:
        return t.cast(dict[str, t.Any], json.load(handle))


def buildable(supported: list[str], transition: str) -> list[str]:
    """Order what build-version.sh accepts: the transition before its suffixes.

    tmux 3.7 is buildable but never tested on its own — the matrix runs its
    lettered successors. It sits with them rather than at the end.
    """
    if transition in supported:
        return list(supported)
    successor = next(
        (each for each in supported if each.startswith(transition)),
        None,
    )
    if successor is None:
        return [*supported, transition]
    at = supported.index(successor)
    return [*supported[:at], transition, *supported[at:]]


def verify(root: pathlib.Path) -> list[str]:
    """Report every list that disagrees with the manifest."""
    manifest = load(root)
    supported: list[str] = manifest["supported"]
    minimum: str = manifest["minimum"]
    newest: str = manifest["maximumTested"]
    violations: list[str] = []

    if supported[0] != minimum:
        violations.append(f"{MANIFEST}: minimum {minimum} is not the first supported")
    if supported[-1] != newest:
        violations.append(f"{MANIFEST}: maximumTested {newest} is not the last supported")

    def read(relative: str) -> str | None:
        path = root / relative
        if not path.is_file():
            violations.append(f"{relative}: missing, so its versions cannot be checked")
            return None
        return path.read_text(encoding="utf-8")

    # Lists written out in full. Each is a literal a person edits, so the check
    # is that the exact rendering the file uses is present and complete.
    renderings: tuple[tuple[str, str], ...] = (
        (".github/workflows/dotnet-tmux.yml", "tmux: [{}]".format(
            ", ".join(f"'{version}'" for version in supported))),
        ("eng/tmux/run-matrix.sh", "REQUIRED_VERSIONS=({})".format(
            " ".join(supported))),
        ("eng/tmux/run-matrix.sh", 'tmuxVersions:[{}]'.format(
            ",".join(f'"{version}"' for version in supported))),
        ("eng/tmux/build-version.sh", "<{}|master>".format(
            "|".join(buildable(supported, manifest["transition"])))),
    )
    for relative, rendering in renderings:
        text = read(relative)
        if text is not None and rendering not in text:
            violations.append(f"{relative}: does not carry {rendering}")

    # The floor and the newest tested version are what the prose and the
    # runtime promise. A document naming a different one is a false claim.
    claims: tuple[tuple[str, tuple[str, ...]], ...] = (
        ("src/LibTmux/Constants/TmuxConstants.cs", (minimum, newest)),
        ("README.md", (minimum, newest)),
        ("src/LibTmux/README.md", (minimum, newest)),
        ("src/LibTmux.Mcp/README.md", (minimum, newest)),
        ("docs/README.md", (minimum, newest)),
        (".github/CONTRIBUTING.md", (minimum,)),
        (".github/ISSUE_TEMPLATE/bug_report.yml", (newest,)),
    )
    for relative, expected in claims:
        text = read(relative)
        if text is None:
            continue
        for version in expected:
            if version not in text:
                violations.append(f"{relative}: does not name tmux {version}")

    return violations


def main(argv: list[str] | None = None) -> int:
    """Report whether every version list agrees with the manifest."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parents[2],
        help="the repository root holding eng/tmux/versions.json",
    )
    arguments = parser.parse_args(argv)
    violations = verify(arguments.root)
    for violation in violations:
        print(violation)

    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
