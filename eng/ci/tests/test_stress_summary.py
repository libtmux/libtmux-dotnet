"""Tests for the stress run's per-test failure counter."""

from __future__ import annotations

import csv
import pathlib
import subprocess
import sys

SCRIPT = pathlib.Path(__file__).parents[1] / "stress_summary.py"


def run_file(directory: pathlib.Path, number: int, failing: set[str]) -> None:
    """Write one JUnit run holding a steady test and the named failures."""
    cases = "".join(
        f'<testcase name="{name}">'
        + ("<failure message='x'/>" if name in failing else "")
        + "</testcase>"
        for name in ("Steady", "Flaky")
    )
    (directory / f"run-{number}.xml").write_text(
        f"<testsuites><testsuite>{cases}</testsuite></testsuites>"
    )


def summarize(directory: pathlib.Path) -> subprocess.CompletedProcess[str]:
    """Run the script over a directory of runs."""
    return subprocess.run(
        [
            sys.executable, str(SCRIPT), "--results", str(directory),
            "--label", "lane", "--csv", str(directory / "out.csv"),
        ],
        capture_output=True, text=True, check=False,
    )


def test_counts_a_test_that_fails_in_some_runs_only(tmp_path):
    run_file(tmp_path, 1, {"Flaky"})
    run_file(tmp_path, 2, set())
    run_file(tmp_path, 3, {"Flaky"})
    result = summarize(tmp_path)
    rows = {
        row["test"]: row
        for row in csv.DictReader((tmp_path / "out.csv").open())
    }
    assert result.returncode == 0
    assert (rows["Flaky"]["runs"], rows["Flaky"]["failures"]) == ("3", "2")
    assert rows["Steady"]["failures"] == "0"
    assert "`Flaky` | 2 | 3" in result.stdout
    assert "`Steady`" not in result.stdout


def test_a_clean_control_reports_no_failures(tmp_path):
    run_file(tmp_path, 1, set())
    result = summarize(tmp_path)
    assert result.returncode == 0
    assert "Every test passed in every run." in result.stdout


def test_a_directory_without_results_is_an_error_not_a_green_table(tmp_path):
    result = summarize(tmp_path)
    assert result.returncode == 2
    assert "no test results" in result.stderr
