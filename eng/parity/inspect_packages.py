"""Read every built package the way a consumer's restore would.

Everything about a package is decided at pack time and invisible from inside
the repository: which frameworks it carries, what it drags in with it, and
whether the documentation a caller's editor shows is there at all.

This repository ships more than one package, and they do not have one contract.
The core library carries assemblies a project references and takes a dependency
on nothing a caller did not ask for; an optional package is allowed the one
dependency it exists to add; the server is a tool, which carries executables
under ``tools/`` and no ``lib/`` at all. Holding them all to the core's shape
would either fail the ones that are fine or pass nothing at all.
"""

from __future__ import annotations

import argparse
import dataclasses
import pathlib
import re
import sys
import zipfile
from xml.etree import ElementTree

TARGET_FRAMEWORKS = ("net8.0", "net10.0")

#: The version a built file name ends with: three numbers, then whatever a
#: prerelease label or build metadata adds.
VERSION_SUFFIX = re.compile(r"\.\d+\.\d+\.\d+(?:[-+][0-9A-Za-z][0-9A-Za-z.-]*)?$")


@dataclasses.dataclass(frozen=True)
class Contract:
    """What one package is expected to be.

    Attributes
    ----------
    dependencies : frozenset[str]
        Package IDs this package may declare. Anything else is a dependency
        chosen for the caller rather than by them.
    tool : bool
        Whether this packs as a .NET tool, which carries its binaries and
        their symbols under ``tools/`` rather than ``lib/``.
    dependency_versions : tuple[tuple[str, str, str], ...]
        Exact framework, package, and minimum versions the public dependency
        contract intentionally fixes.
    """

    dependencies: frozenset[str]
    tool: bool = False
    dependency_versions: tuple[tuple[str, str, str], ...] = ()


#: Logging abstractions are interfaces with no implementation attached, so a
#: caller who wants no logging still pays nothing for it. Every other entry
#: names exactly what that package exists to add.
CONTRACTS = {
    "LibTmux": Contract(
        frozenset({"Microsoft.Extensions.Logging.Abstractions"}),
        dependency_versions=(
            ("net8.0", "Microsoft.Extensions.Logging.Abstractions", "8.0.0"),
        ),
    ),
    "LibTmux.Query.Json": Contract(frozenset({"LibTmux"})),
    "LibTmux.Testing": Contract(frozenset({"LibTmux"})),
    "LibTmux.Workspace": Contract(frozenset({"LibTmux", "YamlDotNet"})),
    "LibTmux.Mcp": Contract(frozenset(), tool=True),
}


def package_id(package: pathlib.Path) -> str:
    """Return the package ID a built file name carries.

    Parameters
    ----------
    package : pathlib.Path
        Path to a ``.nupkg``.

    Returns
    -------
    str
        The ID, which is everything before the version.

    Examples
    --------
    >>> package_id(pathlib.Path("LibTmux.Query.Json.1.2.3.nupkg"))
    'LibTmux.Query.Json'
    >>> package_id(pathlib.Path("LibTmux.1.0.0.nupkg"))
    'LibTmux'
    >>> package_id(pathlib.Path("LibTmux.0.0.1-alpha.1.nupkg"))
    'LibTmux'
    >>> package_id(pathlib.Path("LibTmux.Mcp.0.0.1-alpha.nupkg"))
    'LibTmux.Mcp'
    """
    # A prerelease label is dotted too, so trimming trailing numbers off the
    # name leaves "-alpha" behind and the package stops being recognised. The
    # version is what it is: three numbers and whatever a label adds.
    return VERSION_SUFFIX.sub("", package.name.removesuffix(".nupkg"))


def assets(names: set[str], identifier: str, contract: Contract) -> list[str]:
    """Return one message per asset a consumer would reach for and not find.

    Parameters
    ----------
    names : set[str]
        Entry names in the archive.
    identifier : str
        The package ID.
    contract : Contract
        What this package is expected to be.

    Returns
    -------
    list[str]
        Violations.

    Examples
    --------
    >>> assets(set(), "LibTmux", CONTRACTS["LibTmux"])[0]
    'LibTmux carries no assembly for net8.0'
    """
    violations: list[str] = []

    # An icon element naming a file the package does not carry renders as a
    # broken image rather than as no image.
    if "icon.png" not in names:
        violations.append(f"{identifier} carries no icon")

    for framework in TARGET_FRAMEWORKS:
        directory = (
            f"tools/{framework}/any" if contract.tool else f"lib/{framework}"
        )
        if f"{directory}/{identifier}.dll" not in names:
            violations.append(f"{identifier} carries no assembly for {framework}")

        # A caller's editor shows what the documentation file says, and a
        # package without one shows nothing.
        if f"{directory}/{identifier}.xml" not in names:
            violations.append(f"{identifier} carries no documentation for {framework}")

    return violations


def inspect(package: pathlib.Path) -> list[str]:
    """Return one message per way the package falls short.

    Parameters
    ----------
    package : pathlib.Path
        Path to a built ``.nupkg``.

    Returns
    -------
    list[str]
        Violations, empty when the package is fit to publish.
    """
    identifier = package_id(package)
    contract = CONTRACTS.get(identifier)
    if contract is None:
        message = (
            f"{identifier} is not a package this repository declares. Add what it "
            "is allowed to depend on to inspect_packages.py, or stop packing it."
        )
        return [message]

    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        violations = assets(names, identifier, contract)
        specifications = [name for name in names if name.endswith(".nuspec")]
        if len(specifications) != 1:
            violations.append(f"{identifier} does not carry exactly one specification")
            return violations

        with archive.open(specifications[0]) as stream:
            root = ElementTree.parse(stream).getroot()

    namespace = root.tag[: root.tag.index("}") + 1]
    metadata = root.find(f"{namespace}metadata")
    if metadata is None:
        violations.append(f"{identifier} carries no metadata")
        return violations

    violations.extend(
        f"{identifier} declares dependency {dependency.attrib['id']}"
        for dependency in root.iter(f"{namespace}dependency")
        if dependency.attrib.get("id") not in contract.dependencies
    )
    groups = {
        group.attrib.get("targetFramework"): group
        for group in root.iter(f"{namespace}group")
    }
    for framework, dependency_id, expected in contract.dependency_versions:
        group = groups.get(framework)
        matching = (
            []
            if group is None
            else [
                dependency
                for dependency in group.findall(f"{namespace}dependency")
                if dependency.attrib.get("id") == dependency_id
            ]
        )
        if len(matching) != 1 or matching[0].attrib.get("version") != expected:
            actual = "missing" if len(matching) != 1 else matching[0].attrib.get("version")
            violations.append(
                f"{identifier} requires {dependency_id} {actual} for {framework}; "
                f"expected {expected}"
            )

    # A package page that says nothing about what the package is, who wrote it,
    # or where it came from is one a reader has to leave to evaluate.
    for field, expected in (
        ("id", identifier),
        ("license", "MIT"),
        ("readme", "README.md"),
        ("icon", "icon.png"),
        ("projectUrl", "https://github.com/libtmux/libtmux-dotnet"),
    ):
        element = metadata.find(f"{namespace}{field}")
        if element is None or element.text != expected:
            violations.append(f"{identifier} {field} is not {expected}")

    for field in ("description", "authors"):
        element = metadata.find(f"{namespace}{field}")
        if element is None or not element.text or element.text == identifier:
            violations.append(f"{identifier} carries no {field}")

    # A debugger resolves sources by asking the named repository for one exact
    # revision. Naming another project's repository sends it somewhere that has
    # never heard of the commit.
    repository = root.find(f".//{namespace}repository")
    if repository is None or repository.attrib.get("url") != (
        "https://github.com/libtmux/libtmux-dotnet"
    ):
        violations.append(f"{identifier} does not name this repository")

    symbols = package.with_suffix(".snupkg")
    if contract.tool:
        # The tool carries its own binaries, and their symbols with them.
        if symbols.is_file():
            violations.append(f"{identifier} produced symbols a tool does not need")
    elif not symbols.is_file():
        violations.append(f"{identifier} produced no symbols package beside it")

    return violations


def main(argv: list[str] | None = None) -> int:
    """Report whether the built packages are fit to publish."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--artifacts",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parents[2] / "artifacts" / "packages",
        help="the directory dotnet pack wrote to",
    )
    parser.add_argument(
        "--repository",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parents[2],
        help="the repository the package was built from",
    )
    arguments = parser.parse_args(argv)

    found = sorted(arguments.artifacts.glob("*.nupkg"))
    if not found:
        print(f"no package was found in {arguments.artifacts}")
        return 1

    # Packing fewer packages than this repository declares is as much a failure
    # as packing a broken one: nobody notices a package that stopped shipping.
    packed = {package_id(package) for package in found}
    violations = [
        f"{identifier} was declared but not packed"
        for identifier in sorted(set(CONTRACTS) - packed)
    ]
    violations.extend(message for package in found for message in inspect(package))
    for violation in violations:
        print(violation)

    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
