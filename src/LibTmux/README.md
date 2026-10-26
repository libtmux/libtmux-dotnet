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

[![NuGet](https://img.shields.io/nuget/v/LibTmux)](https://www.nuget.org/packages/LibTmux)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/)

A typed, async-first [tmux](https://github.com/tmux/tmux) client for .NET:
[servers][api-server], [sessions][api-session], [windows][api-window],
[panes][api-pane], [clients][api-client], [options][api-tmuxoptions],
[hooks][api-tmuxhooks] and buffers, against every tmux from **3.3a to 3.8**, on
**net8.0** and **net10.0**.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

[Run a command][api-pane-runasync] and read its exit status,
[find][api-session-getwindowsasync] and
[filter][api-query-queryextensions-matching] what a server holds,
[type into a pane and wait][api-tmuxwait-untilasync] for the answer, and choose
[how a call reaches tmux][api-server-entercontrolmodeasync].

It suits tools that drive a terminal program, test suites that need a real tmux,
and agents that share a terminal.

Add it to your project:

```console
$ dotnet package add LibTmux
```

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

**Contents** — [Quick start](#quick-start) ·
[Find things](#find-sessions-windows-and-panes) ·
[Run a command](#run-a-command-and-read-what-it-printed) ·
[Change tmux](#change-windows-panes-and-options) ·
[Filter](#filter-what-you-read) · [Choose a path](#choose-how-to-reach-tmux) ·
[Retries](#know-when-a-retry-is-safe) · [Observe](#see-what-the-library-does) ·
[Testing](#testing-your-own-code) · [Packages](#related-packages) ·
[Leaves out](#what-it-leaves-out) · [Support](#support-and-status)

## Quick start

Connect to the tmux server that is running, make a session, a window, and a
pane, and type a command into the pane. This is the whole program:

<!-- snippet: ConnectAndBuild usings: LibTmux -->
```csharp
using LibTmux;

// Requires a tmux server already listening on this socket:
// ConnectAsync() discovers one, it never starts one. With nothing
// running yet, call Server.CreateOwnedAsync() instead.
Server server = await Server.ConnectAsync();
Session session = await server.CreateSessionAsync(new NewSessionRequest { Name = "build" });
Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" });
Pane pane = (await window.GetPanesAsync())[0];

await pane.SendTextAsync("dotnet test");
```
<!-- endsnippet -->

To run it, make a console project and paste the program into `Program.cs`, with
tmux 3.3a or newer on `PATH` and a server running:

```console
$ dotnet new console --output demo && \
    cd demo && \
    dotnet package add LibTmux
```

It prints nothing. The new session `build` has a window `tests`, and its pane
has received `dotnet test`.

[`Server.ConnectAsync`][api-server-connectasync] finds a server that is already
listening and never starts one. With nothing running, call
[`Server.CreateOwnedAsync`][api-server-createownedasync], which starts a server
and stops it when disposed.

Every call that reaches tmux is asynchronous and takes a `CancellationToken`.

Every `csharp run` block below runs against a tmux server of its own, with
`server`, `session`, `window`, `pane`, and `ct` already in hand.

## Find sessions, windows, and panes

### [`Session.GetWindowsAsync`][api-session-getwindowsasync]: the windows of a session

One call reads the windows; [`Window.GetPanesAsync`][api-window-getpanesasync]
reads a window's panes, and
[`Server.GetSessionsAsync`][api-server-getsessionsasync] reads the sessions.
They return `IReadOnlyList<T>` and never run tmux while you enumerate them:

```csharp run
foreach (Window each in await session.GetWindowsAsync(ct))
{
    foreach (Pane every in await each.GetPanesAsync(ct))
    {
        Console.WriteLine($"{each.Name} {every.Index} {every.Width}x{every.Height}");
    }
}
```

It prints one line per pane: the window name, the pane index, and its size.

A listing that tmux rejects throws
[`TmuxCommandException`][api-tmuxcommandexception]. A live server with no
sessions returns an empty list. [`Server.IsAliveAsync`][api-server-isaliveasync]
returns `false` when the server cannot be reached.

### [`Server.CaptureSnapshotAsync`][api-server-capturesnapshotasync]: the whole hierarchy in one read

A snapshot walks sessions, windows, and panes once, to the
[`SnapshotDepth`][api-snapshotdepth] you ask for, and records how it went in
[`SnapshotMetadata`][api-snapshotmetadata]:

```csharp run
Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
SnapshotMetadata acquired = snapshot.SnapshotMetadata!;
Console.WriteLine($"{acquired.Depth}: {acquired.Elapsed} on {acquired.Generation}");
foreach (Pane member in snapshot.Panes)
{
    Console.WriteLine($"{member.Session.Name}/{member.Window.Index}: {member.CurrentCommand}");
}
```

It prints the depth, how long the read took, and one line per pane.

- The read takes time and tmux can change while it runs. Contradictory
  placements throw
  [`InconsistentSnapshotException`][api-inconsistentsnapshotexception].
- Reading a relation the depth did not capture throws
  [`IncompleteSnapshotException`][api-incompletesnapshotexception].
- Recapturing builds a separate graph.
  [`Pane.RefreshAsync`][api-pane-refreshasync] replaces only one entity's
  fields.

### [`Session.FindWindowAsync`][api-session-findwindowasync]: look one up

[`Server.GetWindowAsync`][api-server-getwindowasync] throws
[`TmuxObjectNotFoundException`][api-tmuxobjectnotfoundexception] for an absent
window; `FindWindowAsync` returns `null` after a successful lookup that finds
none:

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

It prints the window's name and size, `missing True`, and the active window's
name.

[`Session.ActiveWindow`][api-session-activewindow],
[`Session.ActivePane`][api-session-activepane], and
[`Window.ActivePane`][api-window-activepane] hold at most one captured child.
Read `Value`, or `TryGetValue` when absence is expected; reading `Value` on an
uncaptured child throws, and no property read runs tmux.

## Run a command and read what it printed

### [`Pane.RunAsync`][api-pane-runasync]: a command's real exit status

It runs a POSIX shell command in a pane and returns
[`PaneRunResult`][api-panerunresult]: the exit status and the rendered pane
output. A command that exits nonzero returns normally with that status:

```csharp run tmux-3.4
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

It prints nothing and throws if the status or the line is wrong.

- A timeout returns `TimedOut = true` and `ExitStatus = null`; the command may
  still be running.
- [`PaneRunResult.Output`][api-panerunresult-output] is the rendered pane text,
  so it can differ from the command's stdout and stderr bytes. Check
  [`PaneRunResult.LinesMissed`][api-panerunresult-linesmissed] and
  [`PaneRunResult.AnchorLost`][api-panerunresult-anchorlost] before relying on
  it.
- A second run in the same pane is refused until the first can be authenticated
  as complete.

### [`Pane.SendTextAsync`][api-pane-sendtextasync]: type, then wait for the result

For a program that reads input directly, type it and wait for what it prints
with [`TmuxWait.UntilAsync`][api-tmuxwait-untilasync]. tmux accepts a command
before the shell has finished it, so the example waits for the result:

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

It prints nothing; it returns once the pane shows the line.

Text goes in as input: leading dashes, semicolons, and newlines are typed, and
NUL is rejected before dispatch.

For readiness text a pane prints,
[`Pane.WaitForTextAsync`][api-pane-waitfortextasync] returns a typed outcome
with a bounded rendered tail; the
[executed example](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/modes/control-mode.md#wait-for-rendered-text)
shows setup and cleanup.

## Change windows, panes, and options

### [`Window.RenameAsync`][api-window-renameasync]: a changed copy

A handle says what it read, and that stays true. An operation that changes what
an object is hands back a replacement:

```csharp run
Window renamed = await window.RenameAsync("integration", ct);
```

It prints nothing; `renamed` has the new name and `window` keeps the old one.

[`Window.MoveAsync`][api-window-moveasync],
[`Window.LinkAsync`][api-window-linkasync], and
[`Window.UnlinkAsync`][api-window-unlinkasync] check that the captured session
and index still name the window before they change it.

If topology changes leave the result unknown, the call throws with unknown
dispatch state: do not retry it.

### [`Pane.SplitAsync`][api-pane-splitasync]: add a pane

```csharp run
Pane split = await pane.SplitAsync(new SplitPaneRequest { Direction = PaneDirection.Below }, ct);
await split.SetHeightAsync(10, ct);
```

It adds a pane below the first and sets its height to ten rows.

### [`TmuxOptions`][api-tmuxoptions] and [`TmuxHooks`][api-tmuxhooks]: settings and callbacks

tmux has no types, so an option value carries the text tmux reported alongside
the readings that text supports. An option the window does not hold is reads as
the inherited value:

```csharp run
await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"), ct);
TmuxOption option = (await window.Options.GetAsync(
    new GetOptionRequest("automatic-rename"), ct))[0];

Console.WriteLine($"{option.Value.Raw} flag={option.Value.Boolean}");
```

It prints the raw value and whether tmux reads it as a flag.

Hooks are arrays even with one entry:

```csharp run
TmuxHook hook = await server.Hooks.SetAsync(
    new SetHookRequest("alert-bell", "set-option -g @rang yes"), ct);
```

## Filter what you read

### Plain LINQ

Ordinary filtering is LINQ over what you already read:

```csharp run
IReadOnlyList<Window> windows = await session.GetWindowsAsync(ct);
IEnumerable<Window> building = windows.Where(
    each => each.Name.StartsWith("build", StringComparison.Ordinal));
```

### [`QueryExtensions.Matching`][api-query-queryextensions-matching]: declarative filters over what you hold

It turns an expression into a portable document, or throws; it never quietly
falls back to filtering in memory:

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

### [`QueryExtensions.Translate`][api-query-queryextensions-translate]: the same filter as a document

The expression is also a document, written here and answered somewhere else.
[LibTmux.Query.Json][pkg-query-json] puts it on the wire:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);
```

The document uses stable wire names: [`Session.Name`][api-session-name] is
`session_name` and [`Client.IsControlClient`][api-client-iscontrolclient] is
`client_control_mode`.

A field outside the catalog throws
[`UnsupportedQueryExpressionException`][api-unsupportedqueryexpressionexception].
[`UnsafeTmuxFilter`][api-unsafetmuxfilter] is the separate opt-in for native
tmux `-f` filters.

### [`QueryPlan<T>`][api-query-queryplan]: ask tmux to narrow the read

Prepare a reusable plan from an inspected daemon version with
[`Server.InspectAsync`][api-server-inspectasync], then execute it for a fresh
observation:

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

It prints the match count and why anything stayed local.

- [`QueryPushdown.Auto`][api-query-querypushdown-auto] evaluates an exact
  leading predicate in tmux and the rest locally;
  [`QueryPushdown.Never`][api-query-querypushdown-never] evaluates everything
  locally; [`QueryPushdown.Require`][api-query-querypushdown-require] rejects a
  plan that needs local evaluation.
- The pushed predicate moves work into tmux **without reducing rows or
  payload**.
- Execution never initializes or starts an absent daemon.

### [`QueryFieldCatalog`][api-query-queryfieldcatalog]: which fields can be filtered

```csharp run
foreach (QueryFieldDescriptor field in QueryFieldCatalog.GetFields(QueryTarget.Pane))
{
    Console.WriteLine($"{field.WireName}: {string.Join(", ", field.Operators)}");
}
```

It prints each pane field's wire name and the operators it supports.

## Choose how to reach tmux

### [`Server.ConnectAsync`][api-server-connectasync]: which server it reaches

With no arguments it takes the first of these that says anything:

| Source | What it decides |
|---|---|
| [`ServerConnectionOptions`][api-serverconnectionoptions] | an explicit `socketPath`, `socketName`, or `socketNameFactory` |
| `LIBTMUX_SOCKET_PATH` | the socket, by path |
| `LIBTMUX_SOCKET_NAME` | the socket, by name, under the root below |
| `TMUX_TMPDIR` | the root a name resolves under — `/tmp` when unset |
| — | the socket named `default` |

- Options always win, so exporting a variable for a whole process cannot
  redirect a call that named a socket.
- [`Server.FromEnvironment`][api-server-fromenvironment] reads the socket out of
  the `TMUX` variable inside a pane;
  [`Server.ConnectAsync`][api-server-connectasync] never consults it.

### Three ways to send a command

Which one a call uses is visible where the call starts, and all three work on
every supported tmux.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| One-shot | [`session.CreateWindowAsync(…)`][api-session-createwindowasync] | one command, awaited | another process — **~2.3 ms** |
| Control | [`server.EnterControlModeAsync(ct)`][api-server-entercontrolmodeasync] | one client, streamed | another round trip — **~0.2 ms** |
| Chained | [`server.Chain()…ExecuteAsync(ct)`][api-server-chain] | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

Each figure is the marginal cost of one more command, as medians of 100 samples
against tmux 3.7b; absolute timings move with the machine. The
[recorded runs](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/benchmarks/README.md)
give the distribution, the tmux, the host, and the date.

### [`Session.CreateWindowAsync`][api-session-createwindowasync]: one command, a typed object back

```csharp run
// One command, a typed object back.
Window built = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
```

### [`Server.EnterControlModeAsync`][api-server-entercontrolmodeasync]: one client, held open

```csharp run tmux-3.7
// One client, held open, streaming what tmux does on its own.
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> reply = await control.SendAsync(
    TmuxCommand.Create("list-windows"),
    ct);
```

Control mode is an order of magnitude cheaper per command.
[`IControlModeSession.SendAsync`][api-icontrolmodesession-sendasync] is safe to
call concurrently.

### [`Server.Chain`][api-server-chain]: many commands, one invocation

```csharp run
// Many commands, one invocation, one process cost.
await server.Chain()
    .Then("new-window", "-d", "-n", "one")
    .Then("new-window", "-d", "-n", "two")
    .ExecuteAsync(ct);
```

A chain pays one round trip for the whole sequence, so it wins for a batch.

## Know when a retry is safe

### [`LibTmuxException.Dispatch`][api-libtmuxexception-dispatch]: did tmux get the command?

Retrying is sound only when the command never reached tmux, and every failure
says which it was, so the decision is an exception filter:

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

It prints `safe to retry: NotDispatched` when no tmux process ran.

- [`TmuxDispatchState.NotDispatched`][api-tmuxdispatchstate-notdispatched] is
  claimed only where no tmux process ran: a missing binary, or a command
  rejected before launch.
- [`TmuxDispatchState.Unknown`][api-tmuxdispatchstate-unknown] is the default,
  for a client that started and then died.
- [`TmuxCommandException`][api-tmuxcommandexception] is always
  [`TmuxDispatchState.Dispatched`][api-tmuxdispatchstate-dispatched]: it exists
  because tmux answered.

## See what the library does

### [`ServerConnectionOptions.Logger`][api-serverconnectionoptions-logger]: log each command

Pass an `ILogger` when connecting. Every command the handle runs as a tmux
process is recorded once, at `Debug`, and failures at `Error`; commands sent
through a control-mode client are not:

```csharp
Server logged = await Server.ConnectAsync(new ServerConnectionOptions { Logger = logger });
```

### [`TmuxDiagnostics`][api-tmuxdiagnostics]: spans and metrics

Each command is also a span and a measurement. Subscribe by name, so the library
keeps its single dependency: pass `TmuxDiagnostics.ActivitySourceName` to
OpenTelemetry's `AddSource` and `TmuxDiagnostics.MeterName` to `AddMeter`.

Both cost nothing when nothing listens.

### [`ServerConnectionOptions.Interceptor`][api-serverconnectionoptions-interceptor]: wrap every command

A policy that belongs to your application, such as an audit trail, a retry, or a
stand-in answer in a test, lives outside the library:

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
tmux's place. Commands sent through a control-mode client skip the interceptor.

## Share handles across threads

- [`Server`][api-server], [`Window`][api-window], [`Pane`][api-pane],
  [`Client`][api-client], [`TmuxOptions`][api-tmuxoptions],
  [`TmuxHooks`][api-tmuxhooks], and [`TmuxChain`][api-tmuxchain] are immutable
  and safe to share, including as a DI singleton.
- A handle from [`Server.ConnectAsync`][api-server-connectasync] pins the server
  generation it found, so after tmux restarts its entities throw
  [`StaleServerGenerationException`][api-staleservergenerationexception];
  [`Server.Open`][api-server-open] defers discovery to each call instead.
- Nothing serializes tmux itself: concurrent callers reach one server, which
  applies commands in the order it receives them.

## Testing your own code

[LibTmux.Testing][pkg-testing] gives a test a tmux server of its own, on its own
socket, killed deterministically. It is a separate package, so test scaffolding
stays out of an application's output:

```console
$ dotnet package add LibTmux.Testing
```

Disposing the scope kills the server, so a test that fails part way through
leaves nothing behind. [`TmuxTestFactory`][api-testing-tmuxtestfactory] hands
out the scopes:

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

## Related packages

| Package | Adds |
|---|---|
| [LibTmux.FSharp](https://www.nuget.org/packages/LibTmux.FSharp) | Curried task helpers, native F# sequences, and portable snapshot filters over `LibTmux` |
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Workspace](https://www.nuget.org/packages/LibTmux.Workspace) | Sessions from tmuxp YAML |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |
| [LibTmux.Testing](https://www.nuget.org/packages/LibTmux.Testing) | Scoped tmux servers, sessions and windows for tests |
| [LibTmux.Extensions.DependencyInjection](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection) | A [`Server`][api-server] handle for `Microsoft.Extensions.DependencyInjection` |
| [LibTmux.Workspace.Cli](https://www.nuget.org/packages/LibTmux.Workspace.Cli) | `tmux-workspace`, a tmuxp-compatible command, as a .NET tool |

## Packages

### [`LibTmux`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux): the client

This package. [Quick start](#quick-start) shows the first calls.

Reference: [libtmux.org](https://libtmux.org/en/csharp/latest/reference/) and
[NuGet](https://www.nuget.org/packages/LibTmux).

### [`LibTmux.FSharp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.FSharp): F# helpers over the core

Curried task helpers, ordinary `seq` queries, and typed portable filters over
the core handles.

Reference: [libtmux.org](https://libtmux.org/en/fsharp/latest/reference/),
[guides](https://libtmux.org/en/fsharp/latest/guides/), and
[NuGet](https://www.nuget.org/packages/LibTmux.FSharp).

### [`LibTmux.Query.Json`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Query.Json): query documents as JSON

A filter written in one process can be evaluated in another; the core does not
reference `System.Text.Json`.

Reference: [`QueryJson`][api-query-json-queryjson] and
[NuGet](https://www.nuget.org/packages/LibTmux.Query.Json).

### [`LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing): a tmux server for each test

Scoped servers, sessions, and windows that clean up after themselves.

Reference: [`TmuxTestFactory`][api-testing-tmuxtestfactory], the
[testing guide][api-guide-testing], and
[NuGet](https://www.nuget.org/packages/LibTmux.Testing).

### [`LibTmux.Extensions.DependencyInjection`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection): a Server for dependency injection

`AddLibTmux` registers a [`Server`][api-server] as a singleton and binds
[`ServerConnectionOptions`][api-serverconnectionoptions] through `IOptions<T>`.

Reference: [`Server`][api-server], the
[package README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection#registering),
and
[NuGet](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection).

### [`LibTmux.Workspace`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace): sessions from tmuxp files

Parses [tmuxp](https://github.com/tmux-python/tmuxp) YAML or JSON and builds
sessions, in one call or from a plan you review first.

Reference: [`WorkspaceBuilder`][api-ws-workspace-workspacebuilder], the
[reference index][api-ws-reference], and
[NuGet](https://www.nuget.org/packages/LibTmux.Workspace).

### [`LibTmux.Workspace.Cli`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli): the `tmux-workspace` command

Loads, captures, and converts tmuxp workspace files from a shell.

Reference: the [command reference][api-workspace-cli] and
[NuGet](https://www.nuget.org/packages/LibTmux.Workspace.Cli).

### [`LibTmux.Mcp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Mcp): a tmux server for assistants

A
[Model Context Protocol](https://modelcontextprotocol.io/specification/2025-06-18)
server that gives an assistant hands on tmux; it installs as a .NET tool.

Reference: the [MCP guide][api-mcp-guide], the [tool reference][api-mcp-tools],
and [NuGet](https://www.nuget.org/packages/LibTmux.Mcp).

## What it leaves out

- **Live objects.** A handle holds what tmux said when you asked.
  [`Pane.RefreshAsync`][api-pane-refreshasync] asks again.
- **Synchronous calls.** Every call that reaches tmux is asynchronous.
- **Silent fallbacks.** Declarative filtering throws on an expression it cannot
  translate. A flag the running tmux lacks is left off, and a warning names it.
- **A logging or telemetry implementation.** The one dependency is the logging
  abstractions; [`TmuxDiagnostics`][api-tmuxdiagnostics] names the sources so
  your pipeline subscribes.
- **YAML, JSON, and MCP.** Workspace files, query documents on the wire, and the
  MCP server are separate packages, so a caller who does not want them never
  sees their dependencies.
- **Native Windows.** [`PsmuxServer`][api-psmuxserver] and its siblings read one
  [psmux](https://github.com/psmux/psmux) session and cannot create or change
  anything; see
  [the preview contract](https://github.com/libtmux/libtmux-dotnet/blob/master/docs/psmux.md).

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **tmux.** 3.3a to 3.8, proven against every supported version on each commit.
- **.NET.** net8.0 and net10.0, with one dependency:
  `Microsoft.Extensions.Logging.Abstractions`.
- **Systems.** Linux, including WSL. CI also runs on macOS arm64, and builds on
  Windows x64, where [`Server`][api-server], [`Session`][api-session],
  [`Window`][api-window], and [`Pane`][api-pane] are annotated unsupported.
- **Trimming and NativeAOT.** Core APIs are analyzer-gated.
  [`Compile`][api-query-queryextensions-compile] and
  [`Matching`][api-query-queryextensions-matching] resolve properties by name,
  so they warn trimmed callers to preserve the filtered types' public
  properties.

## Documentation

- [The full contracts](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/docs/contracts.md):
  connect order, snapshot rules, run cleanup, query planning, retries, and
  thread safety
- [API reference][api-reference] and [guides][api-guide-getting-started] on
  libtmux.org
- [Source, docs and issues](https://github.com/libtmux/libtmux-dotnet) and the
  [CHANGELOG](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[pkg-query-json]: https://www.nuget.org/packages/LibTmux.Query.Json
[pkg-testing]: https://www.nuget.org/packages/LibTmux.Testing

[api-client]: https://libtmux.org/en/csharp/latest/reference/libtmux-client/
[api-client-iscontrolclient]: https://libtmux.org/en/csharp/latest/reference/libtmux-client-iscontrolclient/
[api-guide-getting-started]: https://libtmux.org/en/csharp/latest/guides/getting-started/
[api-guide-testing]: https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/
[api-icontrolmodesession-sendasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-icontrolmodesession-sendasync/
[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-inconsistentsnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-inconsistentsnapshotexception/
[api-libtmuxexception-dispatch]: https://libtmux.org/en/csharp/latest/reference/libtmux-libtmuxexception-dispatch/
[api-mcp-guide]: https://libtmux.org/en/csharp/latest/mcp/
[api-mcp-tools]: https://libtmux.org/en/csharp/latest/mcp/tools/
[api-pane]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane/
[api-pane-refreshasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-refreshasync/
[api-pane-runasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-runasync/
[api-pane-sendtextasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-sendtextasync/
[api-pane-splitasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-splitasync/
[api-pane-waitfortextasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-waitfortextasync/
[api-panerunresult]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult/
[api-panerunresult-anchorlost]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult-anchorlost/
[api-panerunresult-linesmissed]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult-linesmissed/
[api-panerunresult-output]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult-output/
[api-psmuxserver]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxserver/
[api-query-json-queryjson]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-query-queryextensions-translate]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-translate/
[api-query-queryfieldcatalog]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryfieldcatalog/
[api-query-queryplan]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryplan/
[api-query-querypushdown-auto]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-auto/
[api-query-querypushdown-never]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-never/
[api-query-querypushdown-require]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-require/
[api-reference]: https://libtmux.org/en/csharp/latest/reference/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-server-capturesnapshotasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-capturesnapshotasync/
[api-server-chain]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-chain/
[api-server-connectasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-connectasync/
[api-server-createownedasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-createownedasync/
[api-server-entercontrolmodeasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-entercontrolmodeasync/
[api-server-fromenvironment]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-fromenvironment/
[api-server-getsessionsasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getsessionsasync/
[api-server-getwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getwindowasync/
[api-server-inspectasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-inspectasync/
[api-server-isaliveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-isaliveasync/
[api-server-open]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-open/
[api-serverconnectionoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions/
[api-serverconnectionoptions-interceptor]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions-interceptor/
[api-serverconnectionoptions-logger]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions-logger/
[api-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-session/
[api-session-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activepane/
[api-session-activewindow]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activewindow/
[api-session-createwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-createwindowasync/
[api-session-findwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-findwindowasync/
[api-session-getwindowsasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-getwindowsasync/
[api-session-name]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-name/
[api-snapshotdepth]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotdepth/
[api-snapshotmetadata]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotmetadata/
[api-staleservergenerationexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-staleservergenerationexception/
[api-testing-tmuxtestfactory]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/
[api-tmuxchain]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxchain/
[api-tmuxcommandexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxcommandexception/
[api-tmuxdiagnostics]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxdiagnostics/
[api-tmuxdispatchstate-dispatched]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxdispatchstate-dispatched/
[api-tmuxdispatchstate-notdispatched]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxdispatchstate-notdispatched/
[api-tmuxdispatchstate-unknown]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxdispatchstate-unknown/
[api-tmuxhooks]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxhooks/
[api-tmuxobjectnotfoundexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxobjectnotfoundexception/
[api-tmuxoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxoptions/
[api-tmuxwait-untilasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxwait-untilasync/
[api-unsafetmuxfilter]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsafetmuxfilter/
[api-unsupportedqueryexpressionexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsupportedqueryexpressionexception/
[api-window]: https://libtmux.org/en/csharp/latest/reference/libtmux-window/
[api-window-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-activepane/
[api-window-getpanesasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-getpanesasync/
[api-window-linkasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-linkasync/
[api-window-moveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-moveasync/
[api-window-renameasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-renameasync/
[api-window-unlinkasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-unlinkasync/
[api-workspace-cli]: https://libtmux.org/en/csharp/latest/workspace/cli/
[api-ws-reference]: https://libtmux.org/en/csharp/latest/workspace/reference/
[api-ws-workspace-workspacebuilder]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder/
