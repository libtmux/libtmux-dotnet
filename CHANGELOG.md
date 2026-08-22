# Changelog

nuget.org shows release notes per version, so somebody landing on the newest
package never learns what an older one fixed. This is the whole history in one
place.

Versions follow [Semantic Versioning](https://semver.org). During alpha the
public API can change in any release with no deprecation period — pin an exact
version.

## [Unreleased]

### Added

- **An experimental query-only psmux preview for native Windows and WSL
  interop.** A separate analyzer-clean API reads one isolated session, its
  windows and panes, and pane text without exposing tmux lifecycle, mutation,
  chaining, control mode, or MCP behavior that psmux cannot preserve. The
  accepted client is the `psmux.exe` published in the psmux `v3.3.8` release:
  `PsmuxServer.SupportedBinarySha256` is that asset's SHA-256, and the commit
  and clean banner are pinned beside it. The caller supplies the executable;
  LibTmux never bundles, downloads, or searches `PATH` for one. The PowerShell
  harness owns a fresh data directory and refuses ambiguous cleanup. Completing
  native/WSL runtime verification remains a release prerequisite. See [the
  exact trust and compatibility boundary](docs/psmux.md).
- **Explicit control-stream loss reporting.** A bounded event reader now gets a
  `TmuxEventsDroppedEvent` with per-report and cumulative counts instead of
  silently missing notifications when it falls behind.

### Changed

- Generated API reference pages now contain only the public LibTmux surface,
  use each partial type's canonical summary, and are checked in CI alongside
  the public API, capability, snippet, and MCP catalogs.
- MCP tail cursors are authenticated, bounded in size, and tied to the exact
  endpoint, server generation, and pane. Search and every serialized tool
  result now obey hard global line and UTF-8 byte ceilings.
- MCP Tasks admit at most eight active executions, retain a bounded result set,
  and apply only to waits and job collection.
- **The `LibTmux` package README states tmux, framework and platform support
  in one table**, including what the Windows psmux preview reads and refuses.
- **The `LibTmux.Mcp` package README is an onboarding path.** How the server
  behaves — what to wait on, what bounds a result, which tools a tier
  registers, what a subscription holds open — moved to
  [`docs/mcp/`](docs/mcp/README.md), beside the generated tool reference.
- **`tmux_run` no longer answers as an MCP task.**

  - Previous behaviour: a task-capable client received a task handle and
    collected the result later.
  - New behaviour: the call blocks until the command exits, for every client.
  - Reason: an SDK task carries no durable tmux handle, so a client that
    disconnected lost work it believed was parked.
  - Recommended action: use `tmux_start_job` and `tmux_job`. tmux owns that
    handle, so it survives a disconnect.

### Fixed

- Control clients clean up a failed or cancelled attach without replacing its
  primary error. MCP hierarchy and pane-activity streams keep independent
  subscribers, recover after a stream ends, and preserve nullable structured
  fields required by their advertised schemas.
- MCP background jobs retain their originating endpoint and generation, cap
  concurrent state, clean up failed starts, serialize collection, and drain
  watcher tasks during disposal instead of leaking or cross-routing work.

## [0.0.0-alpha.8] — 2026-08-22

No behaviour change. `git diff v0.0.0-alpha.7..v0.0.0-alpha.8 -- src/` touches
only comments, XML documentation and package READMEs, so the compiled library
is the alpha.7 library. What ships differently is what a caller reads:
IntelliSense text, and the page on nuget.org.

### Changed

- **The package READMEs are titled `libtmux for .NET`** and state the alpha
  terms the way every libtmux port states them: releases carry an `-alpha`
  prerelease tag, the API is not settled, any release may change or remove
  exported identifiers without a deprecation period, pin an exact version, not
  recommended for production. The previous wording claimed the behaviour is
  proven on every commit; the tmux matrix runs on its own workflow, not on
  each commit.
- **Install instructions use `dotnet package add`**, the noun-first form .NET
  10 added, in the root README and each package README. The older
  `dotnet add package` still works.
- **Ten XML documentation comments that were false are corrected**, and the
  rest are cut to the constraint they carry. A `<summary>` restating its own
  identifier, or a `<remarks>` arguing a choice, is gone; what a caller may
  rely on — ordering, lifetime, cancellation, ownership — stays. This is the
  text IntelliSense shows, so it changes what a caller reads without changing
  what the code does.
- **`LibTmux.Workspace` says what an unsupported workspace key does** rather
  than leaving a reader to infer it.
- The same comment bar is applied to the tests, the engineering scripts and the
  examples, which ship in none of the packages.

### Added

- **`.github/WRITING.md` and `.github/CONTRIBUTING.md`** carry how this project
  writes and how it works. `AGENTS.md` is now a router naming which of them
  governs the change being made, rather than restating both at 364 lines.
- **`examples/README.md`** says how a documented example is verified: every C#
  block in the shipped documents is compiled against the real assemblies, a
  `csharp run` block is additionally executed against a tmux server of its own,
  and a block anchored to a snippet region is held to the code it was quoted
  from.

### Fixed

- The `alpha.5`, `alpha.6` and `alpha.7` headings in this file link their
  release tags. They were bracketed like links with no reference definition, so
  they rendered as literal text.

## [0.0.0-alpha.7] — 2026-08-16

### Changed

- **`LibTmux.Mcp` is a different server.** It offered five tools; it now offers
  42, across three safety tiers, with four fixed `tmux://` resources, two
  resource templates and four workflow prompts. Every shaped tool answers a
  typed record with a JSON output schema; `tmux_display_message` returns the raw
  expanded format text it was asked for. The tool names all changed —
  `list_tmux` and friends are gone in favour of `tmux_hierarchy`, `tmux_run` and
  the rest. [The reference](docs/mcp/tools.md) is generated from the server, so
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
- **`eng/mcp/mcp_swap.py`**, which points every installed agent CLI at a local
  build and reverts from the backup it took. It writes `DOTNET_ROOT` into each
  config: an agent spawns the server with its own environment, and a
  mise-installed SDK is on neither its `PATH` nor its `DOTNET_ROOT`, so the
  binary exits before the handshake and the agent reports only that the server
  has no tools.

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
- **The protocol test harness no longer leaks a tmux server per test.** It
  started one and disposed only the MCP objects, so a suite run left dozens
  behind. They were idle but not free: at load 24 on 20 cores the first thing
  to fail was an unrelated library test waiting ten seconds for a shell to
  redraw a wide prompt, which is the sort of failure that gets called flaky and
  retried rather than read.
- **A run leaves nothing of its own on screen.** The shell echoes the
  rendezvous like anything typed, and it is wider than a pane, so tmux stores
  the wrap as a line break and the marker arrives split across rows. Rows are
  rejoined into the logical line they came from before matching, which is the
  only form the marker is whole in.

## [0.0.0-alpha.6] — 2026-08-16

No change to the library. `git diff v0.0.0-alpha.5..v0.0.0-alpha.6 -- src/`
is empty, so this package is the alpha.5 package.

### Changed

- **macOS arm64 is proven rather than advisory-and-failing.** The lane had
  failed two integration tests since it was added, recorded as timing. It was
  pane width: a GitHub macOS runner's hostname is 61 characters, so bash's
  prompt fills 78 of the pane's 80 columns, and tmux stores the resulting wrap
  as a real line break. Text typed there arrives split, so a capture joined
  with newlines cannot contain it. Those assertions now capture with
  `joinWrappedLines`, which is what `capture-pane -J` is for. A caller
  asserting on pane text hits the same thing on any machine with a long
  prompt.

## [0.0.0-alpha.5] — 2026-08-16

### Added

- **`LIBTMUX_SOCKET_NAME` and `LIBTMUX_SOCKET_PATH` point a connection that
  named no socket.** A harness, sandbox or container can move every
  unqualified connection at once without the call sites in between naming a
  socket they should not have to know about. A caller who named one — by path,
  by name, or by factory — is never redirected, and between the two variables
  the path wins. Adds no public member: the existing resolution path reads them
  where it already reads `TMUX_TMPDIR`.
- **Every documented example is executed against live tmux in CI**, not only
  compiled. The examples are real methods that run as tests, and the blocks the
  documents publish are copied from that code by
  `eng/docs/sync_snippets.py`, which fails the build when the two drift. The
  first example in the README — a bare `Server.ConnectAsync()` — could not run
  under the old harness and now does.

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
- macOS: a pane's path is compared resolved, since `/tmp` is a symlink there,
  and the suite no longer assumes `/bin/sh` reports as `sh`.

### Changed

- The macOS lane runs as an advisory job and reports what it finds, rather than
  the compatibility claim naming a platform nobody ran.

## [0.0.0-alpha.4] — 2026-08-16

### Added

- **`TmuxDispatchState` on every failure**, so a caller can tell a safe retry
  from one that repeats a side effect. `NotDispatched` is claimed only where the
  library can see that no tmux process ran; `TmuxCommandException` is always
  `Dispatched` because it exists because tmux answered; the default is `Unknown`,
  since a client that died mid-command may have been obeyed first. Reads as an
  exception filter:
  `catch (LibTmuxException e) when (e.Dispatch == TmuxDispatchState.NotDispatched)`.
- **Fuzz tests over the parsers** — 8,000 generated cases and a corpus of the
  shapes that break parsers, asserting a refusal rather than a crash or a hang.
- **Recorded benchmarks** under [`docs/benchmarks`](docs/benchmarks/README.md),
  each naming its tmux, host, runtime, commit and date, with the full
  distribution over 100 samples.
- **`SECURITY.md`**, and CodeQL and OpenSSF Scorecard workflows.
- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and this file.

### Changed

- **The benchmark numbers in the README were wrong and are now marginal costs.**
  The table claimed chaining fifty commands cost less than chaining one, which
  cannot be true. It was not noise: it reproduced across two independent
  100-sample runs because five warmup samples were not enough to outlast the
  runtime tiering up, and the penalty always landed on whichever case ran first.
  At forty warmup samples the order corrects. The front page now quotes what one
  more command costs — the part that belongs to the library rather than the
  machine — and links to a recorded run for the absolute milliseconds.
- The documented claim that chaining beats control mode for a batch is now
  stated as host-dependent, because it reverses on a loaded machine.
- Every GitHub Actions reference is pinned to a commit SHA, maintained by
  Dependabot.
- Example coverage extended to `docs/modes/`, so the documents that teach the
  API are compiled and run rather than only the ones that ship.
- Dependencies updated across the board, including four major bumps:
  `ModelContextProtocol` 0.4.0-preview.1 to 2.2.0, `YamlDotNet` 16 to 18, and
  both xunit packages to 4.0.0. No consumer-visible behaviour changed; the core
  library's single dependency is unaffected.

## [0.0.0-alpha.3] — 2026-08-16

### Changed

- The project moved to `github.com/libtmux/libtmux-dotnet`. This is the first
  build whose project and repository links are that address rather than a
  redirect to it. No API, behaviour or dependency difference from alpha.2.

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
- The MCP server reports its real version rather than `0.0.0.0`.

## [0.0.0-alpha.1] — 2026-08-15

First public release. `0.0.0-alpha.1` is the lowest version that still says what
it is: a published version can never be deleted from nuget.org, only unlisted.

- `LibTmux` — the client. Servers, sessions, windows, panes, clients, options,
  hooks and buffers, typed and asynchronous, against tmux 3.2a through 3.7b on
  net8.0 and net10.0. One dependency.
- `LibTmux.Query.Json` — `System.Text.Json` for query documents.
- `LibTmux.Workspace` — sessions from tmuxp workspace files.
- `LibTmux.Mcp` — a Model Context Protocol server, installed as a .NET tool.

[Unreleased]: https://github.com/libtmux/libtmux-dotnet/compare/v0.0.0-alpha.8...HEAD
[0.0.0-alpha.8]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.8
[0.0.0-alpha.7]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.7
[0.0.0-alpha.6]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.6
[0.0.0-alpha.5]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.5
[0.0.0-alpha.4]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.4
[0.0.0-alpha.3]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.3
[0.0.0-alpha.2]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.2
[0.0.0-alpha.1]: https://github.com/libtmux/libtmux-dotnet/releases/tag/v0.0.0-alpha.1
