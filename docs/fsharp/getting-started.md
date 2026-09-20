# Getting started with LibTmux.FSharp

`LibTmux.FSharp` is the F# companion built on
[LibTmux](https://github.com/libtmux/libtmux-dotnet/). Both packages are
maintained in the `libtmux` organization by the same primary author.

It keeps the core handles and task-based I/O. Capture the state needed by a
local query, then use ordinary F# sequences over that immutable result.

```fsharp
open System.Threading
open LibTmux
open LibTmux.FSharp

let readPaneCommandsAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

        return
            captured.Panes
            |> Seq.choose Pane.currentCommand
            |> Seq.toList
    }
```

`Server.capture` performs I/O. The sequence projection only reads the captured
snapshot. A null command becomes `None`; an uncaptured command still raises
`IncompleteSnapshotException`.

Use [portable filters](queries.md) when the condition must become a
`QueryDocument`; use `Seq.filter` for application-specific snapshot work.
The [F# API reference](api.md) is generated from compiled signatures and XML
summaries.

Use the core request records and entity methods for mutations. Task
cancellation stops waiting; it does not undo a mutation that tmux received.

The [F# example](../../examples/LibTmux.FSharp.Examples) is compiled and run
against an owned tmux server. It checks that portable and native F# queries
select the same panes on both target frameworks. CI repeats it against the
freshly packed F# package through an isolated cache.
