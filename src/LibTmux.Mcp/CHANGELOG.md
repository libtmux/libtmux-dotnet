# Changelog

Notable changes to `LibTmux.Mcp`.

Releases are numbered `0.1.N`, and each release increments `N`. The API is not
settled: any release may change or remove exported identifiers, and this
changelog marks each break. Pin an exact version to avoid one. Releases before
0.1.0 were alphas numbered `0.0.0-alpha.N`.

The packages share one version, so every release since this package first
shipped has a section here. Changes that span packages, such as documentation,
CI, tests and release tooling, are in the [main changelog](../../CHANGELOG.md).
`0.0.0-alpha.11` and `0.0.0-alpha.13` were tagged but never published; the
changes under each shipped in the release after.

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

## [0.1.1] — 2026-11-01

### Changed

- **`libtmux-mcp` with no socket variable drives the server tmux picks, not a
  `libtmux-mcp` socket of its own.** (#134)
  - Previous behaviour: it started a daemon on the `libtmux-mcp` socket with the
    bundled configuration, gave it all four toolsets, and stopped it on exit.
  - New behaviour: it drives the server in `TMUX`, otherwise the default socket,
    loads no configuration of its own, omits `teardown`, and leaves every server
    running on exit. `tmux://capabilities` reports the selector `default` and
    the provenance `tmux-default`. A pinned socket keeps the bundled
    configuration.
  - Reason: the Python `libtmux-mcp` passes no socket unless one is set.
  - Recommended action: set `LIBTMUX_SOCKET` to keep the assistant on a server
    of its own. `LIBTMUX_SOCKET_NAME` on its own stops startup and names
    `LIBTMUX_SOCKET`.
- `libtmux-mcp` lets `LIBTMUX_SOCKET_PATH` win when `LIBTMUX_SOCKET` is also
  set, as the Python `libtmux-mcp` does. Startup used to refuse both at once.
  (#134)
- `libtmux-mcp` names `TMUX_TMPDIR` in the `attachCommand` of
  `tmux://capabilities` when the server is not yet running and the socket is a
  name or none, because that root decides where the socket is. (#134)

## [0.1.0] — 2026-10-25

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.24] — 2026-10-25

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.23] — 2026-10-24

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.22] — 2026-10-19

### Added

- `tmux://capabilities` reports the minimum tmux as
  `connection.minimumTmuxVersion`, and the MCP server instructions and
  `get_server_info` name it. (#83)

### Changed

- A `TmuxVersionTooLowException` reaches an MCP caller with the version found,
  the remedy and a note that nothing was sent, and stops the server at startup
  with the same text. A read tool reports it instead of an empty list, and the
  note points at `tmux://capabilities` for the minimum. (#83)

## [0.0.0-alpha.21] — 2026-10-11

### Fixed

- The MCP spawn tools say where a pane started when tmux could not use the
  requested directory by tmux's own rule, instead of an early read of
  `pane_current_path` that often came back empty and missed the fallback. On
  tmux 3.3 and 3.3a, which never fall back to HOME, the note names the
  server's working directory. (#59)

## [0.0.0-alpha.20] — 2026-10-04

### Fixed

- MCP waits forward progress notifications and cancel pending requests when
  the client closes stdin. (#34)

## [0.0.0-alpha.19] — 2026-10-04

### Added

- The MCP server's `list_panes`, `list_windows` and `snapshot_pane` advertise
  `anthropic/alwaysLoad`, so a client that defers tool schemas keeps them
  loaded. (#53)
- Every optional parameter the MCP server advertises says what omitting it
  does, such as acting on the pane the first session shows, or using the
  server's line and wait limits. (#53)

### Fixed

- An MCP tool called without `paneId` or `windowId` acts on the pane or
  window the first session shows. It took the first window's active pane,
  so in a session showing a later window, `send_keys` and
  `run_shell_command` typed into a pane nobody was looking at. (#53)
- The MCP server detaches every control client it attached when it shuts
  down. A client that had already failed stopped it detaching the rest. (#53)

## [0.0.0-alpha.18] — 2026-10-03

### Fixed

- The MCP server's `wait_for_text` reports `PaneDied` as soon as the pane's
  program exits, and reads the pane once more before it reports a timeout.
  (#51)
- The MCP server's `run_shell_command` checks a command that outlived its
  timeout every 5 seconds, not every 100 ms. (#51)

## [0.0.0-alpha.17] — 2026-09-27

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.16] — 2026-09-26

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.15] — 2026-09-20

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.14] — 2026-09-20

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.13] — 2026-09-19

### Added

- MCP `wait_for_text` reports `PresentAtEntry` when the wanted text was already
  visible before waiting. (#28)

### Fixed

- MCP `wait_for_text` masks known input echoes without hiding unrelated output.
  (#28)
- MCP session queries exclude their observation client from attachment counts.
  (#28)
- MCP `get_server_info` returns a null version when its server has exited. (#28)

### Changed

- **MCP `TmuxServerInfo.SocketName` now contains only a socket name.** Read the
  new `SocketPath` property for a path-selected server. (#28)

## [0.0.0-alpha.12] — 2026-09-12

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.11] — 2026-09-07

### Added

- `LibTmux.Mcp` advertises 45 tools, including 14 in the `manage` toolset, from
  one immutable capability registry. Every tool carries conservative protocol
  annotations. Its `_meta` capability object and the static
  `tmux://capabilities` resource carry matching non-protocol metadata for
  process reach, tmux effects, output classes, trust, and input literalization.
- `LIBTMUX_TOOLSETS`, `LIBTMUX_TOOLS`, and `LIBTMUX_EXCLUDE_TOOLS` freeze the
  effective surface at startup. All subsets of `inspect`, `manage`, `execute`,
  and `teardown` are valid; exact-name exclusions apply last.

### Fixed

- **`run_shell_command` reaches only the pane it names.** It sent its payload
  with `send-keys`, which tmux fans out to the synchronized cohort, and
  defended its singular exit status by refusing whenever the cohort was larger
  — advice a caller could not follow, and still a check against a later send.
  The payload now travels through a tmux buffer, which writes straight to the
  named pane, so the outcome is singular by construction and the refusal is
  gone. Under a process toggling `synchronize-panes` every 10-50ms, measured
  across 40 runs: 6 leaks before, none after, and 40 calls completed rather
  than 23.
- **A run reports what the command printed, not the prompt drawn after it.**
  The output window was bounded on the left only, by the marker the run prints
  before the command, so the shell's next prompt fell inside it. A prompt
  narrower than the pane was absorbed by the since-baseline diff and hid this;
  one carrying a deep path wraps into two rows and was reported as two lines of
  output the command never printed. The run now prints an end marker once the
  status is saved and reports only what lies between the two.
- **An empty identifier is refused rather than read as the current object.**
  Null still means the current pane, window or session; `""` used to mean the
  same, so `kill_window(windowId: "")` destroyed the active window and
  `send_keys(paneId: "")` typed into whatever pane was active anywhere on the
  server.
- **A direct call is held to the schema it published.** Every tool declares
  `additionalProperties: false`, which only the batch path enforced: the SDK
  dropped an undeclared argument, coerced `"5"` where an integer was declared,
  and let a wrong type reach the caller as System.Text.Json prose. Declared
  integers now carry their type's range, so a value beyond `Int32` is refused
  by the schema rather than by the binder.
- **Nesting cannot widen a selection.** `call_read_tools_batch` trimmed its
  declared authority by `LIBTMUX_EXCLUDE_TOOLS` alone, so naming the batch in
  `LIBTMUX_TOOLS` while the toolsets omitted `inspect` published a batch that
  still reached all sixteen inspect tools. A batch left with no authority is
  no longer published at all.
- **The synchronized cohort holds only the panes tmux would reach.** tmux gates
  delivery on more than `synchronize-panes`: a dead peer, a peer with input
  disabled, and every hidden pane of a zoomed window are skipped. A source that
  receives nothing is refused rather than reported as sent.
- **`show_option` sees the options a server was configured with.** It asked for
  values set at exactly the scope named, and `set -g` is where nearly all tmux
  configuration lives, so a configured server read as unconfigured. Inherited
  values are included and marked, and the scope in the result means where the
  read asked rather than where the value was set.
- **Refusals name what a caller can act on.** The three selection knobs are
  distinguished rather than all reported as a disabled toolset; the pattern
  subset is described as linear-time rather than as ".NET regular expressions"
  it then refuses; and the mutation warning no longer follows a command tmux
  said it refused, nor appears twice in one message.
- `wait_for_channel` answers with `signalled`, `elapsedSeconds` and
  `effectiveTimeoutSeconds`, so a timeout and a signal are told apart without
  matching prose. A misconfigured environment exits with its message and status
  1 instead of an abort and a stack trace. `swap_pane` refuses a pane and
  itself, `maxLines` below one is refused rather than answered with nothing,
  and `droppedBytes` documents that it counts pane text while the budget bounds
  the serialized result.
- **`show_environment` no longer returns values by default.** Omitting `name`
  answers every variable's name, `hasValue`, and `isRemoved` and no values.
  Naming one variable answers its value unless the name reads as a credential,
  which reports `withheld` instead. The tmux server environment is server-wide
  and outlives every session on the socket.
- **`isCaller` and `callerPaneId` are verified against the socket the pane
  belongs to.** tmux numbers panes per server, so `TMUX_PANE` alone marked an
  unrelated pane on the pinned socket as the terminal the conversation runs
  through, and left the real one unmarked. The "you are here" instruction
  segment answers to the same check.
- **`move_window` refuses `replaceExisting` unless `kill_window` is enabled.**
  tmux `move-window -k` kills whatever holds the destination index, which the
  default `manage` toolset could reach without the teardown gate. A move that
  does replace now says what it killed.
- **A running server holding no sessions answers instead of failing.** tmux
  refuses `list-windows -a` and `list-panes -a` with "no current target" when
  no session exists, which reached callers as raw command failures across
  eleven read tools. `get_server_info` reports zero counts and keeps the tmux
  version.
- Failure advice reads a tool's declared effects rather than the deliberately
  pessimistic `readOnlyHint`, so a failing read is no longer described as
  possibly having changed tmux. A refusal this server wrote keeps its own
  message instead of gaining the unexpected-failure backstop, and tmux's own
  wording is ended before the mutation warning is appended to it.
- The four spawning tools report where a pane actually started when tmux could
  not use the requested `startDirectory`; tmux falls back to `HOME` and then
  `/` without reporting an error.
- `set_history_limit` takes a session rather than a window, because tmux keeps
  `history-limit` at session scope; the previous window parameter silently
  changed every window in the session.
- The server instructions describe budget truncation and scrollback loss
  separately, because `droppedLines` reads 0 when scrollback discarded output.
- **MCP input refuses pane modes before mutation.** `send_keys` and each
  `send_keys_batch` operation check the named source: effective
  `pane_synchronized` `0` means source only, while `1` uses configured
  effective-on membership from inherited window settings and pane overrides.
  A modal cohort member refuses that operation. `paste_text` remains
  target-only because buffer paste does not fan out.
- **A failure reads the same whether it was called directly or inside
  `call_read_tools_batch`.** The batch dispatches its inner operations itself
  and never passed through the failure filter, so the same failure carried
  advice one way and a raw message the other. Argument validation is treated
  as the refusal it is, without the `(Parameter 'x')` suffix .NET appends.
- **Tool descriptions carry the guidance that tells overlapping tools apart.**
  Descriptions are built from the capability model, so the text written as
  `[Description]` on `ReadTools` and `WriteTools` was never advertised — the
  registered handlers are the `CapabilityTools` methods. `send_keys` reached
  clients as "Send keys." Registered descriptions now route callers between
  `send_keys` and `run_shell_command`, distinguish list tools from
  `search_panes`, explain what `respawn_pane` reruns and what
  `killExistingProcess` destroys, and state the modal-input boundary.
- A null arriving for a declared input field is answered as bad input rather
  than as an unexpected internal failure, and `wait_for_text` states that text
  already on screen never matches.
- Calling a tool whose toolset is not enabled says it was not selected instead
  of "Unknown tool", and `select_layout` no longer warns that tmux may have
  acted when it refused a layout name before sending anything.
- **Capability rows say only what the protocol cannot.** They carried the
  title, description, annotations and both schemas that a client already has
  from `tools/list` and joins to by name. `tmux://capabilities` drops from
  67,667 to 14,451 UTF-8 bytes of resource text with all four toolsets
  selected, and the raw JSON-RPC `tools/list` response drops from 132,914 to
  72,665 bytes. The duplicated output schema was also wrong:
  `structuredContent` is an object, so a tool answering a list has its array
  wrapped as `{"result": ...}`, and six rows described the bare array the
  server never sends.

### Changed

- **The MCP server now pins one socket and one configuration at startup.** Use
  `LIBTMUX_SOCKET` for a socket name, `LIBTMUX_SOCKET_PATH` for an absolute
  socket path, and `LIBTMUX_TMUX_CONFIG` for an explicit absolute tmux
  configuration path. The default is the product-dedicated `libtmux-mcp`
  socket with a minimal configuration. Replace the retired positional socket
  argument with `LIBTMUX_SOCKET`.
- **All MCP annotations are conservative.** Every advertised tool reports
  read-only false, destructive true, idempotent false, and open-world true;
  capability metadata carries the more precise process, effect, output, and
  input-literalization model. Internal input-sink classifications remain CI
  validation facts rather than public metadata.
- **CM-7 trust limits are explicit.** Tool filtering shapes the interface, not
  authorization; MCP annotations are consent metadata, not enforcement. A
  selected socket scopes tmux objects, not an OS sandbox, and execute tools run
  with the tmux user's authority.
- `set_synchronize_panes` reports that it amplifies subsequent input, and
  `send_keys` and `send_keys_batch` report preflight source or cohort
  membership, not actual delivery.
- Read batches retain every executed row while rolling back the newest nested
  payloads needed to keep the complete response within 1,000,000 bytes.
- Serialized MCP request IDs are capped at 524,288 bytes and rejected before
  tool dispatch, keeping invalid-request replies bounded.
- The default dedicated daemon is removed at MCP shutdown only when its
  authenticated launch marker still belongs to that process.
- Existing daemons report configuration provenance as unknown because tmux
  does not reload a supplied configuration path into an already-running server.

### Removed

- **`LIBTMUX_SAFETY` and ordered MCP safety tiers are removed.** Remove the
  variable and select unordered capabilities with `LIBTMUX_TOOLSETS`; merely
  defining the retired variable now stops startup with a migration error.
- **The MCP no longer advertises `enter_copy_mode` or `exit_copy_mode`.** Pane
  modes belong to the attached human. Use `capture_pane`, `search_panes`,
  `snapshot_pane`, and `capture_since` cursors to observe without entering,
  driving, or cancelling a mode; wait for the human to leave before sending
  input.
- **The prior MCP jobs, prompts, dynamic hierarchy resources, subscriptions,
  generic format expansion, server discovery, buffers, and server-wide kill
  routes are removed.** Use the pinned 45-tool surface and the static
  `tmux://capabilities` resource. The hierarchy, session, and pane URIs migrate
  to typed list and capture tools; `tmux://self` migrates to `get_server_info`
  and `list_panes`, while `tmux://servers` has no discovery replacement.

## [0.0.0-alpha.10] — 2026-08-30

### Changed

- **`JobStore` no longer implements `IDisposable`.** Dispose it with
  `await using` or `DisposeAsync`; containers that own one must also dispose
  asynchronously. (#18)
- **`tmux_wait_for_channel` moves from `readonly` to `mutating` and is marked
  destructive.** A readonly MCP server no longer advertises it because
  consuming a pending signal changes shared tmux state. (#18)

### Fixed

- `tmux_wait_for_channel` and `tmux_run` withdraw timed-out registrations, so
  retries no longer leave waiters that consume future signals. A signal racing
  withdrawal remains pending and is reported as unattributable. Failures that
  never registered preserve their original error without seeding a signal for
  the next caller. (#18)
- MCP shutdown asynchronously disposes factory-owned activity and job
  resources, withdraws waits held by unfinished jobs, and leaves caller-owned
  resources untouched. (#18)
- MCP hierarchy subscriptions invalidate on active-window and client-session
  changes, so existing subscribers receive the changed payload. (#18)

## [0.0.0-alpha.9] — 2026-08-22

### Changed

- MCP tail cursors are authenticated, bounded in size, and tied to the exact
  endpoint, server generation, and pane. Search and every serialized tool
  result now obey hard global line and UTF-8 byte ceilings.
- MCP Tasks admit at most eight active executions, retain a bounded result set,
  and apply only to waits and job collection.
- **`tmux_run` no longer answers as an MCP task.**

  - Previous behaviour: a task-capable client received a task handle and
    collected the result later.
  - New behaviour: the call blocks until the command exits, for every client.
  - Reason: an SDK task carries no durable tmux handle, so a client that
    disconnected lost work it believed was parked.
  - Recommended action: use `tmux_start_job` and `tmux_job`. tmux owns that
    handle, so it survives a disconnect.

### Fixed

- MCP background jobs retain their originating endpoint and generation, cap
  concurrent state, clean up failed starts, serialize collection, and drain
  watcher tasks during disposal instead of leaking or cross-routing work.

## [0.0.0-alpha.8] — 2026-08-22

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.7] — 2026-08-16

### Changed

- **`LibTmux.Mcp` is a different server.** It offered five tools; it now offers
  42, across three safety tiers, with four fixed `tmux://` resources, two
  resource templates and four workflow prompts. Every shaped tool answers a
  typed record with a JSON output schema; `tmux_display_message` returns the raw
  expanded format text it was asked for. The tool names all changed —
  `list_tmux` and friends are gone in favour of `tmux_hierarchy`, `tmux_run` and
  the rest. [The reference](../../docs/mcp/tools.md) is generated from the server, so
  it cannot describe a surface that is absent.

### Added

- **Waiting instead of polling.** `tmux_run` composes a private tmux rendezvous
  and a private pane option into the command, so "it finished" and "it exited
  1" are facts rather than readings of a prompt. `tmux_wait_for_text` subscribes
  to tmux's control-mode stream, so it sleeps until the pane prints rather than
  asking every few milliseconds. `tmux_start_job` and `tmux_job` carry work that
  outlives one call, which is what a build needs. `tmux_tail_pane` answers only
  what is new since its cursor, so the tenth read of a busy pane costs what the
  first did.
- **A budget on every content-bearing result.** Captures keep the newest lines,
  because a terminal's newest line is the one that says what happened, and
  report the lines and bytes they dropped — a reader who cannot see that lines
  are missing concludes the pane never printed them.
- **Three safety tiers, chosen by `LIBTMUX_SAFETY`.** A tool above the active
  tier is not registered, so it never reaches the model's list. An unrecognised
  value falls to `readonly` rather than to the default, because a typo must
  never widen what a server offers.
- **Subscriptions on both protocol revisions.** A client is told when the
  hierarchy changes, from tmux's own notifications rather than a timer. The
  2026-07-28 revision replaced `resources/subscribe` with a long-lived
  `subscriptions/listen` stream, and the SDK's built-in handling grants that
  subscription without telling the application — a client would subscribe
  successfully and then wait forever for events from a watcher nobody started.
  This server owns the stream instead, which means owning its contract: one
  acknowledgement before any event, every event tagged with the listen request
  so a client sharing one channel can tell streams apart, and staying up until
  the request is cancelled.
- **The Tasks extension, offered and never required.** A client that speaks it
  can start `tmux_run`, `tmux_wait_for_text`, `tmux_wait_for_channel` or
  `tmux_job` as a task and collect the result later. A client that does not
  keeps the blocking call it had. A listing stays a plain call: making it a
  task would cost a round trip to collect an answer that was already there.
- **Progress while a wait runs**, so a client showing a thirty second wait can
  tell it from a hung one.

### Fixed

- **A failure says what to do next.** An unhandled error reached a client as
  "An error occurred invoking 'tmux_run'", which is true and unusable: a model
  cannot tell a bug from its own bad argument, so it retries the same call. Each
  now names the cause and the next step, and a tmux server replaced underneath a
  cached handle is retried once rather than reported.
- **A socket with no tmux behind it is an answer, not an error.** That is the
  ordinary state before the first session and the first thing an assistant asks
  about.
- **Resolving a pane no longer walks the hierarchy.** It was a tmux process per
  session and per window to find something tmux answers in one call, paid on
  every tool call.
- **A run leaves nothing of its own on screen.** The shell echoes the
  rendezvous like anything typed, and it is wider than a pane, so tmux stores
  the wrap as a line break and the marker arrives split across rows. Rows are
  rejoined into the logical line they came from before matching, which is the
  only form the marker is whole in.

## [0.0.0-alpha.6] — 2026-08-16

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.5] — 2026-08-16

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.4] — 2026-08-16

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.3] — 2026-08-16

No entries for `LibTmux.Mcp`.

## [0.0.0-alpha.2] — 2026-08-15

### Fixed

- The MCP server reports its real version rather than `0.0.0.0`.

## [0.0.0-alpha.1] — 2026-08-15

- `LibTmux.Mcp` — a Model Context Protocol server, installed as a .NET tool.

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
[0.0.0-alpha.15]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.15
[0.0.0-alpha.14]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.14
[0.0.0-alpha.13]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.13
[0.0.0-alpha.12]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.12
[0.0.0-alpha.11]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.11
[0.0.0-alpha.10]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.10
[0.0.0-alpha.9]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.9
[0.0.0-alpha.8]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.8
[0.0.0-alpha.7]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.7
[0.0.0-alpha.6]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.6
[0.0.0-alpha.5]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.5
[0.0.0-alpha.4]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.4
[0.0.0-alpha.3]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.3
[0.0.0-alpha.2]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.2
[0.0.0-alpha.1]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.1
