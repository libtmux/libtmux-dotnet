# libtmux for .NET

[![LibTmux](https://img.shields.io/nuget/vpre/LibTmux?logo=nuget&label=LibTmux)](https://www.nuget.org/packages/LibTmux)
[![downloads](https://img.shields.io/nuget/dt/LibTmux?logo=nuget&label=downloads)](https://www.nuget.org/packages/LibTmux)
[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux 3.2a – 3.7b](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Drive [tmux](https://github.com/tmux/tmux) from .NET. Servers, sessions,
windows, panes, clients, options, hooks and buffers, typed and asynchronous,
against every tmux from **3.2a to 3.7b** on **net8.0** and **net10.0**.

> **Alpha.** Releases carry an `-alpha` prerelease tag. The API is not
> settled, and any release may change or remove exported identifiers without a
> deprecation period. Pin an exact version. Not recommended for production.

<!-- snippet: ConnectAndBuild usings: LibTmux -->
```csharp
using LibTmux;

Server server = await Server.ConnectAsync();
Session session = await server.CreateSessionAsync(new NewSessionRequest(name: "build"));
Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "tests"));
Pane pane = (await window.GetPanesAsync())[0];

await pane.SendTextAsync("dotnet test");
await pane.EnterAsync();
```
<!-- endsnippet -->

## Is this for you?

**Yes, if you want to** drive a real terminal from code — build a dev
environment, script a workspace, harness a TUI in tests, or give an assistant
hands on a terminal.

**Look elsewhere if you want** a terminal emulator (this drives tmux, it does
not draw), unrestricted Windows parity, or a process launcher — `Process.Start`
is right there. Native Windows has a bounded, query-only
[`Psmux*` preview](docs/psmux.md).

**What you get that a shell wrapper does not:** typed entities with real IDs,
one dispatch model per workload (below), a version model that tells you when a
flag does not exist on the tmux you are on rather than failing oddly, and
documented ordinary-tmux examples that are executed against live tmux in CI.

## Packages

| Package | | Add it when |
|---|---|---|
| **[LibTmux](src/LibTmux/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux) | Always. The client. One dependency: logging abstractions. |
| **[LibTmux.Query.Json](src/LibTmux.Query.Json/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Query.Json?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Query.Json) | You send queries between processes and want them as JSON. |
| **[LibTmux.Workspace](src/LibTmux.Workspace/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Workspace?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Workspace) | You have [tmuxp](https://github.com/tmux-python/tmuxp) YAML to build from. |
| **[LibTmux.Mcp](src/LibTmux.Mcp/README.md)** | [![v](https://img.shields.io/nuget/vpre/LibTmux.Mcp?logo=nuget&label=%20)](https://www.nuget.org/packages/LibTmux.Mcp) | You want an assistant driving tmux. Installs as a tool, not a reference. |

```console
$ dotnet package add LibTmux --prerelease
```

The core takes exactly one dependency. Anything that would add another ships as
its own package, so a caller who does not want YAML never sees YamlDotNet. They
all carry one version, so any `LibTmux.Workspace` goes with the `LibTmux` of the
same version, without a table to consult.

## Three ways to reach tmux

Which one a call uses is visible where the call starts — never a flag buried in
options — and all three work on every supported tmux.

| Mode | Flip it on | Dispatch | What one more command costs |
|---|---|---|---|
| **[One-shot](docs/modes/one-shot.md)** | `session.CreateWindowAsync(…)` | one command, awaited | another process — **~2.3 ms** |
| **[Control](docs/modes/control-mode.md)** | `server.EnterControlModeAsync(ct)` | one client, streamed | another round trip — **~0.2 ms** |
| **[Chained](docs/modes/chaining.md)** | `server.Chain()…ExecuteAsync(ct)` | N batched, one invocation | more bytes on one command line — **~0.02 ms** |

That is the marginal cost, which is the part that is a property of the library
rather than of the machine: the difference between fifty commands and one,
divided by forty-nine, as medians of 100 samples against tmux 3.7b. The
absolute numbers move — a tmux process start measured 2.4 ms and 19 ms on the
same machine the same day — so the [recorded run](docs/benchmarks/) gives the
whole distribution, the tmux, the host and the date, and
[docs/benchmarks](docs/benchmarks/README.md) says which parts of it travel to
your machine and which do not.

The same window, three ways:

```csharp run
// One-shot: a command, a typed object back.
Window built = await session.CreateWindowAsync(new NewWindowRequest(name: "build"), ct);
Console.WriteLine(built.Name);
```

```csharp run
// Control mode: one client, held open, streaming what tmux does.
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> reply = await control.SendAsync("new-window -d -n build", ct);
```

```csharp run
// Chained: many commands, one invocation.
await server.Chain()
    .Then("new-window", "-d", "-n", "build")
    .Then("new-window", "-d", "-n", "test")
    .ExecuteAsync(ct);
```

Read the crossovers, not the numbers: control mode is an order of magnitude
cheaper *per command* because its client is already running, while a chain
beats it *for a batch* by paying one round trip for the whole sequence.
[Choosing a mode](docs/modes/matrix.md) has allocations and how to rerun the
table yourself.

## Reading what is there

Accessors return `IReadOnlyList<T>` over an explicit read, and never shell out
while you enumerate them — a `foreach` cannot surprise you with a tmux command
per item.

```csharp run
foreach (Window each in await session.GetWindowsAsync(ct))
{
    foreach (Pane every in await each.GetPanesAsync(ct))
    {
        Console.WriteLine($"{each.Name} pane {every.Index} {every.Width}x{every.Height}");
    }
}
```

A handle says what it read, which stays true. Operations that change what an
object *is* hand back a replacement rather than mutating what you hold:

```csharp run
Window renamed = await window.RenameAsync("integration", ct);
Console.WriteLine($"{window.Name} -> {renamed.Name}");
```

Asking tmux again is `RefreshAsync`.

## Running something, and reading it back

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

## Filtering

Ordinary filtering is LINQ over what you already read:

```csharp run
IReadOnlyList<Window> windows = await session.GetWindowsAsync(ct);
IEnumerable<Window> building = windows.Where(
    each => each.Name.StartsWith("build", StringComparison.Ordinal));
```

Declarative filtering is different: it turns an expression into a portable
document, or throws. It never quietly falls back to filtering in memory. Write
it over the objects you already hold:

```csharp run
IReadOnlyList<Session> sessions = await server.GetSessionsAsync(ct);
IReadOnlyList<Session> building = sessions.Matching<Session>(
    session => session.Name.StartsWith("build"));
```

The same expression is also a document, which can be written here and answered
somewhere else:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build") && session.Attached);

Console.WriteLine(document.Target);   // Session
```

You write C# and tmux receives tmux: `Session.Name` goes on the wire as
`session_name`, and `Client.IsControlClient` as `client_control_mode`. The catalog
carries that pair for all twelve queryable fields, and it is closed — a field
outside it throws `UnsupportedQueryExpressionException` rather than falling
back, so an expression that translates is one tmux can answer.
[LibTmux.Query.Json](src/LibTmux.Query.Json/README.md) puts the document on the
wire.

## Options and hooks

Every object reaches the option table tmux keeps for it. tmux has no types, so
a value carries the text it reported alongside the readings that text supports:

```csharp run
await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"), ct);
TmuxOption option = (await window.Options.GetAsync(
    new GetOptionRequest("automatic-rename"), ct))[0];

Console.WriteLine($"{option.Value.Raw} flag={option.Value.Boolean} number={option.Value.Integer}");
```

An option the window does not hold is inherited rather than missing, and hooks
work the same way — arrays even with one entry:

```csharp run
TmuxHook hook = await server.Hooks.SetAsync(
    new SetHookRequest("alert-bell", "set-option -g @rang yes"), ct);
Console.WriteLine(hook.Values[0].Command);
```

## Versions

tmux grew flags across the supported range. Where a flag is missing, the
request still goes out without it and a warning says what was left off. Where a
whole command is missing, nothing is sent and `TmuxVersionTooLowException` says
which version would be needed.

```csharp run
// A handle says what it read: the version is what tmux reported when this
// server was reached, and null when it reported something unparsable.
TmuxVersion? version = server.Version;
Console.WriteLine($"tmux {version?.Raw} 3.4-or-newer={version?.IsAtLeast(TmuxVersion.Parse("3.4"))}");
```

Every difference between 3.2a and 3.7b is [recorded with the test that proves
it](docs/parity/version-deltas.json), and [dotnet-tmux.yml](.github/workflows/dotnet-tmux.yml)
builds all seven from source on every commit.

## Testing your own code

`LibTmux.Testing` gives a test a tmux server of its own, on its own socket,
killed deterministically:

```csharp
using LibTmux.Testing;

TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
await scope.Pane.EnterAsync();
```

Disposing kills the server, so a test that fails part way through leaves
nothing behind. `TmuxWait.UntilAsync` waits for a state rather than sleeping,
which is what keeps tmux tests from being timing-dependent.

## An assistant on your terminal

[LibTmux.Mcp](src/LibTmux.Mcp/README.md) is a
[Model Context Protocol](https://modelcontextprotocol.io) server. It is a
separate package and installs as a .NET tool, not a library reference:

```console
$ dotnet tool install --global LibTmux.Mcp --prerelease
```

```json
{ "mcpServers": { "tmux": { "command": "libtmux-mcp" } } }
```

It exposes 42 tools across three safety tiers, four fixed `tmux://` resources,
two resource templates and four workflow prompts — [the full
reference](docs/mcp/tools.md) is generated from the server itself. Pass a socket
name as its first argument to drive a server other than the ambient one, which
is what a sandbox wants.

What it is built around is that an assistant should never get stuck and never
waste context. `tmux_run` returns the shell's real exit status,
`tmux_start_job` hands back a handle for work that takes minutes, and
`tmux_wait_for_text` normally wakes from tmux's control-mode stream, with a
bounded polling fallback when that stream cannot start. Nothing returns
unbounded output: every capture keeps the newest lines and reports what it
dropped. `LIBTMUX_SAFETY` decides which tier is registered, and a tool above it
never reaches the model's list.
[Full instructions](src/LibTmux.Mcp/README.md).

## Documentation

- [Choosing a mode](docs/modes/matrix.md) — the three dispatch modes, measured
- [Windows psmux preview](docs/psmux.md) — what it reads, and what it refuses
- [API reference](docs/api/README.md) — rendered from the doc comments
- [tmux MCP tools](docs/mcp/tools.md) — every tool, tier and resource, generated
- [Public API](docs/public-api.md) — the reviewed, approved surface
- [Version deltas](docs/parity/version-deltas.json) — every tmux difference, with its proof
- [Decisions](docs/decisions/) — why the transport, object model and query catalog are shaped this way
- [Examples](examples/README.md) — every example here is a test, and the C# in this README is quoted from one
- [AGENTS.md](AGENTS.md) — how to work in this repository

## Compatibility

| | |
|---|---|
| tmux | 3.2a, 3.3a, 3.4, 3.5, 3.6, 3.7a, 3.7b |
| .NET | net8.0, net10.0 |
| OS | Linux, macOS. The bounded [`Psmux*` native-Windows and WSL query preview](docs/psmux.md) is experimental; its release gate runs both paths on net8.0 and net10.0 |
| Trimming / NativeAOT | `LibTmux` core is analyzer-gated and its smoke app is published and run for `linux-x64` on net8.0 and net10.0. That proof does not cover the other packages, macOS, or native Windows/psmux |

## License

[MIT](LICENSE). Practical parity with Python
[libtmux](https://github.com/tmux-python/libtmux), rewritten for .NET.
