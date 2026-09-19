# ADR 0007: One version for the suite

## Status

Accepted for the prerelease line. To be revisited at 1.0.

## Context

Six packages ship from this repository: `LibTmux`, `LibTmux.Query.Json`,
`LibTmux.Testing`, `LibTmux.Extensions.DependencyInjection`,
`LibTmux.Workspace` and `LibTmux.Mcp`. One `VersionPrefix` and one
`VersionSuffix` in `Directory.Build.props` cover all six, one tag names a
release, and one `PackageReleaseNotes` string is written for the suite because
naming a single package's changes there would print them on the other five
package pages.

That has a cost worth stating plainly. `LibTmux.Mcp` is an agent tool,
installed rather than referenced; `LibTmux` is the library everything else
depends on. Under one version, a release that only changed an MCP tool still
moves the library's number, and a consumer reading versions alone cannot tell
whether the library changed at all. The packages also depend on each other at
exactly the same version, which is what makes a mixed set unrepresentable.

## Decision

Keep one version for the suite through the prerelease line.

The changelog is the per-package record: each entry names what changed and
which package it belongs to. A version number is not asked to carry that.

Revisit at 1.0. From there the library's version is a compatibility promise to
everyone who references it, and a number that moves for reasons outside the
library devalues the promise. That is the point at which the cost of
independent versions — a compatibility matrix between packages, a tag and
release note per package, and a dependency range in place of an exact pin —
buys something.

## Alternatives

**A version per package.** Honest numbers, and it is where this ends up if the
suite outlives its prerelease line. It replaces one release with six, each with
its own notes, and turns the `version: same` dependency contract the package
inspector enforces into a range somebody has to reason about. During alpha,
where the public API can change in any release, that is bookkeeping without a
reader.

**An independent version for `LibTmux.Mcp` only.** It removes the largest
source of unrelated movement, since the MCP tool changes most often and is
referenced by nobody. It also means two schemes, two tags and two release
processes for one repository, and it does not help the four library packages
that still move together.

## Consequences

- A release may contain no change to a given package. The changelog says so;
  the version does not.
- `inspect_packages.py` keeps enforcing that inter-package dependencies are the
  same version, which one number makes trivially true.
- A consumer pinning an exact version, as the release notes ask during alpha,
  is unaffected either way.
- At 1.0 this decision is reopened rather than inherited.
