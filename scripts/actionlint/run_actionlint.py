#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Run actionlint at the version `.tool-versions` pins.

The release archive is checked against a SHA-256 digest before anything is
unpacked, so a replaced asset fails the run instead of executing.

```console
$ uv run scripts/actionlint/run_actionlint.py --sha256 DIGEST
```
"""

from __future__ import annotations

import argparse
import hashlib
import io
import os
import pathlib
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
RELEASES = "https://github.com/rhysd/actionlint/releases/download"
TIMEOUT_SECONDS = 60


def pinned_version(tool_versions: str) -> str:
    """Return the actionlint version a `.tool-versions` file pins.

    >>> pinned_version("dotnet 10.0.303\\nactionlint 1.7.12\\n")
    '1.7.12'
    >>> pinned_version("dotnet 10.0.303\\n")
    Traceback (most recent call last):
    ValueError: .tool-versions pins no actionlint version
    """
    for line in tool_versions.splitlines():
        fields = line.split()
        if len(fields) >= 2 and fields[0] == "actionlint":
            return fields[1]
    raise ValueError(".tool-versions pins no actionlint version")


def archive_url(version: str) -> str:
    """Return where the Linux x86-64 release archive for a version lives.

    >>> archive_url("1.7.12").rsplit("/", 1)[1]
    'actionlint_1.7.12_linux_amd64.tar.gz'
    """
    return f"{RELEASES}/v{version}/actionlint_{version}_linux_amd64.tar.gz"


def verify_digest(archive: bytes, expected: str) -> None:
    """Raise unless the archive's SHA-256 digest is the expected one.

    >>> verify_digest(b"", hashlib.sha256(b"").hexdigest())
    """
    actual = hashlib.sha256(archive).hexdigest()
    if actual != expected.strip().lower():
        raise ValueError(
            f"actionlint archive digest is {actual}, expected {expected.strip().lower()}"
        )


def extract_binary(archive: bytes, directory: pathlib.Path) -> pathlib.Path:
    """Write the archive's `actionlint` member into a directory as an executable."""
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
        try:
            member = bundle.extractfile("actionlint")
        except KeyError:
            member = None
        if member is None:
            raise ValueError("actionlint archive holds no actionlint file")
        binary = directory / "actionlint"
        binary.write_bytes(member.read())
    binary.chmod(0o755)
    return binary


def fetch(url: str) -> bytes:
    """Download a URL, following redirects, and fail on an HTTP error."""
    with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:
        body: bytes = response.read()
    return body


def run(binary: pathlib.Path) -> int:
    """Run actionlint over the repository's workflows and return its exit status."""
    return subprocess.run([str(binary)], cwd=ROOT, check=False).returncode


def main(argv: list[str] | None = None) -> int:
    """Download, verify and run the pinned actionlint."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0] if __doc__ else None)
    parser.add_argument(
        "--sha256",
        default=os.environ.get("ACTIONLINT_SHA256"),
        help="digest of the release archive; defaults to $ACTIONLINT_SHA256",
    )
    parser.add_argument(
        "--tool-versions",
        type=pathlib.Path,
        default=ROOT / ".tool-versions",
        help="file that pins the actionlint version",
    )
    arguments = parser.parse_args(argv)
    if not arguments.sha256:
        parser.error("give the archive digest with --sha256 or $ACTIONLINT_SHA256")
    try:
        version = pinned_version(arguments.tool_versions.read_text(encoding="utf-8"))
        archive = fetch(archive_url(version))
        verify_digest(archive, arguments.sha256)
        with tempfile.TemporaryDirectory() as directory:
            binary = extract_binary(archive, pathlib.Path(directory))
            return run(binary)
    except (OSError, ValueError, tarfile.TarError) as error:
        print(f"run_actionlint: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
