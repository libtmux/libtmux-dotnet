# Ownership, discovery and example testing

## Automatic examples and test fixtures

Ordinary examples use `LibTmux.Server.Open()` and tmux's normal endpoint. The test harness supplies `LIBTMUX_SOCKET_NAME` or `LIBTMUX_SOCKET_PATH` and `TMUX_TMPDIR` to the example process. The source below is compiled from `examples/LibTmux.FSharp.Quickstart/FindOrCreate.fs`; snippet synchronization checks that this displayed block remains identical.

## Find or create

This program finds or creates the `build` session and its `tests` window. Each callback removes only what its find-or-create call created. Existing resources survive. If several windows have the exact name `tests`, the call raises `TmuxAmbiguousMatchException` rather than choosing one.

<!-- fsharp-snippet: FindOrCreateSession run -->
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

        let! selected =
            server
            |> Server.findOrCreateSession token "build" (Some(NewSessionRequest(Command = "/bin/sh")))

        match selected with
        | FindOrCreate.Existing _ -> printfn "session: existing"
        | FindOrCreate.Created _ -> printfn "session: created"

        do!
            selected
            |> FindOrCreate.withResource token (fun session ->
                task {
                    let! window =
                        session
                        |> Session.findOrCreateWindow token "tests" (Some(NewWindowRequest(Command = "/bin/sh")))

                    do!
                        window
                        |> FindOrCreate.withResource token (fun value ->
                            task {
                                printfn "window: %s" value.Name
                                let! windows = session |> Session.windows |> Query.list token
                                printfn "windows: %d" windows.Count
                            })
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

On a server with no `build` session, it prints:

<!-- fsharp-output: FindOrCreateSession -->
```text
session: created
window: tests
windows: 2
```
<!-- endfsharp-output -->

When `build` already exists, the first line is `session: existing`; the window count reflects that session. `Server.findOrCreate` applies the same created-versus-borrowed rule to the captured daemon endpoint. `Window.findOrCreatePane ct identity request window` uses the pane-local `@libtmux-identity` option, not a pane title or index. Requests are F# options: `None` selects defaults and `Some request` supplies creation settings. A request cannot replace an existing match or override its selection identity.

## Taking ownership of existing objects

`Server.adopt`, `Session.adopt`, `Window.adopt` and `Pane.adopt` explicitly accept destruction of an existing resource. Each returns an owner for `use!` or `Owned.withResource`. The owner captures the daemon generation and object ID, so renaming or moving the object does not redirect cleanup to a new name or index. A replacement daemon is refused.

| Owner | Destruction when disposed |
| --- | --- |
| Server | Stops the accepted daemon and waits for its process to exit |
| Session | Removes that session; windows linked to other sessions survive |
| Window | Removes the window, every session link and all its panes |
| Pane | Removes that pane, including after it moves to another window |

Use adoption only when the caller intends that destruction. Connecting, querying and discovering remain borrowing operations.

This function receives an existing pane selected by the caller, captures its screen, and removes the pane when the capture finishes. It also attempts cleanup if capture fails or is canceled. The test suite executes this exact function against its fixture pane and checks that the neighboring pane survives.

<!-- fsharp-snippet: AdoptPane tested -->
```fsharp
open System.Threading
open LibTmux
open LibTmux.FSharp

let captureThenRemovePaneAsync (token: CancellationToken) (pane: LibTmux.Pane) =
    task {
        let! owner = pane |> Pane.adopt token
        return! owner |> Owned.withResource token (Pane.capture token (CapturePaneRequest()))
    }
```
<!-- endfsharp-snippet -->

## Finding running tmux servers

`Server.discover ct options` delegates to the shared .NET discovery API. `ServerDiscoveryOptions.Roots` adds explicit absolute socket directories; `IncludeConfiguredRoots` also includes the selected endpoint directory and the current user's tmux directory under the captured `TMUX_TMPDIR` (or `/tmp`). Discovery examines immediate children, skips symlinks and sockets belonging to another user, and never starts a daemon.

`MaximumRoots`, `MaximumEntries`, `MaximumProbes`, `Timeout` and `ProbeTimeout` bound the search. Duplicate input roots count toward the root bound. Read `Servers`, `Diagnostics` and `Truncated` together; the result is not a machine-wide inventory. Cancellation raises instead of reporting partial success. A synchronous filesystem call can exceed the deadline on an unresponsive filesystem.

This function prints the result and returns its borrowed handles. Pass `ServerDiscoveryOptions()` for configured roots, or set `Roots` and `IncludeConfiguredRoots = false` to restrict the search. The test suite executes this exact function with its private socket directory and a one-root limit.

<!-- fsharp-snippet: DiscoverServers tested -->
```fsharp
open System.Threading
open LibTmux
open LibTmux.FSharp

let reportServersAsync (token: CancellationToken) (options: ServerDiscoveryOptions) =
    task {
        let! result = options |> Server.discover token

        for found in result.Servers do
            printfn "server: %s" found.SocketPath

        for diagnostic in result.Diagnostics do
            printfn "%s: %s (%s)" diagnostic.Path diagnostic.Message diagnostic.Kind

        printfn "search truncated: %b" result.Truncated
        return result
    }
```
<!-- endfsharp-snippet -->

## Cleanup at scope exit

`use!` awaits an owner's `DisposeAsync`. For work that can fail while cleanup also fails, `Owned.withResource ct work owner` and `FindOrCreate.withResource ct work result` preserve the original work exception, including its cancellation token. `Control.cleanupFailure error` exposes the cleanup exception, as in the complete program above. If only cleanup fails, that exception propagates directly. Failed cleanup can be retried on the retained owner.

The callback token is checked before work starts; commands inside the callback must receive it to observe later cancellation. Cleanup uses an independent five-second deadline. `FindOrCreate.withResource` applies that cleanup only to the `Created` case. The `Existing` case remains alive, including when the work fails or is canceled.

## Environment defaults and overrides

`LibTmux.Server.Open()` captures its endpoint and child environment at construction. Selection order is explicit socket path, explicit socket name, `LIBTMUX_SOCKET_PATH`, `LIBTMUX_SOCKET_NAME`, the inherited `TMUX` context, then tmux's default endpoint. A selected name uses `TMUX_TMPDIR` as its root; an explicit path takes precedence over a name. Empty default variables are ignored. Later host-environment changes do not retarget that handle.

The harness changes only the child process environment. Returning from a run leaves the parent environment unchanged; examples do not contain QA-specific configuration.

## Sandbox and example testing

`eng/docs/run_fsharp_quickstart.py` runs the published programs on fixture-owned endpoints. Its find-or-create scenarios cover a new session and borrowed reuse, externally selected names and paths, body failure, cleanup failure, and paired failure. It inspects the expected remaining sessions and waits for the fixture daemon's process identity to disappear before deleting its temporary root. An unverified cleanup retains the root and reports failure.

The F# lifecycle tests separately exercise all four adoption boundaries, all four find-or-create results, discovery bounds and cancellation forwarding on .NET 8 and .NET 10. Markdown snippet synchronization is an existing integration. Astro/MDX, Sphinx/reST/MyST and Python doctest adapters remain part of the cross-port implementation scope; this F# guide does not claim those integrations have been executed.
