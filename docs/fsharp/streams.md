# Streams and cleanup

Captured snapshots are replayable local observations. Control-mode events are
live, ordered, destructive observations. They are not a replayable `seq` and
they have one consumer.

Use `Control.iterEvents` or `Control.foldEventsWhile` for a borrowed control
client. Each helper awaits handlers in order, disposes only its enumerator,
and leaves the client open. Use `Control.withSession` to own a client for one
task, or pass a client from `Control.enter` to `Control.useSession`. The
[control-mode example](modes.md#control-mode) shows both lifetimes.

`TmuxEventsDroppedEvent` reports a bounded-buffer overflow; capture again
before deriving state from later events. `TmuxExitEvent` precedes normal stream
completion. A stream fault arrives after buffered events. Cancellation stops
waiting and disposes the reader; it does not undo a command tmux already
received.
