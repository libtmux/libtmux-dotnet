# Run tmux work in a service

This page is for a program that runs for a long time and drives tmux on behalf
of something else: a worker that runs jobs in panes, a web app that starts
sessions, a bot. It has to share one handle, stop cleanly, decide whether a
failed command may run again, see what each command cost, and keep two tasks
from typing into the same pane.

## One handle, bounded

Connect once at startup and share the handle; it is immutable, and every task
may call through it at once ([what calls share](concurrency.md)). Give it a
logger through the options' `Logger`, and bound every command it sends, so a
tmux that stops answering fails a call rather than holding it:

<!-- fsharp-snippet: ServiceHandle -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

// The options name the socket and carry the service's logger, as in
// ServerConnectionOptions(SocketName = "build", Logger = logger).
let connectForServiceAsync (stopping: CancellationToken) (options: ServerConnectionOptions) =
    task {
        let! server = options |> Server.connect stopping

        // Every command through this handle, and the sessions, windows and
        // panes read from it, gives up after five seconds.
        return server |> Server.within (TimeSpan.FromSeconds 5.)
    }
```
<!-- endfsharp-snippet -->

`Server.connect` attaches to a server that is already running and never starts
one. A service that owns its tmux starts it with `Server.createOwned` instead,
and stops it by disposing the scope. In a host built on
`Microsoft.Extensions.DependencyInjection`, the
`LibTmux.Extensions.DependencyInjection` package's `AddLibTmux` registers the
handle as a singleton, reads options bound from configuration, and takes the
logger from the host when the options name none.

## Stopping

Pass the host's stopping token to every call. A wait, a listing or a read that
it cancels raises `OperationCanceledException`, which a stopping service can
ignore. A run is different: once its command reached the pane's shell, the
command keeps running after the call returns and after the process exits, so a
run cancelled then raises a failure that `TmuxFailure.MayHaveRun` matches. Say
so, rather than treating it as an error or as nothing:

<!-- fsharp-snippet: ServiceShutdown -->
```fsharp
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runJobAsync (log: string -> unit) (stopping: CancellationToken) (pane: Pane) (command: string) =
    task {
        try
            let! result = pane |> Pane.run stopping (TimeSpan.FromMinutes 10.) command

            match result with
            | PaneRun.Exited status -> log $"exited {status}"
            | PaneRun.Ended -> log "the shell exited; respawn the pane before the next job"
            | PaneRun.NotStarted -> log "the pane was busy; nothing ran"
            | PaneRun.TimedOut -> log "still running after ten minutes; left running"
        with
        // Stopping is not a failure, but a command already sent keeps
        // running in its pane after this process exits.
        | TmuxFailure.MayHaveRun _ when stopping.IsCancellationRequested ->
            log $"stopped; the command may still be running in {pane.Id}"
        | :? OperationCanceledException when stopping.IsCancellationRequested -> ()
    }
```
<!-- endfsharp-snippet -->

## Running a command again

Only `TmuxFailure.NotSent` means tmux never saw the command, so running it again
repeats nothing; `Retry.ifNotSent` and `Retry.ifNotSentAfter` retry exactly
that case. After `Ran` or `MayHaveRun`, read the state back before deciding,
and never send a mutation, keys or a run again blindly. The library does not
retry on its own. Under `Async.AwaitTask` the failure arrives inside an
`AggregateException`, which the `TmuxFailure` patterns look through.

This program meets each kind on a real server, and retries only the unsent
one:

<!-- fsharp-snippet: FailureKinds -->
```fsharp
open System
open System.Threading
open System.Threading.Tasks
open LibTmux
open LibTmux.FSharp

// A failure says whether tmux saw the command, which decides whether sending
// it again could repeat what it did.
let kind (work: unit -> Task) =
    task {
        try
            do! work ()
            return "succeeded"
        with
        | TmuxFailure.NotSent _ -> return "NotSent"
        | TmuxFailure.Ran _ -> return "Ran"
        | TmuxFailure.MayHaveRun _ -> return "MayHaveRun"
    }

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token
        let socket = "fsharp-failure-kinds-" + Guid.NewGuid().ToString("N")

        let options =
            ServerConnectionOptions(SocketName = socket, ConfigurationFile = "/dev/null")

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value |> Server.newSession token (SessionSpec.running "jobs" "/bin/sh")

        let! pane = session |> Session.activePane token

        let! _ =
            pane |> Pane.sendAndWait token (TimeSpan.FromSeconds 5.) "echo ready" "ready"

        // Typed options read back as the type they were written with.
        do! session.Options |> Options.set token TmuxOptionKey.HistoryLimit 50_000
        let! history = session.Options |> Options.get token TmuxOptionKey.HistoryLimit
        printfn "history-limit: %d" history

        // No server listens on this socket, so no command reached one.
        let missing =
            ServerConnectionOptions(SocketName = socket + "-none", ConfigurationFile = "/dev/null")

        let! notSent = kind (fun () -> missing |> Server.connect token :> Task)
        printfn "A server that is not running: %s" notSent

        // tmux ran the command and refused it.
        let refuse (cancellationToken: CancellationToken) =
            task { do! session.Options |> Options.set cancellationToken TmuxOptionKey.HistoryLimit -1 }

        let! ran = kind (fun () -> refuse token :> Task)
        printfn "A value tmux refuses: %s" ran

        // The command is running when the token is cancelled.
        use stop = CancellationTokenSource.CreateLinkedTokenSource(token)

        let running =
            pane |> Pane.run stop.Token (TimeSpan.FromSeconds 10.) "echo started; sleep 5"

        let! _ = pane |> Pane.waitForText token (TimeSpan.FromSeconds 5.) "started"
        stop.Cancel()
        let! mayHaveRun = kind (fun () -> running :> Task)

        printfn "A run cancelled after it was sent: %s" mayHaveRun

        // Retry sends again only while nothing reached tmux, as while a
        // server is still starting.
        let delays = [ TimeSpan.FromMilliseconds 10.; TimeSpan.FromMilliseconds 20. ]
        let attempts = ref 0

        let count (operation: CancellationToken -> Task<'T>) (cancellationToken: CancellationToken) =
            attempts.Value <- attempts.Value + 1
            operation cancellationToken

        let! unsent =
            kind (fun () -> Retry.ifNotSentAfter token delays (count (fun ct -> missing |> Server.connect ct)) :> Task)

        printfn "Retried while NotSent: %d attempts, then %s" attempts.Value unsent
        attempts.Value <- 0

        let! refused =
            kind (fun () -> Retry.ifNotSentAfter token delays (count refuse) :> Task)

        printfn "Retried after Ran: %d attempt, then %s" attempts.Value refused
    }
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: FailureKinds -->
```text
history-limit: 50000
A server that is not running: NotSent
A value tmux refuses: Ran
A run cancelled after it was sent: MayHaveRun
Retried while NotSent: 3 attempts, then NotSent
Retried after Ran: 1 attempt, then Ran
```
<!-- endfsharp-output -->

## One writer per pane

Keys, a paste and a run share one screen, and the library does not take a pane
for the length of a call: two tasks typing into one pane interleave their
input, and a second run typed before the first ends is read by the same shell.
Give each job a pane of its own, or let one task at a time use a pane:

<!-- fsharp-snippet: ServicePaneGate -->
```fsharp
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open LibTmux

/// Lets one task at a time type into a pane, run in it, or wait on what it typed.
type PaneGate() =
    let gates = ConcurrentDictionary<PaneId, SemaphoreSlim>()

    member _.UseAsync(pane: Pane, cancellationToken: CancellationToken, work: unit -> Task<'T>) =
        task {
            let gate = gates.GetOrAdd(pane.Id, fun _ -> new SemaphoreSlim(1, 1))
            do! gate.WaitAsync(cancellationToken)

            try
                return! work ()
            finally
                gate.Release() |> ignore
        }
```
<!-- endfsharp-snippet -->

The gate covers this process only. Two processes driving one tmux server do not
see each other's gates. It also keeps a semaphore for every pane it has gated,
so a service that creates a pane per job removes the pane's entry when it
kills the pane.

## What each command cost

Every command a handle runs as a tmux process is traced on an activity source
and timed on a meter, both named in `TmuxDiagnostics`. With OpenTelemetry,
subscribe by name: `AddSource(TmuxDiagnostics.ActivitySourceName)` and
`AddMeter(TmuxDiagnostics.MeterName)`; the histogram
`TmuxDiagnostics.CommandDurationInstrumentName` records each command's seconds.
Nothing is recorded while nothing listens. The logger in the connection options
hears each of those commands as it completes, and
`ServerConnectionOptions.Interceptor` sees each tmux process a handle starts,
for a policy of your own such as a rate limit. Commands a control client sends,
including the reads a wait makes through one, pass through none of these.

## Control clients

A wait attaches one control client to its pane's session and shares it with
the other waits on that session; it detaches when the last of them ends. A
service that waits in a loop holds it with `Session.holdWaitClient`, which
saves the attach on each wait. A mirror from `Mirror.start` and a client from
`Control.withSession` attach their own. tmux lists each among the session's
clients, so a session with `destroy-unattached` set is not destroyed while one
is attached.
