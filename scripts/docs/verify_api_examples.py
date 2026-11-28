"""Validate whole-file API examples and run their standalone package consumers."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import tempfile
import time
import zipfile
from collections.abc import Mapping
from pathlib import Path
from typing import Any
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[2]
FRAMEWORKS = ("net8.0", "net10.0")
# A unix socket address holds this many bytes. Each program makes a directory of
# `libtmux-example-` and eight characters under its temporary directory, and its
# socket is a file in it, the name being at longest this long.
SOCKET_ADDRESS_BYTES = 107
PROGRAM_DIRECTORY_BYTES = len("/libtmux-example-") + 8
LONGEST_PROGRAM_SOCKET_NAME = len("/tmux.sock-none")


RUN_DIRECTORY_BYTES = len("/a-") + 8


def maximum_temp_base_bytes() -> int:
    """Return the longest directory a run's own temporary directory can be made in.

    Each run gets ``a-`` and eight characters there, and that is the TMPDIR a
    program makes its socket directory in.
    """
    return (
        SOCKET_ADDRESS_BYTES
        - PROGRAM_DIRECTORY_BYTES
        - LONGEST_PROGRAM_SOCKET_NAME
        - RUN_DIRECTORY_BYTES
    )


PROFILE_SOURCES = {
    "fsharp": "examples/LibTmux.FSharp.Examples/Programs/*.fs",
    "csharp": "examples/LibTmux.Examples/Programs/*.cs",
}


def configured_tmux(environment: Mapping[str, str]) -> str | None:
    """Return the configured test tmux executable, keeping non-blank values untrimmed."""
    for variable in ("LIBTMUX_TEST_TMUX", "LIBTMUX_TMUX"):
        candidate = environment.get(variable)
        if candidate is not None and candidate.strip():
            return candidate
    return None


def resolve_tmux(environment: Mapping[str, str]) -> str:
    """Return the tmux executable under test."""
    return configured_tmux(environment) or "tmux"


def hostile_environment(source: Mapping[str, str], temp_directory: Path) -> dict[str, str]:
    """Point every way tmux or the library picks a server at one that is not there.

    A program that owns its server through ``SocketPath`` is unmoved by all of
    these, so it needs no setup beforehand; one that falls back to a name, a
    root or the caller's server fails at once instead of reaching somebody
    else's.
    """
    return {
        **source,
        "TMUX": "/nonexistent/tmux-0/default,1,0",
        "TMUX_PANE": "%0",
        "TMUX_TMPDIR": "/nonexistent/libtmux-tmux-tmpdir",
        "LIBTMUX_SOCKET": "libtmux-hostile-socket",
        "LIBTMUX_SOCKET_NAME": "libtmux-hostile-name",
        "LIBTMUX_SOCKET_PATH": "/nonexistent/hostile.sock",
        "TMPDIR": str(temp_directory),
    }


def processes_running_in(directory: Path) -> list[int]:
    """Return the processes that still carry ``directory`` as their TMPDIR.

    A tmux server and its panes inherit the environment of the program that
    started them, so a survivor is a server the program did not stop. Without
    /proc there is nothing to read, and none are reported.
    """
    marker = f"TMPDIR={directory}".encode()
    found = []
    for entry in Path("/proc").glob("[0-9]*"):
        try:
            variables = (entry / "environ").read_bytes().split(b"\0")
        except OSError:
            continue
        if marker in variables:
            found.append(int(entry.name))
    return found


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    """Reject duplicate manifest keys instead of losing the earlier value."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def source_path(root: Path, value: str) -> Path:
    """Require a repository-relative file that cannot escape through a symlink."""
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError(f"Invalid source path: {value!r}")
    relative = Path(value)
    path = (root / relative).resolve()
    if relative.is_absolute() or ".." in relative.parts or not path.is_relative_to(root.resolve()):
        raise ValueError(f"Source path escapes the repository: {value}")
    if not path.is_file():
        raise ValueError(f"Source file is missing: {value}")
    return path


def keys(value: dict, expected: set[str], label: str) -> None:
    """Make misspelled or unsupported manifest fields fail validation."""
    if not isinstance(value, dict) or set(value) != expected:
        raise ValueError(f"Unexpected fields in {label}; expected {sorted(expected)}")


def validate(root: Path, manifest: dict, inventory: dict) -> None:
    """Bind complete example files and setup to explicit public compiler IDs."""
    keys(manifest, {"schemaVersion", "profiles", "setupFiles", "examples"}, "manifest")
    if manifest["schemaVersion"] != 1:
        raise ValueError("Unsupported API example schemaVersion")
    if manifest["setupFiles"] != ["global.json", "examples/api/NuGet.config"]:
        raise ValueError("Setup must include the pinned SDK and mapped package feed")
    for value in manifest["setupFiles"]:
        source_path(root, value)
    feed = ElementTree.parse(root / "examples/api/NuGet.config")
    if [node.attrib for node in feed.findall(".//packageSources/*")] != [
        {},
        {"key": "libtmux-source", "value": "libtmux-source/artifacts/api-example-packages"},
        {"key": "nuget.org", "value": "https://api.nuget.org/v3/index.json"},
    ] or [node.tag for node in feed.findall(".//packageSources/*")] != ["clear", "add", "add"]:
        raise ValueError("NuGet.config must use the displayed local feed and nuget.org")
    mapping = {
        node.get("key"): [package.get("pattern") for package in node]
        for node in feed.findall(".//packageSourceMapping/packageSource")
    }
    if mapping != {"libtmux-source": ["LibTmux", "LibTmux.*"], "nuget.org": ["*"]} or [
        node.tag for node in feed.findall(".//packageSourceMapping/*")
    ] != ["clear", "packageSource", "packageSource"]:
        raise ValueError(
            "NuGet.config must resolve LibTmux packages only from the native local pack"
        )
    if not manifest["profiles"] or not isinstance(manifest["profiles"], dict):
        raise ValueError("At least one example profile is required")

    props = ElementTree.parse(root / "Directory.Build.props")
    version = props.findtext(".//VersionPrefix")
    suffix = props.findtext(".//VersionSuffix")
    version = f"{version}-{suffix}" if suffix else version
    for name, profile in manifest["profiles"].items():
        keys(profile, {"package", "projectFile", "entrypoint"}, f"profile {name}")
        expected = {
            "fsharp": {
                "package": "LibTmux.FSharp",
                "projectFile": "examples/api/fsharp/Example.fsproj",
                "entrypoint": "Program.fs",
            },
            "csharp": {
                "package": "LibTmux",
                "projectFile": "examples/api/csharp/Example.csproj",
                "entrypoint": "Program.cs",
            },
        }
        if name not in expected or profile != expected[name]:
            raise ValueError(f"Unsupported example profile: {name}")
        project = ElementTree.parse(source_path(root, profile["projectFile"]))
        allowed = {
            "Project",
            "PropertyGroup",
            "ItemGroup",
            "OutputType",
            "TargetFrameworks",
            "LangVersion",
            "TreatWarningsAsErrors",
            "DisableImplicitFSharpCoreReference",
            "DisableImplicitLibraryPacksFolder",
            "Compile",
            "PackageReference",
        }
        if name == "csharp":
            allowed |= {"Nullable", "ImplicitUsings", "EnableDefaultCompileItems", "AnalysisLevel"}
        if any(node.tag not in allowed for node in project.iter()):
            raise ValueError(f"Profile {name} must not import hidden source or build properties")
        if project.findtext(".//TargetFrameworks") != ";".join(FRAMEWORKS):
            raise ValueError(f"Profile {name} must run on both supported frameworks")
        if [node.attrib for node in project.findall(".//Compile")] != [
            {"Include": profile["entrypoint"]}
        ]:
            raise ValueError(f"Profile {name} must compile only the displayed entrypoint")
        if project.findall(".//ProjectReference") or project.findall(".//Import"):
            raise ValueError(f"Profile {name} must not import hidden source or build properties")
        references = {
            node.get("Include"): node.get("Version")
            for node in project.findall(".//PackageReference")
        }
        dependencies = (
            {"LibTmux.FSharp": f"[{version}]", "FSharp.Core": "[10.1.404]"}
            if name == "fsharp"
            else {"LibTmux": f"[{version}]"}
        )
        if references != dependencies:
            raise ValueError(f"Profile {name} must use the exact library and language dependencies")
        if name == "csharp" and any(
            project.findtext(f".//{key}") != value
            for key, value in {
                "Nullable": "enable",
                "ImplicitUsings": "disable",
                "EnableDefaultCompileItems": "false",
            }.items()
        ):
            raise ValueError(
                "The C# profile must declare nullability, imports and its sole entrypoint"
            )

    members = {
        (member["package"], member["id"])
        for member in inventory["members"]
        if member["visibility"] == "public"
        and not member["implicitDeclaration"]
        and not member["accessor"]
    }
    identifiers: set[str] = set()
    sources: set[str] = set()
    if not isinstance(manifest["examples"], list) or not manifest["examples"]:
        raise ValueError("At least one complete API example is required")
    for entry in manifest["examples"]:
        keys(
            {name: value for name, value in entry.items() if name != "requiresTmux"},
            {"id", "profile", "title", "description", "sourceFile", "targets", "output"},
            "example",
        )
        if "requiresTmux" in entry and not re.fullmatch(
            r"[0-9]+\.[0-9]+[a-z]?", str(entry["requiresTmux"])
        ):
            raise ValueError(f"Example {entry['id']} needs a tmux release in requiresTmux")
        if not isinstance(entry["id"], str) or not re.fullmatch(
            r"[a-z]+-[A-Za-z0-9]+", entry["id"]
        ):
            raise ValueError(f"Invalid example ID: {entry['id']!r}")
        if entry["id"] in identifiers or entry["sourceFile"] in sources:
            raise ValueError(f"Duplicate example ID or source file: {entry['id']}")
        identifiers.add(entry["id"])
        sources.add(entry["sourceFile"])
        if entry["profile"] not in manifest["profiles"]:
            raise ValueError(f"Unknown example profile: {entry['profile']}")
        if not entry["id"].startswith(entry["profile"] + "-"):
            raise ValueError(f"Example ID does not match its language profile: {entry['id']}")
        for field in ("title", "description", "output"):
            if not isinstance(entry[field], str) or not entry[field].strip():
                raise ValueError(f"Example {entry['id']} needs {field}")
        if not entry["output"].endswith("\n") or "\r" in entry["output"]:
            raise ValueError(f"Example {entry['id']} output must use LF and end with a newline")
        source = source_path(root, entry["sourceFile"]).read_bytes()
        if not source.endswith(b"\n") or b"\r" in source:
            raise ValueError(f"Example {entry['id']} must publish its complete LF-terminated file")
        text = source.decode("utf-8")
        if "SocketPath" not in text or "SocketName" in text:
            raise ValueError(
                f"Example {entry['id']} must own its server on a SocketPath of its own, "
                "not a SocketName that depends on the caller's environment"
            )
        if "wait-for" in text:
            raise ValueError(
                f"Example {entry['id']} must not type a tmux command into a pane to signal itself"
            )
        targets = entry["targets"]
        if (
            not isinstance(targets, list)
            or not targets
            or not all(isinstance(value, str) for value in targets)
        ):
            raise ValueError(f"Example {entry['id']} needs compiler targets")
        if len(set(targets)) != len(targets):
            raise ValueError(f"Example {entry['id']} repeats a compiler target")
        package = manifest["profiles"][entry["profile"]]["package"]
        for target in targets:
            if (package, target) not in members:
                raise ValueError(f"Example {entry['id']} has no public compiler target: {target}")

    for name, pattern in PROFILE_SOURCES.items():
        complete = {path.relative_to(root).as_posix() for path in root.glob(pattern)}
        declared = {
            entry["sourceFile"] for entry in manifest["examples"] if entry["profile"] == name
        }
        if declared != complete:
            raise ValueError(
                f"Manifest must cover every complete program for {name}: {sorted(declared ^ complete)}"
            )


def wait_for_exit(directory: Path, seconds: float = 5.0) -> list[int]:
    """Give a stopped server a moment to go, and return what still runs."""
    deadline = time.monotonic() + seconds
    survivors = processes_running_in(directory)
    while survivors and time.monotonic() < deadline:
        time.sleep(0.1)
        survivors = processes_running_in(directory)
    return survivors


def release_key(text: str) -> tuple[int, int, int]:
    """Order tmux releases, including the patch letter: 3.3 sorts below 3.3a."""
    match = re.fullmatch(r"(?:tmux )?(\d+)\.(\d+)([a-z]*)(?:-.*)?", text.strip())
    if match is None:
        return (0, 0, 0)
    letters = match.group(3)
    return (
        int(match.group(1)),
        int(match.group(2)),
        sum(ord(c) - 96 for c in letters) + len(letters),
    )


def digest(path: Path) -> str:
    """Hash whole displayed files without changing their final newline."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_consumers(
    root: Path,
    manifest: dict,
    packages: Path,
    output: Path,
    inventory_path: Path,
) -> None:
    """Compile copied files outside repository props, then check output and cleanup."""
    if output.is_relative_to(root):
        raise ValueError("Standalone consumers must live outside repository build properties")
    output.mkdir(parents=True, exist_ok=False)
    receipt: dict[str, Any] = {
        "sourceRevision": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=root, text=True
        ).strip(),
        "sourceDirty": bool(
            subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True)
        ),
        "manifestSha256": digest(root / "examples/api/manifest.json"),
        "compilerInventorySha256": digest(inventory_path),
        "files": {},
        "packages": {},
        "assemblies": {},
        "commands": [],
        "programs": [],
    }
    environment = dict(os.environ)
    environment["NUGET_PACKAGES"] = str(output / "packages")
    temp_base = Path(environment.get("TMPDIR", "/tmp"))
    if len(os.fsencode(temp_base)) > maximum_temp_base_bytes():
        raise ValueError(
            f"TMPDIR is longer than {maximum_temp_base_bytes()} bytes, "
            "which leaves no room for the longest socket path"
        )
    tmux = resolve_tmux(environment)
    configured = configured_tmux(environment)
    if configured is not None and os.path.sep in configured:
        shim_dir = output / "tmux-bin"
        shim_dir.mkdir()
        target = (
            configured
            if os.path.isabs(configured) or configured[:1].isspace()
            else os.path.abspath(configured)
        )
        (shim_dir / "tmux").symlink_to(target)
        existing_path = environment.get("PATH", "")
        environment["PATH"] = (
            f"{shim_dir}{os.pathsep}{existing_path}" if existing_path else str(shim_dir)
        )
    receipt["tmuxVersion"] = subprocess.check_output([tmux, "-V"], text=True).strip()
    receipt["tmuxBinarySha256"] = digest(Path(shutil.which(tmux) or tmux).resolve())
    archives = list(packages.glob("*.nupkg"))
    if not archives:
        raise ValueError(f"No native packages found in {packages}")
    assemblies = {}
    for archive in archives:
        with zipfile.ZipFile(archive) as package:
            for framework in FRAMEWORKS:
                for name in ("LibTmux.dll", "LibTmux.FSharp.dll"):
                    path = f"lib/{framework}/{name}"
                    if path in package.namelist():
                        if (framework, name) in assemblies:
                            raise ValueError(f"Multiple packed copies of {path}")
                        assemblies[framework, name] = hashlib.sha256(package.read(path)).hexdigest()
    if len(assemblies) != 4:
        raise ValueError("The native pack must contain both library assemblies for both frameworks")

    def command(
        arguments: list[str],
        cwd: Path,
        timeout: int,
        env: Mapping[str, str] | None = None,
    ) -> subprocess.CompletedProcess:
        try:
            result = subprocess.run(
                arguments,
                cwd=cwd,
                env=dict(environment if env is None else env),
                capture_output=True,
                timeout=timeout,
            )
        except subprocess.TimeoutExpired as error:
            receipt["commands"].append(
                {
                    "argv": arguments,
                    "cwd": str(cwd),
                    "timeoutSeconds": timeout,
                    "stdout": (error.stdout or b"").decode(),
                    "stderr": (error.stderr or b"").decode(),
                }
            )
            raise
        receipt["commands"].append(
            {
                "argv": arguments,
                "cwd": str(cwd),
                "exitCode": result.returncode,
                "stdout": result.stdout.decode(),
                "stderr": result.stderr.decode(),
            }
        )
        if result.returncode:
            raise RuntimeError(
                f"Command failed: {arguments}\n{result.stdout.decode()}\n{result.stderr.decode()}"
            )
        return result

    try:
        for entry in manifest["examples"]:
            profile = manifest["profiles"][entry["profile"]]
            if "requiresTmux" in entry and release_key(receipt["tmuxVersion"]) < release_key(
                entry["requiresTmux"]
            ):
                receipt["programs"].append(
                    {
                        "id": entry["id"],
                        "skipped": f"needs tmux {entry['requiresTmux']}; running {receipt['tmuxVersion']}",
                    }
                )
                continue
            consumer = output / entry["id"]
            consumer.mkdir()
            files = [*manifest["setupFiles"], profile["projectFile"], entry["sourceFile"]]
            for value in files:
                source = source_path(root, value)
                name = profile["entrypoint"] if value == entry["sourceFile"] else source.name
                shutil.copyfile(source, consumer / name)
                receipt["files"][value] = digest(source)
            feed = consumer / "libtmux-source/artifacts/api-example-packages"
            feed.mkdir(parents=True)
            for archive in archives:
                shutil.copyfile(archive, feed / archive.name)
                receipt["packages"][archive.name] = digest(archive)
            project = Path(profile["projectFile"]).name
            command(["dotnet", "restore", project, "--configfile", "NuGet.config"], consumer, 120)
            command(
                [
                    "dotnet",
                    "build",
                    project,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--warnaserror",
                ],
                consumer,
                60,
            )
            for framework in FRAMEWORKS:
                deployed_libraries = (
                    ("LibTmux.dll", "LibTmux.FSharp.dll")
                    if entry["profile"] == "fsharp"
                    else ("LibTmux.dll",)
                )
                for name in deployed_libraries:
                    deployed = consumer / f"bin/Release/{framework}/{name}"
                    if digest(deployed) != assemblies[framework, name]:
                        raise ValueError(
                            f"Consumer assembly differs from the native pack: {deployed}"
                        )
                    receipt["assemblies"][f"{entry['id']}/{framework}/{name}"] = digest(deployed)
                directory = Path(tempfile.mkdtemp(prefix="a-", dir=temp_base))
                run_environment = hostile_environment(environment, directory)
                attempt = {
                    "id": entry["id"],
                    "framework": framework,
                    "sourceSha256": digest(root / entry["sourceFile"]),
                    "temporaryDirectory": str(directory),
                }
                receipt["programs"].append(attempt)
                try:
                    result = command(
                        ["dotnet", f"bin/Release/{framework}/Example.dll"],
                        consumer,
                        30,
                        run_environment,
                    )
                    attempt["stdout"] = result.stdout.decode()
                    if result.stdout != entry["output"].encode() or result.stderr:
                        raise ValueError(
                            f"Output differs from the displayed result for {entry['id']} ({framework})"
                        )
                finally:
                    survivors = wait_for_exit(directory)
                    # The socket directory is the program's own. A cancelled Pane.run
                    # leaves its libtmux-run-* files by design, in every TMPDIR.
                    leftovers = sorted(
                        path.name
                        for path in directory.iterdir()
                        if path.name.startswith("libtmux-example-")
                    )
                    attempt["processesRemaining"] = len(survivors)
                    attempt["filesRemaining"] = leftovers
                    for pid in survivors:
                        with contextlib.suppress(ProcessLookupError):
                            os.kill(pid, signal.SIGKILL)
                    shutil.rmtree(directory)
                    if survivors:
                        raise ValueError(f"Example left processes running: {survivors}")
                    if leftovers:
                        raise ValueError(f"Example left files behind: {leftovers}")
    finally:
        (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


def main() -> None:
    """Validate the native attachment manifest; optionally run packed consumers."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--inventory", type=Path, default=ROOT / "artifacts/api-inventory.json")
    parser.add_argument("--packages", type=Path)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    root = arguments.root.resolve()
    manifest = json.loads(
        (root / "examples/api/manifest.json").read_text(), object_pairs_hook=unique_object
    )
    inventory = json.loads(arguments.inventory.read_text())
    validate(root, manifest, inventory)
    if bool(arguments.packages) != bool(arguments.output):
        parser.error("--packages and --output must be supplied together")
    if arguments.packages:
        run_consumers(
            root,
            manifest,
            arguments.packages.resolve(),
            arguments.output.resolve(),
            arguments.inventory.resolve(),
        )
    print(f"Verified {len(manifest['examples'])} complete API examples.")


if __name__ == "__main__":
    main()
