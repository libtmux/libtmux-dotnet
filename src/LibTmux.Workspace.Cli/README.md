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

Save this as `workspace.yaml`: session `example` with an `editor` window
split `main-vertical` between `vim` and `npm test`, and a `docs` window
whose pane starts in `docs` and runs `mkdocs serve`.

```yaml
session_name: example
windows:
  - window_name: editor
    layout: main-vertical
    panes:
      - vim
      - npm test
  - window_name: docs
    panes:
      - start_directory: docs
        shell_command: mkdocs serve
```

Load it on a named socket without attaching:

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

## Inspect a workspace through MCP

Loaded workspaces are ordinary tmux sessions. The separate
[LibTmux.Mcp tool](../LibTmux.Mcp/README.md#point-a-client-at-it) can inspect
them when both commands select the same socket.

After the detached load above, configure your MCP client to launch
`libtmux-mcp` with `LIBTMUX_SOCKET=workspace-example` and
`LIBTMUX_TOOLSETS=inspect` in its environment. When loading with `-S`, set
`LIBTMUX_SOCKET_PATH` to that absolute path instead of `LIBTMUX_SOCKET`.
For named sockets, give both processes the same `TMUX_TMPDIR`; if selecting a
tmux executable explicitly, give both the same `LIBTMUX_TMUX`.

Discover tools with `tools/list`, then call `list_sessions`, `list_windows`
with its `session` argument, and `list_panes`. Retain the returned stable IDs.
Use `capture_pane` with `paneId` and a bounded `maxLines` for visible text.
Captures return projected lines with trailing empty rows removed;
`snapshot_pane` also reports cursor and pane state. `capture_since` first
establishes a cursor without returning text; pass that cursor back to read
new output.

Use `wait_for_text` with `paneId`, regular-expression `patterns`, and bounded
`timeoutSeconds` to wait for new output. Other inspections remain responsive
while the wait is pending. `tmux://capabilities` reports the selected endpoint
and effective tools. Closing the MCP connection cancels pending work and
leaves this separately loaded tmux session running. See the
[MCP tool reference](../../docs/mcp/tools.md) for exact schemas.

This tool does not use `LibTmux.Workspace`, the workspace library in the same
repository. The two are separate implementations: this one reads a wider
document language and refuses a rejected layout where the library records it
and carries on. That package's README names every difference.

## Commands and output

`load`, `freeze`, `convert`, `import teamocil`, `import tmuxinator`, `ls`, `search`, `edit`, `debug-info` and `shell` accept inherited `--json` and `--ndjson`. NDJSON wins when both flags are present. Explicit `--help` prints human help, and `--` ends option scanning, so a workspace file named `-h` is loaded rather than treated as a help request. Machine diagnostics are JSON lines on stderr.

Machine load requires `-d` or an explicit `--append` inside tmux. A session that already exists is reused when it holds every window the document declares, and refused as `session_mismatch` when it does not; reuse never rebuilds. A load that created the session removes it on a known failure -- tmux refused, a script exited non-zero, a document was wrong -- so `status` is `error`, exit 1, and nothing is retained. Cancellation (SIGINT/SIGTERM, exit 130) is not a known failure and leaves the session standing, so a cleanup path racing the same signal cannot destroy what a user could otherwise see and remove. A load that appended keeps what it added, names those windows, and reports `partial`. With several inputs the envelope answers for what each one retained, so one input building and another failing is `partial`.

Load creates panes in configuration order, including windows with three or
more panes. `pane-base-index` changes their starting index; explicit focus
still selects the configured pane.

A window with no `layout` key is tiled, not stacked: tmuxp halves the last
pane repeatedly, giving four panes of 14, 6, 3 and 3 rows at 100x30, where
this tool gives a 2x2 grid; that is a deliberate difference from tmuxp.
Without an explicit `focus` key the pane left active is the last one created,
as tmuxp leaves it, while the window left active is the first, where tmuxp
leaves the last. An explicit `focus` key agrees everywhere, windows and panes
alike.

Every input layout is checked before scripts or topology changes. Custom layouts require a valid checksum, a bounded cell tree, and enough cells for the configured panes. tmux still handles geometry and trims extra cells. Named layouts accept native unique prefixes; `main-h` and `main-v` become ambiguous when mirrored layouts are available on tmux 3.5 and newer. Version-sensitive names use the selected daemon version, with client-version fallback only when that endpoint has no running server.

Native append authenticates the inherited pane's daemon, resolves its current
session through tmux, and retains that session across all inputs. Later native
commands reject a replacement daemon, including global options after a startup
script. The session suffix in `TMUX` does not select the destination. Append
with Python plugins or custom builders fails before building any input or
starting Python; use `-d` to load those extensions into a separate session.

Outside tmux, human attachment requires a foreground controlling terminal. Inside tmux it does not: a switch needs no terminal, so a `run-shell` key binding — `TMUX` set, no `TMUX_PANE` — still switches, picking tmux's most recently used client instead of a specific one. Prompting also needs a terminal; without one, or with `--yes`, a load proceeds as if the answer were yes. A workspace whose session already exists asks `Attach?` and leaves it untouched on `n`, inside or outside tmux, exiting 0: declining a prompt is not a failure, so a session that mismatches the document is never compared to it, and `session_mismatch` never fires for an input the prompt was declined on. With several inputs the prompt is only ever about the last one; an earlier input still builds normally. A new session asks `y` to switch, `n` to load detached, or `a` to append, inside tmux only. `-d` always builds detached, even with `--append`. `-y` refuses an ambiguous client choice. A client with independent `active-pane` focus on the invoking physical window prevents handoff; detached and append modes remain available. The invoking pane and selected daemon are authenticated before building; a `TMUX` that does not parse, a daemon other than the one it names, a `TMUX_PANE` that is not a pane of that daemon, and a pane no client is viewing are each refused as `usage`, exit 2, before anything is built, whether or not that daemon is already running.

Before handoff, the CLI flushes output and checks the selected client again. Client changes cause a late refusal; daemon replacement prevents attachment to a reused session ID. SIGINT and SIGTERM report cancellation. Late failures print recorded load results on stderr; those records describe completed work, not a fresh topology query. A client name can still be reused after the final client observation.

Attached Python extension handoff remains in development. Use `-d` or choose `n` to run those extensions detached; the effective detached choice is passed to Python.

Load supports `-2` for 256 colors. Legacy `-8` and `--88-colors` requests fail before reading workspace files or running tmux or Python because supported tmux versions do not support 88-color mode.

Machine freeze, conversion and import return the document without writing a guessed filename, inside the same `{schema_version, command, status, ...}` envelope under `--json` and `--ndjson` alike: `freeze` answers under `workspace`, conversion and import under `document`. A captured document carries `x-capture-lossy: true`, because it is valid input that replays process names rather than the original command lines. `--save-to` selects a file, `--workspace-format` selects YAML or JSON, and `--force` authorizes replacement. Files are written through a temporary file in the destination directory. Capture retains current topology, directories, window options and current command names; original command arguments, history, hooks and plugin state are not recoverable.

Freeze derives no filename of its own. Without `--save-to` it needs `--json`
or `--ndjson` and returns the document; a human capture with neither is a usage
refusal. A session name is data from a live server — tmux accepts a slash in
one — so it never selects where a capture lands.

Freeze reads the invoking pane from `TMUX_PANE` only when `TMUX` names the
selected endpoint, because pane identifiers are numbered per server. Against
another endpoint it captures that endpoint's only session, or asks for a
session name.

Imports validate the translated workspace before printing or saving it.
Teamocil command groups, window options and the first requested window/pane
focus are preserved. Tmuxinator window command arrays stay in one pane;
explicit pane lists create separate panes. `pre_window` groups run in each
pane, and window `pre` groups retain their conditional command ordering.
Synchronization preserves the source's before/after command timing.

Relative project roots use the import invocation directory. Tmuxinator window
roots then use that project root; Teamocil window roots use the invocation
directory. A missing session name defaults to the source filename stem.
Unsupported fields, including launcher hooks, project `pre`, Teamocil filters
or `clear`, and named tmuxinator pane titles, are refused before any destination
is written. Tmuxinator ERB templates are refused before output or overwrite,
because Tmuxinator expands them through Ruby before parsing and no native
reader does; expand them to YAML or JSON first, since generic conversion still
preserves the raw template text. Teamocil evaluates no templates, so the same
`<%` markup in a Teamocil source is ordinary text and is preserved literally.
Move unsupported behaviors into an explicit supported workspace workflow
before importing; pane commands cannot reproduce launcher lifecycle hooks.

`--color auto|always|never` controls human color. Nonempty `NO_COLOR` wins over forced color; machine formats disable color styling. Current .NET Console initialization can still prefix stdout on a PTY with keypad control bytes. Discovery uses `TMUXP_CONFIGDIR`, XDG configuration and the legacy tmuxp directory. `TMUXINATOR_CONFIG` selects the importer directory. `LIBTMUX_TMUX` can select an explicit tmux executable.

`--log-level debug|info|warning|error|critical` filters optional warnings and file records; the default is `warning`. Command failures remain visible at every level. On Linux, `load --log-file PATH` appends UTF-8 JSON lines. Select `info` for lifecycle records or `debug` to include script output. Relative paths use the invocation directory. New files allow only owner read/write; existing content and permissions are preserved. Directories, pipes, devices and symbolic links are rejected. Other platforms currently reject `--log-file` because the offset of `st_mode` in their `struct stat` is not verified here.

A log destination that cannot be opened fails before tmux or Python runs. A later file-write failure disables that log and reports one secondary diagnostic; workspace execution retains its own result, error or cancellation. Log output contains escaped data and receives no terminal colors. Python delegation leaves the log file under native ownership.

Human `load` shows event-driven progress on a stderr terminal with verified geometry. `--progress-format` selects `default`, `minimal`, `window`, `pane`, `verbose`, or a literal template such as `{session}: {session_pane_progress}`. Bare named tokens and `{{`/`}}` escapes are supported; other fields remain literal. Explicit flags override `TMUXP_PROGRESS_FORMAT` and `TMUXP_PROGRESS_LINES`; defaults are `default` and 3 lines. `--no-progress`, `TMUXP_PROGRESS=0`, `TERM=dumb`, machine output and redirected stderr disable drawing. Progress environment values are validated only when drawing is active.

`--progress-lines 0` forwards decoded script stdout/stderr to their original
destinations; positive values show a bounded tail of terminal streams, and `-1`
uses available terminal rows. Redirected stdout receives decoded script output
directly at every panel size. `NO_COLOR` removes styling while keeping terminal
updates. Pane counters advance after command delivery and configured delays;
opaque Python extensions show a generic activity label. Frames clear before
results, diagnostics and attachment. On terminal resize, the painted frame is
erased and drawing stops; raw output resumes. A platform whose window-size
request is not known here omits drawing.
The owned Console writers do not provide a hard deadline for terminal or
filesystem writes.

Search uses .NET regular expressions with a one-second match timeout. Basic patterns, field aliases and tmuxp search flags are supported; Python-specific regex syntax and some Unicode character classes differ. Invalid expressions return usage status 2, and so does a pattern that spends the match timeout, which names the pattern and offers `--fixed-strings`. `--word-regexp` bounds the whole pattern, so every branch of an alternation matches as a word.

Python shell code and workspace extensions require tmuxp **1.74.0**. Set `TMUX_WORKSPACE_PYTHON` to the compatible Python executable. Child stdout and stderr are drained concurrently; retained output is capped at 64 Ki characters per stream and truncation is explicit. Streaming output decodes UTF-8 with replacement for invalid bytes.

`--generate reference` exports Markdown, or command metadata with `--json`. `--generate man|bash|zsh|fish` exports a manual or completion definitions. Completion currently offers command and option words; contextual argument completion remains open.

## Error codes

Every machine diagnostic carries a `code`. Ten describe the workspace
operation and are shared with the other libtmux workspace ports:

`workspace_not_found`, `invalid_workspace`, `unsupported_key`,
`session_not_found`, `session_mismatch`, `tmux_unavailable`, `tmux_failed`,
`script_failed`, `destination_exists`, `usage`.

`usage` covers every refusal about how the command was invoked or about the
context it was invoked in, including a `TMUX` or `TMUX_PANE` that cannot be
honoured; those exit 2.

The rest report this tool's own plumbing rather than the workspace, and are
specific to this port: `output_failed`, `log_file_unsupported`,
`log_file_unavailable`, `log_file_write_failed`, `terminal_required`,
`terminal_unsupported`, `terminal_changed`, `input_required`, `input_closed`,
`input_failed`, `invalid_choice`, `confirmation_required`, `editor_required`,
`invalid_editor`, `executable_unavailable`, `unsupported_runtime`,
`unsupported_platform`, `bridge_failed`, `ambiguous_client`,
`independent_pane`, `client_changed`, `pane_changed`, `attach_failed`,
`stale_server`, `interrupted` and `internal_error`. Anything unhandled is
`internal_error`, exit 70; nothing reaches a user as a stack trace.

## Validation and benchmarks

The CLI tests target both supported .NET runtimes and use private tmux sockets. Python shell integration requires the pinned optional runtime. Test collections run sequentially. The installed-tool benchmark verifies every leaf, topology, directories and NDJSON framing before reporting timings. It compares against tmuxp 1.74.0 using the same fixture and subprocess timing boundaries; both freeze measurements write YAML files. Results include individual samples, median, range and standard deviation. `--reference-python` selects the pinned comparison runtime.

```console
$ python3 eng/workspace_cli_benchmark.py artifacts/tools/tmux-workspace \
    --samples 5 \
    --output artifacts/workspace-cli-benchmark.json
```
