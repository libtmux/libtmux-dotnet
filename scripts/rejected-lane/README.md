# rejected-lane

Runs `MinimumTmuxVersionTests` for one tmux release below the supported
minimum. A daemon below the floor must be refused before it is touched, so the
`rejected` job of `dotnet-tmux.yml` builds that release as the daemon and the
supported 3.3a as the client, once per release the plan lists:

```console
$ uv run scripts/rejected-lane/rejected_lane.py --version 3.1c
```

Which tests apply depends on the release. A client older than 3.2 rejects `-N`,
so the two tests that run the rejected build as a working client are skipped for
3.0a and 3.1c, and the tests of what only such a build does are skipped for the
others. The control client's hook test needs tmux 3.7, which this lane does not
install, so the matrix runs it. A skipped test or fewer tests than expected fails
the lane. The exit status is the tests'.

Run the tests with the [script runner](../README.md#running-the-checks).
