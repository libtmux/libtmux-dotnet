<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux

A typed, async-first [tmux](https://github.com/tmux/tmux) client for .NET. [Servers][api-server], [sessions][api-session],
[windows][api-window], [panes][api-pane], [clients][api-client],
[options][api-tmuxoptions], [hooks][api-tmuxhooks] and buffers, against every
tmux from **3.3a to 3.7c**, on **net8.0** and **net10.0**.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

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

[`ConnectAsync`][api-server-connectasync] with no arguments takes the first of these that says anything:

| Source | What it decides |
|---|---|
| [`ServerConnectionOptions`][api-serverconnectionoptions] | an explicit `socketPath`, `socketName`, or `socketNameFactory` |
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
[`Server.FromEnvironment()`][api-server-fromenvironment] reads the socket path out of the `TMUX` variable
tmux exports into every pane. `ConnectAsync` never consults it.

Every call that reaches tmux is asynchronous and takes a `CancellationToken`.
There are no synchronous twins to choose between.

## Three ways to reach tmux

Which one a call uses is visible where the call starts, and all three work on
every supported tmux.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| One-shot | [`session.CreateWindowAsync(…)`][api-session-createwindowasync] | one command, awaited | another process — **~2.3 ms** |
| Control | [`server.EnterControlModeAsync(ct)`][api-server-entercontrolmodeasync] | one client, streamed | another round trip — **~0.2 ms** |
| Chained | [`server.Chain()…ExecuteAsync(ct)`][api-server-chain] | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

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

For readiness text printed by a pane, [`Pane.WaitForTextAsync`][api-pane-waitfortextasync]
owns the control client and returns a typed outcome with a bounded rendered
tail. The [executed pane-text example](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/modes/control-mode.md#wait-for-rendered-text)
shows setup and cleanup.

## Reading what is there

Accessors return `IReadOnlyList<T>` over an explicit read and never shell out
while you enumerate them.

[`GetSessionsAsync`][api-server-getsessionsasync] and [`GetAttachedSessionsAsync`][api-server-getattachedsessionsasync] throw [`TmuxCommandException`][api-tmuxcommandexception]
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
[`IsAliveAsync`][api-server-isaliveasync] is the explicit convenience that returns `false` on library
failures. Raw [`ExecuteCommandAsync`][api-server-executecommandasync] keeps completed nonzero exit codes in its
[`TmuxCommandResult`][api-tmuxcommandresult].

`Server.Open(options).InspectAsync(ct)` reads an endpoint without running
`InitializeAsync` or starting a daemon. It returns null only for verified
daemon absence; permission and transport failures remain errors. The returned
handle exposes the observed [`DaemonVersion`][api-server-daemonversion] separately from the client
executable's `Version`. Its relationships remain uncaptured until an explicit
listing or snapshot. Inspecting a materialized handle rejects a replacement
daemon instead of adopting it.

Set [`NewSessionRequest.ExpectedGeneration`][api-newsessionrequest-expectedgeneration] to the inspected handle's [`Generation`][api-server-generation]
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

[`Window.MoveAsync`][api-window-moveasync], `LinkAsync` and `UnlinkAsync` verify that the captured
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
[`CaptureSnapshotAsync`][api-server-capturesnapshotasync]:

```csharp run
Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
SnapshotMetadata acquired = snapshot.SnapshotMetadata!;
Console.WriteLine($"{acquired.Depth}: {acquired.Elapsed} on {acquired.Generation}");
foreach (Pane member in snapshot.Panes)
{
    Console.WriteLine($"{member.Session.Name}/{member.Window.Index}: {member.CurrentCommand}");
}
```

Every depth performs a guarded read, including [`SnapshotDepth.Server`][api-snapshotdepth-server] on an
already connected handle. [`SnapshotMetadata`][api-snapshotmetadata] records depth, daemon generation,
UTC start/end readings and monotonic elapsed time. A clock adjustment can move
the UTC end before the start; use `Elapsed` for duration. Ordinary connected
handles have no snapshot metadata.

Capture reads the hierarchy over an interval. Contradictory parent placements or
child counts throw [`InconsistentSnapshotException`][api-inconsistentsnapshotexception] without returning a partial
graph or retrying. Equal-count changes can escape detection; this is not an
atomic snapshot. Unacquired relations remain unavailable, while captured
relations and metadata can be read without tmux I/O.

Captured children retain the same root through [`Server`][api-server]. Parent and active-child
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

[`GetSessionAsync`][api-server-getsessionasync], `GetWindowAsync`, and `GetPaneAsync` return materialized
objects. Their names, dimensions, indexes and other scalar properties are local
reads. `Get…Async` throws [`TmuxObjectNotFoundException`][api-tmuxobjectnotfoundexception] for an absent entity;
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

[`Session.ActiveWindow`][api-session-activewindow], [`Session.ActivePane`][api-session-activepane] and [`Window.ActivePane`][api-window-activepane] expose
`CapturedValue<T>`, which holds at most one child. Read `Value`, or
`TryGetValue` when absence is expected; `OrNull` answers null instead of
throwing. A session reached through an inactive window has that window's row,
so its own active window may be uncaptured, and reading `Value` then throws
[`IncompleteSnapshotException`][api-incompletesnapshotexception] rather than reporting that there is none.
`RefreshAsync` captures the entity's current active child.
`CaptureSnapshotAsync(SnapshotDepth.Panes)` also preserves the captured
parent and child graph. Reading any of these properties performs no I/O.

### Migrating from identity-only lookups

- Remove a `RefreshAsync` used only to make a lookup's scalar properties
  readable. Keep it when current state is needed later.
- Replace a nullable [`Session.GetWindowAsync`][api-session-getwindowasync] or [`Window.GetPaneAsync`][api-window-getpaneasync] call
  with [`FindWindowAsync`][api-session-findwindowasync] or [`FindPaneAsync`][api-window-findpaneasync]. Required `Get…Async` calls throw
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

[`RunAsync`][api-pane-runasync] waits for a POSIX shell command and reports its real exit status
and bounded rendered pane output. A timeout returns `TimedOut = true` and
`ExitStatus = null`; the command may still be running. Another run in the
same pane is refused until completion can be authenticated. The server
remains owned by its caller; separate processes are not coordinated.

```csharp run
PaneRunResult run = await pane.RunAsync(
    "printf 'hello-from-libtmux\\n'",
    TimeSpan.FromSeconds(10),
    cancellationToken: ct);
if (run.ExitStatus != 0)
{
    throw new InvalidOperationException($"Command exited {run.ExitStatus}.");
}

if (!run.Output.Contains("hello-from-libtmux"))
{
    throw new InvalidOperationException("Command output was not observed in the pane.");
}
```

`Output` is rendered pane text, not byte-exact stdout or stderr. Check
`LinesMissed`, `AnchorLost`, and the omitted-output counts before treating it
as complete. For interactive programs, [`SendTextAsync`][api-pane-sendtextasync] types input without
assuming a shell command ended.

If a completed run's private files cannot be deleted, `RunAsync` throws
[`LibTmuxException`][api-libtmuxexception]. Its `Data["LibTmux.CompletedRunResult"]` retains the bounded
[`PaneRunResult`][api-panerunresult], and `Data["LibTmux.RunDirectoryCleanupDirectory"]` names the
owned directory to inspect and remove. The command has already completed;
do not run it again. A cleanup failure during an earlier refusal stays on the
original exception in `Data["LibTmux.RunDirectoryCleanupFailure"]`. Failed
temporary-buffer deletion similarly preserves the original error and names
the owned buffer in `Data["LibTmux.PasteBufferCleanupBuffer"]`.

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

Source queries make acquisition explicit. Prepare a reusable plan from an
inspected daemon version, then execute it for a fresh observation:

```csharp run
Server inspected = await server.InspectAsync(ct)
    ?? throw new InvalidOperationException("The tmux daemon is absent.");
QueryDocument predicate = QueryExtensions.Translate<Window>(
    candidate => candidate.IsActive && candidate.Name.StartsWith("build", StringComparison.Ordinal));
QueryPlan<Window> plan = predicate.Plan<Window>(inspected.DaemonVersion!.Value);
QueryResult<Window> result = await plan.ExecuteAsync(inspected, ct);

Console.WriteLine($"{result.Count} matches from {result.Snapshot.Windows.Count} placements");
foreach (string reason in plan.FallbackReasons)
{
    Console.WriteLine(reason);
}
```

`Auto` evaluates an exact leading predicate in tmux and the remainder locally.
`Never` evaluates everything locally; `Require` rejects a plan needing local
predicate evaluation. Inspect `PushedPredicate`, `ResidualPredicate`,
`RequiredFields` and `RequiredSnapshotDepth` without I/O. Execution checks the
actual daemon version and generation before acquisition, and never initializes
or starts an absent daemon.

Source evaluation currently supports canonical ID equality, [`Session.Attached`][api-session-attached]
and [`Window.IsActive`][api-window-isactive] on stable tmux 3.3a through 3.7c. Text, numbers and graph
predicates use the local interpreter. Ordered conjunctions preserve earlier
local errors; partial disjunctions and negations stay local.

The source predicate travels in a private projection marker. It moves predicate
work into tmux, **without reducing rows or payload**. `result.Snapshot` retains
the complete graph at the required depth, even when the result is empty.
Repeated window placements remain separate, and acquisition is an interval,
not a transaction. Plans execute native sessions, windows or panes; clients and
projection DTOs remain local filtering inputs.

The document carries stable wire names: [`Session.Name`][api-session-name] is `session_name` and
[`Client.IsControlClient`][api-client-iscontrolclient] is `client_control_mode`. Discover the supported
fields and wire operations without contacting tmux:

```csharp run
foreach (QueryFieldDescriptor field in QueryFieldCatalog.GetFields(QueryTarget.Pane))
{
    Console.WriteLine($"{field.WireName}: {string.Join(", ", field.Operators)}");
}
```

Descriptors distinguish scalar paths such as `Panes.Count` from relation paths
such as `Panes` and `ActiveWindow.Value`. They report related targets,
cardinality, scalar nullability and minimum capture depth. Nested predicates
use [`QueryDocument.RequiredSnapshotDepth`][api-query-querydocument-requiredsnapshotdepth] for the complete requirement. An
uncaptured relation remains an error; it is not empty or null. Clients have no
hierarchy snapshot depth, and the schema-only `client_id` has no entity binding.

Documents can also filter records whose property names are the PascalCase wire
names:

```csharp
internal sealed record PaneRow(string PaneId, string PaneCommand);
```

A field outside the catalog throws [`UnsupportedQueryExpressionException`][api-unsupportedqueryexpressionexception] rather
than falling back. Typed queries evaluate locally over captured objects and are
never assembled into tmux's executable format language. [`UnsafeTmuxFilter`][api-unsafetmuxfilter] is
the separate opt-in for native tmux `-f` behavior.

Put it on the wire with
[LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json).

## Versions

Where a flag is missing on the running tmux, the request goes out without it
and a warning says what was left off. Where a whole command is missing, nothing
is sent and [`TmuxVersionTooLowException`][api-tmuxversiontoolowexception] says which version would be needed.

```csharp run
// The materialized handle records the verified client executable version.
TmuxVersion? version = server.Version;
Console.WriteLine($"tmux {version?.Raw} 3.4-or-newer={version?.IsAtLeast(TmuxVersion.Parse("3.4"))}");
```

## Testing your own code

[`LibTmux.Testing`](https://www.nuget.org/packages/LibTmux.Testing) is a separate package, so
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

Pass an `ILogger` when connecting and every tmux command the handle runs as a
tmux process is recorded once, at the single point they all pass through.
Commands sent through a control-mode client, including the reads a pane wait
makes through one, are not:

```csharp
Server logged = await Server.ConnectAsync(new ServerConnectionOptions { Logger = logger });
```

Commands are recorded at `Debug` and failures at `Error`, with stable scalar
fields (`TmuxSubcommand`, `TmuxSocket`, `TmuxExitCode`) to filter on. Anything
that can carry a payload is truncated, the command line included.

## Tracing and metrics

Each of those commands is also a span and a measurement. [`TmuxDiagnostics`][api-tmuxdiagnostics]
names the sources, so a telemetry pipeline subscribes by name and this library
keeps its single dependency: pass `TmuxDiagnostics.ActivitySourceName` to
OpenTelemetry's `AddSource`, and `TmuxDiagnostics.MeterName` to its `AddMeter`.

The span is named for the subcommand and tagged `tmux.subcommand`,
`tmux.socket` and `tmux.exit_code`; a failure carries `error.type` and an error
status. `TmuxDiagnostics.CommandDurationInstrumentName` records elapsed seconds
under the same tags. Both cost nothing when nothing is listening.

## Wrapping every command

[`Interceptor`][api-serverconnectionoptions-interceptor] sits between the library and the tmux it starts, so a policy that
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

`Server`, [`Window`][api-window], [`Pane`][api-pane], [`Client`][api-client], [`TmuxOptions`][api-tmuxoptions], [`TmuxHooks`][api-tmuxhooks],
[`TmuxEnvironment`][api-tmuxenvironment], [`TmuxChain`][api-tmuxchain] and `CapturedRelation<T>` are immutable once
constructed and safe to share freely, including as a DI singleton. No public
method mutates the handle it was called on: `RefreshAsync`, [`RenameAsync`][api-window-renameasync],
`ConnectAsync` and `CaptureSnapshotAsync` each answer a new handle, and a stale
handle stays a correct record of what was read.

[`Session`][api-session] is safe to share on the same terms. [`IControlModeSession.SendAsync`][api-icontrolmodesession-sendasync]
is safe to call concurrently — tmux answers in the order it received, and each
caller gets its own reply — while `Events` is a single-consumer stream and
`DisposeAsync` is idempotent from any thread.

Two limits are worth knowing before registering a singleton. A handle from
`ConnectAsync` pins the server generation it discovered, so after tmux restarts
its derived entities throw [`StaleServerGenerationException`][api-staleservergenerationexception] rather than
silently addressing the new server; [`Server.Open`][api-server-open] defers discovery to each
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
| tmux | 3.3a to 3.7c |
| .NET | net8.0, net10.0 |
| OS | Linux and macOS. `Server`, `Session`, `Window` and `Pane` are annotated unsupported on Windows, because their lifecycle, mutation and control-mode contracts need a real tmux |
| Trimming / NativeAOT | Core APIs are analyzer-gated. Query [`Compile`][api-query-queryextensions-compile] and [`Matching`][api-query-queryextensions-matching] resolve properties by name, so they warn trimmed callers to preserve the filtered types' public properties |
| Windows preview | [`PsmuxServer`][api-psmuxserver], [`PsmuxSession`][api-psmuxsession], [`PsmuxWindow`][api-psmuxwindow] and [`PsmuxPane`][api-psmuxpane] read one [psmux](https://github.com/psmux/psmux) session — its windows, its panes, and pane text — natively or across WSL. They cannot express lifecycle, mutation, chaining, control mode, or raw commands, so a caller gets a compile error where a suppression would have given a silent gap. [The preview contract](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/psmux.md) names the build it accepts and how to provision it |

## Related packages

| Package | Adds |
|---|---|
| [LibTmux.FSharp](https://www.nuget.org/packages/LibTmux.FSharp) | Curried task helpers, native F# sequences, and portable snapshot filters over `LibTmux` |
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |
| [LibTmux.Testing](https://www.nuget.org/packages/LibTmux.Testing) | Scoped tmux servers, sessions and windows for tests |
| [LibTmux.Extensions.DependencyInjection](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection) | A `Server` handle for `Microsoft.Extensions.DependencyInjection` |
| [LibTmux.Workspace.Cli](https://www.nuget.org/packages/LibTmux.Workspace.Cli) | `tmux-workspace`, a tmuxp-compatible command, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

What changed between versions: [CHANGELOG](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## Package examples

### [`LibTmux`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux): the client

This package. [Start here](#start-here) shows the first calls. Reference:
[libtmux.org](https://libtmux.org/en/csharp/latest/reference/) and
[NuGet](https://www.nuget.org/packages/LibTmux).

### [`LibTmux.FSharp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.FSharp): F# helpers over the core

Curried task helpers, ordinary `seq` queries, and typed portable filters over
the core handles. It is built on `LibTmux`, carries its version, and is
maintained in the same organization by the same primary author. Reference:
[libtmux.org](https://libtmux.org/en/fsharp/latest/reference/),
[guides](https://libtmux.org/en/fsharp/latest/guides/), and
[NuGet](https://www.nuget.org/packages/LibTmux.FSharp).

```console
$ dotnet package add LibTmux.FSharp --prerelease
```

[`Server.panes`][api-fs-fsharp-server-panes]
describes a listing and
[`Query.list`][api-fs-fsharp-query-list]
reads it. tmux narrows the listing, then every row is rechecked:

<!-- fsharp-snippet: ListShellPanes run -->
```fsharp run
open System.Threading
open LibTmux
open LibTmux.FSharp

let shellPanesAsync (cancellationToken: CancellationToken) (server: Server) =
    server
    |> Server.panes
    |> Query.where (PaneFields.currentCommand |> Filter.oneOf [ "bash"; "sh"; "zsh" ])
    |> Query.list cancellationToken
```
<!-- endfsharp-snippet -->

The
[F# quickstart](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.FSharp/README.md#quick-start)
is a complete program.

### [`LibTmux.Query.Json`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Query.Json): query documents as JSON

JSON for query documents, so a filter written in one process can be evaluated in
another. The core does not reference `System.Text.Json`; this package does.
Reference:
[`QueryJson`][api-query-json-queryjson]
and [NuGet](https://www.nuget.org/packages/LibTmux.Query.Json).

```console
$ dotnet package add LibTmux.Query.Json --prerelease
```

A query is a document rather than a lambda. [`QueryJson`][api-query-json-queryjson] writes it as JSON and
reads back an equal document:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);

string wire = QueryJson.Serialize(document);
if (QueryJson.Deserialize(wire) != document)
{
    throw new InvalidOperationException("The document did not survive the wire.");
}
```

### [`LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing): a tmux server for each test

Scoped servers, sessions, and windows that clean up after themselves, so a test
that throws leaves nothing behind. Reference:
[`TmuxTestFactory`][api-testing-tmuxtestfactory],
the
[testing guide](https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/),
and [NuGet](https://www.nuget.org/packages/LibTmux.Testing).

```console
$ dotnet package add LibTmux.Testing --prerelease
```

[`CreateHierarchyAsync`][api-testing-tmuxtestfactory-createhierarchyasync]
starts a server on a socket of its own, with one session, window, and pane.
Disposing the scope kills the server:

```csharp run
TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync(cancellationToken: ct);

await scope.Pane.SendTextAsync("echo $((6 * 7))", cancellationToken: ct);
await scope.Pane.EnterAsync(ct);

// The product is printed only once the shell has run the line.
string screen = await TmuxWait.UntilAsync(
    async token => string.Join('\n', await scope.Pane.CaptureAsync(cancellationToken: token)),
    text => text.Contains("42", StringComparison.Ordinal),
    TimeSpan.FromSeconds(10),
    TimeSpan.FromMilliseconds(20));
```

[`TmuxWait.UntilAsync`][api-tmuxwait-untilasync] ships in `LibTmux`: it waits for a state rather than
sleeping, which is what production code needs when it reads back what a command
produced.

### [`LibTmux.Extensions.DependencyInjection`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection): a Server for dependency injection

`AddLibTmux` registers a `Server` as a singleton and binds
`ServerConnectionOptions` through `IOptions<T>`, so an application that already
composes its services takes a tmux handle as a dependency. Reference:
[`Server`][api-server], the
handle it registers, the
[package README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection#registering),
and
[NuGet](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection).

```console
$ dotnet package add LibTmux.Extensions.DependencyInjection --prerelease
```

Point the registration at a socket and resolve the handle:

```csharp run
ServiceCollection services = new();
services.AddLibTmux(options => options with { SocketName = server.ConnectionOptions.SocketName });

await using ServiceProvider provider = services.BuildServiceProvider();
Server injected = provider.GetRequiredService<Server>();

IReadOnlyList<Session> sessions = await injected.GetSessionsAsync(ct);
if (sessions.Count == 0)
{
    throw new InvalidOperationException("The injected Server reached no sessions.");
}
```

### [`LibTmux.Workspace`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace): sessions from tmuxp files

Parses [tmuxp](https://github.com/tmux-python/tmuxp) YAML or JSON workspace
files and builds them as tmux sessions, in one call or from a plan you review
first. It adds one dependency, YamlDotNet. Reference:
[`WorkspaceBuilder`][api-ws-workspace-workspacebuilder],
the
[reference index](https://libtmux.org/en/csharp/latest/workspace/reference/),
and [NuGet](https://www.nuget.org/packages/LibTmux.Workspace).

```console
$ dotnet package add LibTmux.Workspace --prerelease
```

[`BuildAsync`][api-ws-workspace-workspacebuilder-buildasync]
plans and applies a workspace in one call:

```csharp run
WorkspaceFile workspace = WorkspaceFile.Parse("""
    session_name: api
    windows:
      - window_name: editor
        panes:
          - shell_command: echo editing
      - window_name: server
        panes:
          - shell_command: echo serving
    """);

WorkspaceResult result = await new WorkspaceBuilder(server).BuildAsync(workspace, ct);
if (result.Session.Name != "api" || result.Windows.Count != 2)
{
    throw new InvalidOperationException("Expected the session api with two windows.");
}
```

### [`LibTmux.Workspace.Cli`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli): the `tmux-workspace` command

Loads, captures, and converts [tmuxp](https://github.com/tmux-python/tmuxp)
workspace files from a shell. It installs as a .NET tool. Reference: the
[command reference](https://libtmux.org/en/csharp/latest/workspace/cli/),
[`load`](https://libtmux.org/en/csharp/latest/workspace/cli/load/), and
[NuGet](https://www.nuget.org/packages/LibTmux.Workspace.Cli).

```console
$ dotnet tool install --global LibTmux.Workspace.Cli --prerelease
```

With a workspace file saved as `~/.tmuxp/myproject.yaml`, such as the
[example in the package README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli#tmux-workspace),
build it without attaching, on a tmux socket of its own:

```console
$ tmux-workspace load -d -L demo myproject
```

List the workspaces it can find:

```console
$ tmux-workspace ls
```

### [`LibTmux.Mcp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Mcp): a tmux server for assistants

A server that gives an assistant hands on tmux over the
[Model Context Protocol](https://modelcontextprotocol.io/specification/2025-06-18).
It installs as a .NET tool, not a library reference. Reference:
[libtmux.org](https://libtmux.org/en/csharp/latest/mcp/), the
[tool reference](https://libtmux.org/en/csharp/latest/mcp/tools/), and
[NuGet](https://www.nuget.org/packages/LibTmux.Mcp).

```console
$ dotnet tool install --global LibTmux.Mcp --prerelease
```

An MCP client starts the server itself and speaks to it over standard input and
output. Register it with Claude Code:

```console
$ claude mcp add tmux -- libtmux-mcp
```

A workspace that `tmux-workspace load -L demo` built lives on the socket `demo`.
To let a client read it and change nothing, name the socket and select the
`inspect` toolset:

```console
$ claude mcp add tmux \
    -e LIBTMUX_SOCKET=demo \
    -e LIBTMUX_TOOLSETS=inspect \
    -- libtmux-mcp
```

A client that reads a JSON configuration takes the command and the same settings
as `env`:

```json
{
  "mcpServers": {
    "tmux": {
      "command": "libtmux-mcp",
      "env": { "LIBTMUX_SOCKET": "demo", "LIBTMUX_TOOLSETS": "inspect" }
    }
  }
}
```

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-client]: https://libtmux.org/en/csharp/latest/reference/libtmux-client/
[api-client-iscontrolclient]: https://libtmux.org/en/csharp/latest/reference/libtmux-client-iscontrolclient/
[api-fs-fsharp-query-list]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-query-list/
[api-fs-fsharp-server-panes]: https://libtmux.org/en/fsharp/latest/reference/libtmux-fsharp-server-panes/
[api-icontrolmodesession-sendasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-icontrolmodesession-sendasync/
[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-inconsistentsnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-inconsistentsnapshotexception/
[api-libtmuxexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-libtmuxexception/
[api-newsessionrequest-expectedgeneration]: https://libtmux.org/en/csharp/latest/reference/libtmux-newsessionrequest-expectedgeneration/
[api-pane]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane/
[api-pane-runasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-runasync/
[api-pane-sendtextasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-sendtextasync/
[api-pane-waitfortextasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-waitfortextasync/
[api-panerunresult]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult/
[api-psmuxpane]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxpane/
[api-psmuxserver]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxserver/
[api-psmuxsession]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxsession/
[api-psmuxwindow]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxwindow/
[api-query-json-queryjson]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson/
[api-query-querydocument-requiredsnapshotdepth]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querydocument-requiredsnapshotdepth/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-server-capturesnapshotasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-capturesnapshotasync/
[api-server-chain]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-chain/
[api-server-connectasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-connectasync/
[api-server-daemonversion]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-daemonversion/
[api-server-entercontrolmodeasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-entercontrolmodeasync/
[api-server-executecommandasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-executecommandasync/
[api-server-fromenvironment]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-fromenvironment/
[api-server-generation]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-generation/
[api-server-getattachedsessionsasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getattachedsessionsasync/
[api-server-getsessionasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getsessionasync/
[api-server-getsessionsasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getsessionsasync/
[api-server-isaliveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-isaliveasync/
[api-server-open]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-open/
[api-serverconnectionoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions/
[api-serverconnectionoptions-interceptor]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions-interceptor/
[api-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-session/
[api-session-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activepane/
[api-session-activewindow]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activewindow/
[api-session-attached]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-attached/
[api-session-createwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-createwindowasync/
[api-session-findwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-findwindowasync/
[api-session-getwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-getwindowasync/
[api-session-name]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-name/
[api-snapshotdepth-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotdepth-server/
[api-snapshotmetadata]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotmetadata/
[api-staleservergenerationexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-staleservergenerationexception/
[api-testing-tmuxtestfactory]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/
[api-testing-tmuxtestfactory-createhierarchyasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory-createhierarchyasync/
[api-tmuxchain]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxchain/
[api-tmuxcommandexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxcommandexception/
[api-tmuxcommandresult]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxcommandresult/
[api-tmuxdiagnostics]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxdiagnostics/
[api-tmuxenvironment]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxenvironment/
[api-tmuxhooks]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxhooks/
[api-tmuxobjectnotfoundexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxobjectnotfoundexception/
[api-tmuxoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxoptions/
[api-tmuxversiontoolowexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxversiontoolowexception/
[api-tmuxwait-untilasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxwait-untilasync/
[api-unsafetmuxfilter]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsafetmuxfilter/
[api-unsupportedqueryexpressionexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsupportedqueryexpressionexception/
[api-window]: https://libtmux.org/en/csharp/latest/reference/libtmux-window/
[api-window-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-activepane/
[api-window-findpaneasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-findpaneasync/
[api-window-getpaneasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-getpaneasync/
[api-window-isactive]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-isactive/
[api-window-moveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-moveasync/
[api-window-renameasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-renameasync/
[api-ws-workspace-workspacebuilder]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder/
[api-ws-workspace-workspacebuilder-buildasync]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder-buildasync/
