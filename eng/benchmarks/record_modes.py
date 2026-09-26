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
import json
import pathlib
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
    """Return the output of a git command, or an empty string if it fails."""
    try:
        return subprocess.run(
            ["git", *arguments], capture_output=True, text=True, check=True
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return ""


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


def collect(report: pathlib.Path, tmux_version: str, collected: str) -> dict:
    """Build the record from a BenchmarkDotNet full report."""
    document = json.loads(report.read_text(encoding="utf-8"))
    host = document.get("HostEnvironmentInfo", {})
    warmups = {warmup_samples(benchmark) for benchmark in document["Benchmarks"]}
    if len(warmups) != 1:
        raise SystemExit(f"cases discarded different warmup counts: {sorted(warmups)}")

    cases = []
    for benchmark in document["Benchmarks"]:
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


def render(record: dict) -> str:
    """Render the record as the table a reader actually reads."""
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--tmux-version", required=True)
    parser.add_argument("--collected", required=True, help="ISO date of the run")
    parser.add_argument("--out", type=pathlib.Path, required=True)
    arguments = parser.parse_args()

    # The record names HEAD, so it must be the tree that was measured.
    if git("status", "--porcelain"):
        print("record from a committed tree: the record names HEAD", file=sys.stderr)
        return 1

    record = collect(arguments.report, arguments.tmux_version, arguments.collected)

    arguments.out.mkdir(parents=True, exist_ok=True)
    stem = f"{arguments.collected}-tmux-{arguments.tmux_version}"
    (arguments.out / f"{stem}.json").write_text(
        json.dumps(record, indent=2) + "\n", encoding="utf-8"
    )
    (arguments.out / f"{stem}.md").write_text(render(record), encoding="utf-8")
    print(f"wrote {arguments.out / stem}.json and .md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
