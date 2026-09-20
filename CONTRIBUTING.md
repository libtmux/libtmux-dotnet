# Contributing

## What you need

`dotnet` is pinned by `global.json` and resolves through [mise](https://mise.jdx.dev),
so it is not on `PATH`:

```console
$ mise exec -- dotnet build LibTmux.slnx --configuration Release --warnaserror
```

The validators are Python and run through [uv](https://docs.astral.sh/uv/). You
also need a real `tmux` — the suite drives one rather than mocking it.

## Give your tmux a socket root of its own

Tests set `TMUX_TMPDIR=/tmp/libtmux-dotnet-test` for themselves. If you start a
server by hand, put it under `/tmp/libtmux-dotnet-dev`.

This matters more than it looks. Several libtmux ports run real tmux on one
machine, and a socket in the default root is reachable by all of them, so a
cleanup sweep can kill another project's servers mid-run — and the failure
surfaces in whichever suite noticed, not the one that caused it.

Never `pkill tmux`, and never delete `/tmp/tmux-$UID/`. To find what this
repository left behind, list its own root:

```console
$ ls /tmp/libtmux-dotnet-test/tmux-$(id -u)
```

A socket file outlives the server that made it, so confirm each with
`has-session` before deciding it is alive.

## Running what CI runs

`.github/workflows/dotnet.yml` is the source of truth. Two things are easy to
miss locally, because they check documents rather than the build:

```console
$ uv run python eng/parity/verify_public_api.py
```

```console
$ uv run python eng/parity/verify_capabilities.py
```

The packaging tests read what a pack produced, so run the workflow's order —
pack, then the package consumer, then the ahead-of-time publish — before the
integration suite, or `PackageClosureTests` fails for a reason that is not a bug.

## What a change is expected to carry

**A behaviour change needs a test against a real tmux.** This library's job is
being right about tmux, and only tmux can say whether it is.

**A version-dependent behaviour needs a row in the ledger.** Anything that
differs between 3.2a and 3.7b goes through the capability model, and each
difference names the test that proves it in
[`docs/parity/version-deltas.json`](docs/parity/version-deltas.json).

**A public API addition needs five edits, and each will tell you.** The Roslyn
analyzer baseline (`PublicAPI.Unshipped.txt`), the type and its members in
`docs/public-api.json`, its values if it is an enum, and its owning component in
`eng/parity/verify_production_plan.py`. They fail independently and by name;
follow the errors.

**A documented example is compiled, and a `csharp run` block is executed against
a live tmux.** If it does not compile, it is not documentation. Add examples to
the READMEs or `docs/modes/`, not to the decision records — those quote what was
run at the time and are not edited to keep compiling.

**A performance claim needs a recorded run.** See
[`docs/benchmarks`](docs/benchmarks/README.md). Absolute milliseconds move by a
factor of five on one host, so a claim is stated as a marginal cost or a ratio,
with the tmux, host and date that produced it.

## Style

The build is the style guide: `TreatWarningsAsErrors`, `Nullable` enabled,
analyzers at `10-recommended`, and `EnforceCodeStyleInBuild`. If it compiles
clean, the formatting is right.

**A comment earns its maintenance cost or it goes.** Keep one only where losing
it would cost a maintainer real time rediscovering something the code, types,
assertions and tests do not already carry: an invariant, an ordering or lifetime
requirement, a tmux version boundary, or a reason a simpler implementation would
be wrong. State the constraint and stop — the reasoning that found it belongs in
the commit message, where it is free to keep. Delete comments that narrate the
next lines, restate a name or an assertion, excuse the code, or hand-track a
value the code owns, and prefer deletion when the call is close. One or two
lines; a comment reaching four is carrying several facts or arguing.

XML documentation is judged the other way round — by whether it helps a caller
use the API correctly, not by whether it is non-obvious. `CS1591` is
unsuppressed in the published projects, so a public member without it does not
build.

[`AGENTS.md`](AGENTS.md) states the full policy and the three gates a comment
has to pass.

Commit messages say what changed and why it was worth changing. No emojis.

## Reporting a vulnerability

Not here — see [SECURITY.md](SECURITY.md).
