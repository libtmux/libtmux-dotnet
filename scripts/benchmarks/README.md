# benchmarks

Recorders that turn BenchmarkDotNet output into a record naming the commit,
runtime, host and tmux that produced it. [docs/benchmarks](../../docs/benchmarks/README.md)
documents the runs and where records are kept.

| Script | Records |
| --- | --- |
| `record_fsharp.py` | The F# benchmarks; `benchmarks.yml` runs it, and `--gate` fails the run when pushdown is not faster than listing everything |
| `record_modes.py` | The execution-mode workloads |
| `record_topology.py` | Interleaved topology samples |

Record a run from a clean tree:

```console
$ uv run python scripts/benchmarks/record_fsharp.py \
    --reports artifacts/benchmarks-fsharp/results \
    --tmux-version 3.7c \
    --collected "$(date -u +%F)" \
    --out artifacts/benchmark-record
```

Run the tests with the [script runner](../README.md#running-the-checks).
