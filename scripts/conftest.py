"""Fixtures shared by the script tests."""

from __future__ import annotations

import os
import pathlib

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
API_INVENTORY = ROOT / "artifacts" / "api-inventory.json"

INVENTORY_COMMANDS = (
    "dotnet build LibTmux.slnx --configuration Release",
    "dotnet run --project eng/LibTmux.Engineering --configuration Release --no-build"
    " -- api-inventory . artifacts/api-inventory.json",
)
INVENTORY_MISSING = (
    "artifacts/api-inventory.json is missing; build the solution, then write it "
    f"from the repository root with `{INVENTORY_COMMANDS[0]}` and `{INVENTORY_COMMANDS[1]}`"
)


@pytest.fixture(scope="session")
def api_inventory_path() -> pathlib.Path:
    """Return the compiler inventory a build writes, or say how to write it.

    A checkout has no build output, so a run there skips the tests that read the
    inventory and names the commands that write it. CI builds first, so there a
    missing inventory fails the run rather than skipping what it should check.
    """
    if API_INVENTORY.is_file():
        return API_INVENTORY
    if os.environ.get("CI"):
        pytest.fail(INVENTORY_MISSING, pytrace=False)
    pytest.skip(INVENTORY_MISSING)


def pytest_terminal_summary(terminalreporter: pytest.TerminalReporter) -> None:
    """Say once why tests were skipped for want of the compiler inventory."""
    skipped = terminalreporter.stats.get("skipped", [])
    if any(INVENTORY_MISSING in str(getattr(report, "longrepr", "")) for report in skipped):
        terminalreporter.write_line(INVENTORY_MISSING)
