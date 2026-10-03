"""Assemble isolated same-commit producer transactions atomically."""

# Precise producer errors identify the transaction invariant that failed.
# ruff: noqa: EM101, EM102, TRY003, TRY301

from __future__ import annotations

import argparse
import contextlib
import ctypes
import ctypes.util
import hashlib
import json
import os
import pathlib
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tempfile
import typing as t

COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")

PRODUCER_ALLOWLISTS: dict[str, tuple[str, ...]] = {
    "matrix": (
        "environment.json",
        "results.ndjson",
        "redaction-proof.json",
        "protocol-transcripts/*.txt",
    ),
    "aot": (
        "aot-results.ndjson",
        "allocations.ndjson",
        "api-examples.md",
        "redaction-proof.json",
        "libtmux-query-v2.schema.json",
        "goldens/*.json",
    ),
    "model-aot": (
        "aot-results.ndjson",
        "allocations.ndjson",
        "api-examples.md",
        "model-aot-redaction-proof.json",
    ),
}
AOT_REQUIRED_FILES = {
    "aot-results.ndjson",
    "allocations.ndjson",
    "api-examples.md",
    "redaction-proof.json",
    "libtmux-query-v2.schema.json",
}
AOT_REQUIRED_GOLDENS = {
    "goldens/attached-nvim.json",
    "goldens/regex-invariant.json",
    "goldens/typed-id.json",
    "goldens/turkish-ignore-case.json",
}
AOT_GOLDEN_KEYS = {"schema", "version", "target", "predicate"}
MODEL_AOT_REQUIRED_FILES = {
    "aot-results.ndjson",
    "allocations.ndjson",
    "api-examples.md",
    "model-aot-redaction-proof.json",
}
MODEL_AOT_CONTENDERS = ("Mutable", "Services", "Hybrid")
MODEL_AOT_FRAMEWORKS = ("net10.0", "net8.0")
MODEL_AOT_LANES = {
    (contender, framework)
    for contender in MODEL_AOT_CONTENDERS
    for framework in MODEL_AOT_FRAMEWORKS
}
MODEL_AOT_RESULT_KEYS = {
    "contender",
    "evaluatedCommit",
    "framework",
    "status",
}
MODEL_AOT_ALLOCATION_KEYS = {
    "allocatedBytes",
    "contender",
    "evaluatedCommit",
    "framework",
    "scenario",
}
REDACTION_CATEGORIES = (
    "absolute-paths",
    "emails",
    "environment-values",
    "executable-paths",
    "hostnames",
    "socket-names",
    "temporary-directories",
    "terminal-device-names",
    "tokens",
    "usernames",
)
PRODUCER_KEYS = {
    "schemaVersion",
    "producer",
    "evaluatedCommit",
    "sourceTreeFingerprint",
    "files",
}
FINGERPRINT_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class BundleAssemblyError(ValueError):
    """Producer directories cannot form one trustworthy bundle."""


def _excluded_relative_roots(
    repository: pathlib.Path, excluded_roots: t.Iterable[pathlib.Path]
) -> tuple[pathlib.PurePosixPath, ...]:
    roots: list[pathlib.PurePosixPath] = []
    for root in excluded_roots:
        try:
            relative = root.absolute().relative_to(repository)
        except ValueError:
            continue
        roots.append(pathlib.PurePosixPath(relative.as_posix()))
    return tuple(roots)


def _under_roots(
    path: pathlib.PurePosixPath, roots: t.Iterable[pathlib.PurePosixPath]
) -> bool:
    return any(path == root or root in path.parents for root in roots)


def source_tree_fingerprint(
    repository: pathlib.Path,
    excluded_roots: t.Iterable[pathlib.Path] = (),
) -> str:
    """Hash HEAD, index, tracked bytes, and untracked source deterministically.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree to fingerprint.
    excluded_roots : Iterable[pathlib.Path]
        Declared evidence locations omitted from source identity.

    Returns
    -------
    str
        SHA-256 source-tree fingerprint.
    """
    repository = repository.resolve()
    excluded = _excluded_relative_roots(repository, excluded_roots)
    digest = hashlib.sha256()
    try:
        head = subprocess.run(
            ["git", "-C", str(repository), "rev-parse", "HEAD"],
            check=True,
            capture_output=True,
        ).stdout.strip()
        entries = subprocess.run(
            ["git", "-C", str(repository), "ls-files", "--stage", "-z"],
            check=True,
            capture_output=True,
        ).stdout
        untracked = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z",
            ],
            check=True,
            capture_output=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as exception:
        raise BundleAssemblyError(
            "repository state cannot be fingerprinted"
        ) from exception
    digest.update(b"HEAD\0" + head + b"\0INDEX\0")
    for raw_entry in entries.split(b"\0"):
        if not raw_entry:
            continue
        metadata, separator, raw_path = raw_entry.partition(b"\t")
        if not separator:
            raise BundleAssemblyError("git index entry is malformed")
        relative = raw_path.decode("utf-8", errors="surrogateescape")
        relative_path = pathlib.PurePosixPath(relative)
        if _under_roots(relative_path, excluded):
            continue
        path = repository / relative
        digest.update(b"ENTRY\0" + metadata + b"\t" + raw_path + b"\0")
        digest.update(b"PATH\0" + raw_path + b"\0")
        try:
            digest.update(path.read_bytes())
        except FileNotFoundError:
            digest.update(b"<deleted>")
        except OSError as exception:
            raise BundleAssemblyError(
                "tracked source cannot be fingerprinted"
            ) from exception
        digest.update(b"\0")
    for raw_path in sorted(raw for raw in untracked.split(b"\0") if raw):
        untracked_relative = pathlib.PurePosixPath(
            raw_path.decode("utf-8", errors="surrogateescape")
        )
        if _under_roots(untracked_relative, excluded):
            continue
        path = repository / untracked_relative
        if path.is_symlink() or not path.is_file():
            raise BundleAssemblyError("untracked source cannot be fingerprinted")
        digest.update(b"UNTRACKED\0" + raw_path + b"\0")
        try:
            digest.update(path.read_bytes())
        except OSError as exception:
            raise BundleAssemblyError(
                "untracked source cannot be fingerprinted"
            ) from exception
        digest.update(b"\0")
    return digest.hexdigest()


def source_state(
    repository: pathlib.Path,
    excluded_roots: t.Iterable[pathlib.Path] = (),
) -> str:
    """Classify tracked, index, and nonignored untracked source state.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree to inspect.
    excluded_roots : Iterable[pathlib.Path]
        Declared evidence locations omitted from source state.

    Returns
    -------
    str
        ``clean`` or ``uncommitted``.
    """
    repository = repository.resolve()
    excluded = _excluded_relative_roots(repository, excluded_roots)
    try:
        tracked = subprocess.run(
            ["git", "-C", str(repository), "diff", "--name-only", "-z", "HEAD", "--"],
            check=True,
            capture_output=True,
        ).stdout
        untracked = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z",
            ],
            check=True,
            capture_output=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as exception:
        raise BundleAssemblyError("repository state cannot be inspected") from exception
    paths = (
        pathlib.PurePosixPath(raw.decode("utf-8", errors="surrogateescape"))
        for raw in (tracked + untracked).split(b"\0")
        if raw
    )
    return (
        "uncommitted"
        if any(not _under_roots(path, excluded) for path in paths)
        else "clean"
    )


def _matches_allowlist(producer: str, relative: str) -> bool:
    path = pathlib.PurePosixPath(relative)
    return any(path.match(pattern) for pattern in PRODUCER_ALLOWLISTS[producer])


def _load_json_object(path: pathlib.Path) -> dict[str, t.Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise BundleAssemblyError(
            f"producer JSON is invalid: {path.name}"
        ) from exception
    if not isinstance(value, dict):
        raise BundleAssemblyError(f"producer JSON is not an object: {path.name}")
    return t.cast("dict[str, t.Any]", value)


def _load_ndjson_objects(path: pathlib.Path) -> list[dict[str, t.Any]]:
    try:
        values = [
            json.loads(line)
            for line in path.read_text(encoding="utf-8").splitlines()
            if line
        ]
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise BundleAssemblyError(
            f"producer NDJSON is invalid: {path.name}"
        ) from exception
    if not values or not all(isinstance(value, dict) for value in values):
        raise BundleAssemblyError(f"producer NDJSON schema is invalid: {path.name}")
    return t.cast("list[dict[str, t.Any]]", values)


def _validate_aot(files: dict[str, pathlib.Path], commit: str) -> None:
    if set(files) != AOT_REQUIRED_FILES | AOT_REQUIRED_GOLDENS:
        raise BundleAssemblyError("partial AOT producer")
    for name in ("aot-results.ndjson", "allocations.ndjson"):
        rows = _load_ndjson_objects(files[name])
        if any(row.get("evaluatedCommit") != commit for row in rows):
            raise BundleAssemblyError("AOT internal commit differs from producer")
    markdown = files["api-examples.md"].read_text(encoding="utf-8")
    commit_lines = [
        line for line in markdown.splitlines() if line.startswith("evaluatedCommit:")
    ]
    if commit_lines != [f"evaluatedCommit: {commit}"]:
        raise BundleAssemblyError("AOT internal commit differs from producer")
    proof = _load_json_object(files["redaction-proof.json"])
    if set(proof) != {"passed", "rejected"} or proof.get("passed") is not True:
        raise BundleAssemblyError("AOT redaction proof schema is invalid")
    schema = _load_json_object(files["libtmux-query-v2.schema.json"])
    properties = schema.get("properties")
    version = properties.get("version") if isinstance(properties, dict) else None
    if (
        "evaluatedCommit" in schema
        or not isinstance(schema.get("$schema"), str)
        or not isinstance(version, dict)
        or version.get("const") != 2
    ):
        raise BundleAssemblyError("AOT query schema is invalid")
    for relative in AOT_REQUIRED_GOLDENS:
        golden = _load_json_object(files[relative])
        if (
            set(golden) != AOT_GOLDEN_KEYS
            or golden.get("schema") != "libtmux-query"
            or golden.get("version") != 2
            or golden.get("target") not in {"session", "window", "pane", "client"}
            or not isinstance(golden.get("predicate"), dict)
        ):
            raise BundleAssemblyError("AOT golden schema is invalid")


def _model_aot_lanes(
    rows: list[dict[str, t.Any]],
) -> set[tuple[str, str]]:
    lanes = {
        (row["contender"], row["framework"])
        for row in rows
        if isinstance(row["contender"], str) and isinstance(row["framework"], str)
    }
    if len(rows) != len(MODEL_AOT_LANES) or lanes != MODEL_AOT_LANES:
        raise BundleAssemblyError("model AOT lanes are incomplete or duplicated")
    return lanes


def _validate_model_aot(files: dict[str, pathlib.Path], commit: str) -> None:
    if set(files) != MODEL_AOT_REQUIRED_FILES:
        raise BundleAssemblyError("partial model AOT producer")

    results = _load_ndjson_objects(files["aot-results.ndjson"])
    if any(set(row) != MODEL_AOT_RESULT_KEYS for row in results):
        raise BundleAssemblyError("model AOT result schema is invalid")
    if any(row["evaluatedCommit"] != commit for row in results):
        raise BundleAssemblyError("model AOT internal commit differs from producer")
    if any(row["status"] != "passed" for row in results):
        raise BundleAssemblyError("model AOT result did not pass")
    _model_aot_lanes(results)

    allocations = _load_ndjson_objects(files["allocations.ndjson"])
    if any(set(row) != MODEL_AOT_ALLOCATION_KEYS for row in allocations):
        raise BundleAssemblyError("model AOT allocation schema is invalid")
    if any(row["evaluatedCommit"] != commit for row in allocations):
        raise BundleAssemblyError("model AOT internal commit differs from producer")
    if any(
        not isinstance(row["allocatedBytes"], int)
        or isinstance(row["allocatedBytes"], bool)
        or row["allocatedBytes"] < 0
        or row["scenario"] != "materialization"
        for row in allocations
    ):
        raise BundleAssemblyError("model AOT allocation schema is invalid")
    _model_aot_lanes(allocations)

    try:
        lines = files["api-examples.md"].read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError) as exception:
        raise BundleAssemblyError("model AOT API examples are invalid") from exception
    commit_lines = [line for line in lines if line.startswith("evaluatedCommit:")]
    if commit_lines != [f"evaluatedCommit: {commit}"]:
        raise BundleAssemblyError("model AOT internal commit differs from producer")

    proof = _load_json_object(files["model-aot-redaction-proof.json"])
    if proof != {"passed": True, "rejected": list(REDACTION_CATEGORIES)}:
        raise BundleAssemblyError("model AOT redaction proof schema is invalid")


def _validate_matrix(root: pathlib.Path, commit: str, fingerprint: str) -> None:
    environment = _load_json_object(root / "environment.json")
    if environment.get("sourceState") != "clean":
        raise BundleAssemblyError("matrix producer requires a clean source state")
    if environment.get("evaluatedCommit") != commit:
        raise BundleAssemblyError("matrix internal commit differs from producer")
    if environment.get("sourceTreeFingerprint") != fingerprint:
        raise BundleAssemblyError("matrix fingerprint differs from producer")
    validation = subprocess.run(
        [
            sys.executable,
            str(pathlib.Path(__file__).with_name("validate.py")),
            "--phase",
            "matrix",
            str(root),
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    if validation.returncode != 0:
        raise BundleAssemblyError("matrix producer validation failed")


def inspect_producer(
    expected_name: str,
    root: pathlib.Path,
) -> tuple[str, str, dict[str, pathlib.Path]]:
    """Validate one producer manifest and return its declared files.

    Parameters
    ----------
    expected_name : str
        Producer name supplied by the caller.
    root : pathlib.Path
        Producer directory.

    Returns
    -------
    tuple[str, str, dict[str, pathlib.Path]]
        Evaluated commit, source fingerprint, and declared files.
    """
    if expected_name not in PRODUCER_ALLOWLISTS:
        raise BundleAssemblyError(f"unknown producer: {expected_name}")
    if root.is_symlink():
        raise BundleAssemblyError("producer root symlink is not allowed")
    root = root.resolve()
    manifest_path = root / "producer.json"
    if not manifest_path.is_file():
        raise BundleAssemblyError(f"producer manifest is missing: {expected_name}")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise BundleAssemblyError("producer manifest is invalid") from exception
    if (
        not isinstance(manifest, dict)
        or set(manifest) != PRODUCER_KEYS
        or manifest.get("schemaVersion") != 1
        or manifest.get("producer") != expected_name
    ):
        raise BundleAssemblyError("producer manifest name does not match")
    commit = manifest.get("evaluatedCommit")
    if not isinstance(commit, str) or not COMMIT_PATTERN.fullmatch(commit):
        raise BundleAssemblyError("producer evaluated commit is invalid")
    fingerprint = manifest.get("sourceTreeFingerprint")
    if not isinstance(fingerprint, str) or not FINGERPRINT_PATTERN.fullmatch(
        fingerprint
    ):
        raise BundleAssemblyError("producer source fingerprint is invalid")
    declared = manifest.get("files")
    if not isinstance(declared, list) or not all(
        isinstance(item, str) for item in declared
    ):
        raise BundleAssemblyError("producer files must be a string list")
    declared_strings = t.cast("list[str]", declared)
    if len(declared_strings) != len(set(declared_strings)):
        raise BundleAssemblyError("producer manifest contains duplicate files")
    actual: list[str] = []
    for path in root.rglob("*"):
        if path.is_symlink():
            raise BundleAssemblyError("producer symlinks are not allowed")
        mode = path.stat(follow_symlinks=False).st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            raise BundleAssemblyError("producer entry type is not supported")
        if path != manifest_path:
            actual.append(path.relative_to(root).as_posix())
    actual.sort()
    if sorted(declared_strings) != actual:
        raise BundleAssemblyError("producer manifest contains unknown or stale files")
    files: dict[str, pathlib.Path] = {}
    for relative in declared_strings:
        pure = pathlib.PurePosixPath(relative)
        path = root / relative
        if pure.is_absolute() or ".." in pure.parts or path.is_symlink():
            raise BundleAssemblyError("producer file path is unsafe")
        if not _matches_allowlist(expected_name, relative):
            raise BundleAssemblyError(f"producer file is not allowed: {relative}")
        files[relative] = path
    if expected_name == "matrix":
        _validate_matrix(root, commit, fingerprint)
    elif expected_name == "aot":
        _validate_aot(files, commit)
    elif expected_name == "model-aot":
        _validate_model_aot(files, commit)
    return commit, fingerprint, files


def _bundle_relative_path(producer: str, relative: str) -> str:
    if producer == "matrix":
        return relative
    if producer == "aot":
        return f"aot/{relative}"
    if producer == "model-aot":
        return relative
    raise BundleAssemblyError(f"unknown producer: {producer}")


def _reject_source_changes(
    repository: pathlib.Path,
    evaluated_commit: str,
    fingerprint: str,
    allowed_roots: t.Iterable[pathlib.Path],
) -> None:
    """Reject repository changes outside declared transaction paths.

    Parameters
    ----------
    repository : pathlib.Path
        Git worktree to inspect.
    evaluated_commit : str
        Commit claimed by every producer.
    fingerprint : str
        Source fingerprint claimed by every producer.
    allowed_roots : Iterable[pathlib.Path]
        Evidence and decision paths allowed to change.
    """
    repository = repository.resolve()
    try:
        head = subprocess.run(
            ["git", "-C", str(repository), "rev-parse", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        tracked = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "diff",
                "--name-only",
                "--no-renames",
                "-z",
                "HEAD",
                "--",
            ],
            check=True,
            capture_output=True,
        ).stdout
        untracked = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z",
            ],
            check=True,
            capture_output=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as exception:
        raise BundleAssemblyError("repository state cannot be inspected") from exception
    if head != evaluated_commit:
        raise BundleAssemblyError("repository HEAD differs from evaluated commit")
    allowed: list[pathlib.PurePosixPath] = []
    for root in allowed_roots:
        try:
            relative = root.resolve().relative_to(repository)
        except ValueError:
            continue
        allowed.append(pathlib.PurePosixPath(relative.as_posix()))
    changed = {
        pathlib.PurePosixPath(raw.decode("utf-8", errors="surrogateescape"))
        for raw in (tracked + untracked).split(b"\0")
        if raw
    }
    outside = sorted(
        str(path)
        for path in changed
        if not any(path == root or root in path.parents for root in allowed)
    )
    if outside:
        raise BundleAssemblyError("source changes exist outside declared paths")
    if source_tree_fingerprint(repository, allowed_roots) != fingerprint:
        raise BundleAssemblyError("repository source fingerprint differs from evidence")


def _assert_plain_directory(path: pathlib.Path) -> None:
    if path.is_symlink() or not path.is_dir():
        raise BundleAssemblyError("publication source must be a real directory")
    for entry in path.rglob("*"):
        if entry.is_symlink():
            raise BundleAssemblyError("publication trees cannot contain symlinks")
        mode = entry.stat(follow_symlinks=False).st_mode
        if not stat.S_ISDIR(mode) and not stat.S_ISREG(mode):
            raise BundleAssemblyError("publication tree entry type is unsupported")


def _exchange_directories(source: pathlib.Path, destination: pathlib.Path) -> None:
    """Atomically exchange two directories on a supported platform.

    Parameters
    ----------
    source : pathlib.Path
        New candidate directory.
    destination : pathlib.Path
        Existing published directory.
    """
    source_bytes = os.fsencode(source)
    destination_bytes = os.fsencode(destination)
    library_name = ctypes.util.find_library("c")
    if library_name is None:
        raise BundleAssemblyError("atomic directory exchange is unavailable")
    library = ctypes.CDLL(library_name, use_errno=True)
    result: int
    if sys.platform.startswith("linux"):
        try:
            renameat2 = library.renameat2
        except AttributeError as exception:
            raise BundleAssemblyError(
                "atomic directory exchange is unavailable"
            ) from exception
        renameat2.argtypes = [
            ctypes.c_int,
            ctypes.c_char_p,
            ctypes.c_int,
            ctypes.c_char_p,
            ctypes.c_uint,
        ]
        renameat2.restype = ctypes.c_int
        result = renameat2(-100, source_bytes, -100, destination_bytes, 2)
    elif sys.platform == "darwin":
        try:
            renamex_np = library.renamex_np
        except AttributeError as exception:
            raise BundleAssemblyError(
                "atomic directory exchange is unavailable"
            ) from exception
        renamex_np.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
        renamex_np.restype = ctypes.c_int
        result = renamex_np(source_bytes, destination_bytes, 2)
    else:
        raise BundleAssemblyError(
            "atomic directory exchange is unsupported on this platform"
        )
    if result != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error), str(destination))


def _remove_version(path: pathlib.Path) -> None:
    """Remove one exchanged private tree without broad recursive deletion.

    Parameters
    ----------
    path : pathlib.Path
        Private directory that no longer backs the public destination.
    """
    if path.is_symlink() or not path.is_dir() or path == pathlib.Path(path.anchor):
        raise BundleAssemblyError("cleanup target is not a private directory")
    for entry in sorted(
        path.rglob("*"), key=lambda item: len(item.parts), reverse=True
    ):
        if entry.is_symlink():
            entry.unlink()
        elif entry.is_dir():
            entry.rmdir()
        else:
            entry.unlink()
    path.rmdir()


def _ownership_marker(candidate: pathlib.Path) -> pathlib.Path:
    return candidate.with_name(f"{candidate.name}.owner.json")


def _publication_parent(destination: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    destination = destination.absolute()
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.parent.is_symlink() or not destination.parent.is_dir():
        raise BundleAssemblyError("publication parent must be a real directory")
    parent = destination.parent.resolve()
    return parent, parent / destination.name


def create_owned_candidate(destination: pathlib.Path) -> tuple[pathlib.Path, str]:
    """Create a generated sibling candidate with explicit caller ownership.

    Parameters
    ----------
    destination : pathlib.Path
        Public directory path.

    Returns
    -------
    tuple[pathlib.Path, str]
        Candidate directory and ownership nonce.
    """
    parent, destination = _publication_parent(destination)
    candidate = pathlib.Path(
        tempfile.mkdtemp(prefix=f".{destination.name}.candidate-", dir=parent)
    )
    nonce = secrets.token_hex(16)
    marker = _ownership_marker(candidate)
    marker.write_text(
        json.dumps(
            {
                "candidate": candidate.name,
                "destination": destination.name,
                "nonce": nonce,
                "schemaVersion": 1,
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return candidate, nonce


def _verify_owned_candidate(
    candidate: pathlib.Path, destination: pathlib.Path, nonce: str
) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
    parent, destination = _publication_parent(destination)
    candidate = candidate.absolute()
    prefix = f".{destination.name}.candidate-"
    marker = _ownership_marker(candidate)
    if (
        candidate.parent.resolve() != parent
        or not candidate.name.startswith(prefix)
        or candidate.name == prefix
        or candidate.is_symlink()
        or not candidate.is_dir()
        or marker.is_symlink()
        or not marker.is_file()
        or not re.fullmatch(r"[0-9a-f]{32}", nonce)
    ):
        raise BundleAssemblyError("candidate ownership could not be verified")
    ownership = _load_json_object(marker)
    if ownership != {
        "candidate": candidate.name,
        "destination": destination.name,
        "nonce": nonce,
        "schemaVersion": 1,
    }:
        raise BundleAssemblyError("candidate ownership could not be verified")
    _assert_plain_directory(candidate)
    return candidate, destination, marker


def discard_owned_candidate(
    candidate: pathlib.Path, destination: pathlib.Path, nonce: str
) -> None:
    """Discard only a verified caller-owned generated candidate.

    Parameters
    ----------
    candidate : pathlib.Path
        Generated candidate directory.
    destination : pathlib.Path
        Exact public sibling destination.
    nonce : str
        Caller-held ownership nonce.
    """
    candidate, _destination, marker = _verify_owned_candidate(
        candidate, destination, nonce
    )
    _remove_version(candidate)
    marker.unlink()


def publish_owned_candidate(
    candidate: pathlib.Path, destination: pathlib.Path, nonce: str
) -> None:
    """Publish a verified real directory using one atomic operation.

    Parameters
    ----------
    candidate : pathlib.Path
        Fully validated caller-owned candidate directory.
    destination : pathlib.Path
        Public directory path.
    nonce : str
        Caller-held ownership nonce.
    """
    candidate, destination, marker = _verify_owned_candidate(
        candidate, destination, nonce
    )
    if candidate.stat().st_dev != destination.parent.stat().st_dev:
        raise BundleAssemblyError("publication requires one filesystem")
    if destination.is_symlink():
        raise BundleAssemblyError("publication destination cannot be a symlink")
    if not destination.exists():
        candidate.replace(destination)
        with contextlib.suppress(OSError):
            marker.unlink()
        return
    _assert_plain_directory(destination)
    _exchange_directories(candidate, destination)
    with contextlib.suppress(OSError, BundleAssemblyError):
        _remove_version(candidate)
    if not candidate.exists():
        with contextlib.suppress(OSError):
            marker.unlink()


def assemble(
    producers: t.Mapping[str, pathlib.Path],
    output: pathlib.Path,
    *,
    repository: pathlib.Path | None = None,
) -> None:
    """Atomically assemble same-commit producer directories.

    Parameters
    ----------
    producers : Mapping[str, pathlib.Path]
        Named producer directories.
    output : pathlib.Path
        Durable bundle destination.
    repository : pathlib.Path | None
        Git worktree whose source state must match the evaluated commit.
    """
    if "matrix" not in producers:
        raise BundleAssemblyError("the matrix producer is required")
    inspected = {name: inspect_producer(name, root) for name, root in producers.items()}
    commits = {commit for commit, _fingerprint, _files in inspected.values()}
    if len(commits) != 1:
        raise BundleAssemblyError("mixed evaluated commits are not allowed")
    evaluated_commit = commits.pop()
    fingerprints = {fingerprint for _commit, fingerprint, _files in inspected.values()}
    if len(fingerprints) != 1:
        raise BundleAssemblyError("mixed source fingerprints are not allowed")
    fingerprint = fingerprints.pop()
    if repository is not None:
        _reject_source_changes(
            repository,
            evaluated_commit,
            fingerprint,
            [*producers.values(), output],
        )
    claimed: dict[str, pathlib.Path] = {}
    for name, (_commit, _fingerprint, files) in inspected.items():
        for relative, source in files.items():
            destination_relative = _bundle_relative_path(name, relative)
            if destination_relative in claimed:
                raise BundleAssemblyError(
                    f"producer file collision: {destination_relative}"
                )
            claimed[destination_relative] = source

    output = output.absolute()
    output.parent.mkdir(parents=True, exist_ok=True)
    candidate, nonce = create_owned_candidate(output)
    try:
        for relative, source in claimed.items():
            destination = candidate / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
        validation = subprocess.run(
            [
                sys.executable,
                str(pathlib.Path(__file__).with_name("validate.py")),
                "--phase",
                "matrix",
                str(candidate),
            ],
            check=False,
            capture_output=True,
            text=True,
        )
        if validation.returncode != 0:
            raise BundleAssemblyError("candidate evidence validation failed")
        publish_owned_candidate(candidate, output, nonce)
    except BaseException:
        if candidate.exists():
            discard_owned_candidate(candidate, output, nonce)
        raise


def parse_producer(value: str) -> tuple[str, pathlib.Path]:
    """Parse one ``name=path`` producer argument.

    Parameters
    ----------
    value : str
        Producer argument.

    Returns
    -------
    tuple[str, pathlib.Path]
        Producer name and directory.

    Examples
    --------
    >>> parse_producer("matrix=stage/matrix")
    ('matrix', PosixPath('stage/matrix'))
    """
    name, separator, raw_path = value.partition("=")
    if not separator or not name or not raw_path:
        raise argparse.ArgumentTypeError("producer must use name=path")
    return name, pathlib.Path(raw_path)


def parse_args(argv: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse bundle assembly arguments.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    argparse.Namespace
        Parsed arguments.
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--producer", action="append", type=parse_producer)
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--repository", default=pathlib.Path.cwd(), type=pathlib.Path)
    parser.add_argument("--create-candidate", action="store_true")
    parser.add_argument("--discard-candidate", type=pathlib.Path)
    parser.add_argument("--ownership-nonce")
    parser.add_argument("--publish-candidate", type=pathlib.Path)
    parser.add_argument("--source-fingerprint", type=pathlib.Path)
    parser.add_argument("--source-state", type=pathlib.Path)
    parser.add_argument(
        "--exclude-root",
        action="append",
        default=[],
        type=pathlib.Path,
    )
    arguments = parser.parse_args(argv)
    modes = sum(
        value is not None
        for value in (
            arguments.producer,
            arguments.create_candidate or None,
            arguments.discard_candidate,
            arguments.publish_candidate,
            arguments.source_fingerprint,
            arguments.source_state,
        )
    )
    if modes != 1:
        parser.error(
            "select exactly one assembly, publication, cleanup, or source mode"
        )
    if (
        arguments.producer
        or arguments.create_candidate
        or arguments.discard_candidate
        or arguments.publish_candidate
    ) and arguments.output is None:
        parser.error("--output is required for assembly and candidate operations")
    if arguments.output is not None and not (
        arguments.producer
        or arguments.create_candidate
        or arguments.discard_candidate
        or arguments.publish_candidate
    ):
        parser.error("--output is only valid for assembly and candidate operations")
    if (arguments.discard_candidate or arguments.publish_candidate) and not (
        arguments.ownership_nonce
    ):
        parser.error("--ownership-nonce is required for publication and cleanup")
    if arguments.ownership_nonce and not (
        arguments.discard_candidate or arguments.publish_candidate
    ):
        parser.error("--ownership-nonce is only valid for publication and cleanup")
    if arguments.exclude_root and not (
        arguments.source_fingerprint or arguments.source_state
    ):
        parser.error("--exclude-root is only valid for source modes")
    return arguments


def main(argv: t.Sequence[str] | None = None) -> int:
    """Assemble one durable evidence bundle.

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
    if arguments.source_fingerprint is not None:
        print(
            source_tree_fingerprint(
                arguments.source_fingerprint,
                excluded_roots=arguments.exclude_root,
            )
        )
        return 0
    if arguments.source_state is not None:
        print(
            source_state(
                arguments.source_state,
                excluded_roots=arguments.exclude_root,
            )
        )
        return 0
    if arguments.create_candidate:
        candidate, nonce = create_owned_candidate(arguments.output)
        print(
            json.dumps(
                {"candidate": str(candidate), "ownershipNonce": nonce},
                sort_keys=True,
            )
        )
        return 0
    if arguments.discard_candidate is not None:
        discard_owned_candidate(
            arguments.discard_candidate,
            arguments.output,
            arguments.ownership_nonce,
        )
        return 0
    if arguments.publish_candidate is not None:
        publish_owned_candidate(
            arguments.publish_candidate,
            arguments.output,
            arguments.ownership_nonce,
        )
        return 0
    assert arguments.producer is not None
    producers = dict(arguments.producer)
    if len(producers) != len(arguments.producer):
        raise BundleAssemblyError("producer names must be unique")
    assemble(producers, arguments.output, repository=arguments.repository)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
