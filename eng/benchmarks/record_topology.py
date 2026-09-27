#!/usr/bin/env python3
"""Validate interleaved topology samples and retain their distribution."""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import pathlib
import statistics

MODES = (
    "serial-process",
    "concurrent-process",
    "process-chain",
    "serial-control",
    "concurrent-control",
)
TOPOLOGIES = (("small", 1, 1), ("medium", 4, 8), ("large", 16, 32))
PROCESSES = (24, 24, 1, 0, 0)
STREAM_PAYLOAD_METRICS = (
    "firstOutputMs", "completeOutputMs", "endToEndBytesPerSecond",
    "outputEvents", "processAllocatedBytes",
)
STREAM_OVERFLOW_METRICS = (
    "count", "totalDropped", "barrierMs", "processAllocatedBytes",
    "processHeapBeforeBytes", "processHeapAfterBytes",
)
STREAM_DISPOSAL_METRICS = ("disposalMs", "readerCompletionMs")


def require(condition: bool, message: str) -> None:
    """Reject a sample rather than silently averaging it into a record."""
    if not condition:
        raise ValueError(message)


def percentile(values: list[float], fraction: float) -> float:
    """Interpolate between adjacent ordered samples."""
    ordered = sorted(values)
    position = fraction * (len(ordered) - 1)
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def nonnegative_number(value: object) -> bool:
    """Reject booleans and non-finite timings."""
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def validate_source_identity(
    run: dict,
    exploratory: bool,
    source_fingerprint_before: str | None,
    source_fingerprint_after: str | None,
) -> None:
    """Keep dirty benchmark records separate from clean-source evidence."""
    require(
        isinstance(run.get("sourceCommit"), str)
        and len(run["sourceCommit"]) == 40
        and all(character in "0123456789abcdef" for character in run["sourceCommit"]),
        "source commit is missing",
    )
    require(type(run.get("sourceClean")) is bool, "source cleanliness is missing")
    require(run["sourceClean"] or exploratory, "dirty source requires an exploratory record")
    if exploratory:
        require(
            isinstance(source_fingerprint_before, str)
            and len(source_fingerprint_before) == 64
            and all(character in "0123456789abcdef" for character in source_fingerprint_before)
            and source_fingerprint_after == source_fingerprint_before,
            "exploratory source fingerprints are missing or differ",
        )
    else:
        require(
            source_fingerprint_before is None and source_fingerprint_after is None,
            "source fingerprints require the exploratory option",
        )


def collect(
    path: pathlib.Path,
    *,
    exploratory: bool = False,
    source_fingerprint_before: str | None = None,
    source_fingerprint_after: str | None = None,
) -> dict:
    """Require complete topology, ordering and output checks before statistics."""
    raw = path.read_bytes()
    rows = [json.loads(line) for line in raw.splitlines() if line.strip()]
    require(bool(rows), "the topology probe has no rows")
    run = rows[0]
    require(run.get("kind") == "run", "the first row must identify the run")
    collected = run.get("collectedUtc")
    require(isinstance(collected, str), "run collection timestamp is missing")
    try:
        collected_at = datetime.datetime.fromisoformat(collected)
    except ValueError as error:
        raise ValueError("run collection timestamp is invalid") from error
    require(
        collected_at.tzinfo is not None
        and collected_at.utcoffset() == datetime.timedelta(0),
        "run collection timestamp must be UTC",
    )
    settings = run.get("settings", {})
    warmups = settings.get("warmupRounds")
    measured = settings.get("measuredRounds")
    require(
        (warmups, measured) in ((5, 20), (0, 1)),
        "the warmup/measured round counts are not a full run or smoke run",
    )
    require(settings.get("itemsPerSample") == 8, "the workload item count changed")
    require(settings.get("concurrencyCap") == 4, "the concurrency cap changed")
    validate_source_identity(run, exploratory, source_fingerprint_before, source_fingerprint_after)
    require(
        isinstance(run.get("tmuxBinaryPath"), str)
        and pathlib.Path(run["tmuxBinaryPath"]).is_absolute(),
        "tmux binary path is missing",
    )
    require(
        isinstance(run.get("tmuxBinarySha256"), str)
        and len(run["tmuxBinarySha256"]) == 64
        and all(character in "0123456789abcdef" for character in run["tmuxBinarySha256"]),
        "tmux binary hash is missing",
    )
    require(bool(run.get("tmuxVersion")), "tmux version is missing")
    for field in ("runtime", "os", "architecture"):
        require(isinstance(run.get(field), str) and bool(run[field]), f"{field} is missing")
    require(
        type(run.get("processors")) is int and run["processors"] > 0,
        "processor count is missing",
    )
    require(
        isinstance(run.get("cpuModel"), str) and bool(run["cpuModel"]),
        "CPU model is missing",
    )

    expected_count = 1 + len(TOPOLOGIES) * (1 + (warmups + measured) * len(MODES)) + 1
    require(len(rows) == expected_count, "the topology probe has missing or extra rows")
    cursor = 1
    cases = []
    topologies = []
    for topology, physical, placements in TOPOLOGIES:
        description = rows[cursor]
        cursor += 1
        require(
            description.get("kind") == "topology" and description.get("topology") == topology,
            f"{topology}: topology order changed",
        )
        require(description.get("physicalWindows") == physical, f"{topology}: physical window count changed")
        require(description.get("placements") == placements, f"{topology}: placement count changed")
        require(
            description.get("queryRows") == placements,
            f"{topology}: query row count differs from placements",
        )
        require(description.get("distinctPaneIds") == physical, f"{topology}: distinct pane count changed")
        require(description.get("attachedControlClients") == 1, f"{topology}: attached client count changed")
        require(nonnegative_number(description.get("setupMs")), f"{topology}: setup time is missing")
        topologies.append({
            "topology": topology,
            "physicalWindows": physical,
            "placements": placements,
            "queryRows": description["queryRows"],
            "distinctPaneIds": description["distinctPaneIds"],
            "setupMs": description["setupMs"],
        })

        timings = {mode: [] for mode in MODES}
        for round_number in range(warmups + measured):
            for slot in range(len(MODES)):
                sample = rows[cursor]
                cursor += 1
                mode_index = (round_number + slot) % len(MODES)
                mode = MODES[mode_index]
                require(
                    sample.get("kind") == "sample"
                    and sample.get("topology") == topology
                    and sample.get("round") == round_number
                    and sample.get("slot") == slot
                    and sample.get("mode") == mode,
                    f"{topology}: sample order or mode rotation changed",
                )
                require(sample.get("physicalWindows") == physical, f"{topology}: sample physical window count changed")
                require(sample.get("placements") == placements, f"{topology}: sample placement count changed")
                require(sample.get("warmup") is (round_number < warmups), f"{topology}: warmup label changed")
                require(sample.get("status") == "ok" and sample.get("validated") is True, f"{topology}: sample was not validated")
                require(nonnegative_number(sample.get("elapsedMs")), f"{topology}: sample timing is invalid")
                require(sample.get("processes") == PROCESSES[mode_index], f"{topology}: process count changed")
                require(sample.get("attachedControlClients") == 1, f"{topology}: attached client count changed")
                require(sample.get("usedControlClients") == int(mode_index >= 3), f"{topology}: used client count changed")
                require(sample.get("logicalCommands") == 24, f"{topology}: logical command count changed")
                maximum = sample.get("maximumInFlight")
                require(
                    type(maximum) is int and 1 <= maximum <= 4
                    and (maximum >= 2 if mode_index in (1, 4) else maximum == 1),
                    f"{topology}: concurrency bound changed",
                )
                if round_number >= warmups:
                    timings[mode].append(sample["elapsedMs"])

        for mode in MODES:
            values = timings[mode]
            require(len(values) == measured, f"{topology}: measured sample count changed")
            if measured == 1:
                cases.append({
                    "topology": topology,
                    "mode": mode,
                    "samples": 1,
                    "observedMs": values[0],
                })
            else:
                cases.append({
                    "topology": topology,
                    "mode": mode,
                    "samples": len(values),
                    "medianMs": statistics.median(values),
                    "p95Ms": percentile(values, 0.95),
                    "p99Ms": percentile(values, 0.99),
                })

    summary = rows[cursor]
    require(
        summary.get("kind") == "summary" and summary.get("status") == "ok"
        and summary.get("sampleCount") == len(TOPOLOGIES) * (warmups + measured) * len(MODES),
        "the final sample summary is missing or inconsistent",
    )
    return {
        "schema": "libtmux-topology-smoke-v1" if measured == 1 else "libtmux-topology-mode-record-v1",
        "collectedUtc": collected,
        "sourceCommit": run["sourceCommit"],
        "sourceClean": run["sourceClean"],
        "evidenceStatus": "diagnostic-smoke" if measured == 1 else (
            "exploratory" if exploratory else "clean-source"
        ),
        "sourceTreeFingerprint": source_fingerprint_before if exploratory else None,
        "sourceTreeFingerprintBefore": source_fingerprint_before if exploratory else None,
        "sourceTreeFingerprintAfter": source_fingerprint_after if exploratory else None,
        "tmuxBinaryPath": run["tmuxBinaryPath"],
        "tmuxBinarySha256": run["tmuxBinarySha256"],
        "tmuxVersion": run["tmuxVersion"],
        "runtime": run.get("runtime"),
        "os": run.get("os"),
        "architecture": run.get("architecture"),
        "processors": run["processors"],
        "cpuModel": run["cpuModel"],
        "rawSha256": hashlib.sha256(raw).hexdigest(),
        "method": {
            "tool": "TopologyModeProbe",
            "warmupRounds": warmups,
            "measuredRounds": measured,
            "itemsPerSample": 8,
            "concurrencyCap": 4,
            "modeOrder": list(MODES),
            "interleaved": True,
            "percentiles": "linear interpolation at p*(n-1)",
        },
        "topologies": topologies,
        "cases": cases,
    }


def collect_stream(
    path: pathlib.Path,
    *,
    exploratory: bool = False,
    source_fingerprint_before: str | None = None,
    source_fingerprint_after: str | None = None,
) -> dict:
    """Validate a complete stream probe before summarizing measured samples."""
    raw = path.read_bytes()
    rows = [json.loads(line) for line in raw.splitlines() if line.strip()]
    require(bool(rows), "the stream probe has no rows")
    run = rows[0]
    require(run.get("kind") == "run", "the first row must identify the run")
    collected = run.get("collectedUtc")
    require(isinstance(collected, str), "run collection timestamp is missing")
    try:
        collected_at = datetime.datetime.fromisoformat(collected)
    except ValueError as error:
        raise ValueError("run collection timestamp is invalid") from error
    require(
        collected_at.tzinfo is not None
        and collected_at.utcoffset() == datetime.timedelta(0),
        "run collection timestamp must be UTC",
    )
    validate_source_identity(run, exploratory, source_fingerprint_before, source_fingerprint_after)
    require(bool(run.get("tmuxVersion")), "tmux version is missing")
    for field in ("runtime", "os", "architecture"):
        require(isinstance(run.get(field), str) and bool(run[field]), f"{field} is missing")
    require(
        type(run.get("processors")) is int and run["processors"] > 0,
        "processor count is missing",
    )

    settings = run.get("settings", {})
    counts = tuple(settings.get(field) for field in (
        "samplesPerPayload", "warmupsPerPayload", "overflowSamples", "disposalSamples",
        "overflowBufferCapacity", "overflowNotifications",
    ))
    require(
        counts in ((20, 2, 5, 5, 8, 32), (1, 0, 1, 1, 4, 16)),
        "the stream probe settings are not a full run or smoke run",
    )
    require(
        type(settings.get("latencyPayloadBytes")) is int
        and settings["latencyPayloadBytes"] > 0
        and type(settings.get("throughputPayloadBytes")) is int
        and settings["throughputPayloadBytes"] > 0,
        "payload sizes are missing",
    )
    measured, warmups, overflow_count, disposal_count, capacity, notifications = counts
    expected_count = 1 + 2 * (warmups + measured) + overflow_count + disposal_count
    require(len(rows) == expected_count, "the stream probe has missing or extra samples")

    cursor = 1
    cases = []
    scenarios = (
        ("latency", warmups + measured, warmups, STREAM_PAYLOAD_METRICS),
        ("throughput", warmups + measured, warmups, STREAM_PAYLOAD_METRICS),
        ("notification-buffer-overflow", overflow_count, 0, STREAM_OVERFLOW_METRICS),
        ("pending-reader-disposal", disposal_count, 0, STREAM_DISPOSAL_METRICS),
    )
    for scenario, count, discarded, metric_names in scenarios:
        values = {metric: [] for metric in metric_names}
        for index in range(count):
            sample = rows[cursor]
            cursor += 1
            require(
                sample.get("kind") == "sample"
                and sample.get("scenario") == scenario
                and sample.get("sample") == index
                and sample.get("warmup") is (index < discarded),
                f"{scenario}: sample order or warmup label changed",
            )
            require(sample.get("status") == "ok", f"{scenario}: sample failed")
            measurement = sample.get("measurement")
            require(isinstance(measurement, dict), f"{scenario}: measurement is missing")
            for metric in metric_names:
                require(
                    nonnegative_number(measurement.get(metric)),
                    f"{scenario}: {metric} is invalid",
                )
                if index >= discarded:
                    values[metric].append(measurement[metric])

            if scenario in ("latency", "throughput"):
                payload = settings["latencyPayloadBytes" if scenario == "latency" else "throughputPayloadBytes"]
                require(measurement.get("payloadBytes") == payload, f"{scenario}: payload size changed")
                require(
                    measurement["firstOutputMs"] <= measurement["completeOutputMs"]
                    and measurement["endToEndBytesPerSecond"] > 0
                    and measurement["outputEvents"] > 0,
                    f"{scenario}: pane output measurement is inconsistent",
                )
            elif scenario == "notification-buffer-overflow":
                require(
                    measurement.get("producedRenameCommands") == notifications
                    and measurement.get("bufferCapacityEvents") == capacity
                    and measurement["count"] > 0
                    and measurement["totalDropped"] >= measurement["count"]
                    and measurement.get("controlStillRunning") is True,
                    "notification-buffer-overflow: loss or client state is invalid",
                )
            else:
                require(
                    measurement["readerCompletionMs"] >= measurement["disposalMs"]
                    and measurement.get("sawExit") is True
                    and measurement.get("readerPendingAtTrigger") is True
                    and measurement.get("controlStillRunning") is False
                    and measurement.get("serverStillRunning") is True,
                    "pending-reader-disposal: reader or client state is invalid",
                )

        sample_count = count - discarded
        require(all(len(metric_values) == sample_count for metric_values in values.values()),
                f"{scenario}: measured sample count changed")
        cases.append({
            "scenario": scenario,
            "samples": sample_count,
            "measurements": {
                metric: ({"observed": metric_values[0]} if measured == 1 else {
                    "median": statistics.median(metric_values),
                    "p95": percentile(metric_values, 0.95),
                    "p99": percentile(metric_values, 0.99),
                })
                for metric, metric_values in values.items()
            },
        })

    return {
        "schema": "libtmux-stream-smoke-v1" if measured == 1 else "libtmux-stream-record-v1",
        "collectedUtc": collected,
        "sourceCommit": run["sourceCommit"],
        "sourceClean": run["sourceClean"],
        "evidenceStatus": "diagnostic-smoke" if measured == 1 else (
            "exploratory" if exploratory else "clean-source"
        ),
        "sourceTreeFingerprint": source_fingerprint_before if exploratory else None,
        "sourceTreeFingerprintBefore": source_fingerprint_before if exploratory else None,
        "sourceTreeFingerprintAfter": source_fingerprint_after if exploratory else None,
        "tmuxVersion": run["tmuxVersion"],
        "runtime": run["runtime"],
        "os": run["os"],
        "architecture": run["architecture"],
        "processors": run["processors"],
        "rawSha256": hashlib.sha256(raw).hexdigest(),
        "rawSampleCount": len(rows) - 1,
        "method": {
            "tool": "StreamingProbe",
            "samplesPerPayload": measured,
            "warmupsPerPayload": warmups,
            "overflowSamples": overflow_count,
            "disposalSamples": disposal_count,
            "percentiles": "linear interpolation at p*(n-1)" if measured > 1 else None,
        },
        "cases": cases,
    }


def main() -> None:
    """Write a validated probe distribution alongside its raw NDJSON."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--probe", choices=("topology", "stream"), default="topology")
    parser.add_argument("--exploratory", action="store_true")
    parser.add_argument("--source-fingerprint-before")
    parser.add_argument("--source-fingerprint-after")
    arguments = parser.parse_args()
    require(arguments.input.resolve() != arguments.output.resolve(), "input and output must differ")
    recorder = collect_stream if arguments.probe == "stream" else collect
    record = recorder(
        arguments.input,
        exploratory=arguments.exploratory,
        source_fingerprint_before=arguments.source_fingerprint_before,
        source_fingerprint_after=arguments.source_fingerprint_after,
    )
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(record, indent=2) + "\n")
    print(f"validated {len(record['cases'])} {arguments.probe} distributions")


if __name__ == "__main__":
    main()
