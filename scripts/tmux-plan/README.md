# tmux-plan

`plan.py` decides which tmux versions, target frameworks and lanes a
`dotnet-tmux` run covers, from `eng/tmux/versions.json` and the triggering
event. The `plan` job in `dotnet-tmux.yml` runs it and publishes the matrix, so
the workflow lists no version.

Print the plan for a push:

```console
$ uv run scripts/tmux-plan/plan.py --event push
```

Scopes are `edges` (oldest and newest release), `full` (the whole table, the
refusal lanes and tmux master) and `custom` (the refs a dispatch names).
[CONTRIBUTING.md](../../.github/CONTRIBUTING.md) describes each.

Run the tests with the [script runner](../README.md#running-the-checks).
