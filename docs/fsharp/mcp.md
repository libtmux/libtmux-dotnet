# Assistants and the MCP server

`LibTmux.Mcp` is a [Model Context Protocol](https://modelcontextprotocol.io)
server that lets an assistant drive tmux. It is a .NET tool rather than a
library reference, and it is built on the same core as `LibTmux.FSharp`: its
waits, command runs and pane reads are the ones `Pane.waitForText`,
`Pane.run` and `Pane.capture` call.

```console
$ dotnet tool install --global LibTmux.Mcp --prerelease
```

[Point a client at it](../../src/LibTmux.Mcp/README.md#point-a-client-at-it)
describes the client configuration, and the
[tool reference](../mcp/tools.md) lists every tool.

## Share a server with your program

An F# program and an assistant can work on the same tmux server. Name the
server's socket in the program, then give the MCP server the same name in
`LIBTMUX_SOCKET`:

```json
{
  "mcpServers": {
    "tmux": {
      "command": "libtmux-mcp",
      "env": { "LIBTMUX_SOCKET": "build-agent" }
    }
  }
}
```

A program that connects with `ServerConnectionOptions(SocketName = "build-agent")`
then sees the sessions the assistant creates, and the assistant sees the
program's. The MCP server reads the socket once, when it starts.

## The same operations from F#

| MCP tool | F# |
|---|---|
| `list_sessions`, `list_windows`, `list_panes` | `Server.sessions`, `Server.windows`, `Server.panes` with `Query.list` |
| `search_panes` | `Query.showing`, or `Pane.findOnScreen` for one pane |
| `capture_pane`, `snapshot_pane` | `Pane.capture`, or `Server.capture` for the whole hierarchy |
| `capture_since` | `Control.watchPane`: a pushed stream of the pane's output rather than a cursor the caller passes back; loss arrives as `TmuxEventsDroppedEvent` |
| `wait_for_text` | `Pane.waitForText` and `Pane.waitFor` |
| `send_keys` followed by `wait_for_text` | `Pane.sendAndWait` and `Pane.sendAndWaitFor` |
| `run_shell_command` | `Pane.run` |
| `send_keys` | `Pane.sendKeys` |
| `split_window` | `Pane.split` |
| `set_history_limit`, `set_mouse_enabled`, `show_option` | `Options.set` and `Options.get` with `TmuxOptionKey` |

Two differences follow from who is asking. The MCP server remembers the keys
it typed across calls and discounts their echo from any later wait; F# sends
and waits in one call, so `Pane.sendAndWait` discounts the echo of its own
line and `Pane.waitForText` discounts nothing. And every MCP result is bounded
to keep an assistant's context small, where the F# functions return
everything tmux reported.
