"""Prove the workspace command check notices a missing command or import source."""

from __future__ import annotations

import json
import pathlib
import typing as t

import pytest

import verify_workspace_commands as check


def tree(commands: int = 9, sources: int = 2) -> dict[str, t.Any]:
    """Return a command tree with the given number of commands and import sources."""
    imports = {"name": "import", "commands": [{"name": f"source{n}"} for n in range(sources)]}
    return {"commands": [imports, *({"name": f"command{n}"} for n in range(commands - 1))]}


def test_the_expected_tree_passes() -> None:
    assert check.verify(tree()) == []


@pytest.mark.parametrize(
    ("commands", "sources", "message"),
    [(8, 2, "expected 9 commands, found 8"), (9, 1, "expected 2 import sources, found 1")],
)
def test_a_lost_command_or_source_is_reported(commands: int, sources: int, message: str) -> None:
    assert check.verify(tree(commands, sources)) == [message]


def test_the_command_exits_non_zero_for_a_bad_tree(tmp_path: pathlib.Path) -> None:
    good, bad = tmp_path / "good.json", tmp_path / "bad.json"
    good.write_text(json.dumps(tree()), encoding="utf-8")
    bad.write_text(json.dumps(tree(commands=3)), encoding="utf-8")

    assert check.main([str(good)]) == 0
    assert check.main([str(bad)]) == 1
