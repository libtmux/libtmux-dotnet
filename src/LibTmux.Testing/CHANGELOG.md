# Changelog

Notable changes to `LibTmux.Testing`.

Releases are numbered `0.1.N`, and each release increments `N`. The API is not
settled: any release may change or remove exported identifiers, and this
changelog marks each break. Pin an exact version to avoid one. Releases before
0.1.0 were alphas numbered `0.0.0-alpha.N`.

The packages share one version, so every release since this package first
shipped has a section here. Changes that span packages, such as documentation,
CI, tests and release tooling, are in the [main changelog](../../CHANGELOG.md).
`0.0.0-alpha.13` was tagged but never published; the changes under it shipped in
the release after.

## [Unreleased]

### Added

### Fixed

### Changed

### Removed

## [0.1.1] — 2026-11-01

No entries for `LibTmux.Testing`.

## [0.1.0] — 2026-10-25

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.24] — 2026-10-25

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.23] — 2026-10-24

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.22] — 2026-10-19

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.21] — 2026-10-11

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.20] — 2026-10-04

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.19] — 2026-10-04

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.18] — 2026-10-03

### Fixed

- `LibTmux.Testing` gives each default test scope its own tmux socket, so
  parallel scopes cannot stop one another's servers. (#36)

## [0.0.0-alpha.17] — 2026-09-27

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.16] — 2026-09-26

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.15] — 2026-09-20

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.14] — 2026-09-20

No entries for `LibTmux.Testing`.

## [0.0.0-alpha.13] — 2026-09-19

### Changed

- **Test scopes move to the `LibTmux.Testing` package.** Add that package to
  projects using the testing helpers. (#28)

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
