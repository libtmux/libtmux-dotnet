# Getting started with LibTmux.FSharp

Start with the package [quickstart](../../src/LibTmux.FSharp/README.md#quick-start)
for installation and a complete `Program.fs` that runs against an owned tmux
server. This guide continues from that path.

`LibTmux.FSharp` is built on [LibTmux](https://github.com/libtmux/libtmux-dotnet/).
Both packages are maintained in the `libtmux` organization by the same primary
author.

It keeps the core handles and task-based I/O. This example creates a server on
a unique socket, creates one session, lists its pane, splits it, sends literal
text, and reads the resulting pane IDs. Both owned scopes dispose at the end of
the task.

<!-- fsharp-snippet: OwnedWorkflow run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let inspectOwnedSessionAsync (cancellationToken: CancellationToken) =
    task {
        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "libtmux-fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! ownedServer = LibTmux.Server.CreateOwnedAsync(options, cancellationToken)
        let server = ownedServer.Value

        use! ownedSession =
            server.CreateOwnedSessionAsync(NewSessionRequest(Name = "demo", Command = "/bin/sh"), cancellationToken)

        let! panes = ownedSession.Value |> Session.panes |> Query.list cancellationToken

        let! second =
            panes[0] |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

        do!
            second
            |> Pane.sendKeys cancellationToken (SendKeysRequest(Text = "printf 'ready\\n'", Literal = true))

        let! found = server |> Server.tryFindPane cancellationToken second.Id
        let! captured = server |> Server.capture cancellationToken SnapshotDepth.Panes

        return
            captured.Panes |> Seq.map (fun pane -> pane.Id) |> Seq.toList, found |> Option.map (fun pane -> pane.Id)
    }
```
<!-- endfsharp-snippet -->

`Pane.sendKeys` completes after tmux accepts the keys; it does not wait for the
shell to print. To wait for what it prints, use `Pane.sendAndWait` below. `Server.capture` performs I/O, then the ID projection is local.
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
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        let! session =
            owned.Value.CreateSessionAsync(NewSessionRequest(Name = "work", Command = "/bin/sh"), token)

        let! panes = session |> Session.panes |> Query.list token
        let pane = panes[0]

        // Type a line and wait for what it prints. The screen before it and
        // the line's own echo do not count.
        let! ready =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 5.) "echo server ready" "server ready"

        // A condition sees every visible row each time the pane changes.
        do! pane |> Pane.sendKeys token (SendKeysRequest(Text = "seq 3", Literal = true))

        let! counted =
            pane
            |> Pane.waitUntil token (TimeSpan.FromSeconds 5.) (fun rows -> rows |> Seq.exists ((=) "3"))

        // Running a command waits for its exit status and returns its output.
        let! listing =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'a\\nb\\n'; exit 4"

        let! screen = pane |> Pane.capture token (CapturePaneRequest())

        printfn "ready: %b" ready.Found
        printfn "counted: %b" counted.Found
        printfn "run: exit %A, output %A" listing.ExitStatus (List.ofSeq listing.Output)
        printfn "screen shows the run: %b" (screen |> Seq.exists (fun row -> row = "a"))
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: SendWaitRead -->
```text
ready: true
counted: true
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

Calling `Pane.sendKeys` and then `Pane.waitForText` for text the typed line
contains can end on the shell's echo before the command runs; use
`Pane.sendAndWait` instead. Every wait also ends early when the pane's program
exits or a full-screen program takes over, and each sleeps on the pane's own
output through a control client rather than polling.

`Pane.run` needs the pane at a prompt of `sh`, `ash`, `bash`, `dash`, `zsh` or
a Korn shell; fish, PowerShell and a REPL are refused. It runs the command in a
subshell, so `cd` and `export` do not persist into the pane. A command still
running at the timeout keeps running, and the result reports `TimedOut`.

## Describe a session

`LibTmux.Workspace` builds a session from a description: its windows, their
panes, and the commands each pane runs, as a tmuxp workspace file does. Add the
package, describe the session with F# lists, and build it on a server:

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

`WorkspaceBuilder` sends each pane its commands as soon as the pane exists; pass
`PaneReadiness.Always` to wait for each shell's prompt first (the default waits
only for zsh). `BuildAsync` returns the session and windows it created.
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
