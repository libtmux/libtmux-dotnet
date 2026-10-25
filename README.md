<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for C#">
  </picture>
</div>
<!-- /libtmux-logo -->

# libtmux for C#

[![LibTmux](https://img.shields.io/nuget/vpre/LibTmux?logo=nuget&label=LibTmux)](https://www.nuget.org/packages/LibTmux)
[![downloads](https://img.shields.io/nuget/dt/LibTmux?logo=nuget&label=downloads)](https://www.nuget.org/packages/LibTmux)
[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux 3.3a – 3.8](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

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
$ dotnet package add LibTmux --prerelease
```

> **Alpha.** Releases carry an `-alpha` prerelease tag, and any release may
> change or remove exported identifiers without a deprecation period. Pin an
> exact version. It is not recommended for production.

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
    dotnet package add LibTmux --prerelease
```

It prints nothing. The new session `build` has a window `tests`, and its pane
has received `dotnet test`. With no server running, call
[`Server.CreateOwnedAsync`][api-server-createownedasync]; it starts one and
stops it when disposed.

Every `csharp run` block below runs against a tmux server of its own, with
`server`, `session`, `window`, `pane`, and `ct` already in hand.

## Is this for you?

**Yes, if you want to** drive a real terminal from code: build a dev
environment, script a workspace, harness a TUI in tests, or give an assistant
hands on a terminal.

**Look elsewhere if you want** a terminal emulator (this drives tmux and draws
nothing), full Windows support, or a process launcher: `Process.Start` is right
there. Native Windows has a bounded query-only
[`Psmux*` preview](docs/psmux.md).

**What you get over a shell wrapper:** typed entities with real IDs, one
dispatch model per workload, a version model that names the flag a tmux lacks,
and examples that run against live tmux in CI.

## Packages

| Package | | Add it when |
|---|---|---|
| **[LibTmux](src/LibTmux/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux) | Always. The client. One dependency: logging abstractions. |
| **[LibTmux.FSharp](src/LibTmux.FSharp/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.FSharp?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.FSharp) | You write F# and want curried task helpers, ordinary `seq` queries, and typed portable filters over the core values. |
| **[LibTmux.Query.Json](src/LibTmux.Query.Json/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Query.Json?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Query.Json) | You send queries between processes and want them as JSON. |
| **[LibTmux.Testing](src/LibTmux.Testing/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Testing?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Testing) | You test code that drives tmux and want scopes that clean up after themselves. |
| **[LibTmux.Extensions.DependencyInjection](src/LibTmux.Extensions.DependencyInjection/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Extensions.DependencyInjection?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Extensions.DependencyInjection) | Your application composes services and wants a tmux handle injected. |
| **[LibTmux.Workspace](src/LibTmux.Workspace/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Workspace?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Workspace) | You have [tmuxp](https://github.com/tmux-python/tmuxp) YAML to build from. |
| **[LibTmux.Workspace.Cli](src/LibTmux.Workspace.Cli/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Workspace.Cli?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Workspace.Cli) | You want `tmux-workspace`: load, freeze and convert tmuxp files from a shell. Installs as a tool. |
| **[LibTmux.Mcp](src/LibTmux.Mcp/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Mcp?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Mcp) | You want an assistant driving tmux. Installs as a tool. |

```console
$ dotnet package add LibTmux --prerelease
```

The core takes exactly one dependency. Anything that would add another ships as
its own package, so a caller who does not want YAML never sees YamlDotNet.

They all carry one version, so any `LibTmux.Workspace` goes with the `LibTmux`
of the same version, without a table to consult.

`LibTmux.FSharp` is the F# companion built on
[LibTmux](https://github.com/libtmux/libtmux-dotnet/). Both packages are
maintained in the `libtmux` organization by the same primary author.

It keeps the core handles and task I/O, then adds F#-native composition for
snapshots and portable filters. Start with its
[runnable F# quickstart](src/LibTmux.FSharp/README.md#quick-start).

## Find sessions, windows, and panes

### [`Session.GetWindowsAsync`][api-session-getwindowsasync]: windows and panes

One call reads a session's windows, and
[`Window.GetPanesAsync`][api-window-getpanesasync] reads a window's panes. They
return `IReadOnlyList<T>` and run no tmux command while you enumerate them:

```csharp run
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

A replacement is still the same window. Identity is the server generation plus
the typed ID, so two handles that read different names name one window, and a
handle read from a restarted server does not, even where tmux reused the number:

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

## Change options and hooks

### [`TmuxOptions`][api-tmuxoptions] and [`TmuxHooks`][api-tmuxhooks]: settings and callbacks

Every object reaches the option table tmux keeps for it. tmux has no types, so
a value carries the text it reported alongside the readings that text supports:

```csharp run
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

Where a flag is missing, the request goes out without it and a warning names
what was left off. Where a whole command is missing, nothing is sent and this
exception names the version that would be needed:

```csharp run
// A handle says what it read: the version is what tmux reported when this
// server was reached, and null when it reported something unparsable.
TmuxVersion? version = server.Version;
Console.WriteLine($"tmux {version?.Raw} 3.4-or-newer={version?.IsAtLeast(TmuxVersion.Parse("3.4"))}");
```

It prints the version tmux reported and whether it is 3.4 or newer.

A tmux below 3.3a is refused the same way.

A command that a release mishandles or rejects raises
[`TmuxUnsupportedOnReleaseException`][api-tmuxunsupportedonreleaseexception].
`TmuxVersionTooLowException` derives from it, so catch the base type to cover
every release refusal.

It carries `ActualVersion`, `SafeReleases`, the `CapabilityId` from
[`capabilities.json`](capabilities.json) and the refused `Command`.

Upgrade tmux and restart the tmux server, or use LibTmux 0.0.0-alpha.21, the
last release that supports tmux 3.2a. Every measured difference between 3.3a and
3.8 is [recorded with the test that proves it](docs/parity/version-deltas.json).

The library starts the `tmux` found on `PATH`. To use another binary, set
[`ServerConnectionOptions.TmuxBinaryPath`][api-serverconnectionoptions]. The
`LibTmux` package never reads `LIBTMUX_TMUX`; within the shipped packages, only
`tmux-workspace` and `libtmux-mcp` do.

## Choose how to reach tmux

Which one a call uses is visible where the call starts, and all three work on
every supported tmux.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| **[One-shot](docs/modes/one-shot.md)** | [`session.CreateWindowAsync(…)`][api-session-createwindowasync] | one command, awaited | another round trip on a shared client; a process for the few commands that cannot use it |
| **[Control](docs/modes/control-mode.md)** | [`server.EnterControlModeAsync(ct)`][api-server-entercontrolmodeasync] | one client, streamed | another round trip — **~0.2 ms** |
| **[Chained](docs/modes/chaining.md)** | [`server.Chain()…ExecuteAsync(ct)`][api-server-chain] | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

Each figure is the marginal cost of one more command, as medians of 100 samples
against tmux 3.7b.

Absolute timings move with the machine, so the
[recorded run](docs/benchmarks/README.md) gives the distribution, the tmux, the
host, and the date. [Choosing a mode](docs/modes/matrix.md) has allocations and
how to rerun the table.

### [`Session.CreateWindowAsync`][api-session-createwindowasync]: one command, a typed object back

```csharp run
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

### [`Server.Chain`][api-server-chain]: many commands, one invocation

```csharp run
// Chained: many commands, one invocation.
await server.Chain()
    .Then("new-window", "-d", "-n", "build")
    .Then("new-window", "-d", "-n", "test")
    .ExecuteAsync(ct);
```

Control mode is an order of magnitude cheaper per command, because its client is
already running. A chain wins for a batch by paying one round trip for the whole
sequence.

## Testing your own code

[`LibTmux.Testing`][pkg-testing] gives a test a tmux server of its own, on its
own socket, killed deterministically:

```csharp
using LibTmux.Testing;

TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
```

Disposing kills the server, so a test that fails part way through leaves nothing
behind. [`TmuxWait.UntilAsync`][api-tmuxwait-untilasync], in the client package,
waits for a state that production code also needs when it reads back what a
command produced.

## Workspaces from the command line

[LibTmux.Workspace.Cli](src/LibTmux.Workspace.Cli/README.md) installs
[`tmux-workspace`][api-workspace-cli], which loads, captures and converts
[tmuxp](https://github.com/tmux-python/tmuxp) workspace files:

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

## An assistant on your terminal

[LibTmux.Mcp](src/LibTmux.Mcp/README.md) is a
[Model Context Protocol](https://modelcontextprotocol.io/specification/2025-06-18)
server. It is a separate package that installs as a .NET tool:

```console
$ dotnet tool install --global LibTmux.Mcp --prerelease
```

```json
{ "mcpServers": { "tmux": { "command": "libtmux-mcp" } } }
```

- It exposes its tools in `inspect`, `manage`, `execute`, and `teardown` sets,
  plus the static `tmux://capabilities` resource.
- Existing and user-configured sockets omit teardown by default.
- [The full reference](docs/mcp/tools.md) is generated from the server itself.
- Pin one socket for the process with `LIBTMUX_SOCKET` or `LIBTMUX_SOCKET_PATH`.

It is built so an assistant never gets stuck and never wastes context:

- [`run_shell_command`](https://libtmux.org/en/csharp/latest/mcp/tools/run_shell_command/)
  returns the shell's real exit status.
- [`wait_for_text`](https://libtmux.org/en/csharp/latest/mcp/tools/wait_for_text/)
  wakes from tmux's control-mode stream on tmux 3.7 and newer, and polls on
  older releases. If that stream cannot start, the wait ends unless you set
  `LIBTMUX_MCP_ALLOW_POLLING_FALLBACK=true`.
- Nothing returns unbounded output.
  [`capture_pane`](https://libtmux.org/en/csharp/latest/mcp/tools/capture_pane/)
  reads visible text and scrollback, while
  [`snapshot_pane`](https://libtmux.org/en/csharp/latest/mcp/tools/snapshot_pane/),
  [`search_panes`](https://libtmux.org/en/csharp/latest/mcp/tools/search_panes/),
  and
  [`capture_since`](https://libtmux.org/en/csharp/latest/mcp/tools/capture_since/)
  give structured and incremental reads.
- Pane modes belong to attached humans. The server never enters, drives, or
  exits one, and input refuses a pane that is in one. Wait for the person to
  leave the mode; do not cancel it.
- Toolset and tool selection is frozen at startup and reported through
  `tmux://capabilities`.

[Full instructions](src/LibTmux.Mcp/README.md).

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

## Package directories

### [`LibTmux`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux): the client

Servers, sessions, windows, panes, and buffers, typed and asynchronous, with one
dependency: the logging abstractions.

Reference: [libtmux.org](https://libtmux.org/en/csharp/latest/reference/) and
[NuGet](https://www.nuget.org/packages/LibTmux).

### [`LibTmux.FSharp`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.FSharp): F# helpers over the core

Curried task helpers, ordinary `seq` queries, and typed portable filters over
the core handles. Start with its
[quickstart](src/LibTmux.FSharp/README.md#quick-start).

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
- **YAML, JSON, and MCP in the core.** Workspace files, query documents on the
  wire, and the MCP server are separate packages, so a caller who does not want
  them never sees their dependencies.
- **A terminal emulator.** This drives tmux and draws nothing.
- **Native Windows.** [`PsmuxServer`][api-psmuxserver] and its siblings read one
  [psmux](https://github.com/psmux/psmux) session and cannot create or change
  anything. See [the preview](docs/psmux.md).

## Support and status

- **Alpha.** Any release may change or remove exported identifiers.
- **tmux.** Stable 3.3a and newer. CI builds 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b,
  3.7c, and 3.8. Development, release-candidate, and `next-*` versions have
  unknown capability state.
- **.NET.** net8.0 and net10.0.
- **Systems.** Linux, including WSL. macOS is best effort, with highly limited
  CI coverage. CI builds on Windows x64, where the types that need a real tmux
  are annotated unsupported.
- **Windows preview.** The [`Psmux*` preview](docs/psmux.md) reads a Windows
  psmux session natively or from WSL. It is best effort, with highly limited CI
  coverage; its smoke harness runs by hand.
- **Trimming and NativeAOT.** The `LibTmux` core is analyzer-gated, and its
  smoke app is published and run for `linux-x64` on net8.0 and net10.0.
- **Trimmed callers.** [`Compile`][api-query-queryextensions-compile] and
  [`Matching`][api-query-queryextensions-matching] resolve properties by name
  and warn callers to preserve the filtered types' public properties.

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
- [Examples](examples/README.md) — every example here is a test, and the C# in
  this README is quoted from one
- [AGENTS.md](AGENTS.md) — how to work in this repository

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
[api-mcp-guide]: https://libtmux.org/en/csharp/latest/mcp/
[api-mcp-tools]: https://libtmux.org/en/csharp/latest/mcp/tools/
[api-pane]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane/
[api-pane-refreshasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-refreshasync/
[api-pane-runasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-runasync/
[api-psmuxserver]: https://libtmux.org/en/csharp/latest/reference/libtmux-psmuxserver/
[api-query-json-queryjson]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-json-queryjson/
[api-query-queryextensions-compile]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-compile/
[api-query-queryextensions-matching]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-matching/
[api-query-queryextensions-translate]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-queryextensions-translate/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-server-chain]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-chain/
[api-server-createownedasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-createownedasync/
[api-server-entercontrolmodeasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-entercontrolmodeasync/
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
