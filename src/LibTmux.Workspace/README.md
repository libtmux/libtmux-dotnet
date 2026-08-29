# LibTmux.Workspace

Build tmux sessions from [tmuxp](https://github.com/tmux-python/tmuxp)
workspace files, on top of [LibTmux](https://www.nuget.org/packages/LibTmux).

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

Reading one off disk is the same call:

```csharp
WorkspaceFile fromDisk = WorkspaceFile.Parse(File.ReadAllText("session.yaml"));
```

`start_directory` values are passed to tmux unchanged. Relative paths are not
rebased to the directory containing `session.yaml`.

## Failure behavior

`BuildAsync` returns the session and windows it created. Its `Unsupported` list
contains only layouts that tmux rejected; those windows remain usable.

Other tmux failures throw and can leave a partially built session. The builder
is not transactional. A missing session name or empty window list raises
`WorkspaceFormatException` before creating anything.

## What is in scope

This reads a closed tmuxp subset: session name, start directory, scalar
options, windows, panes, layouts, focus, and scalar or ordered
`shell_command` values. Duplicate or unknown keys, wrong value shapes,
multiple YAML documents, and inputs over 1 MiB raise
`WorkspaceFormatException` instead of being ignored.

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
