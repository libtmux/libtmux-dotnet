# ADR 0009: The F# facade covers idioms, not every operation

## Status

Accepted for the prerelease line. The session builder, typed chains and
per-handle timeout it declined are superseded by
[ADR 0010](0010-fsharp-builds-sessions-and-chains.md).

## Context

The JVM ports generate their Kotlin and Scala coverage from the Java
operation catalog: every Java operation gets a wrapper, and a staleness gate
fails the build when one is missing. `LibTmux.FSharp` is hand-written and
covers a small part of the core, which declares about 2,100 public API entries.

Every core member is already callable from F#. The core returns `Task`,
takes a `CancellationToken`, uses records with `init` properties for
requests, and raises exceptions that carry their dispatch state. What F# code
lacks without the facade is not reach but shape: a token-first, pipe-last
function; `option` for absence; a `Result` for cardinality; a cold stream;
active patterns over failures.

Several requests from the parity review are the same question asked of one
operation: typed command chains, a session builder, a per-call timeout like
the JVM's `within`, and a per-client event buffer capacity.

## Decision

Write the F# facade by hand, for operations where F# changes how the code
reads, and call the core directly for the rest. The facade covers listing and
filtering (`Query`), lookups (`option`), waits and runs, control streams and
the live mirror, typed options, failure patterns and safe retry.

Do not generate a wrapper per core operation. A generated `Pane.resize ct
request pane` adds a name to learn and a page to read for the same call as
`pane.ResizeAsync(request, ct)`, with nothing F# gains from it.

For the other requests:

- **Typed chains:** forty request types convert to commands with `ToCommand`,
  and `Server.Chain().Then(...)` composes them. F# uses that directly.
  Superseded: ADR 0010 adds the `Chain` module.
- **Session builder:** `LibTmux.Workspace` builds a session from a
  `WorkspaceFile` of windows and panes, which F# writes as nested lists.
  Superseded: ADR 0010 adds `SessionSpec` records and `Server.newSession`.
- **`within(timeout)`:** a token from `CancellationTokenSource(timeout)`
  bounds one call; `ServerConnectionOptions.CommandTimeout` bounds every
  command a handle sends, and two handles to one socket give two bounds.
  Superseded: ADR 0010 adds `Server.Within` and `Server.within`.
- **Per-client buffer capacity:** `ServerConnectionOptions.ControlModeEventBufferCapacity`
  is set per handle, so a client that needs more room is entered through a
  handle connected with more.

## Alternatives

**Generate coverage from the core, as the JVM ports do.** Complete by
construction and kept current by a gate. It doubles the surface a reader
scans for every operation while adding no F# shape to most of them, and the
generator, its gate and its docs become something else to maintain.

**Add F# wrappers for chains, building and timeouts.** Each is a few lines,
but each duplicates a core or workspace feature that F# already reaches, and
the duplication drifts.

## Consequences

- `docs/fsharp/interop.md` and `modes.md` show the core calls the facade
  does not wrap: configuration, hooks, formats, layouts and buffers. Chains
  and timeouts were here too until [ADR 0010](0010-fsharp-builds-sessions-and-chains.md)
  wrapped them.
- A new facade function needs an F# shape the core call lacks, stated in its
  commit.
- Revisit if F# users repeatedly write the same wrapper around a core call.
