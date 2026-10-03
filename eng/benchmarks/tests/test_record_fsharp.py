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


def test_the_record_says_what_else_shaped_the_run(tmp_path: pathlib.Path, monkeypatch) -> None:
    monkeypatch.setattr(record_fsharp, "git", lambda *arguments: "abcdef0123456789")
    monkeypatch.setattr(
        record_fsharp,
        "conditions",
        lambda: {"hypervisor": True, "governor": None, "cpusAvailable": 2, "loadAverage": [0.5, 1.25, 2.0]},
    )
    fast = report(tmp_path / "fast-report-full.json", "LibTmux.Benchmarks.Fast-20261003", [("Fold", "", [40.0])])

    record = record_fsharp.collect([fast], "3.7d", "2026-10-03")
    record["host"]["logicalCores"] = 4

    assert record["host"]["cpusAvailable"] == 2
    assert (
        "| **Conditions** | virtual machine; 2 of 4 logical cores available; "
        "load 0.5 / 1.25 / 2.0 (1, 5, 15 min) when recorded |"
    ) in record_fsharp.render(record)


def pushdown_record(pushed_ns: float, listed_ns: float, pushed_bytes: int, listed_bytes: int) -> dict:
    def case(route: str, median_ns: float, allocated: int) -> dict:
        return {"method": "FindTailPanes", "parameters": f"Route={route}", "median_ns": median_ns, "allocated_bytes": allocated}

    return {
        "classes": [
            {
                "name": "FSharpQueryPushdownBenchmarks",
                "cases": [case("pushdown", pushed_ns, pushed_bytes), case("list-then-filter", listed_ns, listed_bytes)],
            }
        ]
    }


def test_the_gate_passes_pushdown_that_narrows_the_listing() -> None:
    assert record_fsharp.gate(pushdown_record(9.0, 82.0, 1_000_000, 30_000_000)) == []


def test_the_gate_fails_pushdown_that_no_longer_narrows() -> None:
    failures = record_fsharp.gate(pushdown_record(60.0, 82.0, 30_000_000, 30_000_000))

    assert len(failures) == 2
    assert "1.4 times as fast" in failures[0]
    assert "no fewer than listing everything" in failures[1]


def test_the_gate_fails_a_record_without_the_pushdown_class() -> None:
    assert record_fsharp.gate({"classes": []}) == ["FSharpQueryPushdownBenchmarks is not in the record"]


def mirror_record(seen_ns: float, captured_ns: float) -> dict:
    record = pushdown_record(9.0, 82.0, 1_000_000, 30_000_000)
    record["classes"].append(
        {
            "name": "FSharpMirrorBenchmarks",
            "cases": [
                {"method": method, "parameters": "Sessions=16", "median_ns": median_ns, "allocated_bytes": 1}
                for method, median_ns in (("RenameUntilSeen", seen_ns), ("CaptureSnapshot", captured_ns))
            ],
        }
    )
    return record


def test_the_gate_passes_a_mirror_that_captures_once_per_change() -> None:
    assert record_fsharp.gate(mirror_record(55.0, 46.0)) == []


def test_the_gate_fails_a_mirror_that_captures_twice_per_change() -> None:
    # The medians a mirror made to capture twice measured with sixteen sessions.
    assert record_fsharp.gate(mirror_record(93.36, 50.25)) == [
        "FSharpMirrorBenchmarks Sessions=16: a rename seen through the mirror costs 1.9 captures; the gate allows 1.65"
    ]
