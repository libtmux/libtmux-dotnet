# Control mode: what tmux says unasked

A control session keeps one tmux client running for as long as you hold it.
That is what makes tmux willing to report things nobody asked for: panes
producing output, windows appearing, sessions changing.

<!-- snippet: WatchForWindowAdd -->
```csharp
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);

await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);

await foreach (TmuxEvent observed in control.Events.WithCancellation(ct))
{
    if (observed is TmuxNotificationEvent { Name: "window-add" } added)
    {
        Console.WriteLine($"window-add {added.Arguments[0]}");
        break;
    }
}
```
<!-- endsnippet -->

Example output:

```
window-add @1
```

Pane output arrives as `TmuxOutputEvent`, already decoded — tmux escapes the
payload the same way it escapes an option value, and this undoes that:

```
%output %1 \033[1m\033[7m%\033[27m ...
```

becomes a `TmuxOutputEvent` whose `Data` holds the real control bytes.

## Two things worth knowing

Entering control mode **attaches**. A control client that never attaches is
told about the hierarchy but not about pane output, so `%output` never arrives
and the stream looks mysteriously quiet.

The stream ends with `TmuxExitEvent` and then completes, so an `await foreach`
is released rather than hanging when the server goes away.

Notifications use a bounded, non-blocking buffer so a slow observer cannot
stall command replies or the control reader. `ControlModeEventBufferCapacity`
defaults to 512 events; `ControlModeEventBufferMaxBytes` defaults to 4 MiB of
decoded UTF-8 payload. Set either property on `ServerConnectionOptions` before
opening the control client. Payload counts output text, notification names and
arguments, and exit reasons. It is not a managed-heap measurement; the event
count and the separate protocol line limit bound object overhead.

The buffer discards oldest events until both ceilings hold. It rejects an
individually oversized event; an oversized final exit reason is omitted while
the terminal exit notification is retained. Each discard produces a
`TmuxEventsDroppedEvent`. The marker precedes the next delivered event, or
arrives alone when no event fits. It does not identify the panes or stream
positions lost. `Count` is the loss since the previous marker and
`TotalDropped` is the lifetime total.

Treat the marker as cache invalidation: re-read state derived from
notifications. `control.WatchAsync(pane)` forwards loss and rechecks whether
the pane still exists. Stopping that iterator leaves the borrowed control
client open. Command replies travel through a separate queue and are never
dropped by the event buffer.

<!-- snippet: NoticeDroppedEvents -->
```csharp
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);

await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);

await foreach (TmuxEvent observed in control.Events.WithCancellation(ct))
{
    if (observed is TmuxEventsDroppedEvent dropped)
    {
        // Anything cached from this stream is now a guess, so the
        // marker is a signal to re-read rather than to log.
        Console.WriteLine($"missed {dropped.Count}, {dropped.TotalDropped} in total");
        continue;
    }

    if (observed is TmuxNotificationEvent { Name: "window-add" })
    {
        break;
    }
}
```
<!-- endsnippet -->

`SendAsync` is safe to call concurrently: tmux answers in the order it was
asked, and each caller gets its own answer.

Outstanding calls are bounded. If the session has reached its pending limit,
`SendAsync` throws `InvalidOperationException` before dispatching another
command. Cancellation stops that caller's wait, not the command; the session
discards that answer until tmux finishes the command, preserving later replies.

## Sending text and Enter

The typed control overload keeps a requested Enter as a separate command:

```csharp
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> replies = await new SendKeysRequest { Text = "make", Literal = true }
    .ExecuteAsync(pane, control, ct);
```

The result concatenates reply lines in command order; it contains no separate
result envelope for each command. Other control callers can send between the
text and Enter requests. If text succeeds but Enter fails or its wait is
cancelled, `LibTmuxException.Dispatch` is `Unknown`: do not retry the whole
request. Enter can execute input in the pane's application.

## When this is not the right mode

For a single command it is more machinery than the job needs — use
[one-shot](one-shot.md). For many commands with nothing to observe, use
[chaining](chaining.md).

What each mode costs, measured for one command and for fifty, is in
[choosing a mode](matrix.md).
