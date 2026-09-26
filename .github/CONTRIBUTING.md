# Contributing

Thanks for looking. This repository is `libtmux` for .NET: a typed,
asynchronous client for tmux, plus a query layer, a workspace builder, and an
MCP server. The gates below are what a change has to pass.

This file is how we work. For how we write — README prose, `CHANGELOG.md`,
release notes, commit messages, XML documentation, source comments, and error
messages — follow [WRITING.md](WRITING.md). Read it before changing any of
them.

## Getting set up

`dotnet` is pinned to `10.0.302` by [`global.json`](../global.json) with
`rollForward: disable`, and again by `.tool-versions`. It resolves through
[mise](https://mise.jdx.dev) locally, so it is not on `PATH` — every command
below is prefixed accordingly:

```console
$ mise exec -- dotnet build LibTmux.slnx --configuration Release --warnaserror
```

CI does not use mise. It installs the same SDK with `actions/setup-dotnet`
from `global-json-file` and calls `dotnet` directly, so a workflow file never
carries the prefix.

Compiler inventory and package inspection run through `eng/LibTmux.Engineering`.
The Python validators run through [uv](https://docs.astral.sh/uv/).
There is no Python project file — each script carries its own PEP 723 header,
and a script with dependencies carries a `.lock` beside it so the gate resolves
the same versions every run:

```console
$ uv lock --script eng/run_tests.py
```

Run the engineering tests through that locked runner rather than naming pytest
on the command line, which resolves whatever released that morning:

```console
$ uv run --locked --script eng/run_tests.py
```

Parity tests read a pinned revision of the Python library. Point
`LIBTMUX_PYTHON_REPOSITORY` at a checkout of
[tmux-python/libtmux](https://github.com/tmux-python/libtmux) that contains it,
or they fail saying which revision they wanted.

You also need a real `tmux`, version 3.2a or newer. The suite drives one
rather than mocking it, because this library's job is being right about tmux
and only tmux can say whether it is.

Name that tmux explicitly when the machine has more than one:

```console
$ export LIBTMUX_TMUX=/usr/local/bin/tmux
```

Without it the tests spawn whatever `tmux` their own `PATH` resolves, while a
command they send into a pane resolves it again through that pane's
interactive shell. A version-matrix install earlier on the interactive `PATH`
makes those two different binaries, and a client cannot talk to a server of
another version. What you see is `run_shell_command` timing out with no exit
status, which reads as a library bug rather than as two tmuxes.

## Own your tmux socket root

Give this repository a socket root of its own before running anything:

- Tests: `TMUX_TMPDIR=/tmp/libtmux-dotnet-test`
- Servers you start by hand: `TMUX_TMPDIR=/tmp/libtmux-dotnet-dev`

This matters more than it looks. Several libtmux ports live on this machine
and run real tmux at the same time. A socket in the default root is reachable
by all of them, so one port's cleanup sweep kills another port's servers
mid-run — and the failure surfaces in whichever suite noticed first, which is
rarely the one that caused it. That misattribution is what turns socket
sharing into a debugging loop.

tmux reads `TMUX_TMPDIR` when it execs and puts a `-L name` socket in
`$TMUX_TMPDIR/tmux-$UID/name`, so exporting it before the run is enough. A
`-S path` socket ignores it and needs a path under the root instead.

Two things are never safe here, because the processes and directories belong
to other workspaces:

- `pkill tmux`, or any kill by a pattern matching more than your own root
- deleting `/tmp/tmux-$UID/` or another port's root

To find what this repository left behind, list its root rather than matching
process names:

```console
$ ls /tmp/libtmux-dotnet-test/tmux-$(id -u)
```

A socket file outlives the server that made it, so read that listing as
candidates and confirm each with `has-session` before deciding it is alive.

## Building

Restore against the lock files, check formatting, then build with warnings as
errors:

```console
$ mise exec -- dotnet restore LibTmux.slnx --locked-mode
```

```console
$ mise exec -- dotnet format LibTmux.slnx --verify-no-changes --no-restore
```

```console
$ mise exec -- dotnet build \
    LibTmux.slnx \
    --configuration Release \
    --no-restore \
    --warnaserror
```

The build is the style guide. `TreatWarningsAsErrors`, `Nullable`,
`EnforceCodeStyleInBuild`, and analyzers at `10-recommended` are set
repository-wide, and `CS1591` is unsuppressed in every shipped project. The
separate format check owns formatting. A clean build verifies analyzer rules
and the presence of public XML comments.

## Running the tests

Unit tests run on both target frameworks:

```console
$ mise exec -- dotnet test \
    --project tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj \
    --configuration Release \
    --framework net8.0 \
    --no-build \
    --minimum-expected-tests 1
```

The integration suite drives a real tmux and needs the socket root above:

```console
$ mise exec -- dotnet test \
    --project tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj \
    --configuration Release \
    --framework net10.0 \
    --no-build \
    --minimum-expected-tests 1
```

## Checks that must pass

[dotnet.yml](workflows/dotnet.yml) owns the primary Linux checks and the
Windows build/unit lane. Its `gate` job requires both `build` and `windows`.
Aggregate jobs reject failed, cancelled, or skipped prerequisites.

| Guarantee | Owner |
| --- | --- |
| Public declarations and nullability | PublicApiAnalyzers and shared `PublicAPI.*.txt` baselines |
| Public XML comments and documentation identities | C# compiler and Roslyn inventory |
| Ownership, parity destinations, platform and I/O policies | `verify_public_api.py` over compiler metadata |
| Current package asset compatibility | SDK Package Validation; no historical baseline |
| Workflow syntax and repository policy | Pinned actionlint and `verify_workflows.py` |
| Package contents, dependency versions and source identity | Native `packages` command |
| Runtime behavior | Unit, integration, example, packed-consumer and AOT owners |

### Packages and standalone consumers

Integration tests need a build and tmux. Package checks run separately. Build
with normalized source paths before packing locally; GitHub sets this through
`CI=true`:

```console
$ mise exec -- dotnet build \
    LibTmux.slnx \
    --configuration Release \
    --no-restore \
    --warnaserror \
    -p:ContinuousIntegrationBuild=true
```

Generate compiler inventory at the same revision as the build:

```console
$ mise exec -- dotnet run \
    --project eng/LibTmux.Engineering \
    --configuration Release \
    --no-build \
    -- api-inventory . artifacts/api-inventory.json
```

Use an empty package output directory for a new version. The inspector rejects
extra versions and unexpected packages:

```console
$ mise exec -- dotnet pack \
    LibTmux.slnx \
    --configuration Release \
    --no-build \
    --output artifacts/packages
```

```console
$ mise exec -- dotnet eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll \
    packages . artifacts/packages artifacts/api-inventory.json
```

The inspector evaluates package IDs, frameworks and dependency versions with
MSBuild. It reads NuGet archives, compares packaged README bytes and public XML
content, and checks portable PDB identity and SourceLink against `HEAD`. The
compiler inventory travels with the packages so the publisher repeats this
inspection without rebuilding the libraries.

The outer-loop regression suite mutates real packages:

```console
$ LIBTMUX_PACKAGE_ARTIFACTS=artifacts/packages mise exec -- \
    uv run --locked --script eng/run_tests.py --quiet -m packaging
```

Use a fresh package cache for each standalone consumer run. Source mapping in
`tests/NuGet.config` makes repository packages resolve from the local pack:

```console
$ NUGET_PACKAGES="$(mktemp -d)" mise exec -- bash -euc '
    dotnet restore \
        tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj \
        --configfile tests/NuGet.config
    for framework in net8.0 net10.0; do
        dotnet run \
            --project tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj \
            --configuration Release \
            --framework "$framework" \
            --no-restore
    done'
```

The AOT owner publishes and executes each native binary once:

```console
$ NUGET_PACKAGES="$(mktemp -d)" mise exec -- bash -euc '
    dotnet restore \
        tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj \
        --runtime linux-x64 \
        --configfile tests/NuGet.config
    for framework in net8.0 net10.0; do
        dotnet publish \
            tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj \
            --configuration Release \
            --framework "$framework" \
            --runtime linux-x64 \
            --no-restore
        "tests/LibTmux.AotSmoke/bin/Release/$framework/linux-x64/native/LibTmux.AotSmoke"
    done'
```

Both commands fail when their executable fails. Keep diagnostic output and
socket isolation in the executables; do not add integration wrappers that run
the same payload again. `LibTmux.PackageConsumer` and `LibTmux.AotSmoke` stay
outside `LibTmux.slnx` because they restore packed artifacts.

Only `LibTmux.AotSmoke` names a runtime identifier. Its restore is separate
from the portable library graph. Its package inputs retain the development
version between builds, so a new cache is required even when the version has
not changed. Historical package compatibility is not a release gate.

### Compiler inventory, policy and documents

After building, regenerate `artifacts/api-inventory.json` with the command
above. It contains exact compiler XML IDs, effective visibility and source
comments; no handwritten declaration inventory is maintained.

```console
$ uv run python eng/parity/verify_public_api.py
```

```console
$ uv run python eng/parity/verify_capabilities.py
```

```console
$ uv run python eng/parity/verify_version_literals.py
```

```console
$ mise exec -- actionlint
```

```console
$ uv run --locked --script eng/parity/verify_workflows.py
```

```console
$ uv run python eng/parity/verify_tmux_versions.py
```

```console
$ uv run python eng/docs/render_api_reference.py --check
```

```console
$ uv run python eng/docs/sync_snippets.py --check
```

```console
$ uv run eng/mcp/dump_tools.py --check
```

The API renderer matches exact compiler identities, including overloads and
generic members. Missing documentation and stale Markdown fail independently
of analyzer baseline checks. `sync_snippets.py` checks published blocks against
their source regions; regenerate them with the same command without `--check`.

`ExampleSuite` runs the complete ordinary example set once. The console
`--smoke` selects one example to check the entrypoint. `ReadmeExampleTests`
compiles its snippet set once, then executes the runnable blocks from that
assembly. The Linux workflow runs this owner once per framework and excludes
it from general integration and tmux-version lanes. The macOS integration run
includes its own README check. [examples/README.md](../examples/README.md)
describes the snippet markers.

The locked engineering runner owns Python tests. Package mutations are tagged
`packaging` and require the explicit artifact environment variable above;
ordinary engineering tests do not build or pack.

### Test loops

Time whole commands, including startup. Keep focused tests below five seconds
and the unit/engineering loop below thirty seconds. Builds, restores, README
compilation, real-tmux integration, package mutations and AOT belong to the
outer loop. Run focused checks after edits and the relevant complete gates
before committing. Benchmarks are separate. Add permanent tests for critical
behavior, and prove changed gates reject a deliberate break.

### Other workflows

[dotnet-tmux.yml](workflows/dotnet-tmux.yml) builds the integration dependency
graph once for both frameworks. An archive named for the source SHA and Release
configuration supplies every supported tmux lane; consumers check the revision
stamp and execute test modules without restore or build. Missing artifacts and
empty test selections fail. The matrix still covers every version in
`eng/tmux/versions.json` on net8.0 and net10.0.

The `compatibility` job requires the producer and all supported lanes. Checks
independent of tmux versions, including packaging and README compilation, run
outside that matrix. The scheduled tmux-master lane remains advisory.

`dotnet.yml` has an advisory macOS arm64 lane on master and manual dispatch.
It builds and runs unit/integration tests with Homebrew tmux; it stays outside
`gate` and restores without locked mode. Text captured from a pane may wrap with
the host's prompt width; assertions about typed text use `joinWrappedLines`.

Action references are pinned to commits. CodeQL also runs on pull requests;
Scorecard runs on its configured schedule. These workflows do not replace the
package or runtime gates.

## Testing the MCP server means running a real agent

`src/LibTmux.Mcp` is a stdio server, so the only honest test of its tool
descriptions is whether a model picks the right tool without being told which.
The private [native MCP config swapper](../eng/mcp-swap/README.md) points every
installed agent CLI at a local build, and `revert` puts their configs back from
the timestamped backup it took:

```console
$ mise exec -- dotnet run --project eng/mcp-swap/LibTmux.McpSwap.csproj -- use \
    --source release \
    --env TMUX_TMPDIR=/tmp/libtmux-dotnet-dev
```

Pass `--env TMUX_TMPDIR=...` whenever the sockets under test are not in the
default root. An agent spawns the server with its own environment, so a socket
this shell can see is one the server cannot.

That same gap is why the swap writes `DOTNET_ROOT` into each config. A
framework-dependent apphost finds its runtime through `DOTNET_ROOT` or `PATH`,
mise puts the SDK in neither, and the failure is silent from the agent's side:
the binary exits before the handshake and the agent reports only that the
server has no tools.

The tool reference is generated rather than written, so it cannot describe a
surface that is not there:

```console
$ uv run eng/mcp/dump_tools.py
```

A wait takes a control-mode client, which is a real attached client: it shows
up in the user's `list-clients` for as long as the wait runs. It attaches with
`ignore-size` so it never drags the window down to its own size, and the watch
is reference counted per session so it exists only while a wait does. Changing
either of those changes what a user sees on their own screen.

## What a change is expected to carry

**A behaviour change needs a test against a real tmux.** This library's job is
being right about tmux, and only tmux can say whether it is.

**A new tmux version starts in the manifest.**
[`eng/tmux/versions.json`](../eng/tmux/versions.json) decides which versions
this repository supports. The workflow matrix, both build scripts, the runtime
constants and every README repeat that list, and
`eng/parity/verify_tmux_versions.py` names each one that has not caught up:

```console
$ uv run python eng/parity/verify_tmux_versions.py
```

**A version-dependent behaviour needs a row in the ledger.** Anything that
differs between 3.2a and 3.7c goes through the capability model, and each
difference names the test that proves it in
[`docs/parity/version-deltas.json`](../docs/parity/version-deltas.json). A
gate on a raw `TmuxVersion.Parse("...")` comparison anywhere else in
`src/LibTmux`, instead of `TmuxCapabilities.IsSupported` or `Supports(...)`,
fails `verify_version_literals.py`: it re-derives an answer the table already
has, and the two can disagree without either side noticing. A handful of
exceptions are named by exact text in that script, each with the tag evidence
that makes it not a capability.

**A public API addition updates one declaration baseline.** Update the
packable library's `PublicAPI.Unshipped.txt`; RS0016, RS0017 and RS0036 enforce
new, stale and incorrectly annotated declarations. Add policy metadata only
when the API introduces ownership, I/O, platform or package-placement rules.
If it maps a Python symbol, use its exact compiler ID in the parity ledger.
Regenerate compiler inventory and API Markdown after changing source comments
or declarations.

**A documented example is compiled, and a `csharp run` block is executed
against a live tmux.** `ReadmeExampleTests` compiles every C# block in the
shipped READMEs and `docs/modes/` against the real assemblies, and runs the
ones tagged to run against a tmux server of their own. If it does not compile,
it is not documentation. Anchoring a block to a snippet region adds drift
protection on top of that; [`examples/README.md`](../examples/README.md) says
how. Add examples to the READMEs or `docs/modes/`, not to the decision records
— those quote what was run at the time and are not edited to keep compiling.

**A performance claim needs a recorded run.** See
[`docs/benchmarks`](../docs/benchmarks/README.md). Absolute milliseconds move
by a factor of five on one host, so a claim is stated as a marginal cost or a
ratio, with the tmux, host and date that produced it.

## The Python original is a separate checkout

This repository was imported out of a monorepo that also held Python libtmux,
so anything grounded in that source needs to be told where it went:

```console
$ LIBTMUX_PYTHON_REPOSITORY=~/work/python/libtmux \
    uv run python eng/parity/verify_ledger.py
```

## Flaky, or broken?

Some real-tmux tests are load-sensitive. A single failure is worth re-running
in isolation before blaming a change, and worth investigating rather than
shrugging at. Three signs it is the machine and not the code: the failing test
moves between runs, the failure reads as a missing server or an expired wait
rather than a wrong value, and the file it is in is not one the change touched.

## Pull requests

Keep the change narrowly scoped. Unrelated cleanup belongs in its own commit,
or its own pull request.

A passing gate is evidence only once it has been shown capable of failing, so
pair a new test with a deliberate break that proves it bites.

Commit format is in [WRITING.md](WRITING.md).

## Review

A reviewer is checking two things beyond correctness: that the change carries
what the section above requires, and that anything a reader will see follows
[WRITING.md](WRITING.md). A comment that restates its code, a changelog entry
that describes effort rather than impact, or a public member without a
`<summary>` are all review findings, not nits.

## Releases

Releases are cut by the owner, in two commits.

First, `chore(release[version]): Bump to 0.0.0-alpha.N` edits
[`Directory.Build.props`](../Directory.Build.props) — `VersionSuffix` and
`PackageReleaseNotes` — and renames `## [Unreleased]` in `CHANGELOG.md` to the
dated version heading. Versioning is manual `VersionPrefix`/`VersionSuffix`,
and one version covers every shipped package.

Second, a `Tag v0.0.0-alpha.N` commit, then the tag itself.

**Never create tags. Never push tags.** A tag matching `v*` triggers
[`release.yml`](workflows/release.yml), which verifies the tag matches the
built `Version` property, packs, proves the package installs and runs on both
target frameworks, generates an SBOM and a provenance attestation, and pushes
to NuGet through trusted publishing. Renaming that workflow file breaks the
trusted-publishing policy registered on nuget.org, so change the policy first.

### The psmux preview gates

Publishing the preview needs the accepted Windows x64 client. It is a published
psmux release asset, so `release.yml` downloads and hash-checks it like any
other pinned dependency — there is no artifact to host and no repository
variable naming one. [`docs/psmux.md`](../docs/psmux.md) states the trust
boundary it has to meet.

`release.yml` still reads the repository variables `PSMUX_WSL_DISTRIBUTION` and
`PSMUX_WSL_DOTNET_PATH`, and needs a self-hosted `Windows`, `X64`, `psmux`
runner for the native and WSL gates, because those describe one machine rather
than the artifact. `PSMUX_WSL_DOTNET_PATH` is the absolute Linux `dotnet` path
for that checkout, which the runner reports:

```console
$ mise exec -- which dotnet
```

Those inputs make the gates runnable; they do not by themselves complete the
runtime evidence.

#### Provisioning the runner

The machine needs Windows x64, `git`, PowerShell 7 (`pwsh`), and a WSL
distribution holding a checkout with its own Linux `dotnet`. The psmux job
drives both sides from one PowerShell process, so a runner without WSL fails
the gate rather than skipping it.

`pwsh` is the one a hosted runner would have supplied. Windows PowerShell 5.1
is not it, and the job's steps ask for `pwsh` by name:

```console
$ winget install --id Microsoft.PowerShell --silent
```

A Windows `dotnet` is not a prerequisite. `global.json` pins an exact SDK, so
the job installs that version into the runner's tool cache regardless of what
the machine already has. Restart the listener after installing anything it
needs to find on `PATH`; it reads the environment once, at start.

Register it with the `psmux` label; `self-hosted`, `Windows` and `X64` are
added for you, and `runs-on` matches on all four:

```console
$ ./config.cmd \
    --unattended \
    --replace \
    --url https://github.com/libtmux/libtmux-dotnet \
    --token "$(gh api -X POST \
        repos/libtmux/libtmux-dotnet/actions/runners/registration-token \
        --jq .token)" \
    --name psmux-wsl-win \
    --labels psmux
```

A registration token expires in an hour, so generate it when you use it. Run
the listener with `./run.cmd`, or install it as a service with `./svc.cmd
install` if it should survive a reboot. The runner must be online when the tag
is pushed: `psmux` has no `ubuntu-latest` fallback, so a queued job waits
rather than failing fast.

Confirm what GitHub sees before relying on it:

```console
$ gh api repos/libtmux/libtmux-dotnet/actions/runners \
    --jq '.runners[] | "\(.name) \(.status) [\([.labels[].name] | join(","))]"'
```

### Recorded evidence is a release artifact

A capability row is `pending` until a matrix run records evidence for it, and
`verified` after. What `verified` claims is exact: these tmux versions, on
these frameworks, at *this tree* — the fingerprint covers every tracked file
outside the evidence directory. Any commit changes it, so a verified row is
true at one commit and stale at the next.

That is why recording belongs at a release boundary rather than in the gate,
and why `reconcile_versions.py` and `verify_ledger.py` are not in
`dotnet.yml`. Between releases every row is `pending`, which is the honest
state: nobody has run the matrix against this tree.

The `workspace` cohort combines the protocol proofs from `0001`, the public
wrapper policy proofs from `closure`, and native layout validation. Its matrix
runs every stable version on both frameworks and the source-bound 3.7/3.7a
transition proof. The older cohort memberships remain fixed.

To record against a clean committed source tree, select a new evidence
directory -- the next unused number under `docs/parity/evidence/`:

```console
$ eng/tmux/run-matrix.sh \
    --evidence-dir docs/parity/evidence/0014 \
    --capability-cohort workspace \
    tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj
```

```console
$ uv run python eng/parity/reconcile_versions.py \
    --evidence docs/parity/evidence/0014/results.ndjson \
    --write
```

Commit the bundle and the rewritten `version-deltas.json` together, because the
fingerprint is of the tree that commit produces. A tmux build takes about forty
seconds here and the matrix runs the suite sixteen times, so budget half an
hour.

## Compatibility

Stable tmux **3.2a and newer**, on **net8.0** and **net10.0**. The required
Linux matrix covers 3.2a through 3.7c; the advisory macOS lane uses the current
Homebrew tmux. Windows is unsupported. The `LibTmux` core package is trim- and
ahead-of-time-analyzer gated and has a Linux NativeAOT execution smoke test.
Optional packages make narrower compatibility claims in their project files
and package READMEs.

During alpha the public API can change in any release with no deprecation
period, so a consumer pins an exact version. Widening the supported range means
a row in the ledger, an entry in the tmux matrix, and a README that says so.

### Removing a public member

Alpha decides the notice period, not the mechanism. From the first stable
release the mechanism is `[Obsolete]`:

- Mark the member `[Obsolete("Use X instead.")]` in the release that replaces
  it, and say so in `CHANGELOG.md` under `### Changed` using the four-part
  frame in [WRITING.md](WRITING.md).
- Delete it no earlier than the next minor release, in an entry under
  `### Removed`.
- `[Obsolete(error: true)]` is for a member that cannot work as documented —
  a wrong answer is worse than a build failure.

A rename is a removal and an addition. Renaming without the obsolete step is
what alpha buys, and the entry still names both spellings so a reader can
follow it: `Server.RaiseIfDeadAsync` became `Server.ThrowIfDeadAsync` that way.

Until then, carry every call site in the repository with the change. A member
that no longer exists must not exist in the examples, the tests, the READMEs,
policy metadata or the API baselines either.

## Reporting a vulnerability

Not here — see [SECURITY.md](../SECURITY.md).
