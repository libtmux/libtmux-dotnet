# checks

Policy checks over documents and sources that the build does not read. Each
exits non-zero and names what disagrees. `dotnet.yml` runs them, and
[CONTRIBUTING.md](../../.github/CONTRIBUTING.md) names the local commands.

| Script | Checks |
| --- | --- |
| `verify_public_api.py` | Ownership, platform and I/O policy in `docs/public-api.json` and the F# baseline, against the compiler inventory in `artifacts/` |
| `verify_capabilities.py` | The C# capability model against `docs/parity/version-deltas.json` |
| `verify_version_literals.py` | No hand-rolled tmux version comparison outside the capability table |
| `verify_tmux_versions.py` | Every list of supported tmux versions against `eng/tmux/versions.json` |
| `verify_changelogs.py` | Each published package keeps its own changelog with an `Unreleased` section |
| `verify_workspace_commands.py` | The command tree the packed `tmux-workspace` tool prints: its commands and the sources of its `import` command |
| `verify_workflows.py` | The CI dependency graph, permissions, coverage and shared-action pins; its dependencies are pinned in `verify_workflows.py.lock` |

Run one from the repository root:

```console
$ uv run python scripts/checks/verify_tmux_versions.py
```

`verify_public_api.py` reads `artifacts/api-inventory.json`, which the
`LibTmux.Engineering` `api-inventory` command writes after a build. Its tests
skip when the file is missing; the
[script runner](../README.md#running-the-checks) gives the two commands that
write it.

Run the tests with the [script runner](../README.md#running-the-checks).
