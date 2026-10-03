# Streams and cleanup

Captured snapshots are replayable local observations. Control-mode events are
live, ordered, destructive observations. They are not a replayable `seq`, and
a control client has one event stream with one consumer: a second reader
started while the first is reading raises `InvalidOperationException`.

## Choose a wait or a stream

To wait for one thing a pane prints, use `Pane.sendAndWait`,
`Pane.waitForText`, `Pane.waitUntil` or `Pane.run`; each returns a single
result, and [which wait](getting-started.md#which-wait) compares them. The waits share one control client
per session while any wait on it runs, and `Pane.run` learns its exit status
from a private `wait-for` channel. Read a stream when the caller reacts to
events as they arrive.

## Streams are cold

`Control.events` and `Control.watchPane` return an `IAsyncEnumerable`.
Nothing is read until a consumer enumerates it, and the consumer supplies the
cancellation token. `Control.foldWhile` and `Control.iter` consume a stream,
await one callback at a time, dispose only their enumerator, and leave the
client open. Any `IAsyncEnumerable` library composes the same streams;
`TaskSeq<'T>` in FSharp.Control.TaskSeq is the same type.

`Control.watchPane` narrows a client's stream to one pane's output, and ends
with `TmuxPaneGoneEvent` once the pane is confirmed gone:

<!-- fsharp-snippet: WatchPaneOutput run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let readPaneUntilAsync
    (cancellationToken: CancellationToken)
    (marker: string)
    (pane: Pane)
    (session: IControlModeSession)
    =
    session
    |> Control.watchPane pane
    |> Control.foldWhile
        cancellationToken
        (fun output event ->
            task {
                match event with
                | :? TmuxOutputEvent as printed ->
                    let output = output + printed.Data

                    if output.Contains(marker, StringComparison.Ordinal) then
                        return StreamStep.Stop output
                    else
                        return StreamStep.Continue output
                | :? TmuxPaneGoneEvent
                | :? TmuxExitEvent -> return StreamStep.Stop output
                | _ -> return StreamStep.Continue output
            })
        ""
```
<!-- endfsharp-snippet -->

A client has one event stream, so watching two panes with `Control.watchPane`
takes two clients. `Control.watchPanes` follows several through one: each
output event names its pane, each pane's end arrives as `TmuxPaneGoneEvent`
after the output buffered before it went, and the stream ends once every pane
is gone. This complete program follows two panes, then their ends:

<!-- fsharp-snippet: WatchPanes run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-watch-panes-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value.CreateSessionAsync(NewSessionRequest(Name = "work", Command = "exec sleep 60"), token)

        // The client buffers everything from the moment it attaches, so the
        // panes it should see can start afterwards.
        use! client = session |> Control.enterSession token
        let! first = session |> Session.panes |> Query.list token

        let start command =
            first[0] |> Pane.split token (SplitPaneRequest(Command = command))

        let! build = start "printf 'build: ok\\n'; exec sleep 60"
        let! test = start "printf 'test: ok\\n'; exec sleep 60"

        // One client, both panes: each output event names its pane.
        let! printed =
            client
            |> Control.watchPanes [ build; test ]
            |> Control.foldWhile
                token
                (fun (printed: Map<string, string>) event ->
                    task {
                        match event with
                        | :? TmuxOutputEvent as output ->
                            let pane = output.PaneId.ToString()
                            let sofar = printed |> Map.tryFind pane |> Option.defaultValue ""
                            let printed = printed |> Map.add pane (sofar + output.Data)

                            if printed.Count = 2 && printed |> Map.forall (fun _ text -> text.Contains '\n') then
                                return StreamStep.Stop printed
                            else
                                return StreamStep.Continue printed
                        | _ -> return StreamStep.Continue printed
                    })
                Map.empty

        // Each pane's end arrives as TmuxPaneGoneEvent, and the stream ends
        // once both are gone.
        do! build.KillAsync(cancellationToken = token)
        do! test.KillAsync(cancellationToken = token)

        let! ended =
            client
            |> Control.watchPanes [ build; test ]
            |> Control.foldWhile
                token
                (fun ended event ->
                    task {
                        match event with
                        | :? TmuxPaneGoneEvent as gone -> return StreamStep.Continue(ended @ [ gone.PaneId ])
                        | _ -> return StreamStep.Continue ended
                    })
                []

        for pane in [ build; test ] do
            printfn "%s" (printed[pane.Id.ToString()].Trim())

        printfn "ended in order given: %b" (ended = [ build.Id; test.Id ])
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: WatchPanes -->
```text
build: ok
test: ok
ended in order given: true
```
<!-- endfsharp-output -->

The watch reads the client's only stream, so it consumes and drops events
for other panes. Attach the client with `Control.enterSession` to the session
that holds the pane.

tmux discards output it has not yet sent once a pane's program exits, so the
last lines of a program that exits at once may never arrive on any control
client. Read final output with `Pane.run`, or capture a pane kept with
`remain-on-exit`.

## Follow live state

`Mirror.start` keeps a current copy of a server's sessions, windows, panes and
clients. Each change tmux announces starts a fresh capture, and a capture that
finds nothing different publishes nothing, so `Mirror.waitUntil` and
`Mirror.views` see each distinct state once. Activity times, cursor positions
and history sizes change with every keystroke and do not count. tmux does not announce a pane's
running command or working directory, nor a layout change in a session the
mirror is not attached to; `Mirror.startRefreshing` also captures whenever the
mirror has been quiet for an interval:

<!-- fsharp-snippet: LiveState run -->
```fsharp run
open System
open System.Threading
open LibTmux
open LibTmux.FSharp

let runAsync () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
        let token = deadline.Token

        let options =
            ServerConnectionOptions(
                SocketName = "fsharp-live-" + Guid.NewGuid().ToString("N"),
                ConfigurationFile = "/dev/null"
            )

        use! owned = options |> Server.createOwned token

        let! session =
            owned.Value.CreateSessionAsync(
                NewSessionRequest(Name = "work", WindowName = "shell", Command = "/bin/sh"),
                token
            )

        // Captures again on each change tmux announces, and every 200 ms for
        // changes it does not, such as the command a pane runs.
        use! mirror =
            session |> Mirror.startRefreshing token (TimeSpan.FromMilliseconds 200.)

        let! _ =
            session.CreateWindowAsync(NewWindowRequest(Name = "logs", Command = "/bin/sh"), token)

        let! withLogs =
            mirror
            |> Mirror.waitUntil token (TimeSpan.FromSeconds 5.) (fun view ->
                view.Server.Windows |> Seq.exists (fun window -> window.Name = "logs"))

        let! panes = session |> Session.panes |> Query.list token

        do! panes[0] |> Pane.sendLine token "exec sleep 30"

        // tryWaitUntil answers None when no view matched in time.
        let! sleeping =
            mirror
            |> Mirror.tryWaitUntil token (TimeSpan.FromSeconds 5.) (fun view ->
                view.Server.Panes |> Seq.exists (fun pane -> pane.CurrentCommand = "sleep"))

        printfn "windows: %s" (String.Join(", ", [ for window in withLogs.Server.Windows -> window.Name ]))

        match sleeping with
        | Some sleeping ->
            printfn
                "sleeping panes: %d"
                (sleeping.Server.Panes
                 |> Seq.filter (fun pane -> pane.CurrentCommand = "sleep")
                 |> Seq.length)

            printfn "newer view: %b" (sleeping.Epoch > withLogs.Epoch)
        | None -> printfn "no pane ran sleep within five seconds"
    }

runAsync().GetAwaiter().GetResult()
```
<!-- endfsharp-snippet -->

It prints:

<!-- fsharp-output: LiveState -->
```text
windows: shell, logs
sleeping panes: 1
newer view: true
```
<!-- endfsharp-output -->

The mirror's control client receives notifications only, never pane output,
and does not change window sizes. If its client ends, the mirror attaches
again through the anchor session; once that session is gone, the mirror ends
and `Mirror.views` raises `TmuxObjectNotFoundException`.

## Ownership

Use `Control.withSession` to own a client for one task, or pass a client from
`Control.enter` or `Control.enterSession` to `Control.useSession`. The
[control-mode example](modes.md#control-mode) shows both lifetimes.

## Loss, ends and failures

A client buffers 512 events by default
(`ServerConnectionOptions.ControlModeEventBufferCapacity`). When a reader falls
behind, the buffer discards the oldest output of the pane with the most output
waiting, so a flooding pane loses its own output rather than a quieter pane's,
and notifications about sessions, windows and layout survive it.
`TmuxEventsDroppedEvent` reports the loss; its `OnlyOutput` is true when no
notification was discarded, so state derived from notifications is still
exact. The client then pauses the flooding pane in tmux until the reader
catches up: `TmuxPanePausedEvent` and `TmuxPaneContinuedEvent` bracket output
the pane printed that the reader never receives, and a client nobody reads
keeps the pane paused. The pause is the client's own `refresh-client -A`,
sent when its buffer fills; it does not set tmux's age-based `pause-after`,
so a reader that keeps up never sees a pause. Capture the pane to read its
screen after a gap. `TmuxExitEvent` precedes normal stream completion. A stream fault arrives after buffered events. Cancellation stops
waiting and disposes the reader; it does not undo a command tmux already
received.

When a callback and the enumerator's cleanup both fail, the callback's
exception propagates unchanged and `Control.cleanupFailure` returns the
cleanup's. The callback's exception keeps its type, so a handler that matches
`:? TmuxPaneException` still catches it; an `AggregateException` of both would
not be caught there.
