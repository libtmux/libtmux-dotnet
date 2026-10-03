# ADR 0010: The F# facade builds sessions and chains, and bounds handles

## Status

Accepted for the prerelease line. Supersedes the parts of
[ADR 0009](0009-fsharp-covers-idioms.md) that declined a session builder,
typed chains and a per-handle timeout. ADR 0009's rule for other operations
stands: a facade function needs an F# shape the core call lacks.

## Context

ADR 0009 answered three requests from the parity review with core features:
`LibTmux.Workspace` for describing a session, `Server.Chain().Then(...)` for
chains, and a `CancellationTokenSource` or `ServerConnectionOptions.CommandTimeout`
for timeouts. Each answer worked, and each left an F# caller writing the shape
the JVM ports give by name:

- A session description went through a tmuxp-shaped `WorkspaceFile`, a separate
  package whose constructors take positional lists, rather than F# records.
- A chain was a method chain on a core object, with every step but typed
  requests spelled as raw tmux arguments, where Kotlin and Java name the steps
  (`newWindow`, `splitLeftRight`, `sendLine`, `arrange`).
- A different bound for some commands needed a second connection to the same
  socket, which starts and verifies a second transport. Java's
  `Server.within(Duration)` returns a handle over the same transport.

Adoption reviews of the branch scored F# idiom and tmux coverage below the
other dimensions for exactly these three, and found nothing else holding them
there.

## Decision

- **Session builder.** `SessionSpec`, `WindowSpec` and `SplitSpec` are F#
  records, built with copy-and-update from `SessionSpec.named`,
  `WindowSpec.named` and `SplitSpec.empty`. `Server.newSession` lowers one as
  the Kotlin DSL does: the first window spec goes into `new-session` itself,
  each later spec is a new window, and splits are made in order beside the pane
  before. `LibTmux.Workspace` remains the route for tmuxp files and for waiting
  on shell prompts.
- **Typed chains.** The `Chain` module pipes a core `TmuxChain` through named
  steps (`newWindow`, `splitLeftRight`, `splitTopBottom`, `sendLine`,
  `arrange`) and runs it with `Chain.run`; `Chain.add` takes a typed request's
  `ToCommand`. `newWindow` names its session, so a chain does not depend on
  which session tmux considers current.
- **Per-handle timeout.** The core gains `Server.Within(TimeSpan)`, a handle
  over the same connection whose commands, and those of every handle taken from
  it, carry the bound. F# reaches it as `Server.within`.

## Alternatives

**Keep ADR 0009's answers.** Every capability already existed, but the shapes
were C# shapes in F# code, and the review counted that against adoption.

**A computation expression for sessions.** Reads like the Kotlin DSL, but
custom operations are harder to discover, harder to document per member, and
records already give named fields, defaults and structural equality.

**A second connection for `within`.** Already possible, and it starts another
transport and repeats version verification for what is only a different
deadline.

## Consequences

- The spec records spell out `ToString`, because the one F# generates formats
  through `printf`, which NativeAOT rejects.
- A chain still returns one `TmuxCommandResult`, not a result per step.
- `Server.within` handles compare equal to the handle they came from.
