# Filters with LibTmux.FSharp

A `Filter<'T>` describes a condition over sessions, windows, panes or clients,
built from the fields in [supported query fields](supported-query-fields.md).
`Query.where` hands it to tmux and rechecks every row tmux returns; see
[queries](queries.md). `Query.matching` and `Filter.toPredicate` apply the same
filter to objects you already hold, which is what this page covers.

## At a glance

Each binding's annotation is the type the compiler checks.

<!-- fsharp-snippet: FilterAtAGlance -->
```fsharp
open System.Collections.Generic
open LibTmux
open LibTmux.FSharp

let filterShapes (panes: Pane list) (capturedSessions: Session list) =
    // A filter is a value; building one makes no tmux call.
    let editor: Filter<Pane> =
        PaneFields.currentCommand |> Filter.oneOf [ "nvim"; "vim" ]

    // The same filter applies to objects already in hand.
    let editors: IReadOnlyList<Pane> = panes |> Query.matching editor
    let isEditor: Pane -> bool = Filter.toPredicate editor
    let firstEditor: Pane option = panes |> List.tryFind isEditor

    // A relation filter reads captured children; its document says how deep.
    let hasEditor: Filter<Session> =
        editor |> Filter.any WindowFields.panes |> Filter.any SessionFields.windows

    let depth: SnapshotDepth = (Filter.toDocument hasEditor).RequiredSnapshotDepth

    let withEditor: IReadOnlyList<Session> =
        capturedSessions |> Query.matching hasEditor

    editors, firstEditor, depth, withEditor
```
<!-- endfsharp-snippet -->

## Captured objects and relations

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

        // LIBTMUX_TMUX picks the tmux CI is testing; without it, the tmux on PATH.
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

        use! owned = options |> Server.createOwned token

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

        let! server = options |> Server.connect token
        let! sessions = server |> Server.sessions |> Query.list token

        let nativeMatches =
            sessions
            |> Seq.filter (fun session -> session.Name.StartsWith("de", StringComparison.Ordinal))
            |> Seq.toList

        let portableMatches =
            sessions |> Query.matching (Filter.startsWith "de" SessionFields.name)

        let! windows = server |> Server.windows |> Query.list token

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
        let! clients = server |> Server.clients |> Query.list token

        let controlClients =
            clients |> Query.matching (Filter.eq true ClientFields.controlMode)

        let names (sessions: seq<LibTmux.Session>) =
            [ for session in sessions -> session.Name ]

        printfn "native: %A" (names nativeMatches)
        printfn "portable: %A" (names portableMatches)
        printfn "windows: %A" [ for window in matchingWindows -> window.Name ]
        printfn "panes: %d" matchingPanes.Count
        printfn "parents: %A" (names matchingParents)
        printfn "control clients: %d" controlClients.Count
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: ServerFilters -->
```text
native: ["demo"]
portable: ["demo"]
windows: ["shell"]
panes: 1
parents: ["demo"]
control clients: 1
```
<!-- endfsharp-output -->

The native and portable session predicates agree on `demo`. The other filters
select the `shell` window, its pane, the pane's parent session, and the one
control client the program opened.

[`Query.matching`](../fsharp-reference/reference/libtmux-fsharp-query.md#matching)
materializes its result locally. It preserves input order and placement
multiplicity, and sends no tmux format filter.

A filter is an F# value; building one makes no tmux call:

<!-- fsharp-snippet: RelationFilter run -->
```fsharp run
open LibTmux.FSharp

let sessionsWithCommands commands =
    Filter.oneOf commands PaneFields.currentCommand
    |> Filter.any WindowFields.panes
    |> Filter.any SessionFields.windows

let editorFilter = sessionsWithCommands [ "nvim"; "vim" ]

printfn "capture depth: %A" (Filter.toDocument editorFilter).RequiredSnapshotDepth
```
<!-- endfsharp-snippet -->

This prints `capture depth: Panes`: the filter matches sessions containing an
editor pane, so it needs panes captured. Capture to the filter document's
`RequiredSnapshotDepth` before matching relations. Uncaptured relationships
raise `IncompleteSnapshotException`.

Only descriptors in [supported query fields](supported-query-fields.md) are
portable. Window placement and parent links are ordinary captured-object data
for native F# predicates.
