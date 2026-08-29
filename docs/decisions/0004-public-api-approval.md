# ADR 0004: Approved C# public API

## Status

Accepted; implementation complete. The production plan and its phase validator
were transient execution scaffolding and were removed after closure. The
canonical API and parity documents retain the durable contract.

ADR 0006 supersedes this decision's exact capability-profile selection rule
and closed stable-version support boundary.

This decision originally approved names, signatures, ownership, package
placement, and parity destinations without claiming that production code or
behavioral evidence existed. That approval boundary is historical: production
is implemented, and current status belongs to the parity ledger and release
evidence.

## Context

The Python public inventory contains 626 source-grounded rows. Decisions 0001,
0002, and 0003 select the raw-byte process transport, immutable hierarchy, and
closed query catalog. The production port keeps one idiomatic .NET surface
rather than a transliteration of Python implementation details.

The canonical contract is `docs/public-api.json`.
`docs/public-api.md` is a deterministic human review generated from that
file. The parity ledger binds every Python row to an exact contract member, an
internal implementation member, or an explained exclusion and replacement.
Member IDs use the versioned project contract grammar recorded in the JSON;
they are not compiler XML documentation IDs. The project grammar keeps concise
C# source-form types, nullable markers, dotted explicit-interface names, and
parentheses on zero-argument methods.

## Decision

### Packages and namespaces

Publish two packages:

- `LibTmux` owns public values, immutable hierarchy handles, operations,
  snapshots, local query AST and evaluation, and the xUnit-independent
  `LibTmux.Testing` namespace.
- `LibTmux.Query.Json` depends on the same package version of `LibTmux` and
  owns only versioned `System.Text.Json` serialization. Its source-generated
  context remains internal behind the `QueryJson` facade.

The transport, generated field catalog, physical query mappings, capability
profiles, format materializer, and test-only PTY or control-mode scopes remain
internal. There is no public transport-abstractions package, `IQueryable`
surface, caller-defined physical field mapping, or contender vocabulary.
The generated format catalog exposes typed descriptors, ordered list-command
projections, minimum versions, and scope metadata internally. Materializers
receive an explicit owning-server context, and byte-length framing replaces
the Python delimiter protocol.

### Async and cancellation

Every process-backed operation is asynchronous. Its final parameter is an
optional `CancellationToken cancellationToken = default`. There are no
synchronous duplicates or sync-over-async wrappers. Interface-required
`DisposeAsync()` methods retain parameterless `ValueTask` signatures.

Raw execution returns an immutable `TmuxCommandResult` with logical arguments,
exit code, raw standard-output and standard-error bytes, and normalized line
views. Nonzero tmux exit is inspectable data at the raw boundary.

Process-backed entry points carry `[UnsupportedOSPlatform("windows")]`.
The separate `Psmux*` query facade is analyzer-clean because it exposes only
the pinned-client, bounded preview; it does not make the ordinary tmux entity
surface Windows-compatible. Portable IDs, snapshots, query translation and
interpretation, and JSON remain available on Windows.

### Hierarchy, identity, and ownership

`Server`, `Session`, `Window`, `Pane`, and `Client` are public sealed immutable
handles. They do not implement `IDisposable` or `IAsyncDisposable`; explicit
`KillAsync()` methods perform destructive lifecycle operations.

A root `Server` may represent an unmaterialized endpoint so callers can probe
or start a presently dead socket. `Server.Open()` and
`Server.FromEnvironment()` are portable and perform no I/O. In that state,
`Generation` and `Version` are `null` and `IsMaterialized` is `false`.
`ConnectAsync()` returns a materialized immutable replacement after successful
discovery. `StartServerAsync()` does not claim materialization because tmux may
exit immediately when the server has no sessions. Session, window, pane, and
client handles are always materialized.

Creation APIs also return clearly named owned scopes. Owned scopes implement
`IAsyncDisposable`, expose the created handle, use bounded non-caller cleanup,
are idempotent, and surface cleanup failure. `await using` applies only to an
owned scope, never a listed entity handle.

`SessionId`, `WindowId`, and `PaneId` are readonly record structs containing a
nonnegative integer. The valid default value is ID zero. Their constructors and
`Parse()` reject malformed, negative, null, or wrong-prefix text as applicable.
`TryParse()` returns `false` and assigns `default` for every invalid input.
`ToString()` restores `$`, `@`, or `%`.

Session, window, and pane identity combines the validated server generation
with a typed ID. Client identity combines generation with client name and does
not include TTY. Server connection equality is separate. Every targeted entity
operation combines generation validation and dispatch in one tmux command
list.

`ServerGeneration` requires positive process ID and start time values; its
default value is invalid at every live-handle boundary. Public enum constants
have explicit stable integral values. `TmuxVersion` normalizes its unavoidable
default struct value to an invalid, non-null representation exposed through
`IsValid`.

`TmuxVersion.versionContract` in the canonical JSON freezes the accepted tmux
token grammar, field projection, total ordering, parse failures, executable
detection, and support metadata. Parsing consumes the whole case-sensitive
token without trimming. Constructors and `Parse()` distinguish null
(`ArgumentNullException`) from malformed or overflowing input
(`FormatException`); `TryParse()` returns `false` and assigns `default`.
Ordered operations reject an invalid operand, while equality remains valid for
the unavoidable default struct value.

Detection accepts one successful `tmux -V` output line with the exact `tmux `
prefix and removes only its line terminator. A nonzero exit or nonempty stderr
throws `TmuxCommandException` carrying the result. Missing executables throw
`TmuxCommandNotFoundException`; other launch or read failures throw
`TmuxTransportException`. Pre-start caller cancellation remains
`OperationCanceledException`; post-start cancellation throws
`TmuxOperationCanceledException`, and cleanup failure throws
`TmuxCleanupException`. These mapped exceptions pass through without wrapping.
Malformed successful output remains `FormatException`.

`master` names the advisory matrix lane, not a version token; development
source reports `next-X.Y`. Minimum support is inclusive at 3.2a.
`MaximumTestedTmuxVersion` is 3.7b and is informational rather than a support
ceiling. Capability profiles select exact parsed identities, including
distinct 3.7, 3.7a, and 3.7b identities, and never fall back to the nearest
older profile. The Python `TMUX_MAX_VERSION` value 3.7 is therefore a semantic
source mapping to the highest required tested C# version, not an exactly
preserved constant.

Window handles are session-scoped views. Equal linked views retain their
distinct `SessionWindowEdge` values and relation order. Enumeration preserves
duplicate relation paths.

### Snapshots and collections

Entity properties are immutable captured state and never start I/O. Fresh
acquisition uses explicitly named asynchronous methods. Client attachment
resolution refreshes the client on every call; detached or missing clients
return `null`.

Session, window, pane, and client snapshots retain a copied
`RawFormatFields` map in addition to typed properties. This preserves tmux
fields that are unknown to the current catalog without exposing a second
object model.

`CapturedRelation<T>` implements `IReadOnlyList<T>` over copied data.
`IsCaptured` and `TryGetItems()` never throw. `GetItems()` and enumeration throw
`IncompleteSnapshotException` when the relation was not captured. Captured
empty and uncaptured are different states. Multi-command captures are
sequential observations, not transactions.

List error policy remains member-specific. Sessions, clients, and attached
sessions return captured empty snapshots for every underlying list-command
failure. Server-wide windows and panes suppress only missing daemon or socket
failures. Child traversal and native searches stay loud. Linked-session
discovery returns empty if either required listing fails. Cancellation and
programmer errors are never suppressed.

### Requests, options, hooks, and environment

Large tmux flag surfaces use immutable request records. Simple operations keep
ordinary parameters. Presence flags use `bool`; nullable Boolean values are
reserved for commands where omission and explicit false differ. Paired flags
use enums or validated request invariants.

tmux removed 88-colour support before the supported 3.2a–3.7b range.
`TmuxColorMode` therefore exposes only modes with valid tmux mappings; numeric
value 1 is reserved and `ServerConnectionOptions` rejects undefined values with
`ArgumentOutOfRangeException`.

Options, hooks, and environment operations are exposed through scoped service
objects on hierarchy handles. Option results preserve raw strings, `on`, `off`,
absence, and every sparse-array index while offering nullable Boolean and
integer projections. A point client lookup throws the typed missing-object
exception; `TryGet`-style and list operations retain their distinct nullable
or empty results.

### Errors

`LibTmuxException` is the root for remote tmux failures. Transport, command,
missing-object, unsupported-version, option, pane, window, and explicit cleanup
failures derive from it.

`TmuxOperationCanceledException` derives from `OperationCanceledException` and
retains the caller token, whether the command may have executed, and the client
process ID. It is used only after process start; pre-start cancellation remains
an ordinary `OperationCanceledException`. `TmuxCleanupException` retains the
original cancellation
and client process identity. `StaleServerGenerationException` and
`IncompleteSnapshotException` derive from `InvalidOperationException`.
`UnsupportedQueryExpressionException` derives from `NotSupportedException`.
Argument errors and BCL cardinality keep standard .NET exception types.

### Query and JSON

`Matching()` accepts `IEnumerable<T>` plus
`Expression<Func<T, bool>>` and returns an `IReadOnlyList<T>`. It translates to
one public immutable canonical AST and throws for unsupported input. It never
publishes `IQueryable`, silently evaluates an untranslated expression, or
compiles the caller expression as a fallback.

Cardinality uses only `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`,
`Any`, and `Count`. The only Python-style edge parser is
`name__contains`; other lookup spellings and `get()` remain explained parity
exclusions.

The core package owns `QueryDocument`, the closed node hierarchy, and tagged
constants. The JSON package consumes those public values and fixes the v1 wire
grammar. Native tmux filters use the distinct `UnsafeTmuxFilter` type and do not
claim typed-query equivalence. Safe planner filters and residual execution stay
internal.

### Testing and examples

`LibTmux.Testing` is xUnit-independent. It contains bounded deadline polling,
unique tmux-safe names, immutable test options and environment state, temporary
server/session/window/hierarchy scopes, and real-tmux context factories.
Factories accept either a new root endpoint or a caller-owned server or session
without taking ownership of the supplied parent. Deadline polling can return
`false` or throw on timeout, and collision-safe name helpers verify absence
against the selected live parent.

Pytest fixture names are excluded as framework-specific. The test-only
`ControlModeClientScope` and `PtyAttachedClientScope` remain integration-test
infrastructure because they change observable tmux state.

Documentation and executable examples must cover:

- connection and an explicitly owned session lifecycle;
- immutable replacement after mutation;
- snapshot capture, local query, and JSON round trip; and
- an isolated real-tmux test using the public testkit.

The supported required tmux range is 3.2a through 3.7b on `net8.0` and
`net10.0`. The development branch is advisory and unknown until an explicit
capability profile is approved. Native Windows tmux execution is unsupported;
portable APIs and pure tests remain supported.

## Consequences

Production follows the canonical JSON contract rather than inventing signatures
component by component. Each of the 18 production components owns an exhaustive
set of ledger rows, with implementation and evidence state updated only after
its behavioral and platform gates pass.

The immutable hierarchy requires callers to retain mutation results. In
exchange, captured state is safe for concurrent reads and I/O stays visible.
Named owned scopes add a small amount of ceremony but make destructive cleanup
intent explicit.

The closed query catalog remains internal and does not leak its implementation
details into consumer signatures. The two-package boundary keeps JSON optional
without introducing a second query model.

## Approval criteria at decision time

The public contract was accepted only while all of the following were true:

- deterministic Markdown rendering matches the canonical JSON;
- every approved or internalized parity row names an exact member;
- every exclusion states its reason and replacement;
- all 626 rows belong to exactly one component numbered 1 through 18;
- implementation and evidence were absent at this approval boundary;
- public member IDs and overloads are unique;
- async, cancellation, ownership, platform, query, ID, and exception rules
  validate mechanically; and
- the production plan owned every row once and named
  the full completion gates.
