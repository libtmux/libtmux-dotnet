# ADR 0006: Stable capability intervals

## Status

Accepted for tmux version-dependent behavior.

This decision supersedes ADR 0004's exact capability-profile selection rule.
It does not change exact `TmuxVersion` identity or the required compatibility
matrix.

## Context

Minimum support is tmux 3.2a. `MaximumTestedTmuxVersion` is informational, not
a support ceiling. The earlier implementation nevertheless selected only eight
exact capability snapshots. Stable releases such as 3.3, 3.3 micro releases,
and 3.7c therefore appeared to carry no capabilities. Callers omitted valid
flags or rejected valid operations.

The cumulative snapshots also made a profile's version serve two meanings: the
server version for exact matches and an older provenance marker for any proposed
floor lookup. Whole-set copies hid the individual version where a behavior was
removed. A misspelled capability name read as ordinary absence.

## Decision

Represent each named capability by the first supported stable version and an
optional first unsupported stable version. Stable final, micro, and patch
releases at or above 3.2a are evaluated against those intervals. A capability
without a recorded end remains supported on later stable releases; this does
not infer capabilities that are absent from the ledger.

Invalid values, versions below 3.2a, development builds, release candidates,
and `next-*` builds have unknown capability state. Ordinary internal callers
treat unknown as unsupported. Unknown capability names throw, so spelling drift
does not silently disable behavior.

The psmux preview remains separate. Its exact binary attestation, typed facade,
and command allowlist define that surface; its numeric compatibility banner
does not widen it through the tmux capability model.

## Alternatives

Exact snapshots were rejected because they contradict the open-ended minimum
support contract.

Selecting the nearest older snapshot was smaller, but retained cumulative
copies, ambiguous profile identity, implicit removal handling, and silent name
misses.

## Evidence

Both contenders ran sequentially with five-CPU process affinity. Each passed a
zero-warning Release build and both unit target frameworks. Deliberately moving
the 3.3 boundary to 3.3a made the interval regression fail for 3.3 and 3.3.7.

The converged implementation passed 608 unit tests with three expected skips
on each target framework. The 42-test real-server version suite passed on tmux
3.2a and 3.7c. A further 214 real-server tests covering every migrated
production consumer passed on tmux 3.7c.

## Consequences

Adding a tmux behavior records one starting boundary. Removing one records the
ending boundary on the same capability. Both changes require source evidence
and a real-server regression in the parity ledger.

Stable releases newer than the required matrix receive established behavior
whose interval remains open. Development-line behavior stays unknown until a
stable release and evidence establish its boundary.
