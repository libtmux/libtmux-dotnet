"""Turn BenchmarkDotNet reports for the F# benchmarks into a record.

A benchmark number is only meaningful next to what produced it, so the record
names the commit, runtime, host and tmux, and keeps every case's distribution
rather than one figure. Run from a clean tree; the record names HEAD.

    uv run python eng/benchmarks/record_fsharp.py \\
        --reports artifacts/benchmarks-fsharp/results \\
        --tmux-version 3.7d \\
        --collected 2026-10-03 \\
        --out docs/benchmarks/runs
"""

from __future__ import annotations

import argparse
import json
import pathlib
import statistics
import subprocess
import sys

UNITS = ((1_000_000.0, "ms"), (1_000.0, "µs"), (1.0, "ns"))


def percentile(ordered: list[float], fraction: float) -> float:
    """Return the linear-interpolated percentile of an already-sorted list."""
    position = fraction * (len(ordered) - 1)
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(samples_ns: list[float]) -> dict[str, float]:
    """Reduce per-operation samples to the distribution a claim needs."""
    ordered = sorted(samples_ns)
    if not ordered:
        raise SystemExit("a case has no samples")
    return {
        "samples": len(ordered),
        "min_ns": ordered[0],
        "median_ns": percentile(ordered, 0.50),
        "mean_ns": statistics.fmean(ordered),
        "p95_ns": percentile(ordered, 0.95),
        "max_ns": ordered[-1],
    }


def git(*arguments: str) -> str:
    """Read required source provenance from Git."""
    try:
        return subprocess.run(["git", *arguments], capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as error:
        raise SystemExit("git failed; source provenance is unavailable") from error


def collect(reports: list[pathlib.Path], tmux_version: str, collected: str) -> dict:
    """Build one record from the full JSON reports of several benchmark classes."""
    if not reports:
        raise SystemExit("no full JSON reports found")
    classes = []
    host: dict = {}
    for report in sorted(reports):
        document = json.loads(report.read_text(encoding="utf-8"))
        host = document.get("HostEnvironmentInfo", host)
        cases = []
        for benchmark in document["Benchmarks"]:
            case = {
                "method": benchmark["Method"],
                "parameters": benchmark.get("Parameters", ""),
                "allocated_bytes": (benchmark.get("Memory") or {}).get("BytesAllocatedPerOperation"),
            }
            case.update(summarize(list(benchmark["Statistics"]["OriginalValues"])))
            cases.append(case)
        name = document["Title"].split("-")[0].removeprefix("LibTmux.Benchmarks.")
        classes.append({"name": name, "cases": cases})

    return {
        "schema": "libtmux-benchmark-record-v1",
        "collected": collected,
        "libraryVersion": git("describe", "--tags", "--always"),
        "commit": git("rev-parse", "HEAD"),
        "tmuxVersion": tmux_version,
        "host": {
            "os": host.get("OsVersion"),
            "processor": host.get("ProcessorName"),
            "logicalCores": host.get("LogicalCoreCount"),
            "runtime": host.get("RuntimeVersion"),
        },
        "classes": classes,
    }


def scaled(value_ns: float, divisor: float) -> str:
    """Format a time at three significant figures in the class's unit."""
    return f"{value_ns / divisor:.3g}"


def render(record: dict) -> str:
    """Render the record as the Markdown the benchmarks README links to."""
    host = record["host"]
    lines = [
        f"# F# benchmarks — {record['collected']}",
        "",
        "Every case checks its result before it is timed; see each class's setup.",
        "",
        "| | |",
        "|---|---|",
        f"| **tmux** | {record['tmuxVersion']} |",
        f"| **Library** | `{record['libraryVersion']}` at `{record['commit'][:12]}` |",
        f"| **Runtime** | {host['runtime']} |",
        f"| **Host** | {host['processor']}, {host['os']} |",
    ]
    for benchmark_class in record["classes"]:
        median = statistics.median(case["median_ns"] for case in benchmark_class["cases"])
        divisor, unit = next((divisor, unit) for divisor, unit in UNITS if median >= divisor)
        lines.extend(
            [
                "",
                f"## {benchmark_class['name']}",
                "",
                f"| Case | Median {unit} | p95 {unit} | Max {unit} | Allocated | Samples |",
                "|---|---:|---:|---:|---:|---:|",
            ]
        )
        for case in benchmark_class["cases"]:
            label = case["method"] + (f" ({case['parameters']})" if case["parameters"] else "")
            allocated = case["allocated_bytes"]
            lines.append(
                f"| {label} | {scaled(case['median_ns'], divisor)} | {scaled(case['p95_ns'], divisor)}"
                f" | {scaled(case['max_ns'], divisor)}"
                f" | {'' if allocated is None else f'{allocated:,} B'} | {case['samples']} |"
            )
    return "\n".join(lines) + "\n"


def main() -> int:
    """Write the record's JSON and Markdown, refusing a tree HEAD does not describe."""
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--reports", type=pathlib.Path, required=True)
    parser.add_argument("--tmux-version", required=True)
    parser.add_argument("--collected", required=True, help="ISO date of the run")
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parsed = parser.parse_args()
    if git("status", "--porcelain", "--untracked-files=no"):
        print("refusing to record from a tree with uncommitted changes", file=sys.stderr)
        return 1

    record = collect(sorted(parsed.reports.glob("*-report-full.json")), parsed.tmux_version, parsed.collected)
    # Appended, not substituted: a tmux version such as 3.7d has a dot of its own.
    stem = f"{parsed.collected}-tmux-{parsed.tmux_version}-fsharp"
    (parsed.out / f"{stem}.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    (parsed.out / f"{stem}.md").write_text(render(record), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
