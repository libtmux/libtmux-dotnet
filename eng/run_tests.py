#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = ["pytest>=8.3", "tomlkit>=0.13"]
# ///
"""Run the engineering test suite against locked dependencies.

The validators are the gate for the parity, capability, API and MCP
documents, so what they run against has to be as fixed as the .NET side
already is. Naming pytest on the command line resolves whatever version
exists that morning, which turns an unrelated release into a red gate.

The lock beside this file pins that resolution. Refresh it deliberately:

```console
$ uv lock --script eng/run_tests.py
```

Arguments go through to pytest:

```console
$ uv run eng/run_tests.py -k swap
```
"""

from __future__ import annotations

import pathlib
import sys

import pytest


def main(argv: list[str] | None = None) -> int:
    """Run pytest over ``eng`` and answer its exit code."""
    arguments = sys.argv[1:] if argv is None else argv
    engineering = pathlib.Path(__file__).resolve().parent

    # Some tests import their subject as ``eng.<area>`` rather than loading it
    # by path, which needs the repository root importable. Running pytest as a
    # module got that from the working directory; a script has to say it.
    root = str(engineering.parent)
    if root not in sys.path:
        sys.path.insert(0, root)

    return int(pytest.main([str(engineering), *arguments]))


if __name__ == "__main__":
    raise SystemExit(main())
