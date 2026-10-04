# Getting started with LibTmux.FSharp

Start with the package [quickstart](../../src/LibTmux.FSharp/README.md#quick-start)
for installation and a complete `Program.fs` that runs against an owned tmux
server. This guide continues from that path.

`LibTmux.FSharp` is built on [LibTmux](https://github.com/libtmux/libtmux-dotnet/).
Both packages are maintained in the `libtmux` organization by the same primary
author.

It keeps the core handles and task-based I/O. This example creates a server on
a unique socket, creates one session, lists its pane, splits it, types a line
into the new pane, and reads the resulting pane IDs. Both owned scopes dispose at the end of
the task.

<!-- fsharp-snippet: OwnedWorkflow run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let inspectOwnedSessionAsync (cancellationToken: CancellationToken) =
    task {
        let options =
            ServerConnectionOptions(
                SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! ownedServer = options |> Server.createOwned cancellationToken
        let server = ownedServer.Value

        use! ownedSession =
            server.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), cancellationToken)

        let! panes = ownedSession.Value |> Session.panes |> Query.list cancellationToken

        let! second =
            panes[0] |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

        do! second |> Pane.sendLine cancellationToken "printf 'ready\\n'"

        let! found = server |> Server.tryFindPane cancellationToken second.Id
        let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

        return
            captured.Panes |> Seq.map (fun pane -> pane.Id) |> Seq.toList, found |> Option.map (fun pane -> pane.Id)
    }
```
<!-- endfsharp-snippet -->

`Pane.sendLine` completes after tmux accepts the line; it does not wait for the
shell to print. To wait for what it prints, use `Pane.sendAndWait` below.
`Server.capture` performs I/O, then the ID projection is local.
`Server.tryFindPane` returns `Some pane` after a successful lookup or `None`
when the pane is absent. This example returns two pane IDs and `Some` of the
new pane's ID. Failures and cancellation still propagate.

## Send, wait, read

To act on what a pane prints, wait for it instead of sleeping. This complete
program types a command and waits for its output, waits for a condition over
the whole screen, and runs a command to its exit status:

<!-- fsharp-snippet: SendWaitRead run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-send-wait-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value |> Server.newSession token (SessionSpec.running "work" "/bin/sh")

        let! pane = session |> Session.activePane token

        // Type a line and wait for what it prints. The screen before it and
        // the line's own echo do not count.
        let! ready =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 5.) "echo server ready" "server ready"

        // A condition sees every visible row each time the pane changes.
        do! pane |> Pane.sendLine token "seq 3"

        let! counted =
            pane
            |> Pane.waitUntil token (TimeSpan.FromSeconds 5.) (fun rows -> rows |> Seq.exists ((=) "3"))

        // Running a command waits for its exit status and returns its output.
        let! listing =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'a\\nb\\n'; exit 4"

        let! screen = pane |> Pane.capture token (CapturePaneRequest())

        // One case for each way a wait can end; leaving one out draws a warning.
        let describe wait =
            match wait with
            | PaneWait.Found -> "found"
            | PaneWait.Printed -> "printed"
            | PaneWait.Stopped pattern -> "stopped by " + pattern
            | PaneWait.TimedOut -> "timed out"
            | PaneWait.Ended -> "the pane's program ended"

        printfn "ready: %s" (describe ready)
        printfn "counted: %s" (describe counted)

        match listing with
        | PaneRun.Exited status -> printfn "run: exit %d, output %A" status (List.ofSeq listing.Output)
        | PaneRun.Ended -> printfn "run: the shell exited first"
        | PaneRun.NotStarted -> printfn "run: the shell was not at a prompt"
        | PaneRun.TimedOut -> printfn "run: still running"

        printfn "screen shows the run: %b" (screen |> Seq.exists (fun row -> row = "a"))
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: SendWaitRead -->
```text
ready: found
counted: found
run: exit 4, output ["a"; "b"]
screen shows the run: true
```
<!-- endfsharp-output -->

### Which wait

| You want to | Call | It ends when |
| --- | --- | --- |
| Type a line and wait for its output | `Pane.sendAndWait` | A later line contains the text. The screen before the line and the line's own echo do not count. |
| Send keys by request and wait by patterns | `Pane.sendAndWaitFor` | A pattern or stop pattern matches later output. The echo of literal text does not count; keys sent by name are not discounted. |
| Wait for output you did not type | `Pane.waitForText`, `Pane.waitFor` | A line contains the text. Text already on screen answers at once with `PresentAtEntry`. |
| Wait for a condition over the whole screen | `Pane.waitUntil` | The condition holds over the visible rows, including what a full-screen program draws. |
| Run a command to its exit status | `Pane.run` | The command exits. It returns the status and the lines it printed. |
| Follow output as it prints | `Control.watchPane`, or `Control.watchPanes` for several panes on one client | You stop reading, or the panes are gone; see [streams](streams.md). |

A wait's `PaneWaitResult` falls under one `PaneWait` case, so a match that
leaves one out draws a compiler warning. `result.Found` is the same test as
`PaneWait.Found`, for code that only asks whether the text appeared:

| Case | Outcomes | It means |
| --- | --- | --- |
| `PaneWait.Found` | `Matched`, `PresentAtEntry` | The text or a pattern appeared, before or during the wait. |
| `PaneWait.Printed` | `AnyOutput` | A wait with no pattern saw the pane print something. |
| `PaneWait.Stopped pattern` | `Stopped` | A stop pattern matched; the case carries it. |
| `PaneWait.TimedOut` | `TimedOut` | The time allowed ran out. |
| `PaneWait.Ended` | `PaneExited`, `AlternateScreen` | The pane's program exited, or a full-screen program took over. |

Calling `Pane.sendLine` and then `Pane.waitForText` for text the typed line
contains can end on the shell's echo before the command runs; use
`Pane.sendAndWait` instead. Every wait also ends early when the pane's program
exits during it or a full-screen program takes over, raises
`TmuxPaneException` on a pane whose program had already exited, and sleeps on
the pane's own output through a control client rather than polling. A wait
attaches that client and reads the pane through it, which costs a few
milliseconds more than reading the screen once; for a series of waits on one
session, `use! _ = Session.holdWaitClient ct session` keeps the client attached
so each wait skips that and takes about as long as one read. Read output
already there with `Pane.capture`, and wait for output still to come; the
[wait latency benchmark](../benchmarks/README.md#f-wait-latency) measures each.

`Pane.run` needs the pane at a prompt of `sh`, `ash`, `bash`, `dash`, `zsh` or
a Korn shell; fish, PowerShell and a REPL are refused. It runs the command in a
subshell, so `cd` and `export` do not persist into the pane. A command still
running at the timeout keeps running, and the result reports `TimedOut`. One
that prints more than scrollback holds reports `LinesMissed`, and its `Output`
is then only what the pane still showed; write long output to a file instead.

How each call reports what can go wrong:

| What happened | A wait | `Pane.run` | `Pane.readSince` |
| --- | --- | --- | --- |
| The time ran out | `PaneWait.TimedOut` | `PaneRun.TimedOut`; the command may still be running | — |
| The token was cancelled | `OperationCanceledException` | `LibTmuxException`, matched by `TmuxFailure.MayHaveRun`, once the command was sent | `OperationCanceledException` |
| The pane's program exits during the call | `PaneWait.Ended` | `PaneRun.Ended`, within five seconds | reads the pane as it stands |
| The program had already exited | `TmuxPaneException` | `TmuxPaneException` | `TmuxPaneException` on a read without a position |
| tmux no longer has the pane | `TmuxObjectNotFoundException` | `TmuxObjectNotFoundException` | `TmuxObjectNotFoundException` |

A run cancelled once its command was sent may still be running, so it raises
what `TmuxFailure.MayHaveRun` matches rather than an
`OperationCanceledException`. A cancelled task loses that: `Async.AwaitTask`
and `Task.Wait` raise a bare `TaskCanceledException` in its place, where a
failed task keeps its exception inside an `AggregateException`, which the
`TmuxFailure` patterns look through. In an `async` workflow, await with
`TmuxAsync.awaitTask`, which also keeps a tmux client's cancellation. One
handler covers both:

<!-- fsharp-snippet: RunCancellation -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runTestsAsync (cancellationToken: CancellationToken) (pane: Pane) =
    task {
        try
            let! result =
                pane |> Pane.run cancellationToken (TimeSpan.FromMinutes 5.) "make test"

            match result with
            | PaneRun.Exited 0 -> return "passed"
            | PaneRun.Exited status -> return $"failed with status {status}"
            | PaneRun.Ended -> return "the shell exited before the tests finished"
            | PaneRun.NotStarted -> return "the shell was not at a prompt"
            | PaneRun.TimedOut -> return "still running after five minutes"
        with
        // Cancelled or lost once the command was sent: it may be running.
        // A tmux client cancelled mid-call matches too, even before the
        // command went, erring towards "may have run". That cancellation
        // is an OperationCanceledException, so this case comes first.
        | TmuxFailure.MayHaveRun _ -> return "may have run; read the pane before trying again"
        // Cancelled between tmux calls, before the command was sent.
        | :? OperationCanceledException -> return "cancelled before it was sent"
    }
```
<!-- endfsharp-snippet -->

## Describe a session

Describe a session as F# records, then create it in one call.
`Server.newSession` makes the session with its first window, adds each later
window, and splits each window's panes in order. A chain adds commands tmux
runs together, each acting on what the one before made, and `Server.within`
bounds every command a handle sends:

<!-- fsharp-snippet: BuildSession run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-build-session-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        // Describe the session, then create it in one call: the first window is
        // the one tmux makes with the session, and each split goes beside the
        // pane before it, sized in cells or as a share of the space it splits.
        let dev =
            { SessionSpec.named "dev" with
                Windows =
                    [
                        { WindowSpec.named "editor" with
                            Command = Some "exec sleep 60"
                        }
                        { WindowSpec.named "logs" with
                            Command = Some "exec sleep 60"
                            Splits =
                                [
                                    { SplitSpec.empty with
                                        Direction = Some PaneDirection.Right
                                        Size = Some(SplitSize.Percent 30)
                                        Command = Some "exec sleep 60"
                                    }
                                    { SplitSpec.empty with
                                        Size = Some(SplitSize.Cells 8)
                                        Command = Some "exec sleep 60"
                                    }
                                ]
                        }
                    ]
            }

        let! session = owned.Value |> Server.newSession token dev

        // A chain runs in one tmux invocation; each step acts on what the one
        // before made.
        let! _ =
            owned.Value
            |> Chain.start
            |> Chain.newWindow session "watch"
            |> Chain.splitLeftRight
            |> Chain.sendLine "exec sleep 60"
            |> Chain.run token

        // Every command through this handle, and the handles taken from it,
        // gives tmux five seconds.
        let bounded = owned.Value |> Server.within (TimeSpan.FromSeconds 5.)
        let! windows = bounded |> Server.windows |> Query.list token

        for window in windows do
            let! panes = window |> Window.panes |> Query.list token
            printfn "%s: %d panes" window.Name panes.Count
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: BuildSession -->
```text
editor: 1 panes
logs: 3 panes
watch: 2 panes
```
<!-- endfsharp-output -->

`LibTmux.Workspace` builds the same kind of session from a tmuxp workspace file,
and can wait for each shell's prompt before sending it commands. Add the
package and describe the session, or parse tmuxp YAML:

```console
$ dotnet package add LibTmux.Workspace --prerelease
```

<!-- fsharp-snippet: DescribeSession run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.Workspace

let buildWorkspaceAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        let description =
            WorkspaceFile(
                sessionName = "build",
                windows =
                    [
                        WorkspaceWindow(
                            windowName = "editor",
                            panes = [ WorkspacePane([ "printf 'editing\\n'" ]); WorkspacePane() ]
                        )
                        WorkspaceWindow(windowName = "logs", panes = [ WorkspacePane([ "printf 'tailing\\n'" ]) ])
                    ]
            )

        // Creates the session, its windows and panes, and sends each pane its
        // commands once its shell is ready.
        let! built = WorkspaceBuilder(server).BuildAsync(description, cancellationToken)
        return built.Session.Name, [ for window in built.Windows -> window.Name ]
    }
```
<!-- endfsharp-snippet -->

`WorkspaceBuilder` creates every pane and applies each layout before typing,
then waits for each pane's shell to show a prompt; pass `PaneReadiness.Never` to
type at once. `BuildAsync` returns the session and windows it created.
`WorkspaceFile.Parse` reads the same description from tmuxp YAML text.

## Read a snapshot

For a server the caller already owns, capture once and use F# sequences:

<!-- fsharp-snippet: ReadPaneCommands run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let readPaneCommandsAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

        return captured.Panes |> Seq.choose Pane.currentCommand |> Seq.toList
    }
```
<!-- endfsharp-snippet -->

`Server.capture` performs I/O. The sequence projection only reads the captured
snapshot. A null command becomes `None`; an uncaptured command still raises
`IncompleteSnapshotException`.

Use [portable filters](filters.md) when the condition must become a
`QueryDocument`; use `Seq.filter` for application-specific snapshot work.
The [F# API reference](api.md) is generated from compiled signatures and XML
summaries.

Use the core request records and entity methods for mutations. Task
cancellation stops waiting; it does not undo a mutation that tmux received.

The [F# example](../../examples/LibTmux.FSharp.Examples) is compiled and run
against an owned tmux server. It checks that portable and native F# queries
select the same panes on both target frameworks. CI repeats it against the
freshly packed F# package through an isolated cache. A successful run writes
`PASS F# snapshot and portable query example`.

From the repository root, run that example against real tmux:

```console
$ dotnet run \
    --project examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj \
    --framework net8.0
```
