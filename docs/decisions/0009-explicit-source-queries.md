# Explicit source queries

Status: accepted. Supersedes the production source-evaluation rejection in
[0003](0003-query-bakeoff.md); its framing and equivalence findings still apply.

## Decision

`QueryDocument.Plan<T>` is pure and requires an observed daemon version.
`QueryPlan<T>.ExecuteAsync` performs inspection, generation-guarded acquisition
and evaluation. Plans are immutable descriptions, not lazy collections.
`QueryResult<T>` implements `IReadOnlyList<T>` and owns the complete captured
`Snapshot`, including when there are no matches.

Source evaluation appends one private Boolean marker to the root list command's
existing framed projection. All rows are acquired. The graph therefore retains
unselected siblings, linked sessions and repeated session/index placements.
Markers stay row-aligned rather than joining a second observation by entity ID.
This reduces predicate work in the client, not transfer size; it does not use
`-f`. Reads still form an acquisition interval rather than an atomic snapshot.

## Equivalence boundary

The initial exact vocabulary is canonical typed-ID equality and inequality,
`session_attached` as nonzero, and the placement-qualified `window_active`.
A typed-ID literal must parse and round-trip exactly before it can enter a
format. No caller-controlled string, regex or field mapping enters that format.

Text remains local because tmux bytes, .NET invalid-UTF8 projection, empty values
and ordinal comparisons do not share one universal source representation.
Numbers remain local because tmux numeric formatting does not implement exact
Int64 comparison. Relations remain local over their complete captured graph.

Only a leading contiguous exact conjunction prefix can move to tmux. A later
false ID must not hide an earlier regex timeout, missing-field error or
cancellation. OR and NOT move only when their entire predicate is exact.
Boolean formats are balanced. The framed projection and the existing generation
guard share the 16,364-byte packed argv limit; overflow remains local in Auto
and fails planning in Require. Unknown or unverified daemon versions do the same.

## Acquisition and evaluation

Execution calls `Server.InspectAsync`, never an initializer. A different daemon
version fails before row acquisition; a different generation fails through the
existing command guard. The native guard disables server startup. Cancellation
is observed before dispatch, between reads, during graph construction, during
local evaluation and before publication. Invalid markers fail the whole read.

Residuals use the existing interpreter with native-only catalog bindings.
That path rejects reflection fallbacks and resolves relationship types through
the catalog; DTO compilation keeps its existing explicit trimming warning.
The public discovery API and the wire schema remain the same semantic owners.

## Alternatives

A `-f` filter would prune rows needed by local graph predicates, and a second
capture would join different observations. Either needs a separate equivalence
proof before replacing the marker strategy. A native raw-format escape hatch
already exists independently of portable source plans and does not establish
portable predicate equivalence.

## Verification

Runtime regressions cover planning order, canonical-ID validation, command
bounds and strict marker framing. Owned-daemon tests compare selections with
local interpretation of that execution's snapshot, check all three evaluation
modes, retain repeated placements and empty-result provenance, and read the
captured graph after daemon termination. Cancellation, corrupt markers and a
replacement daemon must prevent result publication.
