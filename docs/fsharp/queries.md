# Queries with LibTmux.FSharp

Describe a listing with `Server.sessions`, `Server.windows`, `Server.panes`,
`Server.clients`, `Session.windows`, `Session.panes` or `Window.panes`, narrow
it with `Query.where`, `Query.showing` or `Query.whereUnsafe`, and read it with
`Query.list`, `Query.exactlyOne` or `Query.tryExactlyOne`. tmux drops rows that
cannot match before they are read, and every row is rechecked against the
portable filter. Use `Seq.filter` for application predicates over objects you
already hold, and [filters](filters.md) to apply a portable filter to captured
objects and their relations. In an application published with NativeAOT, read one row with
`Query.tryExactlyOne`: `Query.exactlyOne` returns FSharp.Core's `Result`,
which formats itself through `printf`, and NativeAOT rejects that.

These complete programs require .NET 8 or 10 and tmux on Linux or macOS.
Run the commands from this repository's root. Each block can also replace
`Program.fs` in a console project referencing this revision of
`LibTmux.FSharp`. Set `LIBTMUX_TMUX` to select a tmux binary outside `PATH`.

Each program creates a uniquely named server. Its `use!` bindings dispose
the control clients, sessions, and server when the task finishes or fails.
Your existing tmux sessions are unaffected.

## At a glance

Each binding is one task; its annotation is the type the compiler checks.

<!-- fsharp-snippet: QueryAtAGlance -->
```fsharp
open System.Collections.Generic
open System.Threading
open LibTmux
open LibTmux.FSharp

let queryShapesAsync (ct: CancellationToken) (server: Server) (session: Session) (held: Pane list) =
    task {
        // Describing a listing reads nothing.
        let named =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")

        let! (all: IReadOnlyList<Session>) = named |> Query.list ct
        let! (one: Result<Session, CardinalityError>) = named |> Query.exactlyOne ct
        let! (maybe: Session option) = named |> Query.tryExactlyOne ct
        let! (inSession: IReadOnlyList<Pane>) = session |> Session.panes |> Query.list ct

        let! (showingError: IReadOnlyList<Pane>) =
            server
            |> Server.panes
            |> Query.showing (ScreenSearch.Text "ERROR:")
            |> Query.list ct

        let! (withTail: IReadOnlyList<Session>) =
            server
            |> Server.sessions
            |> Query.where (WindowFields.name |> Filter.eq "tail" |> Filter.any SessionFields.windows)
            |> Query.list ct

        let! (active: IReadOnlyList<Pane>) =
            server
            |> Server.panes
            |> Query.whereUnsafe (UnsafeTmuxFilter "#{pane_active}")
            |> Query.list ct

        // Objects already in hand are filtered locally.
        let editors: IReadOnlyList<Pane> =
            held
            |> Query.matching (PaneFields.currentCommand |> Filter.oneOf [ "nvim"; "vim" ])

        return all, one, maybe, inSession, showingError, withTail, active, editors
    }
```
<!-- endfsharp-snippet -->

## Query every level the same way

This program creates a `build` session and a `logs` session whose pane prints
an error. It filters sessions by name and by a window they contain, confines a
query to one session and one window, finds the pane showing the error, and
passes a raw tmux filter through. A filter that reads a relation captures only
the sessions tmux keeps.

```console
$ dotnet run \
    --project examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj \
    --configuration Release \
    --framework net10.0 \
    -p:ExampleProgram=Queries
```

<!-- fsharp-snippet: Queries -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-queries-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        let! build =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(Name = "build", WindowName = "make", Command = "/bin/sh"),
                token
            )

        let! _ =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(
                    Name = "logs",
                    WindowName = "tail",
                    Command = "printf 'ERROR: disk full\\n'; exec sleep 60"
                ),
                token
            )

        let! server = LibTmux.Server.ConnectAsync(options, token)

        let! logs =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "logs")
            |> Query.list token

        let! logPane = logs[0] |> Session.panes |> Query.list token

        let! logged = logPane[0] |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "ERROR:"


        // tmux narrows each listing itself; every row is then rechecked.
        let! named =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")
            |> Query.exactlyOne token

        // A relation filter reads only the sessions whose windows can match.
        let! tailing =
            server
            |> Server.sessions
            |> Query.where (WindowFields.name |> Filter.eq "tail" |> Filter.any SessionFields.windows)
            |> Query.list token

        // Session and window scopes use the same functions.
        let! make =
            build
            |> Session.windows
            |> Query.where (WindowFields.name |> Filter.eq "make")
            |> Query.tryExactlyOne token

        let! makePanes =
            match make with
            | Some window -> window |> Window.panes |> Query.list token
            | None -> failwith "The make window is missing."

        // tmux searches each pane's visible rows, as find-window does.
        let! showingErrors =
            server
            |> Server.panes
            |> Query.showing (ScreenSearch.Text "ERROR:")
            |> Query.list token

        let! errorRow =
            showingErrors[0] |> Pane.findOnScreen token (ScreenSearch.Text "disk full")

        // A raw tmux filter is the escape hatch; nothing rechecks it.
        let! active =
            server
            |> Server.panes
            |> Query.whereUnsafe (UnsafeTmuxFilter "#{pane_active}")
            |> Query.list token

        printfn "logged: %b" logged.Found
        printfn "named: %A" (named |> Result.map (fun session -> session.Name))
        printfn "tailing: %A" [ for session in tailing -> session.Name ]
        printfn "make panes: %d" makePanes.Count
        printfn "error row: %A" errorRow
        printfn "active panes: %d" active.Count

    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: Queries -->
```text
logged: true
named: Ok "build"
tailing: ["logs"]
make panes: 1
error row: Some 1
active panes: 2
```
<!-- endfsharp-output -->

`Query.showing` and `Pane.findOnScreen` search only the rows on screen, as
`find-window -C` does. To search history, capture the pane with
`Pane.capture` and filter the lines. tmux evaluates case-sensitive string,
flag, identifier and count conditions; case-insensitive and regex conditions
are applied only by the recheck.

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
        let! sessions = server |> Server.sessions |> Query.list token
        let! windows = server |> Server.windows |> Query.list token
        let! panes = server |> Server.panes |> Query.list token
        let! clients = server |> Server.clients |> Query.list token

        for session in sessions do
            printfn "Session: %s (%O)" session.Name session.Id

        printfn "Windows: %d; panes: %d; clients: %d" windows.Count panes.Count clients.Count

    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: ServerListings -->
```text
Session: demo ($0)
Session: worker ($1)
Windows: 2; panes: 2; clients: 0
```
<!-- endfsharp-output -->

The two sessions each hold one window and one pane, and no client is attached.
Window listings preserve placements: a window linked into several sessions
can appear several times.

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
        let! windows = server |> Server.windows |> Query.list token
        let! panes = server |> Server.panes |> Query.list token
        let window = windows |> Seq.exactlyOne
        let pane = panes |> Seq.exactlyOne

        use! _control = server |> Control.enter token
        let! clients = server |> Server.clients |> Query.list token
        let client = clients |> Seq.exactlyOne

        let! foundSession = server |> Server.tryFindSession token demo.Value.Id
        let! foundWindow = server |> Server.tryFindWindow token window.Id
        let! foundPane = server |> Server.tryFindPane token pane.Id
        let! foundClient = server |> Server.tryFindClient token client.Name

        let! missingSession =
            server |> Server.tryFindSession token (SessionId Int32.MaxValue)

        let! missingWindow = server |> Server.tryFindWindow token (WindowId Int32.MaxValue)
        let! missingPane = server |> Server.tryFindPane token (PaneId Int32.MaxValue)
        let! missingClient = server |> Server.tryFindClient token (client.Name + "-missing")

        printfn "session: %A" (foundSession |> Option.map (fun found -> found.Name))
        printfn "window: %A" (foundWindow |> Option.map (fun found -> found.Name))
        printfn "pane: %A" (foundPane |> Option.map (fun found -> found.Id = pane.Id))
        printfn "client: %A" (foundClient |> Option.map (fun found -> found.Name = client.Name))

        printfn
            "missing: %A"
            [
                missingSession.IsSome
                missingWindow.IsSome
                missingPane.IsSome
                missingClient.IsSome
            ]
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: ServerLookups -->
```text
session: Some "demo"
window: Some "shell"
pane: Some true
client: Some true
missing: [false; false; false; false]
```
<!-- endfsharp-output -->

Each lookup finds the object it was given, and each lookup of an ID or name
that does not exist returns `None`. `None` means a successful read found no
matching object. Connection failures, command failures, stale server
generations, and cancellation propagate as errors.
