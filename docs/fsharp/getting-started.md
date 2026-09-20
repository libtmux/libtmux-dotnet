# Getting started with LibTmux.FSharp

`LibTmux.FSharp` keeps the core `LibTmux` handles and task-based I/O. Capture
the state needed by the local query, then use ordinary F# sequences over that
immutable result.

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

Use the core request records and entity methods for mutations. Task
cancellation stops waiting; it does not undo a mutation that tmux received.

`examples/LibTmux.FSharp.Examples` is the compiled real-tmux companion. It
creates an owned server, captures its panes, and checks that the portable and
native F# queries select the same panes on both target frameworks. CI repeats
that example against the freshly packed F# package through an isolated cache.
