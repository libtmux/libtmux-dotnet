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

[Run the example](#quick-start) · [Connect to running tmux](#existing-tmux) ·
[Send, wait, read](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/getting-started.md#send-wait-read) ·
[Query tmux](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/queries.md) ·
[Stream events](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/fsharp/streams.md)

Create a session, add a window, and remove the session when the task ends:

<!-- fsharp-contract: golden -->
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
        let server = LibTmux.Server.Open()

        do!
            server
            |> Server.withNewSession token (SessionSpec.running "build" "/bin/sh") (fun session ->
                task {
                    let! window =
                        session
                        |> Session.newWindow token (NewWindowRequest(Name = "tests", Command = "/bin/sh"))

                    printfn "window: %s" window.Name
                    let! windows = session |> Session.windows |> Query.list token
                    printfn "windows: %d" windows.Count
                })
    }

try
    runAsync().GetAwaiter().GetResult()
with error ->
    error
    |> Control.cleanupFailure
    |> Option.iter (fun cleanup -> eprintfn "Cleanup failed: %O" cleanup)

    reraise ()
```
<!-- endfsharp-snippet -->

`LibTmux.Server.Open()` captures the endpoint and child environment when you
construct it. `Server.withNewSession` owns the created session through the
callback, including its windows and panes. It awaits cleanup after success,
exception, or cancellation. If work and cleanup both fail,
`Control.cleanupFailure` returns the cleanup exception while the original
exception retains its type and cancellation token.

Alpha API: pin a package version and upgrade deliberately. The walkthrough
uses .NET SDK 10 and tmux 3.2a through 3.7c on Linux or macOS. The package
targets `net8.0` and `net10.0`.

## Choose a call

### Connect

| Need | F# call | Returns |
| --- | --- | --- |
| Capture the ordinary endpoint | `LibTmux.Server.Open()` | `Server` |
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
| Scope a new session to a task | `Server.withNewSession ct spec work server` | the task result after cleanup |
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

Use the complete `Program.fs` shown above.

Run it:

```console
$ dotnet run
```

It prints:

<!-- fsharp-output: Quickstart -->
```text
window: tests
windows: 2
```
<!-- endfsharp-output -->

The example uses explicit options, `LIBTMUX_SOCKET_PATH`,
`LIBTMUX_SOCKET_NAME`, the current `TMUX` context, or the named default, in
that order. A selected value must be valid; an invalid value raises instead
of selecting a different endpoint. `TMUX_TMPDIR` determines the named-socket
root at construction. Later host-environment edits cannot redirect commands
or cleanup. The handle borrows the server; the session scope removes only
the session it created. An existing session named `build` causes creation to
fail without taking ownership of that session.

`Server.newSession` rolls back failures once the core knows the creation
identity, including failed initial readback and later layout steps. Creation
retains the core daemon token with its receipt; cleanup checks it with PID and
start time before destruction. The reserved server option
`@libtmux_owner_generation` holds 32 ASCII hexadecimal characters. A valid
value is reused; an absent option is initialized, and empty or malformed
existing metadata is rejected. Do not edit it while owners exist. Cleanup
retains the endpoint, daemon generation and session ID. A creation command
that returns no usable reply has an unknown outcome; inspect the endpoint
before retrying. `Control.cleanupFailure` exposes rollback failures from
both the core acquisition and the F# layout scope.

The [quickstart source](https://github.com/libtmux/libtmux-dotnet/blob/master/examples/LibTmux.FSharp.Quickstart/Program.fs)
is the published block. From a checkout,
`dotnet run --project examples/LibTmux.FSharp.Quickstart` runs it against the
source. CI instead passes `-p:UsePackageReferences=true`, restores only
`LibTmux.FSharp` from freshly packed artifacts, runs the program against real
tmux on both target frameworks, and compares what it prints with the block
above. The external harness redirects the unchanged program with a socket
name and path, injects body and cleanup failures, checks the remaining
sessions, and waits for its daemon to terminate before removing its root.

## Existing tmux

`options |> Server.connect ct` attaches to an existing server using the same
endpoint precedence. It verifies a running daemon without starting one.
[Socket selection](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#where-a-bare-connect-lands)
explains the options. `Server.createOwned` gives a scope responsibility for
stopping a whole server; give that scope an explicit endpoint you own.

The following function borrows a server and types into its first matching
shell. Call it only on a shell you intend to control:

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
