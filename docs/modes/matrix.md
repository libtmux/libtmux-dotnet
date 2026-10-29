# Choosing a mode

Three ways to reach tmux. Which one you are using is visible where the call
starts, never in a flag buried in options, and all three are supported on every
tmux this library supports.

| Mode | Flip it on | Dispatch | Example output |
|---|---|---|---|
| [One-shot](one-shot.md) | `session.CreateWindowAsync(...)` | 1 command, awaited | `@1 1:build` |
| [Control](control-mode.md) | `server.EnterControlModeAsync(ct)` | 1 client, streamed | `%window-add @1` |
| [Chained](chaining.md) | `server.Chain()...ExecuteAsync(ct)` | N batched, 1 invocation | `@1` |

Example output is captured from a live tmux and refreshed per release. It shows
the shape of what comes back, not a byte-exact string to assert against.

## The same task, three ways

Create a window named `build`.

```csharp
Window window = await session.CreateWindowAsync(new NewWindowRequest { Name = "build" }, ct);
```

```csharp
await using IControlModeSession control = await server.EnterControlModeAsync(cancellationToken: ct);
await control.SendAsync(TmuxCommand.Create("new-window", "-d", "-n", "build"), ct);
```

```csharp
await server.Chain().Then("new-window", "-d", "-n", "build").ExecuteAsync(ct);
```

## What each costs

One-shot sends each command over a control client the library keeps, so it pays
a round trip, and a process only for the few commands that cannot use that
client (see [one-shot](one-shot.md#how-a-call-reaches-tmux)). Chaining starts
one process for the whole sequence. `EnterControlModeAsync` starts a client of
your own and keeps it.

The recorded run below was measured on 2026-08-16, before the shared client
existed: its one-shot rows are a process per command, so on tmux 3.7 and newer
they overstate what one-shot costs now. This table has not been repeated; run
the benchmarks to refresh it.

The number that belongs to the library is the **marginal** one: what a command
costs *in addition to* the first, which is the difference between fifty and one
over forty-nine. Absolute timings belong to the machine — the same host measured
a one-shot command at 2.4 ms and at 19 ms on the same day — so they are recorded
per run rather than quoted here.

| Mode | Cost of one more command | Because |
|---|---:|---|
| One-shot | ~2,300 us | it starts another tmux process |
| Control | ~205 us | it makes another round trip on an open connection |
| Chained | ~21 us | it appends to a command line that is already being sent |

Medians of 100 samples, tmux 3.7b, `net10.0`, 2026-08-16 —
[the full distribution, host and commit](../benchmarks/runs/2026-08-16-tmux-3.7b.md).

### The absolute numbers, and why they are downstairs

| Commands | Mode | Median | p95 | Allocated |
|---:|---|---:|---:|---:|
| 1 | One-shot | 2.81 ms | 4.03 ms | 292 KB |
| 1 | Chained | 3.02 ms | 4.43 ms | 291 KB |
| 1 | Control | 0.43 ms | 0.66 ms | — |
| 50 | One-shot | 115.93 ms | 155.43 ms | 14,490 KB |
| 50 | Chained | 4.03 ms | 5.76 ms | 368 KB |
| 50 | Control | 10.49 ms | 15.63 ms | 60 KB |

At one command, one-shot and chained are the same measurement: both start
exactly one process, and the allocations say so — 292 KB against 291 KB. Chaining
is not a way to make a single command faster and the table should not be read as
claiming it is.

At fifty, the modes separate for reasons that survive a change of machine:
one-shot pays fifty process starts, chaining pays one, control mode pays one
plus fifty round trips.

**The crossover between chaining and control mode is not portable.** Chaining
wins here because a process start costs about 2.4 ms while fifty round trips
cost about 10 ms. On a host where process starts are expensive — a loaded CI
runner, a container with a cold page cache — the order reverses, and it has
reversed in a recorded run on this machine. If the choice between those two
matters to you, measure it where it will run.

**Allocation is the column that does not move.** It repeated byte-for-byte
across runs whose timings differed by a factor of five, so it is what to check a
change against.

## Recording your own

```console
$ dotnet run \
    --project benchmarks/LibTmux.Benchmarks \
    --configuration Release \
    --framework net10.0 \
    -- --filter '*ModeBenchmarks*' --artifacts artifacts/benchmarks
```

The project multi-targets, so the framework has to be named: the numbers above
are `net10.0`, and running the other one measures something else. Set
`LIBTMUX_TMUX` to measure a tmux other than the one on the path.

See [docs/benchmarks](../benchmarks/README.md) for how a run becomes a record,
and for the warmup mistake that once put an impossible number in this table.

## Version differences

Known behavior differences from tmux 3.3a onward go through the capability
model. Each measured difference has a row with a real-server proof in
[the parity ledger](../parity/version-deltas.json).

## Minimum version

The library supports tmux 3.3a and newer, and decides it from the daemon a
command reaches, not from the client executable. The 3.3 release without its
patch letter is below the minimum. A release candidate or `next-X.Y` build
ranks just below the release it names.

tmux release candidates are not supported. Beyond that ordering, the library
promises nothing about how a check treats one. See
[Release candidates](https://libtmux.org/en/tmux/guides/tmux-support/#release-candidates).

Before the first command, the library asks the daemon for its version with
`run-shell -C`, which fires no `after-*` or `command-error` hook, and throws
`TmuxVersionTooLowException` for a daemon below the minimum or a report it
cannot rank, such as `master`. Every command list then opens with an `if-shell`
that fails unless the daemon still reports that version, so a replaced daemon
is checked again before anything runs. On a replacement below the minimum the
command is refused with no effect, a supported replacement is checked afresh,
and a failed fence on tmux 3.5 or newer fires `command-error`.

An endpoint with no daemon takes its version from the executable that would
start one, which may report its own file name in place of `tmux`. `kill-server`
on an absent endpoint does nothing. `-V` and `-h` describe the executable and
check nothing. Aliases that redefine the commands the library uses are outside
this contract.

A refusal says what to do: upgrade tmux and restart the tmux server, since a
running daemon keeps its version until it exits, or use LibTmux 0.0.0-alpha.21,
the last release that supports tmux 3.2a. A daemon older than tmux 3.2 cannot
run the probe at all, because `run-shell -C` arrived in 3.2: a 3.1 daemon
answers with the usage line of `run-shell`, and a 3.0 or older daemon makes the
client print `server version is too old for client`. Both are refused as below
the minimum, as is a client executable older than 3.2, which does not know the
`-N` the probe passes. Those daemons keep the client's stdout pipe open after
the client exits (stderr closes), so the library stops reading one second after
the client has gone and nothing more has arrived.

A version is read only in the forms tmux has reported: `X.Y`, `X.Ya`,
`X.Y-rc` or `X.Y-rcN`, and `next-X.Y`. Anything else, such as `3.4.1`,
`3.4-dev` or `3.3a-openbsd`, cannot be ranked. It is refused with
`TmuxVersionTooLowException`, whose `ActualVersion` is not valid, and the
message names the minimum rather than calling the build too old.

A control client's version is checked by a probe on a separate process just
before it attaches, because a refusal printed over a control connection arrives
outside any command. A daemon replaced in the moments between that probe and the
attach is not fenced: the client attaches to the replacement, and the next
command it carries is fenced like any other, so a replacement below the minimum
is refused there and a supported one is checked afresh.

## Optional features

The minimum covers ordinary reads, writes, batches and workspace builds.
A few operations are unsafe on releases above it, so each carries its own
requirement and is refused on the daemon that would be harmed, before it has
an effect. Nothing below raises the minimum for anything else.

Every refusal in the table raises `TmuxUnsupportedOnReleaseException` or a
subclass, so one `catch` covers them. It carries `ActualVersion`,
`SafeReleases`, the `CapabilityId` from [`capabilities.json`](../../capabilities.json)
and the refused `Command`. `TmuxVersionTooLowException` derives from it and is
raised where a release floor decides, so a `catch` of the old type keeps
working for those rows. A name or a layout that is invalid on every release
is not a release refusal; it stays `ArgumentException` or `TmuxWindowException`.

| Operation | Requires | Below it |
| --- | --- | --- |
| `Server.EnterControlModeAsync` and `ServerMirror` | tmux 3.7 | `TmuxVersionTooLowException` |
| Pane waits: `Pane.WaitForTextAsync` and the MCP `wait_for_text` | none | The wait polls, with no opt-in. From 3.7 it listens to a control client, and a lost stream on a live pane needs `AllowPollingFallback` to keep going |
| One-shot commands over the shared control client | tmux 3.7 | Each command runs on its own process |
| `delete-buffer -b` and `set-buffer -n`, and work that cleans up a buffer by name: `Pane.RunAsync` and the MCP `paste_text` | tmux 3.4 | Refused before the buffer is staged, with `TmuxVersionTooLowException`; `paste-buffer -d -b` stays available |
| A foreground `run-shell` that names no pane, from `Server.RunShellAsync` or a raw command | tmux 3.5 | Refused with `TmuxVersionTooLowException`; a background, `-C` or pane-targeted request is sent |
| `break-pane` without a window name | tmux 3.7a or any release but 3.7 | Refused on exactly 3.7 with `TmuxVersionTooLowException`; the typed `Pane.BreakAsync` names the window itself |
| Window and session names with `:` or `.` | any release but 3.7 | Refused on exactly 3.7 with `TmuxUnsupportedOnReleaseException`; `NewSessionRequest`, `Session.RenameAsync` and the other typed session calls refuse them on every release with `ArgumentException` |
| `split-window -p` | any release but 3.4 | Refused on 3.4 with `TmuxVersionTooLowException`; typed splits send `-l N%` |
| `select-layout` with an unchecked layout | tmux 3.4 | Refused unless it is a checksummed layout or a unique preset name, with `TmuxUnsupportedOnReleaseException` for a raw command below 3.4 and `TmuxWindowException` for a malformed layout through the typed calls ([below](#refusal-types-for-layouts-and-the-clipboard-query)) |
| `refresh-client -l`, the clipboard query | tmux 3.6a | Refused below 3.6a with `TmuxVersionTooLowException` ([below](#refusal-types-for-layouts-and-the-clipboard-query)) |

### Refusal types for layouts and the clipboard query

Below 3.4, a raw `select-layout` command, sent through
`Server.ExecuteCommandAsync` or `Chain()`, raises
`TmuxUnsupportedOnReleaseException`; from 3.4 tmux answers it.
`Window.SelectLayoutAsync`, and a batched layout that names a window, raise
`TmuxWindowException` for a layout that is malformed, unknown or an ambiguous
preset prefix on the releases that have the mirrored presets, and
`TmuxUnsupportedOnReleaseException` for a mirrored preset on a release that
lacks it, or for the JSON form, which needs tmux 3.8. The `error` field of each
layout entry in [`capabilities.json`](../../capabilities.json) names the
release refusal.

`refresh-client -l` does not end the server from 3.4 unless the attached client
leaves before its terminal answers; on 3.3a it ends the server at once when the
attached client's terminal has the `Ms` clipboard capability, such as
`xterm-256color` or `tmux-256color`. A raw command sends the flag from 3.6a.
`Server.RefreshClientAsync` sends it only from 3.7, so the typed call is safe on
every release.

### What is checked

Every path that sends a command line is checked on the version of the daemon
it reaches: `Server.ExecuteCommandAsync` and the same method on a session,
window and pane, `Chain()`, and the commands of a held control session from
`IControlModeSession.SendAsync`. A command word is read the way tmux reads it,
by alias, name or unique prefix, so `br`, `breakp` and `break-p` are
`break-pane`, and a `;` list is checked one command at a time.

The check resolves the first word of each command in a `;` list. A command
carried inside a string that tmux parses itself is sent as written: the body of
an `if-shell`, the command of `run-shell -C`, a brace block, a hook, a key
binding, a file given to `source-file` or a configured `command-alias`. Shell
text is not read either. These are the refused commands, the releases they
affect, and what happens there when one is nested:

- `break-pane` without `-n`: exact tmux 3.7 only. The server exits. 3.7a fixes
  it ([3.7a `CHANGES`](https://github.com/tmux/tmux/blob/3.7a/CHANGES#L3);
  [3.7 `cmd-break-pane.c`](https://github.com/tmux/tmux/blob/3.7/cmd-break-pane.c#L117-L124)).
- `select-layout` with a string tmux cannot parse: tmux 3.3 and 3.3a. The
  server exits. 3.4 answers "invalid layout".
- `refresh-client -l`: below 3.6a, and only with an attached client. The server
  exits at once on 3.3a when the client's terminal has the `Ms` clipboard
  capability, such as `xterm-256color` or `tmux-256color`, and on 3.4 to 3.6
  when the client leaves before its terminal answers ([3.6a
  `CHANGES`](https://github.com/tmux/tmux/blob/3.6a/CHANGES#L21), commit
  `0af04295f`).
- `delete-buffer -b NAME`, and `set-buffer -b NAME -n`: below 3.4. No crash; a
  name that does not exist acts on the newest buffer instead, so the wrong
  buffer is deleted or renamed.
- `split-window -p`: exactly 3.4. tmux answers "size missing" and changes
  nothing.
- Names containing `:` or `.`: exactly 3.7. tmux answers "invalid window name"
  and changes nothing.

For example, `if-shell -F 1 'break-pane -d'` passes the check unchanged, and
ends a tmux 3.7 server where the same `break-pane` sent directly is refused.
On exact tmux 3.7, name every `break-pane`, nested or not, or upgrade to 3.7a. Naming one with
`-n` avoids the crash, though 3.7 ignores the name.
The default `prefix !` binding is itself a nameless `break-pane`, which no
library check can reach
([`key-bindings.c`](https://github.com/tmux/tmux/blob/3.7/key-bindings.c#L358)).

The same list is published as [`capabilities.json`](../../capabilities.json) at
the repository root: each optional feature with the release it starts at, how
the library handles it, and the error it raises.

Support is stated per feature and release as one of:

- **Non-operative**: the behavior is absent, rejected or wrong.
- **Timing-sensitive**: a known interleaving or workload can fail.
- **Operative with synchronization**: it works when the caller waits on a
  marker, a state or a signal the contract names.
- **Qualified**: the named checks passed for that scope.
- **Unverified**: the evidence is missing or skipped.

An operation in the table is non-operative below its release. Control clients
are qualified from 3.7 on the lanes the integration suite runs; a skipped test
qualifies nothing. A test that only skips below its release is not evidence
that the operation works above it.
