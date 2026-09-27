# LibTmux.FSharp

Compose tmux from F# with task helpers, native sequences, and typed portable
filters over the existing [LibTmux](https://www.nuget.org/packages/LibTmux)
objects. The [F# package](https://www.nuget.org/packages/LibTmux.FSharp) and
core share a repository, release version, and primary author in the `libtmux`
organization.

[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux matrix](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[Run isolated tmux](#quick-start) · [Connect to running tmux](#existing-tmux) ·
[Filter snapshots](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md) ·
[Read control events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md)

A portable relation filter is an F# value after installing the package below:

<!-- fsharp-contract: golden -->
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

This prints `capture depth: Panes`. The filter describes sessions containing
an editor pane; constructing it makes no tmux call. The complete example below
creates a server, captures its object graph, and applies a filter.

Alpha API: pin a package version and upgrade deliberately. The walkthrough
uses .NET SDK 10 and tmux 3.2a through 3.7c on Linux or macOS. The package
targets `net8.0` and `net10.0`.

## Quick start

Install tmux and make sure it is available on `PATH`.

Create an F# console project:

```console
$ dotnet new console --language F# --framework net8.0 --output tmux-demo
```

Enter it:

```console
$ cd tmux-demo
```

Add [LibTmux.FSharp](https://www.nuget.org/packages/LibTmux.FSharp) from NuGet;
it brings in the matching `LibTmux` core package and records the selected
prerelease version in the project:

```console
$ dotnet package add LibTmux.FSharp --prerelease
```

Replace `Program.fs` with this complete program:

<!-- fsharp-snippet: Quickstart run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        let binary =
            match Environment.GetEnvironmentVariable("LIBTMUX_TMUX") with
            | null
            | "" -> "tmux"
            | value -> value

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath = binary
            )

        use! ownedServer = LibTmux.Server.CreateOwnedAsync(options, CancellationToken.None)

        use! _ownedSession =
            ownedServer.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "demo", WindowName = "shell", Command = "/bin/sh"),
                CancellationToken.None
            )

        use! _ownedWorker =
            ownedServer.Value.CreateOwnedSessionAsync(
                NewSessionRequest(Name = "worker", WindowName = "idle", Command = "/bin/sh"),
                CancellationToken.None
            )

        let! connected = LibTmux.Server.ConnectAsync(options, CancellationToken.None)

        let! captured =
            connected |> Server.capture CancellationToken.None SnapshotDepth.Panes

        let session =
            captured.Sessions |> Seq.find (fun candidate -> candidate.Name = "demo")

        let window = session.Windows |> Seq.exactlyOne
        let pane = window.Panes |> Seq.exactlyOne

        let command =
            pane
            |> Pane.currentCommand
            |> Option.defaultWith (fun () -> failwith "The captured pane has no command.")

        let localMatches =
            captured.Panes
            |> Seq.filter (fun candidate -> candidate.Id = pane.Id)
            |> Seq.length

        let hasPane =
            Filter.eq pane.Id PaneFields.id
            |> Filter.any WindowFields.panes
            |> Filter.any SessionFields.windows

        let selected =
            captured.Sessions
            |> Query.matching hasPane
            |> Selection.exactlyOne
            |> Result.defaultWith (fun error -> failwithf "Expected one matching session: %A" error)

        if
            captured.Sessions.Count <> 2
            || captured.Panes.Count <> 2
            || window.Name <> "shell"
            || localMatches <> 1
            || selected.Id <> session.Id
        then
            failwith "The captured graph and portable filter did not agree."

        printfn "%s / %s / %s" session.Name window.Name command
        printfn "local pane matches: %d" localMatches
        printfn "portable match: %s" selected.Name
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

Run it:

```console
$ dotnet run
```

It prints `demo / shell / sh`, `local pane matches: 1`, and
`portable match: demo` on the Linux tmux used for validation. The captured
command name can differ by shell. The program checks the graph and query
result before printing. A second `worker` session also runs a shell; filtering
by the captured pane ID selects one session from two.

`CreateOwnedAsync` starts a server on a unique socket. `ConnectAsync` attaches
a second handle to that socket. `use!` closes the owned sessions and server
when the task ends. `Server.capture` performs one explicit snapshot
acquisition. Traversal through
`captured.Sessions → session.Windows → window.Panes`, `Seq.filter`, and
`Query.matching` then use captured data locally. The portable filter matches
a session through its windows and panes. It does not send a native tmux filter.

The [quickstart source](https://github.com/libtmux/libtmux-dotnet/blob/master/examples/LibTmux.FSharp.Quickstart/Program.fs)
is the published block. CI restores only `LibTmux.FSharp` as a direct package
reference from freshly packed artifacts, then runs this program against real
tmux on both target frameworks.

## Existing tmux

The quickstart's `ConnectAsync(options, ct)` attaches to a running server by
socket name. In an application, use your server's socket name and omit the
owned setup. `ConnectAsync` never starts tmux. A bare `ConnectAsync()` resolves
the default socket; [socket selection](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#where-a-bare-connect-lands)
explains the configuration order. Code running inside a tmux pane can use
`Server.FromEnvironment()` to locate that pane's server.

## Choose a read

| Need | F# call | Result |
| --- | --- | --- |
| Live state | `server.GetSessionsAsync(ct)` or `Server.capture ct depth server` | Task; contacts tmux |
| Application-specific local filter | `captured.Panes |> Seq.filter predicate` | Lazy sequence over captured objects |
| Portable relation filter | `Filter.any` then `Query.matching` | Materialized `IReadOnlyList`; no tmux call |
| A missing live entity | `Server.tryFindPane ct id server` | `Task<Pane option>`; other failures still throw |
| Exactly one match | `Selection.exactlyOne source` | `Result` distinguishing zero from many |

Captured sessions, windows, panes, and IDs are the core .NET types. A window
linked into more than one session has contextual placements; filtering keeps
their order and multiplicity. An uncaptured relationship raises
`IncompleteSnapshotException` rather than behaving as empty.

## Keep going

- [Split panes and send keys](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md): owned scopes and live mutation.
- [Filter snapshots](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md) and [supported fields](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/supported-query-fields.md): quantifiers, capture depth, and the shared schema.
- [Choose an execution mode](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/modes.md) and [read control events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md): scoped clients, consumptive streams, cancellation, and command chains.
- [Call the core from F#](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/interop.md) and [browse signatures](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/api.md).
- [Use tmux from an assistant](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Mcp/README.md): `LibTmux.Mcp` is a separate .NET tool.
- [Read benchmark records](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/benchmarks/README.md): measured core execution modes and benchmark methods.

## Compatibility

| Area | Contract and verification |
| --- | --- |
| .NET | Targets .NET 8 and .NET 10 and uses the matching `LibTmux` package version. |
| tmux | Required Linux CI runs the repository's F# integration example against tmux 3.2a, 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b, and 3.7c on both target frameworks. The README quickstart runs against the runner's tmux in the package workflow. |
| Operating systems | Linux is required CI. An advisory macOS arm64 job runs the example with Homebrew tmux on manual dispatch. Native Windows tmux is unsupported. |
| Trimming and NativeAOT | A Linux consumer publishes and runs the static snapshot and native `Seq` route on both frameworks. |

`Selection.exactlyOne` is unsupported under NativeAOT while FSharp.Core 10.1.302
emits trim and AOT diagnostics for its `Result` return type.
