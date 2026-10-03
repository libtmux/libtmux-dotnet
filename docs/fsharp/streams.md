# Streams and cleanup

Captured snapshots are replayable local observations. Control-mode events are
live, ordered, destructive observations. They are not a replayable `seq`, and
a control client has one event stream with one consumer.

## Choose a wait or a stream

To wait for one thing a pane prints, use `Pane.waitForText`, `Pane.waitUntil`
or `Pane.run`; each returns a single result. The waits share one control client
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

The watch reads the client's only stream, so it consumes and drops events
for other panes. Attach the client with `Control.enterSession` to the session
that holds the pane.

tmux discards output it has not yet sent once a pane's program exits, so the
last lines of a program that exits at once may never arrive on any control
client. Read final output with `Pane.run`, or capture a pane kept with
`remain-on-exit`.

## Ownership

Use `Control.withSession` to own a client for one task, or pass a client from
`Control.enter` or `Control.enterSession` to `Control.useSession`. The
[control-mode example](modes.md#control-mode) shows both lifetimes.

## Loss, ends and failures

`TmuxEventsDroppedEvent` reports a bounded-buffer overflow; capture again
before deriving state from later events. `TmuxExitEvent` precedes normal stream
completion. A stream fault arrives after buffered events. Cancellation stops
waiting and disposes the reader; it does not undo a command tmux already
received.

When a callback and the enumerator's cleanup both fail, the callback's
exception propagates unchanged and `Control.cleanupFailure` returns the
cleanup's.
