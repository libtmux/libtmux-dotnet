# tmux-workspace

[![NuGet](https://img.shields.io/nuget/vpre/LibTmux.Workspace.Cli?logo=nuget&label=LibTmux.Workspace.Cli)](https://www.nuget.org/packages/LibTmux.Workspace.Cli)
[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux 3.2a – 3.7c](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)

Describe a tmux session once, in YAML, and open it with one command.
`tmux-workspace` reads [tmuxp](https://github.com/tmux-python/tmuxp)
workspace files, captures running sessions back into them, and answers in JSON
when a script asks.

Save this as `~/.tmuxp/myproject.yaml`:

```yaml
session_name: myproject
start_directory: ~/code/myproject
windows:
  - window_name: editor
    layout: main-vertical
    panes:
      - vim
      - git status
  - window_name: server
    panes:
      - npm run dev
```

Open it:

```console
$ tmux-workspace load myproject
```

Outside tmux this attaches your terminal to the new session. Inside tmux it
asks whether to switch to it, leave it detached, or add its windows to the
session you are in.

> **Alpha.** Every release carries an `-alpha` tag, and options may change
> between releases. Pin a version where a script depends on it.

## Install

```console
$ dotnet tool install --global LibTmux.Workspace.Cli --prerelease
```

`--prerelease` is required while every release is an alpha. If your shell
cannot find `tmux-workspace` afterwards, add `$HOME/.dotnet/tools` to `PATH`.

Try it without installing anything, using the .NET 10 SDK:

```console
$ dnx LibTmux.Workspace.Cli --prerelease --yes -- --help
```

Pin it for everyone who clones a repository. Run this at the repository root,
commit the `dotnet-tools.json` it writes, and run the tool as
`dotnet tmux-workspace`:

```console
$ dotnet tool install LibTmux.Workspace.Cli --prerelease
```

Upgrade a global install:

```console
$ dotnet tool update --global LibTmux.Workspace.Cli --prerelease
```

## Everyday commands

Load a workspace without attaching:

```console
$ tmux-workspace load -d myproject
```

Load the `.tmuxp.yaml` in a project directory:

```console
$ tmux-workspace load ~/code/myproject
```

Load several workspaces at once; the last one is the one you land in:

```console
$ tmux-workspace load api web
```

Add a workspace's windows to the tmux session you are in:

```console
$ tmux-workspace load --append tools
```

List the workspaces it can find:

```console
$ tmux-workspace ls
```

Find the workspaces that mention `server` in their name, windows, or pane
commands:

```console
$ tmux-workspace search server
```

Save a running session as a workspace file:

```console
$ tmux-workspace freeze myproject --save-to ~/.tmuxp/myproject.yaml
```

Convert a workspace between YAML and JSON:

```console
$ tmux-workspace convert myproject.yaml --save-to myproject.json
```

Turn a tmuxinator project into a workspace (`import teamocil` works the same
way):

```console
$ tmux-workspace import tmuxinator ~/.config/tmuxinator/blog.yml \
    --save-to ~/.tmuxp/blog.yaml
```

Open a workspace in `$EDITOR`:

```console
$ tmux-workspace edit myproject
```

Collect versions and search paths for a bug report:

```console
$ tmux-workspace debug-info
```

Open a Python REPL holding a session's tmux objects. It runs through tmuxp
1.74.0, so point `TMUX_WORKSPACE_PYTHON` at a Python that has it installed:

```console
$ tmux-workspace shell myproject
```

`tmux-workspace <command> --help` lists every option.

## Workspace files

A command that takes a workspace accepts a file path, a directory holding a
`.tmuxp.yaml`, or a bare name. A name is looked up in the first of these that
exists:

1. `$TMUXP_CONFIGDIR`
2. `$XDG_CONFIG_HOME/tmuxp`, which defaults to `~/.config/tmuxp`
3. `~/.tmuxp`

`ls` also shows the nearest `.tmuxp.yaml`, `.tmuxp.yml`, or `.tmuxp.json`
above the current directory.

The format is tmuxp's, in YAML or JSON. The
[configuration reference](https://libtmux.org/en/dotnet/latest/workspace/configuration/)
covers every key, and the
[example gallery](https://libtmux.org/en/dotnet/latest/workspace/examples/gallery/)
has files to start from.

## Scripting

`--json` prints one JSON document on stdout. `--ndjson` streams one record per
line as a load progresses. Errors go to stderr as JSON lines with a stable
`code`.

List workspace names:

```console
$ tmux-workspace ls --json | jq -r '.workspaces[].name'
```

Load detached and read back which sessions were created:

```console
$ tmux-workspace load -d --json myproject | jq -r '.results[].session_name'
```

Inside tmux, a scripted `load` needs `-d` or `--append`, because a script
cannot answer the switch prompt. `ls` and `search` print the same JSON as
tmuxp.

| Exit status | Meaning |
|---|---|
| 0 | Done |
| 1 | The operation failed; `status` and the error `code` say how |
| 2 | Usage: a bad option, or a command that cannot run where it was started |
| 70 | An internal error; please report it |
| 130 | Interrupted |

The [output reference](https://libtmux.org/en/dotnet/latest/workspace/reference/output/)
and [error codes](https://libtmux.org/en/dotnet/latest/workspace/reference/exit-codes/)
document every field.

## Shell completion

Bash, with the bash-completion package:

```console
$ tmux-workspace --generate bash > ~/.local/share/bash-completion/completions/tmux-workspace
```

zsh, into any directory on your `fpath`:

```console
$ tmux-workspace --generate zsh > ~/.zfunc/_tmux-workspace
```

fish:

```console
$ tmux-workspace --generate fish > ~/.config/fish/completions/tmux-workspace.fish
```

`--generate man` writes a manual page.

## Differences from tmuxp

- A window with no `layout` is tiled, where tmuxp keeps halving the last
  pane.
- Without a `focus` key the first window is left active; tmuxp leaves the
  last.
- `-8` is refused: no supported tmux implements 88-color mode. Use `-2` for
  256 colors.
- `shell`, plugins, and custom workspace builders run through tmuxp itself and
  need tmuxp 1.74.0.

[Compatibility](https://libtmux.org/en/dotnet/latest/workspace/reference/compatibility/)
lists every difference.

## Compatibility

| | |
|---|---|
| tmux | 3.2a and newer |
| .NET | .NET 8 or .NET 10 runtime |
| OS | Linux and macOS |

## Documentation

- [Command reference](https://libtmux.org/en/dotnet/latest/workspace/cli/) — every command and option
- [Installation walkthrough](https://libtmux.org/en/dotnet/latest/workspace/guides/installation/) — install, load, and capture on a private socket
- [Inspect a loaded session through MCP](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Mcp/README.md) — let an assistant read your panes
- [LibTmux.Workspace](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Workspace/README.md) — build sessions from C# instead
- [Changelog](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)
