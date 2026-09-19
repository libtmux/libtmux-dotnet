"""Refuse a hand-rolled tmux version comparison outside the capability table.

TmuxCapabilities is the one place a tmux version boundary is supposed to
live: a named capability, an interval, and (via version-deltas.json) a row a
real tmux proves. A comparison built straight from a fresh
``TmuxVersion.Parse("...")`` literal anywhere else in the library re-derives
that answer by hand, next to whichever call it happens to gate -- and can
drift from the table it was meant to reproduce.

That drift shipped: ``Pane.DisplayMessageAsync`` and
``Window.DisplayMessageAsync`` each compared against
``TmuxVersion.Parse("3.3a")`` while the capability the check stood in for,
``display_message_client``, had already been ``3.3`` in the table the whole
time. Nothing caught the two answers disagreeing until someone went and
checked tmux's own history by hand. This is that check: it does not know
which boundary is correct, only that a second, uninspected place to state
one is itself the defect.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

SOURCE_ROOT = pathlib.Path(__file__).parents[2] / "src" / "LibTmux"

#: A version-comparison operator directly against a fresh Parse literal, in
#: either order. Matches across the line break a wrapped condition adds.
COMPARISON = re.compile(
    r"[\w.!?]*[Vv]ersion\w*\s*(?:<=|>=|==|!=|<|>)\s*"
    r'TmuxVersion\.Parse\("[^"]+"\)'
    r"|"
    r'TmuxVersion\.Parse\("[^"]+"\)\s*(?:<=|>=|==|!=|<|>)\s*'
    r"[\w.!?]*[Vv]ersion\w*",
)

#: Files where the library's OWN version boundaries live. A comparison here
#: is the table, not a duplicate of it.
ALLOWED_FILES = frozenset(
    {
        "Versioning/TmuxCapabilities.cs",
    },
)

#: Exact matched text, keyed by the relative path it is allowed in. Neither
#: is a capability the ledger tracks. The JSON-layout entry recognises which
#: shape of an already-dispatched layout string the connected tmux can have
#: produced, checked here because sending the wrong shape to an old tmux
#: crashes its whole server (see the comment above ValidateLayout). The
#: mirrored-preset entry decides which preset names this client accepts
#: before a request is even sent, because those presets did not exist before
#: tmux 3.5. Each was verified against tmux's own tagged source, not against
#: this project's CI matrix; a third one added anywhere in the file still
#: trips this check, by exact text, so growing the allowlist is a deliberate
#: edit a reviewer sees.
ALLOWED_MATCHES = frozenset(
    {
        ("Window.Layout.cs", 'version >= TmuxVersion.Parse("3.5")'),
        ("Window.Layout.cs", 'jsonVersion >= TmuxVersion.Parse("3.8")'),
    },
)


def verify(root: pathlib.Path) -> list[str]:
    """Return one message per hand-rolled version comparison found.

    Parameters
    ----------
    root : pathlib.Path
        Library source root (``src/LibTmux``).

    Returns
    -------
    list[str]
        Violations, empty when every comparison is either the capability
        table itself or an explicitly reviewed exception.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as tmp:
    ...     root = pathlib.Path(tmp)
    ...     _ = (root / "Clean.cs").write_text(
    ...         'Supports(owner, "display_message_client")', encoding="utf-8"
    ...     )
    ...     verify(root)
    []
    """
    violations: list[str] = []
    for path in sorted(root.rglob("*.cs")):
        relative = path.relative_to(root).as_posix()
        if relative in ALLOWED_FILES:
            continue

        text = path.read_text(encoding="utf-8")
        for match in COMPARISON.finditer(text):
            found = re.sub(r"\s+", " ", match.group(0))
            if (relative, found) in ALLOWED_MATCHES:
                continue

            line = text.count("\n", 0, match.start()) + 1
            violations.append(
                f"{relative}:{line} compares against a version literal outside "
                f"TmuxCapabilities: {found!r}. Add the boundary as a named "
                "capability (or an interval on an existing one) and gate on "
                "TmuxCapabilities.IsSupported / Supports(...) instead, or add "
                "it to ALLOWED_MATCHES in verify_version_literals.py with the "
                "tag evidence that makes it not a capability.",
            )

    return violations


def main() -> int:
    """Validate the checked-in library source.

    Returns
    -------
    int
        Process exit code.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, default=SOURCE_ROOT)
    args = parser.parse_args()

    violations = verify(args.root)
    if violations:
        for violation in violations:
            print(violation, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
