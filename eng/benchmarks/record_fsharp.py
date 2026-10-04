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
import math
import os
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


def conditions() -> dict:
    """Read what else shaped the run, where this host says.

    Recorded straight after the run, so the 5- and 15-minute load averages
    span it. Every value is None where the host does not expose it.
    """
    def read(path: str) -> str | None:
        try:
            return pathlib.Path(path).read_text(encoding="utf-8").strip()
        except OSError:
            return None

    cpuinfo = read("/proc/cpuinfo") or ""
    flags = next((line for line in cpuinfo.splitlines() if line.startswith("flags")), "")
    load = os.getloadavg() if hasattr(os, "getloadavg") else None
    affinity = os.sched_getaffinity(0) if hasattr(os, "sched_getaffinity") else None
    return {
        "hypervisor": None if not flags else " hypervisor" in f" {flags.split(':', 1)[-1]} ",
        "governor": read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"),
        "cpusAvailable": None if affinity is None else len(affinity),
        "loadAverage": None if load is None else [round(value, 2) for value in load],
    }


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
        "schema": "libtmux-fsharp-benchmark-record-v1",
        "collected": collected,
        "libraryVersion": git("describe", "--tags", "--always"),
        "commit": git("rev-parse", "HEAD"),
        "tmuxVersion": tmux_version,
        "host": {
            "os": host.get("OsVersion"),
            "processor": host.get("ProcessorName"),
            "logicalCores": host.get("LogicalCoreCount"),
            "runtime": host.get("RuntimeVersion"),
            **conditions(),
        },
        "classes": classes,
    }


def scaled(value_ns: float, divisor: float) -> str:
    """Format a time at three significant figures in the class's unit, never in exponent form."""
    value = value_ns / divisor
    if value >= 100:
        return f"{value:,.0f}"
    text = f"{value:.3g}"
    # A case far faster than its class's median would otherwise read 4e-05.
    return f"{value:.{2 - math.floor(math.log10(value))}f}" if "e" in text else text


def describe_conditions(host: dict) -> str:
    """Say the run's conditions in words, leaving out what the host did not report."""
    parts = []
    if host.get("hypervisor") is not None:
        parts.append("virtual machine" if host["hypervisor"] else "bare metal")
    if host.get("cpusAvailable") is not None:
        parts.append(f"{host['cpusAvailable']} of {host['logicalCores']} logical cores available")
    if host.get("governor"):
        parts.append(f"{host['governor']} governor")
    if host.get("loadAverage"):
        one, five, fifteen = host["loadAverage"]
        parts.append(f"load {one} / {five} / {fifteen} (1, 5, 15 min) when recorded")
    return "; ".join(parts) or "not reported"


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
    if "loadAverage" in host:
        lines.append(f"| **Conditions** | {describe_conditions(host)} |")
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


# Absolute times move by more than half between runs on one host, so the gate
# compares routes measured in the same run. Every recorded host shows pushdown
# 4 to 9 times faster than listing everything and filtering locally, with 10 to
# 30 times fewer bytes allocated, so missing either bar means pushdown stopped
# narrowing the listing rather than a noisy runner.
PUSHDOWN_CLASS = "FSharpQueryPushdownBenchmarks"
MINIMUM_SPEEDUP = 3.0

# A mirror captures the server once per announcement, so a rename seen through
# it costs little more than the capture alone: 1.02 to 1.35 times on the
# workstation and the hosted runner. A mirror made to capture twice per change
# measured 1.86 with sixteen sessions and 2.39 with one.
MIRROR_CLASS = "FSharpMirrorBenchmarks"
MAXIMUM_MIRROR_RATIO = 1.65

# A pane watch passes the client's events through as they arrive, so reading a
# flood through it costs what reading every event by hand does: 0.95 to 1.13
# times on the workstation and the hosted runner. A watch made to list the panes on each output event
# measured 2.33 for 20,000 lines; a short flood has too few events to show it.
FLOOD_CLASS = "FSharpPaneFloodBenchmarks"
MAXIMUM_FLOOD_RATIO = 1.6


def gate(record: dict) -> list[str]:
    """Return why pushdown, the mirror or the pane watch missed its bar in this run, if any did."""
    return (
        _pushdown_failures(record)
        + _ratio_failures(
            record, MIRROR_CLASS, "RenameUntilSeen", "CaptureSnapshot", MAXIMUM_MIRROR_RATIO,
            "a rename seen through the mirror costs {ratio:.1f} captures",
        )
        + _ratio_failures(
            record, FLOOD_CLASS, "WatchPane", "ReadEvents", MAXIMUM_FLOOD_RATIO,
            "reading a flood through the watch costs {ratio:.1f} times reading every event",
        )
    )


def _ratio_failures(record: dict, name: str, method: str, baseline: str, maximum: float, costs: str) -> list[str]:
    # Records made before a class's benchmark existed do not carry it.
    cases = next((entry["cases"] for entry in record["classes"] if entry["name"] == name), None)
    if cases is None:
        return []
    by_case = {(case["method"], case["parameters"]): case for case in cases}
    failures = []
    for parameters in sorted({case["parameters"] for case in cases}):
        measured = by_case.get((method, parameters))
        reference = by_case.get((baseline, parameters))
        if measured is None or reference is None:
            failures.append(f"{name} {parameters}: {method} or {baseline} is missing")
            continue
        ratio = measured["median_ns"] / reference["median_ns"]
        if ratio > maximum:
            failures.append(f"{name} {parameters}: {costs.format(ratio=ratio)}; the gate allows {maximum:g}")
    return failures


def _pushdown_failures(record: dict) -> list[str]:
    cases = next((entry["cases"] for entry in record["classes"] if entry["name"] == PUSHDOWN_CLASS), None)
    if cases is None:
        return [f"{PUSHDOWN_CLASS} is not in the record"]
    routes = {(case["method"], case["parameters"]): case for case in cases}
    failures = []
    for method in sorted({case["method"] for case in cases}):
        pushed = routes.get((method, "Route=pushdown"))
        listed = routes.get((method, "Route=list-then-filter"))
        if pushed is None or listed is None:
            failures.append(f"{method}: the pushdown or list-then-filter route is missing")
            continue
        speedup = listed["median_ns"] / pushed["median_ns"]
        if speedup < MINIMUM_SPEEDUP:
            failures.append(
                f"{method}: pushdown is {speedup:.1f} times as fast as listing everything; the gate needs {MINIMUM_SPEEDUP:g}"
            )
        if None not in (pushed["allocated_bytes"], listed["allocated_bytes"]) and (
            pushed["allocated_bytes"] >= listed["allocated_bytes"]
        ):
            failures.append(
                f"{method}: pushdown allocates {pushed['allocated_bytes']:,} bytes, "
                f"no fewer than listing everything ({listed['allocated_bytes']:,})"
            )
    return failures


def main() -> int:
    """Write the record's JSON and Markdown, refusing a tree HEAD does not describe."""
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--reports", type=pathlib.Path)
    parser.add_argument("--tmux-version")
    parser.add_argument("--collected", help="ISO date of the run")
    parser.add_argument("--out", type=pathlib.Path)
    parser.add_argument("--gate", type=pathlib.Path, metavar="RECORD", help="check a written record instead")
    parsed = parser.parse_args()
    if parsed.gate is not None:
        failures = gate(json.loads(parsed.gate.read_text(encoding="utf-8")))
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1 if failures else 0

    if None in (parsed.reports, parsed.tmux_version, parsed.collected, parsed.out):
        parser.error("--reports, --tmux-version, --collected and --out are required to record")
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
