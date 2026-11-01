<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET F#">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux.FSharp

[![LibTmux.FSharp](https://img.shields.io/nuget/v/LibTmux.FSharp?label=LibTmux.FSharp)](https://www.nuget.org/packages/LibTmux.FSharp)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/fsharp/)

Drive tmux from F#: [list and filter][api-fs-query-list] sessions, windows,
panes and clients with typed filters, [type and wait][api-fs-pane-sendandwait]
for what a pane prints, and [run a command][api-fs-pane-run] to its exit status.
Every function works on the [LibTmux](https://www.nuget.org/packages/LibTmux)
core objects.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux matrix](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

Add it to an F# project:

```console
$ dotnet add package LibTmux.FSharp
```

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

**Contents** — [Quick start](#quick-start) ·
[Find](#find-sessions-windows-and-panes) ·
[Type and wait](#type-into-a-pane-and-wait) ·
[Run](#run-a-command-and-read-its-status) · [Choose a call](#choose-a-call) ·
[Existing tmux](#connect-to-a-running-tmux) · [Leaves out](#what-it-leaves-out)
· [Support](#support-and-status)

## Quick start

Start a server of its own, type a command, wait for its output, run another
command to its exit status, and list sessions with a filter. This is the whole
program:

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

        // One session whose window runs a plain shell, whatever the user's login shell is.
        let! session =
            owned.Value |> Server.newSession token (SessionSpec.running "build" "/bin/sh")

        let! pane = session |> Session.activePane token

        // Type a command and wait for what it prints, not for its echo.
        let! started =
            pane
            |> Pane.sendAndWait token (TimeSpan.FromSeconds 10.) "echo build started" "build started"

        printfn "wait found: %b" started.Found

        // Run a command to its exit status and read what it printed.
        let! result =
            pane |> Pane.run token (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

        match result with
        | PaneRun.Exited status -> printfn "run: exit %d, output %A" status (List.ofSeq result.Output)
        | PaneRun.Ended -> printfn "run: the shell exited first"
        | PaneRun.NotStarted -> printfn "run: the shell was not at a prompt"
        | PaneRun.TimedOut -> printfn "run: still running"

        // List and filter: tmux narrows the listing, then every row is rechecked.
        let! found =
            owned.Value
            |> Server.sessions
            |> Query.where (SessionFields.name |> Filter.startsWith "bu")
            |> Query.list token

        printfn "sessions: %s" (String.Join(", ", [ for listed in found -> listed.Name ]))
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

To run it, make an F# console project and replace `Program.fs` with the program,
with tmux on `PATH`:

```console
$ dotnet new console --language F# --framework net8.0 --output tmux-demo && \
    cd tmux-demo && \
    dotnet add package LibTmux.FSharp && \
    dotnet run
```

It prints:

<!-- fsharp-output: Quickstart -->
```text
wait found: true
run: exit 3, output ["ok"]
sessions: build
```
<!-- endfsharp-output -->

- [`Server.createOwned`][api-fs-server-createowned] starts a server on a unique
  socket with the `tmux` on `PATH`; set `ServerConnectionOptions.TmuxBinaryPath`
  to use another. `use!` stops it when the task ends.
- The wait succeeds whether the line appeared before or after it began.
- The run's exit status comes from the shell, so it does not depend on reading
  the screen.
- The listing reaches tmux as a filter, so tmux returns only the sessions that
  match.

The program is
[published from a checkout](https://github.com/libtmux/libtmux-dotnet/blob/master/examples/LibTmux.FSharp.Quickstart/Program.fs):
`dotnet run --project examples/LibTmux.FSharp.Quickstart` runs it against the
source.

CI restores `LibTmux.FSharp` from freshly packed artifacts, runs it against real
tmux on both target frameworks, and compares what it prints with the block
above.

## Find sessions, windows, and panes

### [`Query.list`][api-fs-query-list]: the panes that match a filter

Describe the listing with [`Server.panes`][api-fs-server-panes] and
[`Query.where`][api-fs-query-where], then read it. Building a query reads
nothing; tmux narrows the listing where it can, and every row it returns is
rechecked:

<!-- fsharp-snippet: ListShellPanes run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let shellPanesAsync (cancellationToken: CancellationToken) (server: Server) =
    server
    |> Server.panes
    |> Query.where (PaneFields.currentCommand |> Filter.oneOf [ "bash"; "sh"; "zsh" ])
    |> Query.list cancellationToken
```
<!-- endfsharp-snippet -->

Run it with a server from [`Server.createOwned`][api-fs-server-createowned]; it
returns the panes whose command is one of the three shells.

[`Query.exactlyOne`][api-fs-query-exactlyone] returns a `Result` for the case
where exactly one match is expected, and
[`Query.atMostOne`][api-fs-query-atmostone] returns an option and raises on
several matches. [`Query.tryExactlyOne`][api-fs-query-tryexactlyone] is the form
to use under NativeAOT.

## Type into a pane and wait

### [`Pane.sendAndWait`][api-fs-pane-sendandwait]: type a line, wait for its answer

It types the line, then waits for a later line to contain the text. The screen
before it and the line's own echo do not count.

From tmux 3.7 it sleeps on the pane's output; earlier releases poll. It ends
early if the program exits while it waits. This example finds a shell, types
into it, and waits:

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

        match shells |> Seq.tryHead with
        | None -> return None
        | Some pane ->
            // Type a command and wait for what it prints, not for its echo.
            let! ready =
                pane
                |> Pane.sendAndWait cancellationToken (TimeSpan.FromSeconds 10.) "echo ready" "ready"

            // Run a command to its exit status and read what it printed.
            let! listing = pane |> Pane.run cancellationToken (TimeSpan.FromSeconds 30.) "ls /"

            match listing with
            | PaneRun.Exited status -> return Some(ready.Found, status, listing.Output)
            | PaneRun.Ended
            | PaneRun.NotStarted
            | PaneRun.TimedOut -> return None
    }
```
<!-- endfsharp-snippet -->

It returns `Some (true, 0, lines)` for the `ls /` listing, or `None` when no
shell pane exists or the shell was not at a prompt.

- [`Pane.waitFor`][api-fs-pane-waitfor] waits for output you did not type, and
  [`Pane.sendAndWaitFor`][api-fs-pane-sendandwaitfor] takes keys and patterns.
- Every wait ends early when the program exits, and raises `TmuxPaneException`
  if it already had.
  [Which wait](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md#which-wait)
  compares them.
- The sample types into the first shell it finds. On a tmux that is already
  running, that may be the terminal you are reading.

## Run a command and read its status

### [`Pane.run`][api-fs-pane-run]: the exit status and what it printed

It returns the lines the command printed, and the `PaneRun` case says how it
ended: the command exited with a status, its shell exited first, it never
started, or it ran out of time:

<!-- fsharp-snippet: RunToExitStatus run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let exitStatusAsync (cancellationToken: CancellationToken) (pane: Pane) =
    task {
        // Run a command to its exit status and read what it printed.
        let! run =
            pane
            |> Pane.run cancellationToken (TimeSpan.FromSeconds 10.) "printf 'ok\\n'; exit 3"

        match run with
        | PaneRun.Exited status -> return Some(status, List.ofSeq run.Output)
        | PaneRun.Ended
        | PaneRun.NotStarted
        | PaneRun.TimedOut -> return None
    }
```
<!-- endfsharp-snippet -->

Against the quick-start server it returns `Some (3, ["ok"])`.

[`Pane.run`][api-fs-pane-run] needs a POSIX shell prompt and refuses fish,
PowerShell, and a REPL.

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
| One session's or window's panes | [`Session.panes`][api-fs-session-panes]` session \|> Query.list ct`; [`Window.panes`][api-fs-window-panes] alike | `IReadOnlyList<Pane>` |
| Panes showing some text | `Server.panes server \|> `[`Query.showing`][api-fs-query-showing]` search \|> Query.list ct` | `IReadOnlyList<Pane>` |
| Exactly one match | `Query.exactlyOne ct query`; [`Query.tryExactlyOne`][api-fs-query-tryexactlyone] under NativeAOT | `Result<'T, CardinalityError>`; `'T option` |
| Find, or create when absent | `Query.atMostOne ct query` | `'T option`; several raise |
| One object by ID | [`Server.tryFindPane`][api-fs-server-tryfindpane]` ct id server` | `Pane option` |
| The pane a session or window shows | [`Session.activePane`][api-fs-session-activepane]` ct session`, [`Window.activePane`][api-fs-window-activepane]` ct window` | `Pane` |

### Type, wait and run

| Need | F# call | Returns |
| --- | --- | --- |
| Type a line, or press a key | [`Pane.sendLine`][api-fs-pane-sendline]` ct line pane`; [`Pane.pressKey`][api-fs-pane-presskey]` ct "C-c" pane` | `Task` |
| Type a line, wait for its output | `Pane.sendAndWait ct timeout line text pane`; [`Pane.sendAndWaitFor`][api-fs-pane-sendandwaitfor] for keys and patterns | `PaneWaitResult`; `.Found`, or match `PaneWait` |
| Wait for output you did not type | [`Pane.waitForText`][api-fs-pane-waitfortext]` ct timeout text pane`; [`Pane.waitFor`][api-fs-pane-waitfor] for patterns | `PaneWaitResult` |
| Wait for a screen condition | [`Pane.waitUntil`][api-fs-pane-waituntil]` ct timeout condition pane` | `PaneWaitResult` |
| Many waits on one session | `use! _ = `[`Session.holdWaitClient`][api-fs-session-holdwaitclient]` ct session` | `IAsyncDisposable`; each wait skips attaching a client, about 5 ms |
| Run a command to its exit status | `Pane.run ct timeout command pane` | `PaneRunResult`; match `PaneRun` |
| Read the screen | [`Pane.capture`][api-fs-pane-capture]` ct request pane` | `IReadOnlyList<string>` |
| What a pane printed since last time | [`Pane.readSince`][api-fs-pane-readsince]` ct position pane` | `PaneOutputSince`; pass its `Position` next time |
| Find text on one screen | [`Pane.findOnScreen`][api-fs-pane-findonscreen]` ct search pane` | row `int option` |

### Build

| Need | F# call | Returns |
| --- | --- | --- |
| Split a pane | [`Pane.split`][api-fs-pane-split]` ct request pane` | the new `Pane` |
| Rename a session or window | [`Window.rename`][api-fs-window-rename]` ct name window` | a handle with the new name |
| Make a window or pane current | [`Window.select`][api-fs-window-select]` ct window`, [`Pane.select`][api-fs-pane-select]` ct pane` | a handle with the state afterwards |
| Kill a session, window or pane | [`Pane.kill`][api-fs-pane-kill]` ct pane` | `Task` |
| Arrange, resize or move a window | [`Window.selectLayout`][api-fs-window-selectlayout]` ct layout window`, [`Window.resize`][api-fs-window-resize]` ct request window`, [`Window.move`][api-fs-window-move]` ct request window` | a handle with the state afterwards |
| Title, resize, swap, respawn or clear a pane | [`Pane.setTitle`][api-fs-pane-settitle]` ct title pane`, [`Pane.resize`][api-fs-pane-resize], [`Pane.swap`][api-fs-pane-swap], [`Pane.respawn`][api-fs-pane-respawn], [`Pane.clearHistory`][api-fs-pane-clearhistory] | the handle, or `Task` |
| Add a window to a session | [`Session.newWindow`][api-fs-session-newwindow]` ct request session` | the new `Window` |
| Create a session running one command | [`Server.newSession`][api-fs-server-newsession]` ct (`[`SessionSpec.running`][api-fs-sessionspec-running]` name command) server` | `Session` |
| Create a session with windows | `Server.newSession ct spec server` | `Session` |
| Several commands, one tmux call | [`Chain.start`][api-fs-chain-start]`  server \|> … \|>  `[`Chain.run`][api-fs-chain-run]` ct` | `TmuxCommandResult` |
| Read or set a typed option | [`Options.get`][api-fs-options-get]` ct key options` | the key's value type |

### Observe

| Need | F# call | Returns |
| --- | --- | --- |
| A whole object graph | [`Server.capture`][api-fs-server-capture]` ct depth server` | snapshot `Server` |
| Live server state | `Mirror.start ct session` | `ServerMirror` |
| Events as they happen | `Control.withSession ct work server` | cold `IAsyncEnumerable` streams |
| One pane's output as it prints | `Control.watchPane pane client`; [`Control.watchPanes`][api-fs-control-watchpanes] for several | events; match `PaneWatch` |
| An assistant on the same tmux | the `LibTmux.Mcp` server | [MCP guide](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/mcp.md) |

### Recover

| Need | F# call | Returns |
| --- | --- | --- |
| Tell failures apart | `TmuxFailure.NotSent`, `Ran`, `MayHaveRun` | active patterns |
| Retry only unsent work | [`Retry.ifNotSent`][api-fs-retry-ifnotsent]` ct retries operation`, or [`Retry.ifNotSentAfter`][api-fs-retry-ifnotsentafter]` ct delays operation` | the operation's result |
| Await a call in an `async` workflow | [`TmuxAsync.awaitTask`][api-fs-tmuxasync-awaittask]` task`, [`TmuxAsync.awaitUnitTask`][api-fs-tmuxasync-awaitunittask]` task` | `Async`; keeps what `MayHaveRun` matches |

### Caveats

- **Queries:** tmux narrows each listing where it can, and every row is
  rechecked.
- **Bounds:** a handle from [`Server.within`][api-fs-server-within] shares its
  bound with the sessions, windows and panes taken from it.
- **Live state:** [`Mirror.start`][api-fs-mirror-start] returns a mirror to
  `use!`. [`Mirror.waitUntil`][api-fs-mirror-waituntil] raises when no view
  matches in time; [`Mirror.tryWaitUntil`][api-fs-mirror-trywaituntil] returns
  `None`.
- **Events:** inside [`Control.withSession`][api-fs-control-withsession],
  [`Control.events`][api-fs-control-events],
  [`Control.watchPane`][api-fs-control-watchpane] and
  [`Control.watchPanes`][api-fs-control-watchpanes] read the client it opens,
  one reader at a time.

## Connect to a running tmux

### [`Server.connect`][api-fs-server-connect]: a server your program did not start

`options |> Server.connect ct` attaches to the server already running on the
socket the options name. It never starts tmux.

Default options choose no socket, so tmux picks the server;
[socket selection](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#serverconnectasync-which-server-it-reaches)
gives the order. Code running inside a tmux pane can use
`Server.FromEnvironment()` to find that pane's server.

## What it leaves out

- **Its own handle types.** Captured sessions, windows, panes, and IDs are the
  core .NET types. A window linked into more than one session has a placement in
  each; filtering keeps their order and multiplicity.
- **Implicit starts.** [`Server.connect`][api-fs-server-connect] never starts
  tmux; [`Server.createOwned`][api-fs-server-createowned] does, and stops it.
- **Control-mode waits on older releases.** Waits use pane output from tmux
  3.7; earlier releases poll.
- **Other shells for [`Pane.run`][api-fs-pane-run].** It needs a POSIX shell
  prompt.
- **Empty answers for missing data.** An uncaptured relationship raises
  `IncompleteSnapshotException`.

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **.NET.** .NET 8 and .NET 10, using the matching `LibTmux` package version.
- **F#.** FSharp.Core 8.0.100 or newer, so an application keeps its SDK's
  FSharp.Core. CI builds and runs a consumer with the .NET 8 SDK's F# compiler.
- **tmux.** A release runs the F# integration example against tmux 3.3a, 3.4,
  3.5, 3.6, 3.7a, 3.7b, 3.7c, and 3.8 on both target frameworks, and each push
  runs it against 3.3a and 3.8.
- **Systems.** Linux, including WSL. macOS is best effort, without CI coverage.
  Native Windows is unsupported: the core marks its tmux calls
  `[UnsupportedOSPlatform("windows")]`.
- **NativeAOT and trimming.** Portable filters bind fields without reflection. A
  Linux consumer publishes and runs snapshots, tmux queries, and portable
  filters under both, on both frameworks.
- **Under NativeAOT,** [`Selection.exactlyOne`][api-fs-selection-exactlyone] and
  [`Query.exactlyOne`][api-fs-query-exactlyone] cannot be published, because
  their `Result` formats through `printf`, which NativeAOT rejects.
- **The NativeAOT alternatives** are
  [`Query.atMostOne`][api-fs-query-atmostone], or
  [`Query.tryExactlyOne`][api-fs-query-tryexactlyone] where none and several can
  be treated alike.

## Documentation

- [API reference](https://libtmux.org/en/fsharp/latest/reference/) and
  [guides](https://libtmux.org/en/fsharp/latest/guides/) on libtmux.org: every
  public symbol, and task-oriented walkthroughs.
- [Source](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.FSharp):
  the package directory on GitHub.
- [Send, wait and read; split panes](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md):
  owned scopes, waits, runs and live mutation.
- [Query tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md),
  [filter captured objects](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/filters.md)
  and
  [supported fields](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/supported-query-fields.md):
  every level, pushdown, screen search, relations and capture depth.
- [Choose an execution mode](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/modes.md)
  and
  [stream events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md):
  scoped clients, cold streams, pane watches, cancellation, and command chains.
- [Test code that drives tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/testing.md):
  a private server per test, waits on state, and CI setup.
- [Share one server across tasks](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/concurrency.md):
  what handles, waits, runs, control clients and mirrors share.
- [Run tmux work in a service](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/service.md):
  one bounded handle, stopping, retries, one writer per pane, and telemetry.
- [Call the core from F#](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/interop.md)
  and
  [browse signatures](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/api.md).
- [Share tmux with an assistant](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/mcp.md):
  the `LibTmux.Mcp` server, a shared socket, and which F# function each tool
  matches.
- [Read benchmark records](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/benchmarks/README.md):
  measured core execution modes and benchmark methods.
- [Changelog](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/CHANGELOG.md):
  what each release of this package changed

[api-fs-chain-run]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-chain-run/
[api-fs-chain-start]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-chain-start/
[api-fs-control-events]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-control-events/
[api-fs-control-watchpane]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-control-watchpane/
[api-fs-control-watchpanes]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-control-watchpanes/
[api-fs-control-withsession]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-control-withsession/
[api-fs-mirror-start]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-mirror-start/
[api-fs-mirror-trywaituntil]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-mirror-trywaituntil/
[api-fs-mirror-waituntil]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-mirror-waituntil/
[api-fs-options-get]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-options-get/
[api-fs-pane-capture]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-capture/
[api-fs-pane-clearhistory]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-clearhistory/
[api-fs-pane-findonscreen]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-findonscreen/
[api-fs-pane-kill]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-kill/
[api-fs-pane-presskey]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-presskey/
[api-fs-pane-readsince]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-readsince/
[api-fs-pane-resize]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-resize/
[api-fs-pane-respawn]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-respawn/
[api-fs-pane-run]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-run/
[api-fs-pane-select]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-select/
[api-fs-pane-sendandwait]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-sendandwait/
[api-fs-pane-sendandwaitfor]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-sendandwaitfor/
[api-fs-pane-sendline]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-sendline/
[api-fs-pane-settitle]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-settitle/
[api-fs-pane-split]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-split/
[api-fs-pane-swap]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-swap/
[api-fs-pane-waitfor]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-waitfor/
[api-fs-pane-waitfortext]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-waitfortext/
[api-fs-pane-waituntil]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-pane-waituntil/
[api-fs-query-atmostone]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-atmostone/
[api-fs-query-exactlyone]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-exactlyone/
[api-fs-query-list]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-list/
[api-fs-query-showing]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-showing/
[api-fs-query-tryexactlyone]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-tryexactlyone/
[api-fs-query-where]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-where/
[api-fs-retry-ifnotsent]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-retry-ifnotsent/
[api-fs-retry-ifnotsentafter]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-retry-ifnotsentafter/
[api-fs-selection-exactlyone]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-selection-exactlyone/
[api-fs-server-capture]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-capture/
[api-fs-server-connect]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-connect/
[api-fs-server-createowned]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-createowned/
[api-fs-server-newsession]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-newsession/
[api-fs-server-panes]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-panes/
[api-fs-server-tryfindpane]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-tryfindpane/
[api-fs-server-within]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-within/
[api-fs-session-activepane]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-session-activepane/
[api-fs-session-holdwaitclient]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-session-holdwaitclient/
[api-fs-session-newwindow]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-session-newwindow/
[api-fs-session-panes]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-session-panes/
[api-fs-sessionspec-running]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-sessionspec-running/
[api-fs-tmuxasync-awaittask]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-tmuxasync-awaittask/
[api-fs-tmuxasync-awaitunittask]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-tmuxasync-awaitunittask/
[api-fs-window-activepane]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-activepane/
[api-fs-window-move]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-move/
[api-fs-window-panes]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-panes/
[api-fs-window-rename]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-rename/
[api-fs-window-resize]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-resize/
[api-fs-window-select]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-select/
[api-fs-window-selectlayout]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-window-selectlayout/
