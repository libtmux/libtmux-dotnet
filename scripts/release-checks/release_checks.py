#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Checks the release workflow runs around the packages it publishes.

- `tag`: the workflow runs at a `v*` tag, and the tag names the version the
  library builds.
- `unpack`: each package unpacked, for the SBOM generator to read.
- `sbom`: the SBOM names every package.
- `fetch-published`: each package nuget.org serves is the one that was uploaded,
  and carries the repository signature.

```console
$ uv run scripts/release-checks/release_checks.py tag --ref-type tag --tag v0.1.2
```
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import typing as t
import urllib.error
import urllib.request
import zipfile

NUGET_FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer"
LIBRARY_PROJECT = "src/LibTmux/LibTmux.csproj"
SIGNATURE = ".signature.p7s"
DEADLINE_SECONDS = 1800.0
RETRY_SECONDS = 30.0


class ReleaseCheckError(Exception):
    """A release check failed; the message is the annotation to print."""


def packages_in(directory: pathlib.Path) -> list[pathlib.Path]:
    """Return the `.nupkg` files of a directory, in name order."""
    return sorted(directory.glob("*.nupkg"))


def nuspec_field(package: pathlib.Path, field: str) -> str:
    """Return the first `<field>` in a package's nuspec.

    The nuspec is read whole before it is searched.
    """
    with zipfile.ZipFile(package) as archive:
        name = next(entry for entry in archive.namelist() if entry.endswith(".nuspec"))
        text = archive.read(name).decode("utf-8")
    found = re.search(rf"<{field}>([^<]+)</{field}>", text)
    if found is None:
        message = f"{package.name} has no <{field}> in its nuspec"
        raise ReleaseCheckError(message)
    return found[1]


def library_version() -> str:
    """Return the version MSBuild reports for the library."""
    done = subprocess.run(
        ["dotnet", "msbuild", LIBRARY_PROJECT, "-getProperty:Version", "-verbosity:quiet"],
        capture_output=True,
        text=True,
        check=True,
    )
    return "".join(done.stdout.split())


def check_tag(
    ref_type: str,
    tag: str,
    commit: str,
    version: t.Callable[[], str] = library_version,
) -> str:
    """Return the line to print when the run is at the tag of the library's version.

    >>> check_tag("tag", "v1.2.3", "abc", lambda: "1.2.3")
    'validated v1.2.3 at abc'
    """
    if ref_type != "tag" or not tag.startswith("v"):
        message = f"release must run at a v* tag, not {ref_type} {tag}"
        raise ReleaseCheckError(message)
    built = version()
    if tag != f"v{built}":
        message = f"tag {tag} does not match version {built}"
        raise ReleaseCheckError(message)
    return f"validated {tag} at {commit}"


def unpack(packages: pathlib.Path, output: pathlib.Path) -> None:
    """Unpack each package into a directory of its own named for it."""
    for package in packages_in(packages):
        destination = output / package.stem
        destination.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(package) as archive:
            archive.extractall(destination)


def check_sbom(packages: pathlib.Path, sbom: pathlib.Path) -> None:
    """Fail naming every package the SBOM does not list as a library."""
    components = json.loads(sbom.read_text(encoding="utf-8")).get("components") or []
    libraries = {c.get("name") for c in components if c.get("type") == "library"}
    missing = [
        identifier
        for identifier in (nuspec_field(package, "id") for package in packages_in(packages))
        if identifier not in libraries
    ]
    if missing:
        raise ReleaseCheckError(
            "\n".join(f"::error::The SBOM does not name {identifier}." for identifier in missing)
        )


def fetch_published(
    packages: pathlib.Path,
    output: pathlib.Path,
    *,
    base_url: str = NUGET_FLAT_CONTAINER,
    deadline: float = DEADLINE_SECONDS,
    interval: float = RETRY_SECONDS,
) -> None:
    """Fetch each package as the registry serves it, and check its signature.

    The registry may take a while to serve what was just pushed, so each fetch
    retries until the deadline.
    """
    output.mkdir(parents=True, exist_ok=True)
    limit = time.monotonic() + deadline
    for package in packages_in(packages):
        identifier = nuspec_field(package, "id")
        version = nuspec_field(package, "version")
        name = f"{identifier.lower()}.{version.lower()}.nupkg"
        url = f"{base_url}/{identifier.lower()}/{version.lower()}/{name}"
        published = output / f"{identifier}.{version}.nupkg"
        while True:
            try:
                with urllib.request.urlopen(url, timeout=60) as response:
                    published.write_bytes(response.read())
                break
            except (urllib.error.URLError, TimeoutError):
                if time.monotonic() > limit:
                    minutes = f"{deadline / 60:g}"
                    message = (
                        f"::error::nuget.org did not serve {identifier} {version} "
                        f"within {minutes} minutes."
                    )
                    raise ReleaseCheckError(message) from None
                time.sleep(interval)
        with zipfile.ZipFile(published) as archive:
            signed = SIGNATURE in archive.namelist()
        if not signed:
            message = (
                f"::error::nuget.org served {identifier} {version} without a repository signature."
            )
            raise ReleaseCheckError(message)


def main(argv: t.Sequence[str] | None = None) -> int:
    """Run the requested check and answer the process status."""
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    tag = commands.add_parser("tag", help="check the run is at the tag of the library's version")
    tag.add_argument("--ref-type", required=True)
    tag.add_argument("--tag", required=True)
    unpack_parser = commands.add_parser("unpack", help="unpack each package into its own directory")
    sbom = commands.add_parser("sbom", help="check the SBOM names every package")
    fetch = commands.add_parser("fetch-published", help="fetch what the registry serves")
    for command in (unpack_parser, sbom, fetch):
        command.add_argument("--packages", type=pathlib.Path, required=True)
    unpack_parser.add_argument("--output", type=pathlib.Path, required=True)
    fetch.add_argument("--output", type=pathlib.Path, required=True)
    sbom.add_argument("--sbom", type=pathlib.Path, required=True)
    options = parser.parse_args(argv)
    try:
        if options.command == "tag":
            print(check_tag(options.ref_type, options.tag, os.environ.get("GITHUB_SHA", "")))
        elif options.command == "unpack":
            unpack(options.packages, options.output)
        elif options.command == "sbom":
            check_sbom(options.packages, options.sbom)
        else:
            fetch_published(options.packages, options.output)
    except ReleaseCheckError as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
