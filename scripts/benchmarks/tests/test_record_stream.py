"""Keep stream distributions tied to complete, identified raw samples."""

import hashlib
import json
import pathlib
import runpy
import subprocess
import sys

import pytest


RECORDER = pathlib.Path(__file__).parents[1] / "record_topology.py"


def fixture_rows(*, smoke=False):
    measured = 1 if smoke else 20
    warmups = 0 if smoke else 2
    auxiliary = 1 if smoke else 5
    rows = [{
        "kind": "run",
        "collectedUtc": "2026-09-27T04:13:06+00:00",
        "sourceCommit": "a" * 40,
        "sourceClean": True,
        "tmuxVersion": "3.7d",
        "runtime": ".NET 10.0.11",
        "os": "Ubuntu 24.04",
        "architecture": "X64",
        "processors": 10,
        "settings": {
            "samplesPerPayload": measured,
            "warmupsPerPayload": warmups,
            "latencyPayloadBytes": 64,
            "throughputPayloadBytes": 262144,
            "overflowSamples": auxiliary,
            "disposalSamples": auxiliary,
            "overflowBufferCapacity": 4 if smoke else 8,
            "overflowNotifications": 16 if smoke else 32,
            "runBudget": "00:08:00",
            "sampleBudget": "00:00:10",
        },
    }]
    for scenario, payload in (("latency", 64), ("throughput", 262144)):
        for index in range(warmups + measured):
            first = 1_000_000.0 if index < warmups else float(index - warmups + 1)
            rows.append({
                "kind": "sample", "scenario": scenario, "sample": index,
                "warmup": index < warmups, "status": "ok",
                "measurement": {
                    "payloadBytes": payload,
                    "firstOutputMs": first,
                    "completeOutputMs": first + 1,
                    "endToEndBytesPerSecond": 1000.0 + index,
                    "outputEvents": 2,
                    "processAllocatedBytes": 100 + index,
                },
            })
    for index in range(auxiliary):
        rows.append({
            "kind": "sample", "scenario": "notification-buffer-overflow",
            "sample": index, "warmup": False, "status": "ok",
            "measurement": {
                "producedRenameCommands": 16 if smoke else 32,
                "bufferCapacityEvents": 4 if smoke else 8,
                "count": 5, "totalDropped": 5, "barrierMs": float(index + 1),
                "processAllocatedBytes": 100, "processHeapBeforeBytes": 1000,
                "processHeapAfterBytes": 1100, "controlStillRunning": True,
            },
        })
    for index in range(auxiliary):
        rows.append({
            "kind": "sample", "scenario": "pending-reader-disposal",
            "sample": index, "warmup": False, "status": "ok",
            "measurement": {
                "disposalMs": float(index + 1),
                "readerCompletionMs": float(index + 2),
                "sawExit": True, "readerPendingAtTrigger": True,
                "controlStillRunning": False, "serverStillRunning": True,
            },
        })
    return rows


def write_rows(path, rows):
    path.write_text("\n".join(json.dumps(row) for row in rows) + "\n")
    return path


def test_stream_record_summarizes_measured_samples_and_retains_raw_identity(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    path = write_rows(tmp_path / "stream.ndjson", fixture_rows())
    record = recorder["collect_stream"](path)
    assert record["schema"] == "libtmux-stream-record-v1"
    assert record["evidenceStatus"] == "clean-source"
    assert record["sourceCommit"] == "a" * 40
    assert record["rawSha256"] == hashlib.sha256(path.read_bytes()).hexdigest()
    assert record["method"]["warmupsPerPayload"] == 2
    assert record["method"]["samplesPerPayload"] == 20
    assert record["rawSampleCount"] == 54
    latency = next(case for case in record["cases"] if case["scenario"] == "latency")
    assert latency["samples"] == 20
    assert latency["measurements"]["firstOutputMs"] == pytest.approx({
        "median": 10.5, "p95": 19.05, "p99": 19.81,
    })


def test_dirty_stream_record_requires_matching_exploratory_fingerprints(tmp_path):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    rows[0]["sourceClean"] = False
    path = write_rows(tmp_path / "stream.ndjson", rows)
    with pytest.raises(ValueError, match="dirty source"):
        recorder["collect_stream"](path)
    with pytest.raises(ValueError, match="fingerprint"):
        recorder["collect_stream"](
            path, exploratory=True,
            source_fingerprint_before="b" * 64,
            source_fingerprint_after="c" * 64,
        )
    record = recorder["collect_stream"](
        path, exploratory=True,
        source_fingerprint_before="b" * 64,
        source_fingerprint_after="b" * 64,
    )
    assert record["evidenceStatus"] == "exploratory"
    assert record["sourceTreeFingerprint"] == "b" * 64


def test_stream_smoke_cli_reports_observations_without_percentiles(tmp_path):
    source = write_rows(tmp_path / "smoke.ndjson", fixture_rows(smoke=True))
    output = tmp_path / "smoke.record.json"
    subprocess.run([
        sys.executable, str(RECORDER), "--probe", "stream",
        "--input", str(source), "--output", str(output),
    ], check=True, capture_output=True, text=True)
    record = json.loads(output.read_text())
    assert record["schema"] == "libtmux-stream-smoke-v1"
    assert record["evidenceStatus"] == "diagnostic-smoke"
    assert record["cases"][0]["measurements"]["firstOutputMs"] == {"observed": 1.0}


@pytest.mark.parametrize("corruption", ["missing", "failed", "wrong-payload"])
def test_incomplete_or_invalid_stream_sample_is_rejected(tmp_path, corruption):
    recorder = runpy.run_path(str(RECORDER))
    rows = fixture_rows()
    if corruption == "missing":
        del rows[1]
    elif corruption == "failed":
        rows[1]["status"] = "failed"
    else:
        rows[1]["measurement"]["payloadBytes"] = 1
    with pytest.raises(ValueError):
        recorder["collect_stream"](write_rows(tmp_path / "stream.ndjson", rows))
