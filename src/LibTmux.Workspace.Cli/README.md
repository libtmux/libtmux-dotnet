<!-- libtmux-logo -->
<div align="center">
  <picture>
    <source srcset="assets/logo.svg" type="image/svg+xml">
    <img src="assets/logo.png" width="128" height="128" alt="libtmux for .NET Workspace CLI">
  </picture>
</div>
<!-- /libtmux-logo -->

<!-- libtmux-logo -->
<div align="center">
<!-- /libtmux-logo -->

# tmux-workspace

[![LibTmux.Workspace.Cli](https://img.shields.io/nuget/v/LibTmux.Workspace.Cli?label=LibTmux.Workspace.Cli)](https://www.nuget.org/packages/LibTmux.Workspace.Cli)
[![libtmux.org](https://img.shields.io/badge/docs-libtmux.org-0b7285)](https://libtmux.org/en/csharp/latest/workspace/)
[![build](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet.yml)
[![tmux 3.3a – 3.8](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml/badge.svg)](https://github.com/libtmux/libtmux-dotnet/actions/workflows/dotnet-tmux.yml)

Describe a tmux session once, in YAML, and open it with one command.
[`tmux-workspace`][api-workspace-cli] reads
[tmuxp](https://github.com/tmux-python/tmuxp) workspace files, captures running
sessions back into them, and answers in JSON when a script asks.

<!-- libtmux-logo -->
</div>
<!-- /libtmux-logo -->

> **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
> The API is not settled: any release may change or remove exported identifiers,
> and the changelog marks each break. Pin an exact version to avoid one.

## Quick start

Install the tool, then save a workspace as `~/.tmuxp/myproject.yaml`:

```console
$ dotnet tool install --global LibTmux.Workspace.Cli
```

The file describes one session:

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

## Install

The quick start installs the tool globally. If your shell cannot find
`tmux-workspace` afterwards, add `$HOME/.dotnet/tools` to `PATH`.

Try it without installing anything, using the .NET 10 SDK:

```console
$ dnx LibTmux.Workspace.Cli --yes -- --help
```

Pin it for everyone who clones a repository. Run this at the repository root,
commit the `dotnet-tools.json` it writes, and run the tool as
`dotnet tmux-workspace`:

```console
$ dotnet tool install LibTmux.Workspace.Cli
```

Upgrade a global install:

```console
$ dotnet tool update --global LibTmux.Workspace.Cli
```

## Commands

### [`tmux-workspace load`][api-workspace-cli-load]: build a session from a workspace file

Load a workspace without attaching:

```console
$ tmux-workspace load -d myproject
```

Load the `.tmuxp.yaml` in a project directory:

```console
$ tmux-workspace load ~/code/myproject
```

Load a workspace on a tmux socket of its own, so it cannot touch the tmux you
are using:

```console
$ tmux-workspace load -d -L demo myproject
```

Attach to that socket with tmux:

```console
$ tmux -L demo attach
```

Load several workspaces at once; the last one is the one you land in:

```console
$ tmux-workspace load api web
```

Add a workspace's windows to the tmux session you are in:

```console
$ tmux-workspace load --append tools
```

### [`tmux-workspace ls`][api-workspace-cli-ls]: what it can find

List the workspaces it can find:

```console
$ tmux-workspace ls
```

Group the list by directory:

```console
$ tmux-workspace ls --tree
```

### [`tmux-workspace search`][api-workspace-cli-search]: find by name, window, or pane command

Find the workspaces that mention `server` in their name, windows, or pane
commands:

```console
$ tmux-workspace search server
```

### [`tmux-workspace freeze`][api-workspace-cli-freeze]: save a running session

Save a running session as a workspace file:

```console
$ tmux-workspace freeze myproject --save-to ~/.tmuxp/myproject.yaml
```

### [`tmux-workspace convert`][api-workspace-cli-convert]: YAML to JSON and back

Convert a workspace between YAML and JSON:

```console
$ tmux-workspace convert myproject.yaml --save-to myproject.json
```

### [`tmux-workspace import`][api-workspace-cli-import]: tmuxinator and teamocil projects

Turn a tmuxinator project into a workspace:

```console
$ tmux-workspace import tmuxinator ~/.config/tmuxinator/blog.yml \
    --save-to ~/.tmuxp/blog.yaml
```

Or a teamocil one:

```console
$ tmux-workspace import teamocil ~/.teamocil/blog.yml \
    --save-to ~/.tmuxp/blog.yaml
```

### [`tmux-workspace edit`][api-workspace-cli-edit]: open a workspace in `$EDITOR`

Open a workspace in `$EDITOR`:

```console
$ tmux-workspace edit myproject
```

### [`tmux-workspace debug-info`][api-workspace-cli-debug-info]: details for a bug report

Collect versions and search paths for a bug report:

```console
$ tmux-workspace debug-info
```

### [`tmux-workspace shell`][api-workspace-cli-shell]: a Python REPL on a session

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
[configuration reference](https://libtmux.org/en/csharp/latest/workspace/configuration/)
covers every key, and the
[example gallery](https://libtmux.org/en/csharp/latest/workspace/examples/gallery/)
has files to start from.

## Scripting

[`--json`][api-workspace-output] prints one JSON document on stdout. `--ndjson`
streams one record per line as a load progresses. Errors go to stderr as JSON
lines with a stable `code`.

List workspace names:

```console
$ tmux-workspace ls --json | jq -r '.workspaces[].name'
```

Load detached and read back which sessions were created:

```console
$ tmux-workspace load -d --json myproject | jq -r '.results[].session_name'
```

Inside tmux, a scripted `load` needs `-d` or `--append`, because a script cannot
answer the switch prompt. [`ls`][api-workspace-cli-ls] and
[`search`][api-workspace-cli-search] print the same JSON as tmuxp.

| Exit status | Meaning |
|---|---|
| 0 | Done |
| 1 | The operation failed; `status` and the error `code` say how |
| 2 | Usage: a bad option, or a command that cannot run where it was started |
| 70 | An internal error; please report it |
| 130 | Interrupted |

The
[output reference](https://libtmux.org/en/csharp/latest/workspace/reference/output/)
and
[error codes](https://libtmux.org/en/csharp/latest/workspace/reference/exit-codes/)
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

[`--generate man`][api-workspace-completion] writes a manual page.

## Differences from tmuxp

- A window with no `layout` is tiled. tmuxp keeps halving the last pane.
- Without a `focus` key the first window is left active. tmuxp leaves the last.
- `-8` is refused, because no supported tmux implements 88-color mode. Use `-2`
  for 256 colors.
- [`shell`][api-workspace-cli-shell], plugins, and custom workspace builders run
  through tmuxp itself and need tmuxp 1.74.0.

The [compatibility reference][api-workspace-compatibility] records what is
implemented and where it differs.

## What it leaves out

- **Old tmux.** A tmux below 3.3a, or one that reports a version no release
  uses, such as `master`, exits 1 with the `tmux_unsupported` code before any
  command runs. Upgrade tmux and restart the tmux server, or use an earlier CLI
  release.
- **88-color mode.** See `-8` above.
- **Starting a server on attach.** The attach that ends a load passes `-N`, so
  it never starts a server.

## Support and status

- **Releases.** Releases are numbered `0.1.N`, and each release increments `N`.
  The API is not settled: any release may change or remove exported identifiers,
  and the changelog marks each break. Pin an exact version to avoid one.
- **tmux.** 3.3a and newer.
- **.NET.** A .NET 8 or .NET 10 runtime.
- **Systems.** Linux, including WSL. CI also runs macOS arm64 and builds on
  Windows x64.
- **Workspace files.** tmuxp's format, in YAML or JSON.
- **Python bridge.** It runs the tmux the CLI uses: `LIBTMUX_TMUX` when set,
  otherwise the `tmux` on `PATH`.

## Documentation

- [Command reference](https://libtmux.org/en/csharp/latest/workspace/cli/) —
  every command and option
- [Installation walkthrough](https://libtmux.org/en/csharp/latest/workspace/guides/installation/)
  — install, load, and capture on a private socket
- [Inspect a loaded session through MCP](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Mcp/README.md)
  — let an assistant read your panes
- [LibTmux.Workspace](https://github.com/libtmux/libtmux-dotnet/blob/master/src/LibTmux.Workspace/README.md)
  — build sessions from C# instead
- [Source](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux.Workspace.Cli)
  — the package directory on GitHub
- [Changelog](https://github.com/libtmux/libtmux-dotnet/blob/master/CHANGELOG.md)

## License

[MIT](https://github.com/libtmux/libtmux-dotnet/blob/master/LICENSE)

[api-workspace-cli]: https://libtmux.org/en/csharp/latest/workspace/cli/
[api-workspace-cli-convert]: https://libtmux.org/en/csharp/latest/workspace/cli/convert/
[api-workspace-cli-debug-info]: https://libtmux.org/en/csharp/latest/workspace/cli/debug-info/
[api-workspace-cli-edit]: https://libtmux.org/en/csharp/latest/workspace/cli/edit/
[api-workspace-cli-freeze]: https://libtmux.org/en/csharp/latest/workspace/cli/freeze/
[api-workspace-cli-import]: https://libtmux.org/en/csharp/latest/workspace/cli/import/
[api-workspace-cli-load]: https://libtmux.org/en/csharp/latest/workspace/cli/load/
[api-workspace-cli-ls]: https://libtmux.org/en/csharp/latest/workspace/cli/ls/
[api-workspace-cli-search]: https://libtmux.org/en/csharp/latest/workspace/cli/search/
[api-workspace-cli-shell]: https://libtmux.org/en/csharp/latest/workspace/cli/shell/
[api-workspace-compatibility]: https://libtmux.org/en/csharp/latest/workspace/reference/compatibility/
[api-workspace-completion]: https://libtmux.org/en/csharp/latest/workspace/cli/completion/
[api-workspace-output]: https://libtmux.org/en/csharp/latest/workspace/reference/output/
