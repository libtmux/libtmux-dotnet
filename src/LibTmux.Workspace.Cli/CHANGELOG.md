# Changelog

Notable changes to `LibTmux.Workspace.Cli`.

Releases are numbered `0.1.N`, and each release increments `N`. The API is not
settled: any release may change or remove exported identifiers, and this
changelog marks each break. Pin an exact version to avoid one. Releases before
0.1.0 were alphas numbered `0.0.0-alpha.N`.

The packages share one version, so every release since this package first
shipped has a section here. Changes that span packages, such as documentation,
CI, tests and release tooling, are in the [main changelog](../../CHANGELOG.md).

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

## [0.1.1] — 2026-11-01

### Changed

- `tmux-workspace` chooses its socket from `-S` and `-L`, then
  `LIBTMUX_SOCKET_PATH` and `LIBTMUX_SOCKET`, then whatever tmux picks. A `load`
  that may switch to or append to the current session passes the server in
  `TMUX` only when none of those chose one; it used to pass it ahead of the
  variables, and every other command used to pass it too. (#134)

## [0.1.0] — 2026-10-25

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.24] — 2026-10-25

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.23] — 2026-10-24

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.22] — 2026-10-19

### Added

- `LibTmux.Workspace.Cli` reports a tmux below the minimum, or one that names
  no release, with the `tmux_unsupported` code and exit status 1 from `load`,
  `freeze` and `shell`. `shell` and extension loads refuse before the Python
  bridge starts. The bridge runs `LIBTMUX_TMUX` when it is set, which it finds
  as `tmux`, and otherwise the `tmux` on `PATH`. (#83)

### Fixed

- The attach that ends `LibTmux.Workspace.Cli load` passes `-N`, so it never
  starts a server when the one it was given has gone. (#83)

## [0.0.0-alpha.21] — 2026-10-11

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.20] — 2026-10-04

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.19] — 2026-10-04

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.18] — 2026-10-03

No entries for `LibTmux.Workspace.Cli`.

## [0.0.0-alpha.17] — 2026-09-27

### Added

- `tmux-workspace --help` ends with examples for the command it describes,
  and every command has them. `--generate reference` gives each command a
  usage line, its arguments and options with their value names, and the same
  examples; `--generate man` writes NAME, SYNOPSIS, and a section per command.

### Fixed

- `tmux-workspace` no longer wraps its human output at 80 columns when stdout
  is not a terminal, which split a long path a pipeline was reading.
- A `tmux-workspace` prompt that has no terminal to ask on names the option
  that answers it — `--yes` or `--save-to` for a save, the session name for
  `freeze` — in its `input_required` error.

## [0.0.0-alpha.16] — 2026-09-26

### Added

- Ship `LibTmux.Workspace.Cli` as a .NET tool (`tmux-workspace`), the same
  framework-dependent tool package as `LibTmux.Mcp`. It discovers, loads,
  captures, converts and imports tmuxp workspaces, with `--json` and
  `--ndjson` machine output, terminal load progress and generated shell
  completion. `freeze` writes only where `--save-to` names it to, and
  otherwise returns the document in machine output. (#26)
- `load` resolves and authenticates its tmux context before building
  anything: an attached load outside the invoking server, one aimed at a
  `-L`/`-S` naming a different server, or an unparsable `TMUX` is refused
  `usage`, exit 2, with nothing created. See "Commands and output" in the
  CLI README for the exact refusal matrix. (#26)
- With stdin a terminal, `load` asks before creating, switching to or
  attaching a session, and `freeze`, `convert` and `import` ask before
  overwriting a file; `--save-to` and `--force` skip the prompt. Where this
  tool has no way to ask -- machine output, or no terminal at all --
  `confirmation_required`, `input_required` and `input_closed` report
  `usage`, exit 2, instead. (#26)
- **A `load` that fails removes only the session it created.** Cancellation
  (SIGINT/SIGTERM) is not a known failure and leaves the session standing,
  so a cleanup path racing the same signal cannot destroy what a user can
  see and remove. Reusing an existing session compares it against the
  document and reports `session_mismatch` when a declared window is
  missing; `results[]` carries one record per attempted input, each with a
  `reused` boolean. (#26)
- `load` creates a window's panes in configuration order -- including three
  or more panes -- and honors `pane-base-index` for their starting index,
  explicit window and pane focus, and tmuxp window options. It validates
  every custom and named layout before running a script or changing tmux
  topology, and resolves YAML merge keys and inert `x-` extension keys. See
  "Commands and output" in the CLI README for where it deliberately differs
  from tmuxp. (#26)
- `load` skips the pane-readiness wait when `workspace_builder_options:
  {pane_readiness: never}` is set, warns rather than refusing on an
  unrecognized `workspace_builder_options` key, and warns when a
  `start_directory` is not a directory, naming the path and tmux's `$HOME`
  fallback. (#26)
- `--append` honours a window's `window_index`, failing the load on a
  collision (`tmux_failed`, exit 1, naming the windows it kept); a session
  `load` creates is sized from the terminal it runs in
  (`TMUXP_DEFAULT_COLUMNS`/`ROWS`, else `COLUMNS`/`ROWS`, else 80x24),
  overridden by the real terminal size unless `TMUXP_DETECT_TERMINAL_SIZE`
  disables detection. (#26)
- `load`, `freeze`, `convert` and the importers share one machine-output
  envelope under `--json`/`--ndjson`, carrying `schema_version` and
  `status`; `freeze` answers under `workspace`, `convert` and `import`
  under `document`. Every code is `lower_snake_case`, grouped under "Error
  codes" in the CLI README; a refusal about how or where the command was
  invoked is `usage`, exit 2, and an unhandled exception is
  `internal_error`, exit 70. `--ndjson` additionally streams
  `window-completed`/`pane-completed` events and brackets a
  `before_script`'s output between `script-started` and `script-completed`.
  (#26)
- `freeze` marks the document it writes with `x-capture-lossy: true`,
  writes window options under `options_after` (`load` accepts either
  spelling), refuses a session name `load` would reject, and reports
  `session_not_found` against a socket whose server has not started. It
  reads the invoking pane from `TMUX_PANE` only when `TMUX` names the
  selected endpoint -- pane identifiers are numbered per server -- and
  otherwise captures that endpoint's only session or asks for one by name.
  (#26)
- `search` matches workspace names and configuration fields with .NET
  regular expressions under a one-second timeout; a pattern that fails to
  compile or spends the timeout is refused as `usage`, exit 2, naming the
  pattern and, for a timeout, offering `--fixed-strings`. `--word-regexp`
  bounds the whole pattern, so every branch of an alternation matches as a
  word. (#26)
- `shell` opens a Python REPL over the workspace API, selecting among
  `best`, `pdb`, `code`, `ptipython`, `ptpython`, `ipython` and `bpython`
  backends; it requires tmuxp **1.74.0**, selected through
  `TMUX_WORKSPACE_PYTHON`, and caps retained child output at 64 Ki
  characters per stream with truncation reported explicitly. (#26)
- `ls` discovers workspaces by walking up from the working directory for a
  local `.tmuxp.{yaml,yml,json}` and listing the global directory
  (`TMUXP_CONFIGDIR`, XDG configuration, or the legacy `~/.tmuxp`),
  excluding hidden files; a bare workspace name resolves the same way.
  `--tree` groups results by directory and `--full` includes window, pane
  and full configuration detail. (#26)
- `convert` and `import` report `invalid_workspace` for a document that
  exists but cannot be read, the same class of problem as one that cannot
  be parsed. `convert` quotes every string scalar a YAML 1.1 (PyYAML/tmuxp)
  or 1.2 resolver would otherwise read back as a boolean, null, integer or
  float. `import tmuxinator` refuses ERB templates before printing or
  saving a workspace, since Tmuxinator expands them through Ruby and no
  native reader does; `import teamocil` keeps the same markup as ordinary
  text, since Teamocil evaluates no templates. (#26)
- `import teamocil` preserves command groups, window options and the first
  requested window or pane focus. `import tmuxinator` keeps a window's
  command array in one pane unless the source lists explicit panes. Both
  preserve `pre_window` and window `pre` conditional command ordering and
  the source's before/after command synchronization. (#26)
- `edit` opens a workspace file in `$EDITOR` and reports its exit status,
  captured output and truncation under the shared machine envelope;
  `debug-info` reports the .NET runtime, platform, tmux version and the
  workspace configuration directories searched, masking `$HOME` in every
  reported path. (#26)
- Attachment and progress have Linux and macOS implementations. `--log-file`
  accepts Linux x64 and arm64. Progress and logging have been verified on
  Linux x64; other implemented platforms still need verification. (#26)
- `--generate reference` exports command metadata as Markdown, or as
  structured JSON under `--json`; `--generate man|bash|zsh|fish` exports a
  manual page or shell completion. Generated bash, zsh and fish completion
  scope each flag to its own subcommand, including nested ones (`import
  teamocil`/`import tmuxinator`); global flags (`--json`, `--ndjson`,
  `--color`) complete everywhere. (#26)
- Every command restores the application cursor-key and keypad mode
  (DECCKM, DECKPAM) that `System.Console` initialization enables on Unix,
  and `--color never` leaves a real terminal free of colour. (#26)
- `-d` always builds a new detached session, even with `--append`, matching
  tmuxp's precedence; pass `--append` alone to append. (#26)
- `load` accepts `-2` for 256 colors; legacy `-8`/`--88-colors` requests are
  refused (`unsupported-color-mode`, exit 2) before reading a workspace file
  or running tmux or Python, since every supported tmux version rejects
  88-color mode. (#26)

[Unreleased]: https://github.com/libtmux/libtmux-dotnet/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.1.1
[0.1.0]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.1.0
[0.0.0-alpha.24]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.24
[0.0.0-alpha.23]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.23
[0.0.0-alpha.22]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.22
[0.0.0-alpha.21]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.21
[0.0.0-alpha.20]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.20
[0.0.0-alpha.19]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.19
[0.0.0-alpha.18]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.18
[0.0.0-alpha.17]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.17
[0.0.0-alpha.16]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.16
