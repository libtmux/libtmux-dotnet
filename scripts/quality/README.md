# quality

`measure.py` prints the raw numbers that [docs/quality-bar.md](../../docs/quality-bar.md)
cites: tmux versions and frameworks the full CI scope covers, documentation
examples compiled and executed, fuzz cases, actions pinned to a commit,
document validators the gate runs, decision records, recorded benchmark runs,
published packages, and the project files and source bytes that should not be
missing. The exit status is the number of problems found.

A maintainer runs it when assessing a tree:

```console
$ uv run python scripts/quality/measure.py
```

It does not refresh the archived prose in the quality bar. Current
compatibility, package, API and platform claims need the CI or release gates
named there, on the exact tree being assessed.

Run the tests with the [script runner](../README.md#running-the-checks).
