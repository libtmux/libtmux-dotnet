"""Tests for phased durable evidence validation."""

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
hash_tree = t.cast(t.Any, importlib.import_module("hash_tree"))
record_deletion = t.cast(t.Any, importlib.import_module("record_deletion"))
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
    "3.2a",
    "3.3a",
    "3.4",
    "3.5",
    "3.6",
    "3.7a",
    "3.7b",
    "3.7c",
]
REQUIRED_FRAMEWORKS = ["net10.0", "net8.0"]
REQUIRED_MATRIX_ROWS = len(REQUIRED_TMUX_VERSIONS) * len(REQUIRED_FRAMEWORKS)
TRANSITION_TMUX_SOURCE_COMMIT = "7" * 40
EVALUATED_COMMIT_TREE = "e" * 40
CAPABILITY_COHORT = "0001"
CLOSURE_COHORT = "closure"


def _seed_tmux_build_cache(root: pathlib.Path, version: str) -> str:
    """Create one source-bound build-version cache without network access."""
    source = root / "sources" / version
    install = root / "installs" / version
    binary = install / "bin" / "tmux"
    source.mkdir(parents=True)
    binary.parent.mkdir(parents=True)
    (source / "source.txt").write_text(version + "\n", encoding="utf-8")
    subprocess.run(["git", "init", "--quiet", str(source)], check=True)
    subprocess.run(["git", "-C", str(source), "add", "source.txt"], check=True)
    subprocess.run(
        [
            "git",
            "-C",
            str(source),
            "-c",
            "user.name=Matrix Test",
            "-c",
            "user.email=matrix@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "fixture",
        ],
        check=True,
    )
    subprocess.run(["git", "-C", str(source), "tag", version], check=True)
    commit = subprocess.run(
        ["git", "-C", str(source), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    binary.write_text(
        f"#!/usr/bin/env sh\nprintf '%s\\n' 'tmux {version}'\n",
        encoding="utf-8",
    )
    binary.chmod(0o755)
    digest = hashlib.sha256(binary.read_bytes()).hexdigest()
    (install / "source-commit").write_text(commit + "\n", encoding="utf-8")
    (install / "cache-metadata.json").write_text(
        json.dumps(
            {
                "binarySha256": digest,
                "binaryVersion": version,
                "schemaVersion": 1,
                "sourceCommit": commit,
                "sourceRef": version,
                "version": version,
            },
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return commit


def _fake_matrix_environment(
    tmp_path: pathlib.Path,
) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path, dict[str, str]]:
    """Create source-bound tmux caches and a recording fake toolchain."""
    repository = pathlib.Path(__file__).parents[3]
    artifact_root = tmp_path / "tmux-cache"
    for version in [*REQUIRED_TMUX_VERSIONS, "3.7"]:
        _seed_tmux_build_cache(artifact_root, version)
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    log = tmp_path / "matrix-environment.txt"
    mise = fake_bin / "mise"
    mise.write_text(
        "#!/usr/bin/env sh\n"
        'if [ "$4" = "--version" ]; then\n'
        "    printf '%s\\n' '10.0.302'\n"
        'elif [ "$4" = "test" ]; then\n'
        '    if [ -n "${LIBTMUX_PROTOCOL_TRANSCRIPT_DIR-}" ]; then\n'
        '        mkdir -p "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR"\n'
        "        printf '%s\\n' 'event=control-send sequence=1 bytes=31' "
        "'event=control-receive sequence=1 marker=%begin' "
        "'event=control-receive sequence=1 marker=%end' "
        '> "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/control.txt"\n'
        "        printf '%s\\n' 'event=pty-attach state=visible' "
        "'event=pty-detach state=gone' "
        '> "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/pty.txt"\n'
        '        case "$*" in\n'
        "            *--filter-method*)\n"
        '                if [ "${LIBTMUX_EXPECTED_TMUX_VERSION-}" = 3.7 ]; then\n'
        "                    workaround=applied\n"
        "                else\n"
        "                    workaround=omitted\n"
        "                fi\n"
        "                printf '%s\\n' \"event=break-pane-transition "
        "framework=${LIBTMUX_TEST_FRAMEWORK-} "
        "tmux-source-commit=${LIBTMUX_TMUX_SOURCE_COMMIT-} "
        "tmux-version=${LIBTMUX_EXPECTED_TMUX_VERSION-} "
        'workaround=$workaround outcome=passed" '
        '>> "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/break-pane-transition.txt"\n'
        "                ;;\n"
        "        esac\n"
        "    fi\n"
        "    printf '%s|%s|%s|%s\\n' \"${LIBTMUX_EXPECTED_TMUX_VERSION-}\" "
        '"${LIBTMUX_TRANSITION_TMUX_3_7-}" '
        '"${LIBTMUX_TRANSITION_TMUX_3_7_SOURCE_COMMIT-}" '
        '"$*" >> "$MATRIX_LOG"\n'
        "    printf '%s\\n' 'total: 1' 'skipped: 0'\n"
        "fi\n",
        encoding="utf-8",
    )
    mise.chmod(0o755)
    uv = fake_bin / "uv"
    uv.write_text(
        "#!/usr/bin/env sh\n"
        'case "$*" in\n'
        '    *--source-fingerprint*) printf \'%s\\n\' "$*" >> "$SOURCE_LOG"; '
        "printf '%064d\\n' 0 ;;\n"
        '    *--source-state*) printf \'%s\\n\' "$*" >> "$SOURCE_LOG"; '
        "printf '%s\\n' clean ;;\n"
        '    *) exec "$REAL_UV" "$@" ;;\n'
        "esac\n",
        encoding="utf-8",
    )
    uv.chmod(0o755)
    environment = os.environ.copy()
    environment.update(
        {
            "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(artifact_root),
            "LIBTMUX_TMUX_REPOSITORY": str(tmp_path / "missing.git"),
            "MATRIX_LOG": str(log),
            "PATH": f"{fake_bin}{os.pathsep}{environment['PATH']}",
            "REAL_UV": shutil.which("uv") or "uv",
            "SOURCE_LOG": str(tmp_path / "source-identity.txt"),
        }
    )
    return repository, artifact_root, log, environment


def test_matrix_runner_skips_transition_outside_component_three_cohort(
    tmp_path: pathlib.Path,
) -> None:
    """Do not build or run the 3.7 transition for ordinary component matrices."""
    repository, artifact_root, log, environment = _fake_matrix_environment(tmp_path)
    transition_install = artifact_root / "installs" / "3.7"
    transition_source = artifact_root / "sources" / "3.7"
    for path in [transition_install, transition_source]:
        shutil.rmtree(path)

    subprocess.run(
        [
            str(repository / "eng" / "tmux" / "run-matrix.sh"),
            "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        ],
        check=True,
        cwd=repository,
        env=environment,
    )

    observations = [
        line.split("|", maxsplit=3) for line in log.read_text().splitlines()
    ]
    assert len(observations) == REQUIRED_MATRIX_ROWS
    assert {row[1] for row in observations} == {""}
    assert {row[2] for row in observations} == {""}
    assert not transition_install.exists()


def test_matrix_runner_runs_exact_source_bound_tmux_3_7_transition(
    tmp_path: pathlib.Path,
) -> None:
    """Run exactly four filtered transition tests for the Component 3 cohort."""
    repository, artifact_root, log, environment = _fake_matrix_environment(tmp_path)
    evidence = tmp_path / "0001"

    subprocess.run(
        [
            str(repository / "eng" / "tmux" / "run-matrix.sh"),
            "--evidence-dir",
            str(evidence),
            "--capability-cohort",
            CAPABILITY_COHORT,
            "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        ],
        check=True,
        cwd=repository,
        env=environment,
    )

    observations = [
        line.split("|", maxsplit=3) for line in log.read_text().splitlines()
    ]
    ordinary = [row for row in observations if "--filter-method" not in row[3]]
    transition = [row for row in observations if "--filter-method" in row[3]]
    assert len(ordinary) == REQUIRED_MATRIX_ROWS
    assert {row[0] for row in ordinary} == set(REQUIRED_TMUX_VERSIONS)
    assert len(transition) == 4
    assert {row[0] for row in transition} == {"3.7", "3.7a"}
    assert {row[1] for row in ordinary} == {
        str(artifact_root / "installs" / "3.7" / "bin" / "tmux")
    }
    assert len({row[2] for row in ordinary}) == 1
    assert all(
        "LibTmux.IntegrationTests.Versioning."
        "VersionParityTests.BreakPane37Workaround" in row[3]
        for row in transition
    )
    transcript = evidence / "protocol-transcripts" / "break-pane-transition.txt"
    lines = transcript.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 4
    assert all(validate.BREAK_PANE_TRANSCRIPT_PATTERN.fullmatch(line) for line in lines)
    assert (
        len(list((evidence / "results.ndjson").read_text().splitlines()))
        == REQUIRED_MATRIX_ROWS
    )
    environment_observation = json.loads(
        (evidence / "environment.json").read_text(encoding="utf-8")
    )
    assert environment_observation["capabilityCohort"] == CAPABILITY_COHORT
    assert environment_observation["includeMasterAdvisory"] is False
    source_commands = (tmp_path / "source-identity.txt").read_text().splitlines()
    assert len(source_commands) == 2
    assert all(f"--exclude-root {evidence}" in command for command in source_commands)


def test_matrix_runner_does_not_infer_cohort_from_evidence_basename(
    tmp_path: pathlib.Path,
) -> None:
    """Treat an unrelated directory named 0001 as ordinary matrix evidence."""
    repository, _artifact_root, log, environment = _fake_matrix_environment(tmp_path)
    evidence = tmp_path / "0001"

    subprocess.run(
        [
            str(repository / "eng" / "tmux" / "run-matrix.sh"),
            "--evidence-dir",
            str(evidence),
            "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        ],
        check=True,
        cwd=repository,
        env=environment,
    )

    observations = [
        line.split("|", maxsplit=3) for line in log.read_text().splitlines()
    ]
    assert len(observations) == REQUIRED_MATRIX_ROWS
    assert all("--filter-method" not in row[3] for row in observations)
    recorded_environment = json.loads(
        (evidence / "environment.json").read_text(encoding="utf-8")
    )
    assert "capabilityCohort" not in recorded_environment
    assert "transitionTmuxSourceCommits" not in recorded_environment


def test_matrix_runner_records_wrapper_policy_closure_without_transition(
    tmp_path: pathlib.Path,
) -> None:
    """Retain the explicit closure cohort without replaying raw transition proof."""
    repository, _artifact_root, log, environment = _fake_matrix_environment(tmp_path)
    evidence = tmp_path / "closure"

    subprocess.run(
        [
            str(repository / "eng" / "tmux" / "run-matrix.sh"),
            "--evidence-dir",
            str(evidence),
            "--capability-cohort",
            CLOSURE_COHORT,
            "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        ],
        check=True,
        cwd=repository,
        env=environment,
    )

    observations = [
        line.split("|", maxsplit=3) for line in log.read_text().splitlines()
    ]
    assert len(observations) == REQUIRED_MATRIX_ROWS
    assert all("--filter-method" not in row[3] for row in observations)
    recorded_environment = json.loads(
        (evidence / "environment.json").read_text(encoding="utf-8")
    )
    assert recorded_environment["capabilityCohort"] == CLOSURE_COHORT
    assert recorded_environment["includeMasterAdvisory"] is False
    assert "transitionTmuxSourceCommits" not in recorded_environment
    assert not (
        evidence / "protocol-transcripts" / "break-pane-transition.txt"
    ).exists()
    assert (
        len((evidence / "results.ndjson").read_text().splitlines())
        == REQUIRED_MATRIX_ROWS
    )
    source_commands = (tmp_path / "source-identity.txt").read_text().splitlines()
    assert len(source_commands) == 2
    assert all(f"--exclude-root {evidence}" in command for command in source_commands)


@pytest.mark.parametrize(
    "arguments",
    [
        ["--capability-cohort", CAPABILITY_COHORT],
        ["--evidence-dir", "evidence", "--capability-cohort", "unknown"],
        [
            "--include-master-advisory",
            "--evidence-dir",
            "evidence",
            "--capability-cohort",
            CAPABILITY_COHORT,
        ],
        [
            "--include-master-advisory",
            "--evidence-dir",
            "evidence",
            "--capability-cohort",
            CLOSURE_COHORT,
        ],
    ],
)
def test_matrix_runner_rejects_invalid_capability_cohort_combinations(
    tmp_path: pathlib.Path,
    arguments: list[str],
) -> None:
    """Require a known retained cohort with evidence and without master."""
    repository, _artifact_root, _log, environment = _fake_matrix_environment(tmp_path)

    completed = subprocess.run(
        [
            str(repository / "eng" / "tmux" / "run-matrix.sh"),
            *arguments,
            "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        ],
        check=False,
        cwd=repository,
        env=environment,
        capture_output=True,
        text=True,
    )

    assert completed.returncode == 2


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
            "tmuxSourceCommit": (
                TRANSITION_TMUX_SOURCE_COMMIT if version == "3.7" else "e" * 40
            ),
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

    validate.validate_bundle(bundle, phase="matrix")


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
    rows = [
        row
        for row in _matrix_rows(COMMIT)
        if row["tmuxVersion"] != REQUIRED_TMUX_VERSIONS[-1]
    ]
    _write_rows(bundle, rows)

    validate.validate_bundle(bundle, phase="matrix")


@pytest.mark.parametrize(
    "mutation",
    ["missing-marker", "missing-transition", "unknown-marker"],
)
def test_matrix_phase_requires_exact_component_three_cohort_contract(
    tmp_path: pathlib.Path,
    mutation: str,
) -> None:
    """Bind transition provenance to the explicit Component 3 cohort marker."""
    bundle = _matrix_bundle(tmp_path)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
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
        validate.validate_bundle(bundle, phase="matrix")


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

    validate.validate_bundle(bundle, phase="matrix")
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
        validate.validate_bundle(bundle, phase="matrix")


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
        validate.validate_bundle(bundle, phase="matrix")


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
            "tmuxSourceCommit": (
                TRANSITION_TMUX_SOURCE_COMMIT if version == "3.7" else "e" * 40
            ),
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
        validate.validate_bundle(bundle, phase="matrix")


def _write_transport_semantic_transcripts(bundle: pathlib.Path) -> None:
    transcripts = bundle / "protocol-transcripts"
    contents = {
        "client-observability.txt": [
            "event=client-attachment phase=before primary=attached selected=unattached",
            "event=client-attachment phase=during primary=attached selected=attached",
            "event=client-observability phase=during control-client=visible",
            "event=client-attachment phase=after primary=attached selected=unattached",
            "event=client-hook kind=attached count=1",
            "event=client-hook kind=detached count=1",
        ],
        "control-cancellation.txt": [
            (
                "event=control-cancellation phase=after-write result=typed "
                "command-may-have-executed=true"
            ),
            "event=control-tombstone phase=before-drain state=blocking",
            "event=control-following-request phase=before-drain state=blocked",
            "event=control-tombstone phase=after-drain state=drained",
            "event=control-following-request phase=after-drain state=completed",
        ],
        "semicolon-middle-failure.txt": [
            (
                "event=semicolon-member position=prefix outcome=completed "
                "side-effect=present"
            ),
            "event=semicolon-member position=middle outcome=failed side-effect=none",
            "event=semicolon-member position=suffix outcome=skipped side-effect=absent",
        ],
    }
    for filename, events in contents.items():
        lines = [
            f"{event} framework={framework} tmux-version={version}"
            for version in REQUIRED_TMUX_VERSIONS
            for framework in REQUIRED_FRAMEWORKS
            for event in events
        ]
        (transcripts / filename).write_text("\n".join(lines) + "\n", encoding="utf-8")


def _repository_bundle(
    tmp_path: pathlib.Path,
    *,
    solution_text: str = "<Solution />\n",
) -> tuple[pathlib.Path, pathlib.Path, str]:
    repository = tmp_path / "repository"
    repository.mkdir()
    record_deletion.run_git(repository, "init", "--quiet")
    record_deletion.run_git(repository, "config", "user.name", "Evidence Test")
    record_deletion.run_git(
        repository, "config", "user.email", "evidence@example.invalid"
    )
    solution = repository / "csharp" / "LibTmux.slnx"
    solution.parent.mkdir()
    solution.write_text(solution_text, encoding="utf-8")
    record_deletion.run_git(repository, "add", "csharp/LibTmux.slnx")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "fixture")
    commit = record_deletion.run_git(repository, "rev-parse", "HEAD")
    bundle = _matrix_bundle(
        repository / "csharp" / "artifacts" / "evidence",
        commit,
        bundle_name="0001",
    )
    return repository, bundle, commit


def _write_critic_reviews(bundle: pathlib.Path, commit: str) -> dict[str, t.Any]:
    reviews = {
        "schemaVersion": 1,
        "evaluatedCommit": commit,
        "reviews": [
            {
                "critic": critic,
                "findings": [
                    {
                        "disposition": "no-findings",
                        "evidence": "reviewed required corpus",
                        "finding": "no findings",
                        "resolution": "not-applicable",
                        "severity": "none",
                    }
                ],
            }
            for critic in [
                "framework-design-guidelines",
                "python-parity",
                "tmux-protocol",
            ]
        ],
    }
    (bundle / "critic-reviews.md").write_text(
        "# Critic reviews\n\n```json\n"
        + json.dumps(reviews, indent=2, sort_keys=True)
        + "\n```\n",
        encoding="utf-8",
    )
    return reviews


def _write_decision(
    repository: pathlib.Path,
    commit: str,
    decision_id: str = "0001",
) -> tuple[pathlib.Path, dict[str, t.Any]]:
    decision = {
        "capabilities": ["bounded process execution"],
        "commands": ["mise exec -- dotnet test"],
        "criticDispositions": f"evidence/{decision_id}/critic-reviews.md",
        "decisionId": decision_id,
        "decisionInputs": {
            "corpus": "hostile-streams",
            "interface": "transport",
            "source": "oracle",
        },
        "evaluatedCommit": commit,
        "evidenceFiles": [f"evidence/{decision_id}/results.ndjson"],
        "grafts": ["raw-byte result shape"],
        "hardGates": [
            {
                "evidence": f"evidence/{decision_id}/results.ndjson",
                "name": "required matrix",
                "status": "passed",
            }
        ],
        "rejectedRisks": ["unbounded stream reads"],
        "remainingUnknowns": [],
        "schemaVersion": 1,
        "winner": "system-process transport",
    }
    path = repository / "csharp" / "docs" / "decisions" / f"{decision_id}-transport.md"
    path.parent.mkdir(parents=True)
    path.write_text(
        "# Transport decision\n\n```json\n"
        + json.dumps(decision, indent=2, sort_keys=True)
        + "\n```\n",
        encoding="utf-8",
    )
    return path, decision


def _write_pre_deletion(
    repository: pathlib.Path, bundle: pathlib.Path, commit: str
) -> None:
    if bundle.name == "0001":
        _write_transport_semantic_transcripts(bundle)
    _write_critic_reviews(bundle, commit)
    _write_decision(repository, commit, bundle.name)


def test_matrix_phase_requires_all_release_framework_rows(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a required matrix with one missing framework row."""
    bundle = _matrix_bundle(tmp_path)
    _write_rows(bundle, _matrix_rows(COMMIT)[:-1])

    with pytest.raises(validate.EvidenceValidationError, match="required matrix"):
        validate.validate_bundle(bundle, phase="matrix")


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
    (bundle / "environment.json").write_text(
        json.dumps(environment) + "\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="environment"):
        validate.validate_bundle(bundle, phase="matrix")


def test_matrix_phase_requires_include_master_environment_key(
    tmp_path: pathlib.Path,
) -> None:
    """Reject an environment that omits the exact advisory declaration."""
    bundle = _matrix_bundle(tmp_path)
    environment = json.loads((bundle / "environment.json").read_text(encoding="utf-8"))
    environment.pop("includeMasterAdvisory")
    (bundle / "environment.json").write_text(
        json.dumps(environment) + "\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="environment"):
        validate.validate_bundle(bundle, phase="matrix")


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
        validate.validate_bundle(bundle, phase="matrix")


def test_matrix_phase_rejects_duplicate_and_unknown_rows(
    tmp_path: pathlib.Path,
) -> None:
    """Reject duplicate pairs and versions outside the required/advisory set."""
    bundle = _matrix_bundle(tmp_path)
    rows = _matrix_rows(COMMIT)
    rows.append(dict(rows[0]))
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="duplicate"):
        validate.validate_bundle(bundle, phase="matrix")

    rows[-1]["tmuxVersion"] = "3.8"
    _write_rows(bundle, rows)
    with pytest.raises(validate.EvidenceValidationError, match="unknown"):
        validate.validate_bundle(bundle, phase="matrix")


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
        validate.validate_bundle(bundle, phase="matrix")

    bundle = _matrix_bundle(
        tmp_path,
        bundle_name="requested",
        include_master_advisory=True,
        capability_cohort=False,
    )
    _write_rows(bundle, _matrix_rows(COMMIT))
    with pytest.raises(validate.EvidenceValidationError, match="master"):
        validate.validate_bundle(bundle, phase="matrix")


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

    validate.validate_bundle(bundle, phase="matrix")


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

    validate.validate_bundle(bundle, phase="matrix")


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
        validate.validate_bundle(bundle, phase="matrix")


def test_matrix_phase_requires_per_version_source_consistency(
    tmp_path: pathlib.Path,
) -> None:
    """Bind both framework observations to one tmux source revision."""
    bundle = _matrix_bundle(tmp_path)
    rows = _matrix_rows(COMMIT)
    rows[1]["tmuxSourceCommit"] = "f" * 40
    _write_rows(bundle, rows)

    with pytest.raises(validate.EvidenceValidationError, match="source commit"):
        validate.validate_bundle(bundle, phase="matrix")


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
    (bundle / "redaction-proof.json").write_text(
        json.dumps(proof) + "\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="redaction proof"):
        validate.validate_bundle(bundle, phase="matrix")


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
    (bundle / "leak.txt").write_text(
        f"prefix={absolute_path};suffix=safe\n", encoding="utf-8"
    )

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
        validate.validate_bundle(alias, phase="matrix")


def test_pre_deletion_requires_all_structured_critics(tmp_path: pathlib.Path) -> None:
    """Require exactly three complete critic dispositions at the matrix commit."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    reviews = _write_critic_reviews(bundle, commit)
    _write_decision(repository, commit)
    reviews["reviews"].pop()
    (bundle / "critic-reviews.md").write_text(
        "```json\n" + json.dumps(reviews) + "\n```\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="critic"):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


def test_transport_pre_deletion_requires_semantic_transcripts(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a transport decision without scenario-level semantic evidence."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    (bundle / "protocol-transcripts" / "control-cancellation.txt").unlink()

    with pytest.raises(validate.EvidenceValidationError, match="semantic transcript"):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


def test_transport_pre_deletion_rejects_unattributed_semantic_events(
    tmp_path: pathlib.Path,
) -> None:
    """Reject semantic evidence that cannot be assigned to matrix lanes."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    transcript_root = bundle / "protocol-transcripts"
    for path in transcript_root.glob("*.txt"):
        if path.name in {
            "break-pane-transition.txt",
            "control.txt",
            "control-contender.txt",
            "pty.txt",
        }:
            continue
        lines = [
            line.split(" framework=", maxsplit=1)[0]
            for line in path.read_text(encoding="utf-8").splitlines()
        ]
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    with pytest.raises(
        validate.EvidenceValidationError, match="semantic transcript lane"
    ):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


def test_transport_pre_deletion_rejects_wrong_semantic_fields(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a scenario transcript that does not prove the asserted outcome."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    path = bundle / "protocol-transcripts" / "control-cancellation.txt"
    content = path.read_text(encoding="utf-8")
    path.write_text(
        content.replace("result=typed", "result=success", 1), encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="semantic transcript"):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


@pytest.mark.parametrize("mutation", ["missing", "duplicate", "advisory"])
def test_transport_pre_deletion_requires_one_semantic_set_per_lane(
    tmp_path: pathlib.Path,
    mutation: str,
) -> None:
    """Reject incomplete, repeated, or advisory semantic lane evidence."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    path = bundle / "protocol-transcripts" / "semicolon-middle-failure.txt"
    lines = path.read_text(encoding="utf-8").splitlines()
    if mutation == "missing":
        lines.pop()
    elif mutation == "duplicate":
        lines.append(lines[0])
    else:
        lines.append(
            "event=semicolon-member position=prefix outcome=completed "
            "side-effect=present framework=net10.0 tmux-version=next-3.8"
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    with pytest.raises(
        validate.EvidenceValidationError, match="semantic transcript lane"
    ):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


@pytest.mark.parametrize("decision_id", ["0002", "0003"])
def test_later_decisions_do_not_require_transport_semantic_transcripts(
    tmp_path: pathlib.Path,
    decision_id: str,
) -> None:
    """Keep the transport-only transcript contract scoped to decision 0001."""
    repository, original, commit = _repository_bundle(tmp_path)
    bundle = original.with_name(decision_id)
    original.rename(bundle)
    environment_path = bundle / "environment.json"
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment.pop("capabilityCohort")
    environment.pop("evaluatedCommitTree")
    environment.pop("transitionTmuxSourceCommits")
    environment_path.write_text(json.dumps(environment) + "\n", encoding="utf-8")
    (bundle / "protocol-transcripts" / "break-pane-transition.txt").unlink()
    _write_pre_deletion(repository, bundle, commit)

    validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


@pytest.mark.parametrize(
    ("disposition", "resolution"),
    [("pending", "resolved"), ("accepted", "not-applicable")],
)
def test_pre_deletion_rejects_unresolved_critic_findings(
    tmp_path: pathlib.Path,
    disposition: str,
    resolution: str,
) -> None:
    """Reject pending or accepted-but-unresolved critic findings."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    reviews = _write_critic_reviews(bundle, commit)
    _write_decision(repository, commit)
    reviews["reviews"][0]["findings"] = [
        {
            "disposition": disposition,
            "evidence": "specific evidence",
            "finding": "bounded cleanup issue",
            "resolution": resolution,
            "severity": "high",
        }
    ]
    (bundle / "critic-reviews.md").write_text(
        "```json\n" + json.dumps(reviews) + "\n```\n", encoding="utf-8"
    )

    with pytest.raises(validate.EvidenceValidationError, match="critic"):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


@pytest.mark.parametrize(
    "mutation",
    ["placeholder", "failed-gate", "wrong-id", "duplicate", "absolute", "extra"],
)
def test_pre_deletion_rejects_malformed_decision_inputs(
    tmp_path: pathlib.Path,
    mutation: str,
) -> None:
    """Reject placeholder, unsafe, incomplete, or unknown decision content."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_critic_reviews(bundle, commit)
    path, decision = _write_decision(repository, commit)
    if mutation == "placeholder":
        decision["winner"] = "TBD"
    elif mutation == "failed-gate":
        decision["hardGates"][0]["status"] = "failed"
    elif mutation == "wrong-id":
        decision["decisionId"] = "0002"
    elif mutation == "duplicate":
        decision["commands"].append(decision["commands"][0])
    elif mutation == "absolute":
        decision["evidenceFiles"] = ["/private/evidence/results.ndjson"]
    else:
        decision["extra"] = True
    path.write_text("```json\n" + json.dumps(decision) + "\n```\n", encoding="utf-8")

    with pytest.raises(validate.EvidenceValidationError, match="decision"):
        validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


def test_pre_deletion_accepts_complete_structured_inputs(
    tmp_path: pathlib.Path,
) -> None:
    """Accept complete critic and decision blocks bound to the matrix commit."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)

    validate.validate_bundle(bundle, phase="pre-deletion", repository=repository)


def test_final_phase_rechecks_deletion_claims(tmp_path: pathlib.Path) -> None:
    """Reject a deletion proof whose claimed absent directory has returned."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[pathlib.Path("csharp/spikes/Removed")],
        absent_globs=["csharp/spikes/Rejected.*"],
        tracked_prefixes=[pathlib.Path("csharp/spikes/Removed")],
        project_tokens=["Removed"],
        project_count=0,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    (repository / "csharp" / "spikes" / "Removed").mkdir(parents=True)
    hash_tree.write_hashes(bundle)

    with pytest.raises(validate.EvidenceValidationError, match="deletion"):
        validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_rejects_unknown_deletion_keys(tmp_path: pathlib.Path) -> None:
    """Reject deletion proof fields that the validator cannot re-evaluate."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[],
        absent_globs=[],
        tracked_prefixes=[],
        project_tokens=[],
        project_count=0,
    )
    proof["unverified"] = True
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)

    with pytest.raises(validate.EvidenceValidationError, match="deletion"):
        validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_accepts_rechecked_complete_proof(tmp_path: pathlib.Path) -> None:
    """Accept exact deletion and hash proof after live repository re-evaluation."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[pathlib.Path("csharp/spikes/Removed")],
        absent_globs=["csharp/spikes/Rejected.*"],
        tracked_prefixes=[pathlib.Path("csharp/spikes/Removed")],
        project_tokens=["Removed"],
        project_count=0,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)

    validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_allows_unrelated_commit_after_evidence(
    tmp_path: pathlib.Path,
) -> None:
    """Allow a later commit that does not resurrect any rejected content."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[pathlib.Path("csharp/spikes/Removed")],
        absent_globs=["csharp/spikes/Rejected.*"],
        tracked_prefixes=[pathlib.Path("csharp/spikes/Removed")],
        project_tokens=["Removed"],
        project_count=0,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)
    (repository / "unrelated.txt").write_text("later\n", encoding="utf-8")
    record_deletion.run_git(repository, "add", "unrelated.txt")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "unrelated")

    validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_allows_later_solution_project_addition(
    tmp_path: pathlib.Path,
) -> None:
    """Allow a later solution project while preserving the proof snapshot."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[],
        absent_globs=[],
        tracked_prefixes=[],
        project_tokens=["Rejected"],
        project_count=0,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)
    (repository / "csharp" / "LibTmux.slnx").write_text(
        '<Solution><Project Path="Later.csproj" /></Solution>\n',
        encoding="utf-8",
    )
    record_deletion.run_git(repository, "add", "csharp/LibTmux.slnx")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "add project")

    validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_allows_later_solution_project_removal(
    tmp_path: pathlib.Path,
) -> None:
    """Allow removal of a project retained in the proof snapshot."""
    repository, bundle, commit = _repository_bundle(
        tmp_path,
        solution_text='<Solution><Project Path="Kept.csproj" /></Solution>\n',
    )
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[],
        absent_globs=[],
        tracked_prefixes=[],
        project_tokens=[],
        project_count=1,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)
    (repository / "csharp" / "LibTmux.slnx").write_text(
        "<Solution />\n", encoding="utf-8"
    )
    record_deletion.run_git(repository, "add", "csharp/LibTmux.slnx")
    record_deletion.run_git(repository, "commit", "--quiet", "-m", "remove project")

    validate.validate_bundle(bundle, phase="final", repository=repository)


def test_final_phase_rejects_divergent_repository_head(
    tmp_path: pathlib.Path,
) -> None:
    """Reject evidence when the checkout no longer descends from its matrix commit."""
    repository, bundle, commit = _repository_bundle(tmp_path)
    _write_pre_deletion(repository, bundle, commit)
    proof = record_deletion.build_proof(
        repository=repository,
        solution=pathlib.Path("csharp/LibTmux.slnx"),
        absent=[],
        absent_globs=[],
        tracked_prefixes=[],
        project_tokens=[],
        project_count=0,
    )
    record_deletion.write_proof(bundle / "deletion.json", proof)
    hash_tree.write_hashes(bundle)
    tree = record_deletion.run_git(repository, "write-tree")
    divergent = record_deletion.run_git(
        repository, "commit-tree", tree, "-m", "divergent"
    )
    record_deletion.run_git(repository, "reset", "--quiet", "--hard", divergent)

    with pytest.raises(validate.EvidenceValidationError, match="ancestry"):
        validate.validate_bundle(bundle, phase="final", repository=repository)
