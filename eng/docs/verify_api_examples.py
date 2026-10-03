"""Validate whole-file API examples and run their standalone package consumers."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
from xml.etree import ElementTree
import zipfile

ROOT = Path(__file__).resolve().parents[2]
FRAMEWORKS = ("net8.0", "net10.0")
PROFILE_SOURCES = {
    "fsharp": "examples/LibTmux.FSharp.Examples/Programs/*.fs",
    "csharp": "examples/LibTmux.Examples/Programs/*.cs",
}


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
        {}, {"key": "libtmux-source", "value": "libtmux-source/artifacts/api-example-packages"},
        {"key": "nuget.org", "value": "https://api.nuget.org/v3/index.json"},
    ] or [node.tag for node in feed.findall(".//packageSources/*")] != ["clear", "add", "add"]:
        raise ValueError("NuGet.config must use the displayed local feed and nuget.org")
    mapping = {
        node.get("key"): [package.get("pattern") for package in node]
        for node in feed.findall(".//packageSourceMapping/packageSource")
    }
    if (
        mapping != {"libtmux-source": ["LibTmux", "LibTmux.*"], "nuget.org": ["*"]}
        or [node.tag for node in feed.findall(".//packageSourceMapping/*")]
        != ["clear", "packageSource", "packageSource"]
    ):
        raise ValueError("NuGet.config must resolve LibTmux packages only from the native local pack")
    if not manifest["profiles"] or not isinstance(manifest["profiles"], dict):
        raise ValueError("At least one example profile is required")

    props = ElementTree.parse(root / "Directory.Build.props")
    version = props.findtext(".//VersionPrefix")
    suffix = props.findtext(".//VersionSuffix")
    version = f"{version}-{suffix}" if suffix else version
    for name, profile in manifest["profiles"].items():
        keys(profile, {"package", "projectFile", "entrypoint"}, f"profile {name}")
        expected = {
            "fsharp": {"package": "LibTmux.FSharp", "projectFile": "examples/api/fsharp/Example.fsproj", "entrypoint": "Program.fs"},
            "csharp": {"package": "LibTmux", "projectFile": "examples/api/csharp/Example.csproj", "entrypoint": "Program.cs"},
        }
        if name not in expected or profile != expected[name]:
            raise ValueError(f"Unsupported example profile: {name}")
        project = ElementTree.parse(source_path(root, profile["projectFile"]))
        allowed = {
            "Project", "PropertyGroup", "ItemGroup", "OutputType", "TargetFrameworks",
            "LangVersion", "TreatWarningsAsErrors", "DisableImplicitFSharpCoreReference",
            "DisableImplicitLibraryPacksFolder", "Compile", "PackageReference",
        }
        if name == "csharp":
            allowed |= {"Nullable", "ImplicitUsings", "EnableDefaultCompileItems", "AnalysisLevel"}
        if any(node.tag not in allowed for node in project.iter()):
            raise ValueError(f"Profile {name} must not import hidden source or build properties")
        if project.findtext(".//TargetFrameworks") != ";".join(FRAMEWORKS):
            raise ValueError(f"Profile {name} must run on both supported frameworks")
        if [node.attrib for node in project.findall(".//Compile")] != [{"Include": profile["entrypoint"]}]:
            raise ValueError(f"Profile {name} must compile only the displayed entrypoint")
        if project.findall(".//ProjectReference") or project.findall(".//Import"):
            raise ValueError(f"Profile {name} must not import hidden source or build properties")
        references = {node.get("Include"): node.get("Version") for node in project.findall(".//PackageReference")}
        dependencies = {"LibTmux.FSharp": f"[{version}]", "FSharp.Core": "[10.1.302]"} if name == "fsharp" else {"LibTmux": f"[{version}]"}
        if references != dependencies:
            raise ValueError(f"Profile {name} must use the exact library and language dependencies")
        if name == "csharp" and any(
            project.findtext(f".//{key}") != value
            for key, value in {"Nullable": "enable", "ImplicitUsings": "disable", "EnableDefaultCompileItems": "false"}.items()
        ):
            raise ValueError("The C# profile must declare nullability, imports and its sole entrypoint")

    members = {
        (member["package"], member["id"])
        for member in inventory["members"]
        if member["visibility"] == "public"
        and not member["implicitDeclaration"] and not member["accessor"]
    }
    identifiers: set[str] = set()
    sources: set[str] = set()
    if not isinstance(manifest["examples"], list) or not manifest["examples"]:
        raise ValueError("At least one complete API example is required")
    for entry in manifest["examples"]:
        keys(entry, {"id", "profile", "title", "description", "sourceFile", "targets", "output"}, "example")
        if not isinstance(entry["id"], str) or not re.fullmatch(r"[a-z]+-[A-Za-z0-9]+", entry["id"]):
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
        targets = entry["targets"]
        if not isinstance(targets, list) or not targets or not all(isinstance(value, str) for value in targets):
            raise ValueError(f"Example {entry['id']} needs compiler targets")
        if len(set(targets)) != len(targets):
            raise ValueError(f"Example {entry['id']} repeats a compiler target")
        package = manifest["profiles"][entry["profile"]]["package"]
        for target in targets:
            if (package, target) not in members:
                raise ValueError(f"Example {entry['id']} has no public compiler target: {target}")

    for name, pattern in PROFILE_SOURCES.items():
        complete = {path.relative_to(root).as_posix() for path in root.glob(pattern)}
        declared = {entry["sourceFile"] for entry in manifest["examples"] if entry["profile"] == name}
        if declared != complete:
            raise ValueError(f"Manifest must cover every complete program for {name}: {sorted(declared ^ complete)}")


def digest(path: Path) -> str:
    """Hash whole displayed files without changing their final newline."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_consumers(
    root: Path, manifest: dict, packages: Path, output: Path, inventory_path: Path,
) -> None:
    """Compile copied files outside repository props, then check output and cleanup."""
    if output.is_relative_to(root):
        raise ValueError("Standalone consumers must live outside repository build properties")
    output.mkdir(parents=True, exist_ok=False)
    receipt = {
        "sourceRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
        "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True)),
        "manifestSha256": digest(root / "examples/api/manifest.json"),
        "compilerInventorySha256": digest(inventory_path),
        "files": {}, "packages": {}, "assemblies": {}, "commands": [], "programs": [],
    }
    environment = {key: value for key, value in os.environ.items() if key not in {"TMUX", "TMUX_PANE"}}
    environment["NUGET_PACKAGES"] = str(output / "packages")
    socket_root = Path(environment.get("TMUX_TMPDIR", "/tmp/libtmux-dotnet-test"))
    socket_root.mkdir(parents=True, exist_ok=True)
    tmux = environment.get("LIBTMUX_TMUX", "tmux")
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

    def command(arguments: list[str], cwd: Path, timeout: int) -> subprocess.CompletedProcess:
        try:
            result = subprocess.run(arguments, cwd=cwd, env=environment, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            receipt["commands"].append({
                "argv": arguments, "cwd": str(cwd), "timeoutSeconds": timeout,
                "stdout": (error.stdout or b"").decode(), "stderr": (error.stderr or b"").decode(),
            })
            raise
        receipt["commands"].append({
            "argv": arguments, "cwd": str(cwd), "exitCode": result.returncode,
            "stdout": result.stdout.decode(), "stderr": result.stderr.decode(),
        })
        if result.returncode:
            raise RuntimeError(f"Command failed: {arguments}\n{result.stdout.decode()}\n{result.stderr.decode()}")
        return result

    try:
        for entry in manifest["examples"]:
            profile = manifest["profiles"][entry["profile"]]
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
            command(["dotnet", "build", project, "--configuration", "Release", "--no-restore", "--warnaserror"], consumer, 60)
            for framework in FRAMEWORKS:
                deployed_libraries = ("LibTmux.dll", "LibTmux.FSharp.dll") if entry["profile"] == "fsharp" else ("LibTmux.dll",)
                for name in deployed_libraries:
                    deployed = consumer / f"bin/Release/{framework}/{name}"
                    if digest(deployed) != assemblies[framework, name]:
                        raise ValueError(f"Consumer assembly differs from the native pack: {deployed}")
                    receipt["assemblies"][f"{entry['id']}/{framework}/{name}"] = digest(deployed)
                directory = Path(tempfile.mkdtemp(prefix="a-", dir=socket_root))
                environment["TMUX_TMPDIR"] = str(directory)
                attempt = {
                    "id": entry["id"], "framework": framework,
                    "sourceSha256": digest(root / entry["sourceFile"]),
                    "socketRoot": str(directory),
                }
                receipt["programs"].append(attempt)
                try:
                    result = command(["dotnet", f"bin/Release/{framework}/Example.dll"], consumer, 30)
                    attempt["stdout"] = result.stdout.decode()
                    if result.stdout != entry["output"].encode() or result.stderr:
                        raise ValueError(f"Output differs from the displayed result for {entry['id']} ({framework})")
                finally:
                    live = []
                    for path in directory.rglob("*"):
                        if path.is_socket():
                            with socket.socket(socket.AF_UNIX) as connection:
                                if connection.connect_ex(str(path)) == 0:
                                    live.append(path)
                    attempt["ownedServersRemaining"] = len(live)
                    for path in live:
                        command([tmux, "-S", str(path), "kill-server"], consumer, 10)
                    shutil.rmtree(directory)
                    if live:
                        raise ValueError(f"Example left live owned servers: {live}")
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
    manifest = json.loads((root / "examples/api/manifest.json").read_text(), object_pairs_hook=unique_object)
    inventory = json.loads(arguments.inventory.read_text())
    validate(root, manifest, inventory)
    if bool(arguments.packages) != bool(arguments.output):
        parser.error("--packages and --output must be supplied together")
    if arguments.packages:
        run_consumers(
            root, manifest, arguments.packages.resolve(), arguments.output.resolve(),
            arguments.inventory.resolve(),
        )
    print(f"Verified {len(manifest['examples'])} complete API examples.")


if __name__ == "__main__":
    main()
