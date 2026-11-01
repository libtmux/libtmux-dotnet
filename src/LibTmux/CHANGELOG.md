# Changelog

Notable changes to `LibTmux`.

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

- **A connection that chooses no socket passes tmux neither `-L` nor `-S`.**
  (#134)
  - Previous behaviour: `Server.Open()` and `ServerConnectionOptions.Default`
    passed `-L default` and never gave tmux `TMUX`, so they reached the default
    socket whatever pane the program ran in.
  - New behaviour: tmux picks the server. Inside a pane that is the one `TMUX`
    names, which is read once when the connection opens; otherwise it is the
    socket named `default` under `TMUX_TMPDIR`. A socket from
    `ServerConnectionOptions`, `LIBTMUX_SOCKET_PATH` or `LIBTMUX_SOCKET` still
    comes first, in that order, and the path wins over the name.
  - Reason: the Python `Server()` works this way, and a program that names no
    socket should reach the tmux its user is sitting in.
  - Recommended action: give a program a server of its own with `SocketName`,
    `SocketPath`, or `LIBTMUX_SOCKET` and `LIBTMUX_SOCKET_PATH`.
- **`LIBTMUX_SOCKET` names the socket; `LIBTMUX_SOCKET_NAME` selects nothing.**
  A program that set the old name now reaches the server tmux picks. (#134)
- `Window.SelectLayoutAsync` accepts a JSON layout from tmux 3.8, the first
  release that reads one, and the `layout-json` capability starts at 3.8. An
  older release is refused with `TmuxUnsupportedOnReleaseException` before
  anything is sent. Installed releases behave as before; upgrade to tmux 3.8
  to apply a JSON layout.

  The control transport runs `display-panes` without attaching a client from
  tmux 3.8, the release that made it a mode. (#122)

## [0.1.0] — 2026-10-25

No entries for `LibTmux`.

## [0.0.0-alpha.24] — 2026-10-25

### Changed

- **Release refusals now raise `TmuxUnsupportedOnReleaseException` instead of
  `ArgumentException` or `TmuxWindowException`.** (#110)
  - Previous behaviour: a raw `select-layout` that crashes tmux below 3.4 and a
    name with `:` or `.` on exactly 3.7 raised `ArgumentException`; a mirrored
    or JSON layout on a release without it raised `TmuxWindowException` from
    `Window.SelectLayoutAsync` and a batch, and `ArgumentException` from
    `Server.ValidateLayoutsAsync`.
  - New behaviour: all raise `TmuxUnsupportedOnReleaseException`, which has no
    `WindowId`; a batched refusal names the window in its message. A layout
    that is malformed or unknown, or an ambiguous preset prefix such as `main-h`
    on tmux 3.5 and newer, still raises `TmuxWindowException` or
    `ArgumentException`, and so do session names with `:` or `.` through the
    typed calls.
  - Reason: the same kind of refusal raised three types, and the manifest named
    one that held for the typed call only.
  - Recommended action: catch `TmuxUnsupportedOnReleaseException` where you
    caught `ArgumentException` or `TmuxWindowException` for these cases. Code
    that catches `TmuxVersionTooLowException` needs no change.
- `ActualVersion` is declared on `TmuxUnsupportedOnReleaseException` and
  inherited by `TmuxVersionTooLowException`. The `new-pane` refusals, the
  client-filter refusal and `TmuxVersion.EnsureAtLeast` now report
  `Dispatch == NotDispatched`, and the first two set `Command`. (#110)

### Added

- `TmuxUnsupportedOnReleaseException` is the one type for a command or feature
  that is unsafe or unavailable on the running tmux release. It carries
  `ActualVersion`, `SafeReleases`, `CapabilityId` and `Command`.
  `TmuxVersionTooLowException` derives from it. (#110)

## [0.0.0-alpha.23] — 2026-10-24

### Fixed

- `Pane.BreakAsync` reads the new window from the pane when tmux prints no
  identifier for a window's only pane. (#84)
- `IControlModeSession.SendAsync` now refuses the commands that the one-shot
  methods refuse, by throwing `TmuxVersionTooLowException`. An unnamed `break-pane` sent through a session held on
  exactly tmux 3.7 ended the server. (#84)
- A command word is now read the way tmux reads it, by alias, name or unique
  prefix, and a `;` list one command at a time, so `ru`, `br` and a command
  after a `;` can no longer slip past the checks below. An alias wins over a
  prefix that other commands share, so `set` and `show` read as `set-option`
  and `show-options`. (#84)

## [0.0.0-alpha.22] — 2026-10-19

### Added

- tmux 3.8 is supported and tested. A dead pane whose `pane_pid` is empty reads
  as pid 0 and keeps its identity, the `display-panes` command runs without a
  client, and the shared control client attaches with `new-layouts`. The JSON
  layout form and `display-panes` without a client are recognised from
  3.8-rc2, the first candidate that has them. (#83)

### Fixed

- `Server.IsAliveAsync` now throws `TmuxVersionTooLowException` for a daemon
  below the minimum instead of answering `false`. (#83)
- A pane wait on a daemon older than tmux 3.7 polls without
  `AllowPollingFallback`, because no control client is attached there. (#83)
- A text wait whose deadline timer fires early reports `TimedOut` only once the
  whole timeout has elapsed on the monotonic clock. (#83)

### Changed

- **The minimum tmux is now 3.3a, checked against the daemon.**
  - Previous behaviour: tmux 3.2a was supported, and the executable's version
    decided what was allowed.
  - New behaviour: the library asks the connected daemon for its version with
    `run-shell -C`, which fires no `after-*` or `command-error` hook, and
    throws `TmuxVersionTooLowException` with `Dispatch` set to `NotDispatched`
    for 3.3, 3.2a and older. A daemon older than tmux 3.2, which cannot run
    that check, is refused the same way. Every command list opens with a fence,
    so a daemon replaced by an older one is refused before any command runs.
  - Reason: 3.2a and the unpatched 3.3 are the releases whose differences the
    library worked around, and a client's version says nothing about the daemon.
  - Recommended action: upgrade tmux and restart the tmux server, or stay on
    0.0.0-alpha.21, the last release that supports tmux 3.2a. The exception
    message says so.

  (#83)
- `LibTmuxInfo.MinimumTmuxVersion` is 3.3a, and `Server.Version` is the version
  the handle was checked on. An absent endpoint takes its version from the
  executable that would start it, `kill-server` on one does nothing, and the
  explicit control client no longer starts a server. (#83)
- A tmux version is read only as `X.Y`, `X.Ya`, `X.Y-rc[N]` or `next-X.Y`.
  `3.4.1`, `3.4-dev`, `3.3a-openbsd` and `master` are refused with
  `TmuxVersionTooLowException`, whose `ActualVersion` is not valid and whose
  message names the minimum without calling the build too old. `Dispatch` is
  `NotDispatched`, and a failed version check is reported the same way. (#83)
- The library names its own control client `client-<pid>` from the process it
  started and learns its session from the attach, so no `list-clients` or
  `display-message` runs and no user `after-*` hook fires when a client attaches
  or a pane is watched. (#83)
- `TmuxVersion.DetectAsync` accepts the file name tmux 3.3a prints in place of
  `tmux`. (#83)

### Removed

- The 3.2a and 3.3 adaptations: `Server.ConfigureAccessAsync`, the prompt
  history methods, the prompt `Type` and `ExpandFormat`, the display-message
  target client and the popup appearance options no longer check the version
  and no longer throw `TmuxVersionTooLowException` or drop a flag with a
  warning, and an entity is read with one `display-message` on every release.
  (#83)

## [0.0.0-alpha.21] — 2026-10-11

### Fixed

- A `ServerMirror` on tmux below 3.7 no longer stops answering after it
  attaches: it sets `no-output` and `ignore-size` on `attach-session` instead
  of with `refresh-client` afterwards, which left queued output undrained on
  those versions. (#61, #59)
- A pane wait or run whose server exits while its watcher attaches reports
  the pane gone, not an attach failure. (#59)

### Changed

- One-shot commands now travel over one control client the library keeps on
  each server, shared by every handle on the socket, instead of starting a tmux
  process per command. A pane run starts 3 tmux processes where it started 16,
  and a two-window, three-pane workspace build starts 2 where it started 32
  (3 where it started 33 when the client attaches during the build).
  `wait-for`, `new-session`, the `kill-*` commands and the commands that open
  interface on a client still run on a process. The client is left out of
  `GetClientsAsync`, `GetAttachedSessionsAsync`, `Session.Attached` and queries
  over `session_attached`; a format of your own that reads `#{session_attached}`
  counts it. A wrapper around `TmuxBinaryPath` sees the client's launch, not
  each command. The client is used from tmux 3.7, judged by the running
  server's version; on older servers every command runs on a process as before,
  because before 3.7 attaching a control client can crash the server
  ([tmux e5a2a25fa](https://github.com/tmux/tmux/commit/e5a2a25fafb8ee107c230d8acad694f6b635f8bb)). See [one-shot](../../docs/modes/one-shot.md#how-a-call-reaches-tmux). (#59)

## [0.0.0-alpha.20] — 2026-10-04

### Added

- Inspect source-query plans before execution and use LINQ over matching
  entities with their captured snapshot. Discover supported fields and
  operators through `QueryFieldCatalog`. (#34)
- Inspect existing daemons without starting them through `Server.InspectAsync`
  and bind resource creation to the observed daemon generation. (#34)

### Fixed

- Native tmux calls preserve Unicode and tab-delimited output under the C
  locale and finish bounded client cleanup after cancellation. (#34)

### Changed

- **Pane runs enforce command, timeout and output bounds.** Commands accept
  at most 64 KiB of UTF-8 text, timeouts must be positive and at most one day,
  and output defaults to the newest 1,000 lines within 64 KiB. (#34)
- **Pane waits require control observation by default.** Enable polling
  fallback explicitly; results disclose output loss and uncertain anchors.
  Stop patterns take precedence at entry, and an already-exited program
  returns `PaneExited`. Triggered waits exclude old text and typed echoes. (#34)
- **Control notifications bound payload bytes as well as event count.** The
  payload default is 4 MiB; adjust `ControlModeEventBufferMaxBytes` and refresh
  state after reported loss. (#34)

## [0.0.0-alpha.19] — 2026-10-04

### Fixed

- **`Server.CreateOwnedAsync` refuses the default socket while a server is
  listening there.** It took that server over and stopped it when the scope
  was disposed. Give an owned server a socket of its own, or use
  `ConnectAsync` to attach without owning it. When tmux cannot say whether a
  server listens, such as on a socket it may not open, the call raises
  `TmuxCommandException`. (#53)
- `Server.CreateOwnedAsync` stops a server it started when it then fails or
  is cancelled; it left the server running with nothing to stop it. (#53)
- `OwnedServerScope.DisposeAsync` stops the server when called again after a
  failed attempt, and a call made while another is stopping it waits for that
  stop and its outcome; both returned at once. (#53)
- `ServerMirror` publishes a view only when something changed: client activity
  times and write counts, the saved cursor, window offsets and
  synchronized-output toggles no longer count. (#53)
- `ServerMirror` attaches a new control client when its client is killed
  without `%exit`, and tries a failed attach again; it ended instead. (#53)
- A typed filter comparing with a number beyond plus or minus 2^53 is checked
  locally only; tmux read it inexactly, so `Width < long.MaxValue` returned
  no panes. (#53)
- `ServerConnectionOptions.CommandTimeout` no longer ends a `WaitForAsync`
  wait or lock, or an `OpenWaitChannel` wait. tmux kept the ended client's
  place, so before tmux 3.7a a queued lock went to it for good. (#53)
- `RunShellRequest.Delay` keeps its fraction of a second; half a second ran
  at once. (#53)
- A failed command with the text `has-session` among its arguments, such as
  `send-keys -l has-session`, no longer reports tmux's error as its output.
  (#53)

### Changed

- **A pane wait on a pane tmux no longer has raises
  `TmuxObjectNotFoundException`, as `Pane.RunAsync` does.** It raised
  `TmuxCommandException` from `capture-pane`, where `TmuxPaneException` was
  documented. Catch `TmuxObjectNotFoundException` for a pane that is gone;
  `TmuxPaneException` still means its program had already exited. (#53)
- Pane waits read the pane through the control client they attach rather than
  starting tmux processes, so a wait for output already printed takes about
  half as long and allocates less than half as much. (#53)
- **Reading a control client's events while another reader is reading throws
  `InvalidOperationException`.** Two readers each received part of the
  stream. Open another control client to read independently. (#53)

## [0.0.0-alpha.18] — 2026-10-03

### Added

- `Server.SearchClientsAsync` lists the clients a raw tmux filter keeps. It
  needs tmux 3.4. (#51)
- `Pane.WaitForTextAsync` and `Pane.WaitUntilAsync` wait for pane output, or
  a condition over the visible rows, without polling. They end early when the
  pane's program exits or a full-screen program starts. (#51)
- `Pane.SendKeysAndWaitAsync` and `Pane.SendTextAndWaitAsync` send keys and
  wait for what the pane prints next. The earlier screen and the typed line's
  echo do not count. (#51)
- `Pane.RunAsync` runs a command at a POSIX shell prompt and returns its exit
  status and output. (#51)
- A control client keeps notifications through a flooding pane: a full buffer
  drops that pane's oldest output first, `TmuxEventsDroppedEvent.OnlyOutput`
  says no notification was lost, and tmux pauses the pane until the reader
  catches up. (#51)

### Fixed

- Disposing an `OwnedServerScope` returns once the tmux server process has
  exited, not when `kill-server` answers. (#51)

### Changed

- **`%pause` and `%continue` arrive as `TmuxPanePausedEvent` and
  `TmuxPaneContinuedEvent`, not `TmuxNotificationEvent`.** Match the typed
  events instead of the names `pause` and `continue`. (#51)
- `PaneObservation.WatchAsync` passes through the watched pane's pause and
  continue events, which bracket output it did not receive. (#51)

## [0.0.0-alpha.17] — 2026-09-27

### Fixed

- **Control mode no longer ends the session when a pane prints bytes that are
  not UTF-8.** tmux sends such bytes raw, and the reader threw
  `TmuxProtocolException`, which ended the session and every pending command.
  They now arrive as `\xNN`, as one-shot capture already reports them, and a
  `TmuxOutputEvent` keeps that text.
- `PaneObservation.WatchAsync` now ends for an already-gone pane or after a
  dropped departure notice, delivering buffered output before the pane-gone
  event. (#35)
- `IControlModeSession.Events` leaves the next retained event for another
  reader when a consumer stops after a dropped-events notification. (#35)

### Changed

- **`IControlModeSession.SendAsync` rejects direct foreground `run-shell`,
  `run` and their command-name prefixes before dispatch.** Unframed shell
  output can corrupt the control stream. Use `Server.RunShellAsync` for output
  and exit status where tmux returns them; tmux 3.3a and 3.4 may write output
  to pane view mode. Direct `run-shell -b` remains available. (#35)
- `PaneObservation.WatchAsync` now throws `NotSupportedException` after
  confirming pane departure on a custom control session without an event
  watermark. Use a core control session to preserve output order. (#35)

## [0.0.0-alpha.16] — 2026-09-26

### Fixed

- `Server.GetClientsAsync` no longer returns an empty list for a real
  failure. It caught every `LibTmuxException` and answered `[]`, the same
  result as the ordinary no-clients case, so a genuine tmux failure or an
  undiscovered server generation read as an empty client list. It now
  answers empty only when no clients are attached, and lets
  `IncompleteSnapshotException`, `InvalidOperationException` and
  `TmuxCommandException` propagate. (#26)
- `Server.KillSessionAsync` ends only the session with exactly that name. It
  sent the name as a bare target, which tmux matches as a prefix, so killing
  `doom` when only `doomsday` existed ended `doomsday` and reported success --
  taking the server down with it if that was its last session. A name no
  session carries now throws. (#26)
- `Server.AttachSessionAsync` attaches only to the session with exactly that
  name, for the same reason: a bare name attached to any session that merely
  started with it. A session id is still addressed as given. An explicit
  name is now validated the same way `Server.CreateSessionAsync` validates
  one -- previously passed to tmux unchecked. (#26)
- `TmuxVersion.Parse` reads a release candidate reported without a number,
  as tmux 3.8's `3.8-rc` is, where it threw. It ranks below any numbered
  candidate and below the release. (#26)
- `Server.CreateSessionAsync`, `Server.HasSessionAsync`,
  `Server.KillSessionAsync`, `Server.AttachSessionAsync` and renaming a
  session refuse a name holding a control character, as the workspace tool
  already did: such a name cannot round-trip through a document a person
  reads or types. (#26)

## [0.0.0-alpha.15] — 2026-09-20

### Added

- `Server.SnapshotMetadata` exposes acquisition depth, server generation,
  timestamps and elapsed time. (#33)
- `SendKeysRequest.ToCommands(pane)` supports chaining text and Enter together.
  `ExecuteAsync(pane, control)` sends both through a control session. (#33)
- `Pane.CurrentCommand` and `Pane.CurrentPath` expose captured values without
  tmux I/O. Uncaptured fields throw `IncompleteSnapshotException`. (#33)

### Fixed

- `Server.CaptureSnapshotAsync` preserves the captured root, parents and active
  children, including repeated window links in one session. Linked sessions
  appear once each. (#33)
- `Server.CaptureSnapshotAsync` rejects contradictory placements and child
  counts with `InconsistentSnapshotException`, without returning a partial
  snapshot. (#33)
- `CapturedRelation.OrEmpty()` prevents callers from modifying captured
  children through a collection cast. (#33)
- `Window.MoveAsync`, `LinkAsync` and `UnlinkAsync` reject a source index that
  now belongs to another window. Typed move and link commands retain this
  protection in chains and control sessions. (#33)
- `Window.MoveAsync` returns the affected placement after moves and renumbering,
  including repeated links and detached moves. Failed readback reports unknown
  dispatch state; do not retry the move. (#33)
- `SendKeysRequest.ExecuteAsync(pane)` sends the requested Enter after the
  text. (#33)
- Query `pane_command` binds to `Pane.CurrentCommand` on returned
  entities. (#33)

### Changed

- `Server.CaptureSnapshotAsync(SnapshotDepth.Server)` verifies the live server
  generation on every capture. All depths honor cancellation before returning
  a snapshot. (#33)
- **`SendKeysRequest.ToCommand(pane)` rejects text-plus-Enter requests.** Use
  `ToCommands(pane)` for both commands, or set `Enter = false` for a single
  command. (#33)
- **`WindowEntityKey` requires a window index as its third constructor
  argument.** Include the captured index when constructing or deconstructing
  a key to distinguish repeated links in one session. (#33)
- `Pane.SendTextAsync` and `SendKeysRequest` reject NUL text before sending any
  command. (#33)

## [0.0.0-alpha.14] — 2026-09-20

No entries for `LibTmux`.

## [0.0.0-alpha.13] — 2026-09-19

### Added

- `ServerConnectionOptions.Interceptor` lets an application inspect, replace or
  retry process-based tmux commands. (#28)
- `TmuxDiagnostics` exposes command tracing and duration measurements for
  OpenTelemetry subscribers. (#28)
- `ServerConnectionOptions.CommandTimeout` bounds each tmux command, including
  connection. It is unset by default. (#28)
- `MaxCapturedBytesPerStream` and `ControlModeEventBufferCapacity` provide
  opt-in limits for captured output and queued control events. (#28)
- `Pane.Left`, `Pane.Top` and `Window.Layout` expose captured geometry. (#28)
- `PaneObservation.WatchAsync` streams one pane's events and ends with
  `TmuxPaneGoneEvent` when the pane disappears. (#28)
- `ControlModeSubscriptions.SubscribeSessionAsync` subscribes to session format
  changes, including on tmux 3.8 and later. (#28)
- `PaneId`, `WindowId` and `SessionId` support .NET parsing interfaces and
  span-based parsing. (#28)

### Fixed

- `Server.GetSessionAsync`, `GetWindowAsync` and `GetPaneAsync` return scalar
  fields ready to read without a refresh. (#28)
- `RawFormatFields` prevents mutation of captured state through a dictionary
  cast. (#28)
- **Live listings now throw on read failure, including an absent daemon.** Empty
  results mean a successful read; command failures retain tmux's stderr. (#28)
- **Unreadable tmux replies now throw `LibTmuxException` subclasses with
  dispatch state.** Catch `TmuxProtocolException` for malformed replies. (#28)
- Snapshots preserve each linked-window placement; live window reads target the
  captured session and index. (#28)
- `Server.CreateOwnedAsync` returns a handle whose listings and accessors
  discover the server it started. (#28)
- `Server.Version` and `TmuxCapabilities` recognise rolling `next-X.Y` tmux
  builds. (#28)
- `Window.SelectLayoutAsync` accepts unambiguous preset prefixes and, on tmux
  3.8 and later, JSON layouts. (#28)
- `EnterControlModeAsync` requests JSON layout notifications on tmux 3.8 and
  later. (#28)
- Targeted display messages, command prompts, server access and prompt history
  operations accept tmux 3.3, where they became available. (#28)
- `Server.WaitForAsync` preserves progress for later lockers when a lock wait is
  cancelled. (#28)
- Caller text beginning with `-` is passed literally to tmux, including
  environment names and their readback. (#28)
- Command logs identify the socket used. (#28)

### Changed

- **Request and connection options use object initializers for optional
  values.** Keep required request arguments in constructors. (#28)
- **Requests build commands through `ToCommand`.** Replace static
  `TmuxChaining.ToCommand` calls with the request method. (#28)
- **Active-child properties return `CapturedValue`.** Use `Value`, `TryGetValue`
  or `OrNull` to distinguish captured children from unavailable state. (#28)
- **Scoped `GetWindowAsync` and `GetPaneAsync` throw when no match exists.** Use
  `FindWindowAsync` or `FindPaneAsync` for nullable results. (#28)
- **`Server.ThrowIfDeadAsync` replaces `RaiseIfDeadAsync`.** Update calls to the
  new name. (#28)
- **`TmuxWait` moves to the `LibTmux` namespace.** Update the namespace import.
  (#28)
- **Buffer and key operations move to `Server.Buffers` and `Server.Keys`.**
  Replace direct server-method calls with the corresponding table methods. (#28)

## [0.0.0-alpha.12] — 2026-09-12

### Added

- `Session.GetWindowAsync` accepts a `WindowId`. The string overload matches
  `window_id` or `window_name`, and two windows in one session can share a
  name; the typed overload matches the identifier alone. (#25)

### Fixed

- **`==` on `Server`, `Session`, `Window`, `Pane` and `Client` now compares
  identity rather than reference.** Equality for these has always been the
  server generation plus the typed ID, and `Equals` implemented it, but neither
  operator existed — so two handles read for the same tmux object answered
  `true` from `Equals` and `false` from `==`. All five now carry `==`, `!=` and
  `IEquatable<T>`. (#25)
- `SessionId`, `WindowId` and `PaneId` no longer throw when sorted. They are
  record structs, which carry equality and no ordering, so `OrderBy` and
  `Array.Sort` failed at run time with `InvalidOperationException` reporting
  only that two elements could not be compared. All three implement
  `IComparable<T>` and `<`, `<=`, `>`, `>=`, ordering by the number tmux
  issued. (#25)

### Changed

- **`TmuxOutputEvent.PaneId` is a `PaneId`, not a `string`.**

  - Previous behaviour: the property named `PaneId` was typed `string`, so
    matching pane output to a handle meant parsing the text back.
  - New behaviour: it is a `PaneId`. An `%output` line whose first word is not
    a pane identifier arrives as a `TmuxNotificationEvent`, named but unparsed,
    because a throwing parse in the pump faults the whole event stream rather
    than one event.
  - Reason: control mode is where output is correlated to a held handle, and it
    was the one place the typed model stopped.
  - Recommended action: drop any `PaneId.Parse` at the call site; compare with
    `pane.Id` directly.

  (#25)
- `PaneActivityHub` keys pane wakeups by `PaneId`. The map was keyed by text, so
  every `%output` event rendered the identifier back to a string to look one up
  — an allocation per chunk a pane wrote. `CaptureSignal` now returns `null` for
  text that is not a pane identifier, which sends the caller to the polling path
  rather than to a wait nothing will end. (#25)

## [0.0.0-alpha.11] — 2026-09-07

No entries for `LibTmux`.

## [0.0.0-alpha.10] — 2026-08-30

### Added

- `TmuxChain.Then(IEnumerable<TmuxCommand>)` accepts a sequence without
  requiring callers to unroll multi-command requests. (#18)
- `Server.OpenWaitChannel` returns a `TmuxWaitChannel` whose open wait
  survives repeated timed attempts and is withdrawn on disposal. `WaitAsync`
  returning `false` means the attempt expired without observing attributable
  completion; a racing signal may already have completed the wait. Concurrent
  disposal joins the same cleanup. (#18)
- tmux 3.7c joins the supported matrix on both target frameworks. Stable
  releases inside supported intervals no longer lose capabilities solely
  because their exact patch version is absent from the catalog. (#18)

### Changed

- **`IControlModeSession.SendAsync` now takes a `TmuxCommand` instead of a
  command-line string.** Construct or use a typed command; its arguments are
  copied and NUL is rejected. tmux refusals now throw
  `ControlModeCommandException` with the command, output, and error lines.
  (#18)
- **`NewWindowRequest.ToCommand` now takes the owning `Session` instead of a
  target string.** Pass the session so the command carries its server
  generation. (#18)
- **`ServerSnapshot`, the non-generic `CapturedRelation` factories,
  `SnapshotLookup`, `SnapshotCollectionExtensions`, `EnumConstant`,
  `InstantConstant`, and `LibTmux.Internal.TmuxCommandContext` are no longer
  public.** Use relations on materialized handles and LINQ `ToDictionary`; the
  removed query constants and internal context had no supported evaluation or
  construction path. (#18)
- `Pane.RefreshAsync`, `Window.RefreshAsync`, `Session.RefreshAsync`,
  environment resolution, and server identifier lookups read only the
  requested entity instead of listing its whole hierarchy level. Linked
  windows and panes remain scoped to the session that produced them. (#18)
- **`StaleServerGenerationException.Actual` is nullable.** Null-check it; a
  replacement generation cannot always be observed safely. (#18)

### Fixed

- **`Client.IsControlClient` and the `client.isControlClient` query field use
  tmux's `client_control_mode` value.** Control-mode clients are no longer
  reported as ordinary clients. (#18)
- Control-mode callers keep their own replies across concurrent commands,
  aliases, hooks, cancellation, and disposal. Malformed or truncated streams
  and pump failures now fault callers; request, output, pending-work, and
  disposal bounds prevent unbounded retention. (#18)
- Query evaluation preserves integer, null, typed-ID, relation-count, overload,
  and invariant-regex semantics. Cancellable matching and fail-closed
  structural limits apply before recursion, and trimmed or AOT consumers
  retain required metadata. (#18)
- Identifier and relation lookups return handles with their owning `Server`, so
  their relations, options, and hooks work. A restarted endpoint is refused
  rather than rebound to a reused object identifier. (#18)
- Commands built from `Pane`, `Window`, `Session`, `TmuxOptions`, or
  `TmuxHooks` retain their originating server generation and are refused after
  a restart instead of acting on reused identifiers. (#18)
- `Pane.SendTextAsync` always sends literal text, so key-like words are not
  interpreted as tmux key actions. (#18)
- `Window.SelectLayoutAsync` rejects malformed custom layout prefixes before
  dispatch instead of handing them to older tmux parsers. (#18)

## [0.0.0-alpha.9] — 2026-08-22

### Added

- **An experimental query-only psmux preview for native Windows and WSL
  interop.** A separate analyzer-clean API reads one isolated session, its
  windows and panes, and pane text without exposing tmux lifecycle, mutation,
  chaining, control mode, or MCP behavior that psmux cannot preserve. The
  accepted client is the `psmux.exe` published in the psmux `v3.3.8` release:
  `PsmuxServer.SupportedBinarySha256` is that asset's SHA-256, and the commit
  and clean banner are pinned beside it. The caller supplies the executable;
  LibTmux never bundles, downloads, or searches `PATH` for one. The PowerShell
  harness owns a fresh data directory and refuses ambiguous cleanup. Native
  Windows and WSL runtime verification runs as a release gate. See [the exact
  trust and compatibility boundary](../../docs/psmux.md).
- **Explicit control-stream loss reporting.** A bounded event reader now gets a
  `TmuxEventsDroppedEvent` with per-report and cumulative counts instead of
  silently missing notifications when it falls behind.

## [0.0.0-alpha.8] — 2026-08-22

No entries for `LibTmux`.

## [0.0.0-alpha.7] — 2026-08-16

No entries for `LibTmux`.

## [0.0.0-alpha.6] — 2026-08-16

No entries for `LibTmux`.

## [0.0.0-alpha.5] — 2026-08-16

### Added

- **`LIBTMUX_SOCKET_NAME` and `LIBTMUX_SOCKET_PATH` point a connection that
  named no socket.** A harness, sandbox or container can move every
  unqualified connection at once without the call sites in between naming a
  socket they should not have to know about. A caller who named one — by path,
  by name, or by factory — is never redirected, and between the two variables
  the path wins. Adds no public member: the existing resolution path reads them
  where it already reads `TMUX_TMPDIR`.

### Fixed

- **A control-mode command that ends the client no longer shifts every later
  reply by one.** The waiter is queued before the write, so the exit sweep
  finds it; a write that then fails marks the slot abandoned rather than
  leaving one for a command tmux never saw.
- **Disposing a control session no longer kills the tmux server.** Only the
  client is killed, so other clients — and other sessions on the same socket —
  survive.
- Control mode survives a consumer that stops reading its event stream.
- A chain refuses a target read from a server that has since restarted, rather
  than aiming a reused id at a different object.
- A query document is treated as input rather than as instructions: an unknown
  field name is refused against the closed catalog instead of resolving to any
  public property.

## [0.0.0-alpha.4] — 2026-08-16

### Added

- **`TmuxDispatchState` on every failure**, so a caller can tell a safe retry
  from one that repeats a side effect. `NotDispatched` is claimed only where the
  library can see that no tmux process ran; `TmuxCommandException` is always
  `Dispatched` because it exists because tmux answered; the default is `Unknown`,
  since a client that died mid-command may have been obeyed first. Reads as an
  exception filter:
  `catch (LibTmuxException e) when (e.Dispatch == TmuxDispatchState.NotDispatched)`.

## [0.0.0-alpha.3] — 2026-08-16

No entries for `LibTmux`.

## [0.0.0-alpha.2] — 2026-08-15

### Fixed

- **A filter can be written over the objects the library hands back.**
  `sessions.Matching(s => s.Name.StartsWith("build"))` now translates; before,
  only a projection row whose properties were spelled like tmux fields did. The
  field catalog now carries the CLR property for each of the twelve queryable
  fields, in both directions.
- **net8.0 consumers take an 8.0 logging abstraction** rather than a 10.0 one,
  so the package no longer pulls a newer runtime into an 8.0 application.
- `Server.GetSessionsAsync` no longer returns an empty list when the endpoint
  reported no tmux version; that failure now surfaces instead of reading as
  "no sessions".

## [0.0.0-alpha.1] — 2026-08-15

- `LibTmux` — the client. Servers, sessions, windows, panes, clients, options,
  hooks and buffers, typed and asynchronous, against tmux 3.2a through 3.7b on
  net8.0 and net10.0. One dependency.

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
