# Writing

This guide adds .NET, C#, F#, NuGet package, and XML documentation rules to the
organization baseline in
[`libtmux/.github` `WRITING.md`](https://github.com/libtmux/.github/blob/main/WRITING.md),
which governs voice, CommonMark formatting, commit and pull request shape, and
slop-free prose. [CONTRIBUTING.md](CONTRIBUTING.md) covers the toolchain, the
gates, and what a change has to carry.

Put exact C# and F# symbols in prose (`SendKeysRequest.Literal`,
`Pane.CaptureAsync`, `Query.list`) rather than paraphrasing them, and use
adjectives only when falsifiable (`allocation-free`, `thread-safe`, `trim-safe`,
`O(n)`). Avoid hard-coded line numbers, fragile file counts, dated "as of"
claims, bare SHAs, and local paths outside evidentiary benchmark logs.

## README shape

Every README is an onboarding path, and each package README is rendered directly
by nuget.org.

- **Header.** Open with the shared `<!-- libtmux-logo -->` block, title, badge
  row (CI, NuGet version, libtmux.org, license), and a one-sentence pitch. The
  root `README.md` centers its title block (`<div align="center">`); package
  READMEs wrap any HTML centering in `<!-- libtmux-logo -->` markers so the pack
  step strips HTML before nuget.org renders the file. Never use GitHub alert
  blocks (`[!NOTE]`, `[!WARNING]`): nuget.org renders them as literal text.
- **Links.** Package READMEs under `src/` must use absolute `https://` or `#`
  links because nuget.org cannot resolve relative repository paths.
- **Sections are tasks.** Open with `## Quick start`, then name each `##`
  heading as a task in the reader's words ("Find sessions, windows, and panes").
- **Headings are the API.** Each `###` heading names the symbol in code font,
  linked to its reference page on libtmux.org, followed by a colon and its job
  in plain words.
- **Under a heading.** Keep prose to two or three lines (`ReadmePackageListTests`
  rejects paragraphs and list items over 240 rendered characters), the example,
  what it prints, at most one short note, and a `More:` link to the guide or
  contract page.
- **Compatibility floors.** State actionable floors (`tmux 3.3a or newer`,
  `net8.0` and `net10.0`, OS support, trimming and NativeAOT) rather than CI
  matrix or host build narration.
- **Packages and closing.** Keep the package index table and one `###` heading
  per package linking its directory on `master`, NuGet page, reference on
  libtmux.org, and `CHANGELOG.md`. Close with `## What it leaves out` and
  `## Support and status` as short bold-lead bullets.
- **Self-contained examples.** The first example in each section starts with its
  `using` or `open` lines, starts an isolated server through
  `Server.CreateOwnedAsync` or `Server.createOwned`, and cleans up on disposal
  without shell `tmux` setup. Keep every code line within 80 columns.
- **Headline features.** The root `README.md` shows filtering and queries,
  `QueryPlan<T>`, command chains, and control mode.

## Changelog

`CHANGELOG.md` is a consumer-facing compatibility ledger. Every bullet answers
what changed for somebody consuming the package, leading with the identifier and
a concrete verb (`Pane.CaptureAsync`, `TMUX_TMPDIR`, `tmux://capabilities`)
under `### Added`, `### Changed`, or `### Fixed` beneath `## [Unreleased]`. Bold
the opening sentence of anything a reader must act on. Released headings use
`## [0.1.N] — YYYY-MM-DD` with matching reference-link definitions at the bottom
of the file.

Each published package keeps its own `CHANGELOG.md` beside its project file
(`src/LibTmux/CHANGELOG.md`, `src/LibTmux.FSharp/CHANGELOG.md`,
`src/LibTmux.Mcp/CHANGELOG.md`, and the rest). Entries belong in the file of the
package they change; root `CHANGELOG.md` holds cross-package changes, docs, CI,
tests, benchmarks, and release tooling. An entry lives in one file. All packages
share one version: a release with no package-specific changes carries
``No entries for `<Package>`.`` under that heading, and relative repository
links inside `src/<Package>/CHANGELOG.md` start with `../../`.

For a breaking change worth spelling out, use the four-part frame:

```markdown
**`Server.KillAsync` now throws instead of returning `false`.**

- Previous behaviour: returned `false` when no server was running.
- New behaviour: throws `TmuxNotRunningException`.
- Reason: the silent `false` masked a misconfigured socket path.
- Recommended action: check `Server.IsRunning`, or catch the exception.
```

## Release notes

Lead with one paragraph: what is shipping, who should care, and whether
upgrading is safe, followed by highlights, breaking changes, and the full list
(for a patch release, one paragraph and `Fixed` is enough). Performance claims
must state a ratio or marginal cost and name the benchmark, tmux version, host,
and date.

## Commit messages

Follow the `Scope(type[detail]): concise description` format, 50-character
subject target, and 72-character `why:` / `what:` body limits in
[`libtmux/.github` `WRITING.md`](https://github.com/libtmux/.github/blob/main/WRITING.md).

Repository-specific conventions:

- Common scopes and types include `feat`, `fix`, `refactor`, `docs`, `chore`,
  `test`, `style`, `dotnet(deps)`, `dotnet(deps[dev])`, and
  `ai(rules[AGENTS])`.
- Do not use Conventional Commits (`feat:`, `fix!:`, `BREAKING CHANGE:`
  footers).

### Release commits

Never create tags and never push tags: a tag matching `v*` triggers the NuGet
trusted-publishing workflow, which the owner runs. A release commit subject is
plain `Tag v<version>`, with `why:` and `what:` in the body.

## API documentation

Every public member carries XML documentation. `CS1591` is unsuppressed in every
shipped project and `TreatWarningsAsErrors` is on, so a missing comment is a
build error.

`<summary>` is one sentence on one line. Start with `Gets` for a property, an
active present-tense verb for a method (`Reads`, `Runs`, `Returns`, `Creates`,
`Sends`), and `Represents`, `Provides`, or `Describes` for a type:

```csharp
/// <summary>Reads one option.</summary>
```

Do not restate the identifier: say what the signature and type cannot.

`<remarks>` carries longer contract details in `<para>` blocks, never folded
into `<summary>` — thread safety, ownership and disposal, cancellation
semantics, ordering, lifetime, and tmux version quirks:

```csharp
/// <remarks>
/// tmux answers commands in the order it received them, so this is safe to
/// call concurrently: each caller gets its own answer rather than someone
/// else's. Cancelling stops the wait, not the command; tmux has already
/// been told.
/// </remarks>
```

`<exception>` names the condition rather than restating the exception type, for
every exception a caller can reasonably hit.

Use semantic XML tags (`<see cref="..."/>`, `<paramref name="..."/>`,
`<c>null</c>`, `<para>`) so IntelliSense and rendered API docs resolve them. Use
bare `<inheritdoc />` (with no `cref`) only where inheritance carries identical
semantics (`Equals`, `GetHashCode`, `Dispose`, interface implementations).

`<example>` and `<code>` appear only on `LibTmuxException`, where the exception
filter pattern warrants an inline snippet; worked examples live in READMEs and
`docs/`.

## Source comments

A comment ships only if it passes all three gates; borderline comments are
deleted.

- **Loss.** Would losing it cost a maintainer real time rediscovering an
  invariant, protocol or tmux version constraint, ordering or lifetime rule, or
  failure mode not already expressed by types and tests?
- **Elite.** State the constraint and stop, in two or three lines (four is the
  ceiling; split distinct facts or cut deliberation into the commit message).
- **Upkeep.** Never hand-sync counts, offsets, line numbers, ticket IDs, or
  transient observations ("currently", "for now") that go stale without a nearby
  edit. Frozen external facts are fine:

```csharp
// tmux < 3.2 reports the pane ID only after the command completes,
// so this query must stay separate.
```

Never delete a comment that documents a real invariant, protocol constraint,
platform quirk, or upstream workaround. Minimal usage examples and `<param>`,
`<returns>`, and `<exception>` tags on public API are exempt from the loss gate
because they serve callers rather than maintainers.

## Terminology and capitalization

`tmux` is always lowercase, including at the start of a sentence.

`libtmux` names the product, the GitHub organization, the command-line family,
and the Python library. `LibTmux` and `LibTmux.FSharp` name the .NET packages,
namespaces, and types. Never swap them.

Use one noun per concept everywhere: server, session, window, pane, client,
option, hook, buffer. Never rotate synonyms such as view, region, or split for a
pane.

## Code blocks

Keep one logical command per `console` block with a `$ ` prompt prefix,
splitting long commands with `\` (one flag per indented continuation line) and
putting explanations in prose above the fence rather than `#` comments inside
it. Hold code lines in documentation and example fences within 80 columns.

### C# blocks are tests

`ReadmeExampleTests` compiles every `csharp` fence across root `README.md`,
each package `README.md`, and `docs/modes/` against the built assemblies, and
executes every ```` ```csharp run ```` block against an isolated tmux server.
Write complete, real code with no `// setup omitted` or invented helpers.
Historical decision records under `docs/decisions/` are exempt.

### Anchored snippets

Blocks wrapped in `<!-- snippet: Name -->` and `<!-- endsnippet -->` are
materialized from `#region` blocks in `examples/LibTmux.Examples/Snippets/` (and
F# `<!-- fsharp-snippet: Name -->` blocks from `examples/LibTmux.FSharp.*`) so
nuget.org receives standalone Markdown that cannot drift from tested source.
Edit the source file first, then sync:

```console
$ uv run python scripts/docs/sync_snippets.py
```

```console
$ uv run python scripts/docs/sync_fsharp_snippets.py
```

See [`examples/README.md`](../examples/README.md) for region markers and
`usings:` options.

## Error messages

Exception messages are complete sentences: capitalized, period-terminated,
naming the offending value, and stating a valid alternative when the caller can
act on it:

```csharp
throw new McpException($"No session '{trimmed}' exists. These do: {known}.");
```

Name both the method and the exception in prose ("`ConnectAsync` throws
`TmuxNotRunningException` when no server is listening on the socket") so failure
modes stay searchable.
