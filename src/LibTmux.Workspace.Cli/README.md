# tmux-workspace

Manage tmux workspaces from YAML or JSON using tmuxp-compatible commands.

The tool supports .NET 8 and .NET 10 on Unix, with tmux 3.2a or newer. Python-specific shell and plugin behavior requires the optional tmuxp compatibility runtime. Native commands parse arguments and configuration before opening a tmux endpoint.

## Install from this checkout

Build the tool package with two build workers:

```console
$ dotnet pack src/LibTmux.Workspace.Cli/LibTmux.Workspace.Cli.csproj \
    --configuration Release \
    --output artifacts/packages \
    -m:2
```

Install the local package:

```console
$ dotnet tool install LibTmux.Workspace.Cli \
    --tool-path artifacts/tools \
    --add-source artifacts/packages \
    --prerelease
```

Load a configuration on a named socket without attaching:

```console
$ artifacts/tools/tmux-workspace load ./workspace.yaml \
    -d \
    -L workspace-example \
    --json
```

Capture that session using the same socket:

```console
$ artifacts/tools/tmux-workspace freeze example \
    -L workspace-example \
    --json
```

## Commands and output

`load`, `freeze`, `convert`, `import teamocil`, `import tmuxinator`, `ls`, `search`, `edit`, `debug-info` and `shell` accept inherited `--json` and `--ndjson`. NDJSON wins when both flags are present. Explicit `--help` prints human help. Machine diagnostics are JSON lines on stderr.

Machine load requires `-d` or an explicit `--append` inside tmux. Existing sessions are reused. An interrupted or failed load reports completed effects; it does not promise rollback. A failing startup script removes only the session created for that input.

Machine freeze, conversion and import return the document without writing a guessed filename. `--save-to` selects a file, `--workspace-format` selects YAML or JSON, and `--force` authorizes replacement. Files are written through a temporary file in the destination directory. Capture retains current topology, directories, window options and current command names; original command arguments, history, hooks and plugin state are not recoverable.

`--color auto|always|never` controls human color. Nonempty `NO_COLOR` wins over forced color; machine output has no terminal color escapes. Discovery uses `TMUXP_CONFIGDIR`, XDG configuration and the legacy tmuxp directory. `TMUXINATOR_CONFIG` selects the importer directory. `LIBTMUX_TMUX` can select an explicit tmux executable.

Search uses .NET regular expressions with a one-second match timeout. Basic patterns, field aliases and tmuxp search flags are supported; Python-specific regex syntax and some Unicode character classes differ. Invalid expressions return usage status 2.

Python shell code and workspace extensions require tmuxp **1.74.0**. Set `TMUX_WORKSPACE_PYTHON` to the compatible Python executable. Child stdout and stderr are drained concurrently; retained output is capped at 64 Ki characters per stream and truncation is explicit. Streaming output decodes UTF-8 with replacement for invalid bytes.

`--generate reference` exports Markdown, or command metadata with `--json`. `--generate man|bash|zsh|fish` exports a manual or completion definitions. Completion currently offers command and option words; contextual argument completion remains open.

## Validation and benchmarks

The CLI tests target both supported .NET runtimes and use private tmux sockets. Python shell integration requires the pinned optional runtime. Test collections run sequentially. The installed-tool benchmark verifies every leaf, topology, directories and NDJSON framing before reporting timings:

```console
$ python3 eng/workspace_cli_benchmark.py artifacts/tools/tmux-workspace \
    --samples 5 \
    --output artifacts/workspace-cli-benchmark.json
```
