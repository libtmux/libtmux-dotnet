"""Prove the tool reference renders what a server advertises, and that a stale one fails."""

from __future__ import annotations

import pathlib
import stat
import sys
import threading
import time

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


def _stand_in_server(tmp_path: pathlib.Path, body: str) -> pathlib.Path:
    """Write an executable Python program that stands in for the MCP server."""
    server = tmp_path / "server"
    server.write_text(f"#!{sys.executable}\n{body}", encoding="utf-8")
    server.chmod(server.stat().st_mode | stat.S_IXUSR)
    return server


def test_a_silent_server_ends_the_run_at_the_deadline(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A server that never writes must not hold the run past its deadline."""
    server = _stand_in_server(tmp_path, "import time\ntime.sleep(20)\n")
    monkeypatch.setattr(dump_tools, "_binary", lambda: server)
    monkeypatch.setattr(dump_tools, "_dotnet_environment", dict)
    monkeypatch.setattr(dump_tools, "DEADLINE_SECONDS", 1.0)
    answers: list[dict[int, dict]] = []
    runner = threading.Thread(target=lambda: answers.append(dump_tools._ask()), daemon=True)

    started = time.monotonic()
    runner.start()
    runner.join(timeout=8)
    elapsed = time.monotonic() - started

    assert not runner.is_alive(), "_ask is still blocked on a server that never writes"
    assert answers == [{}]
    assert elapsed < 8


def test_every_answer_is_collected_by_request_id(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The server answers each request that carries an id, and ignores notifications."""
    server = _stand_in_server(
        tmp_path,
        "import json, sys\n"
        "for line in sys.stdin:\n"
        "    frame = json.loads(line)\n"
        "    if 'id' in frame:\n"
        "        print(json.dumps({'jsonrpc': '2.0', 'id': frame['id'], 'result': {'ok': frame['id']}}),"
        " flush=True)\n",
    )
    monkeypatch.setattr(dump_tools, "_binary", lambda: server)
    monkeypatch.setattr(dump_tools, "_dotnet_environment", dict)

    answers = dump_tools._ask()

    assert answers == {id_: {"ok": id_} for id_ in (1, 2, 3, 4, 5)}


def test_a_server_that_exits_at_once_answers_nothing(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A server that dies before it reads leaves no answers, not a traceback."""
    server = _stand_in_server(tmp_path, "raise SystemExit(3)\n")
    monkeypatch.setattr(dump_tools, "_binary", lambda: server)
    monkeypatch.setattr(dump_tools, "_dotnet_environment", dict)

    assert dump_tools._ask() == {}
