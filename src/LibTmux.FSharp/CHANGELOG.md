# Changelog

Notable changes to `LibTmux.FSharp`.

Releases are numbered `0.1.N`, and each release increments `N`. The API is not
settled: any release may change or remove exported identifiers, and this
changelog marks each break. Pin an exact version to avoid one. Releases before
0.1.0 were alphas numbered `0.0.0-alpha.N`.

The packages share one version, so every release since this package first
shipped has a section here. Changes that span packages, such as documentation,
CI, tests and release tooling, are in the [main changelog](../../CHANGELOG.md).

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

## [0.1.1] — 2026-11-01

No entries for `LibTmux.FSharp`.

## [0.1.0] — 2026-10-25

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.24] — 2026-10-25

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.23] — 2026-10-24

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.22] — 2026-10-19

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.21] — 2026-10-11

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.20] — 2026-10-04

No entries for `LibTmux.FSharp`.

## [0.0.0-alpha.19] — 2026-10-04

### Added

- `LibTmux.FSharp.Pane.sendLine`, `Pane.sendText` and `Pane.pressKey` type a
  line, type text, and press one key by its tmux name, without building a
  `SendKeysRequest`. (#53)
- `LibTmux.FSharp.PaneRun.Exited`, `Ended`, `NotStarted` and `TimedOut` match
  how a `Pane.run` ended, and `PaneWait.Found`, `Printed`, `Stopped`, `TimedOut`
  and `Ended` how a wait did, so a match that leaves an ending out draws a
  compiler warning. (#53)
- `LibTmux.FSharp.SessionSpec.running` describes a session whose one window
  runs a command, for `Server.newSession`. (#53)
- `LibTmux.FSharp.Session.rename`, `kill` and `newWindow`, `Window.rename`,
  `select`, `kill`, `selectLayout`, `resize` and `move`, and `Pane.select`,
  `kill`, `setTitle`, `resize`, `swap`, `respawn` and `clearHistory` act on a
  handle at the end of a pipe, one for each session, window and pane
  operation the MCP server offers, without naming the token as the core
  methods' optional parameters require. (#53)
- `LibTmux.FSharp.TmuxAsync.awaitTask` and `awaitUnitTask` await a call in an
  `async` workflow and raise a tmux client cancelled after it may have acted as
  itself, so `TmuxFailure.MayHaveRun` still matches it where `Async.AwaitTask`
  raises a bare `TaskCanceledException`. (#53)
- `LibTmux.FSharp.PaneWatch.Output`, `Paused`, `Continued`, `Dropped`, `Gone`
  and `Exited` match what `Control.watchPane` and `Control.watchPanes` yield,
  so a match that leaves out a loss report draws a compiler warning. (#53)
- `LibTmux.FSharp.Mirror.tryWaitUntil` returns `None` when no view matches in
  time, where `Mirror.waitUntil` raises. (#53)
- `LibTmux.FSharp.Retry.ifNotSentAfter` retries unsent work after each of a
  list of delays, for a server still starting. (#53)
- `LibTmux.FSharp.Server.createOwned` and `Server.connect` start and attach to
  a server without qualifying the core `LibTmux.Server` type. (#53)

### Fixed

- The `LibTmux.FSharp.TmuxFailure` patterns match a failure that
  `Async.AwaitTask` or `Task.Wait` wrapped in an `AggregateException`. In an
  `async` workflow they matched nothing, so a command that may have run fell
  through to whatever caught the rest. (#53)

### Changed

- **`LibTmux.FSharp.SplitSpec.Size` is a `SplitSize option`.** A string read
  as cells, or with a percent sign as a share, became `SplitSize.Cells` and
  `SplitSize.Percent`: write `Some(SplitSize.Percent 50)` for `Some "50%"`
  and `Some(SplitSize.Cells 20)` for `Some "20"`. `Server.newSession` raises
  `ArgumentOutOfRangeException` for fewer than one cell or a share outside 1
  to 100 before it creates anything. (#53)

## [0.0.0-alpha.18] — 2026-10-03

### Added

- `LibTmux.FSharp.Server` adds `tryFindSession`, `tryFindWindow`, and
  `tryFindClient`. Lookups return `None` for absent objects and propagate
  read errors and cancellation.
- `LibTmux.FSharp` reads every level through one query shape.
  `Server.sessions`, `Server.windows`, `Server.panes`, `Server.clients`,
  `Session.windows`, `Session.panes` and `Window.panes` return a `Query<'T>`;
  `Query.where`, `Query.showing` and `Query.whereUnsafe` narrow it, and
  `Query.list`, `Query.exactlyOne`, `Query.tryExactlyOne` and
  `Query.atMostOne` read it. Building a query reads nothing. (#51)
- `Query.list` passes a typed filter to tmux's `-f` where tmux can evaluate
  it, then rechecks every row, so a listing reads only rows that can match.
  Client listings are narrowed from tmux 3.4. (#51)
- `Query.atMostOne` returns `None` only when nothing matched and raises on
  several, for finding an object or creating it. Under NativeAOT, read one
  row with it or `Query.tryExactlyOne`: FSharp.Core formats the `Result`
  from `Query.exactlyOne` through `printf`, which NativeAOT publication
  rejects. (#51)
- `LibTmux.FSharp.Filter` adds `ne`, `eqIgnoreCase`, `startsWithIgnoreCase`,
  `endsWith`, `endsWithIgnoreCase`, `contains`, `containsIgnoreCase`,
  `matches`, `matchesIgnoreCase`, `lt`, `le`, `gt`, `ge` and `notOneOf`.
  (#51)
- `Query.showing` and `LibTmux.FSharp.Pane.findOnScreen` search a pane's
  visible rows with tmux's own search, as `find-window -C` does. (#51)
- `LibTmux.FSharp.Pane` adds `sendAndWait`, `sendAndWaitFor`, `waitForText`,
  `waitFor`, `waitUntil` and `run`. (#51)
- `LibTmux.FSharp.Server.newSession` creates a session described by
  `SessionSpec`, `WindowSpec` and `SplitSpec` records. (#51)
- `LibTmux.FSharp.Chain` runs a pipeline of named steps, `newWindow`,
  `splitLeftRight`, `splitTopBottom`, `sendLine` and `arrange`, in one tmux
  call. (#51)
- `LibTmux.FSharp.TmuxFailure` matches a failure as `NotSent`, `Ran` or
  `MayHaveRun`, and `Retry.ifNotSent` repeats an operation only when nothing
  it sent reached tmux. (#51)

### Fixed

- `LibTmux.FSharp` loads in a project built with the .NET 8 SDK, where it
  failed with `FileNotFoundException`. It now requires FSharp.Core 8.0.100 or
  later. (#51)
- `LibTmux.FSharp` task functions no longer resume on the caller's
  `SynchronizationContext`, so blocking on one from a UI thread cannot
  deadlock. (#51)

### Changed

- `Filter.oneOf`, `Filter.anyOf` and `Filter.allOf` accept an empty list:
  `oneOf []` and `anyOf []` match nothing, and `allOf []` matches everything.
  (#51)
- `Query.matching` and `Filter.toPredicate` no longer require unreferenced
  code, so trimmed and NativeAOT applications can call them. (#51)

### Removed

- **`LibTmux.FSharp.Server.listPanes` is removed.** Use
  `server |> Server.panes |> Query.list ct`. (#51)
- **`LibTmux.FSharp.Query.matchingWithCancellation` is removed.** Use
  `Query.matching`, or `Filter.toPredicate` in a sequence the caller can stop.
  (#51)
- **`LibTmux.FSharp.Control.iterEvents` and `foldEventsWhile` are removed.**
  Use `Control.events` with `Control.iter` or `Control.foldWhile`, which take
  any `IAsyncEnumerable`. (#51)

## [0.0.0-alpha.17] — 2026-09-27

### Added

- `LibTmux.FSharp` adds curried task helpers, typed portable filters,
  option-valued lookups and cardinality results over existing `LibTmux`
  handles and snapshots. It pins the matching `LibTmux` version;
  `Selection.exactlyOne` is unsupported under NativeAOT. (#35)

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
