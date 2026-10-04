#!/usr/bin/env python3
"""Pack and inspect one immutable prerelease from a clean committed checkout."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="A new, unpublished prerelease identity.")
    parser.add_argument("--revision", required=True, help="The full committed source SHA to build.")
    parser.add_argument("--output", required=True, type=Path,
                        help="A new directory, or the preceding phase's output.")
    parser.add_argument("--phase", choices=("restore", "build", "pack"),
                        help="Run one phase; omit to run the complete recipe.")
    args = parser.parse_args()
    version = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-([A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)", args.version)
    if version is None:
        parser.error("--version must be a prerelease such as 0.0.0-review.1.")
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision):
        parser.error("--revision must be a full lowercase Git commit SHA.")

    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    state_path = output / "review-state.json"
    props = output / "inputs/Directory.Build.props"
    commands_path = output / "commands.json"

    def git(*arguments: str) -> str:
        return subprocess.check_output(["git", *arguments], cwd=root, text=True).strip()

    def check_source() -> None:
        if Path(git("rev-parse", "--show-toplevel")).resolve() != root:
            raise RuntimeError("The recipe must run from its own Git checkout.")
        if git("rev-parse", "HEAD") != args.revision:
            raise RuntimeError("HEAD differs from the requested source revision.")
        if git("status", "--porcelain", "--untracked-files=normal"):
            raise RuntimeError("Commit or preserve outstanding changes before packaging.")

    check_source()
    original = ET.parse(root / "Directory.Build.props")
    declared = original.findtext(".//VersionPrefix", "")
    suffix = original.findtext(".//VersionSuffix", "")
    if suffix:
        declared += "-" + suffix
    if args.version == declared:
        raise RuntimeError("Choose a distinct review identity; the declared version cannot be overwritten.")
    environment = dict(os.environ, DirectoryBuildPropsPath=str(props))
    phases = ("restore", "build", "pack") if args.phase is None else (args.phase,)
    projects = [root / item.attrib["Path"] for item in
                ET.parse(root / "LibTmux.slnx").iter("Project")]
    tool_projects = {project for project in projects
                     if ET.parse(project).findtext(".//PackAsTool", "").strip() == "true"}
    source_inputs = sorted([root / "global.json", root / "Directory.Build.props",
                            root / "Directory.Packages.props",
                            *root.glob("src/*/packages.lock.json")])

    def sha256(path: Path) -> str:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        return digest.hexdigest()

    def manifest(paths, base: Path):
        return [{"file": path.relative_to(base).as_posix(), "sha256": sha256(path)}
                for path in sorted(set(paths))]

    def identity():
        # Hash environment values rather than writing credentials to the receipt.
        volatile = {"PWD", "OLDPWD", "SHLVL", "_", "TERM", "COLUMNS", "LINES"}
        stable_environment = {key: value for key, value in environment.items()
                              if key not in volatile}
        return {
            "version": args.version, "revision": args.revision,
            "tree": git("rev-parse", "HEAD^{tree}"), "root": str(root), "output": str(output),
            "sdk": subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip(),
            "inputs": manifest(source_inputs, root),
            "overrides": manifest([props, output / "NuGet.config"], output),
            "environment_sha256": hashlib.sha256(
                json.dumps(stable_environment, sort_keys=True).encode()).hexdigest(),
        }

    def restored_inputs():
        paths = []
        for project in projects:
            directory = project.parent / "obj"
            assets = directory / "project.assets.json"
            if not assets.is_file():
                raise RuntimeError(f"Restored assets are missing for {project.relative_to(root)}.")
            paths.extend([assets, *directory.glob("*.nuget.g.props"),
                          *directory.glob("*.nuget.g.targets"),
                          *directory.glob("*.nuget.dgspec.json"),
                          *directory.glob("project.nuget.cache")])
        return manifest(paths, root)

    def built_outputs():
        paths = []
        for project in projects:
            binary = project.parent / "bin/Release"
            intermediate = project.parent / "obj/Release"
            # PackTool --no-build publishes copies and generates tool settings.
            # Compiler DLL/PDB/XML inputs remain guarded outside those outputs.
            for path in binary.rglob("*"):
                relative = path.relative_to(binary).parts
                published = (project in tool_projects and len(relative) > 2
                             and relative[1] == "publish")
                if path.is_file() and not published:
                    paths.append(path)
            for path in intermediate.rglob("*"):
                relative = path.relative_to(intermediate).parts
                settings = (project in tool_projects and len(relative) == 2
                            and relative[1] == "DotnetToolSettings.xml")
                if (path.is_file() and not settings
                        and path.suffix in {".dll", ".pdb", ".xml", ".resources"}):
                    paths.append(path)
        engineering = root / "eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll"
        if not engineering.is_file() or not any(path.suffix == ".dll" for path in paths):
            raise RuntimeError("Release build outputs are missing.")
        return manifest(paths, root)

    if phases[0] == "restore":
        output.mkdir(parents=True, exist_ok=False)
        props.parent.mkdir()
        project = ET.Element("Project")
        ET.SubElement(project, "Import", Project=str(root / "Directory.Build.props"))
        properties = ET.SubElement(project, "PropertyGroup")
        ET.SubElement(properties, "VersionPrefix").text = ".".join(version.groups()[:3])
        ET.SubElement(properties, "VersionSuffix").text = version.group(4)
        ET.SubElement(properties, "Version").text = args.version
        ET.ElementTree(project).write(props, encoding="utf-8", xml_declaration=True)
        configuration = ET.Element("configuration")
        sources = ET.SubElement(configuration, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="review", value=".")
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mapping = ET.SubElement(configuration, "packageSourceMapping")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="review"), "package", pattern="LibTmux*")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
        ET.ElementTree(configuration).write(output / "NuGet.config", encoding="utf-8", xml_declaration=True)
        initial = identity()
        state = {"format": 1, "identity": initial, "phase": None}
        commands = []
    else:
        try:
            state = json.loads(state_path.read_text())
            commands = json.loads(commands_path.read_text())
        except (OSError, ValueError) as error:
            raise RuntimeError("A completed preceding phase receipt is required.") from error
        expected = {"build": "restore", "pack": "build"}[phases[0]]
        if not isinstance(state, dict) or state.get("format") != 1 or state.get("phase") != expected:
            raise RuntimeError(f"{phases[0]} requires a completed {expected} phase.")
        initial = identity()
        if state.get("identity") != initial:
            raise RuntimeError("Source, SDK, environment, inputs or review identity changed between phases.")
        if state.get("commands_sha256") != sha256(commands_path):
            raise RuntimeError("The command receipt changed between phases.")
        if not isinstance(commands, list):
            raise RuntimeError("The command receipt is invalid.")
        if state.get("restored_inputs") != restored_inputs():
            raise RuntimeError("Restored inputs changed between phases.")
        if phases[0] == "pack" and state.get("built_outputs") != built_outputs():
            raise RuntimeError("Release build outputs changed between phases.")
        if (list(output.glob("*.nupkg")) or list(output.glob("*.snupkg"))
                or (output / "provenance.json").exists()):
            raise RuntimeError("Package archives already exist; their review identity cannot be overwritten.")

    def run(*command: str) -> None:
        started = time.monotonic()
        result = subprocess.run(command, cwd=root, env=environment, check=False)
        commands.append({"argv": list(command), "exit": result.returncode,
                         "wall_seconds": round(time.monotonic() - started, 4)})
        commands_path.write_text(json.dumps(commands, indent=2) + "\n")
        if result.returncode:
            raise RuntimeError(f"{command[0]} {command[1]} failed with exit {result.returncode}.")

    inventory = output / "api-inventory.json"
    engineering = "eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll"
    for phase in phases:
        if phase == "restore":
            run("dotnet", "restore", "LibTmux.slnx", "--locked-mode")
        elif phase == "build":
            run("dotnet", "build", "LibTmux.slnx", "--configuration", "Release", "--no-restore",
                "--warnaserror", "-p:ContinuousIntegrationBuild=true")
        else:
            run("dotnet", engineering, "api-inventory", ".", str(inventory))
            run("dotnet", "pack", "LibTmux.slnx", "--configuration", "Release", "--no-build",
                "--output", str(output))
            inspected_files = manifest([*output.glob("*.nupkg"), *output.glob("*.snupkg")], output)
            inspected_inventory = sha256(inventory)
            run("dotnet", engineering, "packages", ".", str(output), str(inventory))
            current_files = manifest([*output.glob("*.nupkg"), *output.glob("*.snupkg")], output)
            if inspected_files != current_files or inspected_inventory != sha256(inventory):
                raise RuntimeError("Packages or inventory changed during inspection.")
        check_source()
        if identity() != initial:
            raise RuntimeError("Source, SDK, environment, inputs or review identity changed during packaging.")
        restored = restored_inputs()
        if phase != "restore" and state.get("restored_inputs") != restored:
            raise RuntimeError("Restored inputs changed during packaging.")
        state["restored_inputs"] = restored
        if phase != "restore":
            built = built_outputs()
            if phase == "pack" and state.get("built_outputs") != built:
                raise RuntimeError("Release build outputs changed during packaging.")
            state["built_outputs"] = built
        state["phase"] = phase
        state["commands_sha256"] = sha256(commands_path)
        state_path.write_text(json.dumps(state, indent=2) + "\n")

    if phases[-1] != "pack":
        print(f"PASS review {phases[-1]}: {args.version} at {args.revision}")
        return 0
    files = sorted([*output.glob("*.nupkg"), *output.glob("*.snupkg")])
    if not files:
        raise RuntimeError("No package archives were produced.")
    provenance = {
        "version": args.version,
        "revision": args.revision,
        "tree": initial["tree"],
        "sdk": initial["sdk"],
        "inspection": "passed",
        "packages": [{"file": path.name, "sha256": sha256(path)}
                     for path in files],
        "inventory_sha256": sha256(inventory),
        "inputs": initial["inputs"],
    }
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(f"PASS inspected review packages: {args.version} at {args.revision}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Review packaging failed: {error}", file=sys.stderr)
        sys.exit(1)
