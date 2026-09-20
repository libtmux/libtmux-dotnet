# LibTmux.Workspace

Build tmux sessions from [tmuxp](https://github.com/tmux-python/tmuxp)
YAML or JSON workspace files, on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

> **Alpha.** The public API is not settled and can change between prereleases
> without notice, so pin an exact version.

```console
$ dotnet package add LibTmux.Workspace --prerelease
```

Adds one dependency, [YamlDotNet](https://github.com/aaubry/YamlDotNet), which
is why this is a package of its own rather than part of the client.

## When you want this

You already describe your development sessions in tmuxp YAML and want to build
them from .NET — a launcher, a devcontainer entrypoint, an internal CLI — with
typed results instead of shelling out to another runtime.

## Review and apply

Use your configured LibTmux `Server` and cancellation token. The
[client quickstart](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md)
covers choosing a socket.

```csharp run
WorkspaceFile workspace = WorkspaceFile.Parse("""
    session_name: api
    start_directory: /tmp
    windows:
      - window_name: editor
        panes:
          - shell_command: echo editing
      - window_name: server
        panes:
          - shell_command: echo serving
    """);

WorkspaceBuilder.Validate(workspace);
WorkspaceBuilder builder = new(server);
WorkspacePlan plan = await builder.PlanAsync(workspace, cancellationToken: ct);
foreach (WorkspaceAction action in plan.Actions)
    Console.WriteLine(action);

WorkspaceResult result = await builder.ApplyAsync(plan, ct);
Console.WriteLine($"{result.Session.Name}: {result.Windows.Count} windows");
```

`PlanAsync` validates the declaration and observes the selected endpoint without
creating sessions or running the connection initializer. Its immutable actions
show creation, input, readiness, host scripts, final capture and conditional
cleanup. Displaying or enumerating the plan performs no I/O. `ApplyAsync`
rechecks the observed daemon and session before executing those actions.

`Validate(workspace, options)` checks declaration and policy constraints locally.
It can run before a tmux endpoint is selected. With `Reuse`, host-script checks
wait until planning determines whether creation is needed. Planning checks
endpoint conflicts; tmux and the shell determine command validity during
application.

Before creating the third and each later pane in a window, the plan includes
an `ArrangePanes` action that applies tmux's tiled layout to the existing panes.
This makes room for further splits and can resize programs already running.
A construction layout failure stops application. The declared final layout and
focus are applied afterward; without a declared layout, the construction
arrangement remains. Native window dimensions still limit pane capacity.

For the default policies, `BuildAsync(workspace, ct)` runs the same plan and
application engine in one call. It sends input immediately; it does not infer
shell readiness or wait for commands to finish.

## Resolve a workspace file

Resolve directories relative to the file when reading from disk:

```csharp
string source = Path.GetFullPath("session.yaml");
WorkspaceFile fromDisk = WorkspaceFile.Parse(File.ReadAllText(source))
    .Resolve(Path.GetDirectoryName(source)!);
```

`Parse` preserves the declaration. `Resolve` returns a new declaration whose
directories are absolute: a window inherits the session directory, and a pane
inherits its window directory. Each explicit relative path is resolved against
that parent. Omitted session directories inherit the supplied document base.
Neither operation contacts tmux or checks whether a directory exists.
The builder treats resolved directories as literal paths, including characters
that tmux would otherwise interpret as formats or styles.

Directory expansion accepts `$NAME` and `${NAME}` from the `variables` argument.
A leading `~` requires an absolute `HOME` value in that map; `$$` means a literal
dollar sign. Unknown variables fail. The resolver does not read process
environment variables, and leaves commands, names and option values literal.
Building an unresolved declaration passes directory strings to tmux unchanged,
including native tmux formats. Session and window names are literal.

## Environment and commands

`environment` contributes entries at session, window and pane level. Child
entries override the same ordinal key; other parent entries remain available.
`shell_command_before` accepts the same scalar or ordered command list as
`shell_command`. Commands are sent in session-before, window-before, pane-before,
then pane-command order. A window with no pane declarations still creates one
pane and receives the inherited commands.

For programmatic declarations, `WithDefaults(environment, shellCommandsBefore)`
returns a new value and copies both inputs. Null preserves the local defaults;
an empty collection clears them. Resolving directories preserves these values.
Environment values and command text remain literal until tmux or the receiving
shell interprets them.
Environment names must be nonempty and cannot contain `=` or NUL; values cannot
contain NUL. Invalid declarations fail before dispatch.

## Readiness and existing sessions

`WorkspacePlanOptions` makes startup and conflict behavior explicit:

| Policy | Behavior |
|---|---|
| `ExistingSession = Error` (default) | Refuse a conflicting session. |
| `ExistingSession = Reuse` | Return the inspected session without changing it. |
| `ExistingSession = Append` | Add windows while preserving existing children and session options. |
| `ExistingSession = Replace` | Replace the inspected session while preserving its daemon. |
| `Readiness = Immediate` (default) | Send each command as literal input followed by one Enter. |
| `Readiness = Cooperative` | Wait for startup to signal its per-pane channel before sending commands. |

The default `ServerStartup = CreateOrJoin` permits creation to start a daemon
or join one that appeared after planning. It grants no ownership of that daemon.
Use `RequireExisting` when planning must observe one already running.

Cooperative startup receives a fresh `LIBTMUX_WORKSPACE_READY` environment
value for each pane on every application. Startup must signal that channel
with `tmux wait-for -S "$LIBTMUX_WORKSPACE_READY"`. A signal sent before the
wait is preserved. The builder owns and closes its waits; it never polls a
cursor or treats startup output as a prompt.

This controlled receiver signals before starting `/bin/cat`, which accepts
queued input. Use the selected tmux executable so the pane and client agree:

```csharp run
string tmux = "'" + server.ConnectionOptions.TmuxBinaryPath
    .Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
WorkspaceFile receiver = new("receiver",
    options: new Dictionary<string, string>
    {
        ["default-command"] = $"{tmux} wait-for -S \"$LIBTMUX_WORKSPACE_READY\"; exec /bin/cat",
    },
    windows: [new WorkspaceWindow("input", panes: [new WorkspacePane(["first line"])])]);
WorkspaceBuilder builder = new(server);
WorkspacePlan plan = await builder.PlanAsync(receiver, new WorkspacePlanOptions
{
    Readiness = WorkspaceReadiness.Cooperative,
    ReadinessTimeout = TimeSpan.FromSeconds(5),
}, ct);
WorkspaceResult result = await builder.ApplyAsync(plan, ct);
Console.WriteLine(result.Session.Name);
```

An interactive shell must signal from its own startup when it can accept input.
Readiness acknowledges that startup contract; command completion needs a
separate application signal. A timeout stops input to that pane.

## Results and failures

`BuildAsync` returns the session and windows it created. Its `Unsupported` list
contains only requested final layouts that tmux rejected; those windows remain usable.

Creation, append and replacement end with the plan's `CaptureResult` action. It captures
the server's pane graph and returns the matching session and created window
placements in declaration order, including their final focus. This observes
an interval, not a transaction; concurrent topology changes can fail capture.
Reuse returns the inspected session unchanged, with no created windows or
additional graph capture.

`Journal` records every action, including rejected layouts, failures, uncertain
outcomes and actions never started. `CompensationJournal` records attempted
cleanup. Application failures throw `WorkspaceBuildException`; its
`PartialResult` contains materialized state, or is null when no session could
be read. Planning and declaration errors fail before application begins.

`CompensateOnFailure = true` requests cleanup of resources proven to have been
created by this application. Cleanup has its own bounded `CleanupTimeout`.
It does not reverse shell commands or host effects, and uncertain creations
are never guessed from names. Readiness channels and temporary replacement
keepalives are cleaned regardless of the compensation policy.

tmux starts a session's first pane before session options can be set. The
builder therefore creates one transient bootstrap window, applies the options,
creates the described first window under them, and removes the bootstrap.
tmux hooks can observe that extra window lifecycle. A missing session name or
empty window list raises `WorkspaceFormatException` before creating anything.

## What is in scope

This reads a closed tmuxp subset: session name, start directory, options at
session/window/pane scope, windows, panes, layouts, focus, environment and scalar or ordered
`shell_command` and `shell_command_before` values. `before_script` runs on the
host only when the plan enables `AllowHostScripts`; resolve the declaration
against its document directory first. `HostScriptTimeout` and
`MaxHostOutputBytes` bound host execution and its combined captured output.
Linux cleanup uses pinned process handles when available; otherwise it uses
.NET's best-effort tree cleanup, and an observed loss of descendant coverage
remains a cleanup failure.
Duplicate or unknown keys, wrong value shapes,
multiple YAML documents, and inputs over 1 MiB raise
`WorkspaceFormatException` instead of being ignored. Declaration errors name
the property path and its line and column in the input.

It is **not** a tmuxp runtime. Plugins, lifecycle hooks, and tmuxp's own
configuration search path are rejected — if you need those, run tmuxp.

## Related packages

| Package | Adds |
|---|---|
| [LibTmux](https://www.nuget.org/packages/LibTmux) | The client. Required. |
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

Source, docs and issues: <https://github.com/libtmux/libtmux-dotnet>

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
