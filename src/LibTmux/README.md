# LibTmux

A typed, async-first [tmux](https://github.com/tmux/tmux) client for .NET.
Servers, sessions, windows, panes, clients, options, hooks and buffers, against
every tmux from **3.2a to 3.7c**, on **net8.0** and **net10.0**.

> **Alpha.** The public API is not settled and can change between prereleases
> without notice, so pin an exact version. The behaviour is proven against all
> eight supported tmux versions on every commit.

```console
$ dotnet package add LibTmux --prerelease
```

One dependency: `Microsoft.Extensions.Logging.Abstractions`, which is
interfaces with no implementation attached — a caller who wants no logging pays
nothing for it.

## Start here

```csharp
using LibTmux;

Server server = await Server.ConnectAsync();
Session session = await server.CreateSessionAsync(new NewSessionRequest { Name = "build" });
Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" });
Pane pane = (await window.GetPanesAsync())[0];

await pane.SendTextAsync("dotnet test");
```

To reach one server in particular:

```csharp
Server elsewhere = await Server.ConnectAsync(
    new ServerConnectionOptions { SocketName = "build-box" });
```

### Where a bare connect lands

`ConnectAsync` with no arguments takes the first of these that says anything:

| Source | What it decides |
|---|---|
| `ServerConnectionOptions` | an explicit `socketPath`, `socketName`, or `socketNameFactory` |
| `LIBTMUX_SOCKET_PATH` | the socket, by path |
| `LIBTMUX_SOCKET_NAME` | the socket, by name, under the root below |
| `TMUX_TMPDIR` | the root a name resolves under — `/tmp` when unset |
| — | the socket named `default` |

Options always win. A call that named a socket is never redirected by a
variable, which is what makes the variables safe to export for a whole process
— a test harness, a sandbox, a container — without auditing the call sites in
between. This library's own examples use exactly that: each one exports a
socket name of its own, so the connect above stays one line and still cannot
reach the server you are sitting in.

A pane's own server is a different question, and a different call:
`Server.FromEnvironment()` reads the socket path out of the `TMUX` variable
tmux exports into every pane. `ConnectAsync` never consults it.

Every call that reaches tmux is asynchronous and takes a `CancellationToken`.
There are no synchronous twins to choose between.

## Three ways to reach tmux

Which one a call uses is visible where the call starts, and all three work on
every supported tmux.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| One-shot | `session.CreateWindowAsync(…)` | one command, awaited | another process — **~2.3 ms** |
| Control | `server.EnterControlModeAsync(ct)` | one client, streamed | another round trip — **~0.2 ms** |
| Chained | `server.Chain()…ExecuteAsync(ct)` | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

That is the marginal cost — fifty commands minus one, over forty-nine, as
medians of 100 samples against tmux 3.7b — because it is the part that belongs
to the library rather than to the machine. Absolute timings move by a factor of
five on one host depending on what else it is doing. The recorded runs give the
whole distribution with the tmux, host and date that produced it:
[github.com/libtmux/libtmux-dotnet/tree/master/docs/benchmarks](https://github.com/libtmux/libtmux-dotnet/tree/master/docs/benchmarks).

```csharp run
// One command, a typed object back.
Window built = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
```

```csharp run
// One client, held open, streaming what tmux does on its own.
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> reply = await control.SendAsync(
    TmuxCommand.Create("list-windows"),
    ct);
```

```csharp run
// Many commands, one invocation, one process cost.
await server.Chain()
    .Then("new-window", "-d", "-n", "one")
    .Then("new-window", "-d", "-n", "two")
    .ExecuteAsync(ct);
```

Control mode is an order of magnitude cheaper *per command*; a chain wins *for
a batch* by paying one round trip for the whole sequence.

## Reading what is there

Accessors return `IReadOnlyList<T>` over an explicit read and never shell out
while you enumerate them.

`GetSessionsAsync` and `GetAttachedSessionsAsync` throw `TmuxCommandException`
when tmux rejects the listing. Its `Result` retains the exit code and stderr,
so a missing daemon and a permission error remain distinct from a live server
with no sessions, which returns an empty list.

```csharp run
foreach (Window each in await session.GetWindowsAsync(ct))
{
    foreach (Pane every in await each.GetPanesAsync(ct))
    {
        Console.WriteLine($"{each.Name} {every.Index} {every.Width}x{every.Height}");
    }
}
```

Live listings throw when the read fails, including when the daemon has stopped
or the socket is inaccessible. An empty list means the read succeeded and
matched nothing. Catch the relevant exception when absence is acceptable;
`IsAliveAsync` is the explicit convenience that returns `false` on library
failures. Raw `ExecuteCommandAsync` keeps completed nonzero exit codes in its
`TmuxCommandResult`.

`Server.Open(options).InspectAsync(ct)` reads an endpoint without running
`InitializeAsync` or starting a daemon. It returns null only for verified
daemon absence; permission and transport failures remain errors. The returned
handle exposes the observed `DaemonVersion` separately from the client
executable's `Version`. Its relationships remain uncaptured until an explicit
listing or snapshot. Inspecting a materialized handle rejects a replacement
daemon instead of adopting it.

Set `NewSessionRequest.ExpectedGeneration` to the inspected handle's `Generation`
when creation must use that daemon. Direct creation and `ToCommand()` both refuse
a replacement; direct creation also verifies the generation during readback.
A missing daemon stays stopped, without loading its configuration.
A failure after creation reports that state may already have changed. The
default remains endpoint-scoped creation. `ExpectedGeneration` cannot be combined
with `ReplaceExisting`.

A handle says what it read, and that stays true. Operations that change what an
object is hand back a replacement:

```csharp run
Window renamed = await window.RenameAsync("integration", ct);
```

`Window.MoveAsync`, `LinkAsync` and `UnlinkAsync` verify that the captured
session/index still names the expected window immediately before mutation in
tmux's command queue. Reassigning that index cannot redirect the operation to
another window. Typed move and link commands retain this check in chains and
control mode; extracting raw argv with `ToArguments` does not retain guards.

`Window.MoveAsync` returns the moved placement, including detached moves and
repeated links to the same window. A renumber request returns the original
placement at its new index while preserving link order. Renumbering another
session leaves the source placement unchanged. Move readback checks the
destination's index and window-ID map. If topology changes prevent a consistent
result after the mutation, the operation throws with unknown dispatch state; do
not retry it. These observations are not an atomic snapshot.

Asking tmux again is `RefreshAsync`. A whole hierarchy in one acquisition is
`CaptureSnapshotAsync`:

```csharp run
Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
SnapshotMetadata acquired = snapshot.SnapshotMetadata!;
Console.WriteLine($"{acquired.Depth}: {acquired.Elapsed} on {acquired.Generation}");
foreach (Pane member in snapshot.Panes)
{
    Console.WriteLine($"{member.Session.Name}/{member.Window.Index}: {member.CurrentCommand}");
}
```

Every depth performs a guarded read, including `SnapshotDepth.Server` on an
already connected handle. `SnapshotMetadata` records depth, daemon generation,
UTC start/end readings and monotonic elapsed time. A clock adjustment can move
the UTC end before the start; use `Elapsed` for duration. Ordinary connected
handles have no snapshot metadata.

Capture reads the hierarchy over an interval. Contradictory parent placements or
child counts throw `InconsistentSnapshotException` without returning a partial
graph or retrying. Equal-count changes can escape detection; this is not an
atomic snapshot. Unacquired relations remain unavailable, while captured
relations and metadata can be read without tmux I/O.

Captured children retain the same root through `Server`. Parent and active-child
properties return the captured instances when their depth was acquired. A pane
reached through a repeated window link returns that exact window placement;
filtering panes does not prune its parent's siblings or linked sessions.
Active children follow the IDs recorded in the corresponding parent row, so
selection changes during acquisition need not agree across separate reads.
An active ID missing from the acquired placement fails the capture explicitly.

Recapturing creates a separate graph. `RefreshAsync` replaces just one entity's
fields; its child relations and materialized parents are not attached to an
older graph, even when its `Server` is an earlier captured root.

### Lookups and captured relations

`GetSessionAsync`, `GetWindowAsync`, and `GetPaneAsync` return materialized
objects. Their names, dimensions, indexes and other scalar properties are local
reads. `Get…Async` throws `TmuxObjectNotFoundException` for an absent entity;
`Find…Async` returns `null` only after a successful lookup finds no match.
Both preserve command, transport, cancellation and stale-generation failures.
Session and window lookups stay within their owner.

<!-- snippet: ReadCapturedState -->
```csharp
Window created = await session.CreateWindowAsync(new NewWindowRequest { Name = "lookup" }, ct);
Server connected = await server.ConnectAsync(ct);
Window read = await connected.GetWindowAsync(created.Id, ct);
Console.WriteLine($"{read.Name} {read.Width}x{read.Height}");

Window? missing = await session.FindWindowAsync("not-created", ct);
Console.WriteLine($"missing {missing is null}");

Session current = await session.RefreshAsync(ct);
if (current.ActiveWindow.IsCaptured)
{
    Window active = current.ActiveWindow.Value;
    Console.WriteLine($"active {active.Name}");
}
```
<!-- endsnippet -->

`Session.ActiveWindow`, `Session.ActivePane` and `Window.ActivePane` expose
`CapturedValue<T>`, which holds at most one child. Read `Value`, or
`TryGetValue` when absence is expected; `OrNull` answers null instead of
throwing. A session reached through an inactive window has that window's row,
so its own active window may be uncaptured, and reading `Value` then throws
`IncompleteSnapshotException` rather than reporting that there is none.
`RefreshAsync` captures the entity's current active child.
`CaptureSnapshotAsync(SnapshotDepth.Panes)` also preserves the captured
parent and child graph. Reading any of these properties performs no I/O.

### Migrating from identity-only lookups

- Remove a `RefreshAsync` used only to make a lookup's scalar properties
  readable. Keep it when current state is needed later.
- Replace a nullable `Session.GetWindowAsync` or `Window.GetPaneAsync` call
  with `FindWindowAsync` or `FindPaneAsync`. Required `Get…Async` calls throw
  on absence.
- Replace `session.ActiveWindow.Name` with
  `session.ActiveWindow.Value.Name` when the capture is known. Use
  `IsCaptured` when walking a partial hierarchy.
- Replace `RaiseIfDeadAsync` with `ThrowIfDeadAsync`. The failure behavior is
  unchanged; the old name is gone rather than deprecated, because alpha
  releases carry no deprecation period.
- Catch listing failures where earlier releases returned an empty inventory.
  A stopped daemon is an error; an empty successful read remains an empty list.

## Running something, and reading it back

`SendTextAsync` types leading dashes, semicolons and newlines as input. NUL
is rejected before dispatch. Setting `enter: false` omits the extra Enter
key; it does not remove newlines already present in the text.

```csharp run
await pane.SendTextAsync("echo hello-from-libtmux", cancellationToken: ct);
await pane.EnterAsync(ct);

// tmux accepts a command before the shell has finished it, so the result is
// waited for rather than assumed.
string output = await TmuxWait.UntilAsync(
    async token => string.Join('\n', await pane.CaptureAsync(cancellationToken: token)),
    text => text.Contains("hello-from-libtmux", StringComparison.Ordinal),
    TimeSpan.FromSeconds(10),
    TimeSpan.FromMilliseconds(20));
```

## Splitting and resizing

```csharp run
Pane split = await pane.SplitAsync(new SplitPaneRequest { Direction = PaneDirection.Below }, ct);
await split.SetHeightAsync(10, ct);
```

## Options and hooks

tmux has no types, so a value carries the text it reported alongside the
readings that text supports:

```csharp run
await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"), ct);
TmuxOption option = (await window.Options.GetAsync(
    new GetOptionRequest("automatic-rename"), ct))[0];

Console.WriteLine($"{option.Value.Raw} flag={option.Value.Boolean}");
```

An option the window does not hold is inherited rather than missing. Hooks are
arrays even with one entry:

```csharp run
TmuxHook hook = await server.Hooks.SetAsync(
    new SetHookRequest("alert-bell", "set-option -g @rang yes"), ct);
```

## Filtering

Ordinary filtering is LINQ over what you read:

```csharp run
IReadOnlyList<Window> windows = await session.GetWindowsAsync(ct);
IEnumerable<Window> building = windows.Where(
    each => each.Name.StartsWith("build", StringComparison.Ordinal));
```

Declarative filtering translates an expression into a portable document, or
throws — it never quietly falls back to filtering in memory. Write it over the
objects you already hold:

```csharp run
IReadOnlyList<Session> sessions = await server.GetSessionsAsync(ct);
IReadOnlyList<Session> building = sessions.Matching<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);
```

Relations quantify, and the element type carries its own fields:

```csharp run
Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Windows, ct);
IReadOnlyList<Session> withBuild = captured.Sessions.Matching<Session>(
    session => session.Windows.Any(
        each => each.Name.StartsWith("build", StringComparison.Ordinal)));
```

The same expression is also a document, which can be written here and answered
somewhere else:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);
```

The document carries stable wire names: `Session.Name` is `session_name` and
`Client.IsControlClient` is `client_control_mode`. The catalog is closed over
twelve queryable fields:

| Session | Window | Pane | Client |
|---|---|---|---|
| `Name`, `Id`, `Attached`, `Windows` | `Name`, `Id`, `Panes` | `Id`, `pane_command` | `Name`, `IsControlClient`, `client_id` |

Two fields have no property on their entity, and are reached by declaring a row
whose property names are the wire names — which is also how you query a
projection rather than an entity:

```csharp
internal sealed record PaneRow(string PaneId, string PaneCommand);
```

A field outside the catalog throws `UnsupportedQueryExpressionException` rather
than falling back. Typed queries evaluate locally over captured objects and are
never assembled into tmux's executable format language. `UnsafeTmuxFilter` is
the separate opt-in for native tmux `-f` behavior.

Put it on the wire with
[LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json).

## Versions

Where a flag is missing on the running tmux, the request goes out without it
and a warning says what was left off. Where a whole command is missing, nothing
is sent and `TmuxVersionTooLowException` says which version would be needed.

```csharp run
// A handle says what it read: the version is what tmux reported when this
// server was reached, and null when it reported something unparsable.
TmuxVersion? version = server.Version;
Console.WriteLine($"tmux {version?.Raw} 3.4-or-newer={version?.IsAtLeast(TmuxVersion.Parse("3.4"))}");
```

## Testing your own code

[`LibTmux.Testing`](../LibTmux.Testing/README.md) is a separate package, so
test scaffolding stays out of an application's output. It gives a test a tmux
server of its own, on its own socket, killed deterministically:

```console
$ dotnet package add LibTmux.Testing --prerelease
```


```csharp
using LibTmux.Testing;

TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
```

Disposing kills the server, so a test that fails part way through leaves
nothing behind.

## Logging

Pass an `ILogger` when connecting and every tmux command is recorded once, at
the single point they all pass through:

```csharp
Server logged = await Server.ConnectAsync(new ServerConnectionOptions { Logger = logger });
```

Commands are recorded at `Debug` and failures at `Error`, with stable scalar
fields (`TmuxSubcommand`, `TmuxSocket`, `TmuxExitCode`) to filter on. Anything
that can carry a payload is truncated, the command line included.

## Tracing and metrics

Every command is also a span and a measurement. `TmuxDiagnostics` names the
sources, so a telemetry pipeline subscribes by name and this library keeps its
single dependency: pass `TmuxDiagnostics.ActivitySourceName` to OpenTelemetry's
`AddSource`, and `TmuxDiagnostics.MeterName` to its `AddMeter`.

The span is named for the subcommand and tagged `tmux.subcommand`,
`tmux.socket` and `tmux.exit_code`; a failure carries `error.type` and an error
status. `TmuxDiagnostics.CommandDurationInstrumentName` records elapsed seconds
under the same tags. Both cost nothing when nothing is listening.

## Wrapping every command

`Interceptor` sits between the library and the tmux it starts, so a policy that
belongs to your application — an audit trail, a retry, a refusal, a stand-in
answer in a test — lives outside the library rather than in a fork of it:

```csharp
Server audited = await Server.ConnectAsync(
    new ServerConnectionOptions
    {
        Interceptor = async (invocation, next, token) =>
        {
            TmuxCommandResult result = await next(token);
            Console.WriteLine($"{string.Join(' ', invocation.Arguments)} → {result.ExitCode}");
            return result;
        },
    },
    ct);
```

Call `next` once to pass through, again to retry, or not at all to answer in
tmux's place. It sees every client the connection starts for a command,
including the version probe, but not a control-mode client, which is one
long-lived process. A command against a pane or window arrives inside its
generation guard, so a stand-in has to answer that check too. Unset, nothing
wraps anything and dispatch is what it was; set, it is one delegate call around
each invocation.

## Sharing handles across threads

`Server`, `Window`, `Pane`, `Client`, `TmuxOptions`, `TmuxHooks`,
`TmuxEnvironment`, `TmuxChain` and `CapturedRelation<T>` are immutable once
constructed and safe to share freely, including as a DI singleton. No public
method mutates the handle it was called on: `RefreshAsync`, `RenameAsync`,
`ConnectAsync` and `CaptureSnapshotAsync` each answer a new handle, and a stale
handle stays a correct record of what was read.

`Session` is safe to share on the same terms. `IControlModeSession.SendAsync`
is safe to call concurrently — tmux answers in the order it received, and each
caller gets its own reply — while `Events` is a single-consumer stream and
`DisposeAsync` is idempotent from any thread.

Two limits are worth knowing before registering a singleton. A handle from
`ConnectAsync` pins the server generation it discovered, so after tmux restarts
its derived entities throw `StaleServerGenerationException` rather than
silently addressing the new server; `Server.Open` defers discovery to each
call instead. And nothing serializes tmux itself: concurrent callers reach one
tmux server, which applies commands in the order it receives them.

## Knowing when a retry is safe

Retrying a failed command is the obvious recovery and it is only sound when the
command never reached tmux. Every failure says which it was, so the decision is
an exception filter rather than a guess:

```csharp run
try
{
    await server.CreateSessionAsync(new NewSessionRequest { Name = "build" }, ct);
}
catch (LibTmuxException error) when (error.Dispatch == TmuxDispatchState.NotDispatched)
{
    // tmux was never started, so nothing happened and this can be sent again.
    Console.WriteLine($"safe to retry: {error.Dispatch}");
}
```

`NotDispatched` is claimed only where the library can see that no tmux process
ran — a missing binary, or a command rejected before launch. A client that
started and then died is `Unknown`, because tmux may have acted before the pipe
broke, and `Unknown` is the default for exactly that reason. A
`TmuxCommandException` is always `Dispatched`: it exists because tmux answered.

## Compatibility

| | |
|---|---|
| tmux | 3.2a to 3.7c |
| .NET | net8.0, net10.0 |
| OS | Linux and macOS. `Server`, `Session`, `Window` and `Pane` are annotated unsupported on Windows, because their lifecycle, mutation and control-mode contracts need a real tmux |
| Trimming / NativeAOT | Core APIs are analyzer-gated. Query `Compile` and `Matching` resolve properties by name, so they warn trimmed callers to preserve the filtered types' public properties |
| Windows preview | `PsmuxServer`, `PsmuxSession`, `PsmuxWindow` and `PsmuxPane` read one [psmux](https://github.com/psmux/psmux) session — its windows, its panes, and pane text — natively or across WSL. They cannot express lifecycle, mutation, chaining, control mode, or raw commands, so a caller gets a compile error where a suppression would have given a silent gap. [The preview contract](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/psmux.md) names the build it accepts and how to provision it |

## Related packages

| Package | Adds |
|---|---|
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

What changed between versions: [CHANGELOG](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
