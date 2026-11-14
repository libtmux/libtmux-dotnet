# LibTmux

> tmux 3.3a or newer, tested through 3.8. The API may change in any release.
> The [psmux preview](psmux.md) is experimental and query-only.

A .NET class library for tmux. The three ordinary execution modes reach a real
tmux server, and the mode is visible where the call starts. The psmux preview
reads one isolated session on Windows.

## Start here

[Choosing a mode](modes/matrix.md) shows the same task three ways, with what
each costs per additional command and what that does not tell you about your
machine. [Benchmarks](benchmarks/README.md) holds the recorded runs behind it.

- [One-shot](modes/one-shot.md) — one command, one materialized object
- [Control mode](modes/control-mode.md) — one client, streamed events
- [Chaining](modes/chaining.md) — many commands, one invocation
- [psmux preview](psmux.md) — one isolated session, query only

## Reference

[API reference](api/README.md) is rendered from the doc comments.

[tmux MCP server](mcp/README.md) covers the pinned socket, capability selection,
input trust, and result bounds. [The tool reference](mcp/tools.md) is generated
from the server.

## Packages

The core takes one dependency. Everything else ships separately, under one
version:

- [LibTmux](../src/LibTmux/README.md) — the client
- [LibTmux.FSharp](../src/LibTmux.FSharp/README.md) — curried task helpers,
  native F# sequences, and portable snapshot filters over `LibTmux`
- [LibTmux.Query.Json](../src/LibTmux.Query.Json/README.md) — `System.Text.Json`
  for query documents
- [LibTmux.Extensions.DependencyInjection](../src/LibTmux.Extensions.DependencyInjection/README.md)
  — a server handle and its options, registered with a service collection
- [LibTmux.Workspace](../src/LibTmux.Workspace/README.md) — sessions from tmuxp
  workspace files
- [LibTmux.Workspace.Cli](../src/LibTmux.Workspace.Cli/README.md) — `tmux-workspace`,
  a tmuxp-compatible command, installed as a .NET tool
- [LibTmux.Mcp](../src/LibTmux.Mcp/README.md) — a Model Context Protocol server,
  installed as a .NET tool

## Contracts

Records of the library's surface and behavior, each with a validator.

- [API policy](public-api.md) — ownership, parity destinations, and process
  policy checked against compiler symbols. Analyzer baselines define the
  declarations for every packable library.
- [Version deltas](parity/version-deltas.json) — every tmux behavior
  difference the library gates on, each naming the test that proves it
- [Parity ledger](parity/parity-ledger.json) — where each Python libtmux
  symbol went
- [Decisions](decisions/) — why the transport, object model, query
  catalog, and public API are shaped the way they are
- [Quality-bar snapshot](quality-bar.md) — an archived scored tree and the
  script that prints raw measures for a current one

Decisions 0001 to 0003 keep the `csharp/` paths they were run under.
