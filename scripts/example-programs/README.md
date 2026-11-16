# example-programs

Builds and runs the complete example programs the tmux matrix proves. Each
program under `examples/LibTmux.Examples/Programs` (C#) and
`examples/LibTmux.FSharp.Examples/Programs` (F#) builds into an assembly of its
own, so one build serves every tmux. The program lists are the directories, so
adding a program adds it here.

`dotnet-tmux.yml` runs it. The `build` job builds each kind once:

```console
$ uv run scripts/example-programs/example_programs.py build csharp
```

The matrix runs the built assemblies on a framework against the tmux of the lane:

```console
$ uv run scripts/example-programs/example_programs.py run csharp --framework net10.0
```

`run fsharp` also pipes each program's output to
`scripts/docs/sync_fsharp_snippets.py --expect-output`, which compares it with
what its guide shows. A program that the example manifest marks `requiresTmux`
is skipped when the tmux under test is older, and that tmux is
`LIBTMUX_EXPECTED_TMUX_VERSION` or, for a ref that names no release, what the
built tmux reports.

`dotnet.yml` runs the F# README quickstart from the packed libraries after
building it:

```console
$ uv run scripts/example-programs/example_programs.py quickstart --framework net8.0 --framework net10.0
```

The quickstart waits through a control client, which tmux promises from 3.7. On
an older tmux it prints a warning annotation and a summary line, so the run reads
as skipped, and exits 0.

Run the tests with the [script runner](../README.md#running-the-checks).
