# One-shot: the default

A one-shot call sends one command to tmux, waits for its answer, and returns a
typed object. It is what every typed method on `Server`, `Session`, `Window`,
and `Pane` does unless you asked for something else.

<!-- snippet: CreateWindow -->
```csharp
Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
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

Each call is still a round trip, and a few commands cost a process on top (see
below). For fifty commands that must run back to back, [chaining](chaining.md)
sends them in one round trip.

It also only ever sees what it asked for. To notice a window appearing, or read
what a program writes into a pane, you need a client that stays —
[control mode](control-mode.md).

## How a call reaches tmux

The library keeps one control client per server, shared by every `Server`
handle on the same socket, and sends one-shot commands over it. The client
starts with the first command that can use it, only against a server that has a
session, and attaches with the `ignore-size` and `no-output` client flags, so
it changes no window's size and receives no pane output. It is not the client
`EnterControlModeAsync` returns, reports nothing to you, and ends with its
session or server; the next command starts another.

These commands run on a process of their own, as before: `wait-for`,
`run-shell`, `if-shell` without `-F`, `new-session`, `start-server`,
`attach-session`, `kill-server`, `kill-session`, `kill-window`, `kill-pane`,
`unlink-window`, `move-window`, `join-pane`, `move-pane`, `save-buffer`, `load-buffer`,
`source-file`, the `choose-*` commands, and the commands that act on a client.
They either wait for another client, end the client's session, read or write a
file for the caller, or open interface on a client.

What follows from it:

- **Listings leave the client out.** `GetClientsAsync`, `GetAttachedSessionsAsync`,
  `Session.Attached`, snapshots, and queries over `session_attached` do not
  count it. A `list-clients` you run yourself lists it, and a format of your own
  that reads `#{session_attached}`, `#{client_name}` or `#{session_clients}`
  counts it. `display-message -p` with a `client_*` format and no `-c` reads
  it as the current client.
- **Commands without a target resolve against its session.** A command with no
  target session, window or pane resolves against the client's session instead
  of the server's most recently used one. Name the target.
- **A client command with no client named does not act on it.**
  `detach-client`, `switch-client`, `lock-client`, `suspend-client`,
  `refresh-client`, `display-panes`, `display-popup`, `display-menu`,
  `command-prompt`, `confirm-before`, `display-message` without `-p`,
  `send-keys -K` and `set-buffer -w` act on the most recently active client
  that is not this one, and fail with tmux's `no current client` when there is
  none, except that `display-message` and `send-keys -K` then do nothing and
  `set-buffer` only sets the buffer. `detach-client -a` and `-s` detach the
  other clients one by one and leave this one attached.
- **The `client-attached` hook fires once per attach**, not once per command:
  at the first command, and again each time the client has to start over.
- **Its socket connection is long-lived.** Changing the socket's permissions or
  replacing the executable after it attached does not affect commands it
  carries.
- **A wrapper around `TmuxBinaryPath` sees the client's launch**
  (`-C attach-session`) and the commands that stay on processes, not the rest.
- **When the client ends with a command in flight**, the command runs again on
  a process only if it never reached tmux or only reads (`list-*`, `show-*`,
  `capture-pane -p`, `display-message -p`, `has-session`). Any other command
  fails with `TmuxDispatchState.Unknown`; look before repeating it.
- **Replies are bounded like a process's.** A reply may be as large as
  `ServerConnectionOptions.MaxCapturedBytesPerStream`, the same bound a process
  has on each of its streams.

## Ownership and replacement snapshots

An owned scope cleans up the resource it created. A listed handle has no
cleanup responsibility. Mutations return a fresh snapshot; keep the result
when later calls need the changed state. Give an owned server a socket of its
own: on the default socket, a server already running is refused rather than
owned, since disposing the scope would stop it.

```csharp
using LibTmux;

await using OwnedServerScope ownedServer = await Server.CreateOwnedAsync(
    new ServerConnectionOptions { SocketName = "work" });
await using OwnedSessionScope ownedSession =
    await ownedServer.Value.CreateOwnedSessionAsync(new NewSessionRequest { Name = "work" });
Session original = ownedSession.Value;
Session renamed = await original.RenameAsync("review");
Console.WriteLine($"{original.Name} -> {renamed.Name}");
```

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
