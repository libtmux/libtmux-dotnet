# Queries with LibTmux.FSharp

Read sessions, windows, panes, and clients with the
[`Server` functions](../fsharp-reference/reference/libtmux-fsharp-server.md).
Use `Seq.filter` for application predicates and `Filter` for conditions that
can also become a portable `QueryDocument`.

These complete programs require .NET 8 or 10 and tmux on Linux or macOS.
Run the commands from this repository's root. Each block can also replace
`Program.fs` in a console project referencing this revision of
`LibTmux.FSharp`. Set `LIBTMUX_TMUX` to select a tmux binary outside `PATH`.

Each program creates a uniquely named server. Its `use!` bindings dispose
the control clients, sessions, and server when the task finishes or fails.
Your existing tmux sessions are unaffected.

## List sessions, windows, panes, and clients

This program creates two detached sessions, each with one window and pane.
The four reads report those objects and an empty client list. A listing
captures scalar fields; it does not populate child relations.

```console
$ dotnet run \
    --project examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj \
    --configuration Release \
    --framework net10.0 \
    -p:ExampleProgram=ServerListings
```

<!-- fsharp-snippet: ServerListings -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-listings-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! _demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "jobs", Command = "/bin/sh"),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! sessions = server |> Server.listSessions token
        let! windows = server |> Server.listWindows token
        let! panes = server |> Server.listPanes token
        let! clients = server |> Server.listClients token

        for session in sessions do
            printfn "Session: %s (%O)" session.Name session.Id

        printfn "Windows: %d; panes: %d; clients: %d" windows.Count panes.Count clients.Count

        if
            sessions.Count <> 2
            || windows.Count <> 2
            || panes.Count <> 2
            || clients.Count <> 0
        then
            failwith "Expected two detached sessions, each with one window and pane."
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

The output names `demo` and `worker`, then reports two windows, two panes,
and zero clients. Window listings preserve placements: a window linked into
several sessions can appear several times.

## Look up an object and handle absence

Session, window, and pane lookups take typed IDs. Client lookup takes an
exact, case-sensitive name. The program attaches a control client so it can
exercise successful client lookup without an interactive terminal.

```console
$ dotnet run \
    --project examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj \
    --configuration Release \
    --framework net10.0 \
    -p:ExampleProgram=ServerLookups
```

<!-- fsharp-snippet: ServerLookups -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-lookups-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! windows = server |> Server.listWindows token
        let! panes = server |> Server.listPanes token
        let window = windows |> Seq.exactlyOne
        let pane = panes |> Seq.exactlyOne

        use! _control = server |> Control.enter token
        let! clients = server |> Server.listClients token
        let client = clients |> Seq.exactlyOne

        let! foundSession = server |> Server.tryFindSession token demo.Value.Id
        let! foundWindow = server |> Server.tryFindWindow token window.Id
        let! foundPane = server |> Server.tryFindPane token pane.Id
        let! foundClient = server |> Server.tryFindClient token client.Name

        if
            (foundSession |> Option.map (fun value -> value.Id)) <> Some demo.Value.Id
            || (foundWindow |> Option.map (fun value -> value.Id)) <> Some window.Id
            || (foundPane |> Option.map (fun value -> value.Id)) <> Some pane.Id
            || (foundClient |> Option.map (fun value -> value.Name)) <> Some client.Name
        then
            failwith "A lookup did not return its requested entity."

        let! missingSession =
            server |> Server.tryFindSession token (SessionId Int32.MaxValue)

        let! missingWindow = server |> Server.tryFindWindow token (WindowId Int32.MaxValue)
        let! missingPane = server |> Server.tryFindPane token (PaneId Int32.MaxValue)
        let! missingClient = server |> Server.tryFindClient token (client.Name + "-missing")

        if
            Option.isSome missingSession
            || Option.isSome missingWindow
            || Option.isSome missingPane
            || Option.isSome missingClient
        then
            failwith "A missing entity must return None after a successful read."

        match foundSession with
        | Some session -> printfn "Found session: %s" session.Name
        | None -> failwith "The owned session disappeared."

        printfn "Found window, pane and control client; missing lookups returned None."
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

The program prints `Found session: demo` and verifies successful and absent
lookups for all four object kinds. `None` means a successful read found no
matching object. Connection failures, command failures, stale server
generations, and cancellation propagate as errors.

## Filter fields and captured relations

Use a listing for scalar predicates, such as matching a session name.
Capture child relations before applying a predicate that traverses them.
This program compares a native F# predicate with a portable filter, finds
the session containing a particular pane, and selects control clients.

```console
$ dotnet run \
    --project examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj \
    --configuration Release \
    --framework net10.0 \
    -p:ExampleProgram=ServerFilters
```

<!-- fsharp-snippet: ServerFilters -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let binary =
            Environment.GetEnvironmentVariable("LIBTMUX_TMUX")
            |> Option.ofObj
            |> Option.defaultValue "tmux"

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-filters-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        use! demo =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        use! _worker =
            owned.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "jobs", Command = "/bin/sh"),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)
        let! sessions = server |> Server.listSessions token

        let nativeMatches =
            sessions
            |> Seq.filter (fun session -> session.Name.StartsWith("de", StringComparison.Ordinal))
            |> Seq.toList

        let portableMatches =
            sessions |> Query.matching (Filter.startsWith "de" SessionFields.name)

        let! windows = server |> Server.listWindows token

        let matchingWindows =
            windows |> Query.matching (Filter.eq "shell" WindowFields.name)

        let! captured = server |> Server.capture token SnapshotDepth.Panes

        let demoSession =
            captured.Sessions |> Seq.find (fun session -> session.Id = demo.Value.Id)

        let demoPane =
            demoSession.Windows
            |> Seq.collect (fun window -> window.Panes)
            |> Seq.exactlyOne

        let paneFilter = Filter.eq demoPane.Id PaneFields.id
        let matchingPanes = captured.Panes |> Query.matching paneFilter

        let hasDemoPane =
            paneFilter |> Filter.any WindowFields.panes |> Filter.any SessionFields.windows

        let matchingParents = captured.Sessions |> Query.matching hasDemoPane

        use! _control = server |> Control.enter token
        let! clients = server |> Server.listClients token

        let controlClients =
            clients |> Query.matching (Filter.eq true ClientFields.controlMode)

        if
            (nativeMatches |> List.map (fun session -> session.Id)) <> [ demo.Value.Id ]
            || (portableMatches |> Seq.map (fun session -> session.Id) |> Seq.toList)
               <> [ demo.Value.Id ]
            || matchingWindows.Count <> 1
            || matchingPanes.Count <> 1
            || (matchingParents |> Seq.exactlyOne).Id <> demo.Value.Id
            || controlClients.Count <> 1
        then
            failwith "The native, portable and relation filters selected unexpected entities."

        printfn "Native and portable session filters: demo"
        printfn "Window: shell; panes: %d; parent: demo; control clients: %d" matchingPanes.Count controlClients.Count
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

Both session predicates select `demo`. The other filters select the
`shell` window, its pane, the pane's parent session, and one control client.

[`Query.matching`](../fsharp-reference/reference/libtmux-fsharp-query.md#matching)
materializes its result locally. It preserves input order and placement
multiplicity, and sends no tmux format filter.

Capture to the filter document's `RequiredSnapshotDepth` before matching
relations. Uncaptured relationships raise `IncompleteSnapshotException`.

Only descriptors in [supported query fields](supported-query-fields.md) are
portable. `Pane.currentPath`, window placement, geometry, and parent links are
ordinary captured-object data for native F# predicates.
