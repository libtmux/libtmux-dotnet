#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Write the tool reference from what the server actually advertises.

A hand-written tool table is wrong the first time somebody adds a tool and
forgets the table. This asks the server, so the document cannot describe a
surface that is not there.

Run it after changing the tool surface:

```console
$ uv run eng/mcp/dump_tools.py
```

``--check`` writes nothing and fails when the committed document no longer
matches the server, which is what makes the document a record rather than a
description:

```console
$ uv run eng/mcp/dump_tools.py --check
```
"""

from __future__ import annotations

import difflib
import json
import os
import pathlib
import subprocess
import sys
import time

import build

REPO = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = REPO / "docs" / "mcp" / "tools.md"

CAPABILITY_KEY = "com.git-pull.libtmux-mcp/capability"

FRAMES = (
    {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "dump_tools", "version": "1"},
        },
    },
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
    {"jsonrpc": "2.0", "id": 3, "method": "resources/list"},
    {"jsonrpc": "2.0", "id": 4, "method": "resources/templates/list"},
    {"jsonrpc": "2.0", "id": 5, "method": "prompts/list"},
)


def _binary() -> pathlib.Path:
    for framework in ("net10.0", "net8.0"):
        candidate = (
            REPO / "src" / "LibTmux.Mcp" / "bin" / "Release" / framework / "LibTmux.Mcp"
        )
        if candidate.is_file():
            return candidate
    msg = "build LibTmux.Mcp in Release first"
    raise SystemExit(msg)


def _ask() -> dict[int, dict]:
    """Run one server with all four toolsets and collect answers by request id."""
    env = dict(os.environ)
    for name in (
        "LIBTMUX_SAFETY",
        "LIBTMUX_TOOLS",
        "LIBTMUX_EXCLUDE_TOOLS",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_TMUX_CONFIG",
    ):
        env.pop(name, None)
    env.update(
        {
            "LIBTMUX_SOCKET": "libtmux-mcp-docs",
            "LIBTMUX_TOOLSETS": "inspect,manage,execute,teardown",
        }
    )
    if "DOTNET_ROOT" not in env:
        env.update(build.dotnet_environment())
    proc = subprocess.Popen(
        [str(_binary())],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
        # Keep runtime discovery from the ambient environment but make the
        # advertised capability surface deterministic.
        env=env,
    )
    assert proc.stdin is not None
    assert proc.stdout is not None

    answers: dict[int, dict] = {}
    wanted = {frame["id"] for frame in FRAMES if "id" in frame}
    for frame in FRAMES:
        proc.stdin.write(json.dumps(frame) + "\n")
        proc.stdin.flush()

    deadline = time.monotonic() + 30
    while answers.keys() != wanted and time.monotonic() < deadline:
        line = proc.stdout.readline()
        if not line:
            break
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(message, dict) and "id" in message:
            answers[message["id"]] = message.get("result", {})

    proc.stdin.close()
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
    return answers


def _one_line(text: str) -> str:
    """Collapse a description to its first sentence, for a table cell."""
    flat = " ".join(text.split())
    stop = flat.find(". ")
    return flat if stop < 0 else flat[: stop + 1]


def main() -> int:
    answers = _ask()
    if not answers.get(2):
        print("the server did not answer tools/list", file=sys.stderr)
        return 1

    tools = sorted(answers[2]["tools"], key=lambda tool: tool["name"])
    resources = answers.get(3, {}).get("resources", [])
    templates = answers.get(4, {}).get("resourceTemplates", [])
    prompts = answers.get(5, {}).get("prompts", [])

    lines = [
        "# tmux MCP tools",
        "",
        "Generated from the server itself — a table nobody generates is wrong the",
        "first time somebody adds a tool. Regenerate after changing the surface:",
        "",
        "```console",
        "$ uv run eng/mcp/dump_tools.py",
        "```",
        "",
        f"{len(tools)} tools and {len(resources)} static resource. No dynamic resource",
        f"templates or prompts are registered ({len(templates)} templates, {len(prompts)} prompts).",
        "",
        "Every row is the capability object advertised with the tool under",
        f"`_meta[\"{CAPABILITY_KEY}\"]`. Effects and output classes are sets.",
        "All protocol annotations are conservative: read-only false, destructive true,",
        "idempotent false, and open-world true.",
        "",
        "| Tool | Toolset | Reach | Effects | Output classes | Does |",
        "|---|---|---|---|---|---|",
    ]
    for tool in tools:
        capability = (tool.get("_meta") or {}).get(CAPABILITY_KEY) or {}
        effects = ", ".join(capability.get("tmuxEffects", []))
        outputs = ", ".join(capability.get("outputClasses", [])) or "none"
        lines.append(
            f"| `{tool['name']}` | {capability.get('toolset', 'unknown')} "
            f"| {capability.get('processReach', 'unknown')} | {effects} | {outputs} "
            f"| {_one_line(tool.get('description', ''))} |"
        )

    for label, key, field, uri in (
        ("Resources", 3, "resources", "uri"),
        ("Resource templates", 4, "resourceTemplates", "uriTemplate"),
    ):
        entries = answers.get(key, {}).get(field, [])
        if not entries:
            continue
        lines += ["", f"## {label}", "", "| URI | Does |", "|---|---|"]
        lines += [
            f"| `{entry[uri]}` | {_one_line(entry.get('description', ''))} |"
            for entry in sorted(entries, key=lambda entry: entry[uri])
        ]

    if prompts:
        lines += ["", "## Prompts", "", "| Prompt | Does |", "|---|---|"]
        lines += [
            f"| `{prompt['name']}` | {_one_line(prompt.get('description', ''))} |"
            for prompt in sorted(prompts, key=lambda prompt: prompt["name"])
        ]

    rendered = "\n".join(lines) + "\n"
    if "--check" in sys.argv[1:]:
        current = OUTPUT.read_text() if OUTPUT.is_file() else ""
        if current == rendered:
            print(f"{OUTPUT.relative_to(REPO)} is current ({len(tools)} tools)")
            return 0
        print(
            f"{OUTPUT.relative_to(REPO)} is stale; run: uv run eng/mcp/dump_tools.py",
            file=sys.stderr,
        )
        for line in difflib.unified_diff(
            current.splitlines(),
            rendered.splitlines(),
            fromfile="committed",
            tofile="server",
            lineterm="",
        ):
            print(line, file=sys.stderr)
        return 1

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(rendered)
    print(f"wrote {OUTPUT.relative_to(REPO)} ({len(tools)} tools)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
