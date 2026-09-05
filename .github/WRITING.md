# Writing

How this project writes: `README.md`, `CHANGELOG.md`, release notes, commit
messages, XML documentation, source comments, and error messages. It governs
every surface a reader reaches, and applies to a one-line `<summary>` as much
as to a release announcement.

[CONTRIBUTING.md](CONTRIBUTING.md) covers how we work — the toolchain, the
gates, what a change has to carry. This covers how we write.

## Voice

Calm, literal, precise, useful. Write as though the reader is competent, busy,
and may eventually have to debug this library at 2 a.m.

- **Understated, not enthusiastic.** "Reads one option." Not "Effortlessly
  access the full power of tmux options!"
- **Declarative, not chatty.** "Pass `-l` when it is set." Not "All you have to
  do is set the flag."
- **Specific, not clever.** "tmux 3.4 escapes a dollar sign twice." Not
  "Handles version quirks intelligently."
- **Outcome before implementation.** Say what the caller observes, then how it
  works if that knowledge is part of the contract.
- **Sentence-case headings.** "Running the tests", not "Running The Tests".
- **Adjectives only when falsifiable.** `allocation-free`, `thread-safe`,
  `trim-safe`, `O(n)` carry information. `powerful`, `seamless`, `robust`,
  `blazing-fast` do not.

The most useful editing operation is deleting the introductory sentence.

| Instead of | Prefer |
| --- | --- |
| "We added…" | "`Pane.CaptureAsync` now…" |
| "New and improved" | "`Foo` now…" |
| "powerful", "seamless" | state the capability |
| "easily", "simply" | omit |
| "robust" | name the failure that is handled |
| "comprehensive" | name what is covered |
| "production-ready" | state the guarantee |
| "optimized" | give the magnitude |
| "various fixes" | name the components |
| "under the hood" | omit unless observable |
| "please note that" | state the fact |
| "leverage", "utilize" | "use" |
| "delve into" | "read", or omit |
| "best practices" | name the practice |
| "in order to" | "to" |

## README

The README is an onboarding path, not the project's biography. It is also
package metadata: nuget.org renders it, so it is the first thing most readers
of this library ever see.

The first screen carries the title, one sentence saying exactly what the
library does, the badges, and a minimal example that compiles. Everything else
comes after.

Show the golden path first, then configuration, then the advanced case.
Architecture and design notes go late, or in `docs/` — a reader wants to know
whether they need the library before they are told how it is built.

State compatibility explicitly rather than making somebody open a `.csproj`.
The supported tmux versions, the target frameworks, the supported operating
systems, and trim and ahead-of-time safety each get a row.

Examples are product surface. Every one is compilable, copyable, idiomatic,
and explicit about setup — no elided `using` directives, no `// setup omitted`,
no placeholder methods that do not exist. An example that assumes invisible
context is hostile to a newcomer and useless to an agent. If something is
deliberately left out, say so in prose.

Put exact symbols in prose. "Set `SendKeysRequest.Literal` to `true`" beats
"turn on the literal flag" — it lets a reader jump straight to the code.

Prefer boring, predictable headings. "Compatibility" and "Documentation"
retrieve well; "When things get weird" does not.

## Changelog

`CHANGELOG.md` is a consumer-facing compatibility ledger, not `git log`. Every
bullet answers one question: what changed for somebody consuming the package?
A refactor nobody can observe is not an entry.

One change per bullet. Lead with the identifier and a concrete verb — add,
fix, remove, deprecate, `now`, `no longer`. Name identifiers literally:
`Pane.CaptureAsync`, `TMUX_TMPDIR`, `tmux://capabilities`.

Group under `### Added`, `### Changed`, and `### Fixed`. Bold the opening
sentence of anything a reader must act on; leave the rest plain.

Entries land under `## [Unreleased]`. The maintainer assigns the version when
cutting a release, so nothing here predicts one. A released heading is
`## [0.0.0-alpha.N] — YYYY-MM-DD`, and every bracketed heading gets a matching
reference-link definition at the bottom of the file — a bracket with no
definition renders as literal text.

Do not sell a fix. "No longer truncates panes wider than 500 columns", not
"improves capture reliability". Do not describe effort. Give the old behaviour
only where it explains a break.

State a changed default explicitly, and an incompatibility more explicitly
still, with the way forward in the same bullet. For a break worth spelling
out, use the four-part frame:

```markdown
**`Server.KillAsync` now throws instead of returning `false`.**

- Previous behaviour: returned `false` when no server was running.
- New behaviour: throws `TmuxNotRunningException`.
- Reason: the silent `false` masked a misconfigured socket path.
- Recommended action: check `Server.IsRunning`, or catch the exception.
```

## Release notes

The changelog is archival; a release page answers why anyone should care about
*this* release. It is the changelog plus prioritization.

Lead with one paragraph: what is shipping, who should care, and whether
upgrading is safe. Then highlights, then breaking changes, then the full list.
Do not pretend every patch is a product launch — for a patch, one paragraph
and a `Fixed` list is the whole thing.

Every claim carries evidence. "Serialization allocates one fewer intermediate
buffer per command" is a claim; "significantly faster" is not. A performance
number names the benchmark, the tmux version, the host, and the date, and is
stated as a ratio or a marginal cost, because absolute milliseconds move by a
factor of five between machines.

This project has published no release pages yet, so this section is a bar to
meet rather than a description of what exists.

## Commit messages

```
Scope(type[detail]): concise description

why: Explanation of necessity or impact.

what:
- Specific technical changes made
- Focused on a single topic
```

Keep the subject to 72 characters or fewer, excluding any trailing `(#NN)`
pull request reference; 50 or fewer is better and most of the history manages
it. Wrap body lines at 72. Separate the `why:` and `what:` blocks with a blank
line. No emoji, anywhere.

Common types:

- **feat**: New features or enhancements
- **fix**: Bug fixes
- **refactor**: Code restructuring without functional change
- **docs**: Documentation updates
- **chore**: Maintenance (dependencies, tooling, config)
- **test**: Test-related updates
- **style**: Code style and formatting
- **dotnet(deps)**: Dependencies
- **dotnet(deps[dev])**: Dev dependencies
- **ai(rules[AGENTS])**: AI rule updates

Example:

```
Pane(feat[SendKeys]): Add support for a literal flag

why: Send characters without tmux interpreting them.

what:
- Add a Literal property to SendKeysRequest
- Pass -l when it is set
```

The body explains why this implementation exists. The diff already says which
statements changed, so a body that restates the diff carries nothing.

Conventional Commits (`feat:`, `fix!:`, `BREAKING CHANGE:` footers) are
deliberately not used here. The format above predates them in this repository,
every commit follows it, and no tooling consumes the Conventional form. Do not
introduce it.

Use a heredoc so the formatting survives the shell:

```console
$ git commit -m "$(cat <<'EOF'
Scope(feat[detail]): Concise description

why: Explanation of the change.

what:
- First change
- Second change
EOF
)"
```

### Release commits

Never create tags. Never push tags. The owner handles tagging and tag pushes,
because a tag matching `v*` triggers the publish workflow.

A release commit subject is plain and short: `Tag v<version>`. The detailed
why and what go in the body. Do not use the `Scope(type[detail]):` format for a
release — it buries the lede.

## API documentation

Every public member carries XML documentation. `CS1591` is unsuppressed in all
four shipped projects and `TreatWarningsAsErrors` is on, so a missing comment
is a build error rather than a warning.

`<summary>` is one sentence on one line. Start with a verb: `Gets` for a
property, an active present-tense verb for a method — `Reads`, `Runs`,
`Returns`, `Creates`, `Sends` — and `Represents`, `Provides`, or `Describes`
for a type.

```csharp
/// <summary>Reads one option.</summary>
```

Do not restate the identifier. "Gets the timeout" says nothing the signature
did not; "Gets the maximum time allowed for each attempt before it is
canceled" says what the type cannot.

`<remarks>` carries everything longer, in `<para>` blocks. It is a separate
tag, never folded into `<summary>`. This is where the facts that make a
library trustworthy live — thread safety, ownership and disposal, cancellation
semantics, ordering, lifetime, and the tmux quirk behind a design:

```csharp
/// <remarks>
/// tmux answers commands in the order it received them, so this is safe to
/// call concurrently: each caller gets its own answer rather than someone
/// else's. Cancelling stops the wait, not the command; tmux has already
/// been told.
/// </remarks>
```

`<exception>` names the condition, not the type. "More than one of direction,
explicit size, and mode is set" is useful; "Thrown when the request is
invalid" is not. Document every exception a caller can reasonably hit.

Use the semantic tags rather than formatting by hand: `<see cref="..."/>`,
`<paramref name="..."/>`, `<c>null</c>`, `<para>`. They flow through
IntelliSense and the generated reference; hand-formatting does not.

`<inheritdoc />` is bare, with no `cref`, and only where inheritance genuinely
means identical semantics — `Equals`, `GetHashCode`, `Dispose`, an interface
implementation. Do not use it to avoid documenting a subtly different override.

`<example>` and `<code>` appear exactly once in this codebase, on
`LibTmuxException`, where the catch pattern is non-obvious enough to earn a
runnable snippet. That is the bar. Do not add them by default — a summary and
remarks that state the contract are what this library relies on, and README
and `docs/` carry the worked examples.

## Source comments

A comment ships only if it passes all three gates. Fail any: delete or
rewrite. Borderline: delete — borderline means the information is
reconstructible, which is what makes deletion cheap.

**Loss.** Three years from now, would losing this cost a maintainer real time
rediscovering intent, an invariant, a constraint, or a failure mode the code
and tests do not already make obvious?

**Elite.** Would SQLite, Redis, the Go standard library, or CPython write this
comment, at this length? Those projects state the constraint and stop. They do
not argue with an imagined objector.

**Upkeep.** Will it stay true without maintenance? A comment that hand-syncs a
value the code owns — a count, an offset, a line reference, a duplicated
constant — is false the first time that value moves.

### Ceiling

Two or three lines is the working norm here, and four is the ceiling. A
comment reaching four is either carrying several facts, in which case split
it, or arguing, in which case cut it to the fact.

Rationale, alternatives weighed, and the story of how the code got here belong
in the commit message: timestamped, attached to the exact diff, and free to
maintain.

A comment often holds both a constraint and the deliberation that found it.
Keep the constraint, cut the deliberation. "Runs at most once per second"
survives; "this is the right trade for now" does not.

### Keep

- Why over how: upstream quirks, protocol and compatibility constraints,
  performance tradeoffs still part of the contract.
- Invariants, preconditions, ordering, lifetime, and concurrency requirements
  that types and tests cannot express.
- Code that looks wrong but is not, so a later cleanup does not reintroduce
  the bug.
- A high-level sketch of an algorithm whose local operations do not reveal the
  whole.

### Delete

- Narration of the next lines; code translated into English.
- Restated names, types, defaults, or control flow.
- Values duplicated from the code and hand-synced.
- Justification, hedging, or apology for a choice.
- Speculation about future requirements.
- History version control already holds, including commented-out code.
- Ticket and issue numbers. They say nothing to a reader without tracker
  access, and they rot when the tracker moves. Unfinished work goes in the
  tracker, not the source.
- Transient observations — "currently", "for now", "the latest release" —
  that go stale with no nearby edit.

### The upkeep gate in practice

It reaches values that track our own code. It does not reach frozen external
facts.

Bad (Delete):

```csharp
// There are 321 tests to complete for servers.
```

Good (Keep):

```csharp
// tmux < 3.2 reports the pane ID only after the command completes,
// so this query must stay separate.
```

### Documentation exception

Minimal usage examples, and `<param>`, `<returns>`, and `<exception>` lines on
public API are exempt from the loss gate — they serve the caller, not the
maintainer. They are exempt from nothing else. Ceiling: a good man page entry.

## Terminology and capitalization

`tmux` is always lowercase, including at the start of a sentence. Recast
nothing to avoid it.

`libtmux` names the product, the GitHub organisation, the command-line tool,
and the Python library this one ports. `LibTmux` names the C# package,
namespace, and type. The two are never swapped.

One noun per concept, everywhere: server, session, window, pane, client,
option, hook, buffer. If it is a pane, it is not also a view, a region, or a
split. Synonym rotation costs a reader precision and costs grep, search, and
an agent a match.

## Markdown

Wrap prose at 80 columns. Badges, table rows, fenced code, and a line
dominated by a single URL are exempt — breaking those hurts more than the
column costs.

No GitHub alert blocks. `> [!NOTE]`, `> [!WARNING]` and the rest render as
literal text everywhere except GitHub, and this project's README is rendered
by nuget.org, which is where most readers meet it. A plain blockquote, or just
a sentence, renders everywhere.

Tables, badges, and links are fine.

Never wrap a pull request or issue body. GitHub renders a single newline as a
space inside a file and as a line break inside a comment, so a hand-wrapped
comment arrives as ragged stubs.

## Code blocks

Code blocks are paste-and-run units: pasting one block runs exactly one
intended action. Executed examples are exempt — the test suite runs them,
nobody pastes them.

- **One command per block.** Multiple steps may share a block only when
  explicitly chained with `&&`, `;`, or `\` continuations — the chain is then
  one logical command.
- **Explanations go in prose above the block**, never as `#` comments inside
  it.
- **Command menus are per-command blocks with prose lead-ins**, not tables.
- **Shell commands use the `console` tag with a `$ ` prefix.** This separates
  interactive commands from scripts and enables prompt-aware copy.
- **Split long commands with `\`** — one flag or flag+value pair per indented
  continuation line, positional arguments last.

Good:

Show the last ten commits as a graph:

```console
$ git log \
    --max-count=10 \
    --graph \
    --oneline
```

Bad:

```console
# Show the last ten commits as a graph
$ git log --max-count=10 --graph --oneline
```

### C# blocks are tests

Every C# block in a shipped document is compiled against the real assemblies,
and a block tagged ```` ```csharp run ```` is additionally executed against a
tmux server of its own. `ReadmeExampleTests` does both, across the root README,
each package README, and `docs/modes/`. An example is either true or a failing
test, which is what stops one that cannot work from being rendered on a package
page.

So tag a block `csharp run` when it is meant to execute, and leave it plain
`csharp` when it only illustrates. A plain block still has to compile.

Write examples accordingly. The harness supplies a preamble and hoists type
declarations, but everything else has to be real: no `// setup omitted`, no
method that does not exist, no magic constant the reader cannot resolve.

Decision records under `docs/decisions/` are deliberately outside this. They
quote what was run at the time, and an example edited later to keep compiling
records nothing.

### Anchored snippets

Anchoring is a second guarantee on top of compilation, and it catches a
different failure: prose drifting from the code it was copied from.

A block wrapped in `<!-- snippet: Name -->` and `<!-- endsnippet -->` is
materialized from a `#region` of the same name in
`examples/LibTmux.Examples/Snippets/`, so the document holds a verbatim copy of
code that runs. The copy is materialized rather than transcluded because these
are package READMEs and nuget.org renders the Markdown it is given without
resolving anything.

The loop runs one way: edit the example, then bring the copy across. Never edit
the block in the document.

```console
$ uv run python eng/docs/sync_snippets.py
```

Prefer anchoring for an example a reader is likely to copy verbatim. A block
that only demonstrates a call shape does not need it.

[`examples/README.md`](../examples/README.md) has the mechanism in full — the
region markers, the `usings:` option, what each check fails on, and how to add
an example.

## Error messages

An exception message is a complete sentence: capitalized, period-terminated,
and naming the offending value. Where the caller could plausibly have gotten
it right, say what would have worked.

```csharp
throw new McpException($"No session '{trimmed}' exists. These do: {known}.");
```

Make failure modes searchable. "`ConnectAsync` throws `TmuxNotRunningException`
when no server is listening on the socket" lets a reader find the explanation
from either the method or the exception; "connection can fail" does not.

## Slop prevention

Treat AI slop as review-hostile noise, not as proof that text or code is
wrong. The goal is to maximise information density.

- **AI signatures.** No "Generated by", no conversational filler, no
  unexplained emoji, no tool metadata.
- **Brittle references.** No hard-coded line numbers, fragile file counts,
  dated "as of" claims, bare SHAs, or local absolute paths — unless they are
  strict evidentiary artefacts such as a benchmark log.
- **Diff narration.** Do not restate what moved, was renamed, or was removed
  in anything the reader holds alongside the diff: code, XML documentation,
  README, or a pull request description.
- **Branch-internal narrative.** Do not mention intermediate states, abandoned
  approaches, or "no longer" behaviour unless users of a published release
  actually experienced the old state.
- **Low-value scaffolding.** No ownerless TODOs, unused future-proofing, debug
  artefacts, or defensive wrappers around failure modes nothing can reach.
- **Prose inflation.** The diction table under [Voice](#voice) governs.
- **Coded labels.** Write rules and findings as plain imperatives. No `[R1]`,
  `Option B`, or any index a reader has to decode.

Preserve the why. Never delete a comment documenting an invariant, a protocol
constraint, a platform quirk, or an upstream workaround — those are the facts
[Source comments](#source-comments) keeps, and every other comment is judged
by it.
