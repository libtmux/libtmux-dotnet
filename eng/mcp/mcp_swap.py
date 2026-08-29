#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = ["tomlkit>=0.13"]
# ///
"""Swap MCP server configs across every installed agent CLI.

Use when you want every installed agent CLI to run a particular build of
``tmux-mcp`` -- the one you are editing, a compiled profile, or a
published release -- instead of whatever they point at now. ``use``
rewrites each CLI's config; ``revert`` restores from the timestamped
backup the swap wrote. Swapping a layer that is already swapped keeps
that first backup rather than taking a new one, so ``revert`` always
lands on the pre-swap config.

Sources
-------
``--source`` picks where the server comes from:

- ``debug`` / ``release`` build the project and name the apphost in
  ``bin/<Configuration>/<framework>/``, so an agent spawns it directly
  with no build step in front of the handshake. This is the default
  (``debug``).
- ``run`` launches through ``dotnet run``, which rebuilds on every start.
  Current source with nothing to remember, at the cost of a build check
  per launch -- and a slow first launch after a change can outlast a
  client's handshake timeout.
- ``published`` installs a NuGet release under its own tool path, so
  swapping between releases does not reinstall over the previous one.
- ``path`` takes a binary you name with ``--bin``, wherever it came
  from.

Defaults are derived from the project file:

- server name = ``ToolCommandName`` with a trailing ``-mcp`` stripped
  (``libtmux-mcp`` -> ``tmux``)
- binary name = ``AssemblyName``

The SDK is pinned by ``global.json`` and resolves through mise, so the
``dotnet`` an agent must launch is found once here and written into the
config as an absolute path. An agent does not inherit this shell.

Examples
--------
```console
$ uv run eng/mcp/mcp_swap.py detect
$ uv run eng/mcp/mcp_swap.py status
$ uv run eng/mcp/mcp_swap.py use --dry-run
$ uv run eng/mcp/mcp_swap.py use --source release
$ uv run eng/mcp/mcp_swap.py use --source run
$ uv run eng/mcp/mcp_swap.py use --source published --version 0.1.0-alpha.3
$ uv run eng/mcp/mcp_swap.py revert
```

Scope
-----
This script is best-effort and intentionally narrow:

- **Global configs only.** Writes to ``~/.cursor/mcp.json``,
  ``~/.claude.json``, ``~/.codex/config.toml``,
  ``~/.gemini/settings.json``, ``~/.grok/config.toml`` (TOML
  ``mcp_servers``, same shape as Codex),
  ``~/.gemini/config/mcp_config.json`` (agy / Antigravity CLI, JSON
  ``mcpServers`` — the shared-config file the CLI reads, sibling to the
  ``config.json`` it loads at startup),
  ``$XDG_CONFIG_HOME/opencode/opencode.jsonc`` (JSONC ``mcp``, comments
  preserved) and ``~/.pi/agent/mcp.json`` (JSONC too -- the adapter that
  reads it strips comments). Workspace / project-local
  configs (``$PWD/.cursor/mcp.json``, ``$PWD/.gemini/settings.json``,
  ``$PWD/opencode.json``, per-project ``projects.<abs>.mcpServers``
  entries inside ``~/.claude.json`` *are* recognised for Claude only)
  are NOT walked — workspace files for the others are silently ignored.
  When workspace precedence matters, run the CLI's own
  ``cursor mcp add ...`` / ``gemini mcp add ...`` directly. opencode has
  no non-interactive project-scope add -- ``opencode mcp add`` writes the
  global file -- so edit ``$PWD/opencode.json`` by hand for that.

- **opencode reads three global files.** ``config.json``,
  ``opencode.json`` and ``opencode.jsonc`` in the same directory are all
  loaded and merged, with ``.jsonc`` winning. This script owns
  ``.jsonc`` — the file opencode itself writes to — so its entry is the
  one that takes effect. A stale ``mcp.<name>`` left in a sibling
  ``opencode.json`` still merges underneath rather than being shadowed
  outright; remove it by hand if that matters.

- **pi has no MCP client of its own.** Its README says so, and the
  released build ships no MCP code. ``~/.pi/agent/mcp.json`` is read by
  the third-party ``pi-mcp-adapter`` extension, so a swap written there
  takes effect only once that package is installed. ``detect`` says as
  much rather than reporting a swap that cannot do anything.

- **Claude scope.** ``use`` and ``revert`` accept
  ``--scope {user,project}``. The default ``project`` writes the
  per-project entry under ``projects[<abs-repo>].mcpServers`` —
  only the current repo's directory sees the swap, matching
  pre-flag behaviour. ``--scope user`` writes Claude's top-level
  ``mcpServers`` fallback so every project that has no per-project
  override picks up the swap; useful when QA-ing a branch across
  many directories. Every other CLI here has no per-project layer in
  the config file this script writes; the flag is silently coerced to
  ``user`` for them. Both Claude scopes can coexist with
  independent backups; full ``revert`` unwinds in LIFO order.
- **Simple binary detection.** Probing is ``shutil.which(<binary>)``
  plus ``<config_path>.exists()``. Custom install locations
  (Homebrew, npm prefixes, ``~/.npm-global/bin``,
  ``~/.claude/local/claude``, ``~/.gemini/local/gemini``) are picked
  up only if the binary is on ``PATH``. FastMCP's installer probes
  these locations directly; this script does not.
- **Single config shape per CLI.** No fallback paths, no merge of
  multiple sources. If your setup deviates from the defaults above,
  use the CLI's native ``mcp`` subcommand instead.
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import difflib
import fcntl
import json
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import typing as t

import tomlkit
import tomlkit.items

# A sibling module, not a package: this file runs as a script, so its own
# directory is what Python imports from.
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import build
import jsonc
import xdg
from spec import Dialect, McpServerSpec

CLIName = t.Literal[
    "claude", "codex", "cursor", "gemini", "grok", "agy", "opencode", "pi"
]
ALL_CLIS: tuple[CLIName, ...] = (
    "claude",
    "codex",
    "cursor",
    "gemini",
    "grok",
    "agy",
    "opencode",
    "pi",
)

#: Width of the CLI-name column in ``detect`` output, derived rather
#: than hardcoded so adding a longer name cannot silently misalign it.
_CLI_COLUMN = max(len(name) for name in ALL_CLIS) + 1

#: Claude config scope: ``"user"`` targets the user/system-level top-level
#: ``mcpServers`` fallback that applies to every project without its own
#: override; ``"project"`` targets the project-level per-project
#: ``projects.<abs>.mcpServers`` node. Non-Claude CLIs have no
#: per-project scope in their config files, so for those CLIs the scope
#: is always normalised to ``"user"`` regardless of what was passed.
Scope = t.Literal["user", "project"]
ALL_SCOPES: tuple[Scope, ...] = ("user", "project")


def _normalize_scope(cli: CLIName, scope: Scope | None) -> Scope:
    """Coerce ``scope`` to the value that actually applies to ``cli``.

    Non-Claude CLIs have no per-project config layer — every write to
    them is necessarily user-level — so the flag is silently coerced to
    ``"user"`` for those. For Claude, ``None`` defaults to ``"project"``
    to preserve pre-flag behaviour where the script always wrote the
    per-project entry.
    """
    if cli != "claude":
        return "user"
    return scope if scope is not None else "project"


def _state_key(cli: CLIName, scope: Scope) -> str:
    """Compose the ``cli:scope`` key used inside the state file."""
    return f"{cli}:{scope}"


def _parse_state_key(key: str) -> tuple[CLIName, Scope] | None:
    """Decode a ``cli:scope`` state key, returning ``None`` for malformed input.

    The script declares no compatibility contract for its state file —
    schema is internal — so this only accepts the canonical
    ``f"{cli}:{scope}"`` form. Hand-edited or unrecognised keys return
    ``None`` so ``load_state`` can drop them without crashing.
    """
    if ":" not in key:
        return None
    cli_str, _, scope_str = key.partition(":")
    if cli_str in ALL_CLIS and scope_str in ALL_SCOPES:
        return cli_str, scope_str
    return None


def _parse_state_entry(v: dict[str, t.Any]) -> SwapEntry | None:
    """Build a :class:`SwapEntry` from a raw state-file dict, or ``None``.

    Validates at the trust boundary so a hand-edited ``state.json`` can't
    crash later code paths — particularly :func:`cmd_revert`'s LIFO sort,
    which compares ``SwapEntry.seq_no`` and would raise ``TypeError`` on a
    mixed ``int``/``str`` ordering. ``seq_no`` is coerced via ``int()``;
    any ``KeyError`` (missing required field), ``ValueError`` (non-numeric
    string), or ``TypeError`` (wrong shape, extra keys for the dataclass)
    drops the entry silently. Same drop-on-malformed posture as
    :func:`_parse_state_key`.

    Mirrors CPython's ``Lib/sched.py`` discipline: validate at the
    counter's *origin* (``enterabs`` for sched, ``load_state`` here), not
    at sort time. State-file schema is internal — no compatibility
    contract — so silent drop is the right failure mode.
    """
    try:
        v = {**v, "seq_no": int(v["seq_no"])}
        return SwapEntry(**v)
    except (KeyError, TypeError, ValueError):
        return None


# ``-dev`` suffix in the namespace makes it loud that this is dev-only
# tooling state, distinct from the ``LibTmux.Mcp`` tool it swaps.
STATE_DIR = xdg.state_home() / "tmux-mcp-dev" / "swap"
STATE_FILE = STATE_DIR / "state.json"

BACKUP_SUFFIX_PREFIX = ".bak.mcp-swap-"




@dataclasses.dataclass(frozen=True)
class CLIInfo:
    """Static descriptor for a CLI's config file and discovery heuristics."""

    name: CLIName
    binary: str
    config_path: pathlib.Path
    fmt: t.Literal["json", "jsonc", "toml"]
    #: Key path from the document root down to the mapping of server
    #: name -> entry. A path rather than a single key so a CLI that
    #: nests deeper needs no new branch in the four functions that
    #: read, write, delete and enumerate entries.
    container: tuple[str, ...]
    #: Entry shape written and read back for this CLI.
    dialect: Dialect


CLIS: dict[CLIName, CLIInfo] = {
    "claude": CLIInfo(
        name="claude",
        binary="claude",
        config_path=pathlib.Path.home() / ".claude.json",
        fmt="json",
        container=("mcpServers",),
        dialect="claude",
    ),
    "codex": CLIInfo(
        name="codex",
        binary="codex",
        config_path=pathlib.Path.home() / ".codex" / "config.toml",
        fmt="toml",
        container=("mcp_servers",),
        dialect="standard",
    ),
    "cursor": CLIInfo(
        name="cursor",
        binary="cursor-agent",
        config_path=pathlib.Path.home() / ".cursor" / "mcp.json",
        fmt="json",
        container=("mcpServers",),
        dialect="standard",
    ),
    "gemini": CLIInfo(
        name="gemini",
        binary="gemini",
        config_path=pathlib.Path.home() / ".gemini" / "settings.json",
        fmt="json",
        container=("mcpServers",),
        dialect="standard",
    ),
    "grok": CLIInfo(
        name="grok",
        binary="grok",
        config_path=pathlib.Path.home() / ".grok" / "config.toml",
        fmt="toml",
        container=("mcp_servers",),
        dialect="standard",
    ),
    "agy": CLIInfo(
        name="agy",
        binary="agy",
        config_path=(pathlib.Path.home() / ".gemini" / "config" / "mcp_config.json"),
        fmt="json",
        container=("mcpServers",),
        dialect="standard",
    ),
    "opencode": CLIInfo(
        name="opencode",
        binary="opencode",
        config_path=xdg.config_home() / "opencode" / "opencode.jsonc",
        fmt="jsonc",
        container=("mcp",),
        dialect="opencode",
    ),
    "pi": CLIInfo(
        name="pi",
        binary="pi",
        # pi-mcp-adapter (see PI_ADAPTER_DIR) parses via strip-json-comments
        # with trailing commas allowed, so this is jsonc despite the .json name.
        config_path=pathlib.Path.home() / ".pi" / "agent" / "mcp.json",
        fmt="jsonc",
        container=("mcpServers",),
        dialect="standard",
    ),
}

#: Written into an opencode config this script creates from nothing.
#: opencode injects the same line itself on first load; seeding it here
#: keeps the swap from being followed by a surprise rewrite.
OPENCODE_SCHEMA_URL = "https://opencode.ai/config.json"

#: pi has no built-in MCP client; only the third-party ``pi-mcp-adapter``
#: extension reads the file this swap writes.
PI_ADAPTER_DIR = (
    pathlib.Path.home() / ".pi" / "agent" / "npm" / "node_modules" / "pi-mcp-adapter"
)
PI_ADAPTER_HINT = "needs the pi-mcp-adapter package; pi has no built-in MCP client"


#: A ``--from`` argument pointing at a pull request's head commit.
#: GitHub publishes ``refs/pull/<n>/head`` on the *base* repository, so
#: one URL serves same-repo and fork pull requests alike.

@dataclasses.dataclass
class SwapEntry:
    """One CLI's bookkeeping for a swap, written to the state file."""

    config_path: str
    backup_path: str
    server: str
    action: t.Literal["replaced", "added"]
    #: ``YYYYMMDDHHMMSS`` registration timestamp, human-readable for
    #: anyone inspecting ``state.json`` directly. Sort order is enforced
    #: separately via :attr:`seq_no` so this field stays purely
    #: descriptive.
    swapped_at: str
    #: Monotonic LIFO sort key for :func:`cmd_revert`, assigned as
    #: ``max(existing, default=-1) + 1`` so order is independent of
    #: wall-clock collisions or dict iteration order.
    seq_no: int
    #: Exact destination changed by the swap. ``config_path`` may be a
    #: symlink that is later repointed, so it is not sufficient recovery
    #: identity. Older state entries omit this field and fall back to
    #: ``config_path`` during revert.
    target_path: str | None = None


class SwapStateError(RuntimeError):
    """Swap state is unsafe to use for a mutating operation."""

# ---------------------------------------------------------------------------
# Config IO — per format
# ---------------------------------------------------------------------------


def load_config(info: CLIInfo) -> t.Any:
    """Parse a CLI's config file (JSON, JSONC or TOML) into an editable structure.

    Empty JSON files are treated as empty objects so first-run MCP configs can
    be seeded with their initial server entry.
    """
    raw = info.config_path.read_bytes()
    if info.fmt == "jsonc":
        return jsonc.loads(raw.decode())
    if info.fmt == "json":
        text = raw.decode().strip()
        return json.loads(text) if text else {}
    return tomlkit.parse(raw.decode())


def _json_trailer(original: bytes) -> str:
    """Return the newline a rewritten JSON config should end with.

    Claude writes ``~/.claude.json`` without a trailing newline, so
    appending one unconditionally grows the file by a byte on every swap
    and shows as a diff hunk in a region the swap never touched. Empty
    bytes mean a file being seeded, which gets the conventional newline.
    """
    if not original:
        return "\n"
    return "\n" if original.endswith(b"\n") else ""


def dump_config_bytes(info: CLIInfo, config: t.Any, *, original: bytes) -> bytes:
    """Serialize an edited config back to bytes in its original format.

    ``original`` is the file's pre-edit bytes, or empty when seeding a
    new one. The parsed structure does not record the byte-level
    conventions of the file it came from, so they are carried over from
    the source instead. Required rather than defaulted: a caller that
    omitted it would silently start rewriting regions it never touched,
    which is the defect this parameter exists to prevent. tomlkit
    preserves those conventions itself; only the JSON writer needs it.
    """
    # Dispatched on the exact format rather than "not json": a third
    # format reaching the TOML writer by fall-through would silently
    # write TOML bytes into a JSON file.
    if info.fmt == "toml":
        return tomlkit.dumps(config).encode()
    if info.fmt == "jsonc":
        # The merge derives its output from the original text, so the
        # file's own trailing-newline convention carries over untouched
        # and needs no _json_trailer fixup.
        source = original.decode()
        try:
            return jsonc.merge(source, config, ensure_ascii=False).encode()
        except UnicodeEncodeError:
            return jsonc.merge(source, config, ensure_ascii=True).encode()
    trailer = _json_trailer(original)
    # ensure_ascii would re-escape every non-ASCII character in the file,
    # including config text the swap never read.
    text = json.dumps(config, indent=2, ensure_ascii=False) + trailer
    try:
        return text.encode()
    except UnicodeEncodeError:
        # A lone surrogate — a JS writer slicing a string mid-pair — has no
        # UTF-8 encoding. Escaping the document is then the only form that
        # can be written at all.
        return (json.dumps(config, indent=2) + trailer).encode()


def atomic_write(path: pathlib.Path, data: bytes) -> None:
    """Write bytes to ``path`` without replacing a symlinked config.

    Parameters
    ----------
    path : pathlib.Path
        Destination path. A symlink resolves to its final target so the
        write preserves every link in the chain.
    data : bytes
        Bytes to write atomically.
    """
    target = path.resolve() if path.is_symlink() else path
    target.parent.mkdir(parents=True, exist_ok=True)
    mode = stat.S_IMODE(target.stat().st_mode) if target.exists() else None
    fd, tmp_name = tempfile.mkstemp(prefix=target.name + ".", dir=str(target.parent))
    tmp = pathlib.Path(tmp_name)
    try:
        with os.fdopen(fd, "wb") as fh:
            if mode is not None:
                os.fchmod(fh.fileno(), mode)
            fh.write(data)
        tmp.replace(target)
    except Exception:
        tmp.unlink(missing_ok=True)
        raise


def write_new_backup(base: pathlib.Path, data: bytes) -> pathlib.Path:
    """Write ``data`` to ``base``, or to ``base-1`` / ``base-2`` / … if taken.

    A backup is the only copy of the config as it stood before a swap, so
    clobbering one is unrecoverable data loss. The timestamp embedded in
    ``base`` has one-second granularity, which is not fine enough on its
    own: two swaps inside the same second derive the same path. Creation
    goes through ``O_CREAT | O_EXCL`` so the check and the claim are one
    atomic step and an existing file can never be truncated — the same
    exclusive-create discipline CPython's ``tempfile`` uses to hand out
    unique names.

    Returns the path actually written.
    """
    base.parent.mkdir(parents=True, exist_ok=True)
    candidate = base
    attempt = 0
    while True:
        try:
            fd = os.open(candidate, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            attempt += 1
            candidate = base.with_name(f"{base.name}-{attempt}")
            continue
        with os.fdopen(fd, "wb") as fh:
            fh.write(data)
        return candidate


# ---------------------------------------------------------------------------
# Per-CLI get / set / delete (the only CLI-specific logic)
# ---------------------------------------------------------------------------


@t.overload
def _claude_project_node(
    config: dict[str, t.Any],
    repo: pathlib.Path,
    *,
    create: t.Literal[True],
) -> dict[str, t.Any]: ...


@t.overload
def _claude_project_node(
    config: dict[str, t.Any],
    repo: pathlib.Path,
    *,
    create: t.Literal[False],
) -> dict[str, t.Any] | None: ...


def _claude_project_node(
    config: dict[str, t.Any], repo: pathlib.Path, *, create: bool
) -> dict[str, t.Any] | None:
    """Return (or create) the ``projects.<abs-repo>`` node Claude keys per-project.

    With ``create=True``, the node is unconditionally created if missing
    and the return type is statically narrowed to ``dict[str, t.Any]``;
    callers can drop runtime ``assert node is not None`` defensiveness.
    With ``create=False``, the absence of the node is a real return value
    and the type stays ``dict[str, t.Any] | None``.

    Raises ``RuntimeError`` if Claude's config layout is not the
    expected ``projects.<abs>.mcpServers`` mapping shape — the layout
    is undocumented Claude Code internal state, so a clear error before
    the atomic write beats a silent partial mutation that the backup
    defense would be asked to recover from.
    """
    key = str(repo.resolve())
    projects_node = config.get("projects")
    if projects_node is not None and not isinstance(projects_node, dict):
        msg = (
            "Claude config layout appears to have changed; expected "
            f"'projects' to be a mapping but got "
            f"{type(projects_node).__name__}"
        )
        raise RuntimeError(msg)
    projects = (
        config.setdefault("projects", {}) if create else config.get("projects", {})
    )
    raw_node = projects.get(key)
    node: dict[str, t.Any] | None = None
    if isinstance(raw_node, dict):
        node = raw_node
    elif raw_node is not None:
        msg = (
            "Claude config layout appears to have changed; expected "
            f"'projects[{key!r}]' to be a mapping but got "
            f"{type(raw_node).__name__}"
        )
        raise RuntimeError(msg)
    if node is None and create:
        node = {"allowedTools": [], "mcpContextUris": [], "mcpServers": {}, "env": {}}
        projects[key] = node
    return node


@t.overload
def _claude_user_servers(
    config: dict[str, t.Any], *, create: t.Literal[True]
) -> dict[str, t.Any]: ...


@t.overload
def _claude_user_servers(
    config: dict[str, t.Any], *, create: t.Literal[False]
) -> dict[str, t.Any] | None: ...


def _claude_user_servers(
    config: dict[str, t.Any], *, create: bool
) -> dict[str, t.Any] | None:
    """Return (or create) the top-level ``mcpServers`` dict — Claude user scope.

    Mirrors :func:`_claude_project_node` for the user-scope path so the
    shape guard is centralised once and reused across read / write /
    delete instead of duplicated at each call site (or worse, missing
    on read and delete the way the inline write-side guard left them).
    Same reasoning applies as for the project-scope helper: Claude's
    config shape is undocumented internal state, so a clear
    ``RuntimeError`` before the atomic write beats an opaque
    ``AttributeError`` from ``.setdefault()`` on a non-dict.

    With ``create=True`` the dict is initialised when missing and the
    return type narrows to ``dict[str, t.Any]``. With ``create=False``
    a missing key returns ``None``.
    """
    raw = config.get("mcpServers")
    existing: dict[str, t.Any] | None = None
    if isinstance(raw, dict):
        existing = raw
    elif raw is not None:
        msg = (
            "Claude config layout appears to have changed; expected "
            f"'mcpServers' to be a mapping but got "
            f"{type(raw).__name__}"
        )
        raise RuntimeError(msg)
    if existing is None and create:
        existing = {}
        config["mcpServers"] = existing
    return existing


@t.overload
def _server_map(
    info: CLIInfo, config: t.Any, *, create: t.Literal[True]
) -> dict[str, t.Any]: ...


@t.overload
def _server_map(
    info: CLIInfo, config: t.Any, *, create: t.Literal[False]
) -> dict[str, t.Any] | None: ...


def _server_map(
    info: CLIInfo, config: t.Any, *, create: bool
) -> dict[str, t.Any] | None:
    """Walk ``info.container`` to the mapping holding this CLI's entries.

    Returns ``None`` when the path is absent and ``create`` is false.
    Intermediate levels are created on demand so a nested container needs
    no special case; TOML gets tomlkit tables so the written document
    keeps its formatting.

    Raises
    ------
    RuntimeError
        A key along the path holds something other than a mapping.
        Reported rather than overwritten — a swap must never discard
        config it cannot interpret.
    """
    node: dict[str, t.Any] = config
    for depth, key in enumerate(info.container):
        child = node.get(key)
        if child is None:
            if not create:
                return None
            child = tomlkit.table() if info.fmt == "toml" else {}
            node[key] = child
        elif not isinstance(child, dict):
            path = ".".join(info.container[: depth + 1])
            msg = (
                f"{info.config_path}: {path} is a {type(child).__name__}, "
                f"expected a table of server entries"
            )
            raise RuntimeError(msg)
        node = child
    return node


def _as_toml_table(entry: dict[str, t.Any]) -> tomlkit.items.Table:
    """Render one entry dict as a tomlkit table.

    Nested mappings (``env``) become sub-tables so the written document
    keeps TOML's own structure instead of an inline dict literal.
    """
    table = tomlkit.table()
    for key, value in entry.items():
        if isinstance(value, dict):
            sub = tomlkit.table()
            for sub_key, sub_value in value.items():
                sub[sub_key] = sub_value
            table[key] = sub
        else:
            table[key] = value
    return table


def get_server(
    cli: CLIName,
    config: t.Any,
    name: str,
    repo: pathlib.Path,
    *,
    scope: Scope = "project",
) -> McpServerSpec | None:
    """Fetch the MCP server entry for ``name`` from a CLI's config, if present.

    ``scope`` only affects Claude (see :data:`Scope` for the layered shape
    of ``~/.claude.json``); for Codex / Cursor / Gemini the parameter is
    accepted-but-ignored because their config has no per-project layer.
    """
    if cli == "claude":
        if scope == "user":
            servers = _claude_user_servers(config, create=False)
            entry = servers.get(name) if servers else None
        else:
            node = _claude_project_node(config, repo, create=False)
            if not node:
                return None
            entry = node.get("mcpServers", {}).get(name)
    else:
        servers = _server_map(CLIS[cli], config, create=False)
        entry = servers.get(name) if servers else None
    if entry is None:
        return None
    return _spec_from_entry(entry, info=CLIS[cli])


def set_server(
    cli: CLIName,
    config: t.Any,
    name: str,
    spec: McpServerSpec,
    repo: pathlib.Path,
    *,
    scope: Scope = "project",
) -> t.Literal["replaced", "added"]:
    """Write ``spec`` under ``name`` in a CLI's config, returning replaced/added.

    ``scope == "user"`` for Claude writes the top-level ``mcpServers``
    fallback used by every project that has no per-project override;
    ``"project"`` (the default, preserving pre-flag behaviour) writes
    under ``projects[abs(repo)].mcpServers``. The parameter is silently
    ignored for non-Claude CLIs.
    """
    if cli == "claude":
        if scope == "user":
            servers = _claude_user_servers(config, create=True)
            had = name in servers
            servers[name] = spec.to_entry_dict("claude")
            return "replaced" if had else "added"
        node = _claude_project_node(config, repo, create=True)
        servers = node.setdefault("mcpServers", {})
        had = name in servers
        servers[name] = spec.to_entry_dict("claude")
        return "replaced" if had else "added"
    info = CLIS[cli]
    if info.dialect == "opencode" and not config:
        # Seeding from nothing: opencode rewrites the file on load to add
        # this line, so writing it now avoids an immediate second edit.
        config["$schema"] = OPENCODE_SCHEMA_URL
    servers = _server_map(info, config, create=True)
    had = name in servers
    entry = spec.to_entry_dict(info.dialect)
    servers[name] = _as_toml_table(entry) if info.fmt == "toml" else entry
    return "replaced" if had else "added"


def delete_server(
    cli: CLIName,
    config: t.Any,
    name: str,
    repo: pathlib.Path,
    *,
    scope: Scope = "project",
) -> bool:
    """Remove the entry for ``name`` from a CLI's config; return whether it existed.

    See :func:`set_server` for the meaning of ``scope`` — the parameter
    is honoured for Claude and ignored for the other CLIs.
    """
    if cli == "claude":
        if scope == "user":
            servers = _claude_user_servers(config, create=False)
            if servers is not None and name in servers:
                del servers[name]
                return True
            return False
        node = _claude_project_node(config, repo, create=False)
        if not node:
            return False
        servers = node.get("mcpServers", {})
        return servers.pop(name, None) is not None
    servers = _server_map(CLIS[cli], config, create=False)
    if servers is None or name not in servers:
        return False
    del servers[name]
    return True


def _spec_from_entry(entry: t.Any, *, info: CLIInfo) -> McpServerSpec:
    """Convert a raw config entry (dict or tomlkit Table) into an McpServerSpec.

    Every dialect is normalised down to the portable scalar-command
    shape, so the helpers that reason about a spec —
    :meth:`McpServerSpec.local_repo_path`, :meth:`McpServerSpec.dotnet_configuration`,
    ``_points_at`` — stay dialect-agnostic. Skipping this is not a
    cosmetic loss: an unsplit array command makes the "already local, no
    change" check miss, and every run rewrites a config it did not need
    to touch.
    """
    # tomlkit items quack like dicts/lists; coerce to plain Python for our spec.
    if info.fmt == "toml":
        entry = (
            tomlkit.items.Table.unwrap(entry)
            if isinstance(entry, tomlkit.items.Table)
            else dict(entry)
        )
    if info.dialect == "opencode":
        raw_command = entry.get("command", [])
        argv = (
            [str(part) for part in raw_command]
            if isinstance(raw_command, (list, tuple))
            else [str(raw_command)]
        )
        command, args = (argv[0], argv[1:]) if argv else ("", [])
        raw_env = entry.get("environment") or {}
    else:
        command = str(entry.get("command", ""))
        raw_args = entry.get("args", [])
        args = [str(a) for a in raw_args] if raw_args else []
        raw_env = entry.get("env") or {}
    env = {str(k): str(v) for k, v in dict(raw_env).items()}
    return McpServerSpec(command=command, args=args, env=env)


# ---------------------------------------------------------------------------
# Repo metadata
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# State file
# ---------------------------------------------------------------------------


def load_state(*, strict: bool = False) -> dict[tuple[CLIName, Scope], SwapEntry]:
    """Read the swap-state file, returning an empty mapping when absent.

    The state file's schema is internal — no compatibility contract —
    so this loader assumes a single canonical shape. Malformed keys
    (those that don't parse as ``cli:scope``) and entries with a
    non-coercible ``seq_no`` or missing required fields are dropped
    silently so a hand-edited file cannot crash the script.

    A file that will not parse at all is reported rather than dropped
    silently: it means the record of every swap is gone, so ``revert``
    is about to say there is nothing to unwind while swapped configs
    and their backups sit on disk. Saying so is what lets the operator
    go find those backups. Mutating callers pass ``strict=True`` so an
    unreadable or malformed record blocks changes instead of being
    overwritten as empty state.
    """
    if not STATE_FILE.exists():
        return {}
    try:
        raw = json.loads(STATE_FILE.read_text())
    except (OSError, ValueError) as exc:
        message = f"swap state unreadable ({STATE_FILE}): {exc}"
        print(message, file=sys.stderr)
        if strict:
            raise SwapStateError(message) from exc
        return {}
    if not isinstance(raw, dict):
        if strict:
            message = f"swap state has invalid shape: {STATE_FILE}"
            print(message, file=sys.stderr)
            raise SwapStateError(message)
        return {}
    entries = raw.get("entries", {})
    if not isinstance(entries, dict):
        if strict:
            message = f"swap state has invalid entries: {STATE_FILE}"
            print(message, file=sys.stderr)
            raise SwapStateError(message)
        entries = {}
    out: dict[tuple[CLIName, Scope], SwapEntry] = {}
    for k, v in entries.items():
        parsed = _parse_state_key(k)
        if parsed is None:
            if strict:
                message = f"swap state has invalid key {k!r}: {STATE_FILE}"
                print(message, file=sys.stderr)
                raise SwapStateError(message)
            continue
        entry = _parse_state_entry(v)
        if entry is None:
            if strict:
                message = f"swap state has invalid entry {k!r}: {STATE_FILE}"
                print(message, file=sys.stderr)
                raise SwapStateError(message)
            continue
        out[parsed] = entry
    return out


@contextlib.contextmanager
def _state_lock() -> t.Iterator[None]:
    """Serialize config mutations that share the swap state file."""
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    fd = os.open(STATE_DIR / "state.lock", os.O_RDWR | os.O_CREAT, 0o600)
    with os.fdopen(fd, "a+b") as lock_file:
        fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
        yield


def save_state(entries: dict[tuple[CLIName, Scope], SwapEntry]) -> None:
    """Write the swap-state file atomically."""
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    payload = {
        "entries": {
            _state_key(cli, scope): dataclasses.asdict(v)
            for (cli, scope), v in entries.items()
        },
    }
    atomic_write(STATE_FILE, (json.dumps(payload, indent=2) + "\n").encode("utf-8"))


def _save_or_clear_state(entries: dict[tuple[CLIName, Scope], SwapEntry]) -> None:
    """Persist ``entries``, removing the state file when the mapping is empty."""
    if entries:
        save_state(entries)
    elif STATE_FILE.exists():
        STATE_FILE.unlink()


# ---------------------------------------------------------------------------
# Detection
# ---------------------------------------------------------------------------


@dataclasses.dataclass
class Presence:
    """Detection outcome for a CLI: binary on PATH and config file present."""

    cli: CLIName
    binary_found: bool
    config_found: bool

    @property
    def present(self) -> bool:
        """Return True only when both the binary and the config file were found."""
        return self.binary_found and self.config_found


def detect_clis() -> list[Presence]:
    """Probe all supported CLIs and return their detection results."""
    return [
        Presence(
            cli=info.name,
            binary_found=shutil.which(info.binary) is not None,
            config_found=info.config_path.exists(),
        )
        for info in CLIS.values()
    ]


def present_clis() -> list[CLIName]:
    """Return the list of CLIs that have both a binary and a config present."""
    return [p.cli for p in detect_clis() if p.present]


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------


def cmd_detect(args: argparse.Namespace) -> int:
    """Print detection results for every supported CLI."""
    for p in detect_clis():
        flag = "yes" if p.present else " no"
        extra = []
        if not p.binary_found:
            extra.append("binary missing")
        if not p.config_found:
            extra.append(f"config missing: {CLIS[p.cli].config_path}")
        if p.cli == "pi" and not PI_ADAPTER_DIR.is_dir():
            extra.append(PI_ADAPTER_HINT)
        suffix = f"  ({', '.join(extra)})" if extra else ""
        print(f"  [{flag}] {p.cli:<{_CLI_COLUMN}}{suffix}")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    """Print the current MCP server entry per detected CLI.

    For Claude, prints separate lines for the user-level fallback
    (``[claude:user]``) and the per-project override
    (``[claude:project]``) when both exist; if only one exists, only
    that line shows. ``args.scope`` (when set) restricts Claude output
    to the matching layer only. Other CLIs print a single line as
    ``[<cli>]`` since their config has no scope concept and ignore
    ``args.scope``.
    """
    repo = pathlib.Path(args.repo).resolve()
    server = args.server or build.resolve_repo_meta(repo)[0]
    scope_filter: Scope | None = args.scope
    for cli in args.cli or present_clis():
        info = CLIS[cli]
        if not info.config_path.exists():
            print(f"[{cli}] (no config at {info.config_path})")
            continue
        # Wrap the read + shape-guarded queries in try/except RuntimeError
        # so a malformed Claude config surfaces as a clean per-CLI error
        # instead of aborting status output for the rest of the CLIs.
        try:
            config = load_config(info)
            if cli == "claude":
                # Lazy reads: skip the get_server call entirely for the
                # filtered-out scope so a malformed projects node doesn't
                # raise when the user only asked about user scope.
                user_spec = (
                    get_server(cli, config, server, repo, scope="user")
                    if scope_filter in (None, "user")
                    else None
                )
                project_spec = (
                    get_server(cli, config, server, repo, scope="project")
                    if scope_filter in (None, "project")
                    else None
                )
                shown = False
                if user_spec is not None:
                    tag = _describe_spec(user_spec, repo)
                    print(
                        f"[claude:user] {server} = {user_spec.command} "
                        f"{' '.join(user_spec.args)}  ({tag})"
                    )
                    shown = True
                if project_spec is not None:
                    tag = _describe_spec(project_spec, repo)
                    print(
                        f"[claude:project] {server} = {project_spec.command} "
                        f"{' '.join(project_spec.args)}  ({tag})"
                    )
                    shown = True
                if not shown:
                    label = f"claude:{scope_filter}" if scope_filter else "claude"
                    print(f"[{label}] no entry for {server!r}")
            else:
                spec = get_server(cli, config, server, repo)
                if spec is None:
                    print(f"[{cli}] no entry for {server!r}")
                    continue
                tag = _describe_spec(spec, repo)
                print(
                    f"[{cli}] {server} = {spec.command} {' '.join(spec.args)}  ({tag})"
                )
        except (RuntimeError, ValueError, OSError) as exc:
            print(f"[{cli}] {exc}", file=sys.stderr)
            continue
    return 0


def _describe_spec(spec: McpServerSpec, repo: pathlib.Path) -> str:
    """Return a short label saying where a configured server comes from."""
    project = spec.project_path()
    if project is not None:
        local = spec.local_repo_path()
        here = local is not None and local.resolve() == repo.resolve()
        return "dotnet run: this repo" if here else f"dotnet run: {project}"

    binary = spec.built_binary_path()
    if binary is not None:
        configuration = spec.dotnet_configuration()
        local = spec.local_repo_path()
        if configuration and local and local.resolve() == repo.resolve():
            return f"{configuration.lower()} build: this repo"
        releases = build.RELEASES_ROOT
        try:
            relative = binary.relative_to(releases)
        except ValueError:
            return f"binary: {binary}"
        else:
            # releases/<binary>-<version>/<command>
            return f"nuget: {relative.parts[0].rsplit('-', 1)[-1]}"

    if "/" not in spec.command:
        return f"on PATH: {spec.command}"
    return "other"


def _points_at(
    current: McpServerSpec, target: McpServerSpec, repo: pathlib.Path
) -> bool:
    """Return True when ``current`` already runs what ``target`` describes.

    The environment counts. An entry naming the right binary without the
    variables that let it find its runtime does not run it -- it fails at
    launch, inside the agent -- so treating that as "already correct"
    would leave a broken config in place and report success.
    """
    return (
        current.command == target.command
        and current.args == target.args
        and current.env == target.env
    )


def _cmd_use_local(args: argparse.Namespace) -> int:
    """Rewrite each target CLI's config to run the repo, or a pull request.

    Which build the entry runs is chosen by ``--source``; see the module
    docstring for what each one costs.

    The optional ``--scope`` flag selects Claude's user-level fallback
    vs. per-project override; see :data:`Scope`. The flag is silently
    coerced to ``"user"`` for non-Claude CLIs by :func:`_normalize_scope`.
    """
    repo = pathlib.Path(args.repo).resolve()
    project = getattr(args, "project", None) or build.DEFAULT_PROJECT
    server, default_binary = build.resolve_repo_meta(repo, project)
    server = args.server or server
    binary = args.entry or default_binary
    command = build.project_property(
        build.project_file(repo, project).read_text(), "ToolCommandName"
    ) or "libtmux-mcp"
    extra_env = dict(args.env or [])
    source: build.Source = getattr(args, "source", "debug")

    # A config naming a binary that was never built leaves every agent
    # failing to start a server, and the failure surfaces inside the agent
    # rather than here.
    try:
        if source in ("debug", "release") and not getattr(args, "no_build", False):
            build.dotnet_build(repo, source.capitalize(), project)
        if source == "published":
            build.install_published("LibTmux.Mcp", args.version, binary, command)
        spec = build.build_source_spec(
            source,
            repo=repo,
            binary=binary,
            project=project,
            command=command,
            version=getattr(args, "version", None),
            binary_path=(
                pathlib.Path(args.bin) if getattr(args, "bin", None) else None
            ),
        )
    except (RuntimeError, subprocess.CalledProcessError) as exc:
        print(f"{source}: {exc}", file=sys.stderr)
        return 1

    launcher = pathlib.Path(spec.command)
    if not launcher.is_file():
        print(f"{source}: {launcher} does not exist", file=sys.stderr)
        return 1
    spec = dataclasses.replace(spec, env={**spec.env, **extra_env})

    hint = _naming_hint(repo, server)
    if hint:
        print(hint, file=sys.stderr)

    targets = args.cli or present_clis()
    if not targets:
        print("no CLIs detected — nothing to do", file=sys.stderr)
        return 1

    # Runs under --dry-run too, and for every source: starting the server
    # once here is the difference between finding out that a build cannot
    # speak the protocol now, and finding out from inside each agent.
    if not args.no_preflight:
        print(f"preflight: {spec.command} {' '.join(spec.args)}", file=sys.stderr)
        failure = build.preflight_spec(spec)
        if failure is not None:
            print(f"preflight failed, nothing written:\n{failure}", file=sys.stderr)
            return 1

    ts = time.strftime("%Y%m%d%H%M%S")
    state = load_state(strict=True)
    had_error = 0
    for cli in targets:
        scope = _normalize_scope(cli, args.scope)
        label = f"{cli}:{scope}" if cli == "claude" else cli
        info = CLIS[cli]
        if not info.config_path.exists():
            print(f"[{label}] skip — config not found at {info.config_path}")
            had_error = 1
            continue
        target_path = info.config_path.resolve()
        target_info = dataclasses.replace(info, config_path=target_path)
        # Per-CLI: RuntimeError (bad shape), ValueError (unparseable),
        # OSError (unreadable) all surface as one clean error, not a traceback.
        try:
            original_bytes = target_path.read_bytes()
            config = load_config(target_info)
            current = get_server(cli, config, server, repo, scope=scope)
            if (
                current
                and _points_at(current, spec, repo)
                and all(current.env.get(k) == v for k, v in extra_env.items())
            ):
                where = _describe_spec(spec, repo)
                print(f"[{label}] already {where} — no change")
                continue
            # Three layers, weakest first. The existing entry supplies
            # client-side settings a swap must not drop (LIBTMUX_SAFETY,
            # LIBTMUX_SOCKET, custom dev knobs). The spec overrides them with
            # what it computed this run -- the runtime location, which is
            # derived from the SDK in use and would otherwise be inherited
            # stale from a swap made against a different one. Explicit --env
            # wins over both, because it is the operator saying so.
            base_env = dict(current.env) if current else {}
            base_env.update(spec.env)
            base_env.update(extra_env)
            cli_spec = dataclasses.replace(spec, env=base_env)
            action = set_server(cli, config, server, cli_spec, repo, scope=scope)
            new_bytes = dump_config_bytes(info, config, original=original_bytes)
        except (RuntimeError, ValueError, OSError) as exc:
            print(f"[{label}] {exc}", file=sys.stderr)
            had_error = 1
            continue

        if args.dry_run:
            print(f"--- {info.config_path} (current)")
            print(f"+++ {info.config_path} (proposed)")
            diff = difflib.unified_diff(
                original_bytes.decode(errors="replace").splitlines(keepends=True),
                new_bytes.decode(errors="replace").splitlines(keepends=True),
                lineterm="",
            )
            sys.stdout.writelines(diff)
            continue

        # Re-swapping an unreverted layer must not re-back-up: original_bytes
        # is this script's own prior output, so keep the first backup (the
        # only copy of the pristine config) and its seq_no/swapped_at.
        prior = state.get((cli, scope))
        prior_backup = pathlib.Path(prior.backup_path) if prior is not None else None
        if prior_backup is not None and prior_backup.exists():
            backup_path = prior_backup
            backup_note = f"pre-swap backup kept: {backup_path}"
        else:
            if prior is not None:
                print(
                    f"[{label}] recorded backup is gone ({prior.backup_path}); the "
                    "new backup captures the already-swapped config, not the "
                    "original",
                    file=sys.stderr,
                )
            # Claude is the only CLI where two swaps (different scopes) can
            # touch the same config file in one second; embed the scope so
            # the two backups read distinctly. Non-Claude backup filenames
            # carry no scope suffix. Collisions past that are resolved by
            # ``write_new_backup``, which never overwrites.
            backup_suffix = f"{BACKUP_SUFFIX_PREFIX}{ts}"
            if cli == "claude":
                backup_suffix += f"-{scope}"
            # A backup that cannot be written must abort this CLI rather
            # than degrade into a swap with nothing to revert to — an
            # unwritable directory is the case that produces both.
            try:
                backup_path = write_new_backup(
                    info.config_path.with_suffix(
                        info.config_path.suffix + backup_suffix
                    ),
                    original_bytes,
                )
            except OSError as exc:
                print(f"[{label}] cannot write backup: {exc}", file=sys.stderr)
                had_error = 1
                continue
            backup_note = f"backup: {backup_path}"
        if prior is not None and backup_path == prior_backup:
            # ``swapped_at`` mirrors the timestamp in the backup filename
            # and ``seq_no`` fixes the backup's place in the unwind
            # stack; both describe the kept backup, not this run.
            seq_no, swapped_at = prior.seq_no, prior.swapped_at
        else:
            seq_no = max((e.seq_no for e in state.values()), default=-1) + 1
            swapped_at = ts
        next_state = dict(state)
        next_state[(cli, scope)] = SwapEntry(
            config_path=str(info.config_path),
            backup_path=str(backup_path),
            server=server,
            action=action,
            swapped_at=swapped_at,
            seq_no=seq_no,
            target_path=str(target_path),
        )
        try:
            save_state(next_state)
        except OSError as exc:
            print(
                f"[{label}] cannot save recovery state ({exc}); config unchanged; "
                f"backup at {backup_path}",
                file=sys.stderr,
            )
            had_error = 1
            continue
        previous_state = state
        state = next_state
        try:
            atomic_write(target_path, new_bytes)
            _revalidate(target_info)
        except Exception as exc:
            try:
                atomic_write(target_path, original_bytes)
            except Exception as rollback_exc:
                rollback_note = f"; rollback failed ({rollback_exc})"
            else:
                rollback_note = "; original config restored"
                try:
                    _save_or_clear_state(previous_state)
                except OSError as state_exc:
                    rollback_note += f"; recovery state cleanup failed ({state_exc})"
                else:
                    state = previous_state
            print(
                f"[{label}] write failed ({exc}){rollback_note}; "
                f"backup at {backup_path}",
                file=sys.stderr,
            )
            had_error = 1
            continue
        print(f"[{label}] {action}; {backup_note}")

    return had_error


def cmd_use_local(args: argparse.Namespace) -> int:
    """Run :func:`_cmd_use_local` under the shared mutation lock."""
    if args.dry_run:
        return _cmd_use_local(args)
    try:
        with _state_lock():
            return _cmd_use_local(args)
    except SwapStateError:
        return 1
    except OSError as exc:
        print(f"swap state unavailable: {exc}", file=sys.stderr)
        return 1


def _revalidate(info: CLIInfo) -> None:
    """Re-parse the file after writing; raise on failure."""
    load_config(info)


def _cmd_revert(args: argparse.Namespace) -> int:
    """Restore each target CLI's config from the backup recorded in the state file.

    Without ``--scope``, every recorded entry for the targeted CLIs is
    reverted (so a Claude install that has both user-scope and
    project-scope swaps gets both restored). With ``--scope``, only
    the matching scope is reverted; the parameter is silently coerced
    to ``"user"`` for non-Claude CLIs.
    """
    state = load_state(strict=True)
    # Without --cli, revert every CLI that has any recorded swap.
    targets = list(args.cli) if args.cli else list({cli for cli, _scope in state})
    if not targets:
        print("no recorded swaps — nothing to revert", file=sys.stderr)
        return 1

    had_error = 0
    for cli in targets:
        if args.scope is not None:
            wanted_scopes: tuple[Scope, ...] = (_normalize_scope(cli, args.scope),)
        else:
            wanted_scopes = ALL_SCOPES
        cli_keys = [
            (sc_cli, sc_scope)
            for (sc_cli, sc_scope) in state
            if sc_cli == cli and sc_scope in wanted_scopes
        ]
        if not cli_keys:
            label = f"{cli}:{args.scope}" if args.scope and cli == "claude" else cli
            print(f"[{label}] no state entry — skip")
            continue
        # LIFO by seq_no, not dict/parse order. When two scopes back the same
        # file, the later swap's backup contains the earlier one's edits, so
        # each layer must be restored before the one under it.
        cli_keys.sort(key=lambda k: state[k].seq_no, reverse=True)
        for key in cli_keys:
            sc_cli, sc_scope = key
            entry = state[key]
            label = f"{sc_cli}:{sc_scope}" if sc_cli == "claude" else sc_cli
            backup = pathlib.Path(entry.backup_path)
            dest = pathlib.Path(entry.target_path or entry.config_path)
            if not backup.exists():
                print(f"[{label}] backup missing: {backup}", file=sys.stderr)
                had_error = 1
                break
            if args.dry_run:
                print(f"[{label}] would restore {dest} from {backup}")
                continue
            try:
                atomic_write(dest, backup.read_bytes())
            except OSError as exc:
                print(f"[{label}] restore failed: {exc}", file=sys.stderr)
                had_error = 1
                break
            next_state = dict(state)
            next_state.pop(key)
            try:
                _save_or_clear_state(next_state)
            except OSError as exc:
                print(
                    f"[{label}] restored, but recovery state could not be updated: "
                    f"{exc}",
                    file=sys.stderr,
                )
                had_error = 1
                break
            state = next_state
            try:
                backup.unlink()
            except OSError as exc:
                print(
                    f"[{label}] restored; backup cleanup failed: {exc}", file=sys.stderr
                )
                had_error = 1
            print(f"[{label}] restored from {backup}")
    return had_error


def cmd_revert(args: argparse.Namespace) -> int:
    """Run :func:`_cmd_revert` under the shared mutation lock."""
    if args.dry_run:
        return _cmd_revert(args)
    try:
        with _state_lock():
            return _cmd_revert(args)
    except SwapStateError:
        return 1
    except OSError as exc:
        print(f"swap state unavailable: {exc}", file=sys.stderr)
        return 1


# ---------------------------------------------------------------------------
# doctor — read-only diagnostics
# ---------------------------------------------------------------------------

#: Env vars that, when set, override a CLI's stored subscription/login auth
#: with an API key — a frequent cause of "why is it billing / refusing?"
#: surprises when driving the CLI against a local server. Doctor only reports
#: presence; it never reads the value.
AUTH_ENV_VARS: dict[str, CLIName] = {
    "ANTHROPIC_API_KEY": "claude",
    "OPENAI_API_KEY": "codex",
    "GEMINI_API_KEY": "gemini",
    "GOOGLE_API_KEY": "gemini",
    "XAI_API_KEY": "grok",
    "GROK_API_KEY": "grok",
}


def _env_pair(raw: str) -> tuple[str, str]:
    """Parse a ``KEY=VALUE`` ``--env`` argument, or raise for argparse."""
    key, sep, value = raw.partition("=")
    if not sep or not key:
        msg = f"--env expects KEY=VALUE, got {raw!r}"
        raise argparse.ArgumentTypeError(msg)
    return key, value


def _config_present_clis() -> list[CLIName]:
    """CLIs whose config file exists — enough to *read* entries (no binary needed).

    Distinct from :func:`present_clis`, which also requires the binary on
    ``PATH``. Doctor and the naming hint only inspect config files, so a CLI
    whose binary is absent but whose config is present still has readable
    entries worth surfacing.
    """
    return [cli for cli in ALL_CLIS if CLIS[cli].config_path.exists()]


def _all_server_specs(
    cli: CLIName, config: t.Any, repo: pathlib.Path
) -> dict[str, McpServerSpec]:
    """Enumerate every MCP server entry visible in a CLI's config.

    Spans the scopes a CLI actually keys servers under: Claude's top-level
    user ``mcpServers`` plus this repo's per-project node, and the single
    ``mcpServers`` / ``mcp_servers`` table for the others. Used to detect the
    server-name footgun — the repo registered under a name other than the
    derived default — which a same-name-only lookup misses.
    """
    out: dict[str, McpServerSpec] = {}

    def _add(raw: t.Any) -> None:
        if not isinstance(raw, dict):
            return
        for name, entry in raw.items():
            if not isinstance(entry, dict):
                continue
            out[str(name)] = _spec_from_entry(entry, info=CLIS[cli])

    if cli == "claude":
        _add(_claude_user_servers(config, create=False))
        node = _claude_project_node(config, repo, create=False)
        if node:
            _add(node.get("mcpServers"))
    else:
        _add(_server_map(CLIS[cli], config, create=False))
    return out


def _repo_pointing_names(cli: CLIName, config: t.Any, repo: pathlib.Path) -> list[str]:
    """Server names in this CLI's config whose local checkout is ``repo``."""
    return sorted(
        name
        for name, spec in _all_server_specs(cli, config, repo).items()
        if (local := spec.local_repo_path()) is not None and local == repo
    )


def _naming_hint(repo: pathlib.Path, server: str) -> str | None:
    """Suggest ``--server <name>`` when the repo is registered under another name.

    The derived default (package name minus ``-mcp``) often doesn't match the
    slug the CLIs were actually registered under (e.g. ``tmux`` vs the derived
    ``libtmux``), so a bare run silently operates on a non-existent entry.
    Returns a one-line hint naming the real slug, or ``None`` when the derived
    name is already the registered one (or nothing points here).
    """
    names: set[str] = set()
    server_points = False
    for cli in _config_present_clis():
        try:
            config = load_config(CLIS[cli])
            pointing = _repo_pointing_names(cli, config, repo)
        except (RuntimeError, ValueError, OSError):
            continue
        for name in pointing:
            if name == server:
                server_points = True
            else:
                names.add(name)
    if server_points or not names:
        return None
    pick = min(names)
    return (
        f"note: nothing is registered under server {server!r}, but this repo is "
        f"registered as {sorted(names)} — pass --server {pick} to target it"
    )


def _orphaned_backups(config_path: pathlib.Path) -> list[pathlib.Path]:
    """All ``mcp-swap`` backups sitting next to ``config_path`` (any timestamp)."""
    pattern = config_path.name + BACKUP_SUFFIX_PREFIX + "*"
    return sorted(config_path.parent.glob(pattern))


def cmd_doctor(args: argparse.Namespace) -> int:
    """Report the effective MCP-swap environment without changing anything.

    Read-only. Surfaces the footguns that swap/status don't: the repo
    registered under an unexpected server name, un-reverted swaps and orphaned
    backups accumulating on disk, a state entry whose backup has gone missing
    (so revert would fail), and auth-overriding env vars. It deliberately does
    NOT model each CLI's config-merge behaviour — that is CLI-version-specific
    and lives in documentation, not here.
    """
    repo = pathlib.Path(args.repo).resolve()
    server = args.server or build.resolve_repo_meta(repo)[0]
    print("mcp-swap doctor")
    print(f"  repo:   {repo}")
    print(f"  server: {server}  (derived default; override with --server)")

    print("  entries by CLI:")
    all_repo_names: set[str] = set()
    for cli in _config_present_clis():
        try:
            config = load_config(CLIS[cli])
            specs = _all_server_specs(cli, config, repo)
            pointing = _repo_pointing_names(cli, config, repo)
        except (RuntimeError, ValueError, OSError) as exc:
            print(f"    [{cli}] config unreadable: {exc}")
            continue
        spec = specs.get(server)
        if spec is not None:
            print(f"    [{cli}] {server} = {_describe_spec(spec, repo)}")
        all_repo_names.update(pointing)
        for name in pointing:
            if name != server:
                print(f"    [{cli}] {name} = local: this repo  (other name)")
    if not all_repo_names:
        print("    (no CLI currently points at this repo)")

    if all_repo_names and server not in all_repo_names:
        pick = min(all_repo_names)
        print(
            f"  ! server name mismatch: this repo is registered as "
            f"{sorted(all_repo_names)}, not {server!r} — use --server {pick}"
        )

    state = load_state()
    if state:
        print("  outstanding swaps (un-reverted):")
        for (cli, scope), entry in sorted(state.items(), key=lambda kv: kv[1].seq_no):
            flag = (
                ""
                if pathlib.Path(entry.backup_path).exists()
                else "  ! BACKUP MISSING — revert would fail for this entry"
            )
            print(f"    {cli}:{scope}  swapped_at={entry.swapped_at}{flag}")

    referenced = {e.backup_path for e in state.values()}
    orphans = [
        b
        for info in CLIS.values()
        for b in _orphaned_backups(info.config_path)
        if str(b) not in referenced
    ]
    if orphans:
        total = sum(b.stat().st_size for b in orphans if b.exists())
        print(
            f"  orphaned backups: {len(orphans)} file(s), {total} bytes not tracked "
            "by state — inspect before deleting: an untracked backup can be the "
            "only surviving pre-swap copy of a config"
        )

    auth_hits = [
        (var, cli) for var, cli in AUTH_ENV_VARS.items() if os.environ.get(var)
    ]
    if auth_hits:
        print("  auth-overriding env vars set:")
        for var, cli in auth_hits:
            print(
                f"    ! {var} overrides {cli}'s stored login — prefix with "
                f"`env -u {var}` to use the subscription/OAuth auth instead"
            )
    return 0


# ---------------------------------------------------------------------------
# argparse glue
# ---------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    """Construct the ``argparse`` parser for ``mcp_swap``."""
    p = argparse.ArgumentParser(prog="mcp_swap", description=__doc__.splitlines()[0])
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser(
        "detect", help="list installed CLIs and their config presence"
    ).set_defaults(func=cmd_detect)

    ps = sub.add_parser("status", help="show the current MCP server entry per CLI")
    ps.add_argument("--repo", default=".", help="repo root (default: .)")
    ps.add_argument(
        "--server", help=f"MCP server name (default: {build.DEFAULT_SERVER})"
    )
    ps.add_argument(
        "--cli", action="append", choices=ALL_CLIS, help="limit to one or more CLIs"
    )
    ps.add_argument(
        "--scope",
        choices=ALL_SCOPES,
        default=None,
        help=(
            "Limit Claude output to one scope: 'user' shows only the "
            "top-level mcpServers fallback, 'project' shows only the "
            "projects.<abs>.mcpServers entry. Without this flag, both "
            "Claude scopes print when both have an entry. No-op for "
            "non-Claude CLIs (their config has no per-project layer)."
        ),
    )
    ps.set_defaults(func=cmd_status)

    pu = sub.add_parser(
        "use",
        help="rewrite configs to run a chosen build of this server",
    )
    pu.add_argument("--repo", default=".", help="repo root (default: .)")
    pu.add_argument(
        "--source",
        choices=build.ALL_SOURCES,
        default="debug",
        help=(
            "Which build to point the agents at. 'debug' and 'release' build "
            "the project first and name the apphost in bin/<Configuration>, so "
            "an agent spawns it directly. 'run' launches through 'dotnet run', "
            "rebuilding on every start -- current source with nothing to "
            "remember, at the cost of a build check per launch. 'published' "
            "installs a NuGet release under its own tool path. 'path' takes a "
            "binary you name with --bin. Default: debug."
        ),
    )
    pu.add_argument(
        "--version",
        help="NuGet version for --source published (e.g. 0.0.0-alpha.6)",
    )
    pu.add_argument("--bin", help="binary to run for --source path")
    pu.add_argument(
        "--project",
        default=build.DEFAULT_PROJECT,
        help=f"project providing the server (default: {build.DEFAULT_PROJECT})",
    )
    pu.add_argument(
        "--no-build",
        action="store_true",
        help=(
            "Skip the build that --source debug/release runs first. "
            "Use when the binary is already current and you want the swap "
            "to be instant."
        ),
    )
    pu.add_argument(
        "--no-preflight",
        action="store_true",
        help=(
            "Skip the MCP initialize round trip run before writing. The "
            "probe starts the server once, so a binary that cannot speak "
            "the protocol fails here instead of inside every agent."
        ),
    )
    pu.add_argument(
        "--server", help=f"MCP server name (default: {build.DEFAULT_SERVER})"
    )
    pu.add_argument(
        "--entry", help="binary name (default: the project's AssemblyName)"
    )
    pu.add_argument(
        "--env",
        action="append",
        type=_env_pair,
        metavar="KEY=VALUE",
        help=(
            "Extra env var to write into the server entry (repeatable). "
            "Layered on top of any preserved existing env; explicit --env wins. "
            "Use to inject e.g. TMUX_MCP_SAFETY without a manual post-edit."
        ),
    )
    pu.add_argument("--cli", action="append", choices=ALL_CLIS)
    pu.add_argument(
        "--scope",
        choices=ALL_SCOPES,
        default=None,
        help=(
            "Claude config scope: 'user' rewrites the top-level mcpServers "
            "fallback (every project without an override picks it up), "
            "'project' rewrites projects.<abs>.mcpServers under this repo. "
            "Default 'project'. Silently coerced to 'user' for non-Claude CLIs."
        ),
    )
    pu.add_argument("--dry-run", action="store_true")
    pu.set_defaults(func=cmd_use_local)

    pr = sub.add_parser("revert", help="restore each CLI's config from its swap backup")
    pr.add_argument("--cli", action="append", choices=ALL_CLIS)
    pr.add_argument(
        "--scope",
        choices=ALL_SCOPES,
        default=None,
        help=(
            "Limit revert to one Claude scope. Without this flag, every "
            "recorded scope for the targeted CLIs is reverted."
        ),
    )
    pr.add_argument("--dry-run", action="store_true")
    pr.set_defaults(func=cmd_revert)

    pd = sub.add_parser(
        "doctor", help="report the effective MCP-swap environment (read-only)"
    )
    pd.add_argument("--repo", default=".", help="repo root (default: .)")
    pd.add_argument(
        "--server", help=f"MCP server name (default: {build.DEFAULT_SERVER})"
    )
    pd.set_defaults(func=cmd_doctor)

    return p


def main(argv: list[str] | None = None) -> int:
    """Entry point — dispatches to the selected subcommand."""
    args = build_parser().parse_args(argv)
    return t.cast("int", args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
