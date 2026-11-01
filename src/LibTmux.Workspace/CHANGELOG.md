# Changelog

Notable changes to `LibTmux.Workspace`.

Releases are numbered `0.1.N`, and each release increments `N`. The API is not
settled: any release may change or remove exported identifiers, and this
changelog marks each break. Pin an exact version to avoid one. Releases before
0.1.0 were alphas numbered `0.0.0-alpha.N`.

The packages share one version, so every release since this package first
shipped has a section here. Changes that span packages, such as documentation,
CI, tests and release tooling, are in the [main changelog](../../CHANGELOG.md).
`0.0.0-alpha.11` and `0.0.0-alpha.13` were tagged but never published; the
changes under each shipped in the release after.

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

## [0.1.1] — 2026-11-01

No entries for `LibTmux.Workspace`.

## [0.1.0] — 2026-10-25

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.24] — 2026-10-25

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.23] — 2026-10-24

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.22] — 2026-10-19

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.21] — 2026-10-11

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.20] — 2026-10-04

### Added

- Validate workspaces, review immutable plans, and apply them with action and
  owned-cleanup journals. Cancellation preserves the caller token and partial
  state through `WorkspaceOperationCanceledException`. (#34)
- Load inherited workspace settings, command lists, Enter overrides, window
  indices, `global_options` and `options_after`. Resolve supplied variables,
  opt into host scripts, and export captured session structure. (#34)

### Changed

- **Workspace builds send input immediately by default.** Choose explicit
  cooperative readiness when startup commands can signal completion;
  inferred readiness and `PaneReadiness` settings are removed. (#34)

## [0.0.0-alpha.19] — 2026-10-04

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.18] — 2026-10-03

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.17] — 2026-09-27

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.16] — 2026-09-26

### Fixed

- `WorkspaceBuilder` rebalances between splits, so a window of five or more
  panes builds at 80x24 instead of failing with "no space for a new pane".
  (#26)
- `WorkspaceBuilder` applies a window's layout before sending any command
  into its panes, rather than after. (#26)
- `WorkspaceBuilder` waits for a pane's prompt under any session
  `default-shell`, not only zsh, before sending its first command. (#26)

## [0.0.0-alpha.15] — 2026-09-20

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.14] — 2026-09-20

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.13] — 2026-09-19

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.12] — 2026-09-12

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.11] — 2026-09-07

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.10] — 2026-08-30

### Added

- `WorkspaceBuilder` accepts a readiness timeout and `PaneReadiness.Auto`,
  `Always`, or `Never`, so callers choose whether pane commands wait for a
  prompt-like state. (#18)

### Changed

- **The workspace model is immutable, and `WorkspacePane.ShellCommand` becomes
  `ShellCommands`.** Construct `WorkspaceFile`, `WorkspaceWindow`, and
  `WorkspacePane` instead of setting properties; supplied collections are
  copied. `WorkspaceResult` freezes and compares collection contents. (#18)
- **`WorkspaceBuildException.PartialResult` exposes the session, windows, and
  panes created before workspace application failed.** Workspace builds are
  nontransactional; inspect the partial result for targeted cleanup or retry.
  (#18)
- **`WorkspaceFile.Parse` now rejects unknown or duplicate keys, unsupported
  tmuxp hooks and plugins, wrong value shapes, multiple documents, and
  oversized input instead of ignoring them.** Use the documented closed subset
  or run tmuxp for its Python features. (#18)

### Fixed

- Workspace building preserves scalar and ordered pane commands, uses the first
  pane's directory, lets the last focus flag win, reports rejected layouts,
  and waits for readiness without injecting probe text into pane input. (#18)

## [0.0.0-alpha.9] — 2026-08-22

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.8] — 2026-08-22

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.7] — 2026-08-16

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.6] — 2026-08-16

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.5] — 2026-08-16

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.4] — 2026-08-16

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.3] — 2026-08-16

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.2] — 2026-08-15

No entries for `LibTmux.Workspace`.

## [0.0.0-alpha.1] — 2026-08-15

- `LibTmux.Workspace` — sessions from tmuxp workspace files.

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
