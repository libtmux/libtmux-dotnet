# ADR 0008: One current query schema

## Status

Accepted. Supersedes the version-one wire contract in
[ADR 0003](0003-query-bakeoff.md).

## Context

The query catalog now includes captured pane paths, linked sessions,
session-relative window state, and navigation through captured single
relations. Maintaining an older field subset would require parallel schemas,
version-specific validation, and producer configuration before the API has
downstream users that need them.

## Decision

Support only `libtmux-query` version 2. `QueryDocument.CurrentVersion` names
that contract, and `Translate` always produces it. Readers and writers reject
other versions before interpreting a predicate. There is no compatibility
mode or schema-version constructor parameter.

One catalog owns fields, accessors, value kinds, relation cardinality, and
capture depth. `libtmux-query-v2.schema.json` is the sole packaged schema; its
field manifest is checked against that catalog. `QueryJsonLimits.Default`
owns the document ceilings, which callers may tighten but cannot widen.

Local matching retains captured absence and availability semantics. Navigating
or filtering an uncaptured relation raises an error without tmux I/O. Filtering
membership leaves the underlying captured graph intact.

Historical bakeoff evidence remains unchanged. It records the contract that
was measured, and is not a production compatibility corpus. Current tests use
version 2 and separately reject obsolete and unknown versions.

## Consequences

Stored version-one documents must be rebuilt with the current translator.
The unstable API does not carry two schema implementations or a migration
adapter. Version numbers identify a contract; they do not imply that every
previous contract remains supported.

This decision does not change the separate native-filter boundary in ADR
0003. Typed documents still evaluate over captured objects, and
`UnsafeTmuxFilter` remains an explicit native escape hatch.
