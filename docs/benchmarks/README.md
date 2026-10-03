# Benchmarks

Recorded runs, each naming the tmux, host, runtime and commit that produced it.
Nothing here is a promise about your machine.

## Runs

| Collected | tmux | Library | Record |
|---|---|---|---|
| 2026-08-16 | 3.7b | `0.0.0-alpha.3` | [record](runs/2026-08-16-tmux-3.7b.md) |
| 2026-09-27 | 3.7d | `0.0.0-alpha.16` + F# branch | [five-mode workload](runs/2026-09-27-tmux-3.7d-workload.md), [linked topology](probes/2026-09-27-tmux-3.7d-topology.json), [control stream](probes/2026-09-27-tmux-3.7d-stream.json) |
| 2026-10-03 | 3.7d | `0.0.0-alpha.17` + F# branch | [F# query, pushdown, fold and task costs](runs/2026-10-03-tmux-3.7d-fsharp.md) |

## Why a record rather than a number

A tmux command is a process start, and a process start is not a stable
quantity. The same machine, the same commit and the same tmux measured a
one-shot command at 3.3 ms and at 4.7 ms in two runs an hour apart, and at 19 ms
while a build was running. Any single millisecond figure quoted without its
conditions is quoting the conditions.

So each run records the whole distribution — min, median, mean, p90, p95, p99,
max — and the conditions that produced it. The command-count run below uses
100 samples per case; other benchmarks state their own counts. A claim that
holds at the p95 of a recorded run is a claim about the library. A claim that
holds only at the mean is a claim about that afternoon.

## What is comparable and what is not

**Comparable across machines:** the shape. The command-count record shows
one-shot cost growing with each process, while a chain shares one process.
The five-mode workload holds the command count fixed and checks the same
results through each route.

**Not comparable across machines:** the milliseconds, and the crossover between
chaining and control mode. Which of the two wins at fifty commands depends on
what a process start costs relative to a round trip, and that ratio is a
property of the host. Both orders have been measured here.

**Compare allocations within one workload and runtime.** The records include
allocated bytes alongside timing; changing the workload changes that count.

## Reproducing

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- --filter '*ModeBenchmarks*' --artifacts artifacts/benchmarks
```

The project multi-targets, so the framework has to be named; the recorded runs
are `net10.0`. To measure a specific tmux rather than whatever is on the path,
set `LIBTMUX_TMUX` to its binary.

Turn the result into a record:

```console
$ uv run python eng/benchmarks/record_modes.py \
    --report artifacts/benchmarks/results/LibTmux.Benchmarks.ModeBenchmarks-report-full.json \
    --tmux-version 3.7b \
    --collected 2026-08-16 \
    --out docs/benchmarks/runs
```

## F# captured-field cost

[`FSharpCapturedFieldBenchmarks`](../../benchmarks/LibTmux.Benchmarks/FSharpCapturedFieldBenchmarks.cs)
compares a direct C# read of `Pane.CurrentPath` with the F# `Pane.currentPath`
option for the same captured pane. It checks both values before measuring and
uses present and absent paths. This isolates the option conversion; it does
not run tmux or compare execution modes.

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*FSharpCapturedFieldBenchmarks*' \
    --artifacts artifacts/benchmarks/fsharp-fields
```

## Five-mode live workload

[`ModeWorkloadBenchmarks`](../../benchmarks/LibTmux.Benchmarks/ModeWorkloadBenchmarks.cs)
runs eight inputs against one owned pane. Each input prints a unique marker,
captures the pane, and lists its pane ID. Every mode sends the same three tmux
commands and checks each marker, captured lines, and pane ID by input index.
The process chain checks exact combined output bytes at every input boundary.

| Mode | Processes per timed pass | Attached control clients | Concurrent inputs |
|---|---:|---:|---:|
| Serial process | 24 | 0 | 1 |
| Concurrent process | 24 | 0 | 4 maximum |
| Process chain | 1 | 0 | 1 |
| Serial control | 0 | 1 | 1 |
| Concurrent control | 0 | 1 | 4 maximum |

Setup seeds and captures the pane before timing. A live preflight checks the
results and actual process and client counts for each mode. The timed pass also
checks every result. Control replies are lines while process replies retain raw
bytes; comparison removes only empty lines at the end of a pane capture.
Setup operations use a 30-second cancellation budget; each preflight and
timed pass uses a 10-second budget. Each preflight logs its source commit and
whether the checkout was clean.

```console
$ mise exec -- dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*ModeWorkloadBenchmarks*' \
    --artifacts artifacts/benchmarks/mode-workload
```

The config requests five warmup and twenty measured passes per mode. Set
`LIBTMUX_BENCH_SMOKE=1` for one measured pass without warmups. Keep the full
BenchmarkDotNet JSON and run log. On a committed tree, record them together:

```console
$ python3 eng/benchmarks/record_modes.py \
    --report artifacts/benchmarks/mode-workload/results/LibTmux.Benchmarks.ModeWorkloadBenchmarks-report-full.json \
    --tmux-version 3.7d \
    --collected 2026-09-27 \
    --out docs/benchmarks/runs
```

The recorder uses the sole matching BenchmarkDotNet log beside the report;
pass `--run-log` when that directory contains several runs. It rejects missing
cases, samples, preflight counts, or a source commit that differs from the
current clean checkout. It keeps the older `Commands` record
format unchanged. BenchmarkDotNet runs these modes as
separate cases, so this run alone does not establish an interleaved comparison.
The pane-ID lookup is a tmux query, not an F# `Query.matching` filter; wrapper
query costs need their own measurement.

## Interleaved topology workload

[`TopologyModeProbe`](../../benchmarks/LibTmux.Benchmarks/TopologyModeProbe.cs)
rotates the five modes above across one physical window, four windows linked
eight times, and sixteen windows linked thirty-two times. Each sample runs the
same eight marker, capture, and pane-ID queries. The probe checks the output,
query rows, process counts, and actual concurrency before writing the sample.
One control client stays attached through all five modes, so the results
measure operation cost with that client already present.

Set `LIBTMUX_TMUX` to an absolute tmux binary path as described above, then
write a full NDJSON run:

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --topology-probe artifacts/benchmarks/topology.ndjson
```

The run uses five warmup and twenty measured rounds per topology. It rotates
mode order each round and records source identity, host, runtime, tmux binary
hash, setup time, and every sample. `--topology-probe-smoke` writes one
diagnostic round without distribution statistics.

On a clean committed tree, validate the full run and retain its median, p95,
and p99 distributions:

```console
$ python3 eng/benchmarks/record_topology.py \
    --input artifacts/benchmarks/topology.ndjson \
    --output artifacts/benchmarks/topology.record.json
```

The recorder rejects missing or reordered samples, incorrect workload counts,
and dirty source. Use `--exploratory` with equal before and after source-tree
fingerprints for an uncommitted run; its record remains exploratory evidence.

## F# query and task cost

[`FSharpQueryBenchmarks`](../../benchmarks/LibTmux.Benchmarks/FSharpQueryBenchmarks.cs)
checks that C# and F# filters produce the same document and matches before
measuring construction, compilation, cached predicates, and materialization
over 128 captured panes. No tmux process runs in those cases.
[`FSharpTaskForwardingBenchmarks`](../../benchmarks/LibTmux.Benchmarks/FSharpTaskForwardingBenchmarks.cs)
compares `Pane.CaptureAsync` with `Pane.capture` against the same completed
injected transport. Both calls must forward the same token and return the same
lines.

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*FSharpQueryBenchmarks*' \
    --artifacts artifacts/benchmarks/fsharp-query
```

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*FSharpTaskForwardingBenchmarks*' \
    --artifacts artifacts/benchmarks/fsharp-task
```

## F# control fold cost

[`FSharpControlFoldBenchmarks`](../../benchmarks/LibTmux.Benchmarks/FSharpControlFoldBenchmarks.cs)
compares direct C# `await foreach` with `Control.foldWhile` over the same
synthetic control event source. Both stop after 64 of 128 notifications and
compute the same checksum through the same prebuilt, task-returning folder.
Setup verifies the result, event-read count, reader disposal, and cancellation
token before timing. No tmux client runs. The folder is a C# adapter to the
F# function type and both paths invoke it, isolating the consumption loop.
This does not measure F# source syntax.

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*FSharpControlFoldBenchmarks*' \
    --artifacts artifacts/benchmarks/fsharp-control-fold
```

## F# query pushdown

[`FSharpQueryPushdownBenchmarks`](../../benchmarks/LibTmux.Benchmarks/FSharpQueryPushdownBenchmarks.cs)
builds a server of 64 sessions with 4 windows each, where one pane in 64 runs
`tail`, and finds those panes, and the sessions holding them, three ways: a
query tmux narrows with `-f`, a full listing filtered locally, and a snapshot
filtered locally. Every route must return the same objects before timing.

In the [2026-10-03 record](runs/2026-10-03-tmux-3.7d-fsharp.md) the pane query
took a median of 39 ms pushed down against 254 ms for a full listing and
497 ms for a snapshot, allocating 26 times less than the listing. The session
query, a relation, took 163 ms pushed down against 428 ms and more for the
local routes, allocating 10 times less: tmux evaluates the relation once per
session, and only the matching sessions are captured. Two identical local
routes for that query differ by half, which is the run-to-run noise of a
process start under load.

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --filter '*FSharp*' \
    --artifacts artifacts/benchmarks-fsharp
```

```console
$ uv run python eng/benchmarks/record_fsharp.py \
    --reports artifacts/benchmarks-fsharp/results \
    --tmux-version 3.7d \
    --collected 2026-10-03 \
    --out docs/benchmarks/runs
```

## Regression gate

Timings are not gated in CI: the same case moves by more than half between
runs on one machine, so a threshold loose enough to pass would catch nothing.
What is gated is what does not vary. An integration test counts the tmux
processes a pushed-down query starts and the rows tmux returns through the
connection interceptor, and fails when a listing stops narrowing.

## Control stream probe

[`StreamingProbe`](../../benchmarks/LibTmux.Benchmarks/StreamingProbe.cs)
records raw NDJSON from an owned tmux server. It measures pane-output latency
and throughput for exact payloads between start and end markers. Separate rows
measure notification-buffer overflow and pending-reader disposal. Allocation
and retained-heap figures cover the probe process, not only the stream buffer.
The run row names the source commit and whether the checkout was clean; a
failed Git lookup leaves the commit empty and marks it unclean.

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- \
    --stream-probe artifacts/benchmarks/stream.ndjson
```

`--stream-probe-smoke` runs one sample of each scenario. The full probe writes
twenty measured samples for each payload, with two warmups, and five samples
each for overflow and disposal. It has an eight-minute deadline and writes a
failure row for an error during a sample; setup errors stop the run before a
sample row exists. A buffer has a capacity in events, so
the overflow row uses a burst of rename notifications; it is not a pane-output
loss measurement.

Record a complete run with the shared NDJSON recorder:

```console
$ python3 eng/benchmarks/record_topology.py \
    --probe stream \
    --input artifacts/benchmarks/stream.ndjson \
    --output artifacts/benchmarks/stream.record.json
```

The record retains the raw file's SHA-256, source commit, clean-tree status,
host and runtime, and median, p95, and p99 for each measured stream metric.
Warmups stay in the raw file but do not enter the distributions. The recorder
rejects failed, missing, or reordered samples. Dirty runs require
`--exploratory` and equal source-tree fingerprints captured before and after
the probe; they remain exploratory evidence. Smoke runs report observations
without percentiles.

## A warning worth keeping

The first version of this benchmark discarded five warmup samples. Cases run in
order, and five was not enough to outlast the runtime tiering up and the tmux
binary reaching the page cache, so the first case to run absorbed the cost. That
made chaining one command measure *slower* than chaining fifty — reproducibly,
across two independent runs, which is exactly what makes an artefact convincing.
It reached the README as a table saying fifty commands were faster than one.

Forty warmup samples fixed it. The lesson is in
[`ModeBenchmarkConfig`](../../benchmarks/LibTmux.Benchmarks/ModeBenchmarkConfig.cs):
a result that cannot be true is not a finding, however many times it reproduces.
