# Choosing a mode

Three ways to reach tmux. Which one you are using is visible where the call
starts, never in a flag buried in options, and all three are supported on every
tmux this library supports.

| Mode | Flip it on | Dispatch | Example output |
|---|---|---|---|
| [One-shot](one-shot.md) | `session.CreateWindowAsync(...)` | 1 command, awaited | `@1 1:build` |
| [Control](control-mode.md) | `server.EnterControlModeAsync(ct)` | 1 client, streamed | `%window-add @1` |
| [Chained](chaining.md) | `server.Chain()...ExecuteAsync(ct)` | N batched, 1 invocation | `@1` |

Example output is captured from a live tmux and refreshed per release. It shows
the shape of what comes back, not a byte-exact string to assert against.

## The same task, three ways

Create a window named `build`.

```csharp
Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "build"), ct);
```

```csharp
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);
```

```csharp
await server.Chain().Then("new-window", "-d", "-n", "build").ExecuteAsync(ct);
```

## What each costs

One-shot starts a tmux client per command. Chaining starts one for the whole
sequence. Control mode starts one and keeps it, so commands after the first pay
a round trip rather than a process.

The number that belongs to the library is the **marginal** one: what a command
costs *in addition to* the first, which is the difference between fifty and one
over forty-nine. Absolute timings belong to the machine — the same host measured
a one-shot command at 2.4 ms and at 19 ms on the same day — so they are recorded
per run rather than quoted here.

| Mode | Cost of one more command | Because |
|---|---:|---|
| One-shot | ~2,300 us | it starts another tmux process |
| Control | ~205 us | it makes another round trip on an open connection |
| Chained | ~21 us | it appends to a command line that is already being sent |

Medians of 100 samples, tmux 3.7b, `net10.0`, 2026-08-16 —
[the full distribution, host and commit](../benchmarks/runs/2026-08-16-tmux-3.7b.md).

### The absolute numbers, and why they are downstairs

| Commands | Mode | Median | p95 | Allocated |
|---:|---|---:|---:|---:|
| 1 | One-shot | 2.81 ms | 4.03 ms | 292 KB |
| 1 | Chained | 3.02 ms | 4.43 ms | 291 KB |
| 1 | Control | 0.43 ms | 0.66 ms | — |
| 50 | One-shot | 115.93 ms | 155.43 ms | 14,490 KB |
| 50 | Chained | 4.03 ms | 5.76 ms | 368 KB |
| 50 | Control | 10.49 ms | 15.63 ms | 60 KB |

At one command, one-shot and chained are the same measurement: both start
exactly one process, and the allocations say so — 292 KB against 291 KB. Chaining
is not a way to make a single command faster and the table should not be read as
claiming it is.

At fifty, the modes separate for reasons that survive a change of machine:
one-shot pays fifty process starts, chaining pays one, control mode pays one
plus fifty round trips.

**The crossover between chaining and control mode is not portable.** Chaining
wins here because a process start costs about 2.4 ms while fifty round trips
cost about 10 ms. On a host where process starts are expensive — a loaded CI
runner, a container with a cold page cache — the order reverses, and it has
reversed in a recorded run on this machine. If the choice between those two
matters to you, measure it where it will run.

**Allocation is the column that does not move.** It repeated byte-for-byte
across runs whose timings differed by a factor of five, so it is what to check a
change against.

## Recording your own

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- --filter '*ModeBenchmarks*' --artifacts artifacts/benchmarks
```

The project multi-targets, so the framework has to be named: the numbers above
are `net10.0`, and running the other one measures something else. Set
`LIBTMUX_TMUX` to measure a tmux other than the one on the path.

See [docs/benchmarks](../benchmarks/README.md) for how a run becomes a record,
and for the warmup mistake that once put an impossible number in this table.

## Version differences

Behavior that differs across tmux 3.2a to 3.7b goes through the capability
model, and each difference has a row with a real-server proof in
[the parity ledger](../parity/version-deltas.json).
