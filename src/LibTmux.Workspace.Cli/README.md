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

Load supports `-2` for 256 colors. Legacy `-8` and `--88-colors` requests fail before reading workspace files or running tmux or Python because supported tmux versions do not support 88-color mode.

Machine freeze, conversion and import return the document without writing a guessed filename. `--save-to` selects a file, `--workspace-format` selects YAML or JSON, and `--force` authorizes replacement. Files are written through a temporary file in the destination directory. Capture retains current topology, directories, window options and current command names; original command arguments, history, hooks and plugin state are not recoverable.

`--color auto|always|never` controls human color. Nonempty `NO_COLOR` wins over forced color; machine formats disable color styling. Current .NET Console initialization can still prefix stdout on a PTY with keypad control bytes. Discovery uses `TMUXP_CONFIGDIR`, XDG configuration and the legacy tmuxp directory. `TMUXINATOR_CONFIG` selects the importer directory. `LIBTMUX_TMUX` can select an explicit tmux executable.

`--log-level debug|info|warning|error|critical` filters optional warnings and file records; the default is `warning`. Command failures remain visible at every level. On Linux x64, `load --log-file PATH` appends UTF-8 JSON lines. Select `info` for lifecycle records or `debug` to include script output. Relative paths use the invocation directory. New files allow only owner read/write; existing content and permissions are preserved. Directories, pipes, devices and symbolic links are rejected. Other platforms currently reject `--log-file` because their native file layouts are not verified.

A log destination that cannot be opened fails before tmux or Python runs. A later file-write failure disables that log and reports one secondary diagnostic; workspace execution retains its own result, error or cancellation. Log output contains escaped data and receives no terminal colors. Python delegation leaves the log file under native ownership.

Human `load` shows event-driven progress on a stderr terminal with verified geometry on Linux x64. `--progress-format` selects `default`, `minimal`, `window`, `pane`, `verbose`, or a literal template such as `{session}: {session_pane_progress}`. Bare named tokens and `{{`/`}}` escapes are supported; other fields remain literal. Explicit flags override `TMUXP_PROGRESS_FORMAT` and `TMUXP_PROGRESS_LINES`; defaults are `default` and 3 lines. `--no-progress`, `TMUXP_PROGRESS=0`, `TERM=dumb`, machine output and redirected stderr disable drawing. Progress environment values are validated only when drawing is active.

`--progress-lines 0` forwards decoded script stdout/stderr to their original destinations; positive values show a bounded tail, and `-1` uses available terminal rows. `NO_COLOR` removes styling while keeping terminal updates. Pane counters advance after command delivery and configured delays; opaque Python extensions show a generic activity label. Frames clear before results, diagnostics and attachment. On terminal resize, drawing stops and the old frame remains; raw output resumes. Other platforms currently omit drawing. The owned Console writers do not provide a hard deadline for terminal or filesystem writes.

Search uses .NET regular expressions with a one-second match timeout. Basic patterns, field aliases and tmuxp search flags are supported; Python-specific regex syntax and some Unicode character classes differ. Invalid expressions return usage status 2.

Python shell code and workspace extensions require tmuxp **1.74.0**. Set `TMUX_WORKSPACE_PYTHON` to the compatible Python executable. Child stdout and stderr are drained concurrently; retained output is capped at 64 Ki characters per stream and truncation is explicit. Streaming output decodes UTF-8 with replacement for invalid bytes.

`--generate reference` exports Markdown, or command metadata with `--json`. `--generate man|bash|zsh|fish` exports a manual or completion definitions. Completion currently offers command and option words; contextual argument completion remains open.

## Validation and benchmarks

The CLI tests target both supported .NET runtimes and use private tmux sockets. Python shell integration requires the pinned optional runtime. Test collections run sequentially. The installed-tool benchmark verifies every leaf, topology, directories and NDJSON framing before reporting timings. It compares against tmuxp 1.74.0 using the same fixture and subprocess timing boundaries; both freeze measurements write YAML files. Results include individual samples, median, range and standard deviation. `--reference-python` selects the pinned comparison runtime.

```console
$ python3 eng/workspace_cli_benchmark.py artifacts/tools/tmux-workspace \
    --samples 5 \
    --output artifacts/workspace-cli-benchmark.json
```
