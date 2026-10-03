"""Tests for record_fsharp.py: it keeps each class's distribution in a unit that reads."""

from __future__ import annotations

import importlib.util
import json
import pathlib

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "record_fsharp.py"
SPEC = importlib.util.spec_from_file_location("record_fsharp", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
record_fsharp = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(record_fsharp)


def report(path: pathlib.Path, title: str, cases: list[tuple[str, str, list[float]]]) -> pathlib.Path:
    path.write_text(
        json.dumps(
            {
                "Title": title,
                "HostEnvironmentInfo": {"OsVersion": "Linux", "ProcessorName": "cpu", "RuntimeVersion": ".NET 10"},
                "Benchmarks": [
                    {
                        "Method": method,
                        "Parameters": parameters,
                        "Memory": {"BytesAllocatedPerOperation": 1200},
                        "Statistics": {"OriginalValues": values},
                    }
                    for method, parameters, values in cases
                ],
            }
        ),
        encoding="utf-8",
    )
    return path


def test_each_class_reads_in_the_unit_its_median_needs(tmp_path: pathlib.Path, monkeypatch) -> None:
    monkeypatch.setattr(record_fsharp, "git", lambda *arguments: "abcdef0123456789")
    fast = report(tmp_path / "fast-report-full.json", "LibTmux.Benchmarks.Fast-20261003", [("Fold", "", [40.0, 42.0, 44.0])])
    slow = report(
        tmp_path / "slow-report-full.json",
        "LibTmux.Benchmarks.Slow-20261003",
        [("FindTailPanes", "Route=pushdown", [4_000_000.0, 5_000_000.0, 6_000_000.0])],
    )

    rendered = record_fsharp.render(record_fsharp.collect([fast, slow], "3.7d", "2026-10-03"))

    assert "## Fast" in rendered and "| Case | Median ns |" in rendered
    assert "| Fold | 42 | 43.8 | 44 | 1,200 B | 3 |" in rendered
    assert "## Slow" in rendered and "| Case | Median ms |" in rendered
    assert "| FindTailPanes (Route=pushdown) | 5 | 5.9 | 6 | 1,200 B | 3 |" in rendered


def test_a_case_far_below_its_class_unit_reads_without_an_exponent() -> None:
    assert record_fsharp.scaled(40.0, 1_000_000.0) == "0.0000400"
    assert record_fsharp.scaled(1_500.0, 1_000.0) == "1.5"
