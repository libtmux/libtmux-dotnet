# One-shot: the default

A one-shot call starts a tmux client, runs one command, waits for it, and lets
the client exit. It is what every typed method on `Server`, `Session`,
`Window`, and `Pane` does unless you asked for something else.

<!-- snippet: CreateWindow -->
```csharp
Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "build"), ct);
Console.WriteLine($"{window.Id} {window.Index}:{window.Name}");
```
<!-- endsnippet -->

Example output:

```
@1 1:build
```

## When this is the right mode

Almost always. One command, one materialized object, and the object is a
reading rather than a live view: it keeps saying what tmux reported when it was
made. Refresh is explicit, so nothing changes under you mid-function.

## When it is not

Starting a process costs more than running a command does. For a handful of
commands that is invisible; for fifty in a row it dominates, and
[chaining](chaining.md) pays that cost once instead of fifty times.

It also only ever sees what it asked for. To notice a window appearing, or read
what a program writes into a pane, you need a client that stays —
[control mode](control-mode.md).

## Cancellation is the deadline

No call carries a deadline of its own. A `CancellationToken` is what bounds
one, and a caller that passes none waits as long as tmux takes — which is
forever against a socket that accepts a connection and never answers:

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Server server = await Server.ConnectAsync(cancellationToken: deadline.Token);
```

Cancelling after the client started kills and reaps it, and the failure says
so: `TmuxOperationCanceledException` carries the client's process id and
reports that the command may already have run. What it cannot reap is a
process the client left behind — a pane's program outlives the client that
spawned it, by design.

The transport this uses, and the two shapes it beat, are recorded in
[ADR 0001](../decisions/0001-transport-framing-bakeoff.md).

What each mode costs, measured for one command and for fifty, is in
[choosing a mode](matrix.md).
