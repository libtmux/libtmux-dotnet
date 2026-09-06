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
This script is transactional and intentionally narrow. A multi-client
``use`` or ``revert`` either completes for every selected client or restores
the exact pre-command files; recovery data is authenticated and changes to
owned files make the operation fail closed.

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
import hashlib
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

    ``load_state`` uses this tolerant parser for read-only diagnostics. The
    strict mutation path separately requires the version, root checksum,
    entry checksum, and complete recovery identities.
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
STATE_VERSION = 1
STATE_MAX_BYTES = 1 << 20

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


@dataclasses.dataclass(frozen=True)
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
    #: identity. Strict mutation rejects older entries that omit it.
    target_path: str | None = None
    #: Authenticated identity of the config after the latest transaction
    #: touching its physical target. Every stacked Claude entry is updated
    #: together because both scopes share one file.
    config_identity: dict[str, t.Any] | None = None
    #: Authenticated identity and location of the pristine backup.
    backup_identity: dict[str, t.Any] | None = None
    #: Exact server route this layer installed, including inherited env.
    route: dict[str, t.Any] | None = None
    #: SHA-256 of the canonical entry document without this field.
    checksum: str | None = None


class SwapStateError(RuntimeError):
    """Swap state is unsafe to use for a mutating operation."""


class TransactionFailure(RuntimeError):
    """A planned swap could not commit or roll back exactly."""


class FileState(t.NamedTuple):
    """Stable identity, mode, metadata and bytes for one regular file."""

    device: int
    inode: int
    mode: int
    size: int
    modified_ns: int
    data: bytes


OwnedFiles = dict[pathlib.Path, FileState]


class DirectoryState(t.NamedTuple):
    """Logical and physical identity for a transaction directory."""

    logical: pathlib.Path
    physical: pathlib.Path
    symlink: bool
    link_text: str | None
    link_device: int
    link_inode: int
    link_mode: int
    device: int
    inode: int
    mode: int


class LockState(t.NamedTuple):
    """Authenticated path and open descriptor for the persistent swap lock."""

    logical: pathlib.Path
    physical: pathlib.Path
    parent: DirectoryState | None
    device: int | None
    inode: int | None
    mode: int | None
    links: int | None
    descriptor: int | None


class Target(t.NamedTuple):
    """One selected CLI config layer."""

    cli: CLIName
    scope: Scope
    info: CLIInfo

    @property
    def label(self) -> str:
        return f"{self.cli}:{self.scope}" if self.cli == "claude" else self.cli


class ConfigState(t.NamedTuple):
    """Authenticated logical link and physical config state."""

    target: Target
    parent: DirectoryState
    symlink: bool
    link_text: str | None
    link_device: int
    link_inode: int
    link_mode: int
    physical: pathlib.Path
    file: FileState


class ArtifactState(t.NamedTuple):
    """Authenticated backup or global state artifact."""

    path: pathlib.Path
    parent: DirectoryState | None
    physical: pathlib.Path
    file: FileState | None


class PreparedUse(t.NamedTuple):
    config: ConfigState
    backup: ArtifactState | None
    output: bytes
    action: t.Literal["replaced", "added"] | None
    spec: McpServerSpec
    prior: SwapEntry | None
    swapped_at: str
    seq_no: int

    @property
    def changed(self) -> bool:
        return self.action is not None


class PreparedRevert(t.NamedTuple):
    config: ConfigState
    entries: tuple[tuple[tuple[CLIName, Scope], SwapEntry, ArtifactState], ...]
    output: bytes
    output_mode: int


class StagedUse(t.NamedTuple):
    plan: PreparedUse
    output: pathlib.Path
    recovery: pathlib.Path
    backup: pathlib.Path | None


class StagedRevert(t.NamedTuple):
    plan: PreparedRevert
    output: pathlib.Path
    recovery: pathlib.Path
    backup_recoveries: tuple[pathlib.Path, ...]


class ConfigWrite(t.NamedTuple):
    config: ConfigState
    committed: FileState
    recovery: pathlib.Path


class BackupWrite(t.NamedTuple):
    backup: ArtifactState
    committed: FileState


class BackupRemoval(t.NamedTuple):
    backup: ArtifactState
    recovery: pathlib.Path


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


def _canonical_json(value: t.Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _entry_document(entry: SwapEntry, *, checksum: bool) -> dict[str, t.Any]:
    document = dataclasses.asdict(entry)
    if not checksum:
        document.pop("checksum", None)
    return document


def _with_entry_checksum(entry: SwapEntry) -> SwapEntry:
    digest = hashlib.sha256(_canonical_json(_entry_document(entry, checksum=False)))
    return dataclasses.replace(entry, checksum=digest.hexdigest())


def _state_document(
    entries: dict[tuple[CLIName, Scope], SwapEntry], *, include_checksum: bool = True
) -> dict[str, t.Any]:
    normalized = {
        _state_key(cli, scope): _entry_document(
            _with_entry_checksum(entry), checksum=True
        )
        for (cli, scope), entry in sorted(entries.items())
    }
    core: dict[str, t.Any] = {"version": STATE_VERSION, "entries": normalized}
    if include_checksum:
        core["checksum"] = hashlib.sha256(_canonical_json(core)).hexdigest()
    return core


def _state_bytes(entries: dict[tuple[CLIName, Scope], SwapEntry]) -> bytes:
    data = (json.dumps(_state_document(entries), indent=2) + "\n").encode("utf-8")
    if len(data) > STATE_MAX_BYTES:
        raise SwapStateError(f"swap state exceeds {STATE_MAX_BYTES} bytes")
    return data


def _load_state_bytes(
    data: bytes, *, strict: bool
) -> dict[tuple[CLIName, Scope], SwapEntry]:
    try:
        if len(data) > STATE_MAX_BYTES:
            raise ValueError(f"exceeds {STATE_MAX_BYTES} bytes")
        raw = json.loads(data)
        if not isinstance(raw, dict):
            raise TypeError("has invalid shape")
        versioned = "version" in raw or "checksum" in raw
        if versioned:
            if set(raw) != {"version", "entries", "checksum"}:
                raise ValueError("has unknown or missing fields")
            if type(raw["version"]) is not int or raw["version"] != STATE_VERSION:
                raise ValueError("has unsupported version")
            expected = hashlib.sha256(
                _canonical_json({"version": raw["version"], "entries": raw["entries"]})
            ).hexdigest()
            if not isinstance(raw["checksum"], str) or raw["checksum"] != expected:
                raise ValueError("checksum mismatch")
        elif strict:
            raise ValueError("uses unauthenticated legacy schema")
        entries = raw.get("entries", {})
        if not isinstance(entries, dict):
            raise TypeError("has invalid entries")
        out: dict[tuple[CLIName, Scope], SwapEntry] = {}
        for raw_key, raw_entry in entries.items():
            key = _parse_state_key(raw_key)
            if key is None or not isinstance(raw_entry, dict):
                if strict or versioned:
                    raise ValueError(f"has invalid entry {raw_key!r}")
                continue
            entry = _parse_state_entry(raw_entry)
            if entry is None:
                if strict or versioned:
                    raise ValueError(f"has invalid entry {raw_key!r}")
                continue
            if versioned:
                if entry.checksum is None:
                    raise ValueError(f"entry {raw_key!r} has no checksum")
                expected_entry = _with_entry_checksum(
                    dataclasses.replace(entry, checksum=None)
                ).checksum
                if entry.checksum != expected_entry:
                    raise ValueError(f"entry {raw_key!r} checksum mismatch")
                if strict and (
                    entry.config_identity is None
                    or entry.backup_identity is None
                    or entry.route is None
                    or entry.target_path is None
                ):
                    raise ValueError(f"entry {raw_key!r} lacks recovery identity")
            out[key] = entry
        return out
    except (TypeError, UnicodeDecodeError, ValueError) as exc:
        message = f"swap state unreadable ({STATE_FILE}): {exc}"
        print(message, file=sys.stderr)
        if strict:
            raise SwapStateError(message) from exc
        return {}


def load_state(*, strict: bool = False) -> dict[tuple[CLIName, Scope], SwapEntry]:
    """Load checksummed recovery state; mutating callers reject legacy data."""
    if not os.path.lexists(STATE_FILE):
        return {}
    try:
        if STATE_FILE.is_symlink() or not STATE_FILE.is_file():
            raise ValueError("is not a regular file")
        return _load_state_bytes(STATE_FILE.read_bytes(), strict=strict)
    except OSError as exc:
        message = f"swap state unreadable ({STATE_FILE}): {exc}"
        print(message, file=sys.stderr)
        if strict:
            raise SwapStateError(message) from exc
        return {}


def _lock_path() -> pathlib.Path:
    return STATE_DIR / "state.lock"


def _inspect_lock(*, descriptor: int | None = None) -> LockState:
    """Inspect the lock without following either its path or parent."""
    path = _lock_path()
    parent = None
    if os.path.lexists(STATE_DIR):
        parent = _directory_state(STATE_DIR)
        if parent.symlink:
            raise RuntimeError(f"swap lock directory is a symlink: {STATE_DIR}")
        physical = parent.physical / path.name
    else:
        physical = path.resolve(strict=False)
    if not os.path.lexists(path):
        return LockState(path, physical, parent, None, None, None, None, descriptor)
    before = path.lstat()
    if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
        raise RuntimeError(f"swap lock is not a regular file: {path}")
    resolved = path.resolve(strict=True)
    after = path.lstat()
    before_key = (before.st_dev, before.st_ino, before.st_mode, before.st_nlink)
    after_key = (after.st_dev, after.st_ino, after.st_mode, after.st_nlink)
    if before_key != after_key or resolved != physical:
        raise RuntimeError(f"swap lock changed while it was inspected: {path}")
    return LockState(
        path,
        physical,
        parent,
        after.st_dev,
        after.st_ino,
        stat.S_IMODE(after.st_mode),
        after.st_nlink,
        descriptor,
    )


def _validate_lock(lock: LockState) -> None:
    """Bind the lock's public path to its exclusive open descriptor."""
    if lock.device is None or lock.inode is None:
        if lock.descriptor is not None:
            raise RuntimeError(f"swap lock path disappeared: {lock.logical}")
        return
    if lock.mode != 0o600:
        raise RuntimeError(f"swap lock mode is not 0600: {lock.logical}")
    if lock.links != 1:
        raise RuntimeError(f"swap lock has hard links: {lock.logical}")
    if lock.descriptor is None:
        return
    current = _inspect_lock(descriptor=lock.descriptor)
    if current != lock:
        raise RuntimeError(f"swap lock path changed: {lock.logical}")
    opened = os.fstat(lock.descriptor)
    if (
        not stat.S_ISREG(opened.st_mode)
        or (opened.st_dev, opened.st_ino) != (lock.device, lock.inode)
        or stat.S_IMODE(opened.st_mode) != lock.mode
        or opened.st_nlink != lock.links
    ):
        raise RuntimeError(f"swap lock descriptor changed: {lock.logical}")


@contextlib.contextmanager
def _state_lock() -> t.Iterator[LockState]:
    """Hold and authenticate the persistent lock for every mutation."""
    STATE_DIR.mkdir(parents=True, exist_ok=True, mode=0o700)
    directory_fd: int | None = None
    lock_fd: int | None = None
    try:
        if not hasattr(os, "O_NOFOLLOW") or not hasattr(os, "O_DIRECTORY"):
            raise RuntimeError(
                "platform cannot open the swap lock without following links"
            )
        directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        directory_flags |= getattr(os, "O_CLOEXEC", 0)
        directory_fd = os.open(STATE_DIR, directory_flags)
        parent = _directory_state(STATE_DIR)
        opened_parent = os.fstat(directory_fd)
        if parent.symlink or (opened_parent.st_dev, opened_parent.st_ino) != (
            parent.device,
            parent.inode,
        ):
            raise RuntimeError(f"swap lock directory changed: {STATE_DIR}")
        lock_flags = os.O_RDWR | os.O_NOFOLLOW | getattr(os, "O_CLOEXEC", 0)
        try:
            lock_fd = os.open(
                _lock_path().name,
                lock_flags | os.O_CREAT | os.O_EXCL,
                0o600,
                dir_fd=directory_fd,
            )
            os.fchmod(lock_fd, 0o600)
        except FileExistsError:
            lock_fd = os.open(_lock_path().name, lock_flags, dir_fd=directory_fd)
        fcntl.flock(lock_fd, fcntl.LOCK_EX)
        lock = _inspect_lock(descriptor=lock_fd)
        _validate_lock(lock)
    except Exception as exc:
        if lock_fd is not None:
            os.close(lock_fd)
        if directory_fd is not None:
            os.close(directory_fd)
        raise TransactionFailure(f"swap lock is unusable: {exc}") from exc
    try:
        yield lock
    finally:
        try:
            _validate_lock(lock)
        finally:
            os.close(t.cast(int, lock_fd))
            os.close(t.cast(int, directory_fd))


def save_state(entries: dict[tuple[CLIName, Scope], SwapEntry]) -> None:
    """Write versioned, checksummed swap state atomically."""
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    atomic_write(STATE_FILE, _state_bytes(entries))


def _file_state(path: pathlib.Path) -> FileState:
    before = path.stat()
    if not stat.S_ISREG(before.st_mode):
        raise ValueError(f"{path} is not a regular file")
    data = path.read_bytes()
    after = path.stat()
    before_key = (
        before.st_dev,
        before.st_ino,
        before.st_mode,
        before.st_size,
        before.st_mtime_ns,
    )
    after_key = (
        after.st_dev,
        after.st_ino,
        after.st_mode,
        after.st_size,
        after.st_mtime_ns,
    )
    if before_key != after_key:
        raise RuntimeError(f"{path} changed while it was read")
    return FileState(
        after.st_dev,
        after.st_ino,
        stat.S_IMODE(after.st_mode),
        after.st_size,
        after.st_mtime_ns,
        data,
    )


def _regular_file_state(path: pathlib.Path) -> FileState:
    details = path.lstat()
    if stat.S_ISLNK(details.st_mode) or not stat.S_ISREG(details.st_mode):
        raise ValueError(f"{path} is not a regular file")
    file = _file_state(path)
    if (details.st_dev, details.st_ino) != (file.device, file.inode):
        raise RuntimeError(f"{path} changed while it was resolved")
    return file


def _own(owned: OwnedFiles, path: pathlib.Path) -> None:
    owned[path] = _regular_file_state(path)


def _release_missing(owned: OwnedFiles, path: pathlib.Path) -> None:
    if not os.path.lexists(path):
        owned.pop(path, None)


def _directory_state(path: pathlib.Path) -> DirectoryState:
    logical = path.lstat()
    symlink = stat.S_ISLNK(logical.st_mode)
    if not symlink and not stat.S_ISDIR(logical.st_mode):
        raise ValueError(f"{path} is not a directory or directory symlink")
    physical = path.resolve(strict=True)
    details = physical.stat()
    if not stat.S_ISDIR(details.st_mode):
        raise ValueError(f"{path} is not a directory")
    return DirectoryState(
        path,
        physical,
        symlink,
        os.readlink(path) if symlink else None,
        logical.st_dev,
        logical.st_ino,
        logical.st_mode,
        details.st_dev,
        details.st_ino,
        stat.S_IMODE(details.st_mode),
    )


def _config_state(target: Target) -> ConfigState:
    path = target.info.config_path
    parent = _directory_state(path.parent)
    details = path.lstat()
    symlink = stat.S_ISLNK(details.st_mode)
    if not symlink and not stat.S_ISREG(details.st_mode):
        raise ValueError(f"{path} is not a regular file or symlink")
    physical = path.resolve(strict=True)
    file = _file_state(physical)
    if not symlink and (details.st_dev, details.st_ino) != (file.device, file.inode):
        raise RuntimeError(f"{path} changed while it was resolved")
    return ConfigState(
        target,
        parent,
        symlink,
        os.readlink(path) if symlink else None,
        details.st_dev,
        details.st_ino,
        details.st_mode,
        physical,
        file,
    )


def _artifact_state(path: pathlib.Path, *, required: bool = False) -> ArtifactState:
    parent = _directory_state(path.parent) if path.parent.exists() else None
    physical = (
        parent.physical / path.name
        if parent is not None
        else path.resolve(strict=False)
    )
    if not os.path.lexists(path):
        if required:
            raise FileNotFoundError(path)
        return ArtifactState(path, parent, physical, None)
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"{path} is not a regular file")
    if path.resolve(strict=True) != physical:
        raise RuntimeError(f"{path} changed while it was resolved")
    file = _file_state(physical)
    return ArtifactState(path, parent, physical, file)


def _file_document(file: FileState) -> dict[str, t.Any]:
    return {
        "device": file.device,
        "inode": file.inode,
        "mode": file.mode,
        "modified_ns": file.modified_ns,
        "sha256": hashlib.sha256(file.data).hexdigest(),
        "size": file.size,
    }


def _directory_document(directory: DirectoryState) -> dict[str, t.Any]:
    return {
        "device": directory.device,
        "inode": directory.inode,
        "link_device": directory.link_device if directory.symlink else None,
        "link_inode": directory.link_inode if directory.symlink else None,
        "link_mode": directory.link_mode if directory.symlink else None,
        "link_text": directory.link_text,
        "logical": str(directory.logical),
        "mode": directory.mode,
        "physical": str(directory.physical),
        "symlink": directory.symlink,
    }


def _config_document(config: ConfigState, file: FileState) -> dict[str, t.Any]:
    return {
        "file": _file_document(file),
        "link_device": config.link_device if config.symlink else None,
        "link_inode": config.link_inode if config.symlink else None,
        "link_mode": config.link_mode if config.symlink else None,
        "link_text": config.link_text,
        "logical": str(config.target.info.config_path),
        "parent": _directory_document(config.parent),
        "symlink": config.symlink,
        "target": str(config.physical),
    }


def _artifact_document(artifact: ArtifactState, file: FileState) -> dict[str, t.Any]:
    if artifact.parent is None:
        raise RuntimeError(f"{artifact.path.parent} does not exist")
    return {
        "file": _file_document(file),
        "parent": _directory_document(artifact.parent),
        "path": str(artifact.path),
        "target": str(artifact.physical),
    }


def _same_typed(left: t.Any, right: t.Any) -> bool:
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return set(left) == set(right) and all(
            _same_typed(left[key], right[key]) for key in left
        )
    if isinstance(left, list):
        return len(left) == len(right) and all(
            _same_typed(one, two) for one, two in zip(left, right, strict=True)
        )
    return bool(left == right)


def _verify_directory(expected: DirectoryState) -> None:
    if _directory_state(expected.logical) != expected:
        raise RuntimeError(f"{expected.logical} changed")


def _verify_config(config: ConfigState, expected: FileState) -> None:
    _verify_directory(config.parent)
    path = config.target.info.config_path
    details = path.lstat()
    if config.symlink:
        if (
            not stat.S_ISLNK(details.st_mode)
            or os.readlink(path) != config.link_text
            or (details.st_dev, details.st_ino, details.st_mode)
            != (config.link_device, config.link_inode, config.link_mode)
        ):
            raise RuntimeError(f"{path} symlink changed")
    elif not stat.S_ISREG(details.st_mode):
        raise RuntimeError(f"{path} topology changed")
    if path.resolve(strict=True) != config.physical:
        raise RuntimeError(f"{path} target changed")
    current = _file_state(config.physical)
    if current != expected:
        raise RuntimeError(f"{path} identity, mode, or bytes changed")
    if not config.symlink and (details.st_dev, details.st_ino) != (
        current.device,
        current.inode,
    ):
        raise RuntimeError(f"{path} logical identity changed")


def _verify_artifact(artifact: ArtifactState, expected: FileState | None) -> None:
    if artifact.parent is None:
        if expected is not None or os.path.lexists(artifact.path):
            raise RuntimeError(f"{artifact.path.parent} changed")
        return
    _verify_directory(artifact.parent)
    if expected is None:
        if os.path.lexists(artifact.path):
            raise RuntimeError(f"{artifact.path} appeared")
        return
    if not os.path.lexists(artifact.path) or artifact.path.is_symlink():
        raise RuntimeError(f"{artifact.path} topology changed")
    if artifact.path.resolve(strict=True) != artifact.physical:
        raise RuntimeError(f"{artifact.path} target changed")
    if _file_state(artifact.physical) != expected:
        raise RuntimeError(f"{artifact.path} identity, mode, or bytes changed")


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


def _spec_document(
    name: str, scope: Scope, repo: pathlib.Path, spec: McpServerSpec
) -> dict[str, t.Any]:
    return {
        "args": list(spec.args),
        "command": spec.command,
        "env": dict(spec.env),
        "name": name,
        "repo": str(repo),
        "scope": scope,
    }


def _spec_from_document(document: dict[str, t.Any]) -> McpServerSpec:
    expected = {"args", "command", "env", "name", "repo", "scope"}
    if set(document) != expected:
        raise ValueError("recovery route has unknown or missing fields")
    args = document["args"]
    env = document["env"]
    if (
        not isinstance(args, list)
        or any(not isinstance(value, str) for value in args)
        or not isinstance(env, dict)
        or any(not isinstance(key, str) for key in env)
        or any(not isinstance(value, str) for value in env.values())
        or type(document["command"]) is not str
        or type(document["name"]) is not str
        or type(document["repo"]) is not str
        or document["scope"] not in ALL_SCOPES
    ):
        raise ValueError("recovery route is invalid")
    return McpServerSpec(command=document["command"], args=list(args), env=dict(env))


def _selected_targets(args: argparse.Namespace) -> list[Target]:
    selected = list(args.cli or present_clis())
    if not selected:
        raise TransactionFailure("no CLIs detected — nothing to do")
    return [
        Target(cli, _normalize_scope(cli, args.scope), CLIS[cli]) for cli in selected
    ]


def _source_spec(
    args: argparse.Namespace,
) -> tuple[pathlib.Path, str, str, str, build.Source, McpServerSpec]:
    repo = pathlib.Path(args.repo).resolve()
    project = getattr(args, "project", None) or build.DEFAULT_PROJECT
    server, default_binary = build.resolve_repo_meta(repo, project)
    server = args.server or server
    binary = args.entry or default_binary
    command = (
        build.project_property(
            build.project_file(repo, project).read_text(), "ToolCommandName"
        )
        or "libtmux-mcp"
    )
    source: build.Source = getattr(args, "source", "debug")
    spec = build.build_source_spec(
        source,
        repo=repo,
        binary=binary,
        project=project,
        command=command,
        version=getattr(args, "version", None),
        binary_path=(pathlib.Path(args.bin) if getattr(args, "bin", None) else None),
    )
    return (
        repo,
        project,
        server,
        binary,
        source,
        dataclasses.replace(spec, env={**spec.env, **dict(args.env or [])}),
    )


def _setup_source(
    args: argparse.Namespace,
    repo: pathlib.Path,
    project: str,
    binary: str,
    source: build.Source,
    spec: McpServerSpec,
) -> None:
    command = (
        build.project_property(
            build.project_file(repo, project).read_text(), "ToolCommandName"
        )
        or "libtmux-mcp"
    )
    if source in ("debug", "release") and not getattr(args, "no_build", False):
        build.dotnet_build(repo, source.capitalize(), project)
    if source == "published":
        build.install_published("LibTmux.Mcp", args.version, binary, command)
    launcher = pathlib.Path(spec.command)
    if not launcher.is_file():
        raise TransactionFailure(f"{source}: {launcher} does not exist")
    if not args.no_preflight:
        print(f"preflight: {spec.command} {' '.join(spec.args)}", file=sys.stderr)
        failure = build.preflight_spec(spec)
        if failure is not None:
            raise TransactionFailure(f"preflight failed, nothing written:\n{failure}")


def _available_backup_path(target: Target, timestamp: str) -> pathlib.Path:
    suffix = f"{BACKUP_SUFFIX_PREFIX}{timestamp}"
    if target.cli == "claude":
        suffix += f"-{target.scope}"
    base = target.info.config_path.with_suffix(target.info.config_path.suffix + suffix)
    candidate = base
    attempt = 0
    while os.path.lexists(candidate):
        attempt += 1
        candidate = base.with_name(f"{base.name}-{attempt}")
    return candidate


def _parse_config(config: ConfigState) -> t.Any:
    info = dataclasses.replace(config.target.info, config_path=config.physical)
    return load_config(info)


def _verify_entry_ownership(
    key: tuple[CLIName, Scope],
    entry: SwapEntry,
    config: ConfigState,
    document: t.Any,
) -> ArtifactState:
    if (
        entry.config_identity is None
        or entry.backup_identity is None
        or entry.route is None
        or entry.target_path is None
    ):
        raise SwapStateError(f"{_state_key(*key)} lacks recovery identity")
    if entry.config_path != str(config.target.info.config_path):
        raise SwapStateError(f"{_state_key(*key)} names another logical config")
    if entry.target_path != str(config.physical):
        raise SwapStateError(f"{_state_key(*key)} names another physical config")
    if not _same_typed(entry.config_identity, _config_document(config, config.file)):
        raise SwapStateError(f"{_state_key(*key)} config identity changed")
    backup = _artifact_state(pathlib.Path(entry.backup_path), required=True)
    backup_file = t.cast(FileState, backup.file)
    if not _same_typed(entry.backup_identity, _artifact_document(backup, backup_file)):
        raise SwapStateError(f"{_state_key(*key)} backup identity changed")
    route = entry.route
    spec = _spec_from_document(route)
    if route["scope"] != key[1]:
        raise SwapStateError(f"{_state_key(*key)} scope changed")
    actual = get_server(
        key[0],
        document,
        route["name"],
        pathlib.Path(route["repo"]),
        scope=key[1],
    )
    if actual != spec:
        raise SwapStateError(f"{_state_key(*key)} server route changed")
    return backup


def _plan_use(
    args: argparse.Namespace,
    repo: pathlib.Path,
    server: str,
    spec: McpServerSpec,
    timestamp: str,
    state: dict[tuple[CLIName, Scope], SwapEntry],
    state_file: ArtifactState,
) -> list[PreparedUse]:
    prepared: list[PreparedUse] = []
    errors: list[str] = []
    next_seq = max((entry.seq_no for entry in state.values()), default=-1) + 1
    for target in _selected_targets(args):
        if not os.path.lexists(target.info.config_path):
            errors.append(
                f"[{target.label}] config not found at {target.info.config_path}"
            )
            continue
        try:
            config = _config_state(target)
            document = _parse_config(config)
            relevant = {
                key: entry
                for key, entry in state.items()
                if entry.target_path == str(config.physical)
            }
            recovered: dict[tuple[CLIName, Scope], ArtifactState] = {}
            for key, entry in relevant.items():
                recovered[key] = _verify_entry_ownership(key, entry, config, document)
            current = get_server(target.cli, document, server, repo, scope=target.scope)
            base_env = dict(current.env) if current else {}
            base_env.update(spec.env)
            base_env.update(dict(args.env or []))
            cli_spec = dataclasses.replace(spec, env=base_env)
            prior = state.get((target.cli, target.scope))
            if prior is not None and (target.cli, target.scope) not in recovered:
                raise SwapStateError(
                    f"{target.label} recovery names another config target"
                )
            if current is not None and _points_at(current, cli_spec, repo):
                prepared.append(
                    PreparedUse(
                        config,
                        recovered.get((target.cli, target.scope)),
                        config.file.data,
                        None,
                        cli_spec,
                        prior,
                        prior.swapped_at if prior else timestamp,
                        prior.seq_no if prior else next_seq,
                    )
                )
                continue
            action = set_server(
                target.cli,
                document,
                server,
                cli_spec,
                repo,
                scope=target.scope,
            )
            output = dump_config_bytes(target.info, document, original=config.file.data)
            if prior is None:
                backup = _artifact_state(_available_backup_path(target, timestamp))
                seq_no = next_seq
                next_seq += 1
                swapped_at = timestamp
            else:
                backup = recovered[(target.cli, target.scope)]
                seq_no = prior.seq_no
                swapped_at = prior.swapped_at
            prepared.append(
                PreparedUse(
                    config,
                    backup,
                    output,
                    action,
                    cli_spec,
                    prior,
                    swapped_at,
                    seq_no,
                )
            )
        except Exception as exc:  # noqa: BLE001 - finish planning every target
            errors.append(f"[{target.label}] {exc}")
    if errors:
        raise TransactionFailure("; ".join(errors))
    _reject_transaction_aliases(prepared, state_file)
    return prepared


def _reject_transaction_aliases(
    plans: t.Iterable[PreparedUse | PreparedRevert],
    state_file: ArtifactState,
    lock: LockState | None = None,
) -> None:
    config_paths: dict[pathlib.Path, str] = {}
    config_inodes: dict[tuple[int, int], str] = {}
    paths: dict[pathlib.Path, str] = {}
    inodes: dict[tuple[int, int], str] = {}

    def claim(
        logical: pathlib.Path,
        physical: pathlib.Path,
        file: FileState | None,
        owner: str,
        *,
        config: bool = False,
    ) -> None:
        inode = None if file is None else (file.device, file.inode)
        if config:
            duplicate = config_paths.get(physical)
            if duplicate is None and inode is not None:
                duplicate = config_inodes.get(inode)
            if duplicate is not None:
                raise TransactionFailure(
                    f"duplicate physical config target for {duplicate} and {owner}"
                )
            config_paths[physical] = owner
            if inode is not None:
                config_inodes[inode] = owner
        duplicate = next(
            (
                paths[path]
                for path in dict.fromkeys((logical, physical))
                if path in paths and paths[path] != owner
            ),
            None,
        )
        if duplicate is None and inode is not None and inodes.get(inode) != owner:
            duplicate = inodes.get(inode)
        if duplicate is not None:
            raise TransactionFailure(
                f"duplicate transaction destination for {duplicate} and {owner}"
            )
        paths[logical] = owner
        paths[physical] = owner
        if inode is not None:
            inodes[inode] = owner

    plan_list = list(plans)
    if lock is not None:
        lock_file = (
            None
            if lock.device is None or lock.inode is None
            else FileState(lock.device, lock.inode, t.cast(int, lock.mode), 0, 0, b"")
        )
        claim(lock.logical, lock.physical, lock_file, "swap lock")
    for plan in plan_list:
        claim(
            plan.config.target.info.config_path,
            plan.config.physical,
            plan.config.file,
            f"{plan.config.target.label} config",
            config=True,
        )
    for plan in plan_list:
        artifacts = (
            (() if plan.backup is None else (plan.backup,))
            if isinstance(plan, PreparedUse)
            else tuple(item[2] for item in plan.entries)
        )
        for artifact in artifacts:
            claim(
                artifact.path,
                artifact.physical,
                artifact.file,
                f"{plan.config.target.label} backup",
            )
    claim(
        state_file.path,
        state_file.physical,
        state_file.file,
        "swap state",
    )


def _check_lock_plan(
    plans: t.Iterable[PreparedUse | PreparedRevert], state_file: ArtifactState
) -> None:
    lock = _inspect_lock()
    _reject_transaction_aliases(plans, state_file, lock)
    _validate_lock(lock)


def _stage(
    directory: pathlib.Path,
    logical_name: str,
    role: str,
    data: bytes,
    mode: int,
) -> pathlib.Path:
    descriptor, name = tempfile.mkstemp(
        prefix=f".{logical_name}.mcp-swap-{role}-", dir=str(directory)
    )
    temporary = pathlib.Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        return temporary
    except Exception:
        temporary.unlink(missing_ok=True)
        raise


def _apply_replace(
    source: pathlib.Path,
    destination: pathlib.Path,
    *,
    expected: FileState,
    destination_expected: FileState,
    lock: LockState,
) -> tuple[FileState, Exception | None]:
    """Take aside one exact file without clobbering either pathname."""
    _validate_lock(lock)
    if _regular_file_state(source) != expected:
        raise RuntimeError(f"{source} changed before atomic take-aside")
    if _regular_file_state(destination) != destination_expected:
        raise RuntimeError(f"{destination} changed before atomic take-aside")
    delayed = _apply_unlink(
        destination,
        expected=destination_expected,
        lock=lock,
    )
    if delayed is not None:
        raise delayed
    committed, delayed = _publish_absent(
        source,
        destination,
        expected=expected,
        lock=lock,
    )
    try:
        removal_error = _apply_unlink(source, expected=expected, lock=lock)
    except Exception as exc:  # noqa: BLE001 - retain the committed recovery
        removal_error = exc
    if delayed is None:
        delayed = removal_error
    return committed, delayed


def _publish_absent(
    source: pathlib.Path,
    destination: pathlib.Path,
    *,
    expected: FileState,
    lock: LockState,
) -> tuple[FileState, Exception | None]:
    """Publish an owned file only while the destination remains absent."""
    _validate_lock(lock)
    if _regular_file_state(source) != expected:
        raise RuntimeError(f"{source} changed before atomic publication")
    if os.path.lexists(destination):
        raise RuntimeError(f"{destination} appeared before atomic publication")
    _validate_lock(lock)
    delayed: Exception | None = None
    try:
        os.link(source, destination, follow_symlinks=False)
    except Exception as exc:
        try:
            committed = _regular_file_state(destination)
        except (OSError, RuntimeError, ValueError):
            raise exc
        if committed != expected:
            raise RuntimeError(
                f"atomic publication of {destination} was not exact"
            ) from exc
        delayed = exc
    else:
        committed = _regular_file_state(destination)
    if committed != expected:
        raise RuntimeError(f"atomic publication of {destination} was not exact")
    return committed, delayed


def _remove_exact(path: pathlib.Path, expected: FileState) -> Exception | None:
    """Move one public path into private quarantine before deleting it."""
    quarantine_dir = pathlib.Path(
        tempfile.mkdtemp(prefix=f".{path.name}.mcp-swap-retained-", dir=path.parent)
    )
    quarantine_dir.chmod(0o700)
    quarantine = quarantine_dir / "artifact"
    delayed: Exception | None = None
    try:
        path.rename(quarantine)
    except Exception as exc:  # noqa: BLE001 - authenticate a possibly completed move
        try:
            current = _regular_file_state(quarantine)
        except (OSError, RuntimeError, ValueError):
            try:
                quarantine_dir.rmdir()
            except OSError:
                pass
            raise exc
        delayed = exc
    else:
        current = _regular_file_state(quarantine)
    if current != expected:
        raise RuntimeError(f"{path} changed; retained at {quarantine_dir}")
    if os.path.lexists(path):
        delayed = delayed or RuntimeError(f"{path} appeared during removal")
    try:
        quarantine.unlink()
    except Exception as exc:
        if os.path.lexists(quarantine):
            raise RuntimeError(
                f"{path} removal failed; retained at {quarantine_dir}: {exc}"
            ) from exc
        delayed = delayed or exc
    if os.path.lexists(quarantine):
        raise RuntimeError(f"{quarantine} still exists after removal")
    try:
        quarantine_dir.rmdir()
    except Exception as exc:
        if quarantine_dir.exists():
            raise
        delayed = delayed or exc
    return delayed


def _apply_unlink(
    path: pathlib.Path,
    *,
    expected: FileState,
    lock: LockState,
) -> Exception | None:
    _validate_lock(lock)
    if _regular_file_state(path) != expected:
        raise RuntimeError(f"{path} changed before removal")
    _validate_lock(lock)
    delayed = _remove_exact(path, expected)
    try:
        _validate_lock(lock)
    except Exception as exc:  # noqa: BLE001 - preserve post-removal failure
        if delayed is None:
            delayed = exc
    return delayed


def _cleanup_owned(
    owned: OwnedFiles,
    preserve: set[pathlib.Path] | None = None,
    *,
    lock: LockState,
) -> list[str]:
    retained = preserve or set()
    errors: list[str] = []
    for path in sorted(
        (candidate for candidate in owned if candidate not in retained), key=str
    ):
        try:
            if not os.path.lexists(path):
                continue
            _validate_lock(lock)
            delayed = _remove_exact(path, owned[path])
            if delayed is not None:
                raise delayed
            _validate_lock(lock)
        except (OSError, RuntimeError, ValueError) as exc:
            errors.append(f"could not remove task-owned stage {path}: {exc}")
    return errors


def _snapshot_state(
    *, strict: bool
) -> tuple[ArtifactState, dict[tuple[CLIName, Scope], SwapEntry]]:
    artifact = _artifact_state(STATE_FILE)
    if artifact.file is None:
        return artifact, {}
    if artifact.file.mode != 0o600 and strict:
        raise SwapStateError(f"swap state mode is not 0600: {STATE_FILE}")
    return artifact, _load_state_bytes(artifact.file.data, strict=strict)


def _route_for_plan(
    plan: PreparedUse, repo: pathlib.Path, server: str
) -> dict[str, t.Any]:
    return _spec_document(server, plan.config.target.scope, repo, plan.spec)


def _stage_use_transaction(
    plans: list[PreparedUse],
    repo: pathlib.Path,
    server: str,
    state: dict[tuple[CLIName, Scope], SwapEntry],
    state_file: ArtifactState,
    owned: OwnedFiles,
    lock: LockState,
) -> tuple[
    list[StagedUse],
    pathlib.Path,
    pathlib.Path | None,
    dict[tuple[CLIName, Scope], SwapEntry],
]:
    staged: list[StagedUse] = []
    try:
        for plan in plans:
            _validate_lock(lock)
            output = _stage(
                plan.config.physical.parent,
                plan.config.target.info.config_path.name,
                "output",
                plan.output,
                plan.config.file.mode,
            )
            _own(owned, output)
            recovery = _stage(
                plan.config.physical.parent,
                plan.config.target.info.config_path.name,
                "recovery",
                plan.config.file.data,
                plan.config.file.mode,
            )
            _own(owned, recovery)
            backup_stage = None
            backup = t.cast(ArtifactState, plan.backup)
            if backup.file is None:
                if backup.parent is None:
                    raise RuntimeError(f"{backup.path.parent} does not exist")
                backup_stage = _stage(
                    backup.parent.physical,
                    backup.path.name,
                    "backup",
                    plan.config.file.data,
                    plan.config.file.mode,
                )
                _own(owned, backup_stage)
            staged.append(StagedUse(plan, output, recovery, backup_stage))
            _validate_lock(lock)

        next_state = dict(state)
        for item in staged:
            plan = item.plan
            output_file = _file_state(item.output)
            backup = t.cast(ArtifactState, plan.backup)
            backup_file = (
                _file_state(item.backup)
                if item.backup is not None
                else t.cast(FileState, backup.file)
            )
            config_document = _config_document(plan.config, output_file)
            for key, existing in tuple(next_state.items()):
                if existing.target_path == str(plan.config.physical):
                    next_state[key] = dataclasses.replace(
                        existing, config_identity=config_document, checksum=None
                    )
            next_state[(plan.config.target.cli, plan.config.target.scope)] = SwapEntry(
                config_path=str(plan.config.target.info.config_path),
                backup_path=str(backup.path),
                server=server,
                action=t.cast(t.Literal["replaced", "added"], plan.action),
                swapped_at=plan.swapped_at,
                seq_no=plan.seq_no,
                target_path=str(plan.config.physical),
                config_identity=config_document,
                backup_identity=_artifact_document(backup, backup_file),
                route=_route_for_plan(plan, repo, server),
            )

        if state_file.parent is None:
            raise RuntimeError(f"{STATE_FILE.parent} does not exist")
        state_stage = _stage(
            state_file.parent.physical,
            STATE_FILE.name,
            "state",
            _state_bytes(next_state),
            0o600,
        )
        _own(owned, state_stage)
        state_recovery = None
        if state_file.file is not None:
            state_recovery = _stage(
                state_file.parent.physical,
                STATE_FILE.name,
                "recovery-state",
                state_file.file.data,
                state_file.file.mode,
            )
            _own(owned, state_recovery)
        _validate_lock(lock)
        return staged, state_stage, state_recovery, next_state
    except Exception as exc:
        cleanup = _cleanup_owned(owned, lock=lock)
        detail = f"swap staging failed: {exc}"
        if cleanup:
            detail += "; " + "; ".join(cleanup)
        raise TransactionFailure(detail) from exc


def _verify_removed_config(config: ConfigState) -> None:
    _verify_directory(config.parent)
    path = config.target.info.config_path
    if config.symlink:
        details = path.lstat()
        if (
            not stat.S_ISLNK(details.st_mode)
            or os.readlink(path) != config.link_text
            or (details.st_dev, details.st_ino, details.st_mode)
            != (config.link_device, config.link_inode, config.link_mode)
        ):
            raise RuntimeError(f"{path} symlink changed")
    elif os.path.lexists(path):
        raise RuntimeError(f"{path} appeared")
    if os.path.lexists(config.physical):
        raise RuntimeError(f"{config.physical} appeared")


def _restore_config(operation: ConfigWrite, owned: OwnedFiles, lock: LockState) -> None:
    if os.path.lexists(operation.config.physical):
        _verify_config(operation.config, operation.committed)
        delayed = _apply_unlink(
            operation.config.physical,
            expected=operation.committed,
            lock=lock,
        )
        if delayed is not None:
            raise delayed
    else:
        _verify_removed_config(operation.config)
    restored, delayed = _publish_absent(
        operation.recovery,
        operation.config.physical,
        expected=owned[operation.recovery],
        lock=lock,
    )
    _release_missing(owned, operation.recovery)
    if restored != operation.config.file:
        raise RuntimeError(
            f"{operation.config.target.label} config rollback identity changed"
        )
    _verify_config(operation.config, operation.config.file)
    if delayed is not None:
        raise delayed


def _rollback_use(
    config_writes: list[ConfigWrite],
    backup_writes: list[BackupWrite],
    state_file: ArtifactState,
    state_changed: bool,
    state_committed: FileState | None,
    state_recovery: pathlib.Path | None,
    owned: OwnedFiles,
    lock: LockState,
) -> tuple[list[str], set[pathlib.Path]]:
    errors: list[str] = []
    if state_changed:
        try:
            if state_committed is not None:
                current = _artifact_state(STATE_FILE, required=True)
                _verify_artifact(current, state_committed)
                delayed = _apply_unlink(
                    STATE_FILE,
                    expected=state_committed,
                    lock=lock,
                )
                if delayed is not None:
                    raise delayed
            elif os.path.lexists(STATE_FILE):
                raise RuntimeError("unexpected replacement swap state")
            if state_file.file is not None:
                recovery = t.cast(pathlib.Path, state_recovery)
                restored, delayed = _publish_absent(
                    recovery,
                    STATE_FILE,
                    expected=owned[recovery],
                    lock=lock,
                )
                _release_missing(owned, recovery)
                if restored != state_file.file:
                    raise RuntimeError("swap state rollback identity changed")
                if delayed is not None:
                    raise delayed
        except Exception as exc:  # noqa: BLE001 - continue backup rollback
            errors.append(f"swap state: {exc}")
    for backup_write in reversed(backup_writes):
        try:
            _verify_artifact(backup_write.backup, backup_write.committed)
            delayed = _apply_unlink(
                backup_write.backup.physical,
                expected=backup_write.committed,
                lock=lock,
            )
            if delayed is not None:
                raise delayed
        except Exception as exc:  # noqa: BLE001 - report every recovery failure
            errors.append(f"backup {backup_write.backup.path}: {exc}")
    for operation in reversed(config_writes):
        try:
            _restore_config(operation, owned, lock)
        except Exception as exc:  # noqa: BLE001 - preserve all recovery evidence
            errors.append(f"{operation.config.target.label} config: {exc}")
    if not errors:
        return [], set()
    preserved = set(owned)
    preserved.update(operation.backup.path for operation in backup_writes)
    preserved.add(STATE_FILE)
    return errors, preserved


def _commit_use_transaction(
    staged: list[StagedUse],
    state_stage: pathlib.Path,
    state_recovery: pathlib.Path | None,
    state_file: ArtifactState,
    next_state: dict[tuple[CLIName, Scope], SwapEntry],
    repo: pathlib.Path,
    server: str,
    owned: OwnedFiles,
    lock: LockState,
) -> None:
    config_writes: list[ConfigWrite] = []
    backup_writes: list[BackupWrite] = []
    state_changed = False
    state_committed: FileState | None = None
    try:
        for item in staged:
            _verify_config(item.plan.config, item.plan.config.file)
            backup = t.cast(ArtifactState, item.plan.backup)
            _verify_artifact(backup, backup.file)
        _verify_artifact(state_file, state_file.file)
        for item in staged:
            plan = item.plan
            removed, delayed = _apply_replace(
                plan.config.physical,
                item.recovery,
                expected=plan.config.file,
                destination_expected=owned[item.recovery],
                lock=lock,
            )
            if removed == plan.config.file:
                owned[item.recovery] = removed
            config_writes.append(ConfigWrite(plan.config, removed, item.recovery))
            if removed != plan.config.file:
                raise RuntimeError(
                    f"{plan.config.target.label} config identity changed"
                )
            if delayed is not None:
                raise delayed
            _verify_removed_config(plan.config)
            committed, delayed = _publish_absent(
                item.output,
                plan.config.physical,
                expected=owned[item.output],
                lock=lock,
            )
            _release_missing(owned, item.output)
            config_writes[-1] = ConfigWrite(plan.config, committed, item.recovery)
            _verify_config(plan.config, committed)
            current = plan.config._replace(file=committed)  # type: ignore[attr-defined]
            document = _parse_config(current)
            actual = get_server(
                plan.config.target.cli,
                document,
                server,
                repo,
                scope=plan.config.target.scope,
            )
            if actual != plan.spec:
                raise RuntimeError(
                    f"{plan.config.target.label} committed route is not exact"
                )
            if delayed is not None:
                raise delayed
        for item in staged:
            if item.backup is None:
                continue
            backup = t.cast(ArtifactState, item.plan.backup)
            _verify_artifact(backup, None)
            committed, delayed = _publish_absent(
                item.backup,
                backup.physical,
                expected=owned[item.backup],
                lock=lock,
            )
            _release_missing(owned, item.backup)
            backup_writes.append(BackupWrite(backup, committed))
            if delayed is not None:
                raise delayed
        for operation in config_writes:
            _verify_config(operation.config, operation.committed)
        for backup_write in backup_writes:
            _verify_artifact(backup_write.backup, backup_write.committed)
        _verify_artifact(state_file, state_file.file)
        if state_file.file is not None:
            recovery = t.cast(pathlib.Path, state_recovery)
            removed, delayed = _apply_replace(
                STATE_FILE,
                recovery,
                expected=state_file.file,
                destination_expected=owned[recovery],
                lock=lock,
            )
            if removed == state_file.file:
                owned[recovery] = removed
            state_changed = True
            if removed != state_file.file:
                raise RuntimeError("swap state identity changed")
            if delayed is not None:
                raise delayed
        state_committed, delayed = _publish_absent(
            state_stage,
            STATE_FILE,
            expected=owned[state_stage],
            lock=lock,
        )
        state_changed = True
        _release_missing(owned, state_stage)
        if delayed is not None:
            raise delayed
        loaded = _load_state_bytes(state_committed.data, strict=True)
        expected = {
            key: _with_entry_checksum(entry) for key, entry in next_state.items()
        }
        if loaded != expected:
            raise RuntimeError("committed swap state is not exact")
        _verify_artifact(state_file, state_committed)
        for operation in config_writes:
            _verify_config(operation.config, operation.committed)
        for backup_write in backup_writes:
            _verify_artifact(backup_write.backup, backup_write.committed)
    except Exception as exc:
        rollback_errors, preserved = _rollback_use(
            config_writes,
            backup_writes,
            state_file,
            state_changed,
            state_committed,
            state_recovery,
            owned,
            lock,
        )
        cleanup = _cleanup_owned(owned, preserved, lock=lock)
        detail = f"swap failed: {exc}"
        if rollback_errors:
            detail += "; rollback incomplete: " + "; ".join(rollback_errors)
        if cleanup:
            detail += "; cleanup incomplete: " + "; ".join(cleanup)
        if preserved:
            detail += "; recovery artifacts: " + ", ".join(
                str(path) for path in sorted(preserved, key=str)
            )
        raise TransactionFailure(detail) from exc
    cleanup = _cleanup_owned(owned, lock=lock)
    if cleanup:
        raise TransactionFailure("swap cleanup failed: " + "; ".join(cleanup))


def _print_use_preview(plans: list[PreparedUse], repo: pathlib.Path) -> None:
    for plan in plans:
        if not plan.changed:
            print(
                f"[{plan.config.target.label}] already "
                f"{_describe_spec(plan.spec, repo)} — no change"
            )
            continue
        path = plan.config.target.info.config_path
        print(f"--- {path} (current)")
        print(f"+++ {path} (proposed)")
        diff = difflib.unified_diff(
            plan.config.file.data.decode(errors="replace").splitlines(keepends=True),
            plan.output.decode(errors="replace").splitlines(keepends=True),
            lineterm="",
        )
        sys.stdout.writelines(diff)


def cmd_use_local(args: argparse.Namespace) -> int:
    """Plan every selected CLI, then commit one recoverable transaction."""
    try:
        repo, project, server, binary, source, spec = _source_spec(args)
        timestamp = time.strftime("%Y%m%d%H%M%S")
        state_file, state = _snapshot_state(strict=True)
        preview = _plan_use(args, repo, server, spec, timestamp, state, state_file)
        _check_lock_plan(preview, state_file)
        hint = _naming_hint(repo, server)
        if hint:
            print(hint, file=sys.stderr)
        if args.dry_run:
            _print_use_preview(preview, repo)
            return 0
        _setup_source(args, repo, project, binary, source, spec)
        with _state_lock() as lock:
            state_file, state = _snapshot_state(strict=True)
            plans = _plan_use(args, repo, server, spec, timestamp, state, state_file)
            _reject_transaction_aliases(plans, state_file, lock)
            _validate_lock(lock)
            changed = [plan for plan in plans if plan.changed]
            if not changed:
                _print_use_preview(plans, repo)
                return 0
            owned: OwnedFiles = {}
            staged, state_stage, state_recovery, next_state = _stage_use_transaction(
                changed, repo, server, state, state_file, owned, lock
            )
            _commit_use_transaction(
                staged,
                state_stage,
                state_recovery,
                state_file,
                next_state,
                repo,
                server,
                owned,
                lock,
            )
        for plan in changed:
            backup = t.cast(ArtifactState, plan.backup)
            note = "pre-swap backup kept" if plan.prior else "backup"
            print(f"[{plan.config.target.label}] {plan.action}; {note}: {backup.path}")
        return 0
    except (OSError, RuntimeError, subprocess.CalledProcessError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


def _plan_revert(
    args: argparse.Namespace,
    state: dict[tuple[CLIName, Scope], SwapEntry],
    state_file: ArtifactState,
) -> list[PreparedRevert]:
    selected_clis = list(args.cli or sorted({key[0] for key in state}))
    if not selected_clis:
        raise TransactionFailure("no recorded swaps — nothing to revert")
    selected: set[tuple[CLIName, Scope]] = set()
    for cli in selected_clis:
        scopes = (
            ALL_SCOPES if args.scope is None else (_normalize_scope(cli, args.scope),)
        )
        matches = {key for key in state if key[0] == cli and key[1] in scopes}
        if not matches:
            label = f"{cli}:{args.scope}" if cli == "claude" and args.scope else cli
            print(f"[{label}] no state entry — skip")
        selected.update(matches)
    if not selected:
        return []
    groups: dict[str, list[tuple[CLIName, Scope]]] = {}
    for key in sorted(selected, key=lambda item: (state[item].seq_no, item)):
        physical = state[key].target_path
        if physical is None:
            raise SwapStateError(f"{_state_key(*key)} lacks a physical config")
        groups.setdefault(physical, []).append(key)
    prepared: list[PreparedRevert] = []
    errors: list[str] = []
    for target_path, keys in groups.items():
        ordered = sorted(keys, key=lambda key: state[key].seq_no, reverse=True)
        first_key = ordered[0]
        info = CLIS[first_key[0]]
        target = Target(first_key[0], first_key[1], info)
        try:
            config = _config_state(target)
            if str(config.physical) != target_path:
                raise SwapStateError(
                    f"{_state_key(*first_key)} physical config changed"
                )
            document = _parse_config(config)
            all_layers = sorted(
                (
                    key
                    for key, entry in state.items()
                    if entry.target_path == target_path
                ),
                key=lambda key: state[key].seq_no,
                reverse=True,
            )
            if ordered != all_layers[: len(ordered)]:
                raise SwapStateError(
                    "cannot revert a recovery layer while a newer layer remains"
                )
            artifacts: list[tuple[tuple[CLIName, Scope], SwapEntry, ArtifactState]] = []
            for key in all_layers:
                layer_info = CLIS[key[0]]
                if layer_info.config_path != info.config_path:
                    raise SwapStateError(
                        f"{_state_key(*key)} names an aliased client config"
                    )
                backup = _verify_entry_ownership(key, state[key], config, document)
                if key in selected:
                    artifacts.append((key, state[key], backup))
            last_backup = t.cast(FileState, artifacts[-1][2].file)
            prepared.append(
                PreparedRevert(
                    config,
                    tuple(artifacts),
                    last_backup.data,
                    last_backup.mode,
                )
            )
        except Exception as exc:  # noqa: BLE001 - finish planning every target
            errors.append(f"[{target.label}] {exc}")
    if errors:
        raise TransactionFailure("; ".join(errors))
    _reject_transaction_aliases(prepared, state_file)
    return prepared


def _stage_revert_transaction(
    plans: list[PreparedRevert],
    state: dict[tuple[CLIName, Scope], SwapEntry],
    state_file: ArtifactState,
    owned: OwnedFiles,
    lock: LockState,
) -> tuple[
    list[StagedRevert],
    pathlib.Path | None,
    pathlib.Path,
    dict[tuple[CLIName, Scope], SwapEntry],
]:
    staged: list[StagedRevert] = []
    try:
        for plan in plans:
            _validate_lock(lock)
            output = _stage(
                plan.config.physical.parent,
                plan.config.target.info.config_path.name,
                "restore",
                plan.output,
                plan.output_mode,
            )
            _own(owned, output)
            recovery = _stage(
                plan.config.physical.parent,
                plan.config.target.info.config_path.name,
                "recovery",
                plan.config.file.data,
                plan.config.file.mode,
            )
            _own(owned, recovery)
            backup_recoveries: list[pathlib.Path] = []
            for _key, _entry, backup in plan.entries:
                backup_file = t.cast(FileState, backup.file)
                if backup.parent is None:
                    raise RuntimeError(f"{backup.path.parent} does not exist")
                recovery_backup = _stage(
                    backup.parent.physical,
                    backup.path.name,
                    "recovery-backup",
                    backup_file.data,
                    backup_file.mode,
                )
                _own(owned, recovery_backup)
                backup_recoveries.append(recovery_backup)
            staged.append(
                StagedRevert(plan, output, recovery, tuple(backup_recoveries))
            )
            _validate_lock(lock)
        next_state = dict(state)
        for item in staged:
            output_file = _file_state(item.output)
            for key, _entry, _backup in item.plan.entries:
                next_state.pop(key)
            config_document = _config_document(item.plan.config, output_file)
            for key, entry in tuple(next_state.items()):
                if entry.target_path == str(item.plan.config.physical):
                    next_state[key] = dataclasses.replace(
                        entry, config_identity=config_document, checksum=None
                    )
            info = dataclasses.replace(
                item.plan.config.target.info, config_path=item.output
            )
            document = load_config(info)
            for key, entry in next_state.items():
                if entry.target_path != str(item.plan.config.physical):
                    continue
                route = t.cast(dict[str, t.Any], entry.route)
                actual = get_server(
                    key[0],
                    document,
                    route["name"],
                    pathlib.Path(route["repo"]),
                    scope=key[1],
                )
                if actual != _spec_from_document(route):
                    raise RuntimeError(
                        f"{_state_key(*key)} backup does not restore its route"
                    )
        if state_file.parent is None or state_file.file is None:
            raise RuntimeError("swap state disappeared during revert planning")
        state_stage = None
        if next_state:
            state_stage = _stage(
                state_file.parent.physical,
                STATE_FILE.name,
                "state",
                _state_bytes(next_state),
                0o600,
            )
            _own(owned, state_stage)
        state_recovery = _stage(
            state_file.parent.physical,
            STATE_FILE.name,
            "recovery-state",
            state_file.file.data,
            state_file.file.mode,
        )
        _own(owned, state_recovery)
        _validate_lock(lock)
        return staged, state_stage, state_recovery, next_state
    except Exception as exc:
        cleanup = _cleanup_owned(owned, lock=lock)
        detail = f"revert staging failed: {exc}"
        if cleanup:
            detail += "; " + "; ".join(cleanup)
        raise TransactionFailure(detail) from exc


def _rollback_revert(
    config_writes: list[ConfigWrite],
    backup_removals: list[BackupRemoval],
    state_file: ArtifactState,
    state_changed: bool,
    state_committed: FileState | None,
    state_recovery: pathlib.Path,
    owned: OwnedFiles,
    lock: LockState,
) -> tuple[list[str], set[pathlib.Path]]:
    errors: list[str] = []
    if state_changed:
        try:
            if state_committed is None:
                if os.path.lexists(STATE_FILE):
                    raise RuntimeError("unexpected replacement swap state")
            else:
                current = _artifact_state(STATE_FILE, required=True)
                _verify_artifact(current, state_committed)
                delayed = _apply_unlink(
                    STATE_FILE,
                    expected=state_committed,
                    lock=lock,
                )
                if delayed is not None:
                    raise delayed
            restored, delayed = _publish_absent(
                state_recovery,
                STATE_FILE,
                expected=owned[state_recovery],
                lock=lock,
            )
            _release_missing(owned, state_recovery)
            if restored != state_file.file:
                raise RuntimeError("swap state rollback identity changed")
            if delayed is not None:
                raise delayed
        except Exception as exc:  # noqa: BLE001 - retain state recovery
            errors.append(f"swap state: {exc}")
    for backup_removal in reversed(backup_removals):
        try:
            if os.path.lexists(backup_removal.backup.path):
                raise RuntimeError(f"{backup_removal.backup.path} appeared")
            restored, delayed = _publish_absent(
                backup_removal.recovery,
                backup_removal.backup.physical,
                expected=owned[backup_removal.recovery],
                lock=lock,
            )
            _release_missing(owned, backup_removal.recovery)
            if restored != backup_removal.backup.file:
                raise RuntimeError(
                    f"{backup_removal.backup.path} rollback identity changed"
                )
            if delayed is not None:
                raise delayed
        except Exception as exc:  # noqa: BLE001 - report every rollback failure
            errors.append(f"backup {backup_removal.backup.path}: {exc}")
    for operation in reversed(config_writes):
        try:
            _restore_config(operation, owned, lock)
        except Exception as exc:  # noqa: BLE001 - preserve every recovery copy
            errors.append(f"{operation.config.target.label} config: {exc}")
    if not errors:
        return [], set()
    preserved = set(owned)
    preserved.update(operation.backup.path for operation in backup_removals)
    preserved.add(STATE_FILE)
    return errors, preserved


def _commit_revert_transaction(
    staged: list[StagedRevert],
    state_stage: pathlib.Path | None,
    state_recovery: pathlib.Path,
    state_file: ArtifactState,
    next_state: dict[tuple[CLIName, Scope], SwapEntry],
    owned: OwnedFiles,
    lock: LockState,
) -> None:
    config_writes: list[ConfigWrite] = []
    backup_removals: list[BackupRemoval] = []
    state_changed = False
    state_committed: FileState | None = None
    try:
        for item in staged:
            _verify_config(item.plan.config, item.plan.config.file)
            for _key, _entry, backup in item.plan.entries:
                _verify_artifact(backup, backup.file)
        _verify_artifact(state_file, state_file.file)
        for item in staged:
            plan = item.plan
            removed, delayed = _apply_replace(
                plan.config.physical,
                item.recovery,
                expected=plan.config.file,
                destination_expected=owned[item.recovery],
                lock=lock,
            )
            if removed == plan.config.file:
                owned[item.recovery] = removed
            config_writes.append(ConfigWrite(plan.config, removed, item.recovery))
            if removed != plan.config.file:
                raise RuntimeError(
                    f"{plan.config.target.label} config identity changed"
                )
            if delayed is not None:
                raise delayed
            _verify_removed_config(plan.config)
            committed, delayed = _publish_absent(
                item.output,
                plan.config.physical,
                expected=owned[item.output],
                lock=lock,
            )
            _release_missing(owned, item.output)
            config_writes[-1] = ConfigWrite(plan.config, committed, item.recovery)
            _verify_config(plan.config, committed)
            if delayed is not None:
                raise delayed
        for item in staged:
            for (_key, _entry, backup), recovery in zip(
                item.plan.entries, item.backup_recoveries, strict=True
            ):
                _verify_artifact(backup, backup.file)
                removed, delayed = _apply_replace(
                    backup.physical,
                    recovery,
                    expected=t.cast(FileState, backup.file),
                    destination_expected=owned[recovery],
                    lock=lock,
                )
                if removed == backup.file:
                    owned[recovery] = removed
                backup_removals.append(BackupRemoval(backup, recovery))
                if removed != backup.file:
                    raise RuntimeError(f"{backup.path} identity changed")
                if delayed is not None:
                    raise delayed
        for operation in config_writes:
            _verify_config(operation.config, operation.committed)
        for backup_removal in backup_removals:
            _verify_artifact(backup_removal.backup, None)
        _verify_artifact(state_file, state_file.file)
        removed, delayed = _apply_replace(
            STATE_FILE,
            state_recovery,
            expected=t.cast(FileState, state_file.file),
            destination_expected=owned[state_recovery],
            lock=lock,
        )
        if removed == state_file.file:
            owned[state_recovery] = removed
        state_changed = True
        if removed != state_file.file:
            raise RuntimeError("swap state identity changed")
        if delayed is not None:
            raise delayed
        if state_stage is not None:
            state_committed, delayed = _publish_absent(
                state_stage,
                STATE_FILE,
                expected=owned[state_stage],
                lock=lock,
            )
            _release_missing(owned, state_stage)
            if delayed is not None:
                raise delayed
        if next_state:
            current = _artifact_state(STATE_FILE, required=True)
            loaded = _load_state_bytes(
                t.cast(FileState, current.file).data, strict=True
            )
            expected = {
                key: _with_entry_checksum(entry) for key, entry in next_state.items()
            }
            if loaded != expected:
                raise RuntimeError("committed revert state is not exact")
        elif os.path.lexists(STATE_FILE):
            raise RuntimeError("empty swap state was not removed")
        _verify_artifact(state_file, state_committed)
        for operation in config_writes:
            _verify_config(operation.config, operation.committed)
        for backup_removal in backup_removals:
            _verify_artifact(backup_removal.backup, None)
    except Exception as exc:
        rollback_errors, preserved = _rollback_revert(
            config_writes,
            backup_removals,
            state_file,
            state_changed,
            state_committed,
            state_recovery,
            owned,
            lock,
        )
        cleanup = _cleanup_owned(owned, preserved, lock=lock)
        detail = f"revert failed: {exc}"
        if rollback_errors:
            detail += "; rollback incomplete: " + "; ".join(rollback_errors)
        if cleanup:
            detail += "; cleanup incomplete: " + "; ".join(cleanup)
        if preserved:
            detail += "; recovery artifacts: " + ", ".join(
                str(path) for path in sorted(preserved, key=str)
            )
        raise TransactionFailure(detail) from exc
    cleanup = _cleanup_owned(owned, lock=lock)
    if cleanup:
        raise TransactionFailure("revert cleanup failed: " + "; ".join(cleanup))


def cmd_revert(args: argparse.Namespace) -> int:
    """Authenticate every selected recovery layer, then revert atomically."""
    try:
        state_file, state = _snapshot_state(strict=True)
        preview = _plan_revert(args, state, state_file)
        _check_lock_plan(preview, state_file)
        if args.dry_run:
            for plan in preview:
                for key, entry, _backup in plan.entries:
                    label = f"{key[0]}:{key[1]}" if key[0] == "claude" else key[0]
                    print(f"[{label}] would restore {entry.backup_path}")
            return 0
        if not preview:
            return 0
        with _state_lock() as lock:
            state_file, state = _snapshot_state(strict=True)
            plans = _plan_revert(args, state, state_file)
            _reject_transaction_aliases(plans, state_file, lock)
            _validate_lock(lock)
            owned: OwnedFiles = {}
            staged, state_stage, state_recovery, next_state = _stage_revert_transaction(
                plans, state, state_file, owned, lock
            )
            _commit_revert_transaction(
                staged,
                state_stage,
                state_recovery,
                state_file,
                next_state,
                owned,
                lock,
            )
        for plan in plans:
            for key, entry, _backup in plan.entries:
                label = f"{key[0]}:{key[1]}" if key[0] == "claude" else key[0]
                print(f"[{label}] restored from {entry.backup_path}")
        return 0
    except (OSError, RuntimeError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
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
    pu.add_argument("--entry", help="binary name (default: the project's AssemblyName)")
    pu.add_argument(
        "--env",
        action="append",
        type=_env_pair,
        metavar="KEY=VALUE",
        help=(
            "Extra env var to write into the server entry (repeatable). "
            "Layered on top of any preserved existing env; explicit --env wins. "
            "Use to inject e.g. LIBTMUX_TOOLSETS without a manual post-edit."
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
