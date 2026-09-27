#!/usr/bin/env python3
"""Turn a BenchmarkDotNet run into a record that can be checked rather than trusted.

A benchmark number is only meaningful next to what produced it. The same
machine measured a tmux process start at 3.5 ms and at 19 ms an hour apart,
which is enough to reverse which execution mode looks faster. So a recorded run
carries its tmux, its host, its date and its commit, and reports the whole
distribution instead of a mean that can be quoted alone.

Usage:
    uv run python eng/benchmarks/record_modes.py \\
        --report artifacts/benchmarks/results/LibTmux.Benchmarks.ModeBenchmarks-report-full.json \\
        --tmux-version 3.7b \\
        --collected 2026-08-16 \\
        --out docs/benchmarks/runs
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import statistics
import subprocess
import sys

# Reported at three significant figures. The samples resolve nothing finer:
# the spread between repeats of one case is wider than the gap this would show.
NS_PER_MS = 1_000_000.0


def percentile(ordered: list[float], fraction: float) -> float:
    """Return the linear-interpolated percentile of an already-sorted list."""
    if not ordered:
        raise ValueError("no samples")
    if len(ordered) == 1:
        return ordered[0]
    position = fraction * (len(ordered) - 1)
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(samples_ns: list[float]) -> dict[str, float]:
    """Reduce raw samples to the distribution a reader needs to judge a claim."""
    ordered = sorted(samples_ns)
    return {
        "samples": len(ordered),
        "min_ms": ordered[0] / NS_PER_MS,
        "median_ms": percentile(ordered, 0.50) / NS_PER_MS,
        "mean_ms": statistics.fmean(ordered) / NS_PER_MS,
        "p90_ms": percentile(ordered, 0.90) / NS_PER_MS,
        "p95_ms": percentile(ordered, 0.95) / NS_PER_MS,
        "p99_ms": percentile(ordered, 0.99) / NS_PER_MS,
        "max_ms": ordered[-1] / NS_PER_MS,
        "stdev_ms": statistics.stdev(ordered) / NS_PER_MS if len(ordered) > 1 else 0.0,
    }


def git(*arguments: str) -> str:
    """Read required source provenance from Git."""
    try:
        return subprocess.run(
            ["git", *arguments], capture_output=True, text=True, check=True
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as error:
        raise SystemExit("git failed; source provenance is unavailable") from error


def warmup_samples(benchmark: dict) -> int:
    """Count the workload warmups BenchmarkDotNet discarded for one case.

    Read from the report rather than restated: a record once said five while
    the run behind it discarded forty.
    """
    if "Measurements" not in benchmark:
        raise SystemExit(
            f"{benchmark['Method']}: the report has no Measurements; "
            "export it with BenchmarkDotNet's full JSON exporter"
        )
    return sum(
        1
        for measurement in benchmark["Measurements"]
        if measurement.get("IterationMode") == "Workload"
        and measurement.get("IterationStage") == "Warmup"
    )


def collect(
    report: pathlib.Path,
    tmux_version: str,
    collected: str,
    run_log: pathlib.Path | None = None,
) -> dict:
    """Build the record from a BenchmarkDotNet full report."""
    document = json.loads(report.read_text(encoding="utf-8"))
    benchmarks = document["Benchmarks"]
    if not benchmarks:
        raise SystemExit("the report contains no benchmarks")
    if any(benchmark.get("Parameters", "").startswith("Mode=") for benchmark in benchmarks):
        if not all(benchmark.get("Parameters", "").startswith("Mode=") for benchmark in benchmarks):
            raise SystemExit("the report mixes mode workload and legacy command cases")
        return collect_workload(report, document, tmux_version, collected, run_log)

    host = document.get("HostEnvironmentInfo", {})
    warmups = {warmup_samples(benchmark) for benchmark in benchmarks}
    if len(warmups) != 1:
        raise SystemExit(f"cases discarded different warmup counts: {sorted(warmups)}")

    cases = []
    for benchmark in benchmarks:
        statistics_block = benchmark["Statistics"]
        parameters = benchmark.get("Parameters", "")
        commands = int(parameters.split("=")[-1]) if "=" in parameters else 1
        case = {
            "mode": benchmark["Method"],
            "commands": commands,
            "allocated_bytes": (benchmark.get("Memory") or {}).get(
                "BytesAllocatedPerOperation"
            ),
        }
        case.update(summarize(list(statistics_block["OriginalValues"])))
        cases.append(case)

    cases.sort(key=lambda case: (case["commands"], case["mode"]))

    return {
        "schema": "libtmux-benchmark-record-v1",
        "collected": collected,
        "libraryVersion": git("describe", "--tags", "--always"),
        "commit": git("rev-parse", "HEAD"),
        "tmuxVersion": tmux_version,
        "host": {
            "os": host.get("OsVersion"),
            "processor": host.get("ProcessorName"),
            "physicalCores": host.get("PhysicalCoreCount"),
            "logicalCores": host.get("LogicalCoreCount"),
            "runtime": host.get("RuntimeVersion"),
            "architecture": host.get("Architecture"),
        },
        "method": {
            "tool": "BenchmarkDotNet",
            "runStrategy": "Monitoring",
            "operationsPerSample": 1,
            "samplesPerCase": max(case["samples"] for case in cases),
            "warmupSamples": warmups.pop(),
        },
        "cases": cases,
    }


def collect_workload(
    report: pathlib.Path,
    document: dict,
    tmux_version: str,
    collected: str,
    run_log: pathlib.Path | None,
) -> dict:
    """Record only a complete, preflight-checked five-mode workload."""
    if run_log is None:
        candidates = sorted(report.parent.parent.glob("LibTmux.Benchmarks.ModeWorkloadBenchmarks-*.log"))
        if len(candidates) != 1:
            raise SystemExit("five-mode workloads need one matching log or an explicit --run-log")
        run_log = candidates[0]

    expected = {
        "serial-process": (24, 0),
        "concurrent-process": (24, 0),
        "process-chain": (1, 0),
        "serial-control": (0, 1),
        "concurrent-control": (0, 1),
    }
    benchmarks = document["Benchmarks"]
    modes = [benchmark["Parameters"].removeprefix("Mode=") for benchmark in benchmarks]
    if len(modes) != len(expected) or set(modes) != set(expected):
        raise SystemExit("a five-mode record needs exactly the five modes once each")
    if any(benchmark["Method"] != "Workload" for benchmark in benchmarks):
        raise SystemExit("the five-mode report contains an unexpected method")

    log = run_log.read_text(encoding="utf-8")
    observed = re.findall(
        r"Mode '([^']+)' preflight: (\d+) tmux processes, (\d+) control clients, "
        r"(\d+) logical commands, concurrency cap (\d+)\.",
        log,
    )
    if len(observed) != len(expected) or {row[0] for row in observed} != set(expected):
        raise SystemExit("the run log lacks one preflight for each of the five modes")
    for mode, processes, clients, commands, cap in observed:
        if (int(processes), int(clients)) != expected[mode] or (int(commands), int(cap)) != (24, 4):
            raise SystemExit(f"mode {mode} has unexpected process, client, or command counts")
    source = re.findall(
        r"^Mode '([^']+)' source: commit ([0-9a-f]{40}); clean (true|false)\.$",
        log,
        flags=re.MULTILINE,
    )
    head = git("rev-parse", "HEAD")
    if (len(source) != len(expected) or {row[0] for row in source} != set(expected)
            or not re.fullmatch(r"[0-9a-f]{40}", head)
            or any(commit != head or clean != "true" for _, commit, clean in source)):
        raise SystemExit("the run log lacks a clean source commit matching the current HEAD for every mode")
    if "executed benchmarks: 5" not in log:
        raise SystemExit("the run log does not confirm five executed benchmarks")
    mode_order = re.findall(
        r"^// Benchmark: ModeWorkloadBenchmarks\.Workload:.*\[Mode=([^\]]+)\]$",
        log,
        flags=re.MULTILINE,
    )
    if len(mode_order) != len(expected) or set(mode_order) != set(expected):
        raise SystemExit("the run log lacks the execution order of all five modes")

    warmups = {warmup_samples(benchmark) for benchmark in benchmarks}
    if len(warmups) != 1:
        raise SystemExit(f"cases discarded different warmup counts: {sorted(warmups)}")
    actual_iterations = {
        sum(
            1 for measurement in benchmark["Measurements"]
            if measurement.get("IterationMode") == "Workload"
            and measurement.get("IterationStage") == "Actual"
        ) for benchmark in benchmarks
    }
    if len(actual_iterations) != 1 or 0 in actual_iterations:
        raise SystemExit("cases have different or zero actual iteration counts")

    cases = []
    for benchmark in benchmarks:
        values = benchmark.get("Statistics", {}).get("OriginalValues", [])
        if not values:
            raise SystemExit(f"mode {benchmark['Parameters']} has no measured samples")
        mode = benchmark["Parameters"].removeprefix("Mode=")
        case = {
            "mode": mode,
            "processes": expected[mode][0],
            "controlClients": expected[mode][1],
            "logicalCommands": 24,
            "maximumConcurrentInputs": 4 if mode.startswith("concurrent-") else 1,
            "allocatedBytes": (benchmark.get("Memory") or {}).get("BytesAllocatedPerOperation"),
        }
        case.update(summarize(list(values)))
        cases.append(case)
    cases.sort(key=lambda case: list(expected).index(case["mode"]))

    host = document.get("HostEnvironmentInfo", {})
    return {
        "schema": "libtmux-mode-workload-record-v1",
        "collected": collected,
        "libraryVersion": git("describe", "--tags", "--always"),
        "commit": head,
        "tmuxVersion": tmux_version,
        "host": {
            "os": host.get("OsVersion"),
            "processor": host.get("ProcessorName"),
            "physicalCores": host.get("PhysicalCoreCount"),
            "logicalCores": host.get("LogicalCoreCount"),
            "runtime": host.get("RuntimeVersion"),
            "architecture": host.get("Architecture"),
        },
        "method": {
            "tool": "BenchmarkDotNet",
            "runStrategy": "Monitoring",
            "operationsPerSample": 1,
            "actualIterationsPerCase": actual_iterations.pop(),
            "warmupSamples": warmups.pop(),
            "modeOrder": mode_order,
            "interleaved": False,
            "reportSha256": hashlib.sha256(report.read_bytes()).hexdigest(),
            "runLogSha256": hashlib.sha256(run_log.read_bytes()).hexdigest(),
        },
        "cases": cases,
    }


def render(record: dict) -> str:
    """Render the record as the table a reader actually reads."""
    if record["schema"] == "libtmux-mode-workload-record-v1":
        return render_workload(record)

    host = record["host"]
    lines = [
        f"# Mode benchmarks — {record['collected']}",
        "",
        "A recorded run, not a promise. These numbers describe one machine on one",
        "day; what carries between machines is the shape, not the milliseconds.",
        "",
        "| | |",
        "|---|---|",
        f"| **Collected** | {record['collected']} |",
        f"| **Library** | `{record['libraryVersion']}` at `{record['commit'][:12]}` |",
        f"| **tmux** | {record['tmuxVersion']} |",
        f"| **Runtime** | {host['runtime']} |",
        f"| **Host** | {host['processor']}, {host['physicalCores']}C/{host['logicalCores']}T, {host['os']} |",
        f"| **Method** | BenchmarkDotNet, `RunStrategy.Monitoring`, "
        f"{record['method']['samplesPerCase']} samples of 1 operation, "
        f"{record['method']['warmupSamples']} discarded |",
        "",
        "## Distribution",
        "",
        "Every column is milliseconds over the samples in one case.",
        "",
        "| Commands | Mode | Min | Median | Mean | p90 | p95 | p99 | Max | Allocated |",
        "|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]

    for case in record["cases"]:
        allocated = case["allocated_bytes"]
        allocated_text = f"{allocated / 1024:,.0f} KB" if allocated else "—"
        lines.append(
            f"| {case['commands']} | {case['mode']} "
            f"| {case['min_ms']:.2f} | {case['median_ms']:.2f} | {case['mean_ms']:.2f} "
            f"| {case['p90_ms']:.2f} | {case['p95_ms']:.2f} | {case['p99_ms']:.2f} "
            f"| {case['max_ms']:.2f} | {allocated_text} |"
        )

    lines += [
        "",
        "## Reading this",
        "",
        "The spread inside one case is wider than the gap between some cases. Where",
        "two rows overlap across the distribution, they are one measurement and the",
        "order between their medians is not a finding. Allocation is the column that",
        "repeats exactly, so it is what a change should be checked against.",
        "",
    ]
    return "\n".join(lines)


def render_workload(record: dict) -> str:
    """Render the five-mode result with its measured and setup boundaries."""
    host = record["host"]
    method = record["method"]
    lines = [
        f"# Five-mode workload — {record['collected']}",
        "",
        "Eight inputs each run one marker, pane capture, and pane-ID query.",
        "Each mode checks every result before and during timing. The modes ran",
        "as separate BenchmarkDotNet cases, without sample interleaving.",
        "",
        "| | |",
        "|---|---|",
        f"| **tmux** | {record['tmuxVersion']} |",
        f"| **Library** | `{record['libraryVersion']}` at `{record['commit'][:12]}` |",
        f"| **Runtime** | {host['runtime']} |",
        f"| **Host** | {host['processor']}, {host['os']} |",
        f"| **Run** | {method['actualIterationsPerCase']} measured, "
        f"{method['warmupSamples']} warmup passes per mode |",
        "",
        "| Mode | tmux processes | Control clients | Median ms | p95 ms | p99 ms | Allocated |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for case in record["cases"]:
        allocated = case["allocatedBytes"]
        allocation = f"{allocated / 1024:,.0f} KB" if allocated is not None else "—"
        name = case["mode"].replace("-", " ").capitalize()
        lines.append(
            f"| {name} | {case['processes']} | {case['controlClients']} "
            f"| {case['median_ms']:.3f} | {case['p95_ms']:.3f} "
            f"| {case['p99_ms']:.3f} | {allocation} |"
        )
    lines += [
        "",
        "The process and client counts were checked during preflight, outside",
        "the timed pass. This workload uses a live tmux pane-ID lookup; F#",
        "`Query.matching` costs are measured separately over captured objects.",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--tmux-version", required=True)
    parser.add_argument("--collected", required=True, help="ISO date of the run")
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parser.add_argument("--run-log", type=pathlib.Path)
    arguments = parser.parse_args()

    # The record names HEAD, so it must be the tree that was measured.
    if git("status", "--porcelain"):
        print("record from a committed tree: the record names HEAD", file=sys.stderr)
        return 1

    record = collect(arguments.report, arguments.tmux_version, arguments.collected, arguments.run_log)

    arguments.out.mkdir(parents=True, exist_ok=True)
    stem = f"{arguments.collected}-tmux-{arguments.tmux_version}"
    if record["schema"] == "libtmux-mode-workload-record-v1":
        stem += "-workload"
    (arguments.out / f"{stem}.json").write_text(
        json.dumps(record, indent=2) + "\n", encoding="utf-8"
    )
    (arguments.out / f"{stem}.md").write_text(render(record), encoding="utf-8")
    print(f"wrote {arguments.out / stem}.json and .md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
