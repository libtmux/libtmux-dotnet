"""Prove the package check notices a package a consumer could not use."""

from __future__ import annotations

import pathlib
import runpy
import typing as t
import zipfile

import pytest

PROJECT_URL = "https://github.com/libtmux/libtmux-dotnet"

SPECIFICATION = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{identifier}</id>
    <version>1.0.0</version>
    <authors>Tony Narlock</authors>
    <description>A typed client for tmux.</description>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    <icon>icon.png</icon>
    <projectUrl>{project_url}</projectUrl>
    <repository type="git" url="{repository_url}" commit="{commit}" />
{dependencies}  </metadata>
</package>
"""


def dependency_group(
    dependency: str | None,
    dependency_version: str = "8.0.0",
) -> str:
    """Return the dependency block a package with that dependency carries.

    A tool bundles what it needs under ``tools/`` and declares nothing, so the
    block is absent rather than empty.
    """
    if dependency is None:
        return ""
    return (
        "    <dependencies>\n"
        '      <group targetFramework="net8.0">\n'
        f'        <dependency id="{dependency}" version="{dependency_version}" />\n'
        "      </group>\n"
        "    </dependencies>\n"
    )


def load_inspector() -> dict[str, t.Any]:
    """Load the package check as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "inspect_packages.py")
    )


def inspect(package: pathlib.Path) -> list[str]:
    """Run the package check against one built package."""
    found: list[str] = load_inspector()["inspect"](package)
    return found


def build(
    directory: pathlib.Path,
    *,
    identifier: str = "LibTmux",
    frameworks: tuple[str, ...] = ("net8.0", "net10.0"),
    documentation: bool = True,
    dependency: str | None = "Microsoft.Extensions.Logging.Abstractions",
    symbols: bool = True,
    tool: bool = False,
    repository_url: str = PROJECT_URL,
    project_url: str = PROJECT_URL,
    version: str = "1.0.0",
    icon: bool = True,
) -> pathlib.Path:
    """Write a package shaped the way dotnet pack writes one."""
    package = directory / f"{identifier}.{version}.nupkg"
    with zipfile.ZipFile(package, "w") as archive:
        if icon:
            archive.writestr("icon.png", "png")
        archive.writestr(
            f"{identifier}.nuspec",
            SPECIFICATION.format(
                identifier=identifier,
                dependencies=dependency_group(dependency),
                project_url=project_url,
                repository_url=repository_url,
                commit="a" * 40,
            ),
        )
        for framework in frameworks:
            directory_name = (
                f"tools/{framework}/any" if tool else f"lib/{framework}"
            )
            archive.writestr(f"{directory_name}/{identifier}.dll", "assembly")
            if documentation:
                archive.writestr(f"{directory_name}/{identifier}.xml", "<doc />")

    if symbols:
        (directory / f"{identifier}.{version}.snupkg").write_bytes(b"symbols")

    return package


def test_a_complete_package_passes(tmp_path: pathlib.Path) -> None:
    """A package carrying everything a consumer needs has nothing to report."""
    assert inspect(build(tmp_path)) == []


def test_a_prerelease_package_is_recognised(tmp_path: pathlib.Path) -> None:
    """A prerelease label is dotted, and trimming it off leaves another name."""
    package = build(tmp_path, version="0.0.1-alpha.1")

    assert inspect(package) == []


def test_a_missing_icon_is_reported(tmp_path: pathlib.Path) -> None:
    """An icon element naming a file the package lacks renders as broken."""
    package = build(tmp_path, icon=False)

    assert "LibTmux carries no icon" in inspect(package)


def test_a_missing_framework_is_reported(tmp_path: pathlib.Path) -> None:
    """A package that quietly stopped shipping a framework breaks a consumer."""
    package = build(tmp_path, frameworks=("net10.0",))

    assert "LibTmux carries no assembly for net8.0" in inspect(package)


def test_missing_documentation_is_reported(tmp_path: pathlib.Path) -> None:
    """A caller's editor shows the documentation file, or shows nothing."""
    package = build(tmp_path, documentation=False)

    assert "LibTmux carries no documentation for net8.0" in inspect(package)


def test_an_extra_dependency_is_reported(tmp_path: pathlib.Path) -> None:
    """Every dependency this takes is one a consumer is made to take too."""
    package = build(tmp_path, dependency="Newtonsoft.Json")

    assert "LibTmux declares dependency Newtonsoft.Json" in inspect(package)


def test_the_net8_logging_floor_cannot_be_bumped_to_net10(
    tmp_path: pathlib.Path,
) -> None:
    """Dependency automation must not undo the net8 consumer contract."""
    package = tmp_path / "LibTmux.1.0.0.nupkg"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("icon.png", "png")
        archive.writestr(
            "LibTmux.nuspec",
            SPECIFICATION.format(
                identifier="LibTmux",
                dependencies=dependency_group(
                    "Microsoft.Extensions.Logging.Abstractions",
                    "10.0.11",
                ),
                project_url=PROJECT_URL,
                repository_url=PROJECT_URL,
                commit="a" * 40,
            ),
        )
        for framework in ("net8.0", "net10.0"):
            archive.writestr(f"lib/{framework}/LibTmux.dll", "assembly")
            archive.writestr(f"lib/{framework}/LibTmux.xml", "<doc />")
    (tmp_path / "LibTmux.1.0.0.snupkg").write_bytes(b"symbols")

    assert (
        "LibTmux requires Microsoft.Extensions.Logging.Abstractions 10.0.11 "
        "for net8.0; expected 8.0.0"
    ) in inspect(package)


def test_missing_symbols_are_reported(tmp_path: pathlib.Path) -> None:
    """Stepping into the library while debugging needs the symbols."""
    package = build(tmp_path, symbols=False)

    assert "LibTmux produced no symbols package beside it" in inspect(package)


def test_another_repository_is_reported(tmp_path: pathlib.Path) -> None:
    """A debugger asks the named repository for a commit it has to have."""
    package = build(tmp_path, repository_url="https://github.com/tmux-python/libtmux")

    assert "LibTmux does not name this repository" in inspect(package)


def test_an_optional_package_may_take_the_dependency_it_exists_for(
    tmp_path: pathlib.Path,
) -> None:
    """Holding every package to the core's dependencies would fail the fine ones."""
    package = build(tmp_path, identifier="LibTmux.Workspace", dependency="YamlDotNet")

    assert inspect(package) == []


def test_an_optional_package_is_still_held_to_its_own_contract(
    tmp_path: pathlib.Path,
) -> None:
    """What one package is allowed is not what another is allowed."""
    package = build(tmp_path, identifier="LibTmux.Query.Json", dependency="YamlDotNet")

    assert "LibTmux.Query.Json declares dependency YamlDotNet" in inspect(package)


@pytest.mark.parametrize("identifier", ["LibTmux.Mcp", "LibTmux.Workspace.Cli"])
def test_a_tool_carries_its_binaries_under_tools(
    tmp_path: pathlib.Path, identifier: str
) -> None:
    """A tool has no lib folder, and reporting one missing would be noise."""
    package = build(
        tmp_path,
        identifier=identifier,
        tool=True,
        symbols=False,
        dependency=None,
    )

    assert inspect(package) == []


def test_a_tool_packed_as_a_library_is_reported(tmp_path: pathlib.Path) -> None:
    """A tool whose binaries stopped reaching tools/ installs and does nothing."""
    package = build(
        tmp_path,
        identifier="LibTmux.Mcp",
        symbols=False,
        dependency=None,
    )

    assert "LibTmux.Mcp carries no assembly for net8.0" in inspect(package)


def test_symbols_beside_a_tool_are_reported(tmp_path: pathlib.Path) -> None:
    """A tool ships its own symbols, so a package of them is a second copy."""
    package = build(tmp_path, identifier="LibTmux.Mcp", tool=True, dependency=None)

    assert "LibTmux.Mcp produced symbols a tool does not need" in inspect(package)


def test_an_undeclared_package_is_reported(tmp_path: pathlib.Path) -> None:
    """A package nobody wrote a contract for is one nobody checked."""
    package = build(tmp_path, identifier="LibTmux.Surprise")

    assert inspect(package) != []


def test_a_package_that_stopped_shipping_is_reported(
    tmp_path: pathlib.Path,
) -> None:
    """Nobody notices a package that quietly stopped being packed."""
    build(tmp_path)

    assert load_inspector()["main"](["--artifacts", str(tmp_path)]) == 1


@pytest.mark.parametrize(
    ("field", "replacement"),
    [("MIT", "Apache-2.0"), ("README.md", "docs.md"), ("Tony Narlock", "LibTmux")],
)
def test_changed_metadata_is_reported(
    tmp_path: pathlib.Path,
    field: str,
    replacement: str,
) -> None:
    """The package page says what this is and who may use it, or it does not."""
    package = tmp_path / "LibTmux.1.0.0.nupkg"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr(
            "LibTmux.nuspec",
            SPECIFICATION.format(
                identifier="LibTmux",
                dependencies=dependency_group(
                    "Microsoft.Extensions.Logging.Abstractions"
                ),
                project_url=PROJECT_URL,
                repository_url=PROJECT_URL,
                commit="a" * 40,
            ).replace(field, replacement),
        )
        archive.writestr("icon.png", "png")
        for framework in ("net8.0", "net10.0"):
            archive.writestr(f"lib/{framework}/LibTmux.dll", "assembly")
            archive.writestr(f"lib/{framework}/LibTmux.xml", "<doc />")

    (tmp_path / "LibTmux.1.0.0.snupkg").write_bytes(b"symbols")

    assert inspect(package) != []
