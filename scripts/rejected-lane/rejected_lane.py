#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Run the minimum-version tests against a tmux below the floor.

A daemon below the supported minimum must be refused before it is touched. The
lane builds the rejected release as the daemon and the supported 3.3a as the
client, and runs `MinimumTmuxVersionTests`. Which of those tests apply depends
on the release:

- The control client needs 3.7, which this lane does not install, so its hook
  test runs in the matrix.
- A client older than 3.2 rejects `-N`, so the two tests that run the rejected
  build as a working client do not apply to a build before 3.2, and the tests of
  what only such a build does do not apply to the others.

```console
$ uv run scripts/rejected-lane/rejected_lane.py --version 3.1c
```
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import typing as t

ASSEMBLY = "tests/LibTmux.IntegrationTests/bin/Release/net10.0/LibTmux.IntegrationTests.dll"
TEST_CLASS = "LibTmux.IntegrationTests.Versioning.MinimumTmuxVersionTests"
NEEDS_THE_CONTROL_CLIENT = "*Attaching_and_watching_through_a_control_client_fires_no_hook"
BEFORE_3_2 = ("3.0a", "3.1c")


def skipped_filters(version: str) -> list[str]:
    """Return the test-name filters that do not apply to a rejected release.

    >>> skipped_filters("3.1c")
    ['*reports_a_different_version', '*executable_that_would_start_it_is_below_the_minimum']
    >>> skipped_filters("3.2a")
    ['*before_3_2*']
    """
    if version in BEFORE_3_2:
        return [
            "*reports_a_different_version",
            "*executable_that_would_start_it_is_below_the_minimum",
        ]
    return ["*before_3_2*"]


def test_command(version: str) -> list[str]:
    """Return the `dotnet test` command for one rejected release."""
    return [
        "dotnet",
        "test",
        "--test-modules",
        ASSEMBLY,
        "--filter-class",
        TEST_CLASS,
        "--filter-not-method",
        NEEDS_THE_CONTROL_CLIENT,
        *skipped_filters(version),
        "--fail-skips",
        "on",
        "--minimum-expected-tests",
        "10",
    ]


def main(argv: t.Sequence[str] | None = None) -> int:
    """Run the lane's tests and answer their exit status."""
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n\n")[0])
    parser.add_argument("--version", required=True, help="the rejected tmux release, such as 3.1c")
    options = parser.parse_args(argv)
    return subprocess.run(test_command(options.version), check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
