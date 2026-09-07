# Native MCP config swapper

This private .NET console tool points supported agent CLIs at a chosen build of
`LibTmux.Mcp`, then restores their prior configuration. It is part of this
repository's development tooling: it is non-packable, is not published, and is
not included in the LibTmux NuGet artifacts.

`use` edits every selected client as one filesystem transaction. `revert`
restores the exact bytes and permissions saved by that transaction. A repeated
swap keeps the original backup, so the eventual revert still reaches the
pre-swap configuration.

## Quick start

List client binaries and configuration files the tool can see:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- detect
```

Preview the default debug swap without building, locking, or writing:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --dry-run
```

Build the release profile, preflight it as an MCP server, and swap every
detected client:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --source release
```

Inspect the active `tmux` entry and recovery state:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- status
```

Restore all recorded swaps:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- revert
```

## Sources

`--source` selects what agents launch:

| Source | Behavior |
| --- | --- |
| `debug` | Build `Debug` and launch its apphost directly. This is the default. |
| `release` | Build `Release` and launch its apphost directly. |
| `run` | Launch through `dotnet run`; every agent start may rebuild. |
| `path` | Launch the absolute path supplied by `--bin`. |
| `published` | Install a named NuGet version in an isolated tool directory and launch it. |

Use the current source through `dotnet run`:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --source run
```

Use an already-built executable:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --source path \
    --bin /opt/libtmux/libtmux-mcp
```

Install and use a published release without overwriting another cached release:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --source published \
    --version 0.1.0-alpha.3
```

For `debug` and `release`, `--no-build` reuses an existing apphost. The SDK is
pinned by `global.json`; the resolved `DOTNET_ROOT` is written into each entry
because an agent does not necessarily inherit the invoking shell's environment.

## Clients and configuration scope

The tool supports eight clients in a stable order:

| Client | Configuration | Format and entry |
| --- | --- | --- |
| Claude Code | `~/.claude.json` | JSON `mcpServers`, or `projects.<absolute repo>.mcpServers` |
| Codex | `~/.codex/config.toml` | TOML `mcp_servers` |
| Cursor Agent | `~/.cursor/mcp.json` | JSON `mcpServers` |
| Gemini CLI | `~/.gemini/settings.json` | JSON `mcpServers` |
| Grok CLI | `~/.grok/config.toml` | TOML `mcp_servers` |
| Agy / Antigravity | `~/.gemini/config/mcp_config.json` | JSON `mcpServers` |
| OpenCode | `$XDG_CONFIG_HOME/opencode/opencode.jsonc` | JSONC `mcp` |
| Pi | `~/.pi/agent/mcp.json` | JSONC `mcpServers` |

Repeat `--cli` to select clients. `antigravity` is accepted as an alias for
`agy`:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --cli claude \
    --cli codex
```

Claude has two independent layers. The default project scope edits only the
current repository's `projects` entry. User scope edits the top-level fallback:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --cli claude \
    --scope user
```

Both Claude scopes can be swapped independently. An unscoped revert unwinds
them in last-in, first-out order; a scoped revert cannot skip a newer layer:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- revert \
    --cli claude \
    --scope project
```

`--scope` is accepted by `status`, `use`, and `revert`. Other clients have only
the global file listed above, so their scope is always treated as `user`.

The tool deliberately does not walk workspace files for Cursor, Gemini,
OpenCode, or the other global-only clients. Use a client's native command or
edit its project file when workspace precedence is the behavior under test.
OpenCode merges three global names (`config.json`, `opencode.json`, and
`opencode.jsonc`) with JSONC winning; this tool owns only `opencode.jsonc`.
Pi needs the third-party `pi-mcp-adapter`, because Pi has no built-in MCP
client. Detection only searches `PATH` and the one configuration path in the
table; it does not probe alternative installation prefixes.

## Server name and environment

The repository defaults to the current directory, the project to
`LibTmux.Mcp`, and the shared server name to `tmux`. The executable name comes
from the project's `AssemblyName`; the published command comes from
`ToolCommandName`. Override the repository, project, entry, or server with
`--repo`, `--project`, `--entry`, or `--server`.

Repeat `--env KEY=VALUE` for server-specific variables. Explicit values win;
unmentioned variables from an existing entry survive the swap:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- use \
    --env LIBTMUX_TOOLSETS=inspect,execute \
    --env TMUX_TMPDIR=/tmp/libtmux-dotnet-dev
```

`LIBTMUX_SAFETY` has been removed and is rejected as explicit input. When this
invocation supplies `LIBTMUX_TOOLSETS`, the swap removes an inherited
`LIBTMUX_SAFETY` value while preserving every other existing variable. Without
an explicit toolset replacement, it leaves the inherited environment intact so
the preflight can report the server's migration error without silently choosing
new authority.

`doctor` reports entries for this repository under another name, outstanding
recovery records, orphaned .NET swap backups, and authentication variables
that can override a client's stored login:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- doctor
```

## Preflight and configuration fidelity

Before any write, the tool plans every selected configuration and performs an
MCP `initialize` round trip against every distinct final server environment.
A valid stdio server normally stays alive after responding; the probe accepts
that response immediately, then terminates and reaps the process tree. A
failure, timeout, oversized stream, or malformed response leaves every client
untouched. `--no-preflight` exists only for deliberate low-level debugging.

Inputs must be valid UTF-8. JSON, JSONC, and TOML are syntax-checked before a
transaction. Targeted JSONC and TOML edits preserve unrelated keys, comments,
trailing commas, and surrounding document structure. Shapes that cannot be
updated without risking unrelated content fail before any write.

## Transactions and recovery

Config files, backups, and state are staged, flushed, atomically published,
and revalidated while one authenticated lock is held. The transaction rejects
unexpected aliases, hard links, symlink retargets, inode changes, edits between
planning and commit, and unsafe lock or recovery permissions. If commit fails,
it rolls back all clients; if rollback itself is interrupted, it retains and
names the recovery files instead of deleting the remaining evidence.

The cross-port lock is
`$XDG_STATE_HOME/libtmux-mcp-dev/swap/state.lock`. It uses the POSIX `lockf`
record lock from offset zero through the evolving end of file (`length = 0`),
which overlaps Python `fcntl.lockf` and whole-file Java `FileChannel` locks on
Linux and macOS. Lock verification uses the held descriptor and path metadata;
if a raced path opens the same inode, that descriptor is retained until unlock
so POSIX's close-any-alias rule cannot release the record lock early. .NET
recovery is namespaced under
`$XDG_STATE_HOME/libtmux-mcp-dev/swap/dotnet/state.json`, and backups use the
`.bak.mcp-swap-dotnet-...` suffix. The state envelope is checksummed and
strictly marked for the .NET implementation. It intentionally rejects the old
Python schema and other ports' recovery records rather than guessing how to
restore them.

Dry-run reports the current and proposed bytes but does not acquire the lock,
build, install, preflight, create directories, or write recovery state:

```console
$ mise exec -- dotnet run \
    --project eng/mcp-swap/LibTmux.McpSwap.csproj \
    -- revert \
    --dry-run
```

## Development

The native tests include all 40,320 client selection permutations, strict
encoding and syntax cases, live initialize probes, and filesystem race and
rollback cases:

```console
$ mise exec -- dotnet test \
    --project eng/mcp-swap/tests/LibTmux.McpSwap.Tests.csproj \
    --framework net10.0 \
    --minimum-expected-tests 117
```
