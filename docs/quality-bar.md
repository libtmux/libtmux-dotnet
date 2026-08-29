# Quality bar (archived snapshot)

> **Archived evidence — not a current release claim.** This page records one
> older tree. Its API counts, macOS results, and Linux NativeAOT observations
> must not be cited for the alpha.8 tree; use fresh CI and release evidence.

A rating is worthless as an assertion, so this is the rubric and the evidence
behind each score. Every row names something a reader can check, and the check
is a command or a file rather than a claim.

This snapshot was assessed at `acd37f8` plus the documentation work that
followed it, against tmux 3.2a–3.7b on net8.0 and net10.0.

Scoring: **10** means nothing known is missing. **9.5** means the gaps are named
and are not defects. Below 9 means something is wrong rather than absent.

---

## Developer experience — 9.5

| Criterion | Evidence |
|---|---|
| Install to working code in one screen | `dotnet package add LibTmux --prerelease`, then a seven-line example, both in the first 30 lines of the README |
| One way to do a thing | Async only, no synchronous twins; every tmux-reaching call takes a `CancellationToken` |
| The mode you are in is visible at the call | `session.CreateWindowAsync`, `server.EnterControlModeAsync`, `server.Chain()` — never a flag in options |
| Failure says what to do next | `TmuxDispatchState` on every exception; the retry decision is an exception filter, [proved in tests](../tests/LibTmux.UnitTests/Exceptions/DispatchStateTests.cs) |
| Wrong queries fail at build time | A field outside the twelve-field catalog throws rather than silently filtering in memory |
| IntelliSense on every public member | `CS1591` is suppressed in **0** shipped projects, so an undocumented public member fails the build |
| Debuggable | SourceLink, `.snupkg` symbols, `EmbedUntrackedSources` on all four packages |
| One version across packages | `LibTmux.Workspace` and `LibTmux` always match; no compatibility table |

**The 0.5:** no analyzer ships with the package to catch misuse at the caller's
build, and there is no `net472`/`netstandard2.0` target for older consumers.
Both are absences, not defects.

## .NET best practices — 9.5

| Criterion | Evidence |
|---|---|
| Warnings are errors | `TreatWarningsAsErrors`, `AnalysisLevel 10-recommended`, `EnforceCodeStyleInBuild`, `Nullable enable` |
| Public API cannot drift silently | Roslyn `PublicAPI.{Shipped,Unshipped}.txt` **plus** `docs/public-api.json` and a reflection test comparing both directions |
| Trimming and AOT | `IsTrimmable`, `IsAotCompatible`, both analyzers on, and a [publish smoke test](../tests/LibTmux.AotSmoke) that runs the AOT binary |
| Reproducible restore | Central Package Management, lock files, `RestoreLockedMode` in CI, `Deterministic`, `ContinuousIntegrationBuild` |
| Multi-targeting is real | net8.0 and net10.0 both tested; net8.0 consumers resolve an 8.0 logging abstraction, [verified from the live feed](benchmarks/README.md) |
| Supply chain | 26/26 actions SHA-pinned, Dependabot maintaining them, CodeQL `security-and-quality`, OpenSSF Scorecard, OIDC trusted publishing with no long-lived key, a CycloneDX SBOM and signed build provenance for every published package |
| One dependency | `Microsoft.Extensions.Logging.Abstractions`; anything that would add another ships as its own package |

**The 0.5:** `LangVersion` is pinned to 12.0 while targeting net10.0, so newer
language features are unavailable. Deliberate, since net8.0 is a supported
target, but it is a constraint worth naming.

## tmux — 9.5

| Criterion | Evidence |
|---|---|
| Compatibility range is proven, not claimed | 7 tmux versions built from source and run against the full suite on every commit, behind a `compatibility` required check |
| Version differences are modelled | Capability model; every difference has a row in [`version-deltas.json`](parity/version-deltas.json) naming the test that proves it |
| Hostile output cannot crash a parser | 8,000 [fuzz cases](../tests/LibTmux.UnitTests/Fuzzing/ParserFuzzTests.cs) plus a corpus; refusal required, crash and hang forbidden |
| A query document is input, not instructions | Schema and version must be v1; string, pattern, dialect and regex-option limits enforced on **read**; each regex match is capped at 1s and aggregate matching accepts caller cancellation between nodes; a field resolves only through the catalog ([wire tests](../tests/LibTmux.UnitTests/Query/QueryJsonTrustBoundaryTests.cs), [execution tests](../tests/LibTmux.UnitTests/Query/QuerySemanticsTests.cs)) |
| A control session survives its consumer | Bounded event channel, bounded disposal that kills the client and not the server beneath it, and a waiter whose command never reached tmux is skipped rather than handed the next reply |
| A stale handle cannot hit a live server | Generation guard on one-shot **and** chained entity commands; a chain mixing servers is refused before it runs ([tests](../tests/LibTmux.IntegrationTests/Chaining/ChainGenerationTests.cs)) |
| All three transports | One-shot, control mode, and chaining, each measured and each working on every supported tmux |
| The macOS claim is measured | A `macos arm64` lane builds, unit-tests and runs the integration suite against real tmux on Apple silicon. **846 of 849 pass; 2 remain.** Advisory until those are diagnosed |
| Does not disturb other tmux users | Socket root of its own; the rules are in [AGENTS.md](../AGENTS.md) and enforced by a module initializer |
| Parity with the Python original is tracked | [Parity ledger](parity/parity-ledger.json) maps every Python symbol to where it went |

**The 0.5 is now a known deficit rather than an unknown one.** macOS was listed
as supported and had never been run; the first lane found 15 failing integration
tests. Thirteen were test assumptions about Linux rather than library defects
— `/bin/sh` reporting as `bash`, `/tmp` resolving to `/private/tmp`, and
packaging tests in a lane that does not pack. Two remain, both timing-shaped.
Until they are fixed the claim stays Linux, with macOS measured and reported. The lane also runs `net10.0` only and does not publish ahead of time
there, so the AOT claim is still proven on Linux alone.

## Examples — 9.5

| Criterion | Evidence |
|---|---|
| Examples compile | **51** C# blocks across the READMEs and mode documents are compiled by [a Roslyn harness](../tests/LibTmux.IntegrationTests/Documentation/ReadmeExampleTests.cs) in CI |
| Examples run | **32** of those are marked `csharp run` and execute against a live tmux, each on a socket of its own |
| Contract examples too | The four examples in `public-api.json` are compiled by the same harness |
| A broken example is a failing test | This is why `session.IsAttached` — which did not compile — cannot ship again |
| A standalone project | [`examples/LibTmux.Examples`](../examples/LibTmux.Examples) builds and runs in CI |

**The 0.5:** decision records contain examples that are deliberately *not*
compiled, because they quote what was run at the time. Correct, but it means not
every C# block in the repository is verified.

## Documentation — 9.5

| Criterion | Evidence |
|---|---|
| Every package has its own README | 4 packages, 4 READMEs, all shipped inside the package and rendered on nuget.org |
| API reference cannot drift | Rendered from the compiler's XML output |
| Decisions are recorded with evidence | 5 ADRs quoting the commands that produced their conclusions |
| Performance claims are recorded | [Benchmark runs](benchmarks/README.md) name tmux, host, runtime, commit and date, with min/median/mean/p90/p95/p99/max over 100 samples |
| History is in one place | [CHANGELOG](../CHANGELOG.md), linked from the package release notes, because nuget.org shows notes per version |
| The project files a stranger looks for | `README`, `LICENSE`, `SECURITY`, `CONTRIBUTING`, `CODE_OF_CONDUCT`, `CHANGELOG`, issue templates |
| Contributor onboarding is specific | [CONTRIBUTING](../CONTRIBUTING.md) names the five edits a public API addition needs, and the socket rules |
| Mistakes are documented | The warmup artefact that put an impossible benchmark number in the README is written down where the next person will look |

**The 0.5:** the documentation site is markdown in the repository rather than a
published, versioned doc site.

---

## How to measure a current tree

```console
$ bash eng/quality/measure.sh
```

The script prints current raw measures; it does not refresh this archived prose.
Current compatibility, package, API, and platform claims require their named CI
or release gates on the exact tree being assessed.
