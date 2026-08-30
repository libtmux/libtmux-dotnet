# LibTmux

A typed, async-first [tmux](https://github.com/tmux/tmux) client for .NET.
Servers, sessions, windows, panes, clients, options, hooks and buffers, against
every tmux from **3.2a to 3.7b**, on **net8.0** and **net10.0**.

> **Alpha.** The public API is not settled and can change between prereleases
> without notice, so pin an exact version. The behaviour is proven against all
> seven supported tmux versions on every commit.

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
Session session = await server.CreateSessionAsync(new NewSessionRequest(name: "build"));
Window window = await session.CreateWindowAsync(new NewWindowRequest(name: "tests"));
Pane pane = (await window.GetPanesAsync())[0];

await pane.SendTextAsync("dotnet test");
await pane.EnterAsync();
```

To reach one server in particular:

```csharp
Server elsewhere = await Server.ConnectAsync(
    new ServerConnectionOptions(socketName: "build-box"));
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
Window built = await session.CreateWindowAsync(new NewWindowRequest(name: "build"), ct);
```

```csharp run
// One client, held open, streaming what tmux does on its own.
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
IReadOnlyList<string> reply = await control.SendAsync("list-windows", ct);
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
while you enumerate them:

```csharp run
foreach (Window each in await session.GetWindowsAsync(ct))
{
    foreach (Pane every in await each.GetPanesAsync(ct))
    {
        Console.WriteLine($"{each.Name} {every.Index} {every.Width}x{every.Height}");
    }
}
```

A handle says what it read, and that stays true. Operations that change what an
object is hand back a replacement:

```csharp run
Window renamed = await window.RenameAsync("integration", ct);
```

Asking tmux again is `RefreshAsync`. A whole hierarchy in one read is
`CaptureSnapshotAsync`:

```csharp run
Server snapshot = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
```

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

## Splitting and resizing

```csharp run
Pane split = await pane.SplitAsync(new SplitPaneRequest(direction: PaneDirection.Below), ct);
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
    session => session.Name.StartsWith("build") && session.Attached);
```

Relations quantify, and the element type carries its own fields:

```csharp run
Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Windows, ct);
IReadOnlyList<Session> withBuild = captured.Sessions.Matching<Session>(
    session => session.Windows.Any(each => each.Name.StartsWith("build")));
```

The same expression is also a document, which can be written here and answered
somewhere else:

```csharp run
QueryDocument document = QueryExtensions.Translate<Session>(
    session => session.Name.StartsWith("build") && session.Attached);
```

You write C# and tmux receives tmux. The catalog carries the pair for all
twelve queryable fields — `Session.Name` is `session_name`,
`Client.IsControlClient` is `client_control_mode` — and it is closed:

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
than falling back, so an expression that translates is one tmux can answer.

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

`LibTmux.Testing` ships in this package. It gives a test a tmux server of its
own, on its own socket, killed deterministically:

```csharp
using LibTmux.Testing;

TmuxTestFactory factory = new();
await using TemporaryHierarchyScope scope = await factory.CreateHierarchyAsync();

await scope.Pane.SendTextAsync("echo hello");
await scope.Pane.EnterAsync();
```

Disposing kills the server, so a test that fails part way through leaves
nothing behind.

## Logging

Pass an `ILogger` when connecting and every tmux command is recorded once, at
the single point they all pass through:

```csharp
Server logged = await Server.ConnectAsync(new ServerConnectionOptions(logger: logger));
```

Commands are recorded at `Debug` and failures at `Error`, with stable scalar
fields (`TmuxSubcommand`, `TmuxExitCode`) to filter on. Anything that can carry
a payload is truncated, the command line included.

## Knowing when a retry is safe

Retrying a failed command is the obvious recovery and it is only sound when the
command never reached tmux. Every failure says which it was, so the decision is
an exception filter rather than a guess:

```csharp run
try
{
    await server.CreateSessionAsync(new NewSessionRequest(name: "build"), ct);
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
| tmux | 3.2a to 3.7b |
| .NET | net8.0, net10.0 |
| OS | Linux and macOS. `Server`, `Session`, `Window` and `Pane` are annotated unsupported on Windows, because their lifecycle, mutation and control-mode contracts need a real tmux |
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
