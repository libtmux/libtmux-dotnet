"""Tests for evidence-backed tmux-version reconciliation."""

from __future__ import annotations

import copy
import json
import pathlib
import runpy
import subprocess
import typing as t

import pytest

from eng.evidence.assemble_bundle import source_state, source_tree_fingerprint

VERSION_PARITY_TEST = (
    "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs::"
)
TRANSITION_TMUX_SOURCE_COMMIT = "a" * 40
EVALUATED_COMMIT_TREE = "e" * 40
CAPABILITY_COHORT = "0001"
CLOSURE_COHORT = "closure"
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


def load_reconciler() -> dict[str, t.Any]:
    """Load the reconciliation script as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "reconcile_versions.py")
    )


def load_generator() -> dict[str, t.Any]:
    """Load the parity generator as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "generate_inventory.py")
    )


def seed_document() -> dict[str, t.Any]:
    """Return an isolated copy of the checked-in seed document."""
    path = pathlib.Path(__file__).parents[3] / "docs" / "parity" / "version-deltas.json"
    return t.cast(
        dict[str, t.Any],
        json.loads(path.read_text(encoding="utf-8")),
    )


def matrix_rows(commit: str = "c" * 40) -> list[dict[str, t.Any]]:
    """Build the complete required matrix used by reconciliation tests."""
    return [
        {
            "advisory": False,
            "evaluatedCommit": commit,
            "framework": framework,
            "status": "passed",
            "testCount": 139,
            "tmuxSourceCommit": str(index) * 40,
            "tmuxVersion": version,
        }
        for index, version in enumerate(REQUIRED_TMUX_VERSIONS, start=1)
        for framework in ["net10.0", "net8.0"]
    ]


def write_fixture(
    root: pathlib.Path,
    rows: list[dict[str, t.Any]],
) -> tuple[pathlib.Path, dict[str, tuple[str, ...]], str]:
    """Write matrix evidence and one checked capability test source."""
    test_path = root / "spikes" / "EvidenceTests.cs"
    test_path.parent.mkdir(parents=True)
    test_path.write_text(
        "public sealed class EvidenceTests {\n"
        "    [Fact]\n"
        "    public void Capability_is_exercised() { }\n"
        "    [Fact]\n"
        "    public void BreakPane37Workaround() { }\n"
        "}\n",
        encoding="utf-8",
    )
    subprocess.run(["git", "init", "--quiet", str(root)], check=True)
    subprocess.run(
        ["git", "-C", str(root), "config", "user.name", "Evidence Test"],
        check=True,
    )
    subprocess.run(
        [
            "git",
            "-C",
            str(root),
            "config",
            "user.email",
            "evidence@example.invalid",
        ],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(root), "add", "spikes/EvidenceTests.cs"],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(root), "commit", "--quiet", "-m", "fixture"],
        check=True,
    )
    commit = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    for row in rows:
        row["evaluatedCommit"] = commit
    evidence = root / "docs" / "decisions" / "evidence" / "0001"
    evidence.mkdir(parents=True)
    results = evidence / "results.ndjson"
    results.write_text(
        "".join(json.dumps(row, sort_keys=True) + "\n" for row in rows),
        encoding="utf-8",
    )
    write_environment(results, root)
    test_id = "spikes/EvidenceTests.cs::Capability_is_exercised"
    mapping: dict[str, tuple[str, ...]] = {
        row["capability"]: (test_id,)
        for row in seed_document()["capabilities"]
        if row["capability"]
        in {
            "attachment_accounting",
            "byte_length_framing",
            "control_notifications",
            "format_fields_and_operators",
            "missing_target_format_safety",
            "option_dollar_double_escape",
            "semicolon_grouping",
        }
    }
    return results, mapping, commit


def write_environment(
    results: pathlib.Path,
    repository: pathlib.Path,
    *,
    evaluated_commit: str | None = None,
    fingerprint: str | None = None,
    state: str | None = None,
) -> None:
    """Bind matrix evidence to the current source tree outside its output root."""
    commit = (
        evaluated_commit
        or subprocess.run(
            ["git", "-C", str(repository), "rev-parse", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
    )
    environment = {
        "capabilityCohort": CAPABILITY_COHORT,
        "evaluatedCommit": commit,
        "evaluatedCommitTree": EVALUATED_COMMIT_TREE,
        "frameworks": ["net10.0", "net8.0"],
        "includeMasterAdvisory": False,
        "platform": "linux",
        "redactionProof": True,
        "schemaVersion": 1,
        "sdkVersion": "10.0.302",
        "sourceState": state
        or source_state(repository, excluded_roots=[results.parent]),
        "sourceTreeFingerprint": fingerprint
        or source_tree_fingerprint(repository, excluded_roots=[results.parent]),
        "transitionTmuxSourceCommits": {
            "3.7": TRANSITION_TMUX_SOURCE_COMMIT,
        },
        "tmuxVersions": REQUIRED_TMUX_VERSIONS,
    }
    results.with_name("environment.json").write_text(
        json.dumps(environment, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    rows = [
        json.loads(line) for line in results.read_text(encoding="utf-8").splitlines()
    ]
    release_commit = next(
        row["tmuxSourceCommit"] for row in rows if row["tmuxVersion"] == "3.7a"
    )
    transcript = results.parent / "protocol-transcripts" / "break-pane-transition.txt"
    transcript.parent.mkdir(parents=True, exist_ok=True)
    transcript.write_text(
        "\n".join(
            "event=break-pane-transition "
            f"framework={framework} "
            f"tmux-source-commit={source_commit} "
            f"tmux-version={version} "
            f"workaround={'applied' if version == '3.7' else 'omitted'} "
            "outcome=passed"
            for framework in ["net10.0", "net8.0"]
            for version, source_commit in [
                ("3.7", TRANSITION_TMUX_SOURCE_COMMIT),
                ("3.7a", release_commit),
            ]
        )
        + "\n",
        encoding="utf-8",
    )


def write_closure_environment(
    results: pathlib.Path,
    repository: pathlib.Path,
) -> None:
    """Write the source-bound final cohort without transition-only metadata."""
    write_environment(results, repository)
    environment_path = results.with_name("environment.json")
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment["capabilityCohort"] = CLOSURE_COHORT
    environment.pop("transitionTmuxSourceCommits")
    environment_path.write_text(
        json.dumps(environment, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    transcript = results.parent / "protocol-transcripts" / "break-pane-transition.txt"
    transcript.unlink()


def write_policy_test_source(
    root: pathlib.Path,
    mapping: dict[str, tuple[str, ...]],
) -> str:
    """Commit the exact future wrapper-policy test identities in one fixture."""
    methods_by_path: dict[str, set[str]] = {}
    for tests in mapping.values():
        for test in tests:
            path, method = test.split("::", maxsplit=1)
            methods_by_path.setdefault(path, set()).add(method)
    for path, methods in methods_by_path.items():
        test_path = root / path
        test_path.parent.mkdir(parents=True, exist_ok=True)
        test_path.write_text(
            "public sealed class PolicyTests {\n"
            + "".join(
                f"    [Fact]\n    public void {method}() {{ }}\n"
                for method in sorted(methods)
            )
            + "}\n",
            encoding="utf-8",
        )
    subprocess.run(
        ["git", "-C", str(root), "add", "tests"],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(root), "commit", "--quiet", "-m", "policy tests"],
        check=True,
    )
    return subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def write_rows(path: pathlib.Path, rows: list[dict[str, t.Any]]) -> None:
    """Replace one NDJSON matrix fixture.

    Examples
    --------
    >>> callable(write_rows)
    True
    """
    path.write_text(
        "".join(json.dumps(row, sort_keys=True) + "\n" for row in rows),
        encoding="utf-8",
    )


def test_cohort_maps_only_protocol_observations_to_frozen_production_tests(
    tmp_path: pathlib.Path,
) -> None:
    """Keep wrapper-policy rows outside the Component 3 evidence cohort."""
    namespace = load_reconciler()
    expected_methods = {
        "attachment_accounting": "AttachmentAccounting",
        "break_pane_3_7_workaround": "BreakPane37Workaround",
        "byte_length_framing": "ByteLengthFraming",
        "capture_pane_3_7_metadata": "CapturePane37Metadata",
        "capture_pane_mode_screen": "CapturePaneModeScreen",
        "choose_tree_sort_time": "ChooseTreeSortTime",
        "capture_pane_trim_trailing": "CapturePaneTrimTrailing",
        "clear_history_hyperlinks": "ClearHistoryHyperlinks",
        "clear_prompt_history_command": "ClearPromptHistoryCommand",
        "command_prompt_3_7_behavior": "CommandPrompt37Behavior",
        "command_prompt_background": "CommandPromptBackground",
        "command_prompt_literal": "CommandPromptLiteral",
        "confirm_before_acceptance": "ConfirmBeforeAcceptance",
        "confirm_before_background": "ConfirmBeforeBackground",
        "control_notifications": "ControlNotifications",
        "copy_mode_page_down": "CopyModePageDown",
        "display_menu_mouse": "DisplayMenuMouse",
        "display_menu_styles": "DisplayMenuStyles",
        "display_message_client": "DisplayMessageClient",
        "display_message_literal": "DisplayMessageLiteral",
        "display_message_update_pane": "DisplayMessageUpdatePane",
        "display_popup_3_3_options": "DisplayPopup33Options",
        "display_popup_3_6_key_policy": "DisplayPopup36KeyPolicy",
        "format_fields_and_operators": "FormatFieldsAndOperators",
        "missing_target_format_safety": "MissingTargetFormatSafety",
        "hook_scope_pane_window_set": "HookScopePaneWindowSet",
        "hook_scope_pane_window_show": "HookScopePaneWindowShow",
        "kill_session_group": "KillSessionGroup",
        "list_keys_format": "ListKeysFormat",
        "new_pane_command": "NewPaneCommand",
        "option_dollar_double_escape": "OptionDollarDoubleEscape",
        "paste_buffer_no_vis": "PasteBufferNoVis",
        "refresh_client_clipboard_query": "RefreshClientClipboardQuery",
        "run_shell_arguments": "RunShellArguments",
        "run_shell_show_stderr": "RunShellShowStderr",
        "run_shell_working_directory": "RunShellWorkingDirectory",
        "semicolon_grouping": "SemicolonGrouping",
        "send_keys_client_keys": "SendKeysClientKeys",
        "server_access_command": "ServerAccessCommand",
        "show_prompt_history_command": "ShowPromptHistoryCommand",
        "split_window_appearance": "SplitWindowAppearance",
        "split_window_empty": "SplitWindowEmpty",
    }
    evidence_path = tmp_path / "results.ndjson"
    (tmp_path / "environment.json").write_text(
        json.dumps(
            {
                "capabilityCohort": CAPABILITY_COHORT,
                "evaluatedCommit": "c" * 40,
                "evaluatedCommitTree": EVALUATED_COMMIT_TREE,
                "frameworks": ["net10.0", "net8.0"],
                "includeMasterAdvisory": False,
                "platform": "linux",
                "redactionProof": True,
                "schemaVersion": 1,
                "sdkVersion": "10.0.302",
                "sourceState": "clean",
                "sourceTreeFingerprint": "d" * 64,
                "transitionTmuxSourceCommits": {
                    "3.7": TRANSITION_TMUX_SOURCE_COMMIT,
                },
                "tmuxVersions": [
                    "3.2a",
                    "3.3a",
                    "3.4",
                    "3.5",
                    "3.6",
                    "3.7a",
                    "3.7b",
                ],
            }
        )
        + "\n",
        encoding="utf-8",
    )

    mapping = namespace["capability_tests_for_evidence"](evidence_path)

    protocol_methods = {
        capability: expected_methods[capability]
        for capability in (
            "attachment_accounting",
            "byte_length_framing",
            "control_notifications",
            "format_fields_and_operators",
            "missing_target_format_safety",
            "option_dollar_double_escape",
            "semicolon_grouping",
        )
    }
    assert mapping == {
        capability: (VERSION_PARITY_TEST + method,)
        for capability, method in protocol_methods.items()
    }
    assert {
        row["capability"]: row["namedRealServerTest"]
        for row in seed_document()["capabilities"]
    } == {
        capability: VERSION_PARITY_TEST + method
        for capability, method in expected_methods.items()
    }


def test_uncommitted_evidence_uses_fingerprinted_worktree_test(
    tmp_path: pathlib.Path,
) -> None:
    """Verify a mapped production test from the bound uncommitted source tree."""
    namespace = load_reconciler()
    results, _mapping, commit = write_fixture(tmp_path, matrix_rows())
    test_path = (
        tmp_path
        / "tests"
        / "LibTmux.IntegrationTests"
        / "Versioning"
        / "VersionParityTests.cs"
    )
    test_path.parent.mkdir(parents=True)
    test_path.write_text(
        "public sealed class VersionParityTests {\n"
        "    [UnixFact]\n"
        "    public void AttachmentAccounting() { }\n"
        "}\n",
        encoding="utf-8",
    )
    write_environment(results, tmp_path)
    mapping = {"attachment_accounting": (VERSION_PARITY_TEST + "AttachmentAccounting",)}

    reconciled = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )

    row = next(
        item
        for item in reconciled["capabilities"]
        if item["capability"] == "attachment_accounting"
    )
    assert row["evidence"]["evaluatedCommit"] == commit
    assert row["evidence"]["sourceState"] == "uncommitted"
    assert row["evidence"]["sourceTreeFingerprint"] == source_tree_fingerprint(
        tmp_path,
        excluded_roots=[results.parent],
    )


@pytest.mark.parametrize(
    ("environment_change", "error"),
    [
        ({"evaluatedCommit": "f" * 40}, "environment commit differs from matrix"),
        ({"sourceTreeFingerprint": "f" * 64}, "source fingerprint differs"),
        ({"tmuxVersions": [{}]}, "matrix environment observations are invalid"),
    ],
)
def test_reconciliation_rejects_environment_source_drift(
    tmp_path: pathlib.Path,
    environment_change: dict[str, t.Any],
    error: str,
) -> None:
    """Reject matrix metadata that is not bound to its current source tree."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    environment_path = results.with_name("environment.json")
    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    environment.update(environment_change)
    environment_path.write_text(
        json.dumps(environment, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    with pytest.raises(namespace["VersionReconciliationError"], match=error):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )


def test_command_gate_validation_handles_unhashable_fields() -> None:
    """Return violations for malformed command-gate lists instead of raising."""
    namespace = load_reconciler()
    document = copy.deepcopy(seed_document())
    row = next(item for item in document["capabilities"] if "tmuxFlags" in item)
    row["tmuxFlags"] = [{}]
    row["pythonSourceSymbolIds"] = [{}]

    assert namespace["validate"](document) == [
        f"invalid command gate: {row['capability']}"
    ]


def test_reconciliation_records_only_checked_complete_matrix_evidence(
    tmp_path: pathlib.Path,
) -> None:
    """Promote pending rows with a complete matrix and present mapped tests."""
    namespace = load_reconciler()
    results, mapping, commit = write_fixture(tmp_path, matrix_rows())

    reconciled = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )

    by_capability = {row["capability"]: row for row in reconciled["capabilities"]}
    assert {
        capability
        for capability, row in by_capability.items()
        if row["evidenceStatus"] == "verified"
    } == set(mapping)
    assert {
        capability
        for capability, row in by_capability.items()
        if row["evidenceStatus"] == "pending"
    } == set(by_capability) - set(mapping)
    for row in reconciled["capabilities"]:
        if row["capability"] not in mapping:
            assert "evidence" not in row
            continue

        assert row["evidence"]["evaluatedCommit"] == commit
        assert row["evidence"]["capabilityCohort"] == CAPABILITY_COHORT
        assert row["evidence"]["frameworks"] == ["net10.0", "net8.0"]
        assert row["evidence"]["results"] == (
            "docs/decisions/evidence/0001/results.ndjson"
        )
        assert row["evidence"]["sourceState"] == "clean"
        assert row["evidence"]["sourceTreeFingerprint"] == source_tree_fingerprint(
            tmp_path,
            excluded_roots=[results.parent],
        )
        assert row["evidence"]["testCount"] == 139
        assert row["evidence"]["tests"] == list(mapping[row["capability"]])


def test_reconciliation_retains_raw_break_transition_without_policy_evidence(
    tmp_path: pathlib.Path,
) -> None:
    """Validate raw 3.7 proof while the future BreakAsync policy stays pending."""
    namespace = load_reconciler()
    results, _mapping, _commit = write_fixture(tmp_path, matrix_rows())
    reconciled = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests={},
    )
    row = next(
        item
        for item in reconciled["capabilities"]
        if item["capability"] == "break_pane_3_7_workaround"
    )
    transition = namespace["_inspect_break_pane_transition"](
        results,
        tmp_path,
        namespace["_inspect_matrix"](results),
    )

    assert row["evidenceStatus"] == "pending"
    assert "evidence" not in row
    assert transition["transitionTmuxSourceCommits"] == {
        "3.7": TRANSITION_TMUX_SOURCE_COMMIT,
    }
    assert transition["transitionTranscript"] == (
        "docs/decisions/evidence/0001/"
        "protocol-transcripts/break-pane-transition.txt"
    )


def test_component_three_cohort_rejects_command_policy_promotion(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a raw command probe presented as future wrapper-policy proof."""
    namespace = load_reconciler()
    results, _mapping, _commit = write_fixture(tmp_path, matrix_rows())

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="command policy evidence must remain pending",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests={
                "break_pane_3_7_workaround": (
                    "spikes/EvidenceTests.cs::BreakPane37Workaround",
                )
            },
        )


def test_closure_cohort_uses_only_exact_wrapper_policy_proofs(
    tmp_path: pathlib.Path,
) -> None:
    """Promote policies only from their frozen public-wrapper test identities."""
    namespace = load_reconciler()
    results, _mapping, _commit = write_fixture(tmp_path, matrix_rows())
    write_closure_environment(results, tmp_path)
    mapping = namespace["EVIDENCE_COHORT_TESTS"][CLOSURE_COHORT]
    commit = write_policy_test_source(tmp_path, mapping)
    write_rows(results, matrix_rows(commit))
    write_closure_environment(results, tmp_path)

    reconciled = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
    )
    policies = [
        row for row in reconciled["capabilities"] if row.get("kind") == "command_gate"
    ]

    assert len(policies) == 35
    assert {row["evidenceStatus"] for row in policies} == {"verified"}
    assert {row["evidence"]["capabilityCohort"] for row in policies} == {CLOSURE_COHORT}
    assert {
        row["capability"]: tuple(row["evidence"]["tests"]) for row in policies
    } == mapping


def test_closure_cohort_rejects_raw_version_parity_policy_tests(
    tmp_path: pathlib.Path,
) -> None:
    """Never accept a raw tmux surface probe as wrapper-policy evidence."""
    namespace = load_reconciler()
    results, _mapping, _commit = write_fixture(tmp_path, matrix_rows())
    write_closure_environment(results, tmp_path)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="closure capability mapping is not exact",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests={
                "break_pane_3_7_workaround": (
                    VERSION_PARITY_TEST + "BreakPane37Workaround",
                )
            },
        )


def test_closure_policy_contract_covers_each_behavior_family() -> None:
    """Freeze causal wrapper assertions for warnings, throws, support, and 3.7."""
    namespace = load_reconciler()
    contracts = namespace["POLICY_PROOF_CONTRACTS"]
    rows = {
        row["capability"]: row
        for row in seed_document()["capabilities"]
        if row.get("kind") == "command_gate"
    }

    assert set(contracts) == set(rows)
    for capability, row in rows.items():
        contract = contracts[capability]
        assert row["policyProofContract"] == contract
        assert row["wrapperPolicyTests"] == list(
            namespace["POLICY_WRAPPER_TESTS"][capability]
        )
        assert contract["supportedBoundary"] == "exact_argv_single_dispatch"
        if row["unsupportedBehavior"] == "warn_and_ignore":
            assert contract["unsupportedBoundary"] == "warn_omit_single_dispatch"
        elif row["unsupportedBehavior"] == "throw_unsupported_version":
            assert contract["unsupportedBoundary"] == (
                "typed_version_exception_zero_dispatch"
            )
        elif row["unsupportedBehavior"] == "apply_only_in_affected_version":
            assert contract["unsupportedBoundary"] == "exact_3_7_and_3_7a_transition"
        else:
            assert row["unsupportedBehavior"] == (
                "not_applicable_below_supported_floor"
            )
            assert contract["unsupportedBoundary"] == "not_applicable"


def test_reconciliation_rejects_break_pane_transition_source_drift(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a 3.7a transcript that does not use the required matrix binary."""
    namespace = load_reconciler()
    results, _mapping, _commit = write_fixture(tmp_path, matrix_rows())
    transcript = results.parent / "protocol-transcripts" / "break-pane-transition.txt"
    transcript.write_text(
        transcript.read_text(encoding="utf-8").replace("6" * 40, "f" * 40),
        encoding="utf-8",
    )
    mapping = {
        "break_pane_3_7_workaround": (
            "spikes/EvidenceTests.cs::BreakPane37Workaround",
        )
    }

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="transition transcript",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )


def test_reconciliation_rejects_an_incomplete_required_grid(
    tmp_path: pathlib.Path,
) -> None:
    """Do not promote capabilities when one required framework row is absent."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows()[:-1])

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="required matrix",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )


def test_write_is_atomic_and_deterministic(tmp_path: pathlib.Path) -> None:
    """Write one deterministic document and leave no transaction residue."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    document = tmp_path / "docs" / "parity" / "version-deltas.json"
    document.parent.mkdir(parents=True)
    document.write_text(
        json.dumps(seed_document(), indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    write_environment(results, tmp_path)

    first = namespace["main"](
        ["--evidence", str(results), "--write"],
        document_path=document,
        repository=tmp_path,
        capability_tests=mapping,
    )
    first_content = document.read_text(encoding="utf-8")
    namespace["write_document"](document, json.loads(first_content))

    assert first == 0
    assert document.read_text(encoding="utf-8") == first_content
    assert not list(document.parent.glob(".version-deltas.json.*"))


def test_written_evidence_remains_bound_after_reconciliation_and_commit(
    tmp_path: pathlib.Path,
) -> None:
    """Keep the tested source content identity stable across metadata and commit."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    document = tmp_path / "docs" / "parity" / "version-deltas.json"
    document.parent.mkdir(parents=True)
    document.write_text(
        json.dumps(seed_document(), indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    write_environment(results, tmp_path)

    assert (
        namespace["main"](
            ["--evidence", str(results), "--write"],
            document_path=document,
            repository=tmp_path,
            capability_tests=mapping,
        )
        == 0
    )
    validate_persisted = namespace.get("validate_persisted_evidence")
    assert callable(validate_persisted)
    assert (
        validate_persisted(
            json.loads(document.read_text(encoding="utf-8")),
            repository=tmp_path,
            document_path=document,
        )
        == []
    )

    subprocess.run(
        ["git", "-C", str(tmp_path), "add", "docs"],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "commit", "--quiet", "-m", "evidence"],
        check=True,
    )

    assert (
        validate_persisted(
            json.loads(document.read_text(encoding="utf-8")),
            repository=tmp_path,
            document_path=document,
        )
        == []
    )


def test_persisted_evidence_rejects_missing_results(tmp_path: pathlib.Path) -> None:
    """Reject a verified record after its bound matrix results disappear."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    document = tmp_path / "docs" / "parity" / "version-deltas.json"
    document.parent.mkdir(parents=True)
    document.write_text(
        json.dumps(seed_document(), indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    write_environment(results, tmp_path)
    assert (
        namespace["main"](
            ["--evidence", str(results), "--write"],
            document_path=document,
            repository=tmp_path,
            capability_tests=mapping,
        )
        == 0
    )
    results.unlink()

    validate_persisted = namespace.get("validate_persisted_evidence")
    assert callable(validate_persisted)
    assert "persisted matrix evidence cannot be read" in validate_persisted(
        json.loads(document.read_text(encoding="utf-8")),
        repository=tmp_path,
        document_path=document,
    )


def test_dry_run_validates_without_writing(tmp_path: pathlib.Path) -> None:
    """Validate the proposed reconciliation without mutating the document."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    document = tmp_path / "docs" / "parity" / "version-deltas.json"
    document.parent.mkdir(parents=True)
    original = json.dumps(seed_document(), indent=2, sort_keys=True) + "\n"
    document.write_text(original, encoding="utf-8")
    write_environment(results, tmp_path)

    result = namespace["main"](
        ["--evidence", str(results)],
        document_path=document,
        repository=tmp_path,
        capability_tests=mapping,
    )

    assert result == 0
    assert document.read_text(encoding="utf-8") == original


def test_source_changes_after_matrix_are_rejected(tmp_path: pathlib.Path) -> None:
    """Reject a mapped test added after the matrix fingerprint was recorded."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    late_path = tmp_path / "spikes" / "LateTests.cs"
    late_path.write_text(
        "public sealed class LateTests { public void Too_late() { } }\n",
        encoding="utf-8",
    )
    mapping["attachment_accounting"] = ("spikes/LateTests.cs::Too_late",)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="source fingerprint differs",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )


def test_mapping_must_name_a_discoverable_xunit_test(
    tmp_path: pathlib.Path,
) -> None:
    """Reject a helper method that the aggregate test run cannot discover."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    test_path = tmp_path / "spikes" / "EvidenceTests.cs"
    test_path.write_text(
        "public sealed class EvidenceTests {\n"
        "    public void Capability_is_exercised() { }\n"
        "}\n",
        encoding="utf-8",
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "add", "spikes/EvidenceTests.cs"],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "commit", "--quiet", "-m", "helper"],
        check=True,
    )
    commit = subprocess.run(
        ["git", "-C", str(tmp_path), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    write_rows(results, matrix_rows(commit))
    write_environment(results, tmp_path)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="evaluated commit",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )


def test_matrix_rejects_malformed_commit_and_advisory_rows(
    tmp_path: pathlib.Path,
) -> None:
    """Validate every supplied row before considering capability mappings."""
    namespace = load_reconciler()
    rows = matrix_rows()
    results, mapping, _commit = write_fixture(tmp_path, rows)
    rows[0]["evaluatedCommit"] = "not-a-commit"
    write_rows(results, rows)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="evaluated commit",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path,
            capability_tests=mapping,
        )

    rows = matrix_rows()
    results, mapping, commit = write_fixture(tmp_path / "advisory", rows)
    rows.extend(
        {
            "advisory": False,
            "evaluatedCommit": commit,
            "framework": framework,
            "status": "passed",
            "testCount": 139,
            "tmuxSourceCommit": "a" * 40,
            "tmuxVersion": "master",
        }
        for framework in ["net10.0", "net8.0"]
    )
    write_rows(results, rows)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="master",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path / "advisory",
            capability_tests=mapping,
        )

    for row in rows[-2:]:
        row["advisory"] = True
        row["status"] = []
    write_rows(results, rows)

    with pytest.raises(
        namespace["VersionReconciliationError"],
        match="master",
    ):
        namespace["reconcile"](
            copy.deepcopy(seed_document()),
            results,
            repository=tmp_path / "advisory",
            capability_tests=mapping,
        )


def test_same_cohort_rerun_refreshes_verified_evidence(
    tmp_path: pathlib.Path,
) -> None:
    """Bind verified rows to the latest matrix from their evidence cohort."""
    namespace = load_reconciler()
    first_results, mapping, first_commit = write_fixture(tmp_path, matrix_rows())
    first = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        first_results,
        repository=tmp_path,
        capability_tests=mapping,
    )
    test_source = tmp_path / "spikes" / "EvidenceTests.cs"
    test_source.write_text(
        test_source.read_text(encoding="utf-8") + "\n",
        encoding="utf-8",
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "add", "spikes/EvidenceTests.cs"],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "commit", "--quiet", "-m", "refresh"],
        check=True,
    )
    second_commit = subprocess.run(
        ["git", "-C", str(tmp_path), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    second_rows = matrix_rows(second_commit)
    second_results = first_results.with_name("second.ndjson")
    write_rows(second_results, second_rows)
    write_environment(second_results, tmp_path)

    second = namespace["reconcile"](
        first,
        second_results,
        repository=tmp_path,
        capability_tests=mapping,
    )

    verified = [
        row for row in second["capabilities"] if row["evidenceStatus"] == "verified"
    ]
    assert first_commit != second_commit
    assert {row["evidence"]["evaluatedCommit"] for row in verified} == {second_commit}


def test_same_cohort_rebuilds_tampered_prior_metadata(
    tmp_path: pathlib.Path,
) -> None:
    """Replace prior metadata from the canonical matrix and static mapping."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    expected = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )
    tampered = copy.deepcopy(expected)
    verified = next(
        row for row in tampered["capabilities"] if row["evidenceStatus"] == "verified"
    )
    verified["evidence"]["evaluatedCommit"] = "0" * 40
    verified["evidence"]["tests"] = ["missing.cs::Not_a_test"]

    repaired = namespace["reconcile"](
        tampered,
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )

    assert repaired == expected


def test_later_cohort_preserves_prior_commit_tree_evidence(
    tmp_path: pathlib.Path,
) -> None:
    """Keep prior verification after its mapped spike tests are deleted."""
    namespace = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    first = namespace["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )
    (tmp_path / "spikes" / "EvidenceTests.cs").unlink()
    subprocess.run(
        [
            "git",
            "-C",
            str(tmp_path),
            "add",
            "spikes/EvidenceTests.cs",
        ],
        check=True,
    )
    subprocess.run(
        ["git", "-C", str(tmp_path), "commit", "--quiet", "-m", "delete spike"],
        check=True,
    )
    later_commit = subprocess.run(
        ["git", "-C", str(tmp_path), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    later_results = results.with_name("later.ndjson")
    write_rows(later_results, matrix_rows(later_commit))
    write_environment(later_results, tmp_path)

    later = namespace["reconcile"](
        first,
        later_results,
        repository=tmp_path,
        capability_tests={},
    )

    assert later == first


def test_generator_preserves_valid_reconciliation(tmp_path: pathlib.Path) -> None:
    """Keep evidence-owned fields across generation and currentness checks."""
    reconciler = load_reconciler()
    results, mapping, _commit = write_fixture(tmp_path, matrix_rows())
    reconciled = reconciler["reconcile"](
        copy.deepcopy(seed_document()),
        results,
        repository=tmp_path,
        capability_tests=mapping,
    )
    generator = load_generator()
    document_root = tmp_path / "generated"
    document_root.mkdir()
    version_path = document_root / "version-deltas.json"
    version_path.write_text(
        json.dumps(reconciled, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    generator["write_documents"].__globals__["DOCUMENT_ROOT"] = document_root

    generator["write_documents"]()

    assert json.loads(version_path.read_text(encoding="utf-8")) == reconciled
    assert generator["documents_are_current"]() is True


def test_document_validation_rejects_duplicate_and_nonobject_rows() -> None:
    """Return violations instead of accepting duplicates or raising."""
    namespace = load_reconciler()
    duplicate = copy.deepcopy(seed_document())
    duplicate["capabilities"][-1] = copy.deepcopy(duplicate["capabilities"][0])
    unhashable_capability = copy.deepcopy(seed_document())
    unhashable_capability["capabilities"][0]["capability"] = []
    unhashable_test = copy.deepcopy(seed_document())
    row = unhashable_test["capabilities"][0]
    row["evidenceStatus"] = "verified"
    row["evidence"] = {
        "evaluatedCommit": "a" * 40,
        "frameworks": ["net10.0", "net8.0"],
        "results": "evidence/results.ndjson",
        "testCount": 1,
        "tests": [[]],
        "tmuxSourceCommits": dict.fromkeys(
            ["3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b"],
            "b" * 40,
        ),
        "tmuxVersions": ["3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b"],
    }

    assert namespace["validate"](duplicate)
    assert namespace["validate"]({"capabilities": [None]})
    assert namespace["validate"](unhashable_capability)
    assert namespace["validate"](unhashable_test)


def test_command_flag_capabilities_freeze_version_and_fallback_behavior() -> None:
    """Split command gates whenever version or unsupported behavior differs."""
    rows = {
        row["capability"]: row
        for row in seed_document()["capabilities"]
        if row.get("kind") == "command_gate"
    }
    actual = {
        name: (
            row["featureKind"],
            row["tmuxCommand"],
            tuple(row["tmuxFlags"]),
            row["introducedIn"],
            row["removedIn"],
            row["unsupportedBehavior"],
            tuple(row["pythonSourceSymbolIds"]),
        )
        for name, row in rows.items()
    }
    hook_unsupported = "not_applicable_below_supported_floor"

    assert "command_flags" not in {
        row["capability"] for row in seed_document()["capabilities"]
    }
    assert actual == {
        "break_pane_3_7_workaround": (
            "workaround",
            "break-pane",
            ("-n",),
            "3.7",
            "3.7a",
            "apply_only_in_affected_version",
            ("libtmux.pane:Pane.break_pane",),
        ),
        "capture_pane_3_7_metadata": (
            "flags",
            "capture-pane",
            ("-H", "-L", "-F"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        "capture_pane_mode_screen": (
            "flag",
            "capture-pane",
            ("-M",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        "choose_tree_sort_time": (
            "removed_flag",
            "choose-tree",
            ("-O",),
            "unknown",
            "3.7",
            "warn_and_ignore",
            ("libtmux.pane:Pane.choose_tree",),
        ),
        "capture_pane_trim_trailing": (
            "flag",
            "capture-pane",
            ("-T",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        "clear_history_hyperlinks": (
            "flag",
            "clear-history",
            ("-H",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.clear_history",),
        ),
        "clear_prompt_history_command": (
            "command",
            "clear-prompt-history",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.clear_prompt_history",),
        ),
        "command_prompt_3_7_behavior": (
            "flags",
            "command-prompt",
            ("-e", "-C"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.command_prompt",),
        ),
        "command_prompt_background": (
            "flags",
            "command-prompt",
            ("-b", "-F"),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.command_prompt",),
        ),
        "command_prompt_literal": (
            "flag",
            "command-prompt",
            ("-l",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.command_prompt",),
        ),
        "confirm_before_acceptance": (
            "flags",
            "confirm-before",
            ("-c", "-y"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.confirm_before",),
        ),
        "confirm_before_background": (
            "flag",
            "confirm-before",
            ("-b",),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.confirm_before",),
        ),
        "copy_mode_page_down": (
            "flag",
            "copy-mode",
            ("-d",),
            "3.5",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.copy_mode",),
        ),
        "display_menu_mouse": (
            "flag",
            "display-menu",
            ("-M",),
            "3.5",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.display_menu",),
        ),
        "display_menu_styles": (
            "flags",
            "display-menu",
            ("-C", "-b", "-s", "-S", "-H"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.display_menu",),
        ),
        "display_message_client": (
            "flag",
            "display-message",
            ("-c",),
            "3.3",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.cmd",),
        ),
        "display_message_literal": (
            "flag",
            "display-message",
            ("-l",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            (
                "libtmux.pane:Pane.display_message",
                "libtmux.server:Server.display_message",
                "libtmux.window:Window.display_message",
            ),
        ),
        "display_message_update_pane": (
            "flag",
            "display-message",
            ("-C",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_message",),
        ),
        "display_popup_3_3_options": (
            "flags",
            "display-popup",
            ("-T", "-b", "-s", "-S", "-e", "-B"),
            "3.3",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_popup",),
        ),
        "display_popup_3_6_key_policy": (
            "flags",
            "display-popup",
            ("-k", "-N"),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_popup",),
        ),
        "hook_scope_pane_window_set": (
            "flags",
            "set-hook",
            ("-p", "-w"),
            "3.2",
            "unknown",
            hook_unsupported,
            (
                "libtmux.hooks:HooksMixin.run_hook",
                "libtmux.hooks:HooksMixin.set_hook",
                "libtmux.hooks:HooksMixin.unset_hook",
            ),
        ),
        "hook_scope_pane_window_show": (
            "flags",
            "show-hooks",
            ("-p", "-w"),
            "3.2",
            "unknown",
            hook_unsupported,
            (
                "libtmux.hooks:HooksMixin.show_hook",
                "libtmux.hooks:HooksMixin.show_hooks",
            ),
        ),
        "kill_session_group": (
            "flag",
            "kill-session",
            ("-g",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.session:Session.kill",),
        ),
        "list_keys_format": (
            "flag",
            "list-keys",
            ("-F",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.list_keys",),
        ),
        "new_pane_command": (
            "command",
            "new-pane",
            (),
            "3.7",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.pane:Pane.new_pane",),
        ),
        "paste_buffer_no_vis": (
            "flag",
            "paste-buffer",
            ("-S",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.paste_buffer",),
        ),
        "refresh_client_clipboard_query": (
            "semantic_transition",
            "refresh-client",
            ("-l",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.refresh_client",),
        ),
        "run_shell_arguments": (
            "positional_arguments",
            "run-shell",
            (),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        "run_shell_show_stderr": (
            "flag",
            "run-shell",
            ("-E",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        "run_shell_working_directory": (
            "flag",
            "run-shell",
            ("-c",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        "send_keys_client_keys": (
            "flags",
            "send-keys",
            ("-K", "-c"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.send_keys",),
        ),
        "server_access_command": (
            "command",
            "server-access",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.server_access",),
        ),
        "show_prompt_history_command": (
            "command",
            "show-prompt-history",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.show_prompt_history",),
        ),
        "split_window_appearance": (
            "flags",
            "split-window",
            ("-s", "-S", "-R", "-m", "-k"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.split",),
        ),
        "split_window_empty": (
            "flag",
            "split-window",
            ("-E",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.split",),
        ),
    }

    assert all(row["evidenceStatus"] == "pending" for row in rows.values())
    assert all("evidence" not in row for row in rows.values())
    assert all(
        row["namedRealServerTest"].startswith(
            "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs::"
        )
        for row in rows.values()
    )


def test_refresh_clipboard_is_a_closed_semantic_transition() -> None:
    """Distinguish the 3.7 wrapper policy from the older raw -l surface."""
    namespace = load_reconciler()
    document = seed_document()
    row = next(
        item
        for item in document["capabilities"]
        if item["capability"] == "refresh_client_clipboard_query"
    )

    assert row["featureKind"] == "semantic_transition"
    assert row["surfacePresentBy"] == "3.2a"
    assert row["semanticTransition"] == {
        "after": "query_and_store_buffer_only",
        "before": "query_with_optional_target_pane_forwarding",
    }
    assert namespace["validate"](document) == []

    row["semanticTransition"]["after"] = "query_with_optional_target_pane_forwarding"
    assert namespace["validate"](document)


def test_command_policies_have_exact_later_component_owners() -> None:
    """Hand every wrapper policy to the component that implements its callers."""
    rows = {
        row["capability"]: tuple(row["policyOwnerComponents"])
        for row in seed_document()["capabilities"]
        if row.get("kind") == "command_gate"
    }

    assert rows == {
        "break_pane_3_7_workaround": (12,),
        "capture_pane_3_7_metadata": (12,),
        "capture_pane_mode_screen": (12,),
        "choose_tree_sort_time": (12,),
        "capture_pane_trim_trailing": (12,),
        "clear_history_hyperlinks": (12,),
        "clear_prompt_history_command": (16,),
        "command_prompt_3_7_behavior": (16,),
        "command_prompt_background": (16,),
        "command_prompt_literal": (16,),
        "confirm_before_acceptance": (16,),
        "confirm_before_background": (16,),
        "copy_mode_page_down": (12,),
        "display_menu_mouse": (16,),
        "display_menu_styles": (16,),
        "display_message_client": (16,),
        "display_message_literal": (11, 12, 16),
        "display_message_update_pane": (12,),
        "display_popup_3_3_options": (12,),
        "display_popup_3_6_key_policy": (12,),
        "hook_scope_pane_window_set": (15,),
        "hook_scope_pane_window_show": (15,),
        "kill_session_group": (10,),
        "list_keys_format": (16,),
        "new_pane_command": (12,),
        "paste_buffer_no_vis": (12,),
        "refresh_client_clipboard_query": (13,),
        "run_shell_arguments": (16,),
        "run_shell_show_stderr": (16,),
        "run_shell_working_directory": (16,),
        "send_keys_client_keys": (12,),
        "server_access_command": (16,),
        "show_prompt_history_command": (16,),
        "split_window_appearance": (12,),
        "split_window_empty": (12,),
    }


def test_hook_flags_are_baseline_supported_without_a_below_floor_policy() -> None:
    """Keep tmux 3.2 history without promising behavior below the 3.2a floor."""
    namespace = load_reconciler()
    document = seed_document()
    rows = {
        row["capability"]: row
        for row in document["capabilities"]
        if row["capability"].startswith("hook_scope_pane_window_")
    }

    assert {
        capability: (
            row["introducedIn"],
            row["supportRange"],
            row["unsupportedBehavior"],
        )
        for capability, row in rows.items()
    } == {
        "hook_scope_pane_window_set": (
            "3.2",
            "baseline",
            "not_applicable_below_supported_floor",
        ),
        "hook_scope_pane_window_show": (
            "3.2",
            "baseline",
            "not_applicable_below_supported_floor",
        ),
    }
    assert namespace["validate"](document) == []

    rows["hook_scope_pane_window_set"]["unsupportedBehavior"] = "warn_and_ignore"
    assert namespace["validate"](document) == [
        "invalid command gate: hook_scope_pane_window_set"
    ]


def test_transition_sources_cover_every_version_boundary() -> None:
    """Pin the exact tmux manual at each introduction and removal boundary."""
    rows = [
        row
        for row in seed_document()["capabilities"]
        if row.get("kind") == "command_gate"
    ]

    for row in rows:
        if row["capability"] == "break_pane_3_7_workaround":
            assert row["tmuxTransitionSources"] == {
                version: (
                    f"https://github.com/tmux/tmux/blob/{version}/cmd-break-pane.c"
                )
                for version in ["3.7", "3.7a"]
            }
            continue
        if row["capability"] == "refresh_client_clipboard_query":
            assert row["tmuxTransitionSources"] == {
                "3.7": [
                    "https://github.com/tmux/tmux/blob/3.7/CHANGES",
                    ("https://github.com/tmux/tmux/blob/3.7/cmd-refresh-client.c"),
                ]
            }
            continue
        # A boundary outside the supported range has no manual to pin, which
        # is what "unknown" says.
        versions = {row["introducedIn"], row["removedIn"]} - {"unknown"}
        assert row.get("tmuxTransitionSources") == {
            version: f"https://github.com/tmux/tmux/blob/{version}/tmux.1"
            for version in sorted(versions)
        }


def test_break_pane_workaround_has_a_pinned_tmux_3_7_proof() -> None:
    """Exercise the workaround's active release through its pinned proof path."""
    row = next(
        item
        for item in seed_document()["capabilities"]
        if item["capability"] == "break_pane_3_7_workaround"
    )

    assert row.get("proofTmuxVersions") == ["3.7", "3.7a"]
    assert row.get("tmuxTransitionSources") == {
        "3.7": "https://github.com/tmux/tmux/blob/3.7/cmd-break-pane.c",
        "3.7a": "https://github.com/tmux/tmux/blob/3.7a/cmd-break-pane.c",
    }
