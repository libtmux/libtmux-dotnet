#!/usr/bin/env python3
"""Count each test's failures across repeated runs of the same suite.

The stress workflow runs the real-tmux suites many times and leaves one JUnit
file per run. A test that fails in some runs and passes in others is a flake;
one failure count per test is the number that says how often.

```console
$ python3 eng/ci/stress_summary.py --results results --label macos-latest
```
"""

from __future__ import annotations

import argparse
import csv
import pathlib
import sys
import xml.etree.ElementTree as et


def count(results: pathlib.Path, pattern: str) -> tuple[int, dict[str, list[int]]]:
    """Return how many runs matched and, per test, [runs seen, failures]."""
    runs = 0
    tests: dict[str, list[int]] = {}
    for path in sorted(results.glob(pattern)):
        runs += 1
        for case in et.parse(path).iter("testcase"):
            name = case.get("name") or ""
            if case.find("skipped") is not None:
                continue
            seen = tests.setdefault(name, [0, 0])
            seen[0] += 1
            if case.find("failure") is not None or case.find("error") is not None:
                seen[1] += 1
    return runs, tests


def main(argv: list[str] | None = None) -> int:
    """Append to the CSV and write the Markdown table; answer 2 when nothing matched."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", type=pathlib.Path, required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--pattern", default="run-*.xml", help="JUnit files, one per run")
    parser.add_argument("--repeat", type=int, default=0)
    parser.add_argument("--csv", type=pathlib.Path)
    parser.add_argument("--summary", type=pathlib.Path)
    args = parser.parse_args(argv)

    runs, tests = count(args.results, args.pattern)
    if runs == 0 or not tests:
        print(f"{args.label}: no test results under {args.results}", file=sys.stderr)
        return 2

    flaky = sorted(
        ((name, seen) for name, seen in tests.items() if seen[1] > 0),
        key=lambda item: (-item[1][1], item[0]),
    )
    if args.csv:
        fresh = not args.csv.exists()
        with args.csv.open("a", newline="", encoding="utf-8") as handle:
            writer = csv.writer(handle)
            if fresh:
                writer.writerow(["lane", "test", "runs", "failures"])
            for name, (seen, failed) in sorted(tests.items()):
                writer.writerow([args.label, name, seen, failed])

    lines = [f"### {args.label}", ""]
    wanted = f" of {args.repeat} requested" if args.repeat else ""
    lines.append(f"{runs} runs read{wanted}; {len(tests)} tests; {len(flaky)} failed at least once.")
    lines.append("")
    if flaky:
        lines += ["| Test | Failures | Runs |", "| --- | ---: | ---: |"]
        lines += [f"| `{name}` | {failed} | {seen} |" for name, (seen, failed) in flaky]
    else:
        lines.append("Every test passed in every run.")
    text = "\n".join(lines) + "\n"
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as handle:
            handle.write(text)
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
