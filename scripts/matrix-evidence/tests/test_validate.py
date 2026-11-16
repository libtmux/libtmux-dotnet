"""Tests for evidence bundle validation."""

from __future__ import annotations

import importlib
import json
import pathlib
import sys
import typing as t

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent))
validate = t.cast(t.Any, importlib.import_module("validate"))

COMMIT = "c" * 40
SOURCE_FINGERPRINT = "d" * 64
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
REQUIRED_TMUX_VERSIONS = [
    "3.3a",
    "3.4",
    "3.5",
    "3.6",
    "3.7a",
    "3.7b",
    "3.7c",
    "3.8",
]
REQUIRED_FRAMEWORKS = ["net10.0", "net8.0"]
REQUIRED_MATRIX_ROWS = len(REQUIRED_TMUX_VERSIONS) * len(REQUIRED_FRAMEWORKS)
TRANSITION_TMUX_SOURCE_COMMIT = "7" * 40
EVALUATED_COMMIT_TREE = "e" * 40
CAPABILITY_COHORT = "0001"
CLOSURE_COHORT = "closure"
INTEGRATION_PROJECT = "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
MIRROR_REFRESH_SKIP = (
    "LibTmux.IntegrationTests.Snapshots.ServerMirrorTests."
    "A_change_tmux_does_not_announce_is_seen_within_the_refresh_interval"
)


def _matrix_rows(commit: str) -> list[dict[str, t.Any]]:
    return [
        {
            "advisory": False,
            "evaluatedCommit": commit,
            "framework": framework,
            "status": "passed",
            "testCount": 30,
            "tmuxSourceCommit": "e" * 40,
            "tmuxVersion": version,
        }
        for version in REQUIRED_TMUX_VERSIONS
        for framework in REQUIRED_FRAMEWORKS
    ]


def _write_rows(bundle: pathlib.Path, rows: list[dict[str, t.Any]]) -> None:
    (bundle / "results.ndjson").write_text(
        "".join(json.dumps(row, sort_keys=True) + "\n" for row in rows),
        encoding="utf-8",
    )


def _matrix_bundle(
    root: pathlib.Path,
    commit: str = COMMIT,
    bundle_name: str = "bundle",
    *,
    include_master_advisory: bool = False,
    capability_cohort: bool = True,
) -> pathlib.Path:
    bundle = root / bundle_name
    transcripts = bundle / "protocol-transcripts"
    transcripts.mkdir(parents=True)
    environment = {
        "evaluatedCommit": commit,
        "frameworks": REQUIRED_FRAMEWORKS,
        "includeMasterAdvisory": include_master_advisory,
        "platform": "linux",
        "redactionProof": True,
        "schemaVersion": 1,
        "sdkVersion": "10.0.302",
        "sourceState": "clean",
        "sourceTreeFingerprint": SOURCE_FINGERPRINT,
        "tmuxVersions": REQUIRED_TMUX_VERSIONS,
    }
    if capability_cohort:
        environment.update(
            {
                "capabilityCohort": CAPABILITY_COHORT,
                "evaluatedCommitTree": EVALUATED_COMMIT_TREE,
                "transitionTmuxSourceCommits": {"3.7": TRANSITION_TMUX_SOURCE_COMMIT},
            }
        )
    (bundle / "environment.json").write_text(
        json.dumps(environment, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    _write_rows(bundle, _matrix_rows(commit))
    (bundle / "redaction-proof.json").write_text(
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
    if capability_cohort:
        _write_break_pane_transition(bundle)
    return bundle


def _write_break_pane_transition(
    bundle: pathlib.Path,
    *,
    rows: list[dict[str, t.Any]] | None = None,
) -> None:
    """Write the exact four-record break-pane transition proof."""
    transition_rows = rows or [
        {
            "framework": framework,
            "outcome": "passed",
            "tmuxSourceCommit": (TRANSITION_TMUX_SOURCE_COMMIT if version == "3.7" else "e" * 40),
            "tmuxVersion": version,
            "workaroundApplied": version == "3.7",
        }
        for framework in REQUIRED_FRAMEWORKS
        for version in ["3.7", "3.7a"]
    ]
    lines = [
        "event=break-pane-transition "
        f"framework={row['framework']} "
        f"tmux-source-commit={row['tmuxSourceCommit']} "
        f"tmux-version={row['tmuxVersion']} "
        f"workaround={'applied' if row['workaroundApplied'] else 'omitted'} "
        f"outcome={row['outcome']}"
        for row in transition_rows
    ]
    (bundle / "protocol-transcripts" / "break-pane-transition.txt").write_text(
        "\n".join(lines) + "\n",
        encoding="utf-8",
    )


def test_matrix_phase_accepts_exact_break_pane_transition_proof(
    tmp_path: pathlib.Path,
) -> None:
    """Accept four source-bound observations across both target frameworks."""
    bundle = _matrix_bundle(tmp_path)

    validate.validate_bundle(bundle)


def test_matrix_phase_retains_previous_complete_release_set(
    tmp_path: pathlib.Path,
) -> None:
    """Keep recorded 3.7b evidence valid after the required matrix widens."""
    bundle = _matrix_bundle(tmp_path)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["tmuxVersions"] = REQUIRED_TMUX_VERSIONS[:-1]
    environment_path.write_text(
        json.dumps(environment, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    rows = [row for row in _matrix_rows(COMMIT) if row["tmuxVersion"] != REQUIRED_TMUX_VERSIONS[-1]]
    _write_rows(bundle, rows)

    validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    "mutation",
    ["missing-marker", "missing-transition", "unknown-marker"],
)
@pytest.mark.parametrize("cohort", [CAPABILITY_COHORT, "workspace"])
def test_matrix_phase_requires_exact_component_three_cohort_contract(
    tmp_path: pathlib.Path,
    mutation: str,
    cohort: str,
) -> None:
    """Require the declared cohort and authenticated transition provenance."""
    bundle = _matrix_bundle(tmp_path)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["capabilityCohort"] = cohort
    if mutation == "missing-marker":
        environment.pop("capabilityCohort")
        environment.pop("evaluatedCommitTree")
    elif mutation == "missing-transition":
        environment.pop("transitionTmuxSourceCommits")
    else:
        environment["capabilityCohort"] = "unknown"
    environment_path.write_text(
        json.dumps(environment) + "\n",
        encoding="utf-8",
    )

    with pytest.raises(validate.EvidenceValidationError, match=r"cohort|environment"):
        validate.validate_bundle(bundle)


def test_workspace_matrix_requires_current_stable_versions(
    tmp_path: pathlib.Path,
) -> None:
    """Legacy cohort version sets cannot satisfy a fresh workspace matrix."""
    bundle = _matrix_bundle(tmp_path)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["capabilityCohort"] = "workspace"
    environment["tmuxVersions"] = REQUIRED_TMUX_VERSIONS[:-1]
    environment_path.write_text(json.dumps(environment) + "\n", encoding="utf-8")
    _write_rows(bundle, [row for row in _matrix_rows(COMMIT) if row["tmuxVersion"] != "3.7c"])
    with pytest.raises(validate.EvidenceValidationError, match="environment observations"):
        validate.validate_bundle(bundle)


def test_matrix_phase_accepts_exact_wrapper_policy_closure_cohort(
    tmp_path: pathlib.Path,
) -> None:
    """Accept source-bound closure evidence without Component 3 transition data."""
    commit = "a" * 40
    bundle = _matrix_bundle(tmp_path, commit, capability_cohort=False)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["capabilityCohort"] = CLOSURE_COHORT
    environment["evaluatedCommitTree"] = EVALUATED_COMMIT_TREE
    environment_path.write_text(
        json.dumps(environment, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    validate.validate_bundle(bundle)
    assert validate.validate_matrix(bundle) == commit


def test_matrix_phase_rejects_transition_sidecar_for_closure_cohort(
    tmp_path: pathlib.Path,
) -> None:
    """Keep raw 3.7 transition provenance exclusive to cohort 0001."""
    commit = "a" * 40
    bundle = _matrix_bundle(tmp_path, commit, capability_cohort=False)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["capabilityCohort"] = CLOSURE_COHORT
    environment["evaluatedCommitTree"] = EVALUATED_COMMIT_TREE
    environment_path.write_text(
        json.dumps(environment, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    transition = bundle / "protocol-transcripts" / "break-pane-transition.txt"
    transition.write_text(
        "event=break-pane-transition framework=net8.0 "
        f"tmux-source-commit={TRANSITION_TMUX_SOURCE_COMMIT} "
        "tmux-version=3.7 workaround=applied outcome=passed\n",
        encoding="utf-8",
    )

    with pytest.raises(validate.EvidenceValidationError, match="transition"):
        validate.validate_bundle(bundle)


def test_matrix_phase_forbids_master_rows_for_component_three_cohort(
    tmp_path: pathlib.Path,
) -> None:
    """Keep Component 3 evidence at exactly seven versions by two TFMs."""
    bundle = _matrix_bundle(tmp_path)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["includeMasterAdvisory"] = True
    environment_path.write_text(json.dumps(environment) + "\n", encoding="utf-8")
    rows = _matrix_rows(COMMIT)
    rows.extend(
        {
            "advisory": True,
            "evaluatedCommit": COMMIT,
            "framework": framework,
            "status": "passed",
            "testCount": 30,
            "tmuxSourceCommit": "f" * 40,
            "tmuxVersion": "master",
        }
        for framework in REQUIRED_FRAMEWORKS
    )
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="cohort"):
        validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    ("mutation", "error"),
    [
        ("missing", "transition transcript"),
        ("duplicate", "transition transcript"),
        ("workaround", "transition transcript"),
        ("transition-source", "transition transcript"),
        ("matrix-source", "transition transcript"),
    ],
)
def test_matrix_phase_rejects_unbound_break_pane_transition_proof(
    tmp_path: pathlib.Path,
    mutation: str,
    error: str,
) -> None:
    """Reject incomplete, duplicated, or source-drifted transition records."""
    bundle = _matrix_bundle(tmp_path)
    rows = [
        {
            "framework": framework,
            "outcome": "passed",
            "tmuxSourceCommit": (TRANSITION_TMUX_SOURCE_COMMIT if version == "3.7" else "e" * 40),
            "tmuxVersion": version,
            "workaroundApplied": version == "3.7",
        }
        for framework in REQUIRED_FRAMEWORKS
        for version in ["3.7", "3.7a"]
    ]
    if mutation == "missing":
        rows.pop()
    elif mutation == "duplicate":
        rows[-1] = dict(rows[0])
    elif mutation == "workaround":
        rows[0]["workaroundApplied"] = False
    elif mutation == "transition-source":
        rows[0]["tmuxSourceCommit"] = "f" * 40
    else:
        matrix = _matrix_rows(COMMIT)
        for row in matrix:
            if row["tmuxVersion"] == "3.7a":
                row["tmuxSourceCommit"] = "a" * 40
        _write_rows(bundle, matrix)
    _write_break_pane_transition(bundle, rows=rows)

    with pytest.raises(validate.EvidenceValidationError, match=error):
        validate.validate_bundle(bundle)


def test_matrix_phase_requires_all_release_framework_rows(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a required matrix with one missing framework row."""
    bundle = _matrix_bundle(tmp_path)
    _write_rows(bundle, _matrix_rows(COMMIT)[:-1])

    with pytest.raises(validate.EvidenceValidationError, match="required matrix"):
        validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("frameworks", ["net8.0", "net10.0"]),
        ("tmuxVersions", ["3.7b"]),
        ("tmuxVersions", [{}]),
        ("schemaVersion", 2),
        ("sourceState", "maybe"),
        ("sourceTreeFingerprint", "short"),
        ("includeMasterAdvisory", "true"),
        ("extra", True),
    ],
)
def test_matrix_phase_rejects_malformed_environment(
    tmp_path: pathlib.Path,
    field: str,
    value: t.Any,
) -> None:
    """Reject unknown keys and nonexact environment observations."""
    bundle = _matrix_bundle(tmp_path)
    environment = json.loads((bundle / "environment.json").read_text(encoding="utf-8"))
    environment[field] = value
    (bundle / "environment.json").write_text(json.dumps(environment) + "\n", encoding="utf-8")

    with pytest.raises(validate.EvidenceValidationError, match="environment"):
        validate.validate_bundle(bundle)


def test_matrix_phase_requires_include_master_environment_key(
    tmp_path: pathlib.Path,
) -> None:
    """Reject an environment that omits the exact advisory declaration."""
    bundle = _matrix_bundle(tmp_path)
    environment = json.loads((bundle / "environment.json").read_text(encoding="utf-8"))
    environment.pop("includeMasterAdvisory")
    (bundle / "environment.json").write_text(json.dumps(environment) + "\n", encoding="utf-8")

    with pytest.raises(validate.EvidenceValidationError, match="environment"):
        validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("advisory", True),
        ("status", "failed"),
        ("testCount", 0),
        ("testCount", True),
        ("tmuxSourceCommit", "short"),
        ("extra", "unknown"),
    ],
)
def test_matrix_phase_rejects_malformed_required_rows(
    tmp_path: pathlib.Path,
    field: str,
    value: t.Any,
) -> None:
    """Reject required rows that are not exact passing observations."""
    bundle = _matrix_bundle(tmp_path)
    rows = _matrix_rows(COMMIT)
    rows[0][field] = value
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="matrix row"):
        validate.validate_bundle(bundle)


def test_matrix_phase_rejects_duplicate_and_unknown_rows(
    tmp_path: pathlib.Path,
) -> None:
    """Reject duplicate pairs and versions outside the required/advisory set."""
    bundle = _matrix_bundle(tmp_path)
    rows = _matrix_rows(COMMIT)
    rows.append(dict(rows[0]))
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="duplicate"):
        validate.validate_bundle(bundle)

    rows[-1]["tmuxVersion"] = "3.9"
    _write_rows(bundle, rows)
    with pytest.raises(validate.EvidenceValidationError, match="unknown"):
        validate.validate_bundle(bundle)


def test_matrix_phase_requires_master_rows_only_when_declared(
    tmp_path: pathlib.Path,
) -> None:
    """Bind the exact optional advisory pair to its environment declaration."""
    bundle = _matrix_bundle(tmp_path, capability_cohort=False)
    rows = _matrix_rows(COMMIT)
    rows.append(
        {
            "advisory": True,
            "evaluatedCommit": COMMIT,
            "framework": "net10.0",
            "status": "passed",
            "testCount": 30,
            "tmuxSourceCommit": "f" * 40,
            "tmuxVersion": "master",
        }
    )
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="master"):
        validate.validate_bundle(bundle)

    bundle = _matrix_bundle(
        tmp_path,
        bundle_name="requested",
        include_master_advisory=True,
        capability_cohort=False,
    )
    _write_rows(bundle, _matrix_rows(COMMIT))
    with pytest.raises(validate.EvidenceValidationError, match="master"):
        validate.validate_bundle(bundle)


def test_matrix_phase_accepts_failed_master_without_changing_required_verdict(
    tmp_path: pathlib.Path,
) -> None:
    """Treat independently failed advisory lanes as non-gating observations."""
    bundle = _matrix_bundle(
        tmp_path,
        include_master_advisory=True,
        capability_cohort=False,
    )
    rows = _matrix_rows(COMMIT)
    rows.extend(
        [
            {
                "advisory": True,
                "evaluatedCommit": COMMIT,
                "framework": framework,
                "status": "failed",
                "testCount": 0,
                "tmuxSourceCommit": None,
                "tmuxVersion": "master",
            }
            for framework in ["net10.0", "net8.0"]
        ]
    )
    _write_rows(bundle, rows)

    validate.validate_bundle(bundle)


def test_matrix_phase_accepts_independent_observed_master_verdicts(
    tmp_path: pathlib.Path,
) -> None:
    """Accept passed and failed advisory lanes with shared build provenance."""
    bundle = _matrix_bundle(
        tmp_path,
        include_master_advisory=True,
        capability_cohort=False,
    )
    rows = _matrix_rows(COMMIT)
    rows.extend(
        [
            {
                "advisory": True,
                "evaluatedCommit": COMMIT,
                "framework": "net10.0",
                "status": "passed",
                "testCount": 30,
                "tmuxSourceCommit": "f" * 40,
                "tmuxVersion": "master",
            },
            {
                "advisory": True,
                "evaluatedCommit": COMMIT,
                "framework": "net8.0",
                "status": "failed",
                "testCount": 0,
                "tmuxSourceCommit": "f" * 40,
                "tmuxVersion": "master",
            },
        ]
    )
    _write_rows(bundle, rows)

    validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    "mutation",
    [
        "one-row",
        "mixed-source",
        "null-positive",
        "null-passed",
        "negative-count",
        "passed-zero",
    ],
)
def test_matrix_phase_rejects_malformed_master_rows(
    tmp_path: pathlib.Path,
    mutation: str,
) -> None:
    """Reject incomplete or internally inconsistent advisory observations."""
    bundle = _matrix_bundle(
        tmp_path,
        include_master_advisory=True,
        capability_cohort=False,
    )
    master = [
        {
            "advisory": True,
            "evaluatedCommit": COMMIT,
            "framework": framework,
            "status": "failed",
            "testCount": 0,
            "tmuxSourceCommit": None,
            "tmuxVersion": "master",
        }
        for framework in ["net10.0", "net8.0"]
    ]
    if mutation == "one-row":
        master.pop()
    elif mutation == "mixed-source":
        master[0]["tmuxSourceCommit"] = "f" * 40
    elif mutation == "null-positive":
        master[0]["testCount"] = 1
    elif mutation == "null-passed":
        master[0]["status"] = "passed"
    elif mutation == "negative-count":
        master[0]["tmuxSourceCommit"] = "f" * 40
        master[1]["tmuxSourceCommit"] = "f" * 40
        master[0]["testCount"] = -1
    else:
        master[0]["tmuxSourceCommit"] = "f" * 40
        master[1]["tmuxSourceCommit"] = "f" * 40
        master[0]["status"] = "passed"
        master[0]["testCount"] = 0
    _write_rows(bundle, _matrix_rows(COMMIT) + master)

    with pytest.raises(validate.EvidenceValidationError, match="master"):
        validate.validate_bundle(bundle)


def test_matrix_phase_requires_per_version_source_consistency(
    tmp_path: pathlib.Path,
) -> None:
    """Bind both framework observations to one tmux source revision."""
    bundle = _matrix_bundle(tmp_path)
    rows = _matrix_rows(COMMIT)
    rows[1]["tmuxSourceCommit"] = "f" * 40
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="source commit"):
        validate.validate_bundle(bundle)


@pytest.mark.parametrize("mutation", ["missing", "extra", "false", "unknown-key"])
def test_matrix_phase_requires_exact_redaction_proof(
    tmp_path: pathlib.Path,
    mutation: str,
) -> None:
    """Require an exact complete set of independently scanned categories."""
    bundle = _matrix_bundle(tmp_path)
    proof: dict[str, t.Any] = {"passed": True, "rejected": list(REDACTION_CATEGORIES)}
    if mutation == "missing":
        proof["rejected"].pop()
    elif mutation == "extra":
        proof["rejected"].append("other")
    elif mutation == "false":
        proof["passed"] = False
    else:
        proof["extra"] = True
    (bundle / "redaction-proof.json").write_text(json.dumps(proof) + "\n", encoding="utf-8")

    with pytest.raises(validate.EvidenceValidationError, match="redaction proof"):
        validate.validate_bundle(bundle)


@pytest.mark.parametrize(
    "category",
    [
        "absolute",
        "email",
        "environment",
        "executable",
        "hostname",
        "socket",
        "temporary",
        "tty",
        "token",
        "username",
    ],
)
def test_redaction_rejects_embedded_sensitive_values(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    category: str,
) -> None:
    """Reject sensitive values embedded inside otherwise harmless text."""
    values = {
        "absolute": "/private/worktree/source.cs",
        "email": "person@example.invalid",
        "environment": "fixture-secret-value",
        "executable": sys.executable,
        "hostname": "fixture-hostname",
        "socket": "lt-socket-abcdefgh1234",
        "temporary": "/tmp/private-build/output",
        "tty": "/dev/pts/42",
        "token": "token=abcdefghijklmno",
        "username": "fixture-username",
    }
    monkeypatch.setenv("LIBTMUX_TEST_SECRET", values["environment"])
    monkeypatch.setenv("USER", values["username"])
    monkeypatch.setattr(validate.socket, "gethostname", lambda: values["hostname"])
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    (bundle / "leak.txt").write_text(
        f"safe-prefix-{values[category]}-safe-suffix\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="sensitive"):
        validate.reject_sensitive_content(bundle)


def test_redaction_allows_benign_environment_values_in_prose(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Allow ordinary prose that matches a noncredential environment value."""
    monkeypatch.setenv("STARSHIP_LOG", "error")
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    (bundle / "review.txt").write_text("status=error\n", encoding="utf-8")

    validate.reject_sensitive_content(bundle)


@pytest.mark.parametrize(
    "absolute_path",
    ["/a", "/opt", "C:/SDK", "C:\\SDK", "\\\\server\\share"],
)
def test_redaction_rejects_portable_absolute_paths(
    tmp_path: pathlib.Path,
    absolute_path: str,
) -> None:
    """Reject embedded Unix, drive-qualified, and UNC absolute paths."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    (bundle / "leak.txt").write_text(f"prefix={absolute_path};suffix=safe\n", encoding="utf-8")

    with pytest.raises(validate.EvidenceValidationError, match="sensitive"):
        validate.reject_sensitive_content(bundle)


def test_redaction_distinguishes_json_unicode_ranges_from_unc_paths(
    tmp_path: pathlib.Path,
) -> None:
    """Allow schema escape ranges without weakening JSON UNC rejection."""
    bundle = tmp_path / "bundle"
    bundle.mkdir()
    schema = bundle / "schema.json"
    schema.write_text(
        json.dumps({"pattern": r"^[^\uD800-\uDFFF]*$"}) + "\n",
        encoding="utf-8",
    )

    validate.reject_sensitive_content(bundle)

    schema.write_text(
        json.dumps({"path": r"\\server\share"}) + "\n",
        encoding="utf-8",
    )
    with pytest.raises(validate.EvidenceValidationError, match="sensitive"):
        validate.reject_sensitive_content(bundle)


def test_validator_rejects_a_symlinked_bundle_root(tmp_path: pathlib.Path) -> None:
    """Reject a root alias before resolving it to an otherwise valid bundle."""
    bundle = _matrix_bundle(tmp_path)
    alias = tmp_path / "bundle-alias"
    alias.symlink_to(bundle, target_is_directory=True)

    with pytest.raises(validate.EvidenceValidationError, match="symlink"):
        validate.validate_bundle(alias)


def test_the_sdk_list_is_the_recorded_evidence_plus_the_pinned_sdk(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A new SDK is accepted once it is pinned, and an old one while evidence names it."""
    evidence = tmp_path / "evidence"
    (evidence / "0001").mkdir(parents=True)
    (evidence / "0001" / "environment.json").write_text('{"sdkVersion": "9.9.1"}', encoding="utf-8")
    (evidence / "0002").mkdir()
    (evidence / "0002" / "environment.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr(validate, "EVIDENCE_ROOT", evidence)
    validate.known_sdk_versions.cache_clear()
    try:
        known = validate.known_sdk_versions()
    finally:
        validate.known_sdk_versions.cache_clear()

    assert known == {"9.9.1", validate._pinned_sdk_version()}
