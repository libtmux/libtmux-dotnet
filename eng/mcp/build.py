"""Turn a repository, a run or a release into a server spec.

Pointing a CLI at a build is a separate job from editing that CLI's config,
and it is the half that shells out: finding a dotnet the way mise hides it,
building a profile, installing a published tool, and asking the result to
complete an MCP handshake before anything is written down.
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
import typing as t

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import xdg
from spec import McpServerSpec

#: Where an installed release lands, named once so the status line and
#: the installer cannot disagree about it.
RELEASES_ROOT = xdg.state_home() / "tmux-mcp-dev" / "releases"



ALL_SOURCES = ("debug", "release", "run", "path", "published")
Source = t.Literal["debug", "release", "run", "path", "published"]

DEFAULT_PROJECT = "LibTmux.Mcp"

#: The config-file key every libtmux port registers under.
#:
#: Named rather than derived from the package. Deriving it gives
#: ``libtmux`` here and ``tmux`` in the Rust port, so a swap would add a
#: second server beside the one it meant to replace and the agent would
#: keep using whichever it found first -- measured, and the reason this is
#: a constant. Override with ``--server`` to register alongside on purpose.
DEFAULT_SERVER = "tmux"

#: Frameworks the tool multi-targets, newest first. A profile build writes
#: one output directory per framework; the newest present is the one an
#: agent should launch, and the older one stays available for a bisect.
FRAMEWORKS = ("net10.0", "net8.0")


def project_file(repo: pathlib.Path, project: str = DEFAULT_PROJECT) -> pathlib.Path:
    """Return the project file to build and to point ``dotnet run`` at."""
    path = repo / "src" / project / f"{project}.csproj"
    if not path.is_file():
        msg = f"no {project}.csproj under {repo / 'src' / project}"
        raise RuntimeError(msg)
    return path


def project_property(text: str, name: str) -> str | None:
    """Read one MSBuild property out of a project file.

    Deliberately a substring read rather than an XML parse: the properties
    wanted here are plain literals, and a dependency on an XML library
    would be the only one this script has.
    """
    opening = f"<{name}>"
    closing = f"</{name}>"
    start = text.find(opening)
    if start < 0:
        return None
    stop = text.find(closing, start)
    if stop < 0:
        return None
    return text[start + len(opening) : stop].strip() or None


def resolve_repo_meta(
    repo: pathlib.Path, project: str = DEFAULT_PROJECT
) -> tuple[str, str]:
    """Derive (server_name, binary_name) from the project file.

    The server name is :data:`DEFAULT_SERVER`, the config-file key every
    libtmux port registers under (``mcpServers.<slug>`` in JSON,
    ``[mcp_servers.<slug>]`` in TOML).

    The binary name is what the build writes into ``bin/<Configuration>/``,
    which is ``AssemblyName`` when the project sets one and the project
    name otherwise.
    """
    text = project_file(repo, project).read_text()
    binary = project_property(text, "AssemblyName") or project
    return DEFAULT_SERVER, binary


def find_dotnet() -> str:
    """Locate the SDK an agent must launch, as an absolute path.

    ``dotnet`` is pinned by ``global.json`` and resolves through mise, so
    it is usually absent from a bare ``PATH``. An agent does not inherit
    this shell, and a config naming a bare ``dotnet`` would leave every
    agent failing to start the server with an error that surfaces inside
    the agent rather than here.
    """
    found = shutil.which("dotnet")
    if found:
        return str(pathlib.Path(found).resolve())
    try:
        resolved = subprocess.run(
            ["mise", "which", "dotnet"],
            capture_output=True,
            text=True,
            check=True,
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        resolved = ""
    if resolved:
        return str(pathlib.Path(resolved).resolve())
    msg = (
        "no dotnet on PATH and mise could not name one; "
        "install the SDK or run inside `mise exec`"
    )
    raise RuntimeError(msg)


def dotnet_environment() -> dict[str, str]:
    """Environment an agent needs so the apphost can find its runtime.

    A framework-dependent apphost is a native launcher that locates the
    runtime through ``DOTNET_ROOT`` or ``PATH``. The SDK here is pinned by
    ``global.json`` and installed by mise, so neither names it in the
    environment an agent CLI spawns -- measured: the same binary that runs
    from a developer shell exits with "You must install .NET to run this
    application" under an agent, before the handshake, which the agent
    reports as the server having no tools.

    Carrying the location in the config is what makes the entry work for
    whoever launches it rather than only for the shell that wrote it.

    ``DOTNET_ROOT`` alone, verified by launching under a stripped
    environment. Adding ``PATH`` would work too and would copy the whole
    developer environment into every agent's config file, which is a
    thousand characters of somebody else's machine per entry.
    """
    return {"DOTNET_ROOT": str(pathlib.Path(find_dotnet()).parent)}


def profile_binary(
    repo: pathlib.Path,
    binary: str,
    configuration: str,
    project: str = DEFAULT_PROJECT,
) -> pathlib.Path:
    """Return the built apphost for a configuration, newest framework first."""
    root = repo.resolve() / "src" / project / "bin" / configuration
    for framework in FRAMEWORKS:
        candidate = root / framework / binary
        if candidate.is_file():
            return candidate
    return root / FRAMEWORKS[0] / binary


def build_profile_spec(
    repo: pathlib.Path,
    binary: str,
    configuration: str,
    project: str = DEFAULT_PROJECT,
) -> McpServerSpec:
    """Point an agent straight at a compiled binary.

    The agent launches the apphost itself, so nothing runs in front of it:
    startup is a process spawn rather than a build that may decide to
    recompile while a client is waiting for the handshake.
    """
    return McpServerSpec(
        command=str(profile_binary(repo, binary, configuration, project)),
        env=dotnet_environment(),
    )


def build_run_spec(
    repo: pathlib.Path, project: str = DEFAULT_PROJECT
) -> McpServerSpec:
    """Launch through ``dotnet run``, rebuilding on every start.

    This is the shape to use while editing: the next agent session picks
    up the current source with no build step to remember. It costs a build
    check on each launch, and the first launch after a change can be slow
    enough that a client with a short handshake timeout gives up, which is
    why it is not the default.

    The build writes its progress to stderr, leaving stdout to carry the
    protocol -- ``preflight`` proves that rather than assuming it.
    """
    return McpServerSpec(
        command=find_dotnet(),
        env=dotnet_environment(),
        args=[
            "run",
            "--project",
            str(project_file(repo, project).resolve()),
            "--framework",
            FRAMEWORKS[0],
            "--configuration",
            "Debug",
            "--",
        ],
    )


def build_path_spec(binary_path: pathlib.Path) -> McpServerSpec:
    """Point at a binary the caller names, wherever it came from."""
    return McpServerSpec(command=str(binary_path.resolve()), env=dotnet_environment())


def published_root(version: str, binary: str) -> pathlib.Path:
    """Where a published release is installed so it cannot shadow others.

    Each version gets its own tool path, so swapping between releases does
    not reinstall over the previous one and reverting leaves it available.
    """
    return RELEASES_ROOT / f"{binary}-{version}"


def published_command(version: str, binary: str, command: str) -> pathlib.Path:
    """Return the launcher a published install writes."""
    return published_root(version, binary) / command


def build_published_spec(
    version: str, binary: str, command: str
) -> McpServerSpec:
    """Point at a NuGet release installed under its own tool path."""
    return McpServerSpec(
        command=str(published_command(version, binary, command)),
        env=dotnet_environment(),
    )


def install_published(
    package: str, version: str, binary: str, command: str
) -> pathlib.Path:
    """Install a NuGet release, returning the launcher path.

    Skips the install when that exact version is already present, so
    repeated swaps between releases cost one download each rather than one
    per swap.
    """
    target = published_command(version, binary, command)
    if target.is_file():
        return target
    subprocess.run(
        [
            find_dotnet(),
            "tool",
            "install",
            package,
            "--version",
            version,
            "--tool-path",
            str(published_root(version, binary)),
        ],
        check=True,
    )
    return target


def build_source_spec(
    source: Source,
    *,
    repo: pathlib.Path,
    binary: str,
    project: str = DEFAULT_PROJECT,
    command: str = "libtmux-mcp",
    version: str | None = None,
    binary_path: pathlib.Path | None = None,
) -> McpServerSpec:
    """Build the spec for one source kind."""
    if source == "debug":
        return build_profile_spec(repo, binary, "Debug", project)
    if source == "release":
        return build_profile_spec(repo, binary, "Release", project)
    if source == "run":
        return build_run_spec(repo, project)
    if source == "path":
        if binary_path is None:
            msg = "--source path needs --bin"
            raise RuntimeError(msg)
        return build_path_spec(binary_path)
    if source == "published":
        if version is None:
            msg = "--source published needs --version"
            raise RuntimeError(msg)
        return build_published_spec(version, binary, command)
    msg = f"unknown source {source!r}"
    raise RuntimeError(msg)


def dotnet_build(
    repo: pathlib.Path, configuration: str, project: str = DEFAULT_PROJECT
) -> None:
    """Build the binary a profile spec points at.

    Writing a config that names a binary which does not exist yet leaves
    every agent failing to start a server, and the error surfaces inside
    the agent rather than here.
    """
    subprocess.run(
        [
            find_dotnet(),
            "build",
            str(project_file(repo, project).resolve()),
            "--configuration",
            configuration,
        ],
        check=True,
    )


def _run_text(argv: list[str], cwd: pathlib.Path | None = None) -> str:
    """Run ``argv`` and return stdout, raising on a non-zero exit."""
    return subprocess.run(
        argv,
        cwd=None if cwd is None else str(cwd),
        capture_output=True,
        text=True,
        check=True,
    ).stdout


_INITIALIZE_FRAME = (
    json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "mcp_swap-preflight", "version": "1"},
            },
        }
    )
    + "\n"
)


def preflight_spec(spec: McpServerSpec, *, timeout: float = 300.0) -> str | None:
    """Launch ``spec`` and complete one MCP ``initialize`` round trip.

    Returns ``None`` when the server answered, otherwise a reason to
    show the operator. A pull-request spec resolves its dependencies at
    launch time, inside whichever agent starts it, so an unresolvable
    ref would otherwise land in every config and surface later as an
    opaque startup failure in each one.

    stdin is held open until the answer arrives, the way a real client
    holds it open for the session. Writing the frame and closing at once
    is a different test: an SDK that treats end of input as a disconnect
    tears the session down while the reply is still being written, and
    the server answers nothing. Measured against this one -- immediate
    close returned no bytes, the same frame followed by a pause returned
    the handshake.
    """
    try:
        proc = subprocess.Popen(
            [spec.command, *spec.args],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env={**os.environ, **spec.env},
            text=True,
        )
    except OSError as exc:
        return f"could not launch {spec.command}: {exc}"

    assert proc.stdin is not None
    assert proc.stdout is not None
    try:
        proc.stdin.write(_INITIALIZE_FRAME)
        proc.stdin.flush()
    except OSError as exc:
        proc.kill()
        proc.communicate()
        return f"{spec.command} closed stdin before answering: {exc}"

    answer: str | None = None
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        line = proc.stdout.readline()
        if not line:
            break
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(message, dict) and message.get("id") == 1 and "result" in message:
            answer = line
            break

    # Closed by hand, so ``communicate`` must not be asked to close it
    # again: on CPython 3.12 that raises "I/O operation on closed file"
    # and turns a server that answered correctly into a swap that refuses
    # to write. The remaining output is drained directly instead.
    try:
        proc.stdin.close()
    except OSError:
        pass
    proc.stdin = None

    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()
        out, err = proc.communicate()
    if answer is not None:
        return None

    for line in out.splitlines():
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(message, dict) and message.get("id") == 1 and "result" in message:
            return None

    tail = "\n".join(err.strip().splitlines()[-3:])
    return tail or "server exited without answering initialize"
