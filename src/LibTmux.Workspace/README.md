<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET Workspace">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# LibTmux.Workspace

Build tmux sessions from [tmuxp](https://github.com/tmux-python/tmuxp)
YAML or JSON [workspace files][api-ws-workspace-workspacefile], on top of
[LibTmux](https://www.nuget.org/packages/LibTmux).

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

[Parse a file][api-ws-workspace-workspacefile-parse],
[review the plan][api-ws-workspace-workspacebuilder-planasync] of what it will
do, and [apply it][api-ws-workspace-workspacebuilder-applyasync] or
[build in one call][api-ws-workspace-workspacebuilder-buildasync]. It suits
launchers, devcontainer entrypoints, and internal CLIs that already describe
sessions in tmuxp YAML and want typed results.

Add it to your project:

```console
$ dotnet package add LibTmux.Workspace --prerelease
```

> **Alpha.** The public API will change between prereleases without notice, so
> pin an exact version.

**Contents** — [Quick start](#quick-start) ·
[Plan and apply](#plan-and-apply-a-workspace) ·
[Read a file](#read-a-workspace-file) ·
[Options and commands](#set-options-environment-and-commands) ·
[Startup](#choose-startup-and-existing-session-behavior) ·
[Failures](#handle-results-and-failures) ·
[Library or command](#library-or-command-line) ·
[Leaves out](#what-it-leaves-out) · [Support](#support-and-status)

## Quick start

Build a two-window session from a YAML declaration. The block uses your
configured LibTmux [`Server`][api-server] and cancellation token; the
[LibTmux quick start](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux/README.md#quick-start)
covers choosing a socket:

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

It runs against a tmux server of its own and prints nothing. The session `api`
has windows `editor` and `server`, and the block throws otherwise.

Every `csharp run` block on this page runs the same way, with `server`,
`session`, and `ct` already in hand.

## Plan and apply a workspace

### [`WorkspaceBuilder.PlanAsync`][api-ws-workspace-workspacebuilder-planasync]: review before anything runs

A plan lists what will happen.
[`ApplyAsync`][api-ws-workspace-workspacebuilder-applyasync] executes it, and
[`Validate`][api-ws-workspace-workspacebuilder-validate] checks a declaration
locally before any tmux endpoint is chosen:

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

It prints one line per action, then `api: 2 windows`.

- Planning observes the endpoint and creates nothing. Displaying or enumerating
  the plan runs no tmux command.
- `ApplyAsync` rechecks the daemon and session it observed before it executes.
- From the third pane in a window, the plan adds an
  [`ArrangePanes`][api-ws-workspace-workspaceactionkind-arrangepanes] action
  that tiles the existing panes to make room. It can resize programs already
  running.

### [`WorkspaceBuilder.BuildAsync`][api-ws-workspace-workspacebuilder-buildasync]: plan and apply in one call

It uses the default policies, as the quick start does. It sends input
immediately; it does not wait for shells to be ready or for commands to finish.
[Startup](#choose-startup-and-existing-session-behavior) covers waiting.

## Read a workspace file

### [`WorkspaceFile.Parse`][api-ws-workspace-workspacefile-parse]: YAML or JSON into a declaration

It reads the text without contacting tmux. Unknown keys, wrong value shapes,
multiple YAML documents, and inputs over 1 MiB throw
[`WorkspaceFormatException`][api-ws-workspace-workspaceformatexception], naming
the property path, line, and column.

### [`WorkspaceFile.Resolve`][api-ws-workspace-workspacefile-resolve]: absolute directories

Resolve directories relative to the file when reading from disk:

```csharp
string source = Path.GetFullPath("session.yaml");
WorkspaceFile fromDisk = WorkspaceFile.Parse(File.ReadAllText(source))
    .Resolve(Path.GetDirectoryName(source)!);
```

- A window inherits the session directory, and a pane inherits its window's.
  Neither call contacts tmux or checks that a directory exists.
- `$NAME` and `${NAME}` expand from the `variables` argument. A leading `~`
  needs an absolute `HOME` value there, and `$$` is a literal dollar sign.
  Unknown directory variables fail.
- The resolver does not read process environment variables.

### [`WorkspaceFile.FromSnapshot`][api-ws-workspace-workspacefile-fromsnapshot]: a captured session as a declaration

Capture through [`SnapshotDepth.Panes`][api-snapshotdepth-panes] first; a
missing relation throws
[`IncompleteSnapshotException`][api-incompletesnapshotexception]:

```csharp run
Server captured = await server.CaptureSnapshotAsync(SnapshotDepth.Panes, ct);
Session source = captured.Sessions.Single(value => value.Id == session.Id);
WorkspaceFile frozen = WorkspaceFile.FromSnapshot(source);
WorkspaceFile resolved = frozen.Resolve("/tmp");
Console.WriteLine($"{resolved.SessionName}: {resolved.Windows.Count} windows");
```

It prints the session name and its window count.

The declaration keeps window and pane order, window indexes, names, layouts,
pane directories, and the selected window and pane. It omits commands,
environment, options, terminal text, entity IDs, and pane indices.

## Set options, environment, and commands

### Options: when each one applies

```yaml
session_name: workers
global_options:
  default-shell: /bin/sh
windows:
  - window_name: workers
    layout: main-horizontal
    options:
      main-pane-height: 5
    options_after:
      synchronize-panes: on
    panes:
      - shell_command: echo left
      - shell_command: echo right
```

- Session `options` and `global_options` apply before the windows are created;
  window `options` apply before startup input and further splits;
  `options_after` apply after all pane input and the final layout.
- Global options change defaults on the selected daemon. Plans list them as
  [`SetOptionRequest`][api-setoptionrequest] actions with `Global = true`;
  [`Append`][api-ws-workspace-workspaceexistingsession-append] applies them and
  [`Reuse`][api-ws-workspace-workspaceexistingsession-reuse] leaves them
  untouched.
- [`WorkspaceFile.GlobalOptions`][api-ws-workspace-workspacefile-globaloptions]
  and
  [`WorkspaceWindow.OptionsAfter`][api-ws-workspace-workspacewindow-optionsafter]
  expose the same read-only maps in C#.

### Commands: `enter`

```yaml
windows:
  - panes:
      - enter: false
        shell_command_before:
          - "echo "
        shell_command:
          - cmd: ready
            enter: true
```

- `environment` merges at session, window, and pane level; a child wins on the
  same key.
- Commands are sent in session-before, window-before, pane-before, then pane
  order.
- With `enter: false`, tmux types the text without pressing Enter, and later
  text joins the pending line until an Enter is requested.
  [`WorkspacePane.Enter`][api-ws-workspace-workspacepane-enter] holds the pane
  default.
- A non-Boolean `enter` value is rejected with its path and location. Other
  tmuxp command modifiers are unsupported.

## Choose startup and existing-session behavior

### [`WorkspacePlanOptions`][api-ws-workspace-workspaceplanoptions]: what the plan does about conflicts and readiness

| Policy | Behavior |
|---|---|
| `ExistingSession = Error` (default) | Refuse a conflicting session. |
| `ExistingSession = Reuse` | Return the inspected session without changing it. |
| `ExistingSession = Append` | Add windows while preserving existing children and local session options; apply declared global options. |
| `ExistingSession = Replace` | Replace the inspected session while preserving its daemon. |
| `Readiness = Immediate` (default) | Send each command as literal input, pressing Enter according to its effective inherited state. |
| `Readiness = Cooperative` | Wait for startup to signal its per-pane channel before sending commands. |

- The default `ServerStartup = CreateOrJoin` may start a daemon or join one that
  appeared after planning, and grants no ownership of it.
  [`RequireExisting`][api-ws-workspace-workspaceserverstartup-requireexisting]
  needs a running daemon.
- With [`Cooperative`][api-ws-workspace-workspacereadiness-cooperative], each
  pane gets a fresh `LIBTMUX_WORKSPACE_READY` value, and startup signals it with
  `tmux wait-for -S "$LIBTMUX_WORKSPACE_READY"`. A signal sent before the wait
  is kept.

This receiver signals before it starts `/bin/cat`, which accepts queued input:

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

It prints the session name. A timeout stops input to that pane.

## Handle results and failures

### [`WorkspaceBuildException`][api-ws-workspace-workspacebuildexception]: what happened when application fails

- [`BuildAsync`][api-ws-workspace-workspacebuilder-buildasync] returns the
  session and windows it created. Its `Unsupported` list names the final layouts
  tmux rejected; those windows stay usable.
- [`Journal`][api-ws-workspace-workspacebuildexception-journal] records every
  action, including failures and actions never started.
  [`PartialResult`][api-ws-workspace-workspacebuildexception-partialresult]
  holds the state that exists, or is null when no session could be read.
- Cancelling during application throws
  [`WorkspaceOperationCanceledException`][api-ws-workspace-workspaceoperationcanceledexception]
  with the same partial state. It does not roll anything back.
- `CompensateOnFailure = true` cleans up resources proven to be created by this
  application, within `CleanupTimeout`. It does not reverse shell commands, host
  effects, or global option changes.
- A missing session name or an empty window list throws
  [`WorkspaceFormatException`][api-ws-workspace-workspaceformatexception] before
  anything is created.

Set `window_index` in YAML, or `WorkspaceWindow(windowIndex: 5)` in C#, to ask
for an index. Duplicate requests fail local validation.

## Library or command line

`tmux-workspace`, the [command-line tool][pkg-cli], has its own builder rather
than calling [`WorkspaceBuilder`][api-ws-workspace-workspacebuilder]. Read a
file against the side that will build it:

| | This library | `tmux-workspace` |
|---|---|---|
| Document language | a closed tmuxp subset | the tmuxp language, plus `x-` passthrough |
| Layout validation | syntax and version checked before actions; runtime rejection recorded in `Unsupported` | refuses the load |
| Shell readiness | immediate by default; explicit cooperative readiness fails on timeout | cursor heuristic; sends the command after timeout |
| A failure partway | throws with an action journal and optional receipt-proven compensation | removes a session it created |
| Session size | tmux's default, 80x24 | the invoking terminal |

A file the CLI loads can raise
[`WorkspaceFormatException`][api-ws-workspace-workspaceformatexception] here.
Both build the same topology for the subset they share.

## Related packages

| Package | Adds |
|---|---|
| [LibTmux](https://www.nuget.org/packages/LibTmux) | The client. Required. |
| [LibTmux.Query.Json](https://www.nuget.org/packages/LibTmux.Query.Json) | JSON for query documents |
| [LibTmux.Mcp](https://www.nuget.org/packages/LibTmux.Mcp) | A Model Context Protocol server, as a .NET tool |

## What it leaves out

- **A tmuxp runtime.** Plugins, lifecycle hooks, and tmuxp's configuration
  search path are rejected. Run tmuxp for those.
- **The rest of tmuxp's language.** It reads a closed subset: session name,
  start directory, options, `global_options`, `options_after`, windows, window
  indexes, panes, layouts, focus, environment, and `shell_command` values.
- **Host scripts by default.** `before_script` runs only when the plan enables
  [`AllowHostScripts`][api-ws-workspace-workspaceplanoptions-allowhostscripts].
  It is `/bin/sh -c` text, bounded by
  [`HostScriptTimeout`][api-ws-workspace-workspaceplanoptions-hostscripttimeout]
  and
  [`MaxHostOutputBytes`][api-ws-workspace-workspaceplanoptions-maxhostoutputbytes].
- **Process checkpoints.** A captured declaration cannot recover the shell
  command a foreground program started from.

## Support and status

- **Alpha.** Any release may change or remove exported identifiers. Pin an exact
  version.
- **Dependencies.** [LibTmux](https://www.nuget.org/packages/LibTmux), and
  [YamlDotNet](https://github.com/aaubry/YamlDotNet) for the files.
- **tmux and .NET.** The tmux versions LibTmux supports, on net8.0 and net10.0.
- **Systems.** Linux, including WSL. CI also runs macOS arm64 and builds on
  Windows x64.

## Documentation

- [The full contracts](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Workspace/docs/contracts.md):
  validation, resolving, option stages, commands, readiness, failures, and
  conversion
- [`WorkspaceBuilder`][api-ws-workspace-workspacebuilder] and the
  [workspace reference][api-ws-reference] on libtmux.org
- Source:
  [`src/LibTmux.Workspace`](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace);
  issues and history on [GitHub](https://github.com/libtmux/libtmux-dotnet)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[pkg-cli]: https://www.nuget.org/packages/LibTmux.Workspace.Cli

[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-setoptionrequest]: https://libtmux.org/en/csharp/latest/reference/libtmux-setoptionrequest/
[api-snapshotdepth-panes]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotdepth-panes/
[api-ws-reference]: https://libtmux.org/en/csharp/latest/workspace/reference/
[api-ws-workspace-workspaceactionkind-arrangepanes]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceactionkind-arrangepanes/
[api-ws-workspace-workspacebuilder]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder/
[api-ws-workspace-workspacebuilder-applyasync]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder-applyasync/
[api-ws-workspace-workspacebuilder-buildasync]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder-buildasync/
[api-ws-workspace-workspacebuilder-planasync]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder-planasync/
[api-ws-workspace-workspacebuilder-validate]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuilder-validate/
[api-ws-workspace-workspacebuildexception]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuildexception/
[api-ws-workspace-workspacebuildexception-journal]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuildexception-journal/
[api-ws-workspace-workspacebuildexception-partialresult]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacebuildexception-partialresult/
[api-ws-workspace-workspaceexistingsession-append]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceexistingsession-append/
[api-ws-workspace-workspaceexistingsession-reuse]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceexistingsession-reuse/
[api-ws-workspace-workspacefile]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacefile/
[api-ws-workspace-workspacefile-fromsnapshot]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacefile-fromsnapshot/
[api-ws-workspace-workspacefile-globaloptions]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacefile-globaloptions/
[api-ws-workspace-workspacefile-parse]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacefile-parse/
[api-ws-workspace-workspacefile-resolve]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacefile-resolve/
[api-ws-workspace-workspaceformatexception]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceformatexception/
[api-ws-workspace-workspaceoperationcanceledexception]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceoperationcanceledexception/
[api-ws-workspace-workspacepane-enter]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacepane-enter/
[api-ws-workspace-workspaceplanoptions]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceplanoptions/
[api-ws-workspace-workspaceplanoptions-allowhostscripts]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceplanoptions-allowhostscripts/
[api-ws-workspace-workspaceplanoptions-hostscripttimeout]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceplanoptions-hostscripttimeout/
[api-ws-workspace-workspaceplanoptions-maxhostoutputbytes]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceplanoptions-maxhostoutputbytes/
[api-ws-workspace-workspacereadiness-cooperative]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacereadiness-cooperative/
[api-ws-workspace-workspaceserverstartup-requireexisting]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspaceserverstartup-requireexisting/
[api-ws-workspace-workspacewindow-optionsafter]: https://libtmux.org/en/csharp/latest/workspace/reference/libtmux-workspace-workspacewindow-optionsafter/
