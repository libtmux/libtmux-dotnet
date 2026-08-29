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
stall command replies or the control reader. If the buffer fills, the oldest
events are discarded and a `TmuxEventsDroppedEvent` appears immediately before
the next retained event. `Count` is the loss since the previous marker and
`TotalDropped` is the lifetime total. Treat the marker as cache invalidation:
re-read any state that depends on notifications. Command replies travel through
a separate queue and are not dropped by this buffer.

The marker arrives in sequence, where the discarded events would have been:

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

## When this is not the right mode

For a single command it is more machinery than the job needs — use
[one-shot](one-shot.md). For many commands with nothing to observe, use
[chaining](chaining.md).

What each mode costs, measured for one command and for fifty, is in
[choosing a mode](matrix.md).
