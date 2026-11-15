#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "pytest>=8.3",
#     "pytest-asyncio>=1.0",
#     "tomlkit>=0.13",
#     "PyYAML>=6,<7",
#     "ruff==0.17.5",
#     "ty==0.0.104",
# ]
# ///
"""Run the format, lint, type and test checks over ``scripts/``.

CI and a contributor run the same four commands, against dependencies pinned in
the lock beside this file. Naming pytest or ruff on the command line resolves
whatever version exists that morning, which turns an unrelated release into a
red gate.

Refresh the lock deliberately:

```console
$ uv lock --script scripts/run_checks.py
```

Check formatting without changing a file:

```console
$ uv run --locked --script scripts/run_checks.py format
```

Lint, then type check:

```console
$ uv run --locked --script scripts/run_checks.py lint
```

```console
$ uv run --locked --script scripts/run_checks.py types
```

Run the tests and doctests. Arguments after ``tests`` go through to pytest:

```console
$ uv run --locked --script scripts/run_checks.py tests -k swap
```
"""

from __future__ import annotations

import pathlib
import subprocess
import sys

import pytest

SCRIPTS = pathlib.Path(__file__).resolve().parent
COMMANDS = ("format", "lint", "types", "tests")


def tool_command(name: str, *arguments: str) -> list[str]:
    """Return the command that runs one installed tool from this environment.

    >>> tool_command("ruff", "check", ".")[1:]
    ['check', '.']
    """
    executable = pathlib.Path(sys.executable).parent / name
    return [str(executable), *arguments]


def names_target(arguments: list[str]) -> bool:
    """Say whether pytest arguments already pick a file, directory or test.

    >>> names_target(["-k", "swap"])
    False
    >>> names_target([".", "-q"])
    True
    """
    return any(
        not argument.startswith("-") and pathlib.Path(argument.split("::")[0]).exists()
        for argument in arguments
    )


def static_command(command: str) -> list[str]:
    """Return the tool command behind ``format``, ``lint`` or ``types``.

    >>> static_command("format")[1:]
    ['format', '--check', '.']
    >>> static_command("types")[1:2]
    ['check']
    """
    if command == "format":
        return tool_command("ruff", "format", "--check", ".")
    if command == "lint":
        return tool_command("ruff", "check", ".")
    if command == "types":
        return tool_command("ty", "check", "--python", sys.executable, ".")
    message = f"no static check named {command!r}"
    raise ValueError(message)


def main(argv: list[str] | None = None) -> int:
    """Run one check and answer its exit code."""
    arguments = sys.argv[1:] if argv is None else argv
    if not arguments or arguments[0] not in COMMANDS:
        print(f"usage: run_checks.py {{{','.join(COMMANDS)}}} [pytest arguments]", file=sys.stderr)
        return 2
    command, rest = arguments[0], arguments[1:]
    if command == "tests":
        return int(pytest.main(rest if names_target(rest) else [str(SCRIPTS), *rest]))
    if rest:
        print(f"{command} takes no arguments", file=sys.stderr)
        return 2
    return subprocess.run(static_command(command), cwd=SCRIPTS, check=False).returncode


if __name__ == "__main__":
    raise SystemExit(main())
