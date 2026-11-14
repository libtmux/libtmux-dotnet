<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for C#">
  </picture>
</div>
<!-- /libtmux-logo -->

# libtmux for C#

[![dotnet](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![LibTmux](https://img.shields.io/nuget/v/LibTmux?label=LibTmux)](https://www.nuget.org/packages/LibTmux)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/)
[![license](https://img.shields.io/badge/license-MIT-blue)](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

Drive [tmux](https://github.com/tmux/tmux) from C#: [servers][api-server],
[sessions][api-session], [windows][api-window], [panes][api-pane],
[clients][api-client], [options][api-tmuxoptions], [hooks][api-tmuxhooks] and
buffers, typed and asynchronous, for tmux **3.3a and newer** on **net8.0** and
**net10.0**.

[Run a command][api-pane-runasync] and read its exit status,
[find][api-session-getwindowsasync] and
[filter][api-query-queryextensions-matching] what a server holds,
[type into a pane and wait][api-tmuxwait-untilasync] for the answer, and choose
[how a call reaches tmux][api-server-entercontrolmodeasync].

Add it to your project:

```console
$ dotnet add package LibTmux
```

**Contents** — [Quick start](#quick-start) · [Is this for you?](#is-this-for-you) ·
[Packages](#packages) · [Find things](#find-sessions-windows-and-panes) ·
[Run a command](#run-a-command-and-read-what-it-printed) ·
[Filter](#filter-what-you-read) · [Options](#change-options-and-hooks) ·
[Versions](#check-the-tmux-version) · [Choose a path](#choose-how-to-reach-tmux)
· [Testing](#testing-your-own-code) ·
[Workspaces](#workspaces-from-the-command-line) ·
[Assistants](#an-assistant-on-your-terminal) · [Leaves out](#what-it-leaves-out)
· [Support](#support-and-status)

## Quick start

Start a tmux server of your own, make a session, a window, and a pane, and type
into the pane:

<!-- snippet: ConnectAndBuild usings: LibTmux -->
```csharp
using LibTmux;

// A tmux server of its own on the socket "quickstart". Disposing the
// scope stops it, so nothing is left running.
await using OwnedServerScope owned = await Server.CreateOwnedAsync(
    new ServerConnectionOptions { SocketName = "quickstart" });
Server server = owned.Value;
Session session = await server.CreateSessionAsync(new NewSessionRequest { Name = "build" });
Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "tests" });
Pane pane = (await window.GetPanesAsync())[0];

await pane.SendTextAsync("dotnet test");
Console.WriteLine($"{session.Name}:{window.Name}");
```
<!-- endsnippet -->

Make a console project, then paste the program into `Program.cs`:

```console
$ dotnet new console --output demo && \
    cd demo && \
    dotnet add package LibTmux
```

Run it with tmux 3.3a or newer on `PATH`:

```console
$ dotnet run
```

It prints `build:tests`. [`Server.CreateOwnedAsync`][api-server-createownedasync]
starts the server on its own socket and stops it when the scope is disposed;
[`Server.ConnectAsync`][api-server-connectasync] attaches to one that is already
running.

The `csharp run` blocks below continue from this program: `server`, `session`,
`window`, and `pane` are its variables, and `ct` is a `CancellationToken`.

## Is this for you?

**Yes, if you want to** drive a real terminal from code: build a dev
environment, script a workspace, harness a TUI in tests, or give an assistant
hands on a terminal.

**Look elsewhere if you want** a terminal emulator (this drives tmux and draws
nothing), full Windows support, or a process launcher: `Process.Start` is right
there. Native Windows has a bounded query-only
[`Psmux*` preview](docs/psmux.md).

**What you get over a shell wrapper:** typed entities with real IDs, one
dispatch model per workload, and a version model that names the flag a tmux
lacks.

## Packages

| Package | | Add it when |
|---|---|---|
| **[LibTmux](src/LibTmux/README.md)** | [![LibTmux](https://img.shields.io/nuget/v/LibTmux?label=LibTmux)](https://www.nuget.org/packages/LibTmux) | Always. The client. One dependency: logging abstractions. |
| **[LibTmux.FSharp](src/LibTmux.FSharp/README.md)** | [![LibTmux.FSharp](https://img.shields.io/nuget/v/LibTmux.FSharp?label=LibTmux.FSharp)](https://www.nuget.org/packages/LibTmux.FSharp) | You write F# and want curried task helpers, `seq` queries, and typed portable filters over the core values. |
| **[LibTmux.Query.Json](src/LibTmux.Query.Json/README.md)** | [![LibTmux.Query.Json](https://img.shields.io/nuget/v/LibTmux.Query.Json?label=LibTmux.Query.Json)](https://www.nuget.org/packages/LibTmux.Query.Json) | You send queries between processes and want them as JSON. |
| **[LibTmux.Testing](src/LibTmux.Testing/README.md)** | [![LibTmux.Testing](https://img.shields.io/nuget/v/LibTmux.Testing?label=LibTmux.Testing)](https://www.nuget.org/packages/LibTmux.Testing) | You test code that drives tmux and want scopes that clean up after themselves. |
| **[LibTmux.Extensions.DependencyInjection](src/LibTmux.Extensions.DependencyInjection/README.md)** | [![LibTmux.Extensions.DependencyInjection](https://img.shields.io/nuget/v/LibTmux.Extensions.DependencyInjection?label=LibTmux.Extensions.DependencyInjection)](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection) | Your application composes services and wants a tmux handle injected. |
| **[LibTmux.Workspace](src/LibTmux.Workspace/README.md)** | [![LibTmux.Workspace](https://img.shields.io/nuget/v/LibTmux.Workspace?label=LibTmux.Workspace)](https://www.nuget.org/packages/LibTmux.Workspace) | You have [tmuxp](https://github.com/tmux-python/tmuxp) YAML to build from. |
| **[LibTmux.Workspace.Cli](src/LibTmux.Workspace.Cli/README.md)** | [![LibTmux.Workspace.Cli](https://img.shields.io/nuget/v/LibTmux.Workspace.Cli?label=LibTmux.Workspace.Cli)](https://www.nuget.org/packages/LibTmux.Workspace.Cli) | You want `tmux-workspace`: load, freeze and convert tmuxp files from a shell. Installs as a tool. |
| **[LibTmux.Mcp](src/LibTmux.Mcp/README.md)** | [![LibTmux.Mcp](https://img.shields.io/nuget/v/LibTmux.Mcp?label=LibTmux.Mcp)](https://www.nuget.org/packages/LibTmux.Mcp) | You want an assistant driving tmux. Installs as a tool. |

```console
$ dotnet add package LibTmux
```

- **One dependency.** The core takes the logging abstractions. YAML, JSON and
  MCP ship as their own packages.
- **One version.** Every package carries it, so a `LibTmux.Workspace` goes with
  the `LibTmux` of the same version.
- **F#.** `LibTmux.FSharp` keeps the core handles and adds curried task
  helpers, `seq` queries and portable filters. Start with its
  [quick start](src/LibTmux.FSharp/README.md#quick-start).

## Find sessions, windows, and panes

### [`Session.GetWindowsAsync`][api-session-getwindowsasync]: windows and panes

One call reads a session's windows, and
[`Window.GetPanesAsync`][api-window-getpanesasync] reads a window's panes. They
return `IReadOnlyList<T>` and run no tmux command while you enumerate them:

```csharp run
using LibTmux;

foreach (Window each in await session.GetWindowsAsync(ct))
{
    foreach (Pane every in await each.GetPanesAsync(ct))
    {
        Console.WriteLine($"{each.Name} pane {every.Index} {every.Width}x{every.Height}");
    }
}
```

It prints one line per pane: the window name, the pane index, and its size.

### [`Window.RenameAsync`][api-window-renameasync]: a changed copy

A handle holds what tmux said when it was read. An operation that changes an
object returns a replacement, and [`Pane.RefreshAsync`][api-pane-refreshasync]
asks tmux again:

```csharp run
Window renamed = await window.RenameAsync("integration", ct);
Console.WriteLine($"{window.Name} -> {renamed.Name}");
```

It prints the old name and the new one.

A replacement is still the same window: identity is the server generation plus
the typed ID, not the name.

```csharp run
Window sameWindow = await window.RenameAsync("integration", ct);
Console.WriteLine($"{window == sameWindow} {window.Name} {sameWindow.Name}");

// Identifiers order the way tmux issued them, oldest first.
IEnumerable<Window> oldest = (await session.GetWindowsAsync(ct)).OrderBy(each => each.Id);
Console.WriteLine(string.Join(", ", oldest.Select(each => each.Id)));
```

It prints `True`, both names, and the window IDs in the order tmux issued them.

## Run a command and read what it printed

### [`Pane.RunAsync`][api-pane-runasync]: a command's exit status

It runs a shell command in a pane and returns its real exit status and the lines
it printed. A command that exits nonzero returns normally with that status:

```csharp run tmux-3.4
using LibTmux;

PaneRunResult result = await pane.RunAsync("echo hello; exit 3", TimeSpan.FromSeconds(10), ct);
if (result.ExitStatus != 3 || !result.Output.Contains("hello"))
{
    throw new InvalidOperationException("Expected exit status 3 and the line hello.");
}
```

It prints nothing and throws if the status or the line is wrong.

### [`TmuxWait.UntilAsync`][api-tmuxwait-untilasync]: wait for what a command prints

For input a shell command cannot carry, type it and wait. tmux accepts a command
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

It returns once the pane shows the line.

## Filter what you read

### Plain LINQ

To filter what you already read, use LINQ:

```csharp run
using LibTmux;

IReadOnlyList<Window> windows = await session.GetWindowsAsync(ct);
IEnumerable<Window> building = windows.Where(
    each => each.Name.StartsWith("build", StringComparison.Ordinal));
```

### [`QueryExtensions.Matching`][api-query-queryextensions-matching]: declarative filters over what you hold

It turns an expression into a portable document, or throws; it never quietly
falls back to filtering in memory:

```csharp run
using LibTmux;
using LibTmux.Query;

IReadOnlyList<Session> sessions = await server.GetSessionsAsync(ct);
IReadOnlyList<Session> building = sessions.Matching<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal));
```

### [`QueryExtensions.Translate`][api-query-queryextensions-translate]: the same filter as a document

The expression is also a document, written here and answered somewhere else.
[LibTmux.Query.Json](src/LibTmux.Query.Json/README.md) puts it on the wire:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build", StringComparison.Ordinal)
        && session.Attached);

Console.WriteLine(document.Target);   // Session
```

It prints the document's target, [`Session`][api-session].

- The document uses stable wire names: [`Session.Name`][api-session-name] is
  `session_name` and [`Client.IsControlClient`][api-client-iscontrolclient] is
  `client_control_mode`. The catalog carries all twelve queryable fields.
- A field outside the catalog throws. [`UnsafeTmuxFilter`][api-unsafetmuxfilter]
  is the separate opt-in for native tmux `-f` filters.

### [`QueryPlan<T>`][api-query-queryplan]: what runs in tmux, what runs here

[`QueryExtensions.Plan`][api-query-queryextensions-plan] splits a filter against
the tmux version [`Server.InspectAsync`][api-server-inspectasync] reports: an
exact leading part runs inside tmux and the rest runs locally. The plan says
which, and it runs when you ask:

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

- [`QueryPushdown.Auto`][api-query-querypushdown-auto] splits the filter,
  [`Never`][api-query-querypushdown-never] keeps all of it local, and
  [`Require`][api-query-querypushdown-require] rejects one that cannot run
  entirely in tmux.
- More: [query planning](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/docs/contracts.md#filtering).

## Change options and hooks

### [`TmuxOptions`][api-tmuxoptions] and [`TmuxHooks`][api-tmuxhooks]: settings and callbacks

Every object reaches the option table tmux keeps for it. tmux has no types, so
a value carries the text it reported alongside the readings that text supports:

```csharp run
using LibTmux;

await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"), ct);
TmuxOption option = (await window.Options.GetAsync(
    new GetOptionRequest("automatic-rename"), ct))[0];

Console.WriteLine($"{option.Value.Raw} flag={option.Value.Boolean} number={option.Value.Integer}");
```

It prints the raw value, its flag reading, and its number reading.

An option the window does not hold reads as the inherited value. Hooks are
arrays even with one entry:

```csharp run
TmuxHook hook = await server.Hooks.SetAsync(
    new SetHookRequest("alert-bell", "set-option -g @rang yes"), ct);
Console.WriteLine(hook.Values[0].Command);
```

## Check the tmux version

### [`TmuxVersionTooLowException`][api-tmuxversiontoolowexception]: a command the running tmux lacks

A flag a tmux lacks is left off and a warning names it. A command it lacks
sends nothing, and this exception names the version that would be needed:

```csharp run
using LibTmux;

// A handle says what it read: the version is what tmux reported when this
// server was reached, and null when it reported something unparsable.
TmuxVersion? version = server.Version;
Console.WriteLine($"tmux {version?.Raw} 3.4-or-newer={version?.IsAtLeast(TmuxVersion.Parse("3.4"))}");
```

It prints the version tmux reported and whether it is 3.4 or newer.

- A tmux below 3.3a is refused the same way. Upgrade tmux and restart its
  server.
- [`TmuxUnsupportedOnReleaseException`][api-tmuxunsupportedonreleaseexception]
  covers a command a release mishandles or rejects, and
  `TmuxVersionTooLowException` derives from it. Both carry `ActualVersion`,
  `SafeReleases`, the `CapabilityId` from [`capabilities.json`](capabilities.json)
  and the refused `Command`.
- Every difference between 3.3a and 3.8 is
  [recorded with the test that proves it](docs/parity/version-deltas.json).
- The library starts the `tmux` on `PATH`;
  [`ServerConnectionOptions.TmuxBinaryPath`][api-serverconnectionoptions] picks
  another binary.

## Choose how to reach tmux

Which one a call uses is visible where the call starts. One-shot calls and
chains work on every supported tmux; explicit control mode needs tmux 3.7+.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| **[One-shot](docs/modes/one-shot.md)** | [`session.CreateWindowAsync(…)`][api-session-createwindowasync] | one command, awaited | another round trip on a shared client; a process for the few commands that cannot use it |
| **[Control](docs/modes/control-mode.md)** | [`server.EnterControlModeAsync(ct)`][api-server-entercontrolmodeasync] | one client, streamed | another round trip — **~0.2 ms** |
| **[Chained](docs/modes/chaining.md)** | [`server.Chain()…ExecuteAsync(ct)`][api-server-chain] | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

Each figure is the marginal cost of one more command. Timings move with the
machine: the [recorded run](docs/benchmarks/README.md) has the distribution and
the host, and [Choosing a mode](docs/modes/matrix.md) has allocations.

### [`Session.CreateWindowAsync`][api-session-createwindowasync]: one command, a typed object back

```csharp run
using LibTmux;

// One-shot: a command, a typed object back.
Window built = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
Console.WriteLine(built.Name);
```

### [`Server.EnterControlModeAsync`][api-server-entercontrolmodeasync]: one client, held open

```csharp run tmux-3.7
// Control mode: one client, held open, streaming what tmux does.
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> reply = await control.SendAsync(
    TmuxCommand.Create("new-window", "-d", "-n", "build"),
    ct);
```

### [`IControlModeSession.Events`][api-icontrolmodesession-events]: what tmux says unasked

Send a command, then read the notification it causes. The same stream reports
pane output and the end of the session. More:
[Control mode](docs/modes/control-mode.md).

<!-- snippet: WatchForWindowAdd usings: LibTmux -->
```csharp
using LibTmux;

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

It prints `window-add` and the new window's ID.

### [`Server.Chain`][api-server-chain]: many commands, one invocation

```csharp run
// Chained: many commands, one invocation.
await server.Chain()
    .Then("new-window", "-d", "-n", "build")
    .Then("new-window", "-d", "-n", "test")
    .ExecuteAsync(ct);
```

## Testing your own code

[`LibTmux.Testing`][pkg-testing] gives a test a tmux server of its own, on its
own socket, killed deterministically:

```csharp
using LibTmux.Testing;

TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
```

Disposal attempts to stop the owned server even when the test body throws.
[`TmuxWait.UntilAsync`][api-tmuxwait-untilasync], in the client package,
waits for a state that production code also needs when it reads back what a
command produced.

## Workspaces from the command line

[LibTmux.Workspace.Cli](src/LibTmux.Workspace.Cli/README.md) installs
[`tmux-workspace`][api-workspace-cli], which loads, captures and converts
[tmuxp](https://github.com/tmux-python/tmuxp) workspace files:

```console
$ dotnet tool install --global LibTmux.Workspace.Cli
```

With a workspace file saved as `~/.tmuxp/myproject.yaml`, such as the
[example in the package README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli#tmux-workspace),
build it without attaching, on the named tmux socket `demo`:

```console
$ tmux-workspace load -d -L demo myproject
```

List the workspaces it can find:

```console
$ tmux-workspace ls
```

## An assistant on your terminal

[LibTmux.Mcp](src/LibTmux.Mcp/README.md) is a
[Model Context Protocol](https://modelcontextprotocol.io/specification/2025-06-18)
server, installed as a .NET tool:

```console
$ dotnet tool install --global LibTmux.Mcp
```

```json
{ "mcpServers": { "tmux": { "command": "libtmux-mcp" } } }
```

- Tools come in `inspect`, `manage`, `execute`, and `teardown` sets, and
  teardown is off by default.
- [`run_shell_command`](https://libtmux.org/en/csharp/latest/mcp/tools/run_shell_command/)
  returns the shell's real exit status.
- [`wait_for_text`](https://libtmux.org/en/csharp/latest/mcp/tools/wait_for_text/)
  wakes from tmux's control-mode stream on tmux 3.7 and newer, and polls on
  older releases.
- [`capture_pane`](https://libtmux.org/en/csharp/latest/mcp/tools/capture_pane/)
  reads visible text and scrollback;
  [`snapshot_pane`](https://libtmux.org/en/csharp/latest/mcp/tools/snapshot_pane/),
  [`search_panes`](https://libtmux.org/en/csharp/latest/mcp/tools/search_panes/),
  and
  [`capture_since`](https://libtmux.org/en/csharp/latest/mcp/tools/capture_since/)
  give structured and incremental reads. No tool returns unbounded output.
- Pane modes belong to attached humans: the server never enters one, and input
  refuses a pane that is in one.
- With no socket pinned, tmux picks the server: the one in `TMUX`, otherwise its
  default socket. `LIBTMUX_SOCKET` or `LIBTMUX_SOCKET_PATH` pins one.
- More: [every tool](docs/mcp/tools.md), generated from the server itself, and
  the [full instructions](src/LibTmux.Mcp/README.md).

A client starts the server itself and speaks to it over standard input and
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

## Package directories

### [`LibTmux`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux): the client

Servers, sessions, windows, panes, and buffers, typed and asynchronous, with one
dependency: the logging abstractions.

Reference: [libtmux.org](https://libtmux.org/en/csharp/latest/reference/) and
[NuGet](https://www.nuget.org/packages/LibTmux).
Changelog: [`CHANGELOG.md`](src/LibTmux/CHANGELOG.md).

### [`LibTmux.FSharp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.FSharp): F# helpers over the core

Curried task helpers, `seq` queries, and typed portable filters over
the core handles. Start with its
[quickstart](src/LibTmux.FSharp/README.md#quick-start).

Reference: [libtmux.org](https://libtmux.org/en/fsharp/latest/reference/),
[guides](https://libtmux.org/en/fsharp/latest/guides/), and
[NuGet](https://www.nuget.org/packages/LibTmux.FSharp).
Changelog: [`CHANGELOG.md`](src/LibTmux.FSharp/CHANGELOG.md).

### [`LibTmux.Query.Json`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Query.Json): query documents as JSON

A filter written in one process can be evaluated in another; the core does not
reference `System.Text.Json`.

Reference: [`QueryJson`][api-query-json-queryjson] and
[NuGet](https://www.nuget.org/packages/LibTmux.Query.Json).
Changelog: [`CHANGELOG.md`](src/LibTmux.Query.Json/CHANGELOG.md).

### [`LibTmux.Testing`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Testing): a tmux server for each test

Scoped servers, sessions, and windows that clean up after themselves.

Reference: [`TmuxTestFactory`][api-testing-tmuxtestfactory], the
[testing guide][api-guide-testing], and
[NuGet](https://www.nuget.org/packages/LibTmux.Testing).
Changelog: [`CHANGELOG.md`](src/LibTmux.Testing/CHANGELOG.md).

### [`LibTmux.Extensions.DependencyInjection`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection): a Server for dependency injection

`AddLibTmux` registers a [`Server`][api-server] as a singleton and binds
[`ServerConnectionOptions`][api-serverconnectionoptions] through `IOptions<T>`.

Reference: [`Server`][api-server], the
[package README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Extensions.DependencyInjection#registering),
and
[NuGet](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection).
Changelog: [`CHANGELOG.md`](src/LibTmux.Extensions.DependencyInjection/CHANGELOG.md).

### [`LibTmux.Workspace`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace): sessions from tmuxp files

Parses [tmuxp](https://github.com/tmux-python/tmuxp) YAML or JSON and builds
sessions, in one call or from a plan you review first.

Reference: [`WorkspaceBuilder`][api-ws-workspace-workspacebuilder], the
[reference index][api-ws-reference], and
[NuGet](https://www.nuget.org/packages/LibTmux.Workspace).
Changelog: [`CHANGELOG.md`](src/LibTmux.Workspace/CHANGELOG.md).

### [`LibTmux.Workspace.Cli`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli): the `tmux-workspace` command

Loads, captures, and converts tmuxp workspace files from a shell.

Reference: the [command reference][api-workspace-cli] and
[NuGet](https://www.nuget.org/packages/LibTmux.Workspace.Cli).
Changelog: [`CHANGELOG.md`](src/LibTmux.Workspace.Cli/CHANGELOG.md).

### [`LibTmux.Mcp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Mcp): a tmux server for assistants

A
[Model Context Protocol](https://modelcontextprotocol.io/specification/2025-06-18)
server that gives an assistant hands on tmux; it installs as a .NET tool.

Reference: the [MCP guide][api-mcp-guide], the [tool reference][api-mcp-tools],
and [NuGet](https://www.nuget.org/packages/LibTmux.Mcp).
Changelog: [`CHANGELOG.md`](src/LibTmux.Mcp/CHANGELOG.md).

## What it leaves out

- **Live objects.** A handle holds what tmux said when you asked.
  [`Pane.RefreshAsync`][api-pane-refreshasync] asks again.
- **Synchronous calls.** Every call that reaches tmux is asynchronous.
- **Silent fallbacks.** Declarative filtering throws on an expression it cannot
  translate. A flag the running tmux lacks is left off, and a warning names it.
- **YAML, JSON, and MCP in the core.** Workspace files, query documents on the
  wire, and the MCP server are separate packages, so a caller who does not want
  them never sees their dependencies.
- **A terminal emulator.** This drives tmux and draws nothing.
- **Native Windows.** [`PsmuxServer`][api-psmuxserver] and its siblings read one
  [psmux](https://github.com/psmux/psmux) session and cannot create or change
  anything. See [the preview](docs/psmux.md).

## Support and status

- **Pre-1.0.** Any release may change or remove an exported identifier. Each
  package's changelog marks its breaks, so pin an exact version.
- **tmux.** 3.3a or newer, tested through 3.8. A release candidate is not
  supported: see
  [Release candidates](https://libtmux.org/en/tmux/guides/tmux-support/#release-candidates).
  `TmuxVersion` orders `X.Y-rcN` below `X.Y` and promises nothing more.
- **.NET.** net8.0 and net10.0.
- **Systems.** Linux, including WSL. macOS is best effort. Native Windows has
  only the read-only [`Psmux*` preview](docs/psmux.md), also best effort.
- **Trimming and NativeAOT.** The `LibTmux` core is analyzed for both.
  [`Compile`][api-query-queryextensions-compile] and
  [`Matching`][api-query-queryextensions-matching] resolve properties by name, so
  they warn trimmed callers to preserve the filtered types' public properties.

## Documentation

- [Guides on libtmux.org](https://libtmux.org/en/csharp/latest/) — tutorials,
  topics and the workspace command reference
- [Choosing a mode](docs/modes/matrix.md) — the three dispatch modes, measured
- [Windows psmux preview](docs/psmux.md) — what it reads, and what it refuses
- [API reference](docs/api/README.md) — rendered from the doc comments
- [tmux MCP tools](docs/mcp/tools.md) — every tool, capability row and resource,
  generated
- [API policy](docs/public-api.md) — ownership and parity rules
- [Optional tmux features](capabilities.json) — each feature the library gates,
  the release it starts at, and the error it raises; summarized in
  [Choosing a mode](docs/modes/matrix.md)
- [Version deltas](docs/parity/version-deltas.json) — every tmux difference,
  with the test that proves it
- [Decisions](docs/decisions/) — why the transport, object model and query
  catalog are shaped this way
- [Examples](examples/README.md) — the programs these snippets come from
- [Changelog](CHANGELOG.md) — changes that span packages, with a link to the
  changelog of each package
- [AGENTS.md](AGENTS.md) — how to work in this repository

## AI policy

Use these packages with AI tools as you like. When contributing, check what you
submit and wait for a maintainer's invitation before preparing a pull request.
See the [AI policy](https://libtmux.org/en/meta/ai-policy/), also kept as
[AI_POLICY.md](https://github.com/libtmux/.github/blob/HEAD/AI_POLICY.md).

## Attribution

Please use the following BibTeX template to cite libtmux-dotnet in scientific
discourse:

```bibtex
@misc{libtmux-dotnet,
   author = {Tony Narlock},
   year = {2026},
   note = {https://libtmux.org/en/csharp/},
   title = {libtmux-dotnet: C\# wrapper for tmux}
}
```

## License

[MIT](LICENSE). Practical parity with Python
[libtmux](https://github.com/tmux-python/libtmux), rewritten in C#.

[pkg-testing]: https://www.nuget.org/packages/LibTmux.Testing

[api-client]: https://libtmux.org/en/csharp/latest/reference/libtmux-client/
[api-client-iscontrolclient]: https://libtmux.org/en/csharp/latest/reference/libtmux-client-iscontrolclient/
[api-guide-testing]: https://libtmux.org/en/csharp/latest/guides/testing-with-libtmux/
[api-icontrolmodesession-events]: https://libtmux.org/en/csharp/latest/reference/libtmux-icontrolmodesession-events/
[api-mcp-guide]: https://libtmux.org/en/csharp/latest/mcp/
[api-mcp-tools]: https://libtmux.org/en/csharp/latest/mcp/tools/
[api-pane]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane/
[api-pane-refreshasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-refreshasync/
[api-pane-runasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-runasync/
[api-psmuxserver]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxserver/
[api-query-json-queryjson]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-query-queryextensions-plan]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-plan/
[api-query-queryextensions-translate]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-translate/
[api-query-queryplan]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryplan/
[api-query-querypushdown-auto]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-auto/
[api-query-querypushdown-never]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-never/
[api-query-querypushdown-require]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querypushdown-require/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-server-chain]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-chain/
[api-server-connectasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-connectasync/
[api-server-createownedasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-createownedasync/
[api-server-entercontrolmodeasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-entercontrolmodeasync/
[api-server-inspectasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-inspectasync/
[api-serverconnectionoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-serverconnectionoptions/
[api-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-session/
[api-session-createwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-createwindowasync/
[api-session-getwindowsasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-getwindowsasync/
[api-session-name]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-name/
[api-testing-tmuxtestfactory]: https://libtmux.org/en/csharp/latest/reference/libtmux-testing-tmuxtestfactory/
[api-tmuxhooks]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxhooks/
[api-tmuxoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxoptions/
[api-tmuxunsupportedonreleaseexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxunsupportedonreleaseexception/
[api-tmuxversiontoolowexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxversiontoolowexception/
[api-tmuxwait-untilasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxwait-untilasync/
[api-unsafetmuxfilter]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsafetmuxfilter/
[api-window]: https://libtmux.org/en/csharp/latest/reference/libtmux-window/
[api-window-getpanesasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-getpanesasync/
[api-window-renameasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-renameasync/
[api-workspace-cli]: https://libtmux.org/en/csharp/latest/workspace/cli/
[api-ws-reference]: https://libtmux.org/en/csharp/latest/workspace/reference/
[api-ws-workspace-workspacebuilder]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder/
