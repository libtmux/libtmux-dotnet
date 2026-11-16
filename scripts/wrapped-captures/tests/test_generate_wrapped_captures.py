"""Prove the generator records real captures that the unit tests can pair."""

from __future__ import annotations

import json
import pathlib
import random
import shlex
import shutil

import pytest

import generate_wrapped_captures as generator

TMUX = shutil.which("tmux")
FIXTURE = pathlib.Path(__file__).parents[3] / "tests" / "Fixtures" / "wrapped-captures.json"


def test_patterns_are_the_words_and_the_trimmed_lines() -> None:
    assert generator.patterns_of(["ab  cd ", "  "]) == ["ab", "ab  cd", "cd"]


def test_random_screens_repeat_for_one_seed_and_fit_the_pane() -> None:
    first = [generator.random_screen(random.Random(7)) for _ in range(5)]
    second = [generator.random_screen(random.Random(7)) for _ in range(5)]

    assert first == second
    assert all(len(lines) <= generator.HEIGHT for lines in first)


def test_a_pane_command_keeps_every_path_as_one_word() -> None:
    command = generator.pane_command(
        "/opt/tmux dir/tmux", "/tmp/it's/sock", "/tmp/it's/screen", "c1"
    )

    lexer = shlex.shlex(command, posix=True, punctuation_chars=";")
    lexer.whitespace_split = True
    words = list(lexer)

    assert words == [
        "cat",
        "/tmp/it's/screen",
        ";",
        "/opt/tmux dir/tmux",
        "-S",
        "/tmp/it's/sock",
        "wait-for",
        "-S",
        "c1",
        ";",
        "sleep",
        "600",
    ]


def test_the_command_line_names_its_arguments(capsys: pytest.CaptureFixture[str]) -> None:
    with pytest.raises(SystemExit) as stopped:
        generator.main([])

    assert stopped.value.code == 2
    assert "output" in capsys.readouterr().err


def test_the_committed_fixture_has_the_shape_the_generator_writes() -> None:
    document = json.loads(FIXTURE.read_text(encoding="utf-8"))

    assert document["width"] == generator.WIDTH
    for screen in document["screens"]:
        assert set(screen) == {"name", "lines", "patterns", "rows", "joined", "tmux"}
        assert len(screen["rows"]) == generator.HEIGHT


@pytest.mark.skipif(TMUX is None, reason="needs a tmux on PATH")
def test_a_real_tmux_yields_paired_rows_and_joined_lines() -> None:
    assert TMUX is not None
    shapes = {"plain wrapped line": generator.SHAPES["plain wrapped line"]}

    (wrapped,) = generator.generate([TMUX], random_count=0, shapes=shapes)

    assert wrapped["rows"][0] == "abcdefghijklmnopqNEE"
    assert wrapped["rows"][1] == "DLE_ONE_here"
    assert wrapped["joined"][0] == "abcdefghijklmnopqNEEDLE_ONE_here"
    assert len(wrapped["rows"]) == generator.HEIGHT
