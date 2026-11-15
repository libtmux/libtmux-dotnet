# mcp-tools

`dump_tools.py` starts a built `LibTmux.Mcp` server with every toolset enabled,
asks it for its tools, resources and prompts, and writes
[docs/mcp/tools.md](../../docs/mcp/tools.md). The document cannot describe a
surface the server does not advertise.

Build `LibTmux.Mcp` in Release first, then regenerate the document after
changing the tool surface:

```console
$ uv run scripts/mcp-tools/dump_tools.py
```

`dotnet.yml` runs it with `--check`, which writes nothing and fails when the
committed document no longer matches the server:

```console
$ uv run scripts/mcp-tools/dump_tools.py --check
```

Run the tests with the [script runner](../README.md#running-the-checks).
