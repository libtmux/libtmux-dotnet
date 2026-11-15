"""Create deterministic hash manifests for durable evidence."""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import stat
import typing as t


class EvidenceTreeError(ValueError):
    """Evidence contains an unsafe or unsupported filesystem entry."""


def _resolved_root(root: pathlib.Path) -> pathlib.Path:
    """Reject a root symlink before returning the canonical directory.

    Parameters
    ----------
    root : pathlib.Path
        Caller-supplied evidence root.

    Returns
    -------
    pathlib.Path
        Canonical evidence root.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as directory:
    ...     root = pathlib.Path(directory)
    ...     _resolved_root(root) == root.resolve()
    True
    """
    if root.is_symlink():
        msg = "evidence root symlink is not allowed"
        raise EvidenceTreeError(msg)
    return root.resolve()


def evidence_files(root: pathlib.Path) -> list[pathlib.Path]:
    """Return the durable files covered by the hash manifest.

    Parameters
    ----------
    root : pathlib.Path
        Evidence directory.

    Returns
    -------
    list[pathlib.Path]
        Sorted files other than ``SHA256SUMS``.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as directory:
    ...     root = pathlib.Path(directory)
    ...     _ = (root / "a.txt").write_text("a", encoding="utf-8")
    ...     [path.name for path in evidence_files(root)]
    ['a.txt']
    """
    root = _resolved_root(root)
    files: list[pathlib.Path] = []
    for path in root.rglob("*"):
        if path.is_symlink():
            msg = f"evidence symlink is not allowed: {path.relative_to(root)}"
            raise EvidenceTreeError(msg)
        mode = path.stat(follow_symlinks=False).st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            msg = f"unsupported evidence entry: {path.relative_to(root)}"
            raise EvidenceTreeError(msg)
        if path == root / "SHA256SUMS":
            continue
        files.append(path)
    return sorted(files)


def hash_file(path: pathlib.Path) -> str:
    """Hash one file without decoding it.

    Parameters
    ----------
    path : pathlib.Path
        File to hash.

    Returns
    -------
    str
        Lowercase SHA-256 digest.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as directory:
    ...     path = pathlib.Path(directory) / "empty"
    ...     _ = path.write_bytes(b"")
    ...     hash_file(path)[:8]
    'e3b0c442'
    """
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_hashes(root: pathlib.Path) -> str:
    r"""Write a complete deterministic ``SHA256SUMS`` file.

    Parameters
    ----------
    root : pathlib.Path
        Evidence directory.

    Returns
    -------
    str
        Manifest content.

    Examples
    --------
    >>> import tempfile
    >>> with tempfile.TemporaryDirectory() as directory:
    ...     root = pathlib.Path(directory)
    ...     _ = (root / "a").write_bytes(b"a")
    ...     write_hashes(root).endswith("  a\n")
    True
    """
    root = _resolved_root(root)
    if not root.is_dir():
        msg = f"evidence directory does not exist: {root}"
        raise FileNotFoundError(msg)
    lines = [
        f"{hash_file(path)}  {path.relative_to(root).as_posix()}"
        for path in evidence_files(root)
    ]
    manifest = "\n".join(lines) + ("\n" if lines else "")
    destination = root / "SHA256SUMS"
    candidate = root / ".SHA256SUMS.tmp"
    candidate.write_text(manifest, encoding="utf-8", newline="\n")
    candidate.replace(destination)
    return manifest


def parse_args(argv: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse the evidence directory argument.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    argparse.Namespace
        Parsed command arguments.

    Examples
    --------
    >>> parse_args(["evidence"]).root
    PosixPath('evidence')
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path)
    return parser.parse_args(argv)


def main(argv: t.Sequence[str] | None = None) -> int:
    """Write the evidence hash manifest.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    int
        Process status.
    """
    arguments = parse_args(argv)
    write_hashes(arguments.root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
