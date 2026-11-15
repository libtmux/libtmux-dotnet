# Scripts

Support scripts for CI, releases, generated documents and maintainer tasks.
[SCRIPTS.md](../.github/SCRIPTS.md) says what may stay here. Each script is
typed Python with a PEP 723 header, run through `uv`, and lives in a directory
of its own with a README and tests.

| Directory | Job |
| --- | --- |
| [`checks/`](checks/README.md) | Repository policy checks that run in `dotnet.yml` |
| [`docs/`](docs/README.md) | Generate and verify the API reference, snippets and example guides |
| [`tmux-plan/`](tmux-plan/README.md) | Decide which tmux versions and lanes a CI run covers |
| [`matrix-evidence/`](matrix-evidence/README.md) | Record the tmux version matrix at a release boundary |
| [`benchmarks/`](benchmarks/README.md) | Turn BenchmarkDotNet output into a record |
| [`mcp-tools/`](mcp-tools/README.md) | Write the MCP tool reference from a running server |
| [`package-review/`](package-review/README.md) | Pack and inspect a prerelease for a downstream review |
| [`quality/`](quality/README.md) | Print the raw measures the quality bar cites |
| [`wrapped-captures/`](wrapped-captures/README.md) | Regenerate the wrapped-capture test fixture from real tmux |

## Running the checks

`run_checks.py` runs `ruff format --check`, `ruff check`, `ty check` and
`pytest` over this directory, against the dependencies pinned in
`run_checks.py.lock`. `pytest` collects doctests and runs async tests through
`pytest-asyncio`. Pick one check, `format`, `lint`, `types` or `tests`:

```console
$ uv run --locked --script scripts/run_checks.py tests
```

Arguments after `tests` go through to pytest. Refresh the lock deliberately:

```console
$ uv lock --script scripts/run_checks.py
```
