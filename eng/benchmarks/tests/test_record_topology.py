"""Reject topology records that cannot support a mode comparison."""

import json
import pathlib
import runpy

import pytest


RECORDER = pathlib.Path(__file__).parents[1] / "record_topology.py"
MODES = (
    "serial-process",
    "concurrent-process",
    "process-chain",
    "serial-control",
    "concurrent-control",
)
TOPOLOGIES = (("small", 1, 1), ("medium", 4, 8), ("large", 16, 32))
PROCESSES = (24, 24, 1, 0, 0)


def fixture_rows(warmup_rounds=5, measured_rounds=20):
    rows = [{
        "kind": "run",
        "collectedUtc": "2026-09-27T04:03:27+00:00",
        "sourceCommit": "a" * 40,
        "sourceClean": True,
        "tmuxBinaryPath": "/tmp/tmux",
        "tmuxBinarySha256": "b" * 64,
        "tmuxVersion": "3.7d",
        "runtime": ".NET 10.0.11",
        "os": "Ubuntu 24.04",
        "architecture": "X64",
        "processors": 10,
        "cpuModel": "AMD EPYC 9B14",
        "settings": {
            "warmupRounds": warmup_rounds,
            "measuredRounds": measured_rounds,
            "itemsPerSample": 8,
            "concurrencyCap": 4,
        },
    }]
    for name, physical, placements in TOPOLOGIES:
        rows.append({
            "kind": "topology",
            "topology": name,
            "physicalWindows": physical,
            "placements": placements,
            "queryRows": placements,
            "distinctPaneIds": physical,
            "attachedControlClients": 1,
            "setupMs": 1.0,
        })
        for round_number in range(warmup_rounds + measured_rounds):
            for slot in range(len(MODES)):
                mode_index = (round_number + slot) % len(MODES)
                rows.append({
                    "kind": "sample",
                    "topology": name,
                    "physicalWindows": physical,
                    "placements": placements,
                    "round": round_number,
                    "slot": slot,
                    "mode": MODES[mode_index],
                    "warmup": round_number < warmup_rounds,
                    "status": "ok",
                    "validated": True,
                    "elapsedMs": float(round_number + slot + 1),
                    "processes": PROCESSES[mode_index],
                    "attachedControlClients": 1,
                    "usedControlClients": int(mode_index >= 3),
                    "logicalCommands": 24,
                    "maximumInFlight": 4 if mode_index in (1, 4) else 1,
                })
    rows.append({
        "kind": "summary", "status": "ok",
        "sampleCount": len(TOPOLOGIES) * (warmup_rounds + measured_rounds) * len(MODES),
    })
    return rows


def write_rows(path, rows):
    path.write_text("\n".join(json.dumps(row) for row in rows) + "\n")
    return path


def test_full_record_keeps_raw_order_and_measured_distribution(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    record = recorder["collect"](write_rows(tmp_path / "raw.ndjson", fixture_rows()))
    assert record["schema"] == "libtmux-topology-mode-record-v1"
    assert len(record["cases"]) == 15
    assert all(case["samples"] == 20 for case in record["cases"])
    assert record["method"]["warmupRounds"] == 5
    assert record["method"]["measuredRounds"] == 20
    assert record["method"]["interleaved"] is True
    assert record["rawSha256"]
    assert record["collectedUtc"] == "2026-09-27T04:03:27+00:00"
    assert record["processors"] == 10
    assert record["cpuModel"] == "AMD EPYC 9B14"
    assert record["runtime"] == ".NET 10.0.11"


def test_warmup_outliers_do_not_enter_reported_percentiles(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    measured = 0
    for row in rows:
        if row.get("topology") == "small" and row.get("mode") == "serial-process":
            if row["warmup"]:
                row["elapsedMs"] = 1_000_000.0
            else:
                measured += 1
                row["elapsedMs"] = float(measured)

    record = recorder["collect"](write_rows(tmp_path / "raw.ndjson", rows))
    case = next(case for case in record["cases"]
                if case["topology"] == "small" and case["mode"] == "serial-process")
    assert (case["medianMs"], case["p95Ms"], case["p99Ms"]) == pytest.approx((10.5, 19.05, 19.81))


def test_smoke_record_is_diagnostic_without_percentiles(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows(0, 1)
    record = recorder["collect"](write_rows(tmp_path / "smoke.ndjson", rows))
    assert record["schema"] == "libtmux-topology-smoke-v1"
    assert record["evidenceStatus"] == "diagnostic-smoke"
    assert record["cases"][0]["observedMs"] == 1.0
    assert "p95Ms" not in record["cases"][0]


def test_dirty_full_record_requires_explicit_exploratory_provenance(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    rows[0]["sourceClean"] = False
    path = write_rows(tmp_path / "raw.ndjson", rows)
    with pytest.raises(ValueError, match="dirty source"):
        recorder["collect"](path)

    with pytest.raises(ValueError, match="fingerprint"):
        recorder["collect"](
            path,
            exploratory=True,
            source_fingerprint_before="a" * 64,
            source_fingerprint_after="b" * 64,
        )


def test_matching_exploratory_fingerprints_mark_dirty_record(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    rows[0]["sourceClean"] = False
    record = recorder["collect"](
        write_rows(tmp_path / "raw.ndjson", rows),
        exploratory=True,
        source_fingerprint_before="a" * 64,
        source_fingerprint_after="a" * 64,
    )
    assert record["evidenceStatus"] == "exploratory"
    assert record["sourceTreeFingerprint"] == "a" * 64


@pytest.mark.parametrize("corruption,expected", [
    ("placement", "placement"),
    ("query rows", "query row"),
    ("order", "order"),
    ("processes", "process"),
])
def test_corrupted_sample_is_rejected(tmp_path, corruption, expected):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    if corruption == "placement":
        sample = next(row for row in rows if row["kind"] == "sample")
        sample["placements"] += 1
    elif corruption == "query rows":
        topology = next(row for row in rows if row.get("topology") == "medium")
        topology["queryRows"] -= 1
    elif corruption == "order":
        sample = next(row for row in rows if row["kind"] == "sample")
        sample["mode"] = "concurrent-control"
    else:
        sample = next(row for row in rows if row["kind"] == "sample")
        sample["processes"] += 1

    with pytest.raises(ValueError, match=expected):
        recorder["collect"](write_rows(tmp_path / "raw.ndjson", rows))
