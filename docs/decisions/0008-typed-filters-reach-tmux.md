# ADR 0008: Typed filters reach tmux

## Status

Accepted for the prerelease line. Supersedes two lines of
[ADR 0003](0003-query-bakeoff.md): that typed documents are not lowered into
native tmux filters, and that the version 1 catalog holds twelve fields.

## Context

ADR 0003 kept typed query documents local: a caller listed or captured
entities and filtered them with `Matching()`. A filter that keeps three panes
out of three hundred still read all three hundred, and a relation filter
captured every session to pane depth before discarding most of them.

tmux already evaluates a format filter on each row of a listing (`list-* -f`),
including comparisons, globs, and loops over a session's windows (`#{W:}`) and
a window's panes (`#{P:}`). The JVM ports push filters down the same way.

The version 1 catalog also stopped short of fields callers filter on: a pane's
size, position, edges, title and working directory, and a window's index and
size.

## Decision

A typed filter renders as a tmux format that keeps every row the filter keeps:
a superset, exact where tmux can evaluate the predicate exactly. tmux narrows
the listing, and every returned row is checked against the same document
locally, so the result is the local result whatever tmux kept. A predicate
tmux cannot express renders as no filter at all. A relation filter is
evaluated once per session, and only the sessions it keeps are captured.
Operands are escaped as format text, and `#[` is refused rather than
rewritten. A number tmux can leave unset, such as `pane_dead_status`, is
tested for emptiness first, because tmux reads empty as 0 where the local
check holds it unequal to every number.

Raw `UnsafeTmuxFilter` strings remain separate: tmux evaluates them and
nothing rechecks them.

The version 1 catalog grows during the prerelease line. It adds `pane_index`,
`pane_title`, `pane_current_path`, `pane_width`, `pane_height`, `pane_left`,
`pane_top`, `pane_at_top`, `pane_at_bottom`, `pane_at_left`, `pane_at_right`,
`pane_active`, `pane_dead`, `pane_in_mode`, `pane_pid`, `pane_synchronized`,
`window_index`, `window_width`, `window_height`, `window_active`,
`window_zoomed_flag`, `history_size`, `window_bell_flag`,
`window_activity_flag`, `window_silence_flag`, `pane_dead_status`,
`window_flags`, `window_layout`, `pane_tty` and `pane_start_command`. The
wire grammar stays closed: a reader rejects a name it does not know.

## Alternatives

**Keep documents local.** Correct and simple, and it reads everything every
time. It leaves callers writing raw filters for performance and losing the
typed checks.

**Push down only exact predicates.** Fewer moving parts, but most useful
filters combine an exact part with one tmux cannot evaluate, such as a .NET
regular expression. A superset lets the exact part narrow the listing anyway.

**A version 2 schema for the new fields.** Honest for a stable wire contract,
and the right move after 1.0. During alpha it would add version-dependent
validation for a change that only adds names, while every release already asks
for an exact version pin.

## Consequences

- A pushed-down listing can never answer differently from a local filter over
  the full listing. Differential integration tests compare the two on every
  supported tmux version, over names built to break escaping.
- Client listings are narrowed only from tmux 3.4, where `list-clients -f`
  exists; older tmux lists every client and the recheck filters them.
- A document written by a newer release can be rejected by an older reader.
  Pin the same version on both sides of a process boundary.
- At 1.0 the catalog is frozen per schema version, and a new field needs a new
  version.
