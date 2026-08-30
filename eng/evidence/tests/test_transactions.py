"""Tests for atomic evidence producer assembly."""

from __future__ import annotations

import hashlib
import importlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import typing as t

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent))
assemble_bundle = t.cast(t.Any, importlib.import_module("assemble_bundle"))
record_deletion = t.cast(t.Any, importlib.import_module("record_deletion"))

REDACTION_CATEGORIES = [
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
]


def _repository(tmp_path: pathlib.Path) -> tuple[pathlib.Path, str]:
    repository = tmp_path / "repository"
    repository.mkdir()
    record_deletion.run_git(repository, "init", "--quiet")
    record_deletion.run_git(repository, "config", "user.name", "Evidence Test")
    record_deletion.run_git(
        repository, "config", "user.email", "evidence@example.invalid"
    )
    source = repository / "source.txt"
    source.write_text("source\n", encoding="utf-8")
    record_deletion.run_git(repository, "add", "source.txt")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "fixture")
    return repository, record_deletion.run_git(repository, "rev-parse", "HEAD")


def _publish_fixture(source: pathlib.Path, output: pathlib.Path) -> None:
    candidate, nonce = assemble_bundle.create_owned_candidate(output)
    shutil.copytree(source, candidate, dirs_exist_ok=True)
    assemble_bundle.publish_owned_candidate(candidate, output, nonce)


def _matrix_producer(
    root: pathlib.Path,
    commit: str,
    *,
    source_state: str = "clean",
    extra_file: str | None = None,
) -> pathlib.Path:
    fingerprint = assemble_bundle.source_tree_fingerprint(root.parents[1], [root])
    producer = root / "matrix"
    transcripts = producer / "protocol-transcripts"
    transcripts.mkdir(parents=True)
    files = [
        "environment.json",
        "results.ndjson",
        "redaction-proof.json",
        "protocol-transcripts/control.txt",
        "protocol-transcripts/pty.txt",
    ]
    (producer / "environment.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "frameworks": ["net10.0", "net8.0"],
                "includeMasterAdvisory": False,
                "platform": "linux",
                "redactionProof": True,
                "schemaVersion": 1,
                "sdkVersion": "10.0.302",
                "sourceState": source_state,
                "sourceTreeFingerprint": fingerprint,
                "tmuxVersions": [
                    "3.2a",
                    "3.3a",
                    "3.4",
                    "3.5",
                    "3.6",
                    "3.7a",
                    "3.7b",
                ],
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    rows = [
        {
            "advisory": False,
            "evaluatedCommit": commit,
            "framework": framework,
            "status": "passed",
            "testCount": 30,
            "tmuxSourceCommit": "e" * 40,
            "tmuxVersion": version,
        }
        for version in ["3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b"]
        for framework in ["net10.0", "net8.0"]
    ]
    (producer / "results.ndjson").write_text(
        "".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8"
    )
    (producer / "redaction-proof.json").write_text(
        json.dumps({"passed": True, "rejected": REDACTION_CATEGORIES}) + "\n",
        encoding="utf-8",
    )
    (transcripts / "control.txt").write_text(
        "event=control-send sequence=1 bytes=31\n"
        "event=control-receive sequence=1 marker=%begin\n"
        "event=control-receive sequence=1 marker=%end\n",
        encoding="utf-8",
    )
    (transcripts / "pty.txt").write_text(
        "event=pty-attach state=visible\nevent=pty-detach state=gone\n",
        encoding="utf-8",
    )
    if extra_file is not None:
        (producer / extra_file).write_text("stale\n", encoding="utf-8")
        files.append(extra_file)
    (producer / "producer.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "files": files,
                "producer": "matrix",
                "schemaVersion": 1,
                "sourceTreeFingerprint": fingerprint,
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return producer


def _aot_producer(root: pathlib.Path, commit: str) -> pathlib.Path:
    fingerprint = assemble_bundle.source_tree_fingerprint(root.parents[1], [root])
    producer = root / "aot"
    goldens = producer / "goldens"
    goldens.mkdir(parents=True)
    golden_names = [
        "attached-nvim.json",
        "regex-invariant.json",
        "typed-id.json",
        "turkish-ignore-case.json",
    ]
    files = [
        "aot-results.ndjson",
        "allocations.ndjson",
        "api-examples.md",
        "redaction-proof.json",
        "libtmux-query-v1.schema.json",
        *(f"goldens/{name}" for name in golden_names),
    ]
    (producer / "aot-results.ndjson").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "framework": "net10.0",
                "status": "passed",
            }
        )
        + "\n",
        encoding="utf-8",
    )
    (producer / "allocations.ndjson").write_text(
        json.dumps(
            {
                "allocatedBytes": 1,
                "evaluatedCommit": commit,
                "scenario": "query",
            }
        )
        + "\n",
        encoding="utf-8",
    )
    (producer / "api-examples.md").write_text(
        f"evaluatedCommit: {commit}\n\nExample output.\n", encoding="utf-8"
    )
    (producer / "redaction-proof.json").write_text(
        json.dumps({"passed": True, "rejected": REDACTION_CATEGORIES}) + "\n",
        encoding="utf-8",
    )
    (producer / "libtmux-query-v1.schema.json").write_text(
        json.dumps(
            {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object",
            }
        )
        + "\n",
        encoding="utf-8",
    )
    for name in golden_names:
        (goldens / name).write_text(
            json.dumps(
                {
                    "schema": "libtmux-query",
                    "version": 1,
                    "target": "session",
                    "predicate": {
                        "kind": "field",
                        "target": "session",
                        "wireName": "session_attached",
                    },
                },
                separators=(",", ":"),
            )
            + "\n",
            encoding="utf-8",
        )
    (producer / "producer.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "files": files,
                "producer": "aot",
                "schemaVersion": 1,
                "sourceTreeFingerprint": fingerprint,
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return producer


def _model_aot_producer(root: pathlib.Path, commit: str) -> pathlib.Path:
    fingerprint = assemble_bundle.source_tree_fingerprint(root.parents[1], [root])
    producer = root / "model-aot"
    producer.mkdir(parents=True)
    files = [
        "aot-results.ndjson",
        "allocations.ndjson",
        "api-examples.md",
        "model-aot-redaction-proof.json",
    ]
    lanes = [
        (contender, framework)
        for contender in ["Mutable", "Services", "Hybrid"]
        for framework in ["net10.0", "net8.0"]
    ]
    (producer / "aot-results.ndjson").write_text(
        "".join(
            json.dumps(
                {
                    "contender": contender,
                    "evaluatedCommit": commit,
                    "framework": framework,
                    "status": "passed",
                }
            )
            + "\n"
            for contender, framework in lanes
        ),
        encoding="utf-8",
    )
    (producer / "allocations.ndjson").write_text(
        "".join(
            json.dumps(
                {
                    "allocatedBytes": index,
                    "contender": contender,
                    "evaluatedCommit": commit,
                    "framework": framework,
                    "scenario": "materialization",
                }
            )
            + "\n"
            for index, (contender, framework) in enumerate(lanes)
        ),
        encoding="utf-8",
    )
    (producer / "api-examples.md").write_text(
        f"evaluatedCommit: {commit}\n\nModel API examples.\n", encoding="utf-8"
    )
    (producer / "model-aot-redaction-proof.json").write_text(
        json.dumps({"passed": True, "rejected": REDACTION_CATEGORIES}) + "\n",
        encoding="utf-8",
    )
    (producer / "producer.json").write_text(
        json.dumps(
            {
                "evaluatedCommit": commit,
                "files": files,
                "producer": "model-aot",
                "schemaVersion": 1,
                "sourceTreeFingerprint": fingerprint,
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return producer


def _read_rows(path: pathlib.Path) -> list[dict[str, t.Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def _write_rows(path: pathlib.Path, rows: list[dict[str, t.Any]]) -> None:
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")


def test_failed_transaction_preserves_validated_bundle(tmp_path: pathlib.Path) -> None:
    """Leave prior durable evidence byte-for-byte unchanged on failure."""
    repository, commit = _repository(tmp_path)
    output = repository / "artifacts" / "durable"
    prior = repository / "artifacts" / "prior"
    prior.mkdir(parents=True)
    marker = prior / "marker.txt"
    marker.write_bytes(b"validated prior bundle\n")
    _publish_fixture(prior, output)
    producer = _matrix_producer(
        repository / "artifacts" / "staging",
        commit,
        extra_file="stale.txt",
    )

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="not allowed"):
        assemble_bundle.assemble({"matrix": producer}, output, repository=repository)

    assert (output / "marker.txt").read_bytes() == b"validated prior bundle\n"


def test_mixed_commits_are_rejected_before_durable_touch(
    tmp_path: pathlib.Path,
) -> None:
    """Reject cross-commit producer manifests before pointer publication."""
    repository, commit = _repository(tmp_path)
    output = repository / "artifacts" / "durable"
    prior = repository / "artifacts" / "prior"
    prior.mkdir(parents=True)
    (prior / "marker.txt").write_text("prior\n", encoding="utf-8")
    _publish_fixture(prior, output)
    matrix = _matrix_producer(repository / "artifacts" / "staging", commit)
    aot = _aot_producer(repository / "artifacts" / "staging", "b" * 40)

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="mixed evaluated commits"
    ):
        assemble_bundle.assemble(
            {"matrix": matrix, "aot": aot}, output, repository=repository
        )

    assert (output / "marker.txt").read_text(encoding="utf-8") == "prior\n"


def test_unknown_producer_is_rejected(tmp_path: pathlib.Path) -> None:
    """Accept only the approved matrix and AOT producer types."""
    repository, commit = _repository(tmp_path)
    matrix = _matrix_producer(repository / "artifacts" / "staging", commit)

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="unknown producer"):
        assemble_bundle.assemble(
            {"matrix": matrix, "critic": matrix},
            repository / "artifacts" / "durable",
            repository=repository,
        )


def test_symlinked_producer_root_is_rejected_before_publication(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a producer root alias without touching the durable destination."""
    repository, commit = _repository(tmp_path)
    matrix = _matrix_producer(repository / "artifacts" / "staging", commit)
    alias = repository / "artifacts" / "matrix-alias"
    alias.symlink_to(matrix, target_is_directory=True)
    output = repository / "artifacts" / "durable"

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="symlink"):
        assemble_bundle.assemble({"matrix": alias}, output, repository=repository)

    assert not output.exists()


def test_partial_aot_producer_is_rejected(tmp_path: pathlib.Path) -> None:
    """Reject an AOT transaction missing any required artifact family."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)
    (aot / "allocations.ndjson").unlink()
    manifest = json.loads((aot / "producer.json").read_text(encoding="utf-8"))
    manifest["files"].remove("allocations.ndjson")
    (aot / "producer.json").write_text(json.dumps(manifest) + "\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="partial AOT"):
        assemble_bundle.inspect_producer("aot", aot)


def test_internal_aot_commit_must_match_manifest(tmp_path: pathlib.Path) -> None:
    """Reject AOT rows assembled under a different evaluated commit."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)
    (aot / "allocations.ndjson").write_text(
        json.dumps(
            {
                "allocatedBytes": 1,
                "evaluatedCommit": "b" * 40,
                "scenario": "query",
            }
        )
        + "\n",
        encoding="utf-8",
    )

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="internal commit"):
        assemble_bundle.inspect_producer("aot", aot)


def test_query_aot_accepts_commit_free_canonical_contract(
    tmp_path: pathlib.Path,
) -> None:
    """Keep provenance outside the closed query schema and golden envelopes."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)

    assemble_bundle.inspect_producer("aot", aot)

    schema = json.loads(
        (aot / "libtmux-query-v1.schema.json").read_text(encoding="utf-8")
    )
    golden = json.loads(
        (aot / "goldens" / "attached-nvim.json").read_text(encoding="utf-8")
    )
    assert "evaluatedCommit" not in schema
    assert "evaluatedCommit" not in golden


def test_query_aot_rejects_provenance_inside_a_closed_golden(
    tmp_path: pathlib.Path,
) -> None:
    """Reject metadata that would change the approved query wire document."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)
    path = aot / "goldens" / "attached-nvim.json"
    golden = json.loads(path.read_text(encoding="utf-8"))
    golden["evaluatedCommit"] = commit
    path.write_text(json.dumps(golden) + "\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="golden schema"):
        assemble_bundle.inspect_producer("aot", aot)


def test_query_aot_requires_all_four_canonical_goldens(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a query contract that omits one named canonical example."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)
    missing = "goldens/typed-id.json"
    (aot / missing).unlink()
    manifest = json.loads((aot / "producer.json").read_text(encoding="utf-8"))
    manifest["files"].remove(missing)
    (aot / "producer.json").write_text(json.dumps(manifest) + "\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="partial AOT"):
        assemble_bundle.inspect_producer("aot", aot)


def test_query_aot_rejects_conflicting_api_commit_markers(
    tmp_path: pathlib.Path,
) -> None:
    """Require one unambiguous machine-readable API provenance marker."""
    repository, commit = _repository(tmp_path)
    aot = _aot_producer(repository / "artifacts" / "staging", commit)
    path = aot / "api-examples.md"
    path.write_text(
        path.read_text(encoding="utf-8") + f"evaluatedCommit: {'b' * 40}\n",
        encoding="utf-8",
    )

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="internal commit"):
        assemble_bundle.inspect_producer("aot", aot)


def test_model_aot_assembles_flat_with_matrix(tmp_path: pathlib.Path) -> None:
    """Publish model AOT evidence at the decision bundle root."""
    repository, commit = _repository(tmp_path)
    staging = repository / "artifacts" / "staging"
    matrix = _matrix_producer(staging, commit)
    model_aot = _model_aot_producer(staging, commit)
    output = repository / "artifacts" / "durable"

    assemble_bundle.assemble(
        {"matrix": matrix, "model-aot": model_aot},
        output,
        repository=repository,
    )

    assert (output / "aot-results.ndjson").is_file()
    assert (output / "allocations.ndjson").is_file()
    assert (output / "api-examples.md").is_file()
    assert (output / "model-aot-redaction-proof.json").is_file()
    assert not (output / "model-aot").exists()
    assert not (output / "producer.json").exists()


@pytest.mark.parametrize(
    "missing",
    [
        "aot-results.ndjson",
        "allocations.ndjson",
        "api-examples.md",
        "model-aot-redaction-proof.json",
    ],
)
def test_model_aot_requires_each_artifact_family(
    tmp_path: pathlib.Path,
    missing: str,
) -> None:
    """Reject a model AOT manifest missing any complete artifact family."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    (model_aot / missing).unlink()
    manifest_path = model_aot / "producer.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"].remove(missing)
    manifest_path.write_text(json.dumps(manifest) + "\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="partial model AOT"):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_requires_a_producer_manifest(tmp_path: pathlib.Path) -> None:
    """Reject a model AOT directory without its transaction manifest."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    (model_aot / "producer.json").unlink()

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="manifest is missing"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


@pytest.mark.parametrize(
    "artifact",
    ["aot-results.ndjson", "allocations.ndjson", "api-examples.md"],
)
def test_model_aot_internal_commits_match_manifest(
    tmp_path: pathlib.Path,
    artifact: str,
) -> None:
    """Bind every model AOT evidence family to its producer commit."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / artifact
    if artifact == "api-examples.md":
        path.write_text(f"evaluatedCommit: {'b' * 40}\n", encoding="utf-8")
    else:
        rows = _read_rows(path)
        rows[0]["evaluatedCommit"] = "b" * 40
        _write_rows(path, rows)

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT internal commit"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


@pytest.mark.parametrize("artifact", ["aot-results.ndjson", "allocations.ndjson"])
def test_model_aot_rejects_a_missing_lane(
    tmp_path: pathlib.Path,
    artifact: str,
) -> None:
    """Require every contender and framework exactly once in both row families."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / artifact
    rows = _read_rows(path)
    _write_rows(path, rows[:-1])

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="model AOT lanes"):
        assemble_bundle.inspect_producer("model-aot", model_aot)


@pytest.mark.parametrize("artifact", ["aot-results.ndjson", "allocations.ndjson"])
def test_model_aot_rejects_a_duplicate_lane(
    tmp_path: pathlib.Path,
    artifact: str,
) -> None:
    """Reject duplicate contender and framework observations."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / artifact
    rows = _read_rows(path)
    _write_rows(path, [*rows, rows[0]])

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="model AOT lanes"):
        assemble_bundle.inspect_producer("model-aot", model_aot)


@pytest.mark.parametrize("allocated_bytes", [-1, True])
def test_model_aot_rejects_invalid_allocations(
    tmp_path: pathlib.Path,
    allocated_bytes: int | bool,
) -> None:
    """Accept only nonnegative integer allocation observations."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / "allocations.ndjson"
    rows = _read_rows(path)
    rows[0]["allocatedBytes"] = allocated_bytes
    _write_rows(path, rows)

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT allocation"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_requires_materialization_allocations(
    tmp_path: pathlib.Path,
) -> None:
    """Keep all relative allocation observations on one named scenario."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / "allocations.ndjson"
    rows = _read_rows(path)
    rows[0]["scenario"] = "probe"
    _write_rows(path, rows)

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT allocation"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_requires_passed_probe_rows(tmp_path: pathlib.Path) -> None:
    """Reject a lane whose native AOT probe did not pass."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / "aot-results.ndjson"
    rows = _read_rows(path)
    rows[0]["status"] = "failed"
    _write_rows(path, rows)

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="model AOT result"):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_api_commit_line_is_exactly_once(tmp_path: pathlib.Path) -> None:
    """Require one exact machine-readable commit line in API examples."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / "api-examples.md"
    path.write_text(
        f"evaluatedCommit: {commit}\n\nevaluatedCommit: {commit}\n",
        encoding="utf-8",
    )

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT internal commit"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_api_rejects_a_conflicting_commit_line(
    tmp_path: pathlib.Path,
) -> None:
    """Reject API examples carrying two different provenance markers."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    path = model_aot / "api-examples.md"
    path.write_text(
        f"evaluatedCommit: {commit}\n\nevaluatedCommit: {'b' * 40}\n",
        encoding="utf-8",
    )

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT internal commit"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_requires_complete_redaction_proof(tmp_path: pathlib.Path) -> None:
    """Require the canonical redaction categories in their stable order."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    (model_aot / "model-aot-redaction-proof.json").write_text(
        json.dumps({"passed": True, "rejected": REDACTION_CATEGORIES[:-1]}) + "\n",
        encoding="utf-8",
    )

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="model AOT redaction proof"
    ):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_model_aot_manifest_excludes_itself(tmp_path: pathlib.Path) -> None:
    """Keep producer.json as transaction metadata rather than decision evidence."""
    repository, commit = _repository(tmp_path)
    model_aot = _model_aot_producer(repository / "artifacts" / "staging", commit)
    manifest_path = model_aot / "producer.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"].append("producer.json")
    manifest_path.write_text(json.dumps(manifest) + "\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="unknown or stale"):
        assemble_bundle.inspect_producer("model-aot", model_aot)


def test_uncommitted_matrix_cannot_be_assembled(tmp_path: pathlib.Path) -> None:
    """Reject explicitly precommit evidence from durable assembly."""
    repository, commit = _repository(tmp_path)
    matrix = _matrix_producer(
        repository / "artifacts" / "staging",
        commit,
        source_state="uncommitted",
    )

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="clean source state"):
        assemble_bundle.assemble(
            {"matrix": matrix},
            repository / "artifacts" / "durable",
            repository=repository,
        )


def test_source_change_outside_evidence_paths_is_rejected(
    tmp_path: pathlib.Path,
) -> None:
    """Reject source drift while permitting declared evidence paths."""
    repository, commit = _repository(tmp_path)
    matrix = _matrix_producer(repository / "artifacts" / "staging", commit)
    (repository / "source.txt").write_text("changed\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="source changes"):
        assemble_bundle.assemble(
            {"matrix": matrix},
            repository / "artifacts" / "durable",
            repository=repository,
        )


def test_source_tree_fingerprint_changes_with_tracked_content(
    tmp_path: pathlib.Path,
) -> None:
    """Bind evidence to the tracked and index source tree, not only HEAD."""
    repository, _commit = _repository(tmp_path)
    clean = assemble_bundle.source_tree_fingerprint(repository)

    (repository / "source.txt").write_text("changed\n", encoding="utf-8")
    dirty = assemble_bundle.source_tree_fingerprint(repository)

    assert len(clean) == 64
    assert dirty != clean


def test_source_tree_fingerprint_includes_untracked_path_and_bytes(
    tmp_path: pathlib.Path,
) -> None:
    """Bind precommit identity to every nonignored untracked source byte."""
    repository, _commit = _repository(tmp_path)
    clean = assemble_bundle.source_tree_fingerprint(repository)
    untracked = repository / "csharp" / "spikes" / "SdkGlob.cs"
    untracked.parent.mkdir(parents=True)
    untracked.write_text("class First {}\n", encoding="utf-8")
    first = assemble_bundle.source_tree_fingerprint(repository)
    untracked.write_text("class Second {}\n", encoding="utf-8")
    second = assemble_bundle.source_tree_fingerprint(repository)

    assert clean != first
    assert first != second


def test_source_tree_fingerprint_excludes_tracked_index_and_worktree_bytes(
    tmp_path: pathlib.Path,
) -> None:
    """Omit a declared output from both index identity and current bytes."""
    repository, _commit = _repository(tmp_path)
    evidence = repository / "csharp" / "docs" / "parity" / "evidence" / "0001"
    evidence.mkdir(parents=True)
    result = evidence / "results.ndjson"
    result.write_text("first\n", encoding="utf-8")
    record_deletion.run_git(
        repository,
        "add",
        result.relative_to(repository).as_posix(),
    )
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "evidence")
    baseline = assemble_bundle.source_tree_fingerprint(repository, [evidence])

    result.write_text("second\n", encoding="utf-8")
    record_deletion.run_git(
        repository,
        "add",
        result.relative_to(repository).as_posix(),
    )

    assert assemble_bundle.source_tree_fingerprint(repository, [evidence]) == baseline
    assert assemble_bundle.source_tree_fingerprint(repository) != baseline


def test_source_fingerprint_cli_accepts_repeatable_excluded_roots(
    tmp_path: pathlib.Path,
) -> None:
    """Keep a CLI fingerprint stable across declared evidence rewrites."""
    repository, _commit = _repository(tmp_path)
    evidence = repository / "csharp" / "docs" / "parity" / "evidence"
    first_output = evidence / "0001"
    second_output = evidence / "candidate"
    first_output.mkdir(parents=True)
    second_output.mkdir(parents=True)
    result = first_output / "results.ndjson"
    candidate = second_output / "results.ndjson"
    result.write_text("first\n", encoding="utf-8")
    candidate.write_text("first\n", encoding="utf-8")
    record_deletion.run_git(repository, "add", "csharp/docs/parity")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "parity")
    command = [
        sys.executable,
        str(pathlib.Path(assemble_bundle.__file__)),
        "--source-fingerprint",
        str(repository),
        "--exclude-root",
        str(first_output),
        "--exclude-root",
        str(second_output),
    ]
    baseline = subprocess.run(
        command,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    result.write_text("second\n", encoding="utf-8")
    candidate.write_text("second\n", encoding="utf-8")
    record_deletion.run_git(repository, "add", "csharp/docs/parity")
    repeated = subprocess.run(
        command,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    assert repeated == baseline


def test_untracked_sdk_glob_source_is_uncommitted_and_not_durable(
    tmp_path: pathlib.Path,
) -> None:
    """Reject an untracked C# source file even when an SDK glob would compile it."""
    repository, commit = _repository(tmp_path)
    matrix = _matrix_producer(repository / "artifacts" / "staging", commit)
    untracked = repository / "csharp" / "spikes" / "SdkGlob.cs"
    untracked.parent.mkdir(parents=True)
    untracked.write_text("class HiddenSource {}\n", encoding="utf-8")

    assert assemble_bundle.source_state(repository) == "uncommitted"
    with pytest.raises(assemble_bundle.BundleAssemblyError, match="source changes"):
        assemble_bundle.assemble(
            {"matrix": matrix},
            repository / "artifacts" / "durable",
            repository=repository,
        )


def test_matrix_and_aot_publish_under_one_atomic_pointer(
    tmp_path: pathlib.Path,
) -> None:
    """Publish complete same-commit matrix and AOT evidence without collisions."""
    repository, commit = _repository(tmp_path)
    staging = repository / "artifacts" / "staging"
    matrix = _matrix_producer(staging, commit)
    aot = _aot_producer(staging, commit)
    output = repository / "artifacts" / "durable"

    assemble_bundle.assemble(
        {"matrix": matrix, "aot": aot}, output, repository=repository
    )

    assert output.is_dir()
    assert not output.is_symlink()
    assert (output / "results.ndjson").is_file()
    assert (output / "aot" / "aot-results.ndjson").is_file()
    assert (output / "aot" / "redaction-proof.json").is_file()
    assert not (output / "aot-results.ndjson").exists()
    assert not (output / "model-aot-redaction-proof.json").exists()


def test_directory_exchange_failure_preserves_prior_destination(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Keep the prior pointer live when atomic pointer replacement fails."""
    parent = tmp_path / "publication"
    first = parent / "first"
    first.mkdir(parents=True)
    (first / "marker.txt").write_text("prior\n", encoding="utf-8")
    output = parent / "current"
    _publish_fixture(first, output)
    second = parent / "second"
    second.mkdir()
    (second / "marker.txt").write_text("new\n", encoding="utf-8")

    def fail_exchange(_source: pathlib.Path, _destination: pathlib.Path) -> None:
        message = "injected exchange failure"
        raise OSError(message)

    monkeypatch.setattr(assemble_bundle, "_exchange_directories", fail_exchange)

    with pytest.raises(OSError, match="exchange failure"):
        _publish_fixture(second, output)

    assert output.is_dir()
    assert not output.is_symlink()
    assert (output / "marker.txt").read_text(encoding="utf-8") == "prior\n"


def test_owned_cleanup_refuses_arbitrary_candidate_basename(
    tmp_path: pathlib.Path,
) -> None:
    """Never infer recursive deletion authority from a caller-supplied basename."""
    output = tmp_path / "publication" / "current"
    candidate = tmp_path / "publication" / "important.candidate-data"
    candidate.mkdir(parents=True)
    (candidate / "keep.txt").write_text("keep\n", encoding="utf-8")

    with pytest.raises(assemble_bundle.BundleAssemblyError, match="ownership"):
        assemble_bundle.discard_owned_candidate(candidate, output, "wrong-nonce")

    assert (candidate / "keep.txt").is_file()


def test_owned_candidate_cleanup_requires_and_consumes_nonce(
    tmp_path: pathlib.Path,
) -> None:
    """Clean only a generated sibling carrying the caller's ownership nonce."""
    output = tmp_path / "publication" / "current"
    candidate, nonce = assemble_bundle.create_owned_candidate(output)
    (candidate / "partial.txt").write_text("partial\n", encoding="utf-8")

    assemble_bundle.discard_owned_candidate(candidate, output, nonce)

    assert not candidate.exists()


def test_owned_publication_failure_remains_recoverable(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Preserve caller ownership after a failed exchange so cleanup can recover."""
    output = tmp_path / "publication" / "current"
    prior, prior_nonce = assemble_bundle.create_owned_candidate(output)
    (prior / "marker.txt").write_text("prior\n", encoding="utf-8")
    assemble_bundle.publish_owned_candidate(prior, output, prior_nonce)
    candidate, nonce = assemble_bundle.create_owned_candidate(output)
    (candidate / "marker.txt").write_text("new\n", encoding="utf-8")

    def fail_exchange(_source: pathlib.Path, _destination: pathlib.Path) -> None:
        message = "injected exchange failure"
        raise OSError(message)

    monkeypatch.setattr(assemble_bundle, "_exchange_directories", fail_exchange)
    with pytest.raises(OSError, match="exchange failure"):
        assemble_bundle.publish_owned_candidate(candidate, output, nonce)

    assert (output / "marker.txt").read_text(encoding="utf-8") == "prior\n"
    assemble_bundle.discard_owned_candidate(candidate, output, nonce)
    assert not candidate.exists()


def test_cleanup_failure_after_publication_is_not_reported(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Keep new data published when retiring the prior version fails."""
    parent = tmp_path / "publication"
    first = parent / "first"
    first.mkdir(parents=True)
    (first / "marker.txt").write_text("prior\n", encoding="utf-8")
    output = parent / "current"
    _publish_fixture(first, output)
    second = parent / "second"
    second.mkdir()
    (second / "marker.txt").write_text("new\n", encoding="utf-8")

    def fail_cleanup(_path: pathlib.Path) -> None:
        message = "injected cleanup failure"
        raise OSError(message)

    monkeypatch.setattr(assemble_bundle, "_remove_version", fail_cleanup)

    _publish_fixture(second, output)

    assert output.is_dir()
    assert not output.is_symlink()
    assert (output / "marker.txt").read_text(encoding="utf-8") == "new\n"


def test_directory_exchange_rejects_unsupported_platform(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Fail explicitly where no native atomic directory exchange exists."""
    source = tmp_path / "source"
    destination = tmp_path / "destination"
    source.mkdir()
    destination.mkdir()
    monkeypatch.setattr(assemble_bundle.sys, "platform", "win32")

    with pytest.raises(
        assemble_bundle.BundleAssemblyError, match="atomic directory exchange"
    ):
        assemble_bundle._exchange_directories(source, destination)


def _cached_tmux_fixture(
    tmp_path: pathlib.Path,
) -> tuple[pathlib.Path, pathlib.Path, str]:
    artifacts = tmp_path / "artifacts"
    source = artifacts / "sources" / "3.2a"
    source.mkdir(parents=True)
    record_deletion.run_git(source, "init", "--quiet")
    record_deletion.run_git(source, "config", "user.name", "Cache Test")
    record_deletion.run_git(source, "config", "user.email", "cache@example.invalid")
    (source / "source.txt").write_text("tmux\n", encoding="utf-8")
    record_deletion.run_git(source, "add", "source.txt")
    record_deletion.run_git(source, "commit", "--quiet", "-m", "fixture")
    record_deletion.run_git(source, "tag", "3.2a")
    commit = record_deletion.run_git(source, "rev-parse", "HEAD")
    install = artifacts / "installs" / "3.2a"
    binary = install / "bin" / "tmux"
    binary.parent.mkdir(parents=True)
    binary.write_text("#!/bin/sh\nprintf 'tmux 3.2a\\n'\n", encoding="utf-8")
    binary.chmod(0o755)
    (install / "source-commit").write_text(commit + "\n", encoding="utf-8")
    (install / "cache-metadata.json").write_text(
        json.dumps(
            {
                "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                "binaryVersion": "3.2a",
                "schemaVersion": 1,
                "sourceCommit": commit,
                "sourceRef": "3.2a",
                "version": "3.2a",
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return artifacts, binary, commit


def test_build_version_accepts_only_complete_valid_cache(
    tmp_path: pathlib.Path,
) -> None:
    """Accept an atomic cache only when source, binary, and metadata agree."""
    artifacts, binary, commit = _cached_tmux_fixture(tmp_path)
    script = pathlib.Path(__file__).parents[2] / "tmux" / "build-version.sh"

    result = subprocess.run(
        [str(script), "3.2a"],
        check=False,
        capture_output=True,
        text=True,
        env={**os.environ, "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifacts)},
    )

    assert result.returncode == 0, result.stderr
    assert f"binary={binary}" in result.stdout
    assert f"commit={commit}" in result.stdout


def test_build_version_rejects_invalid_worker_limit(tmp_path: pathlib.Path) -> None:
    """Reject a worker limit that cannot bound make concurrency."""
    artifacts, _binary, _commit = _cached_tmux_fixture(tmp_path)
    script = pathlib.Path(__file__).parents[2] / "tmux" / "build-version.sh"

    result = subprocess.run(
        [str(script), "3.2a"],
        check=False,
        capture_output=True,
        text=True,
        env={
            **os.environ,
            "LIBTMUX_BUILD_JOBS": "0",
            "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifacts),
        },
    )

    assert result.returncode == 2
    assert "LIBTMUX_BUILD_JOBS must be a positive integer" in result.stderr


@pytest.mark.parametrize("digest_tool", ["sha256sum", "shasum"])
def test_build_version_selects_portable_digest_tool(
    tmp_path: pathlib.Path,
    digest_tool: str,
) -> None:
    """Validate identical cache digests with the normal Linux and macOS tools."""
    artifacts, binary, commit = _cached_tmux_fixture(tmp_path)
    script = pathlib.Path(__file__).parents[2] / "tmux" / "build-version.sh"
    tool_directory = tmp_path / "tools"
    tool_directory.mkdir()
    required = ["awk", "bash", "dirname", "git", "jq", "sed", digest_tool]
    for command in required:
        executable = pathlib.Path(
            subprocess.run(
                ["which", command],
                check=True,
                capture_output=True,
                text=True,
            ).stdout.strip()
        )
        (tool_directory / command).symlink_to(executable)

    result = subprocess.run(
        [str(script), "3.2a"],
        check=False,
        capture_output=True,
        text=True,
        env={
            **os.environ,
            "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifacts),
            "PATH": str(tool_directory),
        },
    )

    assert result.returncode == 0, result.stderr
    assert f"binary={binary}" in result.stdout
    assert f"commit={commit}" in result.stdout


def test_build_version_rejects_tampered_cached_binary(
    tmp_path: pathlib.Path,
) -> None:
    """Reject and attempt to rebuild a cache whose binary digest changed."""
    artifacts, binary, _commit = _cached_tmux_fixture(tmp_path)
    binary.write_text("#!/bin/sh\nprintf 'tmux tampered\\n'\n", encoding="utf-8")
    binary.chmod(0o755)
    script = pathlib.Path(__file__).parents[2] / "tmux" / "build-version.sh"

    result = subprocess.run(
        [str(script), "3.2a"],
        check=False,
        capture_output=True,
        text=True,
        env={**os.environ, "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifacts)},
    )

    assert result.returncode != 0
    assert "cache validation failed; rebuilding" in result.stderr


@pytest.mark.parametrize(
    "tamper",
    ["metadata-commit", "metadata-ref", "reported-version", "source-head"],
)
def test_build_version_rejects_inconsistent_cache_contracts(
    tmp_path: pathlib.Path,
    tamper: str,
) -> None:
    """Reject cache metadata that disagrees with source or executable identity."""
    artifacts, binary, _commit = _cached_tmux_fixture(tmp_path)
    metadata_path = artifacts / "installs" / "3.2a" / "cache-metadata.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if tamper == "metadata-commit":
        metadata["sourceCommit"] = "a" * 40
    elif tamper == "metadata-ref":
        metadata["sourceRef"] = "3.3a"
    elif tamper == "reported-version":
        binary.write_text("#!/bin/sh\nprintf 'tmux 9.9\\n'\n", encoding="utf-8")
        binary.chmod(0o755)
        metadata["binarySha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        metadata["binaryVersion"] = "9.9"
    else:
        source = artifacts / "sources" / "3.2a"
        (source / "source.txt").write_text("changed\n", encoding="utf-8")
        record_deletion.run_git(source, "add", "source.txt")
        record_deletion.run_git(source, "commit", "--quiet", "-m", "drift")
    metadata_path.write_text(json.dumps(metadata) + "\n", encoding="utf-8")
    script = pathlib.Path(__file__).parents[2] / "tmux" / "build-version.sh"

    result = subprocess.run(
        [str(script), "3.2a"],
        check=False,
        capture_output=True,
        text=True,
        env={**os.environ, "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifacts)},
    )

    assert result.returncode != 0
    assert "cache validation failed; rebuilding" in result.stderr


def test_matrix_runner_delegates_atomic_publication_and_binds_source() -> None:
    """Keep shell publication, transcripts, and commit binding on tested helpers."""
    script = pathlib.Path(__file__).parents[2] / "tmux" / "run-matrix.sh"
    content = script.read_text(encoding="utf-8")

    assert ".retired" not in content
    assert "rm -rf" not in content
    assert "--remove-private" not in content
    assert "--publish-candidate" in content
    assert "--discard-candidate" in content
    assert "--ownership-nonce" in content
    assert "--source-fingerprint" in content
    assert "--source-state" in content
    assert "includeMasterAdvisory" in content
    assert "failed true null 0" in content
    assert "sourceTreeFingerprint" in content
    assert "sourceState" in content
    assert "LIBTMUX_PROTOCOL_TRANSCRIPT_DIR" in content
    assert "platform_name=linux" in content
    assert "platform_name=macos" in content
