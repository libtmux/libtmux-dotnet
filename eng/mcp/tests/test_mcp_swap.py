"""Tests for eng/mcp/mcp_swap.py.

The swap script rewrites configuration files that belong to the person
running it, so what these cover is mostly the round trip: that a swap can
be undone byte for byte, that it never disturbs a server it did not put
there, and that a half-finished run leaves something ``revert`` can still
undo.

It is loaded by path rather than imported, because it is a script rather
than a package, and exercised against temporary fixtures that mirror each
CLI's real layout.
"""

from __future__ import annotations

import argparse
import dataclasses
import fcntl
import importlib.util
import json
import os
import pathlib
import sys
import threading
import time
import types
import typing as t

import pytest
import tomlkit

_SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "mcp_swap.py"

# The script imports its siblings from its own directory, so a test loading it
# by path has to put that directory where Python will look.
sys.path.insert(0, str(_SCRIPT.parent))

import build
import jsonc
import xdg

_spec = importlib.util.spec_from_file_location("mcp_swap", _SCRIPT)
assert _spec and _spec.loader
mcp_swap = importlib.util.module_from_spec(_spec)
sys.modules["mcp_swap"] = mcp_swap
_spec.loader.exec_module(mcp_swap)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def fake_home(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> pathlib.Path:
    """Redirect every config path the script touches into ``tmp_path``."""
    monkeypatch.setattr(
        mcp_swap,
        "CLIS",
        {
            "claude": mcp_swap.CLIInfo(
                name="claude",
                binary="claude",
                config_path=tmp_path / ".claude.json",
                fmt="json",
                container=("mcpServers",),
                dialect="claude",
            ),
            "codex": mcp_swap.CLIInfo(
                name="codex",
                binary="codex",
                config_path=tmp_path / ".codex" / "config.toml",
                fmt="toml",
                container=("mcp_servers",),
                dialect="standard",
            ),
            "cursor": mcp_swap.CLIInfo(
                name="cursor",
                binary="cursor-agent",
                config_path=tmp_path / ".cursor" / "mcp.json",
                fmt="json",
                container=("mcpServers",),
                dialect="standard",
            ),
            "gemini": mcp_swap.CLIInfo(
                name="gemini",
                binary="gemini",
                config_path=tmp_path / ".gemini" / "settings.json",
                fmt="json",
                container=("mcpServers",),
                dialect="standard",
            ),
            "grok": mcp_swap.CLIInfo(
                name="grok",
                binary="grok",
                config_path=tmp_path / ".grok" / "config.toml",
                fmt="toml",
                container=("mcp_servers",),
                dialect="standard",
            ),
            "agy": mcp_swap.CLIInfo(
                name="agy",
                binary="agy",
                config_path=tmp_path / ".gemini" / "config" / "mcp_config.json",
                fmt="json",
                container=("mcpServers",),
                dialect="standard",
            ),
            "opencode": mcp_swap.CLIInfo(
                name="opencode",
                binary="opencode",
                config_path=tmp_path / ".config" / "opencode" / "opencode.jsonc",
                fmt="jsonc",
                container=("mcp",),
                dialect="opencode",
            ),
            "pi": mcp_swap.CLIInfo(
                name="pi",
                binary="pi",
                config_path=tmp_path / ".pi" / "agent" / "mcp.json",
                fmt="jsonc",
                container=("mcpServers",),
                dialect="standard",
            ),
        },
    )
    state_dir = tmp_path / "state"
    monkeypatch.setattr(mcp_swap, "STATE_DIR", state_dir)
    monkeypatch.setattr(mcp_swap, "STATE_FILE", state_dir / "state.json")
    return tmp_path


@pytest.fixture
def fake_repo(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> pathlib.Path:
    """Create a minimal .NET tree for meta resolution.

    Carries a prebuilt apphost under ``bin/Debug`` so the swap has
    something to point at without invoking the SDK; tests pass
    ``--no-build`` to keep the real toolchain out of the loop, and the
    runtime lookup is stubbed for the same reason.
    """
    repo = tmp_path / "repo"
    project = repo / "src" / "LibTmux.Mcp"
    project.mkdir(parents=True)
    (project / "LibTmux.Mcp.csproj").write_text(
        "<Project>\n"
        "  <PropertyGroup>\n"
        "    <AssemblyName>LibTmux.Mcp</AssemblyName>\n"
        "    <ToolCommandName>libtmux-mcp</ToolCommandName>\n"
        "  </PropertyGroup>\n"
        "</Project>\n"
    )
    binary = project / "bin" / "Debug" / build.FRAMEWORKS[0] / "LibTmux.Mcp"
    binary.parent.mkdir(parents=True)
    binary.write_text("#!/bin/sh\nexit 0\n")
    binary.chmod(0o755)

    # The SDK resolves through mise here, so a test must not depend on
    # whether this machine has one on PATH.
    dotnet = tmp_path / "sdk" / "dotnet"
    dotnet.parent.mkdir(parents=True, exist_ok=True)
    dotnet.write_text("#!/bin/sh\nexit 0\n")
    dotnet.chmod(0o755)
    monkeypatch.setattr(build, "find_dotnet", lambda: str(dotnet))
    return repo


def _write_json(path: pathlib.Path, data: dict[str, t.Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n")


def _runtime_env(fake_repo: pathlib.Path) -> dict[str, str]:
    """The env a written entry always carries.

    A framework-dependent apphost finds its runtime through
    ``DOTNET_ROOT``, and an agent does not inherit the shell that wrote
    the config, so every entry this script writes carries it.
    """
    del fake_repo
    return dict(build.dotnet_environment())


def _pinned_json_entry() -> dict[str, t.Any]:
    return {"command": "libtmux-mcp", "args": []}


def _pinned_claude_entry() -> dict[str, t.Any]:
    return {
        "type": "stdio",
        "command": "libtmux-mcp",
        "args": [],
        "env": {},
    }


# ---------------------------------------------------------------------------
# resolve_repo_meta
# ---------------------------------------------------------------------------


def test_resolve_repo_meta_strips_mcp_suffix(fake_repo: pathlib.Path) -> None:
    """``libtmux-mcp`` resolves to server name ``libtmux`` and entry ``libtmux-mcp``.

    The default matches the slug pre-existing users registered under;
    ``--server tmux`` is the override to target the README/serverInfo
    slug for fresh installs.
    """
    server, entry = build.resolve_repo_meta(fake_repo)
    assert server == "tmux"
    assert entry == "LibTmux.Mcp"


def test_resolve_repo_meta_falls_back_to_the_project_name(
    tmp_path: pathlib.Path,
) -> None:
    """A project that names no assembly is entered under its own name."""
    repo = tmp_path / "repo"
    project = repo / "src" / "Weather.Mcp"
    project.mkdir(parents=True)
    (project / "Weather.Mcp.csproj").write_text("<Project />\n")

    # The slug is the shared one whatever the project is called: every
    # libtmux port swaps into the same slot, which is what makes a swap
    # replace rather than accumulate.
    assert build.resolve_repo_meta(repo, "Weather.Mcp") == (
        build.DEFAULT_SERVER,
        "Weather.Mcp",
    )


# ---------------------------------------------------------------------------
# JSON round-trip: cursor / gemini / agy
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("cli", ["cursor", "gemini", "agy"])
def test_json_swap_and_revert_round_trip(
    fake_home: pathlib.Path, fake_repo: pathlib.Path, cli: str
) -> None:
    """Swap then revert a JSON-backed CLI must yield byte-identical bytes."""
    info = mcp_swap.CLIS[cli]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()

    args = mcp_swap.build_parser().parse_args(
        ["use", "--no-build", "--no-preflight", "--repo", str(fake_repo), "--cli", cli]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    after = json.loads(info.config_path.read_text())
    entry = after["mcpServers"]["tmux"]
    assert entry["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )
    assert entry["args"] == []

    revert_args = mcp_swap.build_parser().parse_args(["revert", "--cli", cli])
    assert mcp_swap.cmd_revert(revert_args) == 0
    assert info.config_path.read_bytes() == original


def test_grok_and_agy_registered() -> None:
    """The Grok and agy CLIs are exposed as first-class choices."""
    assert "grok" in mcp_swap.ALL_CLIS
    assert "agy" in mcp_swap.ALL_CLIS
    assert mcp_swap.CLIS["grok"].fmt == "toml"
    assert mcp_swap.CLIS["grok"].config_path.name == "config.toml"
    assert mcp_swap.CLIS["agy"].fmt == "json"
    assert mcp_swap.CLIS["agy"].config_path.name == "mcp_config.json"
    parser = mcp_swap.build_parser()
    assert parser.parse_args(["status", "--cli", "grok"]).cli == ["grok"]
    assert parser.parse_args(["status", "--cli", "agy"]).cli == ["agy"]


def test_grok_set_get_delete_roundtrip(fake_repo: pathlib.Path) -> None:
    """The Grok CLI reads/writes the TOML ``[mcp_servers]`` table like Codex."""
    config = tomlkit.parse("")
    spec = mcp_swap.McpServerSpec(
        command=str(build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug"))
    )
    assert mcp_swap.set_server("grok", config, "tmux", spec, fake_repo) == "added"
    assert "mcp_servers" in config
    got = mcp_swap.get_server("grok", config, "tmux", fake_repo)
    assert got is not None
    assert got.local_repo_path() == fake_repo
    assert mcp_swap.set_server("grok", config, "tmux", spec, fake_repo) == "replaced"
    assert mcp_swap.delete_server("grok", config, "tmux", fake_repo)
    assert mcp_swap.get_server("grok", config, "tmux", fake_repo) is None


def test_load_config_tolerates_empty_json(tmp_path: pathlib.Path) -> None:
    """An empty JSON config can be seeded with the first MCP server entry."""
    cfg = tmp_path / "mcp_config.json"
    cfg.write_text("")
    info = mcp_swap.CLIInfo(
        name="agy",
        binary="agy",
        config_path=cfg,
        fmt="json",
        container=("mcpServers",),
        dialect="standard",
    )
    assert mcp_swap.load_config(info) == {}


def test_use_local_preserves_existing_env_when_replacing(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Existing ``env`` on a replaced entry survives ``use``.

    Regression: ``cmd_use_local`` previously constructed the replacement
    spec via ``build_local_spec`` (env={}) and wrote it directly,
    silently dropping client-side settings like ``LIBTMUX_TOOLSETS`` or
    ``LIBTMUX_SOCKET`` that the user had set on the prior pinned-PyPI
    entry. The fix merges ``current.env`` into the new spec; this test
    locks the behaviour by seeding env on a Cursor entry, running
    ``use``, and asserting both the new local-uv command shape and
    the original env survived.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {
                "tmux": {
                    "command": "uvx",
                    "args": ["libtmux-mcp==0.1.0a2"],
                    "env": {"LIBTMUX_TOOLSETS": "inspect", "FOO": "bar"},
                }
            }
        },
    )

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    entry = json.loads(info.config_path.read_text())["mcpServers"]["tmux"]
    assert entry["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )
    assert entry["args"] == []
    assert entry["env"] == _runtime_env(fake_repo) | {
        "LIBTMUX_TOOLSETS": "inspect",
        "FOO": "bar",
    }


def test_use_local_with_no_prior_entry_writes_empty_env(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """When no prior entry exists, the new spec lands with empty env.

    The env-merge branch only fires for replacements; the "added" path
    (e.g. Codex with no prior libtmux block) should match
    ``build_local_spec``'s default empty env. This pins the Codex add
    case so the merge logic doesn't accidentally synthesise env from
    nothing.
    """
    info = mcp_swap.CLIS["codex"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_text("# empty config\n")

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "codex",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    config = tomlkit.parse(info.config_path.read_text())
    table = config["mcp_servers"]["tmux"]
    assert isinstance(table, tomlkit.items.Table)
    assert dict(table["env"]) == _runtime_env(fake_repo)


def test_json_swap_preserves_unrelated_servers(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Other servers in ``mcpServers`` are not touched during a libtmux swap."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {
                "tmux": _pinned_json_entry(),
                "agentex": {
                    "command": "libtmux-mcp",
                    "args": ["--directory", "/tmp", "run", "x"],
                },
            }
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    after = json.loads(info.config_path.read_text())
    assert set(after["mcpServers"].keys()) == {"tmux", "agentex"}


# ---------------------------------------------------------------------------
# Claude — per-project keying
# ---------------------------------------------------------------------------


def test_claude_swap_writes_under_repo_abspath_only(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Claude's per-project keying: only this repo's key gets rewritten."""
    info = mcp_swap.CLIS["claude"]
    other_repo_key = "/home/someone/other-project"
    _write_json(
        info.config_path,
        {
            "projects": {
                other_repo_key: {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            }
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    after = json.loads(info.config_path.read_text())

    assert (
        after["projects"][other_repo_key]["mcpServers"]["tmux"]
        == _pinned_claude_entry()
    )

    repo_key = str(fake_repo.resolve())
    new_entry = after["projects"][repo_key]["mcpServers"]["tmux"]
    assert new_entry["type"] == "stdio"
    assert new_entry["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )
    assert new_entry["args"] == []


# ---------------------------------------------------------------------------
# Claude --scope {user,project}
# ---------------------------------------------------------------------------


def test_claude_user_scope_writes_top_level_mcpServers(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``--scope user`` rewrites the top-level fallback, not a per-project node."""
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {"mcpServers": {"tmux": _pinned_claude_entry()}},
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "user",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    after = json.loads(info.config_path.read_text())
    new_entry = after["mcpServers"]["tmux"]
    assert new_entry["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )
    assert new_entry["args"] == []
    # No projects.<abs> node should have been created — user scope must
    # not bleed into the per-project layer.
    assert "projects" not in after or str(fake_repo.resolve()) not in after.get(
        "projects", {}
    )


def test_claude_user_scope_round_trip_restores_byte_identical(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``--scope user`` swap then revert yields byte-identical bytes."""
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {"mcpServers": {"tmux": _pinned_claude_entry()}},
    )
    original = info.config_path.read_bytes()

    swap_args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "user",
        ]
    )
    assert mcp_swap.cmd_use_local(swap_args) == 0
    assert info.config_path.read_bytes() != original  # sanity

    revert_args = mcp_swap.build_parser().parse_args(
        ["revert", "--cli", "claude", "--scope", "user"]
    )
    assert mcp_swap.cmd_revert(revert_args) == 0
    assert info.config_path.read_bytes() == original


def test_claude_user_and_project_swaps_coexist_independently(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Running both scopes leaves two distinct state entries with separate backups."""
    info = mcp_swap.CLIS["claude"]
    # Seed both layers with PyPI-style entries so the swap has something
    # to replace in each scope.
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    parser = mcp_swap.build_parser()

    # First swap: project scope (the legacy default).
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                ]
            )
        )
        == 0
    )
    # Second swap: user scope.
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                    "--scope",
                    "user",
                ]
            )
        )
        == 0
    )

    state = mcp_swap.load_state()
    assert ("claude", "project") in state
    assert ("claude", "user") in state
    assert (
        state[("claude", "project")].backup_path
        != state[("claude", "user")].backup_path
    )

    # Revert just user-scope; project entry must remain intact.
    assert (
        mcp_swap.cmd_revert(
            parser.parse_args(["revert", "--cli", "claude", "--scope", "user"])
        )
        == 0
    )
    state_after = mcp_swap.load_state()
    assert ("claude", "user") not in state_after
    assert ("claude", "project") in state_after

    after = json.loads(info.config_path.read_text())
    # User-level back to PyPI shape.
    assert after["mcpServers"]["tmux"]["command"] == "libtmux-mcp"
    # Project-level still local.
    proj_entry = after["projects"][str(fake_repo.resolve())]["mcpServers"]["tmux"]
    assert proj_entry["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )


def test_claude_full_revert_unwinds_both_scopes_in_lifo_order(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Reverting both Claude scopes (no ``--scope`` filter) restores the original.

    Regression: forward iteration over the swap-chronological state dict
    leaves the file in the post-first-swap state because the second
    backup contains the first swap's modifications. The two backups
    form a layered stack — they must be unwound in reverse-registration
    order (LIFO) so each backup peels off its own layer before the
    prior one is restored. CPython's ``contextlib.ExitStack`` uses the
    same LIFO discipline for the same reason.
    """
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    original = info.config_path.read_bytes()
    parser = mcp_swap.build_parser()

    # Two swaps in registration order: project first, then user.
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                ]
            )
        )
        == 0
    )
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                    "--scope",
                    "user",
                ]
            )
        )
        == 0
    )

    # Full revert: no --scope filter — must unwind BOTH layers.
    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "claude"])) == 0

    # Forward iteration would leave the file in the post-first-swap state
    # (project-scope still local). LIFO restores the true original.
    assert info.config_path.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()


def test_use_local_populates_swapped_at_and_seq_no(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``cmd_use_local`` records both human-readable timestamp and monotonic seq_no."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    state = mcp_swap.load_state()
    entry = state[("cursor", "user")]
    # ``swapped_at`` is the same ``time.strftime("%Y%m%d%H%M%S")`` value
    # that goes into the backup filename, so checking format suffices.
    assert len(entry.swapped_at) == 14 and entry.swapped_at.isdigit()
    assert entry.swapped_at in entry.backup_path
    assert entry.seq_no == 0


def test_seq_no_increments_across_swaps(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Each new swap gets ``seq_no = max(existing, default=-1) + 1``."""
    info_cursor = mcp_swap.CLIS["cursor"]
    info_gemini = mcp_swap.CLIS["gemini"]
    _write_json(info_cursor.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    _write_json(info_gemini.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()

    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "gemini",
                ]
            )
        )
        == 0
    )

    state = mcp_swap.load_state()
    assert state[("cursor", "user")].seq_no == 0
    assert state[("gemini", "user")].seq_no == 1


def test_revert_refuses_unauthenticated_legacy_lifo_state(
    fake_home: pathlib.Path,
) -> None:
    """Legacy entries cannot authorize a restore without identity checksums."""
    info = mcp_swap.CLIS["claude"]
    info.config_path.write_text("AFTER_BOTH_SWAPS\n")

    backup_old = mcp_swap.STATE_DIR.parent / "old-backup"
    backup_new = mcp_swap.STATE_DIR.parent / "new-backup"
    backup_old.parent.mkdir(parents=True, exist_ok=True)
    backup_old.write_text("ORIGINAL\n")
    backup_new.write_text("AFTER_FIRST_SWAP\n")

    # Wrong dict order: newer entry (higher seq_no) FIRST in JSON,
    # older entry (lower seq_no) SECOND. Without the explicit-seq_no
    # sort, dict iteration would unwind in the wrong direction.
    mcp_swap.STATE_DIR.mkdir(parents=True, exist_ok=True)
    state_payload = {
        "entries": {
            "claude:user": {
                "config_path": str(info.config_path),
                "backup_path": str(backup_new),
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20240202020202",
                "seq_no": 1,  # newer
            },
            "claude:project": {
                "config_path": str(info.config_path),
                "backup_path": str(backup_old),
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20240101010101",
                "seq_no": 0,  # older
            },
        },
    }
    mcp_swap.STATE_FILE.write_text(json.dumps(state_payload))

    mcp_swap.STATE_FILE.chmod(0o600)
    before = mcp_swap.STATE_FILE.read_bytes()
    args = mcp_swap.build_parser().parse_args(["revert", "--cli", "claude"])
    assert mcp_swap.cmd_revert(args) == 1
    assert info.config_path.read_text() == "AFTER_BOTH_SWAPS\n"
    assert mcp_swap.STATE_FILE.read_bytes() == before
    assert backup_old.read_text() == "ORIGINAL\n"
    assert backup_new.read_text() == "AFTER_FIRST_SWAP\n"


def test_non_claude_scope_user_passes_through_to_global_config(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``--scope`` is a no-op for non-Claude CLIs (their config has no scope layer)."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--scope",
            "user",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    after = json.loads(info.config_path.read_text())
    assert after["mcpServers"]["tmux"]["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )

    # State key reflects the normalised scope, not the raw flag value.
    state = mcp_swap.load_state()
    assert ("cursor", "user") in state
    # And the bizarre case "--scope project" against a non-Claude CLI is
    # silently coerced to user, not stored as a phantom (cursor, project).
    assert ("cursor", "project") not in state


# ---------------------------------------------------------------------------
# Codex TOML — format preservation + add-when-missing
# ---------------------------------------------------------------------------


def test_codex_swap_preserves_toml_comments(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """TOML round-trip preserves top-level comments and sibling tables."""
    info = mcp_swap.CLIS["codex"]
    info.config_path.parent.mkdir(parents=True)
    info.config_path.write_text(
        "# Top-level comment preserved across swap\n"
        "[mcp_servers.libtmux]\n"
        'command = "uvx"\n'
        'args = ["libtmux-mcp==0.1.0a2"]\n'
        "\n"
        "[other]\n"
        "keep = true\n"
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "codex",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    text = info.config_path.read_text()
    assert "# Top-level comment preserved across swap" in text
    doc = tomlkit.loads(text).unwrap()
    assert doc["mcp_servers"]["tmux"]["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )
    assert doc["other"]["keep"] is True


def test_codex_adds_block_when_absent_and_revert_removes_it(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """When no entry exists, ``use`` adds one and ``revert`` removes it again."""
    info = mcp_swap.CLIS["codex"]
    info.config_path.parent.mkdir(parents=True)
    info.config_path.write_text("[notice]\nhello = true\n")
    original = info.config_path.read_bytes()

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "codex",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    state = mcp_swap.load_state()
    # Codex has no per-project layer, so its scope is always "user".
    assert state[("codex", "user")].action == "added"

    revert_args = mcp_swap.build_parser().parse_args(["revert", "--cli", "codex"])
    assert mcp_swap.cmd_revert(revert_args) == 0
    assert info.config_path.read_bytes() == original


# ---------------------------------------------------------------------------
# Idempotence + dry-run
# ---------------------------------------------------------------------------


def test_dry_run_does_not_write(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """``--dry-run`` prints a diff but leaves the config and state file untouched."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--dry-run",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    assert info.config_path.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()
    assert "LibTmux.Mcp" in capsys.readouterr().out


def test_second_swap_is_noop(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Re-running ``use`` against an already-local config writes nothing new."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    first_bytes = info.config_path.read_bytes()

    capsys.readouterr()
    assert mcp_swap.cmd_use_local(args) == 0
    assert info.config_path.read_bytes() == first_bytes
    assert "already debug build" in capsys.readouterr().out


# ---------------------------------------------------------------------------
# State file
# ---------------------------------------------------------------------------


def test_state_file_cleared_after_full_revert(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Reverting every recorded swap deletes the empty state file on disk."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    mcp_swap.cmd_use_local(
        mcp_swap.build_parser().parse_args(
            [
                "use",
                "--no-build",
                "--no-preflight",
                "--repo",
                str(fake_repo),
                "--cli",
                "cursor",
            ]
        )
    )
    assert mcp_swap.STATE_FILE.exists()
    mcp_swap.cmd_revert(mcp_swap.build_parser().parse_args(["revert"]))
    assert not mcp_swap.STATE_FILE.exists()


def test_save_state_writes_atomically(fake_home: pathlib.Path) -> None:
    """save_state routes through atomic_write — no leftover temp files."""
    entry = mcp_swap.SwapEntry(
        config_path="/tmp/cfg.json",
        backup_path="/tmp/cfg.json.bak",
        server="tmux",
        action="replaced",
        swapped_at="20260101000000",
        seq_no=0,
    )
    mcp_swap.save_state({("claude", "project"): entry})

    assert mcp_swap.STATE_FILE.exists()
    payload = json.loads(mcp_swap.STATE_FILE.read_text())
    assert payload["entries"]["claude:project"]["server"] == "tmux"

    # tempfile.mkstemp writes siblings prefixed "<name>." — none should
    # remain after a successful atomic_write.
    leftovers = [
        p
        for p in mcp_swap.STATE_DIR.iterdir()
        if p.name.startswith("mcp_swap.json.") and p != mcp_swap.STATE_FILE
    ]
    assert leftovers == [], f"unexpected tempfile leftovers: {leftovers}"


def test_use_local_serializes_the_full_state_transaction(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Concurrent commands cannot lose one another's recovery records."""
    for cli in ("cursor", "gemini"):
        _write_json(
            mcp_swap.CLIS[cli].config_path,
            {"mcpServers": {"tmux": _pinned_json_entry()}},
        )
    parser = mcp_swap.build_parser()
    real_save_state = mcp_swap.save_state
    guard = threading.Lock()
    gate = threading.Barrier(3)
    active = 0
    overlapped = False
    results: list[int] = []

    def slow_save_state(entries: dict[t.Any, t.Any]) -> None:
        nonlocal active, overlapped
        with guard:
            active += 1
            overlapped = overlapped or active > 1
        try:
            time.sleep(0.1)
            real_save_state(entries)
        finally:
            with guard:
                active -= 1

    def swap(cli: str) -> None:
        args = parser.parse_args(
            [
                "use",
                "--no-build",
                "--no-preflight",
                "--repo",
                str(fake_repo),
                "--cli",
                cli,
            ]
        )
        gate.wait()
        results.append(mcp_swap.cmd_use_local(args))

    monkeypatch.setattr(mcp_swap, "save_state", slow_save_state)
    threads = [
        threading.Thread(target=swap, args=(cli,)) for cli in ("cursor", "gemini")
    ]
    for thread in threads:
        thread.start()
    gate.wait()
    for thread in threads:
        thread.join(timeout=2)

    assert results == [0, 0]
    assert not overlapped
    assert set(mcp_swap.load_state()) == {("cursor", "user"), ("gemini", "user")}
    assert all(not thread.is_alive() for thread in threads)


# ---------------------------------------------------------------------------
# McpServerSpec helpers
# ---------------------------------------------------------------------------


def test_claude_project_node_rejects_non_mapping_projects(
    fake_repo: pathlib.Path,
) -> None:
    """A non-mapping ``projects`` value is rejected with a clear error.

    Claude's ``~/.claude.json`` layout is undocumented internal state.
    If a future Claude release reshapes ``projects`` (e.g. to a list),
    the script should fail before the atomic write begins so the
    backup defense is not asked to recover from a partially-mutated
    structure.
    """
    config: dict[str, t.Any] = {"projects": "not a dict"}
    with pytest.raises(RuntimeError, match="layout appears to have changed"):
        mcp_swap._claude_project_node(config, fake_repo, create=True)


def test_claude_project_node_rejects_non_mapping_project_node(
    fake_repo: pathlib.Path,
) -> None:
    """A non-mapping per-project node is rejected with a clear error."""
    key = str(fake_repo.resolve())
    config: dict[str, t.Any] = {"projects": {key: "scalar instead of dict"}}
    with pytest.raises(RuntimeError, match="layout appears to have changed"):
        mcp_swap._claude_project_node(config, fake_repo, create=True)


def test_claude_project_node_accepts_well_shaped_config(
    fake_repo: pathlib.Path,
) -> None:
    """Well-shaped config passes through to creation without error."""
    config: dict[str, t.Any] = {}
    node = mcp_swap._claude_project_node(config, fake_repo, create=True)
    assert isinstance(node, dict)
    assert "mcpServers" in node


def test_claude_user_scope_rejects_non_mapping_mcpServers(
    fake_repo: pathlib.Path,
) -> None:
    """User-scope ``set_server`` rejects a non-mapping top-level ``mcpServers``.

    Symmetric with the existing ``_claude_project_node`` shape guard for
    the per-project path. Without this guard, a malformed Claude config
    would surface as an opaque ``AttributeError`` from ``.setdefault()``;
    with it, the user gets the same actionable RuntimeError that the
    project-scope path raises. Pattern follows hatchling's pre-mutation
    config validation in ``builders/config.py``.
    """
    config: dict[str, t.Any] = {"mcpServers": "not a dict"}
    spec = mcp_swap.McpServerSpec(command="uv", args=["run", "libtmux-mcp"])
    with pytest.raises(RuntimeError, match="layout appears to have changed"):
        mcp_swap.set_server("claude", config, "tmux", spec, fake_repo, scope="user")


def test_claude_user_scope_get_server_rejects_non_mapping_mcpServers(
    fake_repo: pathlib.Path,
) -> None:
    """User-scope ``get_server`` rejects a non-mapping top-level ``mcpServers``.

    Mirrors the write-side guard so reads fail loudly with an actionable
    ``RuntimeError`` instead of an opaque ``AttributeError`` from a
    chained ``.get()``. Symmetric coverage matches the project-scope
    path, which routes all three of read/write/delete through
    ``_claude_project_node``.
    """
    config: dict[str, t.Any] = {"mcpServers": "not a dict"}
    with pytest.raises(RuntimeError, match="layout appears to have changed"):
        mcp_swap.get_server("claude", config, "tmux", fake_repo, scope="user")


def test_claude_user_scope_delete_server_rejects_non_mapping_mcpServers(
    fake_repo: pathlib.Path,
) -> None:
    """User-scope ``delete_server`` rejects a non-mapping top-level ``mcpServers``.

    Mirrors the write- and read-side guards so deletes fail loudly with
    an actionable ``RuntimeError`` instead of a silent no-op or a
    ``TypeError`` from ``name in servers`` against a non-mapping.
    """
    config: dict[str, t.Any] = {"mcpServers": "not a dict"}
    with pytest.raises(RuntimeError, match="layout appears to have changed"):
        mcp_swap.delete_server("claude", config, "tmux", fake_repo, scope="user")


# ---------------------------------------------------------------------------
# Graceful CLI error UX — RuntimeError from shape guards must not surface
# as a Python traceback at the CLI boundary.
# ---------------------------------------------------------------------------


def test_use_local_returns_clean_error_on_malformed_claude_user_mcpServers(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A malformed Claude config produces a clean error + exit 1, no traceback.

    Regression: ``set_server``'s shape guard raises ``RuntimeError`` from
    ``_claude_user_servers``, which previously propagated past
    ``cmd_use_local``'s inner ``try/except`` (that one wraps only
    ``atomic_write`` + ``_revalidate``). Per-CLI ``try/except RuntimeError``
    around the config-prep region now catches it. Pattern follows pytest's
    main-level ``UsageError`` formatter in ``_pytest/config/__init__.py``.
    """
    info = mcp_swap.CLIS["claude"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_text(json.dumps({"mcpServers": "not a dict"}))

    rc = mcp_swap.main(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "user",
        ]
    )
    captured = capsys.readouterr()
    assert rc == 1
    assert "[claude:user]" in captured.err
    assert "layout appears to have changed" in captured.err
    assert "Traceback" not in captured.err


def test_status_continues_to_other_clis_on_malformed_claude(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A malformed Claude config does not abort the rest of the status batch.

    Per-CLI continuation: cursor's status line still prints even when
    Claude's config is corrupt. Same per-CLI continuation pattern
    ``cmd_use_local`` and ``cmd_revert`` already use.
    """
    cursor_info = mcp_swap.CLIS["cursor"]
    _write_json(cursor_info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    claude_info = mcp_swap.CLIS["claude"]
    claude_info.config_path.parent.mkdir(parents=True, exist_ok=True)
    claude_info.config_path.write_text(json.dumps({"mcpServers": "not a dict"}))

    rc = mcp_swap.main(
        [
            "status",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--cli",
            "cursor",
        ]
    )
    captured = capsys.readouterr()
    assert rc == 0
    assert "[cursor]" in captured.out
    assert "[claude]" in captured.err
    assert "layout appears to have changed" in captured.err
    assert "Traceback" not in captured.err


# ---------------------------------------------------------------------------
# State-file resilience — hand-edited corruption must not crash the script.
# Schema is internal (no compat contract) so the policy is "drop on parse
# failure", consistent with how malformed state-file keys already behave.
# ---------------------------------------------------------------------------


def test_load_state_drops_entries_with_non_int_seq_no(
    fake_home: pathlib.Path,
) -> None:
    """A non-coercible ``seq_no`` is dropped at load time.

    Same drop-on-malformed posture as :func:`mcp_swap._parse_state_key`:
    schema is internal, so a hand-edited file with corrupt counter
    values is silently skipped rather than crashing.
    """
    mcp_swap.STATE_DIR.mkdir(parents=True, exist_ok=True)
    payload = {
        "entries": {
            "claude:user": {
                "config_path": "/tmp/cfg.json",
                "backup_path": "/tmp/cfg.json.bak",
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20260101000000",
                "seq_no": "not-an-int",  # corrupted
            },
            "claude:project": {
                "config_path": "/tmp/cfg.json",
                "backup_path": "/tmp/cfg.json.bak2",
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20260101000001",
                "seq_no": 1,  # well-formed
            },
        },
    }
    mcp_swap.STATE_FILE.write_text(json.dumps(payload))

    state = mcp_swap.load_state()
    assert ("claude", "user") not in state
    assert ("claude", "project") in state
    assert state[("claude", "project")].seq_no == 1


def test_load_state_coerces_numeric_string_seq_no(
    fake_home: pathlib.Path,
) -> None:
    """A numeric-string ``seq_no`` is coerced via ``int()``, not dropped.

    Distinguishes "user typed quotes around the number" from "user
    typed something non-numeric": the former should still load
    cleanly, the latter should drop.
    """
    mcp_swap.STATE_DIR.mkdir(parents=True, exist_ok=True)
    payload = {
        "entries": {
            "cursor:user": {
                "config_path": "/tmp/cfg.json",
                "backup_path": "/tmp/cfg.json.bak",
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20260101000000",
                "seq_no": "3",  # numeric string — coerce
            },
        },
    }
    mcp_swap.STATE_FILE.write_text(json.dumps(payload))

    state = mcp_swap.load_state()
    assert ("cursor", "user") in state
    assert state[("cursor", "user")].seq_no == 3


def test_load_state_drops_entries_with_missing_required_fields(
    fake_home: pathlib.Path,
) -> None:
    """Entries missing required SwapEntry fields are dropped, not raised.

    Pre-fix, ``SwapEntry(**v)`` raised ``TypeError: missing 1 required
    positional argument: 'seq_no'`` and aborted the load. Post-fix,
    :func:`mcp_swap._parse_state_entry` catches ``TypeError`` and
    returns ``None``, dropping the entry.
    """
    mcp_swap.STATE_DIR.mkdir(parents=True, exist_ok=True)
    payload = {
        "entries": {
            "cursor:user": {
                # Missing seq_no entirely.
                "config_path": "/tmp/cfg.json",
                "backup_path": "/tmp/cfg.json.bak",
                "server": "tmux",
                "action": "replaced",
                "swapped_at": "20260101000000",
            },
        },
    }
    mcp_swap.STATE_FILE.write_text(json.dumps(payload))

    state = mcp_swap.load_state()
    assert state == {}


def test_revert_with_corrupt_seq_no_preserves_every_recovery_layer(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Same-file recovery stops when one layer has a corrupt ``seq_no``.

    Regression: the LIFO sort at ``cmd_revert`` would compare ``int`` vs
    ``str`` (``int < str`` raises in Python 3) when two same-CLI
    entries existed and one had a hand-edited corrupt counter.
    Cross-CLI buckets are length-1 and never invoke comparison —
    making the failure mode asymmetric, only triggering on Claude
    project + user. Dropping that layer and applying the other backup
    would violate LIFO order, so mutation is refused while all recovery
    material remains intact.
    """
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                ]
            )
        )
        == 0
    )
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "claude",
                    "--scope",
                    "user",
                ]
            )
        )
        == 0
    )

    before_config = info.config_path.read_bytes()
    raw = json.loads(mcp_swap.STATE_FILE.read_text())
    raw["entries"]["claude:user"]["seq_no"] = "not-an-int"
    mcp_swap.STATE_FILE.write_text(json.dumps(raw))
    corrupt_state = mcp_swap.STATE_FILE.read_bytes()

    rc = mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "claude"]))
    assert rc == 1
    assert info.config_path.read_bytes() == before_config
    assert mcp_swap.STATE_FILE.read_bytes() == corrupt_state


# ---------------------------------------------------------------------------
# Backup file lifecycle — delete-on-success, keep-on-error.
# ---------------------------------------------------------------------------


def test_revert_deletes_backup_after_successful_restore(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """A successful revert deletes the backup file it just consumed.

    Pre-fix, ``cmd_revert`` restored from ``.bak.mcp-swap-<ts>`` and left
    the file on disk. Repeated swap/revert cycles let backups accumulate
    indefinitely. Post-fix matches CPython's
    ``tempfile.NamedTemporaryFile`` cleanup discipline
    (``Lib/tempfile.py:614-618``): delete on success, keep on error.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    state = mcp_swap.load_state()
    backup = pathlib.Path(state[("cursor", "user")].backup_path)
    assert backup.exists()

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 0
    assert not backup.exists()


def test_revert_dry_run_keeps_backup(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``revert --dry-run`` must not delete the backup file.

    The dry-run path ``continue``s before reaching the unlink, so this
    locks the behaviour against a future refactor that restructures
    the loop body.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    state = mcp_swap.load_state()
    backup = pathlib.Path(state[("cursor", "user")].backup_path)

    assert (
        mcp_swap.cmd_revert(
            parser.parse_args(["revert", "--cli", "cursor", "--dry-run"])
        )
        == 0
    )
    assert backup.exists()


def test_revert_returns_failure_when_the_recorded_backup_is_missing(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Automation receives a nonzero status when recovery cannot complete."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    backup.unlink()

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 1
    assert ("cursor", "user") in mcp_swap.load_state()


def test_explicit_missing_config_returns_failure_without_creating_state(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """An explicitly requested absent config is an error, not a successful no-op."""
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1
    assert not mcp_swap.STATE_FILE.exists()


# ---------------------------------------------------------------------------
# `status --scope` filter — completes symmetry with use / revert.
# ---------------------------------------------------------------------------


def test_status_scope_user_filters_to_user_only(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """``status --scope user`` shows only the user-scope claude line."""
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "status",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "user",
        ]
    )
    assert mcp_swap.cmd_status(args) == 0
    out = capsys.readouterr().out
    assert "[claude:user]" in out
    assert "[claude:project]" not in out


def test_status_scope_project_filters_to_project_only(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """``status --scope project`` shows only the project-scope claude line."""
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "status",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "project",
        ]
    )
    assert mcp_swap.cmd_status(args) == 0
    out = capsys.readouterr().out
    assert "[claude:project]" in out
    assert "[claude:user]" not in out


def test_status_no_scope_shows_both_layers(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Without ``--scope``, both Claude layers print when both have entries.

    Locks the existing default behaviour so a future refactor can't
    silently change it.
    """
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    args = mcp_swap.build_parser().parse_args(
        ["status", "--repo", str(fake_repo), "--cli", "claude"]
    )
    assert mcp_swap.cmd_status(args) == 0
    out = capsys.readouterr().out
    assert "[claude:user]" in out
    assert "[claude:project]" in out


def test_status_scope_no_op_for_non_claude_cli(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """``--scope`` is a no-op for non-Claude CLIs (their config has no scope layer).

    Asserts that ``--cli cursor --scope project`` produces the same
    single ``[cursor]`` line as ``--cli cursor`` alone.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()

    # Without --scope.
    assert (
        mcp_swap.cmd_status(
            parser.parse_args(["status", "--repo", str(fake_repo), "--cli", "cursor"])
        )
        == 0
    )
    out_no_scope = capsys.readouterr().out

    # With --scope project (silently no-op for cursor).
    assert (
        mcp_swap.cmd_status(
            parser.parse_args(
                [
                    "status",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                    "--scope",
                    "project",
                ]
            )
        )
        == 0
    )
    out_with_scope = capsys.readouterr().out

    assert out_no_scope == out_with_scope
    assert "[cursor]" in out_with_scope


def test_status_scope_user_with_only_project_entry_shows_no_entry(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Filtering to a scope with no entry prints a scope-tagged "no entry" line.

    Locks the symmetry with ``use`` / ``revert`` output, which
    label scope-filtered actions as ``[claude:<scope>]`` rather than
    falling back to the unscoped ``[claude]`` form.
    """
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "status",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
            "--scope",
            "user",
        ]
    )
    assert mcp_swap.cmd_status(args) == 0
    out = capsys.readouterr().out
    assert "[claude:user] no entry for" in out
    assert "[claude:project]" not in out


# ---------------------------------------------------------------------------
# use --env injection
# ---------------------------------------------------------------------------


def _local_entry(repo: pathlib.Path) -> dict[str, t.Any]:
    """Return the JSON entry a default (debug-profile) swap writes."""
    return {
        "command": str(build.profile_binary(repo, "LibTmux.Mcp", "Debug")),
        "args": [],
    }


def test_use_local_env_flag_injects_into_entry(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``--env KEY=VALUE`` lands in the written server entry's ``env``.

    The isolation workflow needs to point the server at a scratch socket
    without a manual post-edit; ``--env`` writes that env at swap time.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {}})

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--env",
            "LIBTMUX_SOCKET=mcp-target",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    entry = json.loads(info.config_path.read_text())["mcpServers"]["tmux"]
    assert entry["env"] == _runtime_env(fake_repo) | {"LIBTMUX_SOCKET": "mcp-target"}


def test_use_local_env_flag_wins_over_preserved_env(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Explicit ``--env`` overrides a preserved key; other preserved keys survive."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {
                "tmux": {
                    "command": "uvx",
                    "args": ["libtmux-mcp==0.1.0a2"],
                    "env": {"LIBTMUX_TOOLSETS": "inspect", "KEEP": "me"},
                }
            }
        },
    )

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--env",
            "LIBTMUX_TOOLSETS=inspect,manage,execute,teardown",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    entry = json.loads(info.config_path.read_text())["mcpServers"]["tmux"]
    assert entry["env"] == _runtime_env(fake_repo) | {
        "LIBTMUX_TOOLSETS": "inspect,manage,execute,teardown",
        "KEEP": "me",
    }


def test_env_pair_rejects_malformed() -> None:
    """``--env`` without ``=`` is an argparse error, not a silent skip."""
    with pytest.raises(SystemExit):
        mcp_swap.build_parser().parse_args(["use", "--env", "NOEQUALS"])


def test_use_local_env_written_on_already_local_entry(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """``--env`` still writes when the entry already points at this repo.

    Regression: the already-local short-circuit ``continue``d before the env
    merge, so ``--env`` (e.g. an isolated ``LIBTMUX_SOCKET``) was silently
    dropped whenever the config already pointed local — the common case. The
    guard now only short-circuits when the requested env is already satisfied.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _local_entry(fake_repo)}})

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--env",
            "LIBTMUX_SOCKET=mcp-target",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    entry = json.loads(info.config_path.read_text())["mcpServers"]["tmux"]
    assert entry.get("env") == _runtime_env(fake_repo) | {
        "LIBTMUX_SOCKET": "mcp-target"
    }


def test_use_local_already_local_still_noop_when_env_matches(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """The short-circuit still fires when the requested env is already present."""
    info = mcp_swap.CLIS["cursor"]
    spec = _local_entry(fake_repo)
    spec["env"] = _runtime_env(fake_repo) | {"LIBTMUX_SOCKET": "mcp-target"}
    _write_json(info.config_path, {"mcpServers": {"tmux": spec}})
    before = info.config_path.read_bytes()

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--env",
            "LIBTMUX_SOCKET=mcp-target",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    assert info.config_path.read_bytes() == before


# ---------------------------------------------------------------------------
# naming hint
# ---------------------------------------------------------------------------


def test_naming_hint_points_at_registered_alias(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Hint names the real slug when the repo uses a non-default server name.

    A bare run would otherwise no-op on a missing entry, so the hint points
    at the name the CLIs were actually registered under.
    """
    _write_json(
        mcp_swap.CLIS["cursor"].config_path,
        {"mcpServers": {"tmuxdev": _local_entry(fake_repo)}},
    )
    hint = mcp_swap._naming_hint(fake_repo.resolve(), "tmux")
    assert hint is not None
    assert "--server tmuxdev" in hint


def test_naming_hint_none_when_derived_name_matches(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """No hint when the repo is already registered under the derived name."""
    _write_json(
        mcp_swap.CLIS["cursor"].config_path,
        {"mcpServers": {"tmux": _local_entry(fake_repo)}},
    )
    assert mcp_swap._naming_hint(fake_repo.resolve(), "tmux") is None


def test_naming_hint_none_when_repo_also_registered_under_derived(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """No hint when the derived name points here, even if another name does too.

    Regression: the hint used to fire on the other name and falsely claim
    'nothing is registered under <derived>' when the derived name in fact
    points at the repo.
    """
    _write_json(
        mcp_swap.CLIS["cursor"].config_path,
        {
            "mcpServers": {
                "tmux": _local_entry(fake_repo),
                "tmuxdev": _local_entry(fake_repo),
            }
        },
    )
    assert mcp_swap._naming_hint(fake_repo.resolve(), "tmux") is None


# ---------------------------------------------------------------------------
# doctor
# ---------------------------------------------------------------------------


def test_doctor_reports_name_mismatch_and_auth_env(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Doctor surfaces the server-name mismatch and auth-overriding env vars."""
    _write_json(
        mcp_swap.CLIS["cursor"].config_path,
        {"mcpServers": {"tmuxdev": _local_entry(fake_repo)}},
    )
    monkeypatch.setenv("OPENAI_API_KEY", "sk-test")

    args = mcp_swap.build_parser().parse_args(["doctor", "--repo", str(fake_repo)])
    assert mcp_swap.cmd_doctor(args) == 0
    out = capsys.readouterr().out
    assert "server name mismatch" in out
    assert "--server tmux" in out
    assert "OPENAI_API_KEY" in out and "codex" in out


def test_doctor_flags_missing_backup_and_orphans(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Doctor flags a state entry whose backup vanished, and untracked backups."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _local_entry(fake_repo)}})
    # A recorded swap whose backup file does not exist -> revert would fail.
    mcp_swap.save_state(
        {
            ("cursor", "user"): mcp_swap.SwapEntry(
                config_path=str(info.config_path),
                backup_path=str(info.config_path) + ".bak.mcp-swap-20200101000000",
                server="tmux",
                action="replaced",
                swapped_at="20200101000000",
                seq_no=0,
            )
        }
    )
    # An orphaned backup on disk not referenced by state.
    orphan = info.config_path.parent / (
        info.config_path.name + ".bak.mcp-swap-20190101000000"
    )
    orphan.write_text("stale")

    args = mcp_swap.build_parser().parse_args(["doctor", "--repo", str(fake_repo)])
    assert mcp_swap.cmd_doctor(args) == 0
    out = capsys.readouterr().out
    assert "BACKUP MISSING" in out
    assert "orphaned backups" in out


def test_orphaned_backups_matches_swap_pattern(
    fake_home: pathlib.Path,
) -> None:
    """``_orphaned_backups`` finds swap backups and ignores the live config."""
    info = mcp_swap.CLIS["cursor"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_text("{}\n")
    b1 = info.config_path.parent / (
        info.config_path.name + ".bak.mcp-swap-20260101000000"
    )
    b1.write_text("x")
    found = mcp_swap._orphaned_backups(info.config_path)
    assert b1 in found
    assert info.config_path not in found


def test_doctor_does_not_call_orphaned_backups_safe_to_delete(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Orphan advice must not read as "safe to delete".

    An untracked backup can be the *only* pre-swap copy of a config — a
    swap whose write failed leaves exactly that. Telling the user to bin
    it turns a recoverable state into data loss.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _local_entry(fake_repo)}})
    orphan = info.config_path.parent / (
        info.config_path.name + ".bak.mcp-swap-20190101000000"
    )
    orphan.write_text("pristine")

    args = mcp_swap.build_parser().parse_args(["doctor", "--repo", str(fake_repo)])
    assert mcp_swap.cmd_doctor(args) == 0
    out = capsys.readouterr().out
    assert "orphaned backups" in out
    assert "safe to delete" not in out
    assert "inspect before deleting" in out


# ---------------------------------------------------------------------------
# Repeat swaps must not destroy the pre-swap config.
# ---------------------------------------------------------------------------


def _freeze_timestamps(monkeypatch: pytest.MonkeyPatch, stamps: list[str]) -> None:
    """Make the script's ``time.strftime`` yield ``stamps`` in order.

    The backup filename embeds ``%Y%m%d%H%M%S``, so same-second vs
    different-second is the difference between two swaps deriving the
    same path or two distinct ones. Pinning the sequence makes both
    cases deterministic instead of a race against the wall clock. Only
    the script's own ``time`` reference is swapped, so pytest's log
    formatting keeps the real clock.
    """
    remaining = list(stamps)

    def fake_strftime(*_args: object) -> str:
        return remaining.pop(0) if remaining else stamps[-1]

    monkeypatch.setattr(mcp_swap, "time", types.SimpleNamespace(strftime=fake_strftime))


@pytest.mark.parametrize(
    ("case", "stamps"),
    [
        ("same_second", ["20260101000000", "20260101000000"]),
        ("different_second", ["20260101000000", "20260101000001"]),
    ],
)
def test_repeat_swap_then_revert_restores_pristine_config(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    case: str,
    stamps: list[str],
) -> None:
    """Swap -> swap -> revert must yield the byte-identical pre-swap config.

    Regression for two ways the second swap used to destroy the only
    pristine copy: with a same-second timestamp both swaps derived the
    same backup path and the second write clobbered the first; with a
    different-second timestamp the second swap wrote a fresh backup (of
    the already-swapped config) and repointed state at it, orphaning the
    pristine one. Either way ``revert`` restored a swapped config.
    """
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()
    _freeze_timestamps(monkeypatch, stamps)
    parser = mcp_swap.build_parser()

    def swap(value: str) -> None:
        # Distinct --env per swap so the second run is a real rewrite,
        # not the "already local" no-op.
        assert (
            mcp_swap.cmd_use_local(
                parser.parse_args(
                    [
                        "use",
                        "--no-build",
                        "--no-preflight",
                        "--repo",
                        str(fake_repo),
                        "--cli",
                        "cursor",
                        "--env",
                        f"LIBTMUX_SOCKET={value}",
                    ]
                )
            )
            == 0
        )

    swap("one")
    first_backup = pathlib.Path(mcp_swap.load_state()[("cursor", "user")].backup_path)
    assert first_backup.read_bytes() == original

    swap("two")

    # The second swap keeps the first backup: same path, same bytes, and
    # no second backup file left behind.
    entry = mcp_swap.load_state()[("cursor", "user")]
    assert pathlib.Path(entry.backup_path) == first_backup
    assert first_backup.read_bytes() == original
    assert mcp_swap._orphaned_backups(info.config_path) == [first_backup]

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 0
    assert info.config_path.read_bytes() == original


def test_repeat_swap_keeps_claude_lifo_order_across_scopes(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Re-swapping one Claude scope must not reorder the LIFO unwind.

    Both scopes back the same physical file, so a backup's position in
    the stack is fixed by what it captured, not by when it was last
    touched. Bumping ``seq_no`` on the re-swap would make ``revert``
    peel the user layer off first and leave the project layer's backup
    (which still contains the user swap) as the final state.
    """
    info = mcp_swap.CLIS["claude"]
    _write_json(
        info.config_path,
        {
            "mcpServers": {"tmux": _pinned_claude_entry()},
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                },
            },
        },
    )
    original = info.config_path.read_bytes()
    _freeze_timestamps(
        monkeypatch, ["20260101000000", "20260101000001", "20260101000002"]
    )
    parser = mcp_swap.build_parser()

    def swap(scope: str, value: str) -> None:
        assert (
            mcp_swap.cmd_use_local(
                parser.parse_args(
                    [
                        "use",
                        "--no-build",
                        "--no-preflight",
                        "--repo",
                        str(fake_repo),
                        "--cli",
                        "claude",
                        "--scope",
                        scope,
                        "--env",
                        f"LIBTMUX_SOCKET={value}",
                    ]
                )
            )
            == 0
        )

    swap("user", "one")
    swap("project", "one")
    # Re-swap the *older* layer; its backup still holds the pristine file.
    swap("user", "two")

    state = mcp_swap.load_state()
    assert state[("claude", "user")].seq_no < state[("claude", "project")].seq_no

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "claude"])) == 0
    assert info.config_path.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()


def test_write_new_backup_never_overwrites(tmp_path: pathlib.Path) -> None:
    """A taken backup path is left alone; the write lands on a suffixed sibling."""
    base = tmp_path / "config.toml.bak.mcp-swap-20260101000000"
    first = mcp_swap.write_new_backup(base, b"pristine\n")
    assert first == base

    second = mcp_swap.write_new_backup(base, b"later\n")
    assert second == base.with_name(base.name + "-1")
    assert base.read_bytes() == b"pristine\n"
    assert second.read_bytes() == b"later\n"

    third = mcp_swap.write_new_backup(base, b"later still\n")
    assert third == base.with_name(base.name + "-2")


def test_swap_after_backup_vanished_refuses_to_replace_recovery(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A missing pristine backup blocks re-swap and preserves its state record."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()

    def swap(value: str) -> None:
        assert (
            mcp_swap.cmd_use_local(
                parser.parse_args(
                    [
                        "use",
                        "--no-build",
                        "--no-preflight",
                        "--repo",
                        str(fake_repo),
                        "--cli",
                        "cursor",
                        "--env",
                        f"LIBTMUX_SOCKET={value}",
                    ]
                )
            )
            == 0
        )

    swap("one")
    stale = pathlib.Path(mcp_swap.load_state()[("cursor", "user")].backup_path)
    swapped_bytes = info.config_path.read_bytes()
    stale.unlink()
    capsys.readouterr()

    args = parser.parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--env",
            "LIBTMUX_SOCKET=two",
        ]
    )
    state_before = mcp_swap.STATE_FILE.read_bytes()
    assert mcp_swap.cmd_use_local(args) == 1
    assert str(stale) in capsys.readouterr().err
    assert info.config_path.read_bytes() == swapped_bytes
    assert mcp_swap.STATE_FILE.read_bytes() == state_before


# ---------------------------------------------------------------------------
# Pull-request targeting
# ---------------------------------------------------------------------------


class RemoteURLFixture(t.NamedTuple):
    """One git remote spelling and the https URL it normalizes to.

    Attributes
    ----------
    test_id : str
        Identifier shown in the parametrized test name.
    remote : str
        A URL as ``git remote get-url`` may report it.
    expected : str
        The https form the pull-request ref is fetched from.
    """

    test_id: str
    remote: str
    expected: str


REMOTE_URL_FIXTURES: list[RemoteURLFixture] = [
    RemoteURLFixture(
        "git_ssh_scheme", "git+ssh://git@github.com/o/n.git", "https://github.com/o/n"
    ),
    RemoteURLFixture(
        "ssh_scheme", "ssh://git@github.com/o/n.git", "https://github.com/o/n"
    ),
    RemoteURLFixture(
        "scp_shorthand", "git@github.com:o/n.git", "https://github.com/o/n"
    ),
    RemoteURLFixture(
        "https_dotgit", "https://github.com/o/n.git", "https://github.com/o/n"
    ),
    RemoteURLFixture("https_plain", "https://github.com/o/n", "https://github.com/o/n"),
    RemoteURLFixture(
        "self_hosted",
        "git@git.example.com:team/n.git",
        "https://git.example.com/team/n",
    ),
]


def test_preflight_accepts_a_server_that_answers_initialize(
    tmp_path: pathlib.Path,
) -> None:
    """A stdio server that replies to ``initialize`` passes preflight."""
    server = tmp_path / "server.py"
    server.write_text(
        "import json, sys\n"
        "line = sys.stdin.readline()\n"
        "req = json.loads(line)\n"
        'print(json.dumps({"jsonrpc": "2.0", "id": req["id"], "result": {}}))\n',
        encoding="utf-8",
    )
    spec = mcp_swap.McpServerSpec(command=sys.executable, args=[str(server)])

    assert build.preflight_spec(spec, timeout=60) is None


def test_preflight_reports_stderr_when_the_server_never_answers(
    tmp_path: pathlib.Path,
) -> None:
    """A server that dies is reported with the tail of its stderr."""
    server = tmp_path / "server.py"
    server.write_text(
        'import sys\nsys.stderr.write("could not resolve ref\\n")\nsys.exit(1)\n',
        encoding="utf-8",
    )
    spec = mcp_swap.McpServerSpec(command=sys.executable, args=[str(server)])

    assert build.preflight_spec(spec, timeout=60) == "could not resolve ref"


def test_preflight_reports_a_command_that_cannot_launch() -> None:
    """A missing binary is named rather than raising."""
    spec = mcp_swap.McpServerSpec(command="mcp-swap-no-such-binary", args=[])

    failure = build.preflight_spec(spec, timeout=60)

    assert failure is not None
    assert "mcp-swap-no-such-binary" in failure


def test_preflight_passes_spec_env_to_the_process(tmp_path: pathlib.Path) -> None:
    """``spec.env`` reaches the launched server.

    The cooldown bypass a prerelease branch needs travels this way, so a
    preflight that dropped it would reject a spec that works in an agent.
    """
    server = tmp_path / "server.py"
    server.write_text(
        "import json, os, sys\n"
        "req = json.loads(sys.stdin.readline())\n"
        'if os.environ.get("MCP_SWAP_PROBE") != "1":\n'
        "    sys.exit(2)\n"
        'print(json.dumps({"jsonrpc": "2.0", "id": req["id"], "result": {}}))\n',
        encoding="utf-8",
    )
    spec = mcp_swap.McpServerSpec(
        command=sys.executable, args=[str(server)], env={"MCP_SWAP_PROBE": "1"}
    )

    assert build.preflight_spec(spec, timeout=60) is None


# ---------------------------------------------------------------------------
# JSON writer fidelity
#
# An unmodified config must round-trip load_config -> dump_config_bytes
# byte-identical. Not guaranteed: indent width, CRLF, `\/` and `\uXXXX`
# escapes, duplicate keys, and number spelling (`1e5` -> `100000.0`) --
# none appear in real JSON-CLI output, and none change what a CLI reads.
# ---------------------------------------------------------------------------


class JSONFidelityCase(t.NamedTuple):
    """A JSON config body whose exact bytes survive a no-op rewrite.

    Attributes
    ----------
    test_id : str
        Identifier shown in the parametrized test name.
    body : str
        The config file's text, written to disk verbatim.
    """

    test_id: str
    body: str


PRESERVED_JSON: list[JSONFidelityCase] = [
    JSONFidelityCase(
        "mcp_servers_block",
        '{\n  "mcpServers": {\n    "tmux": {\n      "command": "uvx",\n'
        '      "args": [\n        "libtmux-mcp==0.1.0a2"\n      ]\n    }\n  }\n}\n',
    ),
    JSONFidelityCase(
        "non_ascii_model_label",
        '{\n  "model": "Fable 5 · Most capable…",\n  "mcpServers": {}\n}\n',
    ),
    JSONFidelityCase(
        "emoji_and_cjk", '{\n  "history": [\n    "🙂 日本語 café"\n  ]\n}\n'
    ),
    JSONFidelityCase("escaped_lone_surrogate", '{\n  "truncated": "\\ud800"\n}\n'),
    JSONFidelityCase("unsorted_keys", '{\n  "zeta": 1,\n  "alpha": 2\n}\n'),
    JSONFidelityCase(
        "claude_shape_without_trailing_newline",
        '{\n  "model": "Fable 5 · Most capable…",\n  "projects": {\n'
        '    "/home/someone/repo": {\n      "mcpServers": {}\n    }\n  }\n}',
    ),
]


def _json_config(tmp_path: pathlib.Path, body: str) -> tuple[t.Any, bytes]:
    """Write ``body`` verbatim and return its ``CLIInfo`` and exact bytes."""
    path = tmp_path / "config.json"
    raw = body.encode()
    path.write_bytes(raw)
    info = mcp_swap.CLIInfo(
        name="cursor",
        binary="cursor-agent",
        config_path=path,
        fmt="json",
        container=("mcpServers",),
        dialect="standard",
    )
    return info, raw


@pytest.mark.parametrize(
    JSONFidelityCase._fields,
    PRESERVED_JSON,
    ids=[c.test_id for c in PRESERVED_JSON],
)
def test_untouched_json_config_round_trips_byte_identical(
    tmp_path: pathlib.Path, test_id: str, body: str
) -> None:
    """Parsing a config and writing it back unmodified changes nothing.

    Every case is a shape the JavaScript agent CLIs actually emit:
    two-space indent, literal non-ASCII, escapes only below ``0x20`` plus
    lone surrogates, and no terminating newline.
    """
    assert test_id
    info, raw = _json_config(tmp_path, body)

    assert (
        mcp_swap.dump_config_bytes(info, mcp_swap.load_config(info), original=raw)
        == raw
    )


def test_dump_config_bytes_ends_a_seeded_file_with_a_newline(
    tmp_path: pathlib.Path,
) -> None:
    """With no original to match, a JSON config gets the conventional newline."""
    info, _ = _json_config(tmp_path, "")

    assert (
        mcp_swap.dump_config_bytes(info, {"mcpServers": {}}, original=b"")
        == b'{\n  "mcpServers": {}\n}\n'
    )


def test_dump_config_bytes_escapes_a_config_it_cannot_encode(
    tmp_path: pathlib.Path,
) -> None:
    r"""A lone surrogate has no UTF-8 form, so the document is escaped instead.

    JavaScript writes a string sliced through a surrogate pair as
    ``"\ud800"``, which parses to a Python string ``str.encode`` rejects.
    Escaping the whole document is what keeps the file writable at all.
    """
    config = {"truncated": "\ud800", "label": "café"}

    with pytest.raises(UnicodeEncodeError):
        json.dumps(config, indent=2, ensure_ascii=False).encode()

    info, _ = _json_config(tmp_path, "")
    written = mcp_swap.dump_config_bytes(info, config, original=b"")

    assert written == b'{\n  "truncated": "\\ud800",\n  "label": "caf\\u00e9"\n}\n'
    assert json.loads(written.decode()) == config


def test_swap_leaves_non_ascii_elsewhere_in_the_config_alone(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """A real swap does not re-escape config text it never read.

    Claude stores model labels and prompt history alongside the MCP
    entries, so escaping on write turns a one-entry edit into a diff
    spanning the file.
    """
    info = mcp_swap.CLIS["claude"]
    label = "Fable 5 · Most capable…"
    _write_json(
        info.config_path,
        {
            "model": label,
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()},
                    "history": ["café ☕"],
                }
            },
        },
    )
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 0

    after = info.config_path.read_text()
    assert f'"model": "{label}"' in after
    assert '"café ☕"' in after
    assert "\\u" not in after


def test_swap_does_not_append_a_newline_the_cli_never_wrote(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Claude's config has no trailing newline, and swapping must not add one."""
    info = mcp_swap.CLIS["claude"]
    body = json.dumps(
        {
            "projects": {
                str(fake_repo.resolve()): {
                    "mcpServers": {"tmux": _pinned_claude_entry()}
                }
            }
        },
        indent=2,
    )
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_text(body)

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "claude",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0

    assert not info.config_path.read_bytes().endswith(b"\n")


class UnreadableConfigCase(t.NamedTuple):
    """A config body that cannot be parsed, and the error it provokes.

    Attributes
    ----------
    test_id : str
        Identifier shown in the parametrized test name.
    body : bytes
        Exact bytes written to the config file.
    """

    test_id: str
    body: bytes


UNREADABLE_CONFIGS: list[UnreadableConfigCase] = [
    UnreadableConfigCase("malformed_json", b"{ this is not json"),
    UnreadableConfigCase("truncated_json", b'{"mcpServers": {'),
    UnreadableConfigCase("invalid_utf8", b'{"a": "\xff\xfe"}'),
]


@pytest.mark.parametrize(
    UnreadableConfigCase._fields,
    UNREADABLE_CONFIGS,
    ids=[c.test_id for c in UNREADABLE_CONFIGS],
)
def test_unreadable_config_reports_instead_of_crashing(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
    test_id: str,
    body: bytes,
) -> None:
    """A config that will not parse is reported and skipped, not raised through.

    ``load_config`` raises ``ValueError`` for every unparseable form —
    JSON, TOML and UTF-8 decode errors all derive from it — which the
    per-CLI handler has to catch for the run to survive one bad file.
    """
    assert test_id
    info = mcp_swap.CLIS["cursor"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_bytes(body)
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1

    assert "cursor" in capsys.readouterr().err
    assert info.config_path.read_bytes() == body


def test_unreadable_config_blocks_the_whole_selection(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """One bad config prevents every selected CLI from being mutated."""
    bad = mcp_swap.CLIS["cursor"]
    bad.config_path.parent.mkdir(parents=True, exist_ok=True)
    bad.config_path.write_bytes(b"{ not json")
    good = mcp_swap.CLIS["gemini"]
    _write_json(good.config_path, {"mcpServers": {}})
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--cli",
            "gemini",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1

    written = json.loads(good.config_path.read_text())
    assert "tmux" not in written["mcpServers"]


class CorruptStateCase(t.NamedTuple):
    """A swap-state file body that cannot yield entries.

    Attributes
    ----------
    test_id : str
        Identifier shown in the parametrized test name.
    body : str
        Exact text written to the state file.
    """

    test_id: str
    body: str


CORRUPT_STATE: list[CorruptStateCase] = [
    CorruptStateCase("not_json", "{ not json at all"),
    CorruptStateCase("empty_file", ""),
    CorruptStateCase("json_but_a_list", "[1, 2, 3]"),
    CorruptStateCase("entries_not_a_mapping", '{"entries": "nope"}'),
]


@pytest.mark.parametrize(
    CorruptStateCase._fields,
    CORRUPT_STATE,
    ids=[c.test_id for c in CORRUPT_STATE],
)
def test_corrupt_swap_state_is_reported_not_raised(
    fake_home: pathlib.Path,
    test_id: str,
    body: str,
) -> None:
    """A state file that yields no entries degrades to empty, never raises.

    ``revert`` and ``doctor`` both read this file before doing anything,
    so a hand-edited or truncated one would otherwise take down every
    command that consults it.
    """
    assert test_id
    mcp_swap.STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    mcp_swap.STATE_FILE.write_text(body, encoding="utf-8")

    assert mcp_swap.load_state() == {}


def test_unparseable_swap_state_names_the_file(
    fake_home: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """An unreadable state file says so, because backups are now orphaned.

    Returning empty silently would let ``revert`` report nothing to do
    while swapped configs and their backups sit on disk.
    """
    mcp_swap.STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    mcp_swap.STATE_FILE.write_text("{ not json", encoding="utf-8")

    mcp_swap.load_state()

    assert str(mcp_swap.STATE_FILE) in capsys.readouterr().err


def test_revert_survives_a_corrupt_state_file(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """``revert`` reports nothing to unwind rather than crashing."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {}})
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    mcp_swap.STATE_FILE.write_text("{ corrupted", encoding="utf-8")

    revert_args = mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])

    assert mcp_swap.cmd_revert(revert_args) in (0, 1)


def test_corrupt_state_blocks_a_new_swap_without_touching_the_config(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Unreadable recovery bookkeeping is never overwritten as empty state."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()
    corrupt = b"{ not json"
    mcp_swap.STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    mcp_swap.STATE_FILE.write_bytes(corrupt)
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1
    assert info.config_path.read_bytes() == original
    assert mcp_swap.STATE_FILE.read_bytes() == corrupt


def test_unwritable_directory_aborts_before_swapping(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A config whose backup cannot be written is left alone.

    The backup is the only copy of the pre-swap config, so a swap that
    could not take one would leave nothing to revert to. Aborting the
    CLI is the safe half of that trade.
    """
    if os.geteuid() == 0:
        pytest.skip("root ignores directory permissions")
    info = mcp_swap.CLIS["grok"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    original = '[mcp_servers.other]\ncommand = "x"\n'
    info.config_path.write_text(original, encoding="utf-8")
    info.config_path.parent.chmod(0o500)
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "grok",
        ]
    )

    try:
        assert mcp_swap.cmd_use_local(args) == 1
        assert "staging" in capsys.readouterr().err
        assert info.config_path.read_text() == original
    finally:
        info.config_path.parent.chmod(0o700)


def test_unwritable_directory_blocks_the_other_clis(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """One unstaged config prevents the transaction from changing any client."""
    if os.geteuid() == 0:
        pytest.skip("root ignores directory permissions")
    blocked = mcp_swap.CLIS["grok"]
    blocked.config_path.parent.mkdir(parents=True, exist_ok=True)
    blocked.config_path.write_text('[mcp_servers.o]\ncommand = "x"\n', encoding="utf-8")
    blocked.config_path.parent.chmod(0o500)
    reachable = mcp_swap.CLIS["cursor"]
    _write_json(reachable.config_path, {"mcpServers": {}})
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "grok",
            "--cli",
            "cursor",
        ]
    )

    try:
        assert mcp_swap.cmd_use_local(args) == 1
        written = json.loads(reachable.config_path.read_text())
        assert "tmux" not in written["mcpServers"]
    finally:
        blocked.config_path.parent.chmod(0o700)


def test_state_write_failure_leaves_the_config_unchanged(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A swap is not applied until its recovery record is durable."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()
    real_publish = mcp_swap._publish_absent

    def fail_state_write(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        if destination == mcp_swap.STATE_FILE:
            raise PermissionError("state is read-only")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_state_write)
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1
    assert info.config_path.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()


def test_swap_write_failure_keeps_recovery_state_without_raising(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A failed config write remains recoverable even when rollback also fails."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = info.config_path.read_bytes()
    target = info.config_path.resolve()
    real_publish = mcp_swap._publish_absent

    def fail_config_write(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        if destination == target:
            raise PermissionError("config is read-only")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_config_write)
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
        ]
    )

    assert mcp_swap.cmd_use_local(args) == 1
    recoveries = _transaction_residue(fake_home)
    assert not mcp_swap.STATE_FILE.exists()
    assert any(path.read_bytes() == original for path in recoveries)


def test_revert_write_failure_returns_failure_and_keeps_recovery_files(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """An unwritable destination does not crash or discard recovery material."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    state = mcp_swap.load_state()
    backup = pathlib.Path(state["cursor", "user"].backup_path)
    target = info.config_path.resolve()
    swapped = info.config_path.read_bytes()
    state_bytes = mcp_swap.STATE_FILE.read_bytes()
    real_publish = mcp_swap._publish_absent

    def fail_config_write(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        if destination == target:
            raise PermissionError("config is read-only")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_config_write)

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 1
    assert backup.exists()
    assert mcp_swap.STATE_FILE.read_bytes() == state_bytes
    assert any(path.read_bytes() == swapped for path in _transaction_residue(fake_home))


def test_revert_state_failure_keeps_recovery_files(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A restored config keeps its backup until state cleanup is durable."""
    info = mcp_swap.CLIS["cursor"]
    _write_json(info.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    parser = mcp_swap.build_parser()
    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    state_bytes = mcp_swap.STATE_FILE.read_bytes()
    backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    swapped = info.config_path.read_bytes()
    config_identity = (info.config_path.stat().st_dev, info.config_path.stat().st_ino)
    backup_identity = (backup.stat().st_dev, backup.stat().st_ino)
    state_identity = (
        mcp_swap.STATE_FILE.stat().st_dev,
        mcp_swap.STATE_FILE.stat().st_ino,
    )
    real_apply = mcp_swap._apply_replace

    def fail_state_update(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        if source == mcp_swap.STATE_FILE:
            raise PermissionError("state is read-only")
        return real_apply(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_apply_replace", fail_state_update)

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 1
    assert info.config_path.read_bytes() == swapped
    assert (
        info.config_path.stat().st_dev,
        info.config_path.stat().st_ino,
    ) == config_identity
    assert mcp_swap.STATE_FILE.read_bytes() == state_bytes
    assert (
        mcp_swap.STATE_FILE.stat().st_dev,
        mcp_swap.STATE_FILE.stat().st_ino,
    ) == state_identity
    assert backup.exists()
    assert (backup.stat().st_dev, backup.stat().st_ino) == backup_identity


def _build_symlink_chain(
    root: pathlib.Path, hops: int
) -> tuple[pathlib.Path, pathlib.Path, list[pathlib.Path]]:
    """Create ``hops`` links ending at an existing config file.

    Parameters
    ----------
    root : pathlib.Path
        Empty directory where the link and target trees are created.
    hops : int
        Number of links in the chain.

    Returns
    -------
    tuple of pathlib.Path, pathlib.Path, list of pathlib.Path
        Entry path, final target, and each link in the chain.
    """
    target = root / "dotfiles" / "mcp.json"
    target.parent.mkdir(parents=True)
    target.write_bytes(b"original\n")
    link_dir = root / "home"
    link_dir.mkdir()
    links: list[pathlib.Path] = []
    entry = target
    for hop in range(hops):
        link = link_dir / f"hop-{hop}.json"
        link.symlink_to(entry)
        links.append(link)
        entry = link
    return entry, target, links


@pytest.mark.parametrize("hops", [1, 3], ids=["single", "chain"])
def test_atomic_write_updates_the_symlink_target(
    tmp_path: pathlib.Path, hops: int
) -> None:
    """The final target receives the bytes and every link survives."""
    entry, target, links = _build_symlink_chain(tmp_path, hops)

    mcp_swap.atomic_write(entry, b"swapped\n")

    assert all(link.is_symlink() for link in links)
    assert target.read_bytes() == b"swapped\n"
    assert entry.read_bytes() == b"swapped\n"


def test_atomic_write_stages_beside_the_symlink_target(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The temp file shares the final target's filesystem for atomic rename."""
    entry, target, _links = _build_symlink_chain(tmp_path, 1)
    real_mkstemp = mcp_swap.tempfile.mkstemp
    staged_in: list[str | None] = []

    def recording_mkstemp(*args: t.Any, **kwargs: t.Any) -> tuple[int, str]:
        staged_in.append(kwargs.get("dir"))
        return t.cast(tuple[int, str], real_mkstemp(*args, **kwargs))

    monkeypatch.setattr(mcp_swap.tempfile, "mkstemp", recording_mkstemp)

    mcp_swap.atomic_write(entry, b"swapped\n")

    assert staged_in == [str(target.parent)]


def test_atomic_write_preserves_the_target_mode(tmp_path: pathlib.Path) -> None:
    """Replacing a config does not silently narrow its permission bits."""
    target = tmp_path / "mcp.json"
    target.write_bytes(b"original\n")
    target.chmod(0o640)

    mcp_swap.atomic_write(target, b"swapped\n")

    assert target.stat().st_mode & 0o777 == 0o640


def test_symlinked_config_swap_and_revert_round_trip(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Swap and revert update the target without replacing the config link."""
    info = mcp_swap.CLIS["cursor"]
    target = fake_home / "dotfiles" / "cursor" / "mcp.json"
    _write_json(target, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = target.read_bytes()
    info.config_path.parent.mkdir(parents=True)
    info.config_path.symlink_to(target)
    parser = mcp_swap.build_parser()

    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    state = mcp_swap.load_state()["cursor", "user"]
    backup = pathlib.Path(state.backup_path)
    assert info.config_path.is_symlink()
    assert backup.parent == info.config_path.parent
    assert json.loads(target.read_text())["mcpServers"]["tmux"]["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 0
    assert info.config_path.is_symlink()
    assert target.read_bytes() == original
    assert not backup.exists()


@pytest.mark.parametrize("replacement_kind", ["symlink", "file"])
def test_revert_refuses_when_a_config_link_is_replaced(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    replacement_kind: str,
) -> None:
    """Repointing or replacing a link cannot redirect recovery into a new file."""
    info = mcp_swap.CLIS["cursor"]
    original_target = fake_home / "dotfiles" / "original.json"
    new_target = fake_home / "dotfiles" / "replacement.json"
    _write_json(original_target, {"mcpServers": {"tmux": _pinned_json_entry()}})
    _write_json(new_target, {"sentinel": "leave me alone"})
    replacement = new_target.read_bytes()
    info.config_path.parent.mkdir(parents=True)
    info.config_path.symlink_to(original_target)
    parser = mcp_swap.build_parser()

    assert (
        mcp_swap.cmd_use_local(
            parser.parse_args(
                [
                    "use",
                    "--no-build",
                    "--no-preflight",
                    "--repo",
                    str(fake_repo),
                    "--cli",
                    "cursor",
                ]
            )
        )
        == 0
    )
    swapped = original_target.read_bytes()
    info.config_path.unlink()
    if replacement_kind == "symlink":
        info.config_path.symlink_to(new_target)
        replacement_path = new_target
    else:
        info.config_path.write_bytes(replacement)
        replacement_path = info.config_path

    assert mcp_swap.cmd_revert(parser.parse_args(["revert", "--cli", "cursor"])) == 1
    assert original_target.read_bytes() == swapped
    assert replacement_path.read_bytes() == replacement
    assert mcp_swap.STATE_FILE.exists()


# ---------------------------------------------------------------------------
# opencode and pi
#
# opencode adds JSONC, a non-mcpServers container key, and an argv-array
# dialect; pi's config is read by an extension, not the agent itself.
# ---------------------------------------------------------------------------


def test_fake_home_covers_every_registered_cli(fake_home: pathlib.Path) -> None:
    """``fake_home`` replaces ``CLIS`` wholesale, so it must list every CLI.

    Regression guard rather than a behavior test. ``_config_present_clis``
    iterates ``ALL_CLIS`` while indexing ``CLIS``, so a CLI added to the
    registry but not to this fixture raises ``KeyError`` from half a dozen
    unrelated doctor and naming-hint tests. Naming the invariant here turns
    that into one obvious failure.
    """
    assert set(mcp_swap.CLIS) == set(mcp_swap.ALL_CLIS)


@pytest.mark.parametrize("raw", ["relcfg", "", "  ", "./cfg"])
def test_relative_xdg_config_home_is_ignored(
    raw: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Regression: a relative XDG_CONFIG_HOME resolved against the cwd.

    The spec requires these to be absolute and to be ignored otherwise.
    Honouring a relative one made opencode's config -- and the backup path
    recorded for it -- depend on where the swap was run from, so revert
    from any other directory reported the backup missing for good.
    """
    monkeypatch.setenv("XDG_CONFIG_HOME", raw)
    assert xdg.config_home() == pathlib.Path.home() / ".config"


def test_absolute_xdg_config_home_is_honoured(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Opencode resolves XDG the way its own loader does."""
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    assert xdg.config_home() == tmp_path


def test_opencode_and_pi_registered() -> None:
    """Both new CLIs are first-class ``--cli`` choices with their own shapes."""
    assert "opencode" in mcp_swap.ALL_CLIS
    assert "pi" in mcp_swap.ALL_CLIS
    opencode = mcp_swap.CLIS["opencode"]
    assert opencode.fmt == "jsonc"
    assert opencode.config_path.name == "opencode.jsonc"
    assert opencode.container == ("mcp",)
    assert opencode.dialect == "opencode"
    pi = mcp_swap.CLIS["pi"]
    assert pi.fmt == "jsonc"
    assert pi.config_path.name == "mcp.json"
    assert pi.container == ("mcpServers",)
    assert pi.dialect == "standard"
    parser = mcp_swap.build_parser()
    assert parser.parse_args(["status", "--cli", "opencode"]).cli == ["opencode"]
    assert parser.parse_args(["status", "--cli", "pi"]).cli == ["pi"]


@pytest.mark.parametrize("cli", ["opencode", "pi"])
def test_new_cli_set_get_delete_roundtrip(cli: str, fake_repo: pathlib.Path) -> None:
    """Each new CLI's four container branches agree with one another.

    Proves the name was threaded through ``get_server``, ``set_server``,
    ``delete_server`` and ``_all_server_specs`` rather than falling through
    to another CLI's container key.
    """
    config: dict[str, t.Any] = {}
    spec = mcp_swap.McpServerSpec(
        command=str(build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug"))
    )
    assert mcp_swap.set_server(cli, config, "tmux", spec, fake_repo) == "added"
    assert mcp_swap.CLIS[cli].container[0] in config
    got = mcp_swap.get_server(cli, config, "tmux", fake_repo)
    assert got is not None
    assert got.local_repo_path() == fake_repo
    assert got.local_repo_path() == fake_repo
    assert mcp_swap.set_server(cli, config, "tmux", spec, fake_repo) == "replaced"
    assert mcp_swap._all_server_specs(cli, config, fake_repo).keys() == {"tmux"}
    assert mcp_swap.delete_server(cli, config, "tmux", fake_repo)
    assert mcp_swap.get_server(cli, config, "tmux", fake_repo) is None


def test_opencode_entry_packs_argv_into_one_command_array(
    fake_repo: pathlib.Path,
) -> None:
    """The opencode dialect uses one argv array and the key ``environment``.

    A scalar ``command`` is a decode error that stops opencode starting at
    all, and an ``env`` key is dropped without a warning, so both spellings
    are pinned here rather than left to the round-trip tests.
    """
    spec = mcp_swap.McpServerSpec(
        command="uv", args=["--directory", "/repo", "run"], env={"A": "b"}
    )
    entry = spec.to_entry_dict("opencode")
    assert entry["type"] == "local"
    assert entry["command"] == ["uv", "--directory", "/repo", "run"]
    assert entry["environment"] == {"A": "b"}
    assert "args" not in entry
    assert "env" not in entry


def test_opencode_array_entry_reads_back_as_command_plus_args() -> None:
    """An array ``command`` normalizes to the portable scalar-plus-args spec.

    Regression: without the split, ``command`` becomes the ``str()`` of a
    Python list, the repo behind the entry cannot be recovered, and the
    "already ... — no change" short-circuit never fires, so every run
    rewrites a config that needed no change.
    """
    info = mcp_swap.CLIS["opencode"]
    argv = [
        "/usr/bin/dotnet",
        "run",
        "--project",
        "/repo/src/LibTmux.Mcp/LibTmux.Mcp.csproj",
        "--framework",
        "net10.0",
        "--configuration",
        "Debug",
        "--",
    ]
    spec = mcp_swap._spec_from_entry(
        {"type": "local", "command": argv, "environment": {"A": "b"}},
        info=info,
    )
    assert spec.command == "/usr/bin/dotnet"
    assert spec.args == argv[1:]
    assert spec.env == {"A": "b"}
    assert spec.local_repo_path() == pathlib.Path("/repo")


def _opencode_config(fake_home: pathlib.Path, body: str) -> t.Any:
    """Write ``body`` to the fake opencode config and return its ``CLIInfo``."""
    info = mcp_swap.CLIS["opencode"]
    info.config_path.parent.mkdir(parents=True, exist_ok=True)
    info.config_path.write_text(body)
    return info


def _swap_opencode(fake_repo: pathlib.Path) -> int:
    """Run ``use`` against opencode only."""
    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--no-build",
            "--no-preflight",
            "--repo",
            str(fake_repo),
            "--cli",
            "opencode",
        ]
    )
    return int(mcp_swap.cmd_use_local(args))


def test_opencode_swap_preserves_jsonc_comments(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Line comments, block comments and sibling servers survive a swap."""
    info = _opencode_config(
        fake_home,
        "{\n"
        "  // header comment\n"
        '  "$schema": "https://opencode.ai/config.json",\n'
        "  /* a block comment\n"
        "     spanning lines */\n"
        '  "model": "openrouter/x",\n'
        '  "mcp": {\n'
        '    "other": { "type": "local", "command": ["echo", "keep"] }\n'
        "  }\n"
        "}\n",
    )
    assert _swap_opencode(fake_repo) == 0
    text = info.config_path.read_text()
    assert "// header comment" in text
    assert "/* a block comment" in text
    assert "spanning lines */" in text
    doc = jsonc.loads(text)
    assert doc["model"] == "openrouter/x"
    assert doc["mcp"]["other"]["command"] == ["echo", "keep"]
    assert doc["mcp"]["tmux"]["command"][0].endswith("LibTmux.Mcp")


def test_opencode_comment_inside_the_replaced_entry_survives(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """A comment attached to the entry being rewritten is not collateral.

    The case a whole-entry rewrite loses and a field-level splice keeps.
    Real opencode configs carry the rationale for a pinned ``command``
    directly above it, which is exactly the text a swap would destroy.
    """
    info = _opencode_config(
        fake_home,
        "{\n"
        '  "mcp": {\n'
        '    "tmux": {\n'
        '      "type": "local",\n'
        "      // Pinned deliberately; this rationale must outlive the swap.\n"
        '      "command": ["uvx", "libtmux-mcp==0.1.0a2"],\n'
        '      "environment": { "KEEP": "me" }\n'
        "    }\n"
        "  }\n"
        "}\n",
    )
    assert _swap_opencode(fake_repo) == 0
    text = info.config_path.read_text()
    assert "// Pinned deliberately; this rationale must outlive the swap." in text
    entry = jsonc.loads(text)["mcp"]["tmux"]
    assert entry["command"][0].endswith("LibTmux.Mcp")
    assert entry["environment"] == _runtime_env(fake_repo) | {"KEEP": "me"}


def test_opencode_swap_and_revert_round_trip_is_byte_identical(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Revert restores a commented JSONC config byte for byte."""
    body = (
        "{\n"
        "  // keep me\n"
        '  "model": "m",\n'
        '  "mcp": {\n'
        '    "tmux": {\n'
        '      "type": "local",\n'
        '      "command": ["uvx", "libtmux-mcp==0.1.0a2"]\n'
        "    }\n"
        "  }\n"
        "}\n"
    )
    info = _opencode_config(fake_home, body)
    original = info.config_path.read_bytes()
    assert _swap_opencode(fake_repo) == 0
    assert info.config_path.read_bytes() != original
    revert = mcp_swap.build_parser().parse_args(["revert", "--cli", "opencode"])
    assert mcp_swap.cmd_revert(revert) == 0
    assert info.config_path.read_bytes() == original


def test_opencode_second_swap_reports_no_change(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """The idempotence check fires for the array command shape.

    Depends on ``_spec_from_entry`` splitting the array; without it the
    config is rewritten on every invocation.
    """
    info = _opencode_config(fake_home, '{\n  "mcp": {}\n}\n')
    assert _swap_opencode(fake_repo) == 0
    after_first = info.config_path.read_bytes()
    assert _swap_opencode(fake_repo) == 0
    assert info.config_path.read_bytes() == after_first


def test_opencode_seeds_schema_into_an_empty_config(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Seeding an empty file writes ``$schema`` alongside the server entry."""
    info = _opencode_config(fake_home, "")
    assert _swap_opencode(fake_repo) == 0
    doc = jsonc.loads(info.config_path.read_text())
    assert doc["$schema"] == mcp_swap.OPENCODE_SCHEMA_URL
    assert doc["mcp"]["tmux"]["type"] == "local"


def test_opencode_symlinked_config_swap_updates_target_not_link(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """A JSONC config symlinked into a dotfiles tree keeps its link."""
    info = mcp_swap.CLIS["opencode"]
    target = fake_home / "dotfiles" / "opencode.jsonc"
    target.parent.mkdir(parents=True)
    target.write_text('{\n  // linked\n  "mcp": {}\n}\n')
    info.config_path.parent.mkdir(parents=True)
    info.config_path.symlink_to(target)

    assert _swap_opencode(fake_repo) == 0
    assert info.config_path.is_symlink()
    assert info.config_path.readlink() == target
    text = target.read_text()
    assert "// linked" in text
    assert jsonc.loads(text)["mcp"]["tmux"]["command"][0].endswith("LibTmux.Mcp")


def test_pi_config_with_comments_is_readable(
    fake_home: pathlib.Path, fake_repo: pathlib.Path
) -> None:
    """Regression: pi's adapter accepts JSONC, so strict JSON rejected it.

    ``pi-mcp-adapter`` reads the file through ``strip-json-comments`` with
    trailing commas allowed. Parsing it as strict JSON made ``status`` and
    ``use`` report a config the adapter reads fine as unreadable.
    """
    info = mcp_swap.CLIS["pi"]
    info.config_path.parent.mkdir(parents=True)
    info.config_path.write_text(
        '{\n  // the adapter allows comments\n  "mcpServers": {\n'
        '    "keep": { "command": "echo", "args": ["hi"] },\n  }\n}\n'
    )
    args = mcp_swap.build_parser().parse_args(
        ["use", "--no-build", "--no-preflight", "--repo", str(fake_repo), "--cli", "pi"]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    text = info.config_path.read_text()
    assert "// the adapter allows comments" in text
    servers = jsonc.loads(text)["mcpServers"]
    assert servers["keep"]["command"] == "echo"
    assert servers["tmux"]["command"] == str(
        build.profile_binary(fake_repo, "LibTmux.Mcp", "Debug")
    )


def test_detect_reports_the_pi_adapter_prerequisite(
    fake_home: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """``detect`` says why a pi swap will not take effect on its own.

    pi ships no MCP client, so the file this script writes is read only by
    the ``pi-mcp-adapter`` extension. Reporting pi as swappable without
    that caveat would be the one thing this script must never do: claim an
    agent will run something it will not.
    """
    monkeypatch.setattr(mcp_swap, "PI_ADAPTER_DIR", fake_home / "absent")
    monkeypatch.setattr(mcp_swap.shutil, "which", lambda _binary: "/usr/bin/stub")
    info = mcp_swap.CLIS["pi"]
    info.config_path.parent.mkdir(parents=True)
    info.config_path.write_text('{"mcpServers": {}}\n')

    assert mcp_swap.cmd_detect(mcp_swap.build_parser().parse_args(["detect"])) == 0
    out = capsys.readouterr().out
    assert mcp_swap.PI_ADAPTER_HINT in out

    monkeypatch.setattr(mcp_swap, "PI_ADAPTER_DIR", fake_home)
    assert mcp_swap.cmd_detect(mcp_swap.build_parser().parse_args(["detect"])) == 0
    assert mcp_swap.PI_ADAPTER_HINT not in capsys.readouterr().out


# ---------------------------------------------------------------------------
# JSONC writer fidelity
#
# Unlike the JSON writer (values only), the JSONC splice writer promises
# byte-identical untouched regions, including comments and formatting;
# string-aware scanning matters because a naive stripper corrupts
# URLs/paths silently.
# ---------------------------------------------------------------------------


PRESERVED_JSONC: list[JSONFidelityCase] = [
    JSONFidelityCase("line_comment", '{\n  // note\n  "mcp": {}\n}\n'),
    JSONFidelityCase("block_comment", '{\n  /* note\n     more */\n  "mcp": {}\n}\n'),
    JSONFidelityCase("comment_after_last_member", '{\n  "mcp": {}\n  // tail\n}\n'),
    JSONFidelityCase("trailing_comma", '{\n  "mcp": {},\n}\n'),
    JSONFidelityCase("no_trailing_newline", '{\n  "mcp": {}\n}'),
    JSONFidelityCase("four_space_indent", '{\n    "mcp": {}\n}\n'),
    JSONFidelityCase("url_containing_double_slash", '{\n  "a": "https://x/y//z"\n}\n'),
    JSONFidelityCase("block_marker_inside_string", '{\n  "a": "/* not one */"\n}\n'),
    JSONFidelityCase("windows_path", '{\n  "a": "C:\\\\tmp\\\\x"\n}\n'),
    JSONFidelityCase("literal_backslash_u", '{\n  "a": "C:\\\\u0041"\n}\n'),
    JSONFidelityCase("emoji_and_cjk", '{\n  "a": "🙂 日本語 café"\n}\n'),
    JSONFidelityCase("empty_object", "{}\n"),
    JSONFidelityCase("comment_only_object", '{\n  "mcp": {\n    // none yet\n  }\n}\n'),
    JSONFidelityCase(
        "comment_before_the_delimiter", '{\n  "a": 1 /* x */,\n  "b": 2\n}\n'
    ),
]


def _jsonc_config(tmp_path: pathlib.Path, body: str) -> tuple[t.Any, bytes]:
    """Write ``body`` verbatim and return its ``CLIInfo`` and exact bytes."""
    path = tmp_path / "opencode.jsonc"
    path.write_text(body)
    info = mcp_swap.CLIInfo(
        name="opencode",
        binary="opencode",
        config_path=path,
        fmt="jsonc",
        container=("mcp",),
        dialect="opencode",
    )
    return info, path.read_bytes()


@pytest.mark.parametrize(
    JSONFidelityCase._fields,
    PRESERVED_JSONC,
    ids=[c.test_id for c in PRESERVED_JSONC],
)
def test_untouched_jsonc_config_round_trips_byte_identical(
    test_id: str, body: str, tmp_path: pathlib.Path
) -> None:
    """Loading and rewriting an unmodified JSONC config changes no byte."""
    assert test_id
    info, raw = _jsonc_config(tmp_path, body)
    config = mcp_swap.load_config(info)
    assert mcp_swap.dump_config_bytes(info, config, original=raw) == raw


@pytest.mark.parametrize(
    JSONFidelityCase._fields,
    PRESERVED_JSONC,
    ids=[c.test_id for c in PRESERVED_JSONC],
)
def test_jsonc_values_match_stdlib_json(
    test_id: str, body: str, tmp_path: pathlib.Path
) -> None:
    r"""JSONC parsing agrees with stdlib json wherever stdlib can parse.

    Escape handling is the standard library's, not a reimplementation's.
    The rejected ``json-five`` dependency failed exactly here: it raised on
    ``"C:\\x"`` and decoded a literal ``\\u0041`` to ``"A"``.
    """
    assert test_id
    try:
        expected = json.loads(body)
    except json.JSONDecodeError:
        pytest.skip("comment or trailing comma — stdlib cannot parse it")
    assert jsonc.loads(body) == expected


def test_jsonc_config_is_not_written_through_the_toml_writer(
    tmp_path: pathlib.Path,
) -> None:
    """A jsonc config comes back as JSON text, not TOML.

    Regression: ``dump_config_bytes`` branched on ``fmt != "json"``, so any
    third format reached ``tomlkit.dumps`` and put TOML bytes in a JSON
    file. The dispatch is on the exact format now.
    """
    info, raw = _jsonc_config(tmp_path, '{\n  "mcp": {}\n}\n')
    out = mcp_swap.dump_config_bytes(
        info, {"mcp": {"x": {"type": "local"}}}, original=raw
    )
    text = out.decode()
    assert text.lstrip().startswith("{")
    assert jsonc.loads(text)["mcp"]["x"]["type"] == "local"


class JsoncDeletionCase(t.NamedTuple):
    """A member removal whose exact resulting text is pinned.

    Attributes
    ----------
    test_id : str
        Identifier shown in the parametrized test name.
    body : str
        The config text before the merge.
    data : dict[str, t.Any]
        The reconciled data the merge is driven with.
    expected : str
        The exact text the merge must produce.
    """

    test_id: str
    body: str
    data: dict[str, t.Any]
    expected: str


JSONC_DELETIONS: list[JsoncDeletionCase] = [
    JsoncDeletionCase(
        "first_member",
        '{\n  "a": 1,\n  "b": 2\n}\n',
        {"b": 2},
        '{\n  "b": 2\n}\n',
    ),
    JsoncDeletionCase(
        "middle_member",
        '{\n  "a": 1,\n  "b": 2,\n  "c": 3\n}\n',
        {"a": 1, "c": 3},
        '{\n  "a": 1,\n  "c": 3\n}\n',
    ),
    JsoncDeletionCase(
        "last_member",
        '{\n  "a": 1,\n  "b": 2\n}\n',
        {"a": 1},
        '{\n  "a": 1\n}\n',
    ),
    JsoncDeletionCase(
        "comma_hidden_behind_a_comment",
        '{\n  "a": 1 /* x, y */,\n  "b": 2\n}\n',
        {"b": 2},
        '{\n  "b": 2\n}\n',
    ),
]


@pytest.mark.parametrize(
    JsoncDeletionCase._fields,
    JSONC_DELETIONS,
    ids=[c.test_id for c in JSONC_DELETIONS],
)
def test_jsonc_merge_removing_a_member_takes_exactly_one_comma(
    test_id: str, body: str, data: dict[str, t.Any], expected: str
) -> None:
    """Regression: a removal took the comma on both sides of the member.

    Deleting a member between two others left its neighbours undelimited,
    so the next merge pass raised ``JSONDecodeError`` and the swap reported
    the config unreadable. ``comma_hidden_behind_a_comment`` covers the
    partner defect: the delimiter scan read the raw text, where a comma
    inside a comment passes for the separator.
    """
    assert test_id
    assert jsonc.merge(body, data, ensure_ascii=False) == expected


@pytest.mark.parametrize(
    "name", ["back\\slash", 'quo"te', "new\nline", "tab\tbed", "unicode\u00e9"]
)
def test_jsonc_merge_escapes_an_inserted_key(name: str) -> None:
    """Regression: an inserted key was written raw, so a swap could not converge.

    ``--server`` takes an arbitrary string. Written unescaped, a backslash or
    quote in it emitted text that would not parse back, so the member was
    never found again and the merge re-inserted it until the pass ceiling --
    spinning while holding the swap lock and then failing.
    """
    src = '{\n  "mcp": {}\n}\n'
    data = jsonc.loads(src)
    data["mcp"][name] = {"type": "local"}
    out = jsonc.merge(src, data, ensure_ascii=False)
    assert jsonc.loads(out)["mcp"][name] == {"type": "local"}


def test_jsonc_merge_removing_a_middle_member_stays_parseable() -> None:
    """The shape that surfaced it: an opencode entry losing optional fields."""
    src = (
        '{\n  "mcp": {\n    "tmux": {\n      "type": "local",\n'
        '      "enabled": true,\n      "timeout": 5000,\n'
        '      "command": ["uvx", "old"]\n    }\n  }\n}\n'
    )
    data = jsonc.loads(src)
    data["mcp"]["tmux"] = {"type": "local", "command": ["uv", "run", "x"]}
    out = jsonc.merge(src, data, ensure_ascii=False)
    assert jsonc.loads(out) == data


def test_jsonc_merge_inserting_into_a_comment_only_object_keeps_the_comment() -> None:
    """Regression: blanking made a documented object look empty.

    The emptiness guard reads the comment-blanked text, where a comment is
    indistinguishable from whitespace, so insertion used to splice over the
    whole interior and take the comment with it.
    """
    src = '{\n  "mcp": {\n    // why there are no servers yet\n  }\n}\n'
    data = jsonc.loads(src)
    data["mcp"]["tmux"] = {"type": "local", "command": ["uv"]}
    out = jsonc.merge(src, data, ensure_ascii=False)
    assert out == (
        '{\n  "mcp": {\n    // why there are no servers yet\n'
        '    "tmux": {\n      "type": "local",\n      "command": [\n'
        '        "uv"\n      ]\n    }\n  }\n}\n'
    )


def test_jsonc_merge_inserting_into_a_comment_only_document_keeps_the_comment() -> None:
    """The same splice at the root, where there is no enclosing member."""
    src = "{\n  // root rationale\n}\n"
    data = jsonc.loads(src)
    data["mcp"] = {}
    out = jsonc.merge(src, data, ensure_ascii=False)
    assert out == '{\n  // root rationale\n  "mcp": {}\n}\n'


@pytest.mark.parametrize(
    "body",
    ["{}\n", "{ }\n", '{\n  "mcp": {}\n}\n', '{\n  "mcp": {\n  }\n}\n'],
)
def test_jsonc_merge_inserting_into_an_empty_object_is_unchanged(body: str) -> None:
    """A genuinely empty interior still collapses to the old splice point."""
    data = jsonc.loads(body)
    data.setdefault("mcp", {})["tmux"] = {"type": "local"}
    out = jsonc.merge(body, data, ensure_ascii=False)
    assert jsonc.loads(out)["mcp"]["tmux"] == {"type": "local"}
    assert out.rstrip().endswith("}")


def test_jsonc_comment_blanking_preserves_offsets() -> None:
    """Blanking a comment must not move the bytes around it.

    Offsets are what let a span found in the blanked text address the same
    bytes in the original; if blanking changed the length, every splice
    would land in the wrong place.
    """
    src = '{\n  // note\n  "a": 1, /* x */\n  "b": "//not a comment"\n}\n'
    blanked = jsonc.blank_comments(src)
    assert len(blanked) == len(src)
    assert "//not a comment" in blanked
    assert "note" not in blanked
    assert blanked.count("\n") == src.count("\n")


# ---------------------------------------------------------------------------
# Whole-selection transaction and authenticated recovery
# ---------------------------------------------------------------------------


def _use_args(
    fake_repo: pathlib.Path, *clis: str, extra: list[str] | None = None
) -> argparse.Namespace:
    argv = [
        "use",
        "--no-build",
        "--no-preflight",
        "--repo",
        str(fake_repo),
    ]
    for cli in clis:
        argv += ["--cli", cli]
    argv += extra or []
    return mcp_swap.build_parser().parse_args(argv)


def _transaction_residue(root: pathlib.Path) -> list[pathlib.Path]:
    return [
        path
        for path in root.rglob("*")
        if path.is_file() and path.name.startswith(".") and ".mcp-swap-" in path.name
    ]


def _transaction_quarantines(root: pathlib.Path) -> list[pathlib.Path]:
    return [
        path
        for path in root.rglob("*")
        if path.is_dir() and ".mcp-swap-retained-" in path.name
    ]


def test_use_plans_every_selected_config_before_setup_or_mutation(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A late planning error prevents build, launch, and earlier config writes."""
    cursor = mcp_swap.CLIS["cursor"].config_path
    gemini = mcp_swap.CLIS["gemini"].config_path
    _write_json(cursor, {"mcpServers": {"tmux": _pinned_json_entry()}})
    gemini.parent.mkdir(parents=True)
    gemini.write_bytes(b"{ malformed")
    original = cursor.read_bytes()
    calls: list[str] = []
    monkeypatch.setattr(build, "dotnet_build", lambda *_args: calls.append("build"))
    monkeypatch.setattr(
        build, "preflight_spec", lambda *_args, **_kwargs: calls.append("preflight")
    )

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--source",
            "release",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--cli",
            "gemini",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 1
    assert calls == []
    assert cursor.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()


def test_use_dry_run_never_builds_launches_or_writes(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Dry-run validates and renders every target without external side effects."""
    cursor = mcp_swap.CLIS["cursor"].config_path
    _write_json(cursor, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = cursor.read_bytes()
    calls: list[str] = []
    monkeypatch.setattr(build, "dotnet_build", lambda *_args: calls.append("build"))
    monkeypatch.setattr(
        build, "install_published", lambda *_args: calls.append("install")
    )
    monkeypatch.setattr(
        build, "preflight_spec", lambda *_args, **_kwargs: calls.append("preflight")
    )

    args = mcp_swap.build_parser().parse_args(
        [
            "use",
            "--source",
            "release",
            "--repo",
            str(fake_repo),
            "--cli",
            "cursor",
            "--dry-run",
        ]
    )
    assert mcp_swap.cmd_use_local(args) == 0
    assert calls == []
    assert cursor.read_bytes() == original
    assert not mcp_swap.STATE_DIR.exists()


def test_use_rejects_duplicate_physical_config_targets_before_writing(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Two client names cannot make one physical config run twice."""
    cursor = mcp_swap.CLIS["cursor"]
    _write_json(cursor.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = cursor.config_path.read_bytes()
    monkeypatch.setitem(
        mcp_swap.CLIS,
        "gemini",
        dataclasses.replace(mcp_swap.CLIS["gemini"], config_path=cursor.config_path),
    )

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", "gemini")) == 1
    assert "duplicate physical config target" in capsys.readouterr().err
    assert cursor.config_path.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()


def test_use_rejects_config_backup_cross_alias_before_writing(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A client's config cannot be another client's planned backup path."""
    cursor = mcp_swap.CLIS["cursor"]
    _write_json(cursor.config_path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    alias = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    monkeypatch.setitem(
        mcp_swap.CLIS,
        "gemini",
        dataclasses.replace(mcp_swap.CLIS["gemini"], config_path=alias),
    )
    before = {
        cursor.config_path: cursor.config_path.read_bytes(),
        alias: alias.read_bytes(),
    }
    state = mcp_swap.STATE_FILE.read_bytes()

    assert (
        mcp_swap.cmd_use_local(
            _use_args(
                fake_repo,
                "cursor",
                "gemini",
                extra=["--env", "LIBTMUX_SOCKET=changed"],
            )
        )
        == 1
    )
    assert "duplicate transaction destination" in capsys.readouterr().err
    assert {path: path.read_bytes() for path in before} == before
    assert mcp_swap.STATE_FILE.read_bytes() == state


def test_late_use_failure_rolls_back_every_selected_client(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Failure while committing the final config restores the whole selection."""
    paths = [mcp_swap.CLIS[name].config_path for name in ("cursor", "gemini")]
    for path in paths:
        _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    originals = {path: path.read_bytes() for path in paths}
    real_publish = mcp_swap._publish_absent
    failed = False

    def fail_final_config(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal failed
        if destination == paths[-1].resolve() and not failed:
            failed = True
            raise PermissionError("synthetic final config failure")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_final_config)
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", "gemini")) == 1
    assert {path: path.read_bytes() for path in paths} == originals
    assert not mcp_swap.STATE_FILE.exists()
    assert _transaction_residue(fake_home) == []


def test_revert_refuses_a_human_edit_and_keeps_recovery(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Revert never overwrites config bytes it did not authenticate."""
    path = mcp_swap.CLIS["cursor"].config_path
    _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    entry = mcp_swap.load_state()["cursor", "user"]
    backup = pathlib.Path(entry.backup_path)
    human = b'{"human": "keep"}\n'
    path.write_bytes(human)

    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 1
    )
    assert path.read_bytes() == human
    assert backup.exists()
    assert mcp_swap.STATE_FILE.exists()


def test_revert_refuses_a_same_path_inode_replacement(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Byte-identical replacement does not inherit ownership from the old inode."""
    path = mcp_swap.CLIS["cursor"].config_path
    _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    swapped = path.read_bytes()
    prior_inode = path.stat().st_ino
    replacement = path.with_name("replacement.json")
    replacement.write_bytes(swapped)
    replacement.chmod(path.stat().st_mode & 0o777)
    os.replace(replacement, path)
    assert path.stat().st_ino != prior_inode

    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 1
    )
    assert path.read_bytes() == swapped
    assert mcp_swap.STATE_FILE.exists()


def test_revert_refuses_a_config_symlink_retarget(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """A retargeted logical config link cannot redirect or authorize recovery."""
    info = mcp_swap.CLIS["cursor"]
    first = fake_home / "dotfiles" / "first.json"
    second = fake_home / "dotfiles" / "second.json"
    _write_json(first, {"mcpServers": {"tmux": _pinned_json_entry()}})
    _write_json(second, {"human": "keep"})
    info.config_path.parent.mkdir(parents=True)
    info.config_path.symlink_to(first)
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    swapped = first.read_bytes()
    info.config_path.unlink()
    info.config_path.symlink_to(second)
    human = second.read_bytes()

    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 1
    )
    assert first.read_bytes() == swapped
    assert second.read_bytes() == human
    assert info.config_path.is_symlink()
    assert mcp_swap.STATE_FILE.exists()


def test_state_checksum_tampering_blocks_revert_without_writes(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Changing a recovery record without its checksum invalidates the transaction."""
    path = mcp_swap.CLIS["cursor"].config_path
    _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    swapped = path.read_bytes()
    state = json.loads(mcp_swap.STATE_FILE.read_text())
    state["entries"]["cursor:user"]["server"] = "other"
    mcp_swap.STATE_FILE.write_text(json.dumps(state))
    corrupt = mcp_swap.STATE_FILE.read_bytes()

    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 1
    )
    assert path.read_bytes() == swapped
    assert mcp_swap.STATE_FILE.read_bytes() == corrupt


def test_successful_use_and_revert_leave_no_transaction_stages(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Committed transactions consume every temporary recovery stage."""
    path = mcp_swap.CLIS["cursor"].config_path
    _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = path.read_bytes()
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    assert _transaction_residue(fake_home) == []
    assert _transaction_quarantines(fake_home) == []
    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 0
    )
    assert path.read_bytes() == original
    assert _transaction_residue(fake_home) == []
    assert _transaction_quarantines(fake_home) == []


def _path_identity(path: pathlib.Path) -> tuple[int, int, int, bytes]:
    details = path.stat()
    return (
        details.st_dev,
        details.st_ino,
        details.st_mode & 0o777,
        path.read_bytes(),
    )


@pytest.mark.parametrize("alias_kind", ["symlink", "hardlink"])
@pytest.mark.parametrize("dry_run", [False, True], ids=["use", "dry-run"])
def test_config_alias_to_swap_lock_is_rejected(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    alias_kind: str,
    dry_run: bool,
) -> None:
    """A selected config cannot name the persistent transaction lock."""
    config = mcp_swap.CLIS["cursor"].config_path
    lock = mcp_swap.STATE_DIR / "state.lock"
    lock.parent.mkdir(parents=True)
    _write_json(lock, {"mcpServers": {"tmux": _pinned_json_entry()}})
    lock.chmod(0o600)
    config.parent.mkdir(parents=True)
    if alias_kind == "symlink":
        config.symlink_to(lock)
    else:
        os.link(lock, config)
    lock_identity = _path_identity(lock)
    config_identity = _path_identity(config)
    extra = ["--dry-run"] if dry_run else []

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=extra)) == 1

    assert _path_identity(lock) == lock_identity
    assert _path_identity(config) == config_identity
    assert not mcp_swap.STATE_FILE.exists()
    assert _transaction_residue(fake_home) == []


@pytest.mark.parametrize("alias_kind", ["symlink", "hardlink"])
@pytest.mark.parametrize("artifact", ["backup", "state"])
@pytest.mark.parametrize(
    "operation", ["use", "use-dry-run", "revert", "revert-dry-run"]
)
def test_recovery_alias_to_swap_lock_is_rejected(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    alias_kind: str,
    artifact: str,
    operation: str,
) -> None:
    """A recovery artifact cannot also act as the transaction lock."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    recovery = backup if artifact == "backup" else mcp_swap.STATE_FILE
    lock = mcp_swap.STATE_DIR / "state.lock"
    lock.unlink()
    if alias_kind == "symlink":
        lock.symlink_to(recovery)
    else:
        os.link(recovery, lock)
    before = {
        path: _path_identity(path) for path in (config, backup, mcp_swap.STATE_FILE)
    }
    lock_identity = _path_identity(lock)
    args = (
        mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        if operation.startswith("revert")
        else _use_args(
            fake_repo,
            "cursor",
            extra=["--env", "LIBTMUX_SOCKET=changed"],
        )
    )
    if operation.endswith("dry-run"):
        args.dry_run = True

    command = (
        mcp_swap.cmd_revert
        if operation.startswith("revert")
        else mcp_swap.cmd_use_local
    )
    assert command(args) == 1

    assert {path: _path_identity(path) for path in before} == before
    assert _path_identity(lock) == lock_identity


@pytest.mark.parametrize("dry_run", [False, True], ids=["use", "dry-run"])
def test_prospective_lock_alias_is_rejected_before_setup_or_creation(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    dry_run: bool,
) -> None:
    """An absent planned backup cannot become the not-yet-created lock."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    lock = mcp_swap.STATE_DIR / "state.lock"
    setup: list[object] = []
    monkeypatch.setattr(mcp_swap, "_available_backup_path", lambda *_args: lock)
    monkeypatch.setattr(
        mcp_swap, "_setup_source", lambda *_args: setup.append(object())
    )
    extra = ["--dry-run"] if dry_run else []

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=extra)) == 1

    assert setup == []
    assert not os.path.lexists(lock)
    assert (
        config.read_bytes()
        == json.dumps({"mcpServers": {"tmux": _pinned_json_entry()}}, indent=2).encode()
        + b"\n"
    )


@pytest.mark.parametrize("dry_run", [False, True], ids=["use", "dry-run"])
@pytest.mark.parametrize(
    "defect", ["mode", "symlink", "hardlink", "directory", "directory-symlink"]
)
def test_unsafe_swap_lock_is_rejected_without_writes(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    dry_run: bool,
    defect: str,
) -> None:
    """Read-only planning rejects unsafe lock type, mode, and parent topology."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = config.read_bytes()
    lock = mcp_swap.STATE_DIR / "state.lock"
    if defect == "directory-symlink":
        target = fake_home / "lock-target"
        target.mkdir()
        lock.parent.parent.mkdir(parents=True, exist_ok=True)
        lock.parent.symlink_to(target, target_is_directory=True)
        (target / lock.name).write_bytes(b"")
        (target / lock.name).chmod(0o600)
    else:
        lock.parent.mkdir(parents=True)
        target = fake_home / "lock-target"
        target.write_bytes(b"human lock\n")
        target.chmod(0o600)
        if defect == "symlink":
            lock.symlink_to(target)
        elif defect == "hardlink":
            os.link(target, lock)
        elif defect == "directory":
            lock.mkdir()
        else:
            lock.write_bytes(b"")
            lock.chmod(0o640)
    extra = ["--dry-run"] if dry_run else []

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=extra)) == 1

    assert config.read_bytes() == original
    assert not mcp_swap.STATE_FILE.exists()
    assert _transaction_residue(fake_home) == []


@pytest.mark.parametrize("operation", ["use", "revert"])
def test_transaction_holds_and_revalidates_the_swap_lock(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    operation: str,
) -> None:
    """A same-path lock replacement stops a transaction while the old lock is held."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    if operation == "revert":
        assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    original = config.read_bytes()
    lock = mcp_swap.STATE_DIR / "state.lock"
    real_stage = mcp_swap._stage
    real_replace = os.replace
    observed_locked = False
    replacement_inode: int | None = None

    def stage(*args: object, **kwargs: object) -> pathlib.Path:
        nonlocal observed_locked, replacement_inode
        if replacement_inode is None:
            competitor = os.open(lock, os.O_RDWR)
            try:
                with pytest.raises(BlockingIOError):
                    fcntl.flock(competitor, fcntl.LOCK_EX | fcntl.LOCK_NB)
                observed_locked = True
            finally:
                os.close(competitor)
            human = lock.with_name("human-state.lock")
            human.write_bytes(b"human lock replacement\n")
            human.chmod(0o600)
            real_replace(human, lock)
            replacement_inode = lock.stat().st_ino
        return real_stage(*args, **kwargs)

    monkeypatch.setattr(mcp_swap, "_stage", stage)

    result = (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        if operation == "revert"
        else mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor"))
    )
    assert result == 1
    assert observed_locked
    assert replacement_inode is not None
    assert lock.stat().st_ino == replacement_inode
    assert lock.read_bytes() == b"human lock replacement\n"
    assert config.read_bytes() == original


@pytest.mark.parametrize("operation", ["use", "revert"])
def test_dry_run_never_acquires_the_swap_lock(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    operation: str,
) -> None:
    """Dry-run performs lock checks without becoming a lock owner."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    if operation == "revert":
        assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    lock = mcp_swap.STATE_DIR / "state.lock"
    if not lock.exists():
        lock.parent.mkdir(parents=True)
        lock.write_bytes(b"")
        lock.chmod(0o600)
    before = _path_identity(lock)

    def forbidden(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("dry-run acquired the swap lock")

    monkeypatch.setattr(fcntl, "flock", forbidden)
    result = (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(
                ["revert", "--dry-run", "--cli", "cursor"]
            )
        )
        if operation == "revert"
        else mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=["--dry-run"]))
    )

    assert result == 0
    assert _path_identity(lock) == before


def test_use_and_revert_keep_one_private_persistent_lock(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Successful transactions reuse one private single-link lock inode."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    lock = mcp_swap.STATE_DIR / "state.lock"

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    first = _path_identity(lock)
    assert lock.stat().st_nlink == 1
    assert first[2] == 0o600
    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 0
    )
    assert _path_identity(lock) == first


def test_lock_directory_replacement_after_file_open_is_rejected(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """An open descriptor cannot authenticate a disappeared lock directory."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    original = config.read_bytes()
    lock = mcp_swap.STATE_DIR / "state.lock"
    displaced = lock.parent.with_name("displaced-state")
    real_flock = fcntl.flock
    real_rename = os.rename
    injected = False

    def flock(descriptor: int, operation: int) -> None:
        nonlocal injected
        real_flock(descriptor, operation)
        if injected or operation != fcntl.LOCK_EX:
            return
        real_rename(lock.parent, displaced)
        lock.parent.mkdir()
        injected = True

    monkeypatch.setattr(fcntl, "flock", flock)

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 1
    assert injected
    assert config.read_bytes() == original
    assert (displaced / lock.name).is_file()
    assert not os.path.lexists(lock)


@pytest.mark.parametrize(
    ("operation", "boundary"),
    [
        ("use", "config-take-aside"),
        ("use", "config-publish"),
        ("use", "backup-publish"),
        ("use", "state-publish"),
        ("repeat-use", "state-take-aside"),
        ("revert", "config-take-aside"),
        ("revert", "config-publish"),
        ("revert", "backup-take-aside"),
        ("revert", "state-take-aside"),
        ("partial-revert", "state-publish"),
    ],
)
def test_late_transition_source_replacement_survives(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    operation: str,
    boundary: str,
) -> None:
    """Every commit boundary rejects and retains a late source inode."""
    clients = ("cursor", "gemini") if operation == "partial-revert" else ("cursor",)
    for cli in clients:
        _write_json(
            mcp_swap.CLIS[cli].config_path,
            {"mcpServers": {"tmux": _pinned_json_entry()}},
        )
    if operation in {"repeat-use", "revert", "partial-revert"}:
        assert mcp_swap.cmd_use_local(_use_args(fake_repo, *clients)) == 0
    config = mcp_swap.CLIS["cursor"].config_path
    backup = (
        pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
        if mcp_swap.STATE_FILE.exists()
        else config.with_suffix(config.suffix + mcp_swap.BACKUP_SUFFIX_PREFIX)
    )
    real_link = os.link
    real_rename = os.rename
    real_replace = os.replace
    unexpected_inode: int | None = None

    def selected(source: pathlib.Path, destination: pathlib.Path) -> bool:
        if boundary == "config-take-aside":
            return (
                source == config.resolve() and ".mcp-swap-recovery-" in destination.name
            )
        if boundary == "config-publish":
            role = "restore" if "revert" in operation else "output"
            return (
                destination == config.resolve() and f".mcp-swap-{role}-" in source.name
            )
        if boundary == "backup-publish":
            return destination.name.startswith(
                config.name + mcp_swap.BACKUP_SUFFIX_PREFIX
            )
        if boundary == "backup-take-aside":
            return source == backup and ".mcp-swap-recovery-backup-" in destination.name
        if boundary == "state-take-aside":
            return (
                source == mcp_swap.STATE_FILE
                and ".mcp-swap-recovery-state-" in destination.name
            )
        return destination == mcp_swap.STATE_FILE and ".mcp-swap-state-" in source.name

    def inject(source: pathlib.Path) -> None:
        nonlocal unexpected_inode
        human = source.with_name(f".{source.name}.human-{boundary}")
        human.write_bytes(b"human boundary replacement\n")
        human.chmod(0o600)
        real_replace(human, source)
        unexpected_inode = source.stat().st_ino

    def replace(source: os.PathLike[str], destination: os.PathLike[str]) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            inject(source_path)
        real_replace(source_path, destination_path)

    def rename(
        source: os.PathLike[str],
        destination: os.PathLike[str],
        *args: object,
        **kwargs: object,
    ) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            inject(source_path)
        real_rename(source_path, destination_path, *args, **kwargs)

    def link(
        source: os.PathLike[str],
        destination: os.PathLike[str],
        *args: object,
        **kwargs: object,
    ) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            inject(source_path)
        real_link(source_path, destination_path, *args, **kwargs)

    monkeypatch.setattr(os, "replace", replace)
    monkeypatch.setattr(os, "rename", rename)
    monkeypatch.setattr(os, "link", link)
    if operation in {"revert", "partial-revert"}:
        result = mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
    else:
        extra = ["--env", "LIBTMUX_SOCKET=changed"] if operation == "repeat-use" else []
        result = mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=extra))

    assert result == 1
    assert unexpected_inode is not None
    assert unexpected_inode in {
        path.stat().st_ino for path in fake_home.rglob("*") if path.is_file()
    }


@pytest.mark.parametrize(
    ("operation", "boundary"),
    [
        ("use", "config-take-aside"),
        ("use", "config-publish"),
        ("use", "backup-publish"),
        ("use", "state-publish"),
        ("repeat-use", "state-take-aside"),
        ("revert", "config-take-aside"),
        ("revert", "config-publish"),
        ("revert", "backup-take-aside"),
        ("revert", "state-take-aside"),
        ("partial-revert", "state-publish"),
    ],
)
def test_transition_never_overwrites_a_late_destination(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    operation: str,
    boundary: str,
) -> None:
    """A file arriving at a transition destination survives failure."""
    clients = ("cursor", "gemini") if operation == "partial-revert" else ("cursor",)
    for cli in clients:
        _write_json(
            mcp_swap.CLIS[cli].config_path,
            {"mcpServers": {"tmux": _pinned_json_entry()}},
        )
    if operation in {"repeat-use", "revert", "partial-revert"}:
        assert mcp_swap.cmd_use_local(_use_args(fake_repo, *clients)) == 0
    config = mcp_swap.CLIS["cursor"].config_path
    backup = (
        pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
        if mcp_swap.STATE_FILE.exists()
        else config.with_suffix(config.suffix + mcp_swap.BACKUP_SUFFIX_PREFIX)
    )
    real_link = os.link
    real_rename = os.rename
    real_replace = os.replace
    unexpected_inode: int | None = None

    def selected(source: pathlib.Path, destination: pathlib.Path) -> bool:
        if boundary == "config-take-aside":
            return (
                source == config.resolve() and ".mcp-swap-recovery-" in destination.name
            )
        if boundary == "config-publish":
            role = "restore" if "revert" in operation else "output"
            return (
                destination == config.resolve() and f".mcp-swap-{role}-" in source.name
            )
        if boundary == "backup-publish":
            return destination.name.startswith(
                config.name + mcp_swap.BACKUP_SUFFIX_PREFIX
            )
        if boundary == "backup-take-aside":
            return source == backup and ".mcp-swap-recovery-backup-" in destination.name
        if boundary == "state-take-aside":
            return (
                source == mcp_swap.STATE_FILE
                and ".mcp-swap-recovery-state-" in destination.name
            )
        return destination == mcp_swap.STATE_FILE and ".mcp-swap-state-" in source.name

    def appear(destination: pathlib.Path) -> None:
        nonlocal unexpected_inode
        human = destination.with_name(f".{destination.name}.human-{boundary}")
        human.write_bytes(b"human destination replacement\n")
        human.chmod(0o600)
        real_replace(human, destination)
        unexpected_inode = destination.stat().st_ino

    def replace(source: os.PathLike[str], destination: os.PathLike[str]) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            appear(destination_path)
        real_replace(source_path, destination_path)

    def rename(
        source: os.PathLike[str],
        destination: os.PathLike[str],
        *args: object,
        **kwargs: object,
    ) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            appear(destination_path)
        real_rename(source_path, destination_path, *args, **kwargs)

    def link(
        source: os.PathLike[str],
        destination: os.PathLike[str],
        *args: object,
        **kwargs: object,
    ) -> None:
        source_path = pathlib.Path(source)
        destination_path = pathlib.Path(destination)
        if unexpected_inode is None and selected(source_path, destination_path):
            appear(destination_path)
        real_link(source_path, destination_path, *args, **kwargs)

    monkeypatch.setattr(os, "replace", replace)
    monkeypatch.setattr(os, "rename", rename)
    monkeypatch.setattr(os, "link", link)
    if operation in {"revert", "partial-revert"}:
        result = mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
    else:
        extra = ["--env", "LIBTMUX_SOCKET=changed"] if operation == "repeat-use" else []
        result = mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", extra=extra))

    assert result == 1
    assert unexpected_inode is not None
    assert unexpected_inode in {
        path.stat().st_ino for path in fake_home.rglob("*") if path.is_file()
    }


@pytest.mark.parametrize("operation", ["use", "revert"])
def test_cleanup_retains_a_late_owned_path_replacement(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    operation: str,
) -> None:
    """Final cleanup rejects and retains a substituted task-owned inode."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    if operation == "revert":
        assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    real_cleanup = mcp_swap._cleanup_owned
    real_rename = os.rename
    real_replace = os.replace
    real_unlink = os.unlink
    cleaning = False
    unexpected_inode: int | None = None

    def cleanup(*args: object, **kwargs: object) -> list[str]:
        nonlocal cleaning
        cleaning = True
        try:
            return real_cleanup(*args, **kwargs)
        finally:
            cleaning = False

    def inject(path: pathlib.Path) -> None:
        nonlocal unexpected_inode
        if (
            not cleaning
            or unexpected_inode is not None
            or ".mcp-swap-recovery-" not in path.name
        ):
            return
        human = path.with_name(f".{path.name}.human-cleanup")
        human.write_bytes(b"human cleanup replacement\n")
        human.chmod(0o600)
        real_replace(human, path)
        unexpected_inode = path.stat().st_ino

    def rename(
        source: os.PathLike[str],
        destination: os.PathLike[str],
        *args: object,
        **kwargs: object,
    ) -> None:
        inject(pathlib.Path(source))
        real_rename(source, destination, *args, **kwargs)

    def unlink(path: os.PathLike[str], *args: object, **kwargs: object) -> None:
        inject(pathlib.Path(path))
        real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(mcp_swap, "_cleanup_owned", cleanup)
    monkeypatch.setattr(os, "rename", rename)
    monkeypatch.setattr(os, "unlink", unlink)
    result = (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        if operation == "revert"
        else mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor"))
    )

    assert result == 1
    assert unexpected_inode is not None
    assert unexpected_inode in {
        path.stat().st_ino for path in fake_home.rglob("*") if path.is_file()
    }


def test_all_eight_clients_commit_and_revert_as_one_transaction(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Every supported dialect participates in the same authenticated round trip."""
    originals: dict[pathlib.Path, bytes] = {}
    for cli in mcp_swap.ALL_CLIS:
        info = mcp_swap.CLIS[cli]
        info.config_path.parent.mkdir(parents=True, exist_ok=True)
        if info.fmt == "toml":
            body = b'# keep\n[mcp_servers.other]\ncommand = "other"\n'
        elif info.fmt == "jsonc":
            body = b"{\n  // keep\n}\n"
        else:
            body = b"{}\n"
        info.config_path.write_bytes(body)
        originals[info.config_path] = body

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, *mcp_swap.ALL_CLIS)) == 0
    state = json.loads(mcp_swap.STATE_FILE.read_text())
    assert state["version"] == mcp_swap.STATE_VERSION
    assert len(state["entries"]) == len(mcp_swap.ALL_CLIS)
    assert all(entry["checksum"] for entry in state["entries"].values())

    assert mcp_swap.cmd_revert(mcp_swap.build_parser().parse_args(["revert"])) == 0
    assert {path: path.read_bytes() for path in originals} == originals
    assert not mcp_swap.STATE_FILE.exists()
    assert _transaction_residue(fake_home) == []


def test_late_revert_failure_restores_every_pre_revert_identity(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A final config failure rolls earlier restores back byte-for-byte and by inode."""
    paths = [mcp_swap.CLIS[name].config_path for name in ("cursor", "gemini")]
    for path in paths:
        _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", "gemini")) == 0
    configs = {path: _path_identity(path) for path in paths}
    state = _path_identity(mcp_swap.STATE_FILE)
    backups = {
        pathlib.Path(entry.backup_path): _path_identity(pathlib.Path(entry.backup_path))
        for entry in mcp_swap.load_state().values()
    }
    real_publish = mcp_swap._publish_absent
    failed = False

    def fail_final_restore(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal failed
        if destination == paths[-1].resolve() and not failed:
            failed = True
            raise PermissionError("synthetic final restore failure")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_final_restore)
    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(
                ["revert", "--cli", "cursor", "--cli", "gemini"]
            )
        )
        == 1
    )
    assert {path: _path_identity(path) for path in paths} == configs
    assert _path_identity(mcp_swap.STATE_FILE) == state
    assert {path: _path_identity(path) for path in backups} == backups
    assert _transaction_residue(fake_home) == []


def test_repeat_use_blocked_rollback_preserves_prior_recovery_identities(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A failed repeat swap never deletes or replaces its earlier backup and state."""
    cursor = mcp_swap.CLIS["cursor"].config_path
    gemini = mcp_swap.CLIS["gemini"].config_path
    for path in (cursor, gemini):
        _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    prior_state = _path_identity(mcp_swap.STATE_FILE)
    prior_backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    prior_backup_identity = _path_identity(prior_backup)
    real_publish = mcp_swap._publish_absent

    def fail_commit_and_cursor_rollback(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        if destination == gemini.resolve() and "mcp-swap-output" in source.name:
            raise PermissionError("synthetic final config failure")
        if destination == cursor.resolve() and "mcp-swap-recovery" in source.name:
            raise PermissionError("synthetic rollback failure")
        return real_publish(source, destination, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_commit_and_cursor_rollback)
    assert (
        mcp_swap.cmd_use_local(
            _use_args(
                fake_repo,
                "cursor",
                "gemini",
                extra=["--env", "LIBTMUX_SOCKET=repeat"],
            )
        )
        == 1
    )
    assert _path_identity(mcp_swap.STATE_FILE) == prior_state
    assert _path_identity(prior_backup) == prior_backup_identity
    residues = _transaction_residue(fake_home)
    assert any("recovery" in path.name for path in residues)
    assert any("state" in path.name for path in residues)


def test_hardlinked_client_configs_are_rejected_before_writing(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Different paths to one config inode cannot enter a transaction twice."""
    cursor = mcp_swap.CLIS["cursor"].config_path
    gemini = mcp_swap.CLIS["gemini"].config_path
    _write_json(cursor, {"mcpServers": {"tmux": _pinned_json_entry()}})
    gemini.parent.mkdir(parents=True)
    os.link(cursor, gemini)
    before = _path_identity(cursor)

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor", "gemini")) == 1
    assert "duplicate physical config target" in capsys.readouterr().err
    assert _path_identity(cursor) == before
    assert not mcp_swap.STATE_FILE.exists()


def test_revert_refuses_a_modified_backup_and_preserves_every_artifact(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
) -> None:
    """Backup bytes and mode are authenticated before any restore starts."""
    path = mcp_swap.CLIS["cursor"].config_path
    _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    swapped = _path_identity(path)
    state = mcp_swap.STATE_FILE.read_bytes()
    backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    backup.write_bytes(b"human backup edit\n")
    edited = backup.read_bytes()

    assert (
        mcp_swap.cmd_revert(
            mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
        )
        == 1
    )
    assert _path_identity(path) == swapped
    assert backup.read_bytes() == edited
    assert mcp_swap.STATE_FILE.read_bytes() == state


def test_use_rollback_reverses_state_backups_then_configs(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Rollback applies the exact reverse of config, backup, then state commit."""
    clients = ("cursor", "gemini")
    configs = [mcp_swap.CLIS[cli].config_path for cli in clients]
    for path in configs:
        _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    real_publish = mcp_swap._publish_absent
    real_unlink = mcp_swap._apply_unlink
    rolling_back = False
    events: list[tuple[str, pathlib.Path]] = []
    created_backups: list[pathlib.Path] = []

    def fail_after_state_commit(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal rolling_back
        if not rolling_back and mcp_swap.BACKUP_SUFFIX_PREFIX in destination.name:
            created_backups.append(destination)
        result = real_publish(source, destination, **kwargs)
        if destination == mcp_swap.STATE_FILE and "mcp-swap-state" in source.name:
            rolling_back = True
            return result[0], PermissionError("synthetic post-state failure")
        if rolling_back:
            events.append(("publish", destination))
        return result

    def record_unlink(path: pathlib.Path, **kwargs: object) -> Exception | None:
        if rolling_back:
            events.append(("unlink", path))
        return real_unlink(path, **kwargs)

    monkeypatch.setattr(mcp_swap, "_publish_absent", fail_after_state_commit)
    monkeypatch.setattr(mcp_swap, "_apply_unlink", record_unlink)

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, *clients)) == 1
    state_event = [("unlink", mcp_swap.STATE_FILE)]
    backup_events = [("unlink", path) for path in reversed(created_backups)]
    config_events = [
        event
        for path in reversed(configs)
        for event in (("unlink", path.resolve()), ("publish", path.resolve()))
    ]
    assert events == state_event + backup_events + config_events


def test_revert_rollback_reverses_state_backups_then_configs(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Revert rollback reverses state and backup tombstones before config writes."""
    clients = ("cursor", "gemini")
    configs = [mcp_swap.CLIS[cli].config_path for cli in clients]
    for path in configs:
        _write_json(path, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, *clients)) == 0
    backups = [
        pathlib.Path(entry.backup_path)
        for entry in sorted(
            mcp_swap.load_state().values(), key=lambda entry: entry.seq_no
        )
    ]
    real_apply = mcp_swap._apply_replace
    real_publish = mcp_swap._publish_absent
    rolling_back = False
    events: list[pathlib.Path] = []

    def fail_after_state_tombstone(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal rolling_back
        result = real_apply(source, destination, **kwargs)
        if source == mcp_swap.STATE_FILE:
            rolling_back = True
            return result[0], PermissionError("synthetic post-state failure")
        return result

    def record_publish(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        result = real_publish(source, destination, **kwargs)
        if rolling_back:
            events.append(destination)
        return result

    monkeypatch.setattr(mcp_swap, "_apply_replace", fail_after_state_tombstone)
    monkeypatch.setattr(mcp_swap, "_publish_absent", record_publish)

    args = mcp_swap.build_parser().parse_args(
        ["revert", "--cli", "cursor", "--cli", "gemini"]
    )
    assert mcp_swap.cmd_revert(args) == 1
    assert events == [
        mcp_swap.STATE_FILE,
        *reversed(backups),
        *(path.resolve() for path in reversed(configs)),
    ]


def test_use_preserves_same_path_replacement_during_recovery_commit(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A byte-identical foreign inode cannot become transaction-owned mid-commit."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    real_publish = mcp_swap._publish_absent
    real_replace = os.replace
    replacement_identity: tuple[int, int] | None = None

    def replace_config_after_backup(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal replacement_identity
        result = real_publish(source, destination, **kwargs)
        if (
            replacement_identity is None
            and mcp_swap.BACKUP_SUFFIX_PREFIX in destination.name
        ):
            replacement = config.with_name("foreign-config")
            replacement.write_bytes(config.read_bytes())
            real_replace(replacement, config)
            details = config.stat()
            replacement_identity = (details.st_dev, details.st_ino)
        return result

    monkeypatch.setattr(mcp_swap, "_publish_absent", replace_config_after_backup)

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 1
    details = config.stat()
    assert (details.st_dev, details.st_ino) == replacement_identity
    assert any("recovery" in path.name for path in _transaction_residue(fake_home))


def test_use_preserves_symlink_retarget_during_recovery_commit(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A concurrent symlink retarget is retained and makes the swap fail closed."""
    config = mcp_swap.CLIS["cursor"].config_path
    config.parent.mkdir(parents=True)
    original_target = fake_home / "original.json"
    human_target = fake_home / "human.json"
    _write_json(original_target, {"mcpServers": {"tmux": _pinned_json_entry()}})
    human = b'{"human": "keep"}\n'
    human_target.write_bytes(human)
    config.symlink_to(original_target)
    real_publish = mcp_swap._publish_absent
    retargeted = False

    def retarget_after_backup(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal retargeted
        result = real_publish(source, destination, **kwargs)
        if not retargeted and mcp_swap.BACKUP_SUFFIX_PREFIX in destination.name:
            config.unlink()
            config.symlink_to(human_target)
            retargeted = True
        return result

    monkeypatch.setattr(mcp_swap, "_publish_absent", retarget_after_backup)

    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 1
    assert config.resolve() == human_target
    assert config.read_bytes() == human
    assert any("recovery" in path.name for path in _transaction_residue(fake_home))


def test_revert_preserves_human_edit_during_backup_tombstone(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A human edit after config restore blocks cleanup and survives rollback."""
    config = mcp_swap.CLIS["cursor"].config_path
    _write_json(config, {"mcpServers": {"tmux": _pinned_json_entry()}})
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, "cursor")) == 0
    state_identity = _path_identity(mcp_swap.STATE_FILE)
    backup = pathlib.Path(mcp_swap.load_state()["cursor", "user"].backup_path)
    backup_identity = _path_identity(backup)
    human = b'{"human": "keep"}\n'
    real_apply = mcp_swap._apply_replace
    edited = False

    def edit_config_after_backup_tombstone(
        source: pathlib.Path,
        destination: pathlib.Path,
        **kwargs: object,
    ) -> tuple[mcp_swap.FileState, Exception | None]:
        nonlocal edited
        result = real_apply(source, destination, **kwargs)
        if not edited and source == backup:
            config.write_bytes(human)
            edited = True
        return result

    monkeypatch.setattr(mcp_swap, "_apply_replace", edit_config_after_backup_tombstone)

    args = mcp_swap.build_parser().parse_args(["revert", "--cli", "cursor"])
    assert mcp_swap.cmd_revert(args) == 1
    assert config.read_bytes() == human
    assert _path_identity(mcp_swap.STATE_FILE) == state_identity
    assert _path_identity(backup) == backup_identity
    assert any("recovery" in path.name for path in _transaction_residue(fake_home))


def test_revert_dry_run_plans_every_selected_client_before_reporting_errors(
    fake_home: pathlib.Path,
    fake_repo: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """One invalid recovery target does not keep later selections uninspected."""
    clients = ("cursor", "gemini")
    for cli in clients:
        _write_json(
            mcp_swap.CLIS[cli].config_path,
            {"mcpServers": {"tmux": _pinned_json_entry()}},
        )
    assert mcp_swap.cmd_use_local(_use_args(fake_repo, *clients)) == 0
    mcp_swap.CLIS["cursor"].config_path.write_bytes(b'{"human": "keep"}\n')
    inspected: list[str] = []
    real_config_state = mcp_swap._config_state

    def record_config_state(target: mcp_swap.Target) -> mcp_swap.ConfigState:
        inspected.append(target.cli)
        return real_config_state(target)

    monkeypatch.setattr(mcp_swap, "_config_state", record_config_state)
    args = mcp_swap.build_parser().parse_args(
        ["revert", "--dry-run", "--cli", "cursor", "--cli", "gemini"]
    )

    assert mcp_swap.cmd_revert(args) == 1
    assert inspected == ["cursor", "gemini"]
