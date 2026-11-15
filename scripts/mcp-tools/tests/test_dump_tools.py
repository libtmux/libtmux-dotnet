"""Prove the tool reference renders what a server advertises, and that a stale one fails."""

from __future__ import annotations

import pathlib

import pytest

import dump_tools


def test_a_description_collapses_to_its_first_sentence() -> None:
    assert dump_tools._one_line("Lists panes.   Takes a\nsession. More.") == "Lists panes."
    assert dump_tools._one_line("No stop") == "No stop"


@pytest.mark.parametrize(
    ("schema", "expected"),
    [
        ({"type": "string"}, "`name`: string"),
        ({"type": ["string", "null"]}, "`name`: string?"),
        ({"type": "integer", "minimum": 1, "maximum": 5}, "`name`: integer from 1 to 5"),
        ({"type": "integer", "minimum": 1}, "`name`: integer at least 1"),
        ({"enum": ["a", "b"]}, '`name`: "a" or "b"'),
        ({"type": "array", "items": {"type": "string"}}, "`name`: string[]"),
        ({"type": "boolean", "default": False}, "`name`: boolean = false"),
    ],
)
def test_a_parameter_renders_its_type_bounds_and_default(
    schema: dict[str, object], expected: str
) -> None:
    assert dump_tools._parameter("name", schema) == expected


def test_the_dotnet_environment_names_the_sdk_directory(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    dotnet = tmp_path / "dotnet"
    dotnet.write_text("", encoding="utf-8")
    monkeypatch.setenv("DOTNET_ROOT", str(tmp_path))

    assert dump_tools._dotnet_environment() == {"DOTNET_ROOT": str(tmp_path)}


def test_a_missing_build_is_a_clear_error(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(dump_tools, "REPO", pathlib.Path("/nonexistent"))

    with pytest.raises(SystemExit, match=r"build LibTmux\.Mcp in Release first"):
        dump_tools._binary()
