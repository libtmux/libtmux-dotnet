# Changelog

Notable changes to libtmux-dotnet that span more than one package:
documentation, CI, tests and benchmarks, release tooling, and the notes that
cover a whole release. A change to one package is in that package's changelog:

- [`LibTmux`](src/LibTmux/CHANGELOG.md)
- [`LibTmux.FSharp`](src/LibTmux.FSharp/CHANGELOG.md)
- [`LibTmux.Testing`](src/LibTmux.Testing/CHANGELOG.md)
- [`LibTmux.Query.Json`](src/LibTmux.Query.Json/CHANGELOG.md)
- [`LibTmux.Extensions.DependencyInjection`](src/LibTmux.Extensions.DependencyInjection/CHANGELOG.md)
- [`LibTmux.Workspace`](src/LibTmux.Workspace/CHANGELOG.md)
- [`LibTmux.Workspace.Cli`](src/LibTmux.Workspace.Cli/CHANGELOG.md)
- [`LibTmux.Mcp`](src/LibTmux.Mcp/CHANGELOG.md)

The packages share one version, so a release has a section in each changelog
from the release that first shipped the package. Releases are numbered `0.1.N`,
and each release increments `N`. The API is not settled: any release may change
or remove exported identifiers, and these changelogs mark each break. Pin an
exact version to avoid one. Releases before 0.1.0 were alphas numbered
`0.0.0-alpha.N`.

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

### Development

## [0.1.1] — 2026-11-01

A program that chooses no socket now reaches the server tmux picks, and
`libtmux-mcp` drives that server instead of one of its own. `LIBTMUX_SOCKET`
names the socket, and `LIBTMUX_SOCKET_NAME` selects nothing. The bold entries
under Changed carry the migration notes.

### Development

- The C# and F# test projects claim a socket root of their own before the first
  test. Each run makes an owner-only directory under `/tmp/libtmux-dotnet-test`,
  points `TMUX_TMPDIR` and `TMPDIR` at it, clears `TMUX`, `TMUX_PANE` and every
  `LIBTMUX_SOCKET*` variable, and stops with an error that names the variable
  when one survives. `LIBTMUX_TEST_SOCKET_ROOT` moves the parent of the runs.
  (#133)
- When a test process exits, every tmux server left anywhere under its run's
  directory is stopped through its `-S` path and the run fails with exit code
  78. The directory is removed once no server in it answers. (#133)
- `SandboxCanaryTests` starts a bare `tmux` and shows that it lands in the run's
  directory, and hands a recording stand-in a decoy for each variable to show
  that the check can fail. (#133)

## [0.1.0] — 2026-10-25

### Changed

- **Releases are numbered `0.1.N` and carry no `-alpha` tag.** The next release
  is `0.1.0`, then `0.1.1` and so on, each incrementing `N`. The API is still
  not settled, so pin an exact version; the changelog marks each break.

## [0.0.0-alpha.24] — 2026-10-25

This release is breaking for code that caught `ArgumentException` or
`TmuxWindowException` when tmux refused a command: they now raise
`TmuxUnsupportedOnReleaseException`. The migration note is the first entry
under Changed.

### Changed

- `capabilities.json` names the exception of each workaround, and the `apis`
  of `layout-without-checksum` and `name-separators-rejected` list only the
  calls that raise the entry's `error`. (#110)
- `tmux-workspace` reports the JSON `code` `tmux_unsupported` for any
  `TmuxUnsupportedOnReleaseException`, not only `TmuxVersionTooLowException`.
  The MCP failure advice for the base type says "Nothing was sent to tmux."
  and does not point to `tmux://capabilities`. (#110)
- The optional-features table names the exception each refusal raises, and the
  matrix states which nested commands the check does not read, the releases
  they affect and what happens there. A test pins the limit. (#108)
- The README and the version matrix link `capabilities.json`. (#109)
- The version matrix cites pinned tmux sources for the nested-command limit.
  (#109)
- The version matrix says which layout path raises which exception, and the
  `layout-without-checksum` summary in `capabilities.json` says the same.
  (#109)
- The README says that `tmux-workspace` and `libtmux-mcp` read `LIBTMUX_TMUX`
  and the `LibTmux` package does not. (#109)

## [0.0.0-alpha.23] — 2026-10-24

Unsafe optional tmux features are gated one at a time. Each is refused on the
tmux releases where it is unsafe, before anything is sent, and the minimum
stays 3.3a. Pane waits now find text that crosses a wrapped row, and end with
the output read so far when the tmux server stops.

### Added

- `capabilities.json` at the repository root lists the optional tmux features
  and release defects the library gates, with the release each starts at or
  covers, for the documentation's capabilities page. It is written from the
  registry the feature guard reads, and a unit test fails when the two differ.
  (#84)
- `ExampleAttribute.RequiresTmux` and `requiresTmux` in the API example
  manifest name the first tmux an example runs on. (#84)

### Fixed

- `Pane.WaitForTextAsync` and the MCP `wait_for_text` now find text that
  crosses a wrapped row, below tmux 3.7 and from it, spaces a wrapped row
  held included. A wrapped line is one line; a real line break still ends one.
  A line may start with any number of spaces, a full pane width included, or
  hold rows of only spaces, and the lines after it still match. If the two
  captures of a read disagree about a line, only that line is matched row by
  row. (#84)
- A pane wait now ends as `PaneWaitOutcome.PaneExited`, or `PaneDied` in MCP,
  with the output read so far when the tmux server stops during it, as it does
  when the pane dies. It used to raise a transport error with no output. A
  live pane whose control stream is lost still raises `TmuxTransportException`.
  (#84)

### Changed

- **An operation that is unsafe on one supported tmux is refused on that
  tmux, and the minimum stays 3.3a.** Each is refused before anything is sent,
  with `TmuxVersionTooLowException` and `Dispatch` set to `NotDispatched`.
  See [the matrix](docs/modes/matrix.md#optional-features). (#84)
  - `Server.EnterControlModeAsync`, and with it `ServerMirror`, needs tmux 3.7,
    because a control client can crash an older server while it attaches. Pane
    waits poll below it, with no opt-in.
  - Deleting or renaming a buffer by name, `Pane.RunAsync` and the MCP
    `paste_text` need 3.4, and a foreground `run-shell` that names no pane
    needs 3.5, typed or raw.
  - Raw commands and batches are refused on the release that mishandles them:
    an unnamed `break-pane` and a name with `:` or `.` on exactly 3.7,
    `split-window -p` on 3.4, an unchecked `select-layout` below 3.4, and
    `refresh-client -l` below 3.6a, where 3.3a ends the server at once with a
    client attached, and 3.4 through 3.6 end it when the client leaves before
    its terminal answers. The typed
    `Server.RefreshClientAsync` sends the flag only from 3.7, so it is safe.
  - The checks read the command they are given, not a tmux command nested in
    `if-shell`, `run-shell -C`, a brace block, a hook or a binding. Do not
    nest a command that is refused.

## [0.0.0-alpha.22] — 2026-10-19

tmux 3.3a is now the minimum. Every command is checked against the daemon it
reaches, before it has any effect, and an older or unrecognized daemon is
refused with `TmuxVersionTooLowException` and the remedy. Pane waits poll below
tmux 3.7, and tmux 3.8 joins the tested range. The package READMEs open on a
quick start, and CI no longer runs macOS or the psmux smoke; both stay
supported at best effort.

### Changed

- The package READMEs open on a quick start and answer one task per section,
  with each API as a linked subsection, a tested example, and a short note.
  The long contract paragraphs of `LibTmux`, `LibTmux.Workspace`, and
  `LibTmux.Query.Json` moved to a `docs/contracts.md` beside each README, and
  every README ends with what it leaves out and what it supports.

### Development

- `ReadmePackageListTests` resolves every `tree/master` and `blob/master` link
  in the root, package, and MCP READMEs against the checkout, holds the root and
  `LibTmux` READMEs to an index row for each package and to a heading that
  links each package's directory, and fails if the sections that map the page
  go. It also requires the shared logo header on every package README and
  checks that packing leaves no HTML in it.
- `ReadmeCommandTests` parses every `tmux-workspace` command in those READMEs
  against the tool's command line, and loads the workspace the
  `LibTmux.Workspace.Cli` README shows with the command the root README shows.
- `McpReadmeConfigurationTests` starts `libtmux-mcp` with every `LIBTMUX_`
  setting the READMEs configure it with, runs the handshake and tool-listing
  probes they show, and checks the startup report names each setting's effect.
- `ReadmeToolLinkTests` holds every tool name in the root, `LibTmux.Mcp`, and
  MCP behavior READMEs to a link to its libtmux.org page, taking the names from
  the server's manifest.
- The root and `LibTmux` READMEs show a run example for each package under
  `Package examples`, and the `LibTmux.Testing` and
  `LibTmux.Extensions.DependencyInjection` READMEs run theirs.
  `sync_fsharp_snippets.py` publishes F# examples into the root and `LibTmux`
  READMEs too.
- `ReadmePackageListTests` also requires the root README, the MCP behavior
  page, and every package README to open on `Quick start`, and fails a
  paragraph or list item over four 80-column lines of rendered text. It no
  longer compares per-package examples between the root and `LibTmux`
  READMEs, which do not carry them now, and `sync_fsharp_snippets.py` stops
  publishing F# examples into those two.
- CI no longer runs macOS, and releases no longer wait on the psmux native
  Windows and WSL smoke. Both remain supported at best effort, with highly
  limited CI coverage, because neither ran reliably in CI. Publishing waits
  for the dotnet gate and the tmux matrix, and
  `eng/psmux/Invoke-PsmuxSmoke.ps1` runs by hand as `docs/psmux.md` shows.
  (#99)

## [0.0.0-alpha.21] — 2026-10-11

One-shot commands travel over one shared control client per server from tmux
3.7, so a pane run or workspace build no longer starts a tmux process per
command. The macOS lane passes without raising any timeout.

### Development

- The macOS lane runs on pull requests and gates them. A `macos-stress`
  workflow repeats the real-tmux suites and reports each test's failure count,
  and `.github/MACOS_CI.md` lists what a test needs to pass on macOS. (#59)

## [0.0.0-alpha.20] — 2026-10-04

Source queries expose their execution plans and retain the captured graph
behind matching entities. Workspaces resolve inherited declarations and apply
reviewed plans with action and cleanup journals. Pane runs and waits bound
output and report partial effects. Query documents now require schema v2;
workspace readiness and pane polling fallback must be chosen explicitly.

### Fixed

- Pane runs retain their same-pane reservation until completion is verified.
  Cleanup failures preserve the original error or completed result, and MCP
  failures identify owned resources to inspect before retrying. (#34)

### Development

- Build inspected local review packages from committed source with
  `eng/package_review.py`. Checked restore, build and pack phases let
  downstream consumers verify a specific source revision. (#34)

## [0.0.0-alpha.19] — 2026-10-04

`LibTmux.FSharp` gains a function for every session, window and pane
operation the MCP server offers, complete patterns for runs, waits and pane
watches, and `TmuxAsync` for `async` workflows. A run ends within five seconds
when its pane's program exits, pane waits read through the control client they
attach and keep reading a busy pane, and failures say whether tmux saw the
command, so `Retry.ifNotSentAfter` retries while a server starts.
`SplitSpec.Size` becomes a `SplitSize`, and a control client's events allow one
reader at a time; see Changed.

### Added

- `Pane.Active`, `Dead`, `DeadStatus`, `InMode`, `ProcessId`, `Synchronized`,
  `HistorySize`, `Tty` and `StartCommand`, and `Window.Active`, `Zoomed`,
  `Flags`, `BellAlert`, `ActivityAlert` and `SilenceAlert`, read pane and
  window state; queries filter on them, and on `Window.Layout`, through tmux.
  `LibTmux.FSharp` adds the matching `PaneFields` and `WindowFields`. (#53)
- `Session.GetActivePaneAsync` and `Window.GetActivePaneAsync` read the pane a
  session or window shows, and `LibTmux.FSharp.Session.activePane` and
  `Window.activePane` do the same from F#. (#53)
- `Pane.ReadOutputSinceAsync` and `LibTmux.FSharp.Pane.readSince` read what a
  pane printed since a position the last read returned, as the MCP server's
  `capture_since` does. (#53)
- `Session.HoldWaitClientAsync` and `LibTmux.FSharp.Session.holdWaitClient`
  keep the control client a session's pane waits use attached, so a series of
  waits attaches it once. (#53)

### Fixed

- **`PaneObservation.WatchAsync`, `LibTmux.FSharp.Control.watchPane` and
  `Control.watchPanes` raise `ArgumentException` for a pane outside the
  control client's session, and `InvalidOperationException` when a watched
  pane's window moves to another session.** tmux sends that pane's output to
  no client of another session, so the watch waited until cancelled. Attach
  the control client to the pane's session. (#53)
- A pane wait, and the MCP server's `wait_for_text`, over a pane that keeps
  changing, such as one redrawing a progress bar, reads it again until a read
  holds still or the time runs out; it failed at once, saying the pane
  changed during every snapshot attempt. (#53)
- `Pane.RunAsync`, `LibTmux.FSharp.Pane.run` and the MCP server's
  `run_shell_command` end within five seconds when the pane's program exits
  mid-run, with `PaneRunResult.PaneExited` set (`PaneRun.Ended` from F#,
  `paneExited` from the MCP server), and with the exit status when the
  command recorded one but its completion signal was lost. They waited out
  the timeout, then reported a timeout, or raised a `LibTmuxException`
  saying the pane was running a different process. (#53)
- A `TmuxOptionException` for an option command tmux refused, a value that
  does not parse as its key's type, or no value reported has
  `Dispatch = Dispatched`, so `LibTmux.FSharp.TmuxFailure.Ran` matches it. It
  said `Unknown`, which `MayHaveRun` matched. (#53)
- `Pane.RunAsync` and `LibTmux.FSharp.Pane.run` refusing a pane that is not
  waiting at a shell raise `TmuxPaneException` with
  `Dispatch = NotDispatched`, so `TmuxFailure.NotSent` matches it. It said
  `Unknown`, which `MayHaveRun` matched as though the command might have
  run. (#53)
- A command sent where no tmux server listens fails with
  `Dispatch = NotDispatched`, so `LibTmux.FSharp.TmuxFailure.NotSent` matches
  it and `Retry.ifNotSentAfter` retries it while a server starts. It said
  `Dispatched`, and no retry repeated it. (#53)
- Pane waits, and the MCP server's `wait_for_text` and `capture_since`, no
  longer miss a shell's new output once the pane's history reaches nine
  tenths of `history-limit`; from 1,800 lines of tmux's default 2,000 a wait
  timed out and a read found nothing new until tmux first freed history. (#53)
- `PaneObservation.WatchAsync` and `LibTmux.FSharp.Control.watchPanes` check
  which watched panes remain with one tmux command after a layout change,
  where they sent one per pane and could fall far enough behind to drop
  events. (#53)
- Package pages on nuget.org no longer open with the logo's HTML shown as
  text. (#53)

### Development

- F# benchmark records state whether the host was a virtual machine, how many
  cores the run could use, the CPU governor and the load averages. (#53)
- `FSharpMirrorBenchmarks` measures what one tmux change costs a live mirror,
  against the capture each rebuild performs. (#53)
- `FSharpPaneFloodBenchmarks` measures reading a pane's output flood through
  a real control client, by hand and through `Control.watchPane`. (#53)
- `FSharpWaitLatencyBenchmarks` measures `Pane.sendAndWait` and `Pane.run`
  against polling the screen every 50 ms, for output printed at once and
  after a delay, with and without a held control client. (#53)
- The `benchmarks` workflow fails a run in which pushdown is less than three
  times as fast as listing everything or allocates no less, in which a rename
  seen through a mirror costs more than 1.65 captures, or in which reading a
  pane's flood through `Control.watchPane` costs more than 1.6 times reading
  it by hand. (#53)

## [0.0.0-alpha.18] — 2026-10-03

`LibTmux.FSharp` reads every level of a server through one query shape whose
filters tmux narrows with `-f`, and gains waits, send-and-wait and runs that
the core now exposes, a live server mirror, typed options, session
descriptions and command chains. Owned servers are gone when their disposal
returns, and `LibTmux.FSharp` loads in projects built with the .NET 8 SDK. A
few F# functions are replaced by queries and streams; see Removed.

### Added

- Queries filter on a pane's index, title, working directory, size, position
  and edges, a window's index and size, `SessionFields.windowCount` and
  `WindowFields.paneCount`, in C#, F# and the version 1 query schema. (#51)
- `QueryExtensions.Translate` accepts `StartsWith`, `EndsWith`, `Contains`
  and `Equals` with `StringComparison.Ordinal` or `OrdinalIgnoreCase`, and
  query documents gain `startsWithOrdinalIgnoreCase`,
  `endsWithOrdinalIgnoreCase` and `containsOrdinalIgnoreCase`. (#51)
- `ServerMirror` keeps a live copy of a server's sessions, windows, panes and
  clients, recaptured on each change tmux announces and optionally on an
  interval. `LibTmux.FSharp.Mirror` wraps it. (#51)
- `TmuxOptionKey<T>`, `TmuxOptions.GetAsync<T>` and `TmuxOptions.SetAsync<T>`
  read and write an option as its type. `LibTmux.FSharp.Options` wraps them.
  (#51)
- `Server.Within(TimeSpan)` returns a handle over the same connection whose
  commands, and those of every handle taken from it, carry that timeout.
  `LibTmux.FSharp.Server.within` wraps it. (#51)
- `PaneObservation.WatchAsync` follows several panes through one control
  client. `LibTmux.FSharp.Control` adds `watchPanes`, `watchPane`, `events`,
  `enterSession` and `cleanupFailure`. (#51)

### Development

- macOS real-tmux CI waits for observable pane state and offers a restricted
  debug shell for investigating runner failures. (#36)
- CI builds and runs an F# consumer with the .NET 8 SDK against the packed
  `LibTmux.FSharp`. (#51)
- CI compares what every F# example program and the README quickstart print
  with their recorded output, on every supported tmux. (#51)
- An integration test fails a pushed-down query that starts more tmux
  processes or reads more rows than it needs. (#51)
- A hung integration test fails its lane after five minutes with a dump that
  names the test. (#51)
- Benchmarks record F# query, pushdown, control-fold, task and pane-watch
  costs, and the `benchmarks` workflow records them on a hosted runner. (#51)
- ADR 0008 records that typed filters reach tmux with a local recheck, ADR
  0009 that the F# facade covers idioms rather than every core operation, and
  ADR 0010 that it still builds sessions and chains and bounds handles. (#51)

## [0.0.0-alpha.17] — 2026-09-27

Adds `LibTmux.FSharp` over the existing .NET handles, snapshots and query
documents. Control mode now rejects foreground shell commands whose output
cannot be framed, and pane observation retains output through departure and
dropped-event boundaries. The workspace CLI gains per-command help examples
and clearer errors when it cannot prompt.

### Added

- **A package downloaded from nuget.org can be verified.** nuget.org's
  repository signature changes a package's digest, so `gh attestation verify`
  found nothing for the file a restore downloads, including every
  `0.0.0-alpha.16` package. Releases now also attest each package as nuget.org
  serves it; SECURITY.md shows the command.

### Fixed

- `LibTmux.Extensions.DependencyInjection`'s README installed it without
  `--prerelease`, which finds no version while every release is an alpha.

### Development

- Package inspection refuses a package README with a relative link, which
  leads nowhere on nuget.org, and two such links were made absolute.
- The recorded benchmark run states the forty warmup samples it discarded,
  not five, and `record_modes.py` reads that count from the BenchmarkDotNet
  report and refuses to record from a tree with uncommitted changes.

## [0.0.0-alpha.16] — 2026-09-26

Ships `LibTmux.Workspace.Cli`, the `tmux-workspace` .NET tool for tmuxp
workspaces, and makes `Server` session calls match a name exactly rather than
as a prefix. `Server.KillSessionAsync` now throws for a name no session
carries exactly, and `Server.GetClientsAsync` for a real tmux failure, where
each used to report success.

### Added

- `LibTmux.Server` exposes `EndpointArguments`, `BuildGuardedCommandLine`,
  `IsValidLayoutCandidate` and `ValidateLayoutsAsync`, and `ServerGeneration`
  exposes `Parse` and `DisplayFormat` -- the pieces `tmux-workspace` assembles
  a guarded command line, checks a layout candidate and addresses one server
  generation with. (#26)

### Fixed

- A malformed custom layout could reach tmux unchecked through
  `Server.Chain()`, `WorkspaceBuilder` or `Window.SelectLayoutAsync`, which
  only checked that a custom layout's checksum prefix was well-formed, not
  that it was correct or that the cell tree it introduced was valid. All
  three now validate a layout's syntax, checksum and minimum pane count
  through `Server.ValidateLayoutsAsync` -- checked against the running
  daemon's version, or the client's when none is running -- before anything
  dispatches. Layout checks infer release-candidate capabilities at their
  release boundary and keep `next-X.Y` below `X.Y`, without changing public
  version ordering or stable-only capability projections. (#26)

## [0.0.0-alpha.15] — 2026-09-20

Corrects snapshot identity, window placement mutations and typed pane input.
`WindowEntityKey` and composite `SendKeysRequest` callers must follow the
migrations below.

### Added

- This port's guides and examples now publish to libtmux.org at
  `/en/dotnet/latest/`, a path the site's port switcher and every page's
  sidebar already linked and which returned 403 until now. libtmux.org's own
  assembly builds the tree, reading this checkout for the sources its examples
  quote. (#21)
- `docfx.json` at the repository root, which extracts `LibTmux`,
  `LibTmux.Query.Json`, and `LibTmux.Workspace` from their XML documentation
  comments for the API reference libtmux.org publishes at
  `/en/reference/dotnet/`. `LibTmux.Mcp` is excluded: it packs as a tool and
  ships no assembly to reference, so its documented surface is the MCP tool
  list in `docs/mcp/tools.md`. (#21)

## [0.0.0-alpha.14] — 2026-09-20

Ships every change listed under `0.0.0-alpha.13`, which was tagged but never
published. No other changes.

## [0.0.0-alpha.13] — 2026-09-19

**Tagged but never published.** The release run for this tag did not reach
its publish step, so `0.0.0-alpha.13` does not exist on nuget.org. Every change
below ships in `0.0.0-alpha.14`; pin that instead.

### Changed

- **Query documents are created through `QueryExtensions.Translate`, with
  read-only metadata.** Use `Compile`, `Matching` or JSON interchange instead of
  constructing predicate nodes. (#28)
- **Invalid query documents throw `UnsupportedQueryExpressionException`.** Catch
  that exception for schema and semantic errors; malformed JSON still throws
  `JsonException`. (#28)

### Development

- **The release SBOM lists what the packages contain.** `release.yml` scanned
  the `.nupkg` files themselves, which syft's .NET catalogers do not open, so
  every SBOM through `0.0.0-alpha.12` listed no components. It now scans the
  unpacked packages and stops the release if the SBOM omits one. (#27)

## [0.0.0-alpha.12] — 2026-09-12

### Development

- The listed-entity validator tests disposal rather than every interface. It
  read any interface on `Server`, `Session`, `Window`, `Pane` or `Client` as
  "listed entity is disposable", while the check after it already tested public
  `Dispose` and `DisposeAsync` members. (#25)
- **Every CI job carries a timeout.** A job that stopped making progress ran to
  GitHub's six-hour ceiling; the advisory macOS lane reached fifty minutes
  against a median under two. Each job now fails in minutes instead. (#24)
- **The advisory macOS lane runs after merge, not on every pull request.** It is
  `continue-on-error`, so it never gated a merge — it only spent macOS minutes
  at ten times the Linux rate. Pushes to master and manual dispatch still
  exercise the platform. (#24)

## [0.0.0-alpha.11] — 2026-09-07

**Tagged but never published.** The release run for this tag did not reach
its publish step, so `0.0.0-alpha.11` does not exist on nuget.org. Every change
below ships in `0.0.0-alpha.12`; pin that instead.

### Fixed

- The private MCP config swapper sets its lock file's mode after creating it.
  `openat` is variadic in C, and Apple's arm64 ABI passes a variadic argument
  on the stack rather than in a register, so the 0600 the lock was created with
  never reached the kernel there and every swap failed its own ownership check.
- `SessionInfo` drops `width` and `height`; tmux removed the `session_width`
  and `session_height` formats in 2.9. A zero resize extent is refused
  alongside the negatives tmux already refused, an unknown layout is no longer
  reported as "tmux tmux <version>", and `wait_for_text` documents that text
  already on screen never matches.

## [0.0.0-alpha.10] — 2026-08-30

No entries that span packages.

## [0.0.0-alpha.9] — 2026-08-22

### Changed

- Generated API reference pages now contain only the public LibTmux surface,
  use each partial type's canonical summary, and are checked in CI alongside
  the public API, capability, snippet, and MCP catalogs.
- **The `LibTmux` package README states tmux, framework and platform support
  in one table**, including what the Windows psmux preview reads and refuses.
- **The `LibTmux.Mcp` package README is an onboarding path.** How the server
  behaves — what to wait on, what bounds a result, which tools a tier
  registers, what a subscription holds open — moved to
  [`docs/mcp/`](docs/mcp/README.md), beside the generated tool reference.

### Fixed

- Control clients clean up a failed or cancelled attach without replacing its
  primary error. MCP hierarchy and pane-activity streams keep independent
  subscribers, recover after a stream ends, and preserve nullable structured
  fields required by their advertised schemas.

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

### Added

- **`eng/mcp/mcp_swap.py`**, which points every installed agent CLI at a local
  build and reverts from the backup it took. It writes `DOTNET_ROOT` into each
  config: an agent spawns the server with its own environment, and a
  mise-installed SDK is on neither its `PATH` nor its `DOTNET_ROOT`, so the
  binary exits before the handshake and the agent reports only that the server
  has no tools.

### Fixed

- **The protocol test harness no longer leaks a tmux server per test.** It
  started one and disposed only the MCP objects, so a suite run left dozens
  behind. They were idle but not free: at load 24 on 20 cores the first thing
  to fail was an unrelated library test waiting ten seconds for a shell to
  redraw a wide prompt, which is the sort of failure that gets called flaky and
  retried rather than read.

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

- **Every documented example is executed against live tmux in CI**, not only
  compiled. The examples are real methods that run as tests, and the blocks the
  documents publish are copied from that code by
  `eng/docs/sync_snippets.py`, which fails the build when the two drift. The
  first example in the README — a bare `Server.ConnectAsync()` — could not run
  under the old harness and now does.

### Fixed

- macOS: a pane's path is compared resolved, since `/tmp` is a symlink there,
  and the suite no longer assumes `/bin/sh` reports as `sh`.

### Changed

- The macOS lane runs as an advisory job and reports what it finds, rather than
  the compatibility claim naming a platform nobody ran.

## [0.0.0-alpha.4] — 2026-08-16

### Added

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

No entries that span packages.

## [0.0.0-alpha.1] — 2026-08-15

First public release. `0.0.0-alpha.1` is the lowest version that still says what
it is: a published version can never be deleted from nuget.org, only unlisted.

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
