<!-- libtmux-logo -->
<p align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="https://raw.githubusercontent.com/libtmux/libtmux-dotnet/master/src/LibTmux/assets/logo.png" width="128" height="128" alt="libtmux for C# / .NET">
  </picture>
</p>
<!-- /libtmux-logo -->

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

<!-- snippet: ConnectAndBuild usings: System, LibTmux -->
```csharp
using System;
using LibTmux;

Server server = Server.Open();
OwnedSessionScope owned = await server.CreateOwnedSessionAsync(
    new NewSessionRequest { Name = $"build-{Guid.NewGuid():N}" });
await owned.UseAsync(async (session, token) =>
{
    Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" }, token);
    Console.WriteLine($"Created {session.Id} / {window.Id}: {window.Name}");
});
```
<!-- endsnippet -->

`Server.Open()` captures an endpoint without starting tmux. Session creation
starts a daemon when needed. `UseAsync` removes the session after the callback
returns, throws or cancels, with an independent cleanup deadline. The server
handle is borrowed. `OwnedScope.CleanupFailure(error)` returns a second
cleanup error while the original body exception and cancellation token remain
intact. A cleanup failure after success propagates on its own; failed disposal
can be retried.

To reach one server in particular:

```csharp
Server elsewhere = await Server.ConnectAsync(
    new ServerConnectionOptions { SocketName = "build-box" });
```

### Endpoint defaults

`Server.Open()` and `Server.ConnectAsync()` select one endpoint from the
effective constructor environment. `ChildEnvironment` overrides host variables
without changing the host process or tmux's server/session environment tables.

The handle copies the complete effective client environment and resolves its executable against that environment's `PATH` at construction. Later host edits, including newly added variables, do not reach its subprocess or control clients. A bare executable name absent from the captured `PATH` fails when an operation tries to launch it; a later host `PATH` change does not select another executable. An explicit executable path bypasses that search.

| Precedence | Selector |
|---|---|
| 1 | Explicit `SocketPath` or `SocketName` (`SocketNameFactory` is deferred once) |
| 2 | Nonempty `LIBTMUX_SOCKET_PATH` |
| 3 | Nonempty `LIBTMUX_SOCKET_NAME` |
| 4 | Nonempty `TMUX`, parsed from the final two commas |
| 5 | The socket named `default` |

Supplying both an explicit path and a name is an `ArgumentException`. Paths
must be absolute. Names must be nonempty leaf names other than `.` and `..`.
Empty environment selectors are absent; whitespace is preserved. An invalid
selected value fails instead of trying a lower-precedence target.

`TMUX` accepts a positive ASCII decimal PID and a nonnegative decimal session
ID, optionally prefixed by `$`, or the `-1` job sentinel. Commas in the socket
path survive parsing. `Server.FromEnvironment()` explicitly selects this
context even when library defaults are present. Clients remove `TMUX` and
`TMUX_PANE` after selection, including values supplied through child overrides.

Named Unix sockets use the captured `TMUX_TMPDIR`, or `/tmp` when absent, and
`tmux-UID/name`. Launches pin that absolute path with `-S`. The root must exist;
a missing or removed root fails without falling back. Only its `tmux-UID`
directory is created, with mode 0700. An existing directory must be real,
owned by the current UID, and have no other-user permissions; group access is
allowed. Explicit socket paths do not create parent directories. The handle
keeps its endpoint when the host environment changes.

The configured Psmux preview retains its Windows data-directory and namespace
representation and its existing executable trust checks. Unix socket rules do
not apply to that backend.

Every call that reaches tmux is asynchronous and takes a `CancellationToken`.
There are no synchronous twins to choose between.

## Ownership and reuse

Lookup returns borrowed handles. Disposing a control client detaches that
client and leaves remote resources alive. `Session.AdoptAsync()`, `Window.AdoptAsync()`
and `Pane.AdoptAsync()` accept destruction responsibility for existing objects.
`Server.AdoptAsync()` first reads the daemon identity. Each owner retains
the captured endpoint, daemon and object ID through renames and moves.
Window disposal kills the window, its links and its panes; use `UnlinkAsync`
for the separate unlink operation.

Creation and explicit adoption reserve the server option
`@libtmux_owner_generation`. An absent option is initialized to 32
ASCII hexadecimal characters; a valid value is reused unchanged. An empty or
malformed existing value fails before creation or adoption and is never overwritten.
Creation captures the token on the same native connection as its receipt;
adoption captures it before accepting the resource. Cleanup tests the token
alongside PID and start time in the same native command group as destruction,
so equal numeric generations cannot redirect cleanup. Do not shadow or edit this
reserved metadata while owners exist. Plain lookup and discovery remain
read-only.

<!-- snippet: AdoptExisting usings: System, LibTmux -->
```csharp
using System;
using LibTmux;

Server server = Server.Open();
Session existing = await server.CreateSessionAsync(new NewSessionRequest { Name = $"adopt-{Guid.NewGuid():N}" });
OwnedSessionScope owner = await existing.AdoptAsync();
try
{
    await owner.UseAsync(async (session, token) =>
    {
        await session.RenameAsync("renamed-" + Guid.NewGuid().ToString("N"), token);
        Console.WriteLine($"Cleanup retains session ID {session.Id}.");
    });
}
catch (Exception error)
{
    if (OwnedScope.CleanupFailure(error) is Exception cleanup)
    {
        Console.Error.WriteLine($"Cleanup failed: {cleanup.Message}");
    }
    throw;
}
```
<!-- endsnippet -->

`await using` also disposes an owner, but C# replaces a body exception if
`DisposeAsync` throws while unwinding. Use `UseAsync` when both failures must
remain inspectable. Concurrent disposal calls share one attempt. Success is
idempotent; failure permits another attempt. Cleanup has its own five-second
deadline, including after body cancellation. A missing socket with a still-live
captured PID reports an unknown outcome and permits a cleanup retry. PID reuse
can also produce this conservative refusal; missing transport alone does not
prove that the owned resource was destroyed.

`CreateOwnedAsync` refuses an existing daemon. A unique startup environment
marker proves that the accepting call started the daemon, and a native
nonwaiting generation guard rejects replacement daemons during cleanup.
Owned servers keep running without sessions until disposal, which waits for
the captured process to exit. Startup still honors normal tmux configuration.
Whole-server examples choose an explicit disposable endpoint:

<!-- snippet: OwnDisposableServer usings: System, LibTmux -->
```csharp
using System;
using LibTmux;

var options = new ServerConnectionOptions { SocketName = "disposable-" + Guid.NewGuid().ToString("N") };
OwnedServerScope owner = await Server.CreateOwnedAsync(options);
await owner.UseAsync(async (server, token) =>
{
    await server.CreateSessionAsync(new NewSessionRequest { Name = "work" }, token);
    Console.WriteLine($"Owned daemon: {server.Generation}.");
});
```
<!-- endsnippet -->

Creation waits up to five seconds for an initial reply even if the caller
cancels, so cancellation cannot discard a returned identity. Once an ID is
known, failed readback or cancellation rolls back against that daemon.
`OwnedScope.CleanupFailure(error)` exposes a failed rollback. A missing or
malformed initial reply leaves the result unknown; inspect the endpoint
before retrying. Rollback does not undo earlier explicit replacement effects.

If rollback fails before acquisition returns an owner, `OwnedScope.CleanupOwners(error)` returns the accepted cleanup owners. Retry each owner's `DisposeAsync` after resolving the cleanup failure. These owners retain the original daemon generation and object ID; they refuse a replacement daemon. Nested callback scopes retain each failed owner, starting with the inner scope. Successful retries leave the original exception, recorded cleanup failures and owner list available for inspection. An unknown creation result with no accepted identity provides no cleanup owner.

### Find or create

`FoundOrCreated<T>.Created` distinguishes new resources from reuse. `Owner`
is present only for a resource created by the call. Disposing the result
cleans up that owner and leaves reused resources alive. Adopt a borrowed
handle only when you intend to destroy it later.

<!-- snippet: FindOrCreateHierarchy usings: System, LibTmux -->
```csharp
using System;
using LibTmux;

Server server = Server.Open();
string name = "build-" + Guid.NewGuid().ToString("N");
await using FoundOrCreated<Session> session = await server.FindOrCreateSessionAsync(name);
await using FoundOrCreated<Window> window = await session.Value.FindOrCreateWindowAsync("tests");
await using FoundOrCreated<Pane> pane = await window.Value.FindOrCreatePaneAsync("application/test-runner");
await using FoundOrCreated<Pane> reused = await window.Value.FindOrCreatePaneAsync("application/test-runner");
Console.WriteLine($"Created: {pane.Created}; reused: {!reused.Created}; borrowed: {reused.Owner is null}.");
```
<!-- endsnippet -->

| Operation | Matching rule |
|---|---|
| `Server.FindOrCreateAsync` | One daemon at the captured endpoint |
| `Server.FindOrCreateSessionAsync` | Exact literal session name |
| `Session.FindOrCreateWindowAsync` | Exact literal name within that session |
| `Window.FindOrCreatePaneAsync` | Exact local pane option `@libtmux-identity` within that window |

Window and pane duplicates raise `TmuxAmbiguousMatchException`. tmux itself
forbids duplicate session names; a competing creator's session returns as
borrowed. Pane creation installs its identity and rolls back if that step
fails. Names containing `#` remain literal under these matching APIs.

Calls sharing the same socket path spelling serialize within this process,
including independently opened handles. Path aliases and unrelated tmux
clients do not share that lock. Other clients can rename, remove or duplicate
windows and pane identities; cross-process uniqueness needs application
coordination. A bounded set of gates means different paths can also serialize.
Initializers must not call find-or-create while acquisition holds a gate.
Borrowed server reuse skips `InitializeAsync`. A competing daemon starter remains borrowed unless its
startup environment proves that this call started it.

### Bounded discovery

`Server.InspectAsync` reads one known endpoint. `Server.DiscoverAsync` scans
immediate children of the directories in `Roots` plus the current user's
configured socket directory and the selected endpoint's directory. Set
`IncludeConfiguredRoots = false` for an explicit search only.

<!-- snippet: DiscoverServers usings: System, LibTmux -->
```csharp
using System;
using LibTmux;

ServerDiscoveryResult discovery = await Server.DiscoverAsync(new ServerDiscoveryOptions
{
    MaximumEntries = 64,
    MaximumProbes = 16,
    Timeout = TimeSpan.FromSeconds(2),
});
foreach (DiscoveredServer found in discovery.Servers)
{
    Console.WriteLine($"{found.SocketPath}: {found.Server.Generation}");
}
foreach (ServerDiscoveryDiagnostic diagnostic in discovery.Diagnostics)
{
    Console.WriteLine($"{diagnostic.Kind}: {diagnostic.Path}: {diagnostic.Message}");
}
Console.WriteLine($"Truncated: {discovery.Truncated}.");
```
<!-- endsnippet -->

Discovery returns borrowed handles, per-root and per-candidate diagnostics,
and `Truncated` when a root, entry, probe or total time bound stops it. A
failed probe differs from an empty directory. It skips symlink roots and
entries, non-sockets and sockets owned by another user. Distinct paths to the
same daemon generation produce one handle plus a duplicate diagnostic.
Root components resolve through the filesystem before enumeration, so
`symlink/..` follows the linked directory and missing components produce root
errors. The root limit counts input entries, including duplicates.
No-start probes cannot launch a daemon. Filesystem enumeration itself is
synchronous; the time bound applies between filesystem calls and during
probes. The result describes only the roots and bounds used by that call.

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

For readiness text printed by a pane, `Pane.WaitForTextAsync`
owns the control client and returns a typed outcome with a bounded rendered
tail. The [executed pane-text example](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/modes/control-mode.md#wait-for-rendered-text)
shows setup and cleanup.

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

`RunAsync` waits for a POSIX shell command and reports its real exit status
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
as complete. For interactive programs, `SendTextAsync` types input without
assuming a shell command ended.

If a completed run's private files cannot be deleted, `RunAsync` throws
`LibTmuxException`. Its `Data["LibTmux.CompletedRunResult"]` retains the bounded
`PaneRunResult`, and `Data["LibTmux.RunDirectoryCleanupDirectory"]` names the
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

Source evaluation currently supports canonical ID equality, `Session.Attached`
and `Window.IsActive` on stable tmux 3.2a through 3.7c. Text, numbers and graph
predicates use the local interpreter. Ordered conjunctions preserve earlier
local errors; partial disjunctions and negations stay local.

The source predicate travels in a private projection marker. It moves predicate
work into tmux, **without reducing rows or payload**. `result.Snapshot` retains
the complete graph at the required depth, even when the result is empty.
Repeated window placements remain separate, and acquisition is an interval,
not a transaction. Plans execute native sessions, windows or panes; clients and
projection DTOs remain local filtering inputs.

The document carries stable wire names: `Session.Name` is `session_name` and
`Client.IsControlClient` is `client_control_mode`. Discover the supported
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
use `QueryDocument.RequiredSnapshotDepth` for the complete requirement. An
uncaptured relation remains an error; it is not empty or null. Clients have no
hierarchy snapshot depth, and the schema-only `client_id` has no entity binding.

Documents can also filter records whose property names are the PascalCase wire
names:

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

Each of those commands is also a span and a measurement. `TmuxDiagnostics`
names the sources, so a telemetry pipeline subscribes by name and this library
keeps its single dependency: pass `TmuxDiagnostics.ActivitySourceName` to
OpenTelemetry's `AddSource`, and `TmuxDiagnostics.MeterName` to its `AddMeter`.

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
| [LibTmux.FSharp](https://www.nuget.org/packages/LibTmux.FSharp) | Curried task helpers, native F# sequences, and portable snapshot filters over `LibTmux` |
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

What changed between versions: [CHANGELOG](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
