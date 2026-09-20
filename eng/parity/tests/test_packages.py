"""Outer-loop mutations of real archives; requires a completed Release pack."""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import zipfile
from xml.etree import ElementTree

import pytest

pytestmark = pytest.mark.packaging

ROOT = pathlib.Path(__file__).resolve().parents[3]


@pytest.fixture(scope="module")
def artifacts() -> pathlib.Path:
    """Use the explicitly supplied pack output; ordinary tests need no build."""
    directory = os.environ.get("LIBTMUX_PACKAGE_ARTIFACTS")
    if directory is None:
        pytest.skip("outer loop: set LIBTMUX_PACKAGE_ARTIFACTS after Release pack")
    path = pathlib.Path(directory).resolve()
    assert list(path.glob("LibTmux.[0-9]*.nupkg")), "no core package in pack output"
    return path


def inspect(
    directory: pathlib.Path, root: pathlib.Path = ROOT
) -> subprocess.CompletedProcess[str]:
    """Invoke the owning validator without restoring or building."""
    return subprocess.run(
        [
            "dotnet",
            str(
                ROOT
                / "eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll"
            ),
            "packages",
            str(root),
            str(directory),
            str(ROOT / "artifacts/api-inventory.json"),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def test_real_packages_pass(artifacts: pathlib.Path) -> None:
    result = inspect(artifacts)
    assert result.returncode == 0, result.stdout + result.stderr


def test_matching_source_and_archive_cannot_expand_allowed_dependencies(
    artifacts: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    source = tmp_path / "source"
    subprocess.run(
        ["git", "clone", "--quiet", "--shared", str(ROOT), str(source)],
        check=True,
    )
    shutil.copy2(ROOT / "docs/public-api.json", source / "docs/public-api.json")
    packages = tmp_path / "packages"
    shutil.copytree(artifacts, packages)
    baseline = inspect(packages, source)
    assert baseline.returncode == 0, baseline.stdout + baseline.stderr

    project = source / "src/LibTmux/LibTmux.csproj"
    project.write_text(
        project.read_text().replace(
            "</Project>",
            '<ItemGroup><PackageReference Include="YamlDotNet" /></ItemGroup></Project>',
        )
    )
    central = ElementTree.parse(source / "Directory.Packages.props")
    version = central.find(".//PackageVersion[@Include='YamlDotNet']").attrib["Version"]
    core = next(packages.glob("LibTmux.[0-9]*.nupkg"))
    with zipfile.ZipFile(core) as archive:
        entries = {name: archive.read(name) for name in archive.namelist()}
    name = next(name for name in entries if name.endswith(".nuspec"))
    spec = ElementTree.fromstring(entries[name])
    namespace = spec.tag.removesuffix("package")
    ElementTree.register_namespace("", namespace[1:-1])
    for group in spec.findall(f".//{namespace}group"):
        ElementTree.SubElement(
            group, f"{namespace}dependency", {"id": "YamlDotNet", "version": version}
        )
    entries[name] = ElementTree.tostring(spec)
    with zipfile.ZipFile(core, "w") as archive:
        for name, contents in entries.items():
            archive.writestr(name, contents)
    result = inspect(packages, source)
    assert result.returncode != 0, "validator accepted matching forbidden dependency"
    assert "allowed dependencies" in result.stderr


@pytest.mark.parametrize(
    ("mutation", "diagnostic"),
    [
        ("readme", "README.md"),
        ("readme-missing", "README.md"),
        ("dependency", "dependencies or versions"),
        ("framework", "framework assembly assets"),
        ("xml", "XML contents"),
        ("symbols", None),
        ("repository", "repository revision"),
        ("sourcelink", "SourceLink repository revision"),
        ("bundled-assembly", "missing packaged tools/net8.0/any/LibTmux.dll"),
        ("sourcelink-wildcard", "SourceLink mapping"),
        ("sourcelink-path", "SourceLink mapping"),
        ("extra", "Package set"),
    ],
)
def test_corrupt_packages_fail(
    artifacts: pathlib.Path,
    tmp_path: pathlib.Path,
    mutation: str,
    diagnostic: str | None,
) -> None:
    """Change one shipped property without fabricating DLLs or valid PDBs."""
    for package in artifacts.glob("*.nupkg"):
        shutil.copy2(package, tmp_path)
    for package in artifacts.glob("*.snupkg"):
        shutil.copy2(package, tmp_path)
    core = next(tmp_path.glob("LibTmux.[0-9]*.nupkg"))
    if mutation in {"symbols", "sourcelink", "sourcelink-wildcard", "sourcelink-path"}:
        target = core.with_suffix(".snupkg")
    elif mutation == "bundled-assembly":
        target = next(tmp_path.glob("LibTmux.Mcp.*.nupkg"))
    else:
        target = core
    if mutation == "extra":
        shutil.copy2(core, tmp_path / "unexpected.nupkg")
    else:
        with zipfile.ZipFile(target) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        if mutation == "readme":
            entries["README.md"] = b""
        elif mutation == "readme-missing":
            del entries["README.md"]
        elif mutation == "dependency":
            name = next(name for name in entries if name.endswith(".nuspec"))
            assert b' version="8.0.0"' in entries[name]
            entries[name] = entries[name].replace(
                b' version="8.0.0"', b' version="99.0.0"'
            )
        elif mutation == "framework":
            del entries["lib/net8.0/LibTmux.dll"]
        elif mutation == "bundled-assembly":
            del entries["tools/net8.0/any/LibTmux.dll"]
        elif mutation in {"sourcelink-wildcard", "sourcelink-path"}:
            name = "lib/net8.0/LibTmux.pdb"
            revision = json.loads((ROOT / "artifacts/api-inventory.json").read_text())[
                "revision"
            ].encode()
            before, after = (
                (revision + b'/*"', revision + b'/x"')
                if mutation == "sourcelink-wildcard"
                else (b'"/_/*":', b'"/_*" :')
            )
            assert len(before) == len(after) and before in entries[name]
            entries[name] = entries[name].replace(before, after)
        elif mutation == "xml":
            name = "lib/net8.0/LibTmux.xml"
            xml = ElementTree.fromstring(entries[name])
            member = xml.find(".//member[@name='T:LibTmux.Server']/summary")
            assert member is not None
            member.text = "Wrong package documentation."
            entries[name] = ElementTree.tostring(xml)
        elif mutation == "symbols":
            entries["lib/net8.0/LibTmux.pdb"] = b"invalid portable metadata"
        elif mutation in {"repository", "sourcelink"}:
            revision = json.loads((ROOT / "artifacts/api-inventory.json").read_text())[
                "revision"
            ].encode()
            name = (
                "lib/net8.0/LibTmux.pdb"
                if mutation == "sourcelink"
                else next(name for name in entries if name.endswith(".nuspec"))
            )
            assert revision in entries[name]
            entries[name] = entries[name].replace(revision, b"0" * len(revision))
        with zipfile.ZipFile(target, "w") as archive:
            for name, contents in entries.items():
                archive.writestr(name, contents)
    result = inspect(tmp_path)
    assert result.returncode != 0, f"validator accepted {mutation} mutation"
    assert result.stderr
    if diagnostic is not None:
        assert diagnostic.casefold() in (result.stdout + result.stderr).casefold()
