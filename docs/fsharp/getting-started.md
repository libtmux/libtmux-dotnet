# Getting started with LibTmux.FSharp

Start with the package [quickstart](../../src/LibTmux.FSharp/README.md#quick-start)
for installation and a complete `Program.fs` that runs against an owned tmux
server. This guide continues from that path.

`LibTmux.FSharp` is built on [LibTmux](https://github.com/libtmux/libtmux-dotnet/).
Both packages are maintained in the `libtmux` organization by the same primary
author.

It keeps the core handles and task-based I/O. This example creates a server on
a unique socket, creates one session, discovers it through the core API,
splits its pane, sends literal text, and reads the resulting pane IDs. Both
owned scopes dispose at the end of the task.

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

        let! sessions = server.GetSessionsAsync(cancellationToken)
        let! clients = server.GetClientsAsync(cancellationToken)
        let! foundSession = server.FindSessionAsync(ownedSession.Value.Id, cancellationToken)

        if
            sessions.Count <> 1
            || sessions[0].Id <> ownedSession.Value.Id
            || clients.Count <> 0
            || isNull foundSession
        then
            failwith "The owned session was not discoverable on its detached server."

        let! panes = server |> Server.panes |> Query.list cancellationToken

        let first =
            panes
            |> Selection.exactlyOne
            |> Result.defaultWith (fun error -> failwithf "Expected one initial pane: %A" error)

        let! second =
            first |> Pane.split cancellationToken (SplitPaneRequest(Command = "/bin/sh"))

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
shell to print. `Server.capture` performs I/O, then the ID projection is local.
`Server.tryFindPane` returns `Some pane` after a successful lookup or `None`
when the pane is absent. This example returns two pane IDs and `Some` of the
new pane's ID. Failures and cancellation still propagate.

## Send, wait, read

To act on what a pane prints, wait for it instead of sleeping. This complete
program sends a command and waits for its output, waits for a condition over
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

        // Send, then wait for the pane's own output instead of sleeping.
        do!
            pane
            |> Pane.sendKeys token (SendKeysRequest(Text = "printf 'server %s\\n' ready", Literal = true))

        let! ready = pane |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "server ready"

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

`Pane.waitForText` matches one printed line. Text already on screen ends the
wait at once with `PresentAtEntry`, so a wait started after a fast command
still succeeds. `Pane.waitUntil` sees every visible row, including what a
full-screen program draws. `Pane.run` needs the pane at a POSIX shell prompt;
it returns the command's exit status and the lines it printed, and reports
`TimedOut` for a command that is still running. The waits sleep on the pane's
own output through a control client rather than polling.

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

By default `WorkspaceBuilder` waits for each pane's shell prompt before sending
its commands, and `BuildAsync` returns the session and windows it created.
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

Use [portable filters](queries.md) when the condition must become a
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
