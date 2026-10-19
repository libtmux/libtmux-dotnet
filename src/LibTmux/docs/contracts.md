# LibTmux contracts

The rules behind the
[`LibTmux` README](https://github.com/libtmux/libtmux-dotnet/tree/master/src/LibTmux#readme),
in full. The README keeps what a caller acts on; this page keeps the exact
behavior, so a reader who needs the edge cases can find them without reading
them on the first screen.

## Which server a connect reaches

Options always win. A call that named a socket is never redirected by a
variable, which is what makes the variables safe to export for a whole process
— a test harness, a sandbox, a container — without auditing the call sites in
between. This library's own examples use exactly that: each one exports a
socket name of its own, so the connect above stays one line and still cannot
reach the server you are sitting in.

A pane's own server is a different question, and a different call:
[`Server.FromEnvironment()`][api-server-fromenvironment] reads the socket path
out of the `TMUX` variable tmux exports into every pane. `ConnectAsync` never
consults it.

Every call that reaches tmux is asynchronous and takes a `CancellationToken`.
There are no synchronous twins to choose between.

## Reading what is there

Live listings throw when the read fails, including when the daemon has stopped
or the socket is inaccessible. An empty list means the read succeeded and
matched nothing. Catch the relevant exception when absence is acceptable;
[`IsAliveAsync`][api-server-isaliveasync] is the explicit convenience that
returns `false` on library failures. Raw
[`ExecuteCommandAsync`][api-server-executecommandasync] keeps completed nonzero
exit codes in its [`TmuxCommandResult`][api-tmuxcommandresult].

### Inspecting without starting a server

`Server.Open(options).InspectAsync(ct)` reads an endpoint without running
`InitializeAsync` or starting a daemon. It returns null only for verified daemon
absence; permission and transport failures remain errors. The returned handle
exposes the observed [`DaemonVersion`][api-server-daemonversion] separately from
the client executable's `Version`. Its relationships remain uncaptured until an
explicit listing or snapshot. Inspecting a materialized handle rejects a
replacement daemon instead of adopting it.

Set
[`NewSessionRequest.ExpectedGeneration`][api-newsessionrequest-expectedgeneration]
to the inspected handle's [`Generation`][api-server-generation] when creation
must use that daemon. Direct creation and `ToCommand()` both refuse a
replacement; direct creation also verifies the generation during readback. A
missing daemon stays stopped, without loading its configuration. A failure after
creation reports that state may already have changed. The default remains
endpoint-scoped creation. `ExpectedGeneration` cannot be combined with
`ReplaceExisting`.

### Moving and linking windows

[`Window.MoveAsync`][api-window-moveasync], `LinkAsync` and `UnlinkAsync` verify
that the captured session/index still names the expected window immediately
before mutation in tmux's command queue. Reassigning that index cannot redirect
the operation to another window. Typed move and link commands retain this check
in chains and control mode; extracting raw argv with `ToArguments` does not
retain guards.

`Window.MoveAsync` returns the moved placement, including detached moves and
repeated links to the same window. A renumber request returns the original
placement at its new index while preserving link order. Renumbering another
session leaves the source placement unchanged. Move readback checks the
destination's index and window-ID map. If topology changes prevent a consistent
result after the mutation, the operation throws with unknown dispatch state; do
not retry it. These observations are separate tmux calls, so a concurrent change
can fall between them.

### Snapshots

Every depth performs a guarded read, including
[`SnapshotDepth.Server`][api-snapshotdepth-server] on an already connected
handle. [`SnapshotMetadata`][api-snapshotmetadata] records depth, daemon
generation, UTC start/end readings and monotonic elapsed time. A clock
adjustment can move the UTC end before the start; use `Elapsed` for duration.
Ordinary connected handles have no snapshot metadata.

Capture reads the hierarchy over an interval. Contradictory parent placements or
child counts throw
[`InconsistentSnapshotException`][api-inconsistentsnapshotexception] without
returning a partial graph or retrying. Equal-count changes can escape detection;
this is not an atomic snapshot. Unacquired relations remain unavailable, while
captured relations and metadata can be read without tmux I/O.

Captured children retain the same root through [`Server`][api-server]. Parent
and active-child properties return the captured instances when their depth was
acquired. A pane reached through a repeated window link returns that exact
window placement; filtering panes does not prune its parent's siblings or linked
sessions. Active children follow the IDs recorded in the corresponding parent
row, so selection changes during acquisition need not agree across separate
reads. An active ID missing from the acquired placement fails the capture
explicitly.

Recapturing creates a separate graph. `RefreshAsync` replaces just one entity's
fields; its child relations and materialized parents are not attached to an
older graph, even when its `Server` is an earlier captured root.

### Lookups and captured relations

[`GetSessionAsync`][api-server-getsessionasync], `GetWindowAsync`, and
`GetPaneAsync` return materialized objects. Their names, dimensions, indexes and
other scalar properties are local reads. `Get…Async` throws
[`TmuxObjectNotFoundException`][api-tmuxobjectnotfoundexception] for an absent
entity; `Find…Async` returns `null` only after a successful lookup finds no
match. Both preserve command, transport, cancellation and stale-generation
failures. Session and window lookups stay within their owner.

[`Session.ActiveWindow`][api-session-activewindow],
[`Session.ActivePane`][api-session-activepane] and
[`Window.ActivePane`][api-window-activepane] expose `CapturedValue<T>`, which
holds at most one child. Read `Value`, or `TryGetValue` when absence is
expected; `OrNull` answers null instead of throwing. A session reached through
an inactive window has that window's row, so its own active window may be
uncaptured, and reading `Value` then throws
[`IncompleteSnapshotException`][api-incompletesnapshotexception] rather than
reporting that there is none. `RefreshAsync` captures the entity's current
active child. `CaptureSnapshotAsync(SnapshotDepth.Panes)` also preserves the
captured parent and child graph. Reading any of these properties performs no
I/O.

### Migrating from identity-only lookups

- Remove a `RefreshAsync` used only to make a lookup's scalar properties
  readable. Keep it when current state is needed later.
- Replace a nullable [`Session.GetWindowAsync`][api-session-getwindowasync] or
  [`Window.GetPaneAsync`][api-window-getpaneasync] call with
  [`FindWindowAsync`][api-session-findwindowasync] or
  [`FindPaneAsync`][api-window-findpaneasync]. Required `Get…Async` calls throw
  on absence.
- Replace `session.ActiveWindow.Name` with
  `session.ActiveWindow.Value.Name` when the capture is known. Use
  `IsCaptured` when walking a partial hierarchy.
- Replace `RaiseIfDeadAsync` with `ThrowIfDeadAsync`. The failure behavior is
  unchanged; the old name is removed with no deprecation period, as in any alpha
  release.
- Catch listing failures where earlier releases returned an empty inventory.
  A stopped daemon is an error; an empty successful read remains an empty list.

## Running a command

`Output` is the pane's rendered text. It does not keep the command's stdout and
stderr byte for byte. Check `LinesMissed`, `AnchorLost`, and the omitted-output
counts before treating it as complete. For interactive programs,
[`SendTextAsync`][api-pane-sendtextasync] types input without assuming a shell
command ended.

If a completed run's private files cannot be deleted, `RunAsync` throws
[`LibTmuxException`][api-libtmuxexception]. Its
`Data["LibTmux.CompletedRunResult"]` retains the bounded
[`PaneRunResult`][api-panerunresult], and
`Data["LibTmux.RunDirectoryCleanupDirectory"]` names the owned directory to
inspect and remove. The command has already completed; do not run it again. A
cleanup failure during an earlier refusal stays on the original exception in
`Data["LibTmux.RunDirectoryCleanupFailure"]`. Failed temporary-buffer deletion
similarly preserves the original error and names the owned buffer in
`Data["LibTmux.PasteBufferCleanupBuffer"]`.

`SendTextAsync` types leading dashes, semicolons and newlines as input. NUL
is rejected before dispatch. Setting `enter: false` omits the extra Enter
key; it does not remove newlines already present in the text.

## Filtering

`Auto` evaluates an exact leading predicate in tmux and the remainder locally.
`Never` evaluates everything locally; `Require` rejects a plan needing local
predicate evaluation. Inspect `PushedPredicate`, `ResidualPredicate`,
`RequiredFields` and `RequiredSnapshotDepth` without I/O. Execution checks the
actual daemon version and generation before acquisition, and never initializes
or starts an absent daemon.

Source evaluation currently supports canonical ID equality,
[`Session.Attached`][api-session-attached] and
[`Window.IsActive`][api-window-isactive] on stable tmux 3.3a through 3.8. Text,
numbers and graph predicates use the local interpreter. Ordered conjunctions
preserve earlier local errors; partial disjunctions and negations stay local.

The source predicate travels in a private projection marker. It moves predicate
work into tmux, **without reducing rows or payload**. `result.Snapshot` retains
the complete graph at the required depth, even when the result is empty.
Repeated window placements remain separate, and acquisition is several tmux
calls, so a concurrent change can fall between them. Plans execute native
sessions, windows or panes; clients and projection DTOs remain local filtering
inputs.

Descriptors distinguish scalar paths such as `Panes.Count` from relation paths
such as `Panes` and `ActiveWindow.Value`. They report related targets,
cardinality, scalar nullability and minimum capture depth. Nested predicates use
[`QueryDocument.RequiredSnapshotDepth`][api-query-querydocument-requiredsnapshotdepth]
for the complete requirement. An uncaptured relation remains an error; it is not
empty or null. Clients have no hierarchy snapshot depth, and the schema-only
`client_id` has no entity binding.

A field outside the catalog throws
[`UnsupportedQueryExpressionException`][api-unsupportedqueryexpressionexception]
rather than falling back. Typed queries evaluate locally over captured objects
and are never assembled into tmux's executable format language.
[`UnsafeTmuxFilter`][api-unsafetmuxfilter] is the separate opt-in for native
tmux `-f` behavior.

## Failures and retries

`NotDispatched` is claimed only where the library can see that no tmux process
ran — a missing binary, or a command rejected before launch. A client that
started and then died is `Unknown`, because tmux may have acted before the pipe
broke, and `Unknown` is the default for exactly that reason. A
`TmuxCommandException` is always `Dispatched`: it exists because tmux answered.

## Tracing and metrics

The span is named for the subcommand and tagged `tmux.subcommand`,
`tmux.socket` and `tmux.exit_code`; a failure carries `error.type` and an error
status. `TmuxDiagnostics.CommandDurationInstrumentName` records elapsed seconds
under the same tags. Both cost nothing when nothing is listening.

## Wrapping every command

Call `next` once to pass through, again to retry, or not at all to answer in
tmux's place. It sees every client the connection starts for a command,
including the version probe, but not a control-mode client, which is one
long-lived process. A command against a pane or window arrives inside its
generation guard, so a stand-in has to answer that check too. Unset, nothing
wraps anything and dispatch is what it was; set, it is one delegate call around
each invocation.

## Sharing handles across threads

`Server`, [`Window`][api-window], [`Pane`][api-pane], [`Client`][api-client],
[`TmuxOptions`][api-tmuxoptions], [`TmuxHooks`][api-tmuxhooks],
[`TmuxEnvironment`][api-tmuxenvironment], [`TmuxChain`][api-tmuxchain] and
`CapturedRelation<T>` are immutable once constructed and safe to share freely,
including as a DI singleton. No public method mutates the handle it was called
on: `RefreshAsync`, [`RenameAsync`][api-window-renameasync], `ConnectAsync` and
`CaptureSnapshotAsync` each answer a new handle, and a stale handle stays a
correct record of what was read.

[`Session`][api-session] is safe to share on the same terms.
[`IControlModeSession.SendAsync`][api-icontrolmodesession-sendasync] is safe to
call concurrently — tmux answers in the order it received, and each caller gets
its own reply — while `Events` is a single-consumer stream and `DisposeAsync` is
idempotent from any thread.

Two limits are worth knowing before registering a singleton. A handle from
`ConnectAsync` pins the server generation it discovered, so after tmux restarts
its derived entities throw
[`StaleServerGenerationException`][api-staleservergenerationexception] rather
than silently addressing the new server; [`Server.Open`][api-server-open] defers
discovery to each call instead. And nothing serializes tmux itself: concurrent
callers reach one tmux server, which applies commands in the order it receives
them.

[api-client]: https://libtmux.org/en/csharp/latest/reference/libtmux-client/
[api-icontrolmodesession-sendasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-icontrolmodesession-sendasync/
[api-incompletesnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-incompletesnapshotexception/
[api-inconsistentsnapshotexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-inconsistentsnapshotexception/
[api-libtmuxexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-libtmuxexception/
[api-newsessionrequest-expectedgeneration]: https://libtmux.org/en/csharp/latest/reference/libtmux-newsessionrequest-expectedgeneration/
[api-pane]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane/
[api-pane-sendtextasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-pane-sendtextasync/
[api-panerunresult]: https://libtmux.org/en/csharp/latest/reference/libtmux-panerunresult/
[api-query-querydocument-requiredsnapshotdepth]: https://libtmux.org/en/csharp/latest/reference/libtmux-query-querydocument-requiredsnapshotdepth/
[api-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-server/
[api-server-daemonversion]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-daemonversion/
[api-server-executecommandasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-executecommandasync/
[api-server-fromenvironment]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-fromenvironment/
[api-server-generation]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-generation/
[api-server-getsessionasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-getsessionasync/
[api-server-isaliveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-isaliveasync/
[api-server-open]: https://libtmux.org/en/csharp/latest/reference/libtmux-server-open/
[api-session]: https://libtmux.org/en/csharp/latest/reference/libtmux-session/
[api-session-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activepane/
[api-session-activewindow]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-activewindow/
[api-session-attached]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-attached/
[api-session-findwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-findwindowasync/
[api-session-getwindowasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-session-getwindowasync/
[api-snapshotdepth-server]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotdepth-server/
[api-snapshotmetadata]: https://libtmux.org/en/csharp/latest/reference/libtmux-snapshotmetadata/
[api-staleservergenerationexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-staleservergenerationexception/
[api-tmuxchain]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxchain/
[api-tmuxcommandresult]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxcommandresult/
[api-tmuxenvironment]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxenvironment/
[api-tmuxhooks]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxhooks/
[api-tmuxobjectnotfoundexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxobjectnotfoundexception/
[api-tmuxoptions]: https://libtmux.org/en/csharp/latest/reference/libtmux-tmuxoptions/
[api-unsafetmuxfilter]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsafetmuxfilter/
[api-unsupportedqueryexpressionexception]: https://libtmux.org/en/csharp/latest/reference/libtmux-unsupportedqueryexpressionexception/
[api-window]: https://libtmux.org/en/csharp/latest/reference/libtmux-window/
[api-window-activepane]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-activepane/
[api-window-findpaneasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-findpaneasync/
[api-window-getpaneasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-getpaneasync/
[api-window-isactive]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-isactive/
[api-window-moveasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-moveasync/
[api-window-renameasync]: https://libtmux.org/en/csharp/latest/reference/libtmux-window-renameasync/
