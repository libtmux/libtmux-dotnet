"""Tests for deterministic evidence hashes."""

from __future__ import annotations

import importlib
import os
import pathlib
import sys
import typing as t

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent))
hash_tree = t.cast(t.Any, importlib.import_module("hash_tree"))
validate = t.cast(t.Any, importlib.import_module("validate"))


def test_byte_change_breaks_sha256sums(tmp_path: pathlib.Path) -> None:
    """Reject a durable tree whose hashed bytes changed."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    (bundle / "environment.json").write_text(
        '{"evaluatedCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}\n',
        encoding="utf-8",
    )
    hash_tree.write_hashes(bundle)
    (bundle / "environment.json").write_bytes(b"changed\n")

    with pytest.raises(validate.EvidenceValidationError, match="SHA256SUMS"):
        validate.verify_hashes(bundle)


def test_hash_manifest_covers_every_other_file(tmp_path: pathlib.Path) -> None:
    """Include nested evidence files in a deterministic manifest."""
    bundle = tmp_path / "bundle"
    transcript = bundle / "protocol-transcripts" / "control.txt"
    transcript.parent.mkdir(parents=True)
    transcript.write_text("sanitized transcript\n", encoding="utf-8")
    (bundle / "environment.json").write_text("{}\n", encoding="utf-8")

    manifest = hash_tree.write_hashes(bundle)

    assert [line.rsplit("  ", 1)[1] for line in manifest.splitlines()] == [
        "environment.json",
        "protocol-transcripts/control.txt",
    ]


def test_nested_sha256sums_is_included(tmp_path: pathlib.Path) -> None:
    """Exclude only the root manifest, not nested files with the same name."""
    bundle = tmp_path / "bundle"
    nested = bundle / "nested" / "SHA256SUMS"
    nested.parent.mkdir(parents=True)
    nested.write_text("nested evidence\n", encoding="utf-8")

    manifest = hash_tree.write_hashes(bundle)

    assert manifest.endswith("  nested/SHA256SUMS\n")


def test_hash_tree_rejects_symlinks(tmp_path: pathlib.Path) -> None:
    """Reject a manifest whose content can change through a symlink."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    target = tmp_path / "outside.txt"
    target.write_text("outside\n", encoding="utf-8")
    (bundle / "linked.txt").symlink_to(target)

    with pytest.raises(hash_tree.EvidenceTreeError, match="symlink"):
        hash_tree.write_hashes(bundle)


def test_hash_tree_rejects_a_symlinked_root(tmp_path: pathlib.Path) -> None:
    """Reject a root alias before hashing or mutating its target directory."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    (bundle / "evidence.txt").write_text("evidence\n", encoding="utf-8")
    alias = tmp_path / "bundle-alias"
    alias.symlink_to(bundle, target_is_directory=True)

    with pytest.raises(hash_tree.EvidenceTreeError, match="symlink"):
        hash_tree.write_hashes(alias)

    assert not (bundle / "SHA256SUMS").exists()


def test_hash_tree_rejects_unsupported_entry_types(tmp_path: pathlib.Path) -> None:
    """Reject filesystem entries other than regular files and directories."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    fifo = bundle / "events.fifo"
    os.mkfifo(fifo)

    with pytest.raises(hash_tree.EvidenceTreeError, match="unsupported"):
        hash_tree.write_hashes(bundle)
