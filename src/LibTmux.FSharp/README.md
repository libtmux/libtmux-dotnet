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

let runInShellAsync
    (cancellationToken: CancellationToken)
    (server: Server)
    =
    task {
        // List and filter: tmux narrows the listing, then each row is
        // rechecked.
        let! shells =
            server
            |> Server.panes
            |> Query.where (
                PaneFields.currentCommand
                |> Filter.oneOf [ "bash"; "sh"; "zsh" ]
            )
            |> Query.list cancellationToken

        match shells |> Seq.tryHead with
        | None -> return None
        | Some pane ->
            // Type a command and wait for what it prints, not for its echo.
            let! ready =
                pane
                |> Pane.sendAndWait
                    cancellationToken
                    (TimeSpan.FromSeconds 10.)
                    "echo ready"
                    "ready"

            // Run a command to its exit status and read what it printed.
            let! listing =
                pane
                |> Pane.run
                    cancellationToken
                    (TimeSpan.FromSeconds 30.)
                    "ls /"

            match listing with
            | PaneRun.Exited status ->
                return Some(ready.Found, status, listing.Output)
            | PaneRun.Ended
            | PaneRun.NotStarted
            | PaneRun.TimedOut -> return None
    }
```
<!-- endfsharp-snippet -->

Building a query reads nothing; `Query.list` asks tmux, which drops panes that
cannot match, and checks every row it returns. `Pane.sendAndWait` types the
line, then waits for a later line to contain the text; the screen before it and
the line's own echo do not count. It sleeps on the pane's output instead of
polling, and ends early if the program exits while it waits. `Pane.run` returns
the lines the command printed, and `PaneRun` tells a command that exited, with
its status, from one whose shell exited first, one that never started, and one
that ran out of time. `server` comes from `Server.createOwned`, which the quick
start below uses to run these steps on an isolated server. Pass a server from
`Server.connect` only with care: the sample types into the first shell it
finds, and on a tmux already running that may be the terminal you are reading.

Alpha API: pin a package version and upgrade deliberately. The walkthrough
uses .NET SDK 10 and tmux 3.2a through 3.7c on Linux or macOS. The package
targets `net8.0` and `net10.0`.

## Choose a call

### Connect

| Need | F# call | Returns |
| --- | --- | --- |
| Start a server you own | `options \|> Server.createOwned ct` | `OwnedServerScope` to `use!` |
| Attach to a running server | `options \|> Server.connect ct` | `Server` |
| Bound every command's time | `Server.within timeout server` | `Server` |

### Find

| Need | F# call | Returns |
| --- | --- | --- |
| List and filter | `Server.panes server \|> Query.where filter \|> Query.list ct` | `IReadOnlyList<Pane>` |
| One session's or window's panes | `Session.panes session \|> Query.list ct`; `Window.panes` alike | `IReadOnlyList<Pane>` |
| Panes showing some text | `Server.panes server \|> Query.showing search \|> Query.list ct` | `IReadOnlyList<Pane>` |
| Exactly one match | `Query.exactlyOne ct query`; `Query.tryExactlyOne` under NativeAOT | `Result<'T, CardinalityError>`; `'T option` |
| Find, or create when absent | `Query.atMostOne ct query` | `'T option`; several raise |
| One object by ID | `Server.tryFindPane ct id server` | `Pane option` |
| The pane a session or window shows | `Session.activePane ct session`, `Window.activePane ct window` | `Pane` |

### Type, wait and run

| Need | F# call | Returns |
| --- | --- | --- |
| Type a line, or press a key | `Pane.sendLine ct line pane`; `Pane.pressKey ct "C-c" pane` | `Task` |
| Type a line, wait for its output | `Pane.sendAndWait ct timeout line text pane`; `Pane.sendAndWaitFor` for keys and patterns | `PaneWaitResult`; `.Found`, or match `PaneWait` |
| Wait for output you did not type | `Pane.waitForText ct timeout text pane`; `Pane.waitFor` for patterns | `PaneWaitResult` |
| Wait for a screen condition | `Pane.waitUntil ct timeout condition pane` | `PaneWaitResult` |
| Many waits on one session | `use! _ = Session.holdWaitClient ct session` | `IAsyncDisposable`; each wait skips attaching a client, about 5 ms |
| Run a command to its exit status | `Pane.run ct timeout command pane` | `PaneRunResult`; match `PaneRun` |
| Read the screen | `Pane.capture ct request pane` | `IReadOnlyList<string>` |
| What a pane printed since last time | `Pane.readSince ct position pane` | `PaneOutputSince`; pass its `Position` next time |
| Find text on one screen | `Pane.findOnScreen ct search pane` | row `int option` |

### Build

| Need | F# call | Returns |
| --- | --- | --- |
| Split a pane | `Pane.split ct request pane` | the new `Pane` |
| Rename a session or window | `Window.rename ct name window` | a handle with the new name |
| Make a window or pane current | `Window.select ct window`, `Pane.select ct pane` | a handle with the state afterwards |
| Kill a session, window or pane | `Pane.kill ct pane` | `Task` |
| Arrange, resize or move a window | `Window.selectLayout ct layout window`, `Window.resize ct request window`, `Window.move ct request window` | a handle with the state afterwards |
| Title, resize, swap, respawn or clear a pane | `Pane.setTitle ct title pane`, `Pane.resize`, `Pane.swap`, `Pane.respawn`, `Pane.clearHistory` | the handle, or `Task` |
| Add a window to a session | `Session.newWindow ct request session` | the new `Window` |
| Create a session running one command | `Server.newSession ct (SessionSpec.running name command) server` | `Session` |
| Create a session with windows | `Server.newSession ct spec server` | `Session` |
| Several commands, one tmux call | `Chain.start server \|> … \|> Chain.run ct` | `TmuxCommandResult` |
| Read or set a typed option | `Options.get ct key options` | the key's value type |

### Observe

| Need | F# call | Returns |
| --- | --- | --- |
| A whole object graph | `Server.capture ct depth server` | snapshot `Server` |
| Live server state | `Mirror.start ct session` | `ServerMirror` |
| Events as they happen | `Control.withSession ct work server` | cold `IAsyncEnumerable` streams |
| One pane's output as it prints | `Control.watchPane pane client`; `Control.watchPanes` for several | events; match `PaneWatch` |
| An assistant on the same tmux | the `LibTmux.Mcp` server | [MCP guide](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/mcp.md) |

### Recover

| Need | F# call | Returns |
| --- | --- | --- |
| Tell failures apart | `TmuxFailure.NotSent`, `Ran`, `MayHaveRun` | active patterns |
| Retry only unsent work | `Retry.ifNotSent ct retries operation`, or `Retry.ifNotSentAfter ct delays operation` | the operation's result |
| Await a call in an `async` workflow | `TmuxAsync.awaitTask task`, `TmuxAsync.awaitUnitTask task` | `Async`; keeps what `MayHaveRun` matches |

### Caveats

- **Queries:** tmux narrows each listing where it can, and every row is
  rechecked.
- **Waits:** `Pane.sendAndWait` ignores the line's echo. Every wait ends early
  when the program exits, and raises `TmuxPaneException` if it already had.
  [Which wait](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md#which-wait)
  compares them and their `PaneWait` outcomes.
- **Runs:** `Pane.run` needs a POSIX shell prompt; it refuses fish, PowerShell and a REPL.
- **Bounds:** a handle from `Server.within` shares its bound with the
  sessions, windows and panes taken from it.
- **Live state:** `Mirror.start` returns a mirror to `use!`.
  `Mirror.waitUntil` raises when no view matches in time;
  `Mirror.tryWaitUntil` returns `None`.
- **Events:** inside `Control.withSession`, `Control.events`,
  `Control.watchPane` and `Control.watchPanes` read the client it opens, one
  reader at a time.

Captured sessions, windows, panes, and IDs are the core .NET types. A window
linked into more than one session has contextual placements; filtering keeps
their order and multiplicity. An uncaptured relationship raises
`IncompleteSnapshotException` rather than behaving as empty.

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

        // A server of its own on a private socket, without user configuration.
        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        // One session whose window runs a plain shell, not the login shell.
        let! session =
            owned.Value
            |> Server.newSession token (SessionSpec.running "build" "/bin/sh")

        let! pane = session |> Session.activePane token

        // Type a command and wait for what it prints, not for its echo.
        let! started =
            pane
            |> Pane.sendAndWait
                token
                (TimeSpan.FromSeconds 10.)
                "echo build started"
                "build started"

        printfn "wait found: %b" started.Found

        // Run a command to its exit status and read what it printed.
        let! result =
            pane
            |> Pane.run
                token
                (TimeSpan.FromSeconds 10.)
                "printf 'ok\\n'; exit 3"

        match result with
        | PaneRun.Exited status ->
            printfn "run: exit %d, output %A" status (List.ofSeq result.Output)
        | PaneRun.Ended -> printfn "run: the shell exited first"
        | PaneRun.NotStarted -> printfn "run: the shell was not at a prompt"
        | PaneRun.TimedOut -> printfn "run: still running"

        // List and filter: tmux narrows the listing, then each row is
        // rechecked.
        let! found =
            owned.Value
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")
            |> Query.list token

        printfn
            "sessions: %s"
            (String.Join(", ", [ for listed in found -> listed.Name ]))
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
wait found: true
run: exit 3, output ["ok"]
sessions: build
```
<!-- endfsharp-output -->

`Server.createOwned` starts a server on a unique socket, with the `tmux` on
`PATH`; set `ServerConnectionOptions.TmuxBinaryPath` to use another. `use!`
stops it when the task ends. The wait succeeds whether the line appeared
before or after it began, and the run's exit status comes from the shell, not
from reading the screen. The listing reaches tmux as a filter, so tmux returns
only the sessions that match.

The [quickstart source](https://github.com/libtmux/libtmux-dotnet/blob/master/examples/LibTmux.FSharp.Quickstart/Program.fs)
is the published block. From a checkout,
`dotnet run --project examples/LibTmux.FSharp.Quickstart` runs it against the
source. CI instead passes `-p:UsePackageReferences=true`, restores only
`LibTmux.FSharp` from freshly packed artifacts, runs the program against real
tmux on both target frameworks, and compares what it prints with the block
above.

## Existing tmux

`options |> Server.connect ct` attaches to a server already running on the
socket the options name; use it in place of `Server.createOwned` for a server
your program did not start. `Server.connect` never starts tmux. Default options
resolve the default socket; [socket selection](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#where-a-bare-connect-lands)
explains the configuration order. Code running inside a tmux pane can use
`Server.FromEnvironment()` to locate that pane's server.

## Keep going

- [Send, wait and read; split panes](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md): owned scopes, waits, runs and live mutation.
- [Query tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md), [filter captured objects](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/filters.md) and [supported fields](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/supported-query-fields.md): every level, pushdown, screen search, relations and capture depth.
- [Choose an execution mode](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/modes.md) and [stream events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md): scoped clients, cold streams, pane watches, cancellation, and command chains.
- [Test code that drives tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/testing.md): a private server per test, waits instead of sleeps, and CI setup.
- [Share one server across tasks](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/concurrency.md): what handles, waits, runs, control clients and mirrors share.
- [Run tmux work in a service](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/service.md): one bounded handle, stopping, retries, one writer per pane, and telemetry.
- [Call the core from F#](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/interop.md) and [browse signatures](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/api.md).
- [Share tmux with an assistant](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/mcp.md): the `LibTmux.Mcp` server, a shared socket, and which F# function each tool matches.
- [Read benchmark records](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/benchmarks/README.md): measured core execution modes and benchmark methods.

## Compatibility

| Area | Contract and verification |
| --- | --- |
| .NET | Targets .NET 8 and .NET 10 and uses the matching `LibTmux` package version. |
| F# | Requires FSharp.Core 8.0.100 or newer, so an application keeps its SDK's FSharp.Core. Required CI builds and runs a consumer with the .NET 8 SDK's F# compiler and implicit FSharp.Core. |
| tmux | Required Linux CI runs the repository's F# integration example against tmux 3.2a, 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b, and 3.7c on both target frameworks. The README quickstart runs against the runner's tmux in the package workflow. |
| Operating systems | Linux is required CI. An advisory macOS arm64 job runs the example with Homebrew tmux on manual dispatch. Native Windows is unsupported: the core marks its tmux calls `[UnsupportedOSPlatform("windows")]`. WSL runs the Linux build, which CI does not exercise separately. |
| Trimming and NativeAOT | Portable filters bind fields without reflection. A Linux consumer publishes and runs captured snapshots, a tmux query, portable filters with relations and regex, and native `Seq` predicates under NativeAOT and trimming on both frameworks. |

`Selection.exactlyOne` and `Query.exactlyOne` return FSharp.Core's `Result`,
whose compiler-generated `ToString` formats through `printf`, which NativeAOT
publication rejects. Under NativeAOT, read one row with `Query.atMostOne`,
which still raises on several matches, or with `Query.tryExactlyOne` where
none and several may be treated alike. The NativeAOT consumer runs both.
