"""Prove each rejected release skips the tests that do not apply to it."""

from __future__ import annotations

import os
import pathlib
import stat
import sys

import pytest

import rejected_lane as lane


@pytest.mark.parametrize("version", ["3.0a", "3.1c"])
def test_a_build_before_3_2_skips_the_two_tests_that_run_it_as_a_client(version: str) -> None:
    command = lane.test_command(version)

    skipped = command[command.index("--filter-not-method") + 1 : command.index("--fail-skips")]
    assert skipped == [
        lane.NEEDS_THE_CONTROL_CLIENT,
        "*reports_a_different_version",
        "*executable_that_would_start_it_is_below_the_minimum",
    ]


@pytest.mark.parametrize("version", ["3.2a", "3.3"])
def test_a_later_rejected_build_skips_what_only_an_earlier_one_does(version: str) -> None:
    command = lane.test_command(version)

    skipped = command[command.index("--filter-not-method") + 1 : command.index("--fail-skips")]
    assert skipped == [lane.NEEDS_THE_CONTROL_CLIENT, "*before_3_2*"]


def test_a_skipped_test_fails_the_lane_and_fewer_tests_than_expected_do_too() -> None:
    command = lane.test_command("3.3")

    assert command[command.index("--fail-skips") + 1] == "on"
    assert command[command.index("--minimum-expected-tests") + 1] == "10"


def test_the_exit_status_of_the_tests_is_the_lane_status(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    dotnet = tmp_path / "dotnet"
    dotnet.write_text(f"#!{sys.executable}\nraise SystemExit(5)\n", encoding="utf-8")
    dotnet.chmod(dotnet.stat().st_mode | stat.S_IXUSR)
    monkeypatch.setenv("PATH", f"{tmp_path}{os.pathsep}{os.environ['PATH']}")

    assert lane.main(["--version", "3.3"]) == 5
