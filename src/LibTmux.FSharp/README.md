<!-- libtmux-logo -->
<p align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="https://raw.githubusercontent.com/libtmux/libtmux-dotnet/master/src/LibTmux.FSharp/assets/logo.png" width="128" height="128" alt="libtmux for F#">
  </picture>
</p>
<!-- /libtmux-logo -->

# LibTmux.FSharp

Drive tmux from F#: send keys and wait for what a pane prints, run a command
to its exit status, and list and filter sessions, windows, panes and clients
with typed filters tmux evaluates itself. Every function works on the
[LibTmux](https://www.nuget.org/packages/LibTmux) core objects. The
[F# package](https://www.nuget.org/packages/LibTmux.FSharp) and core share a
repository, release version, and primary author in the `libtmux` organization.

[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux matrix](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[Run isolated tmux](#quick-start) · [Connect to running tmux](#existing-tmux) ·
[Send, wait, read](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md#send-wait-read) ·
[Query tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md) ·
[Stream events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md)

Find a shell, send it keys, wait for its output, and run a command:

<!-- fsharp-contract: golden -->
<!-- fsharp-snippet: SendWaitList run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runInShellAsync (cancellationToken: CancellationToken) (server: Server) =
    task {
        // List and filter: tmux narrows the listing, then every row is rechecked.
        let! shells =
            server
            |> Server.panes
            |> Query.where (PaneFields.currentCommand |> Filter.oneOf [ "bash"; "sh"; "zsh" ])
            |> Query.list cancellationToken

        let pane = shells[0]

        // Send keys, then wait for what the program prints instead of sleeping.
        do!
            pane
            |> Pane.sendKeys cancellationToken (SendKeysRequest(Text = "printf 'ready %s\\n' now", Literal = true))

        let! ready =
            pane
            |> Pane.waitForText cancellationToken (TimeSpan.FromSeconds 10.) "ready now"

        // Run a command to its exit status and read what it printed.
        let! listing = pane |> Pane.run cancellationToken (TimeSpan.FromSeconds 30.) "ls /"

        return ready.Found, listing.Succeeded, listing.Output
    }
```
<!-- endfsharp-snippet -->

Building a query reads nothing; `Query.list` asks tmux, which drops panes that
cannot match, and checks every row it returns. `Pane.waitForText` sleeps on
the pane's own output instead of polling, and ends early if the program exits.
`Pane.run` returns the command's exit status and the lines it printed. The
quick start below runs these steps against an isolated tmux server.

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
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null",
                TmuxBinaryPath =
                    (Environment.GetEnvironmentVariable "LIBTMUX_TMUX"
                     |> Option.ofObj
                     |> Option.defaultValue "tmux")
            )

        use! owned = LibTmux.Server.CreateOwnedAsync(options, token)

        for name in [ "build"; "web"; "worker" ] do
            let! _ =
                owned.Value.CreateSessionAsync(NewSessionRequest(Name = name, Command = "/bin/sh"), token)

            ()

        let! server = LibTmux.Server.ConnectAsync(options, token)

        // List and filter: tmux narrows the listing, then every row is rechecked.
        let! build =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.eq "build")
            |> Query.exactlyOne token

        let! others =
            server
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.ne "build")
            |> Query.list token

        let session =
            build
            |> Result.defaultWith (fun error -> failwithf "Expected one build session: %A" error)

        let! panes = session |> Session.panes |> Query.list token
        let pane = panes[0]

        // Send keys, then wait for what the program prints instead of sleeping.
        do!
            pane
            |> Pane.sendKeys token (SendKeysRequest(Text = "printf 'build %s\\n' started", Literal = true))

        let! started =
            pane |> Pane.waitForText token (TimeSpan.FromSeconds 10.) "build started"

        // Run a command to its exit status and read what it printed.
        let! result =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

        printfn "other sessions: %s" (String.Join(", ", [ for session in others -> session.Name ]))
        printfn "wait found: %b" started.Found
        printfn "run: exit %d, output %A" result.ExitStatus.Value (List.ofSeq result.Output)
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

Run it:

```console
$ dotnet run
```

It prints:

<!-- fsharp-output: Quickstart -->
```text
other sessions: web, worker
wait found: true
run: exit 3, output ["ok"]
```
<!-- endfsharp-output -->

`CreateOwnedAsync` starts a server on a unique socket and `use!` stops it when
the task ends. `ConnectAsync` attaches a second handle to that socket, as an
application attaches to a server it did not start. `Query.exactlyOne` returns
a `Result` that says whether no session or several matched. The wait succeeds
whether the line appeared before or after it began, and the run's exit status
comes from the shell, not from reading the screen.

The [quickstart source](https://github.com/libtmux/libtmux-dotnet/blob/master/examples/LibTmux.FSharp.Quickstart/Program.fs)
is the published block. CI restores only `LibTmux.FSharp` as a direct package
reference from freshly packed artifacts, runs this program against real tmux
on both target frameworks, and compares what it prints with the block above.

## Existing tmux

The quickstart's `ConnectAsync(options, ct)` attaches to a running server by
socket name. In an application, use your server's socket name and omit the
owned setup. `ConnectAsync` never starts tmux. A bare `ConnectAsync()` resolves
the default socket; [socket selection](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#where-a-bare-connect-lands)
explains the configuration order. Code running inside a tmux pane can use
`Server.FromEnvironment()` to locate that pane's server.

## Choose a call

| Need | F# call | Result |
| --- | --- | --- |
| List and filter live objects | `Server.panes server \|> Query.where filter \|> Query.list ct` | Task; tmux narrows the listing and every row is rechecked |
| Exactly one match | `Query.exactlyOne ct query` | `Result` distinguishing none from several |
| A missing live entity | `Server.tryFindPane ct id server` | `Task<Pane option>`; other failures still throw |
| Wait for output | `Pane.waitForText ct timeout text pane` | `PaneWaitResult`; ends early when the program exits |
| Run a command to its exit status | `Pane.run ct timeout command pane` | `PaneRunResult` with the status and printed lines |
| A whole object graph | `Server.capture ct depth server` | Snapshot to traverse and filter locally |
| React to events as they happen | `Control.events` or `Control.watchPane` | Cold `IAsyncEnumerable` for a control client |

Captured sessions, windows, panes, and IDs are the core .NET types. A window
linked into more than one session has contextual placements; filtering keeps
their order and multiplicity. An uncaptured relationship raises
`IncompleteSnapshotException` rather than behaving as empty.

## Keep going

- [Send, wait and read; split panes](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md): owned scopes, waits, runs and live mutation.
- [Query tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md) and [supported fields](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/supported-query-fields.md): every level, pushdown, screen search, relations and capture depth.
- [Choose an execution mode](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/modes.md) and [stream events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md): scoped clients, cold streams, pane watches, cancellation, and command chains.
- [Test code that drives tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/testing.md): a private server per test, waits instead of sleeps, and CI setup.
- [Call the core from F#](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/interop.md) and [browse signatures](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/api.md).
- [Share tmux with an assistant](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/mcp.md): the `LibTmux.Mcp` server, a shared socket, and which F# function each tool matches.
- [Read benchmark records](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/benchmarks/README.md): measured core execution modes and benchmark methods.

## Compatibility

| Area | Contract and verification |
| --- | --- |
| .NET | Targets .NET 8 and .NET 10 and uses the matching `LibTmux` package version. |
| F# | Requires FSharp.Core 8.0.100 or newer, so an application keeps its SDK's FSharp.Core. Required CI builds and runs a consumer with the .NET 8 SDK's F# compiler and implicit FSharp.Core. |
| tmux | Required Linux CI runs the repository's F# integration example against tmux 3.2a, 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b, and 3.7c on both target frameworks. The README quickstart runs against the runner's tmux in the package workflow. |
| Operating systems | Linux is required CI. An advisory macOS arm64 job runs the example with Homebrew tmux on manual dispatch. Native Windows tmux is unsupported. |
| Trimming and NativeAOT | Portable filters bind fields without reflection. A Linux consumer publishes and runs captured snapshots, portable filters with relations and regex, and native `Seq` predicates under NativeAOT and trimming on both frameworks. |

`Selection.exactlyOne` is unsupported under NativeAOT while FSharp.Core 10.1.302
emits trim and AOT diagnostics for its `Result` return type.
