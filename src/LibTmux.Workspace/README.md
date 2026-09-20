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

## Use it

```yaml
session_name: api
start_directory: /tmp
windows:
  - window_name: editor
    layout: even-horizontal
    focus: true
    panes:
      - shell_command: echo editing
      - shell_command: echo watching
  - window_name: server
    panes:
      - shell_command: echo serving
```

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

WorkspaceResult result = await new WorkspaceBuilder(server).BuildAsync(workspace, ct);
Console.WriteLine($"{result.Session.Name}: {result.Windows.Count} windows");
```

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
Calling `BuildAsync` on an unresolved declaration retains the previous behavior:
it passes directory strings to tmux unchanged, including native tmux formats.

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

## Failure behavior

`BuildAsync` returns the session and windows it created. Its `Unsupported` list
contains only layouts that tmux rejected; those windows remain usable.

Other tmux failures throw `WorkspaceBuildException`. Its `PartialResult`
contains the session and windows materialized before failure, or is null when
none could be read. The builder is not transactional. Before sending workspace
commands, `PaneReadiness.Auto`, the default, waits only for panes using a zsh
session `default-shell`.
`PaneReadiness.Always` waits before commands sent to every default-shell pane;
`PaneReadiness.Never` sends them immediately. A nonempty session
`default-command` skips the wait under every policy because that command is not
treated as an interactive shell.

A wait polls the targeted pane's `pane_current_command`, `cursor_x`, and
`cursor_y` for up to ten seconds. It sends no keys and creates no `wait-for`
channel. The result is a prompt heuristic, not an input acknowledgement:
startup output can move the cursor before a prompt exists, while a prompt left
at `(0, 0)` times out. Pass a different timeout to the `WorkspaceBuilder`
constructor when startup needs a different budget. An expired wait raises
`TmuxWaitTimeoutException` before a workspace command reaches that pane.

tmux starts a session's first pane before session options can be set. The
builder therefore creates one transient bootstrap window, applies the options,
creates the described first window under them, and removes the bootstrap.
tmux hooks can observe that extra window lifecycle. Readiness polling uses
targeted `display-message` calls, so an `after-display-message` hook can also
observe each sample. A missing session name or empty window list raises
`WorkspaceFormatException` before creating anything.

## What is in scope

This reads a closed tmuxp subset: session name, start directory, scalar
options, windows, panes, layouts, focus, environment and scalar or ordered
`shell_command` and `shell_command_before` values. Duplicate or unknown keys, wrong value shapes,
multiple YAML documents, and inputs over 1 MiB raise
`WorkspaceFormatException` instead of being ignored. Declaration errors name
the property path and its line and column in the input.

It is **not** a tmuxp runtime. Plugins, before/after hooks, and tmuxp's own
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
