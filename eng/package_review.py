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
    parser.add_argument("--output", required=True, type=Path, help="A directory that does not yet exist.")
    args = parser.parse_args()
    version = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-([A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)", args.version)
    if version is None:
        parser.error("--version must be a prerelease such as 0.0.0-review.1.")
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision):
        parser.error("--revision must be a full lowercase Git commit SHA.")

    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve()

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
    output.mkdir(parents=True, exist_ok=False)
    inputs = output / "inputs"
    inputs.mkdir()
    project = ET.Element("Project")
    ET.SubElement(project, "Import", Project=str(root / "Directory.Build.props"))
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "VersionPrefix").text = ".".join(version.groups()[:3])
    ET.SubElement(properties, "VersionSuffix").text = version.group(4)
    ET.SubElement(properties, "Version").text = args.version
    props = inputs / "Directory.Build.props"
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
    environment = dict(os.environ, DirectoryBuildPropsPath=str(props))
    commands = []

    def run(*command: str) -> None:
        started = time.monotonic()
        result = subprocess.run(command, cwd=root, env=environment, check=False)
        commands.append({"argv": list(command), "exit": result.returncode,
                         "wall_seconds": round(time.monotonic() - started, 4)})
        (output / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        if result.returncode:
            raise RuntimeError(f"{command[0]} {command[1]} failed with exit {result.returncode}.")

    run("dotnet", "restore", "LibTmux.slnx", "--locked-mode")
    run("dotnet", "build", "LibTmux.slnx", "--configuration", "Release", "--no-restore",
        "--warnaserror", "-p:ContinuousIntegrationBuild=true")
    inventory = output / "api-inventory.json"
    engineering = "eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll"
    run("dotnet", engineering, "api-inventory", ".", str(inventory))
    run("dotnet", "pack", "LibTmux.slnx", "--configuration", "Release", "--no-build",
        "--output", str(output))
    run("dotnet", engineering, "packages", ".", str(output), str(inventory))
    check_source()
    files = sorted([*output.glob("*.nupkg"), *output.glob("*.snupkg")])
    if not files:
        raise RuntimeError("No package archives were produced.")
    provenance = {
        "version": args.version,
        "revision": args.revision,
        "tree": git("rev-parse", "HEAD^{tree}"),
        "sdk": subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip(),
        "inspection": "passed",
        "packages": [{"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                     for path in files],
        "inventory_sha256": hashlib.sha256(inventory.read_bytes()).hexdigest(),
        "inputs": [{"file": path.relative_to(root).as_posix(),
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                   for path in sorted([root / "global.json", root / "Directory.Build.props",
                                       root / "Directory.Packages.props",
                                       *root.glob("src/*/packages.lock.json")])],
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
