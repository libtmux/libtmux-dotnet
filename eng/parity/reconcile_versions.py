"""Validate and reconcile the tmux-version capability matrix."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import typing as t

REPOSITORY_ROOT = pathlib.Path(__file__).parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from eng.evidence.assemble_bundle import (  # noqa: E402
    BundleAssemblyError,
    source_state,
    source_tree_fingerprint,
)

DOCUMENT_PATH = (
    pathlib.Path(__file__).parents[2] / "docs" / "parity" / "version-deltas.json"
)
PROTOCOL_CAPABILITIES = {
    "attachment_accounting",
    "byte_length_framing",
    "control_notifications",
    "format_fields_and_operators",
    "missing_target_format_safety",
    "option_dollar_double_escape",
    "semicolon_grouping",
}
COMMAND_GATE_CAPABILITIES = {
    "break_pane_3_7_workaround",
    "capture_pane_3_7_metadata",
    "capture_pane_mode_screen",
    "capture_pane_trim_trailing",
    "choose_tree_sort_time",
    "clear_history_hyperlinks",
    "clear_prompt_history_command",
    "command_prompt_3_7_behavior",
    "command_prompt_background",
    "command_prompt_literal",
    "confirm_before_acceptance",
    "confirm_before_background",
    "copy_mode_page_down",
    "display_menu_mouse",
    "display_menu_styles",
    "display_message_client",
    "display_message_literal",
    "display_message_update_pane",
    "display_popup_3_3_options",
    "display_popup_3_6_key_policy",
    "hook_scope_pane_window_set",
    "hook_scope_pane_window_show",
    "kill_session_group",
    "list_keys_format",
    "new_pane_command",
    "paste_buffer_no_vis",
    "refresh_client_clipboard_query",
    "run_shell_arguments",
    "run_shell_show_stderr",
    "run_shell_working_directory",
    "send_keys_client_keys",
    "server_access_command",
    "show_prompt_history_command",
    "split_window_appearance",
    "split_window_empty",
}
REQUIRED_CAPABILITIES = PROTOCOL_CAPABILITIES | COMMAND_GATE_CAPABILITIES
VERSION_PARITY_TEST = (
    "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs::"
)
VERSION_PARITY_METHODS = {
    "attachment_accounting": "AttachmentAccounting",
    "break_pane_3_7_workaround": "BreakPane37Workaround",
    "byte_length_framing": "ByteLengthFraming",
    "capture_pane_3_7_metadata": "CapturePane37Metadata",
    "capture_pane_mode_screen": "CapturePaneModeScreen",
    "capture_pane_trim_trailing": "CapturePaneTrimTrailing",
    "choose_tree_sort_time": "ChooseTreeSortTime",
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
PRODUCTION_CAPABILITY_TESTS = {
    capability: (VERSION_PARITY_TEST + method,)
    for capability, method in VERSION_PARITY_METHODS.items()
}
EVIDENCE_COHORT_TESTS: dict[str, dict[str, tuple[str, ...]]] = {
    "0001": {
        capability: PRODUCTION_CAPABILITY_TESTS[capability]
        for capability in PROTOCOL_CAPABILITIES
    }
}
CAPABILITY_COHORT = "0001"
CLOSURE_COHORT = "closure"
POLICY_OWNER_COMPONENTS = {
    "break_pane_3_7_workaround": (12,),
    "capture_pane_3_7_metadata": (12,),
    "capture_pane_mode_screen": (12,),
    "capture_pane_trim_trailing": (12,),
    "choose_tree_sort_time": (12,),
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
POLICY_TEST_FILES_BY_COMPONENT = {
    10: (
        "tests/LibTmux.IntegrationTests/Hierarchy/ServerSessionLifecycleTests.cs"
    ),
    11: ("tests/LibTmux.IntegrationTests/Hierarchy/WindowTopologyTests.cs"),
    12: ("tests/LibTmux.IntegrationTests/Hierarchy/PaneOperationsTests.cs"),
    13: ("tests/LibTmux.IntegrationTests/Clients/ClientAdministrationTests.cs"),
    15: "tests/LibTmux.IntegrationTests/Hooks/HookOperationsTests.cs",
    16: "tests/LibTmux.IntegrationTests/Utilities/ServerUtilitiesTests.cs",
}
POLICY_WRAPPER_TESTS = {
    capability: tuple(
        f"{POLICY_TEST_FILES_BY_COMPONENT[component]}::"
        f"{VERSION_PARITY_METHODS[capability]}VersionPolicy"
        for component in components
    )
    for capability, components in POLICY_OWNER_COMPONENTS.items()
}
POLICY_UNSUPPORTED_PROOFS: dict[str, str] = dict.fromkeys(
    COMMAND_GATE_CAPABILITIES,
    "warn_omit_single_dispatch",
)
POLICY_UNSUPPORTED_PROOFS.update(
    dict.fromkeys(
        (
            "clear_prompt_history_command",
            "command_prompt_background",
            "confirm_before_background",
            "new_pane_command",
            "server_access_command",
            "show_prompt_history_command",
        ),
        "typed_version_exception_zero_dispatch",
    )
)
POLICY_UNSUPPORTED_PROOFS.update(
    {
        "break_pane_3_7_workaround": "exact_3_7_and_3_7a_transition",
        "hook_scope_pane_window_set": "not_applicable",
        "hook_scope_pane_window_show": "not_applicable",
    }
)
POLICY_PROOF_CONTRACTS = {
    capability: {
        "supportedBoundary": "exact_argv_single_dispatch",
        "unsupportedBoundary": POLICY_UNSUPPORTED_PROOFS[capability],
    }
    for capability in COMMAND_GATE_CAPABILITIES
}
EVIDENCE_COHORT_TESTS[CLOSURE_COHORT] = POLICY_WRAPPER_TESTS
HOOK_SCOPE_CAPABILITIES = {
    "hook_scope_pane_window_set",
    "hook_scope_pane_window_show",
}
REQUIRED_FRAMEWORKS = ("net10.0", "net8.0")
REQUIRED_TMUX_VERSIONS = (
    "3.2a",
    "3.3a",
    "3.4",
    "3.5",
    "3.6",
    "3.7a",
    "3.7b",
    "3.7c",
)
LEGACY_REQUIRED_TMUX_VERSIONS = REQUIRED_TMUX_VERSIONS[:-1]
KNOWN_REQUIRED_TMUX_VERSION_SETS = {
    LEGACY_REQUIRED_TMUX_VERSIONS,
    REQUIRED_TMUX_VERSIONS,
}
TMUX_SOURCE_ENDPOINTS = {
    "3.2a": "https://github.com/tmux/tmux/tree/3.2a",
    "3.7b": "https://github.com/tmux/tmux/tree/3.7b",
}
BASE_ROW_KEYS = {
    "capability",
    "evidenceStatus",
    "introducedIn",
    "namedRealServerTest",
    "removedIn",
    "tmuxSourceEndpoints",
}
COMMAND_GATE_KEYS = BASE_ROW_KEYS | {
    "featureKind",
    "kind",
    "policyOwnerComponents",
    "policyProofContract",
    "pythonSourceSymbolIds",
    "tmuxCommand",
    "tmuxFlags",
    "tmuxTransitionSources",
    "unsupportedBehavior",
    "wrapperPolicyTests",
}
BASELINE_COMMAND_GATE_KEYS = COMMAND_GATE_KEYS | {"supportRange"}
SEMANTIC_TRANSITION_KEYS = COMMAND_GATE_KEYS | {
    "semanticTransition",
    "surfacePresentBy",
}
EVIDENCE_KEYS = {
    "capabilityCohort",
    "evaluatedCommit",
    "frameworks",
    "results",
    "sourceContentFingerprint",
    "sourceState",
    "sourceTreeFingerprint",
    "testCount",
    "tests",
    "tmuxSourceCommits",
    "tmuxVersions",
}
ENVIRONMENT_KEYS = {
    "capabilityCohort",
    "evaluatedCommit",
    "evaluatedCommitTree",
    "frameworks",
    "includeMasterAdvisory",
    "platform",
    "redactionProof",
    "schemaVersion",
    "sdkVersion",
    "sourceState",
    "sourceTreeFingerprint",
    "transitionTmuxSourceCommits",
    "tmuxVersions",
}
CLOSURE_ENVIRONMENT_KEYS = ENVIRONMENT_KEYS - {"transitionTmuxSourceCommits"}
MATRIX_ROW_KEYS = {
    "advisory",
    "evaluatedCommit",
    "framework",
    "status",
    "testCount",
    "tmuxSourceCommit",
    "tmuxVersion",
}
COMMIT_PATTERN = re.compile(r"[0-9a-f]{40}")
FINGERPRINT_PATTERN = re.compile(r"[0-9a-f]{64}")
TMUX_VERSION_PATTERN = re.compile(r"\d+\.\d+(?:[a-z]+)?(?:-[0-9A-Za-z.]+)?")
REAL_SERVER_TEST_PATTERN = re.compile(
    r"tests/LibTmux\.IntegrationTests/Versioning/"
    r"[A-Za-z][A-Za-z0-9]*Tests\.cs::[A-Za-z][A-Za-z0-9]*"
)
BREAK_PANE_CAPABILITY = "break_pane_3_7_workaround"
BREAK_PANE_TRANSCRIPT = "protocol-transcripts/break-pane-transition.txt"
BREAK_PANE_TRANSCRIPT_PATTERN = re.compile(
    r"^event=break-pane-transition "
    r"framework=(?P<framework>net10\.0|net8\.0) "
    r"tmux-source-commit=(?P<source_commit>[0-9a-f]{40}) "
    r"tmux-version=(?P<version>3\.7a?) "
    r"workaround=(?P<workaround>applied|omitted) "
    r"outcome=(?P<outcome>passed)$"
)


class VersionReconciliationError(ValueError):
    """Report invalid or unsupported version evidence."""


def _fail(
    message: str,
    cause: BaseException | None = None,
) -> t.NoReturn:
    """Raise one typed reconciliation failure with an optional cause.

    Examples
    --------
    >>> try:
    ...     _fail("invalid")
    ... except VersionReconciliationError as error:
    ...     str(error)
    'invalid'
    """
    if cause is None:
        raise VersionReconciliationError(message)
    raise VersionReconciliationError(message) from cause


def load_document(path: pathlib.Path = DOCUMENT_PATH) -> dict[str, t.Any]:
    """Load the checked-in version delta document.

    Examples
    --------
    >>> isinstance(load_document(), dict)
    True
    """
    with path.open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def is_tmux_version_bound(value: object) -> bool:
    """Return whether a matrix bound is unknown or a tmux version token.

    Examples
    --------
    >>> is_tmux_version_bound("3.2a")
    True
    >>> is_tmux_version_bound("not-a-version")
    False
    """
    return value == "unknown" or (
        isinstance(value, str) and TMUX_VERSION_PATTERN.fullmatch(value) is not None
    )


def _known_required_tmux_versions(value: object) -> tuple[str, ...] | None:
    """Return an exact current or retained historical release set."""
    versions = (
        tuple(value)
        if isinstance(value, list) and all(isinstance(version, str) for version in value)
        else ()
    )
    return versions if versions in KNOWN_REQUIRED_TMUX_VERSION_SETS else None


def is_real_server_test(value: object) -> bool:
    """Return whether a named test follows the real-server convention.

    Examples
    --------
    >>> is_real_server_test(
    ...     "tests/LibTmux.IntegrationTests/Versioning/"
    ...     "VersionParityTests.cs::CommandFlags"
    ... )
    True
    >>> is_real_server_test("VersionParityTests")
    False
    """
    return (
        isinstance(value, str) and REAL_SERVER_TEST_PATTERN.fullmatch(value) is not None
    )


def _is_relative_path(value: object) -> bool:
    """Return whether a value is a safe repository-relative path.

    Examples
    --------
    >>> _is_relative_path("evidence/results.ndjson")
    True
    >>> _is_relative_path("../results.ndjson")
    False
    """
    if not isinstance(value, str):
        return False
    path = pathlib.PurePosixPath(value)
    return bool(value) and not path.is_absolute() and ".." not in path.parts


def _update_fingerprint_field(digest: t.Any, value: bytes) -> None:
    """Add one length-delimited byte field to a content fingerprint.

    Examples
    --------
    >>> digest = hashlib.sha256()
    >>> _update_fingerprint_field(digest, b"value")
    >>> len(digest.hexdigest())
    64
    """
    digest.update(len(value).to_bytes(8, "big"))
    digest.update(value)


def source_content_fingerprint(
    repository: pathlib.Path,
    excluded_paths: t.Iterable[pathlib.Path] = (),
) -> str:
    """Hash current repository content independently of Git commit state.

    Tracked and non-ignored untracked files participate by path, executable
    mode, and bytes. Evidence outputs and the reconciliation document can be
    excluded so writing or committing those metadata files closes over the
    same tested source identity.
    """
    repository = repository.resolve()
    excluded: list[pathlib.PurePosixPath] = []
    for path in excluded_paths:
        try:
            excluded_relative = path.resolve().relative_to(repository)
        except ValueError:
            continue
        excluded.append(pathlib.PurePosixPath(excluded_relative.as_posix()))
    try:
        result = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "ls-files",
                "--cached",
                "--others",
                "--exclude-standard",
                "-z",
            ],
            check=True,
            capture_output=True,
        )
    except (OSError, subprocess.CalledProcessError) as exception:
        _fail("source content cannot be enumerated", exception)
    try:
        paths = sorted(
            {
                pathlib.PurePosixPath(raw.decode("utf-8"))
                for raw in result.stdout.split(b"\0")
                if raw
            },
            key=lambda path: path.as_posix(),
        )
    except UnicodeDecodeError as exception:
        _fail("source content path is not UTF-8", exception)

    digest = hashlib.sha256()
    for relative_path in paths:
        if any(
            root == relative_path or root in relative_path.parents for root in excluded
        ):
            continue
        if relative_path.is_absolute() or ".." in relative_path.parts:
            _fail("source content path escapes the repository")
        candidate = repository / relative_path
        try:
            metadata = candidate.lstat()
        except FileNotFoundError:
            continue
        except OSError as exception:
            _fail("source content cannot be inspected", exception)
        _update_fingerprint_field(digest, relative_path.as_posix().encode("utf-8"))
        if stat.S_ISLNK(metadata.st_mode):
            try:
                target = candidate.readlink().as_posix().encode("utf-8")
            except (OSError, UnicodeEncodeError) as exception:
                _fail("source symlink cannot be inspected", exception)
            _update_fingerprint_field(digest, b"symlink")
            _update_fingerprint_field(digest, target)
        elif stat.S_ISREG(metadata.st_mode):
            try:
                content = candidate.read_bytes()
            except OSError as exception:
                _fail("source content cannot be read", exception)
            mode = b"executable" if metadata.st_mode & stat.S_IXUSR else b"regular"
            _update_fingerprint_field(digest, mode)
            _update_fingerprint_field(digest, content)
        else:
            _fail("source content is not a regular file or symlink")
    return digest.hexdigest()


def _validate_reconciled_evidence(
    value: object,
    *,
    expected_cohort: str,
    expected_tests: tuple[str, ...] | None,
) -> bool:
    """Return whether one persisted reconciliation record is exact.

    Examples
    --------
    >>> _validate_reconciled_evidence(
    ...     {}, expected_cohort="0001", expected_tests=("test",)
    ... )
    False
    """
    expected_keys = EVIDENCE_KEYS
    if not isinstance(value, dict) or set(value) != expected_keys:
        return False
    commit = value["evaluatedCommit"]
    test_count = value["testCount"]
    commits = value["tmuxSourceCommits"]
    tests = value["tests"]
    content_fingerprint = value["sourceContentFingerprint"]
    fingerprint = value["sourceTreeFingerprint"]
    required_versions = _known_required_tmux_versions(value["tmuxVersions"])
    base_valid = (
        isinstance(commit, str)
        and COMMIT_PATTERN.fullmatch(commit) is not None
        and value["capabilityCohort"] == expected_cohort
        and value["frameworks"] == list(REQUIRED_FRAMEWORKS)
        and _is_relative_path(value["results"])
        and isinstance(content_fingerprint, str)
        and FINGERPRINT_PATTERN.fullmatch(content_fingerprint) is not None
        and value["sourceState"] in {"clean", "uncommitted"}
        and isinstance(fingerprint, str)
        and FINGERPRINT_PATTERN.fullmatch(fingerprint) is not None
        and isinstance(test_count, int)
        and not isinstance(test_count, bool)
        and test_count > 0
        and isinstance(tests, list)
        and bool(tests)
        and (expected_tests is None or tests == list(expected_tests))
        and all(
            isinstance(test, str)
            and "::" in test
            and _is_relative_path(test.split("::", maxsplit=1)[0])
            for test in tests
        )
        and len(tests) == len(set(tests))
        and required_versions is not None
        and isinstance(commits, dict)
        and set(commits) == set(required_versions)
        and all(
            isinstance(source_commit, str)
            and COMMIT_PATTERN.fullmatch(source_commit) is not None
            for source_commit in commits.values()
        )
    )
    return base_valid


def validate(document: dict[str, t.Any]) -> list[str]:
    """Return version-matrix contract violations.

    Examples
    --------
    >>> validate({"capabilities": []})
    ['missing required capabilities']
    """
    if not isinstance(document, dict) or set(document) != {"capabilities"}:
        return ["document schema is invalid"]
    rows = document.get("capabilities")
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        return ["capability rows are invalid"]
    typed_rows = t.cast(list[dict[str, t.Any]], rows)
    capabilities = [row.get("capability") for row in typed_rows]
    if (
        len(capabilities) != len(REQUIRED_CAPABILITIES)
        or not all(isinstance(capability, str) for capability in capabilities)
        or set(capabilities) != REQUIRED_CAPABILITIES
    ):
        return ["missing required capabilities"]
    violations: list[str] = []
    for row in typed_rows:
        capability = t.cast(str, row["capability"])
        status = row.get("evidenceStatus")
        feature_kind = row.get("featureKind")
        base_keys = (
            (
                SEMANTIC_TRANSITION_KEYS
                if feature_kind == "semantic_transition"
                else (
                    BASELINE_COMMAND_GATE_KEYS
                    if capability in HOOK_SCOPE_CAPABILITIES
                    else COMMAND_GATE_KEYS
                )
            )
            | (
                {"proofTmuxVersions"}
                if capability == "break_pane_3_7_workaround"
                else set()
            )
            if capability in COMMAND_GATE_CAPABILITIES
            else BASE_ROW_KEYS
        )
        expected_keys = base_keys | ({"evidence"} if status == "verified" else set())
        if (
            set(row) != expected_keys
            or not isinstance(status, str)
            or status not in {"pending", "verified"}
        ):
            violations.append(f"invalid evidence schema: {capability}")
        endpoints = row.get("tmuxSourceEndpoints")
        if not isinstance(endpoints, dict) or set(endpoints) != set(
            TMUX_SOURCE_ENDPOINTS
        ):
            violations.append(f"missing source endpoints: {capability}")
        elif endpoints != TMUX_SOURCE_ENDPOINTS:
            violations.append(f"invalid source endpoints: {capability}")
        if not is_tmux_version_bound(
            row.get("introducedIn")
        ) or not is_tmux_version_bound(row.get("removedIn")):
            violations.append(f"invalid version bounds: {capability}")
        if capability in COMMAND_GATE_CAPABILITIES:
            command = row.get("tmuxCommand")
            flags = row.get("tmuxFlags")
            source_ids = row.get("pythonSourceSymbolIds")
            unsupported = row.get("unsupportedBehavior")
            policy_owners = row.get("policyOwnerComponents")
            policy_tests = row.get("wrapperPolicyTests")
            proof_contract = row.get("policyProofContract")
            introduced = row.get("introducedIn")
            removed = row.get("removedIn")
            expected_transition_versions = (
                {introduced, removed} - {"unknown"}
                if isinstance(introduced, str) and isinstance(removed, str)
                else set()
            )
            if capability == "break_pane_3_7_workaround":
                expected_transition_sources: object = {
                    version: (
                        f"https://github.com/tmux/tmux/blob/{version}/cmd-break-pane.c"
                    )
                    for version in sorted(expected_transition_versions)
                }
            elif capability == "refresh_client_clipboard_query":
                expected_transition_sources = {
                    "3.7": [
                        "https://github.com/tmux/tmux/blob/3.7/CHANGES",
                        ("https://github.com/tmux/tmux/blob/3.7/cmd-refresh-client.c"),
                    ]
                }
            else:
                expected_transition_sources = {
                    version: f"https://github.com/tmux/tmux/blob/{version}/tmux.1"
                    for version in sorted(expected_transition_versions)
                }
            command_gate_valid = (
                row.get("kind") == "command_gate"
                and feature_kind
                in {
                    "command",
                    "flag",
                    "flags",
                    "positional_arguments",
                    "removed_flag",
                    "semantic_transition",
                    "workaround",
                }
                and isinstance(command, str)
                and bool(command)
                and isinstance(flags, list)
                and all(
                    isinstance(flag, str) and flag.startswith("-") for flag in flags
                )
                and len(flags) == len(set(flags))
                and isinstance(source_ids, list)
                and bool(source_ids)
                and all(
                    isinstance(source_id, str) and ":" in source_id
                    for source_id in source_ids
                )
                and len(source_ids) == len(set(source_ids))
                and policy_owners == list(POLICY_OWNER_COMPONENTS.get(capability, ()))
                and policy_tests == list(POLICY_WRAPPER_TESTS[capability])
                and proof_contract == POLICY_PROOF_CONTRACTS[capability]
                # A flag older than the supported floor has no introduction
                # inside the supported range to name.
                and (introduced != "unknown" or feature_kind == "removed_flag")
                and row.get("tmuxTransitionSources") == expected_transition_sources
                and unsupported
                in {
                    "apply_only_in_affected_version",
                    "not_applicable_below_supported_floor",
                    "throw_unsupported_version",
                    "warn_and_ignore",
                }
                and (
                    (
                        feature_kind
                        in {
                            "flag",
                            "flags",
                            "removed_flag",
                            "semantic_transition",
                            "workaround",
                        }
                        and flags
                    )
                    or (
                        feature_kind in {"command", "positional_arguments"}
                        and not flags
                    )
                )
                and (
                    feature_kind in {"workaround", "removed_flag"}
                    or removed == "unknown"
                )
                # tmux mostly gains flags, so a row that records one going away
                # states both that it is gone and from when.
                and (
                    feature_kind != "removed_flag"
                    or (
                        introduced == "unknown"
                        and removed != "unknown"
                        and unsupported == "warn_and_ignore"
                    )
                )
                and (
                    feature_kind != "workaround"
                    or (
                        introduced == "3.7"
                        and removed == "3.7a"
                        and unsupported == "apply_only_in_affected_version"
                    )
                )
                and (
                    feature_kind != "command"
                    or unsupported == "throw_unsupported_version"
                )
                and (
                    feature_kind != "positional_arguments"
                    or unsupported == "warn_and_ignore"
                )
                and (
                    capability not in HOOK_SCOPE_CAPABILITIES
                    or (
                        row.get("supportRange") == "baseline"
                        and introduced == "3.2"
                        and removed == "unknown"
                        and unsupported == "not_applicable_below_supported_floor"
                    )
                )
                and (
                    capability in HOOK_SCOPE_CAPABILITIES
                    or unsupported != "not_applicable_below_supported_floor"
                )
                and (
                    feature_kind != "semantic_transition"
                    or (
                        capability == "refresh_client_clipboard_query"
                        and row.get("surfacePresentBy") == "3.2a"
                        and row.get("semanticTransition")
                        == {
                            "before": "query_with_optional_target_pane_forwarding",
                            "after": "query_and_store_buffer_only",
                        }
                        and introduced == "3.7"
                        and removed == "unknown"
                        and unsupported == "warn_and_ignore"
                    )
                )
                and (
                    capability != "break_pane_3_7_workaround"
                    or row.get("proofTmuxVersions") == ["3.7", "3.7a"]
                )
            )
            if not command_gate_valid:
                violations.append(f"invalid command gate: {capability}")
        if not is_real_server_test(row.get("namedRealServerTest")):
            violations.append(f"invalid real-server test: {capability}")
        if status == "verified":
            expected_cohort = (
                CLOSURE_COHORT
                if capability in COMMAND_GATE_CAPABILITIES
                else CAPABILITY_COHORT
            )
            expected_tests = (
                POLICY_WRAPPER_TESTS[capability]
                if capability in COMMAND_GATE_CAPABILITIES
                else None
            )
            if not _validate_reconciled_evidence(
                row.get("evidence"),
                expected_cohort=expected_cohort,
                expected_tests=expected_tests,
            ):
                violations.append(f"invalid reconciled evidence: {capability}")
    return violations


def _load_matrix(path: pathlib.Path) -> list[dict[str, t.Any]]:
    """Load newline-delimited matrix rows with precise failures.

    Examples
    --------
    >>> callable(_load_matrix)
    True
    """
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
        rows = [json.loads(line) for line in lines if line.strip()]
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        _fail("matrix evidence cannot be read", exception)
    if not rows or not all(isinstance(row, dict) for row in rows):
        _fail("matrix evidence is empty or invalid")
    return t.cast(list[dict[str, t.Any]], rows)


def _load_environment(path: pathlib.Path) -> dict[str, t.Any]:
    """Load and validate the source identity beside matrix results.

    Examples
    --------
    >>> callable(_load_environment)
    True
    """
    try:
        environment = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        _fail("matrix environment cannot be read", exception)
    if not isinstance(environment, dict):
        _fail("matrix environment schema is not exact")
    cohort = environment.get("capabilityCohort")
    expected_keys = (
        ENVIRONMENT_KEYS
        if cohort == CAPABILITY_COHORT
        else CLOSURE_ENVIRONMENT_KEYS
        if cohort == CLOSURE_COHORT
        else set()
    )
    if set(environment) != expected_keys:
        _fail("matrix environment schema is not exact")
    commit = environment["evaluatedCommit"]
    fingerprint = environment["sourceTreeFingerprint"]
    required_versions = _known_required_tmux_versions(environment["tmuxVersions"])
    if (
        not isinstance(commit, str)
        or COMMIT_PATTERN.fullmatch(commit) is None
        or environment["frameworks"] != list(REQUIRED_FRAMEWORKS)
        or cohort not in EVIDENCE_COHORT_TESTS
        or environment["includeMasterAdvisory"] is not False
        or environment["platform"] not in {"linux", "macos"}
        or environment["redactionProof"] is not True
        or environment["schemaVersion"] != 1
        or environment["sdkVersion"] != "10.0.302"
        or environment["sourceState"] not in {"clean", "uncommitted"}
        or not isinstance(fingerprint, str)
        or FINGERPRINT_PATTERN.fullmatch(fingerprint) is None
        or required_versions is None
    ):
        _fail("matrix environment observations are invalid")
    if cohort == CAPABILITY_COHORT:
        transition_commits = environment["transitionTmuxSourceCommits"]
        if (
            not isinstance(transition_commits, dict)
            or set(transition_commits) != {"3.7"}
            or not isinstance(transition_commits["3.7"], str)
            or COMMIT_PATTERN.fullmatch(transition_commits["3.7"]) is None
        ):
            _fail("matrix environment observations are invalid")
    return t.cast(dict[str, t.Any], environment)


def _inspect_source_environment(
    evidence_path: pathlib.Path,
    repository: pathlib.Path,
    matrix: dict[str, t.Any],
) -> dict[str, str]:
    """Verify evidence against its current clean or uncommitted source tree.

    Examples
    --------
    >>> callable(_inspect_source_environment)
    True
    """
    environment = _load_environment(evidence_path.with_name("environment.json"))
    if environment["evaluatedCommit"] != matrix["evaluatedCommit"]:
        _fail("environment commit differs from matrix")
    excluded_roots = [evidence_path.parent]
    try:
        current_fingerprint = source_tree_fingerprint(repository, excluded_roots)
        current_state = source_state(repository, excluded_roots)
    except BundleAssemblyError as exception:
        _fail("matrix source tree cannot be inspected", exception)
    if environment["sourceTreeFingerprint"] != current_fingerprint:
        _fail("source fingerprint differs from matrix environment")
    if environment["sourceState"] != current_state:
        _fail("source state differs from matrix environment")
    return {
        "capabilityCohort": t.cast(str, environment["capabilityCohort"]),
        "sourceState": t.cast(str, environment["sourceState"]),
        "sourceTreeFingerprint": t.cast(
            str,
            environment["sourceTreeFingerprint"],
        ),
    }


def _inspect_break_pane_transition(
    evidence_path: pathlib.Path,
    repository: pathlib.Path,
    matrix: dict[str, t.Any],
) -> dict[str, t.Any]:
    """Validate and normalize the exact 3.7 break-pane transition proof."""
    environment = _load_environment(evidence_path.with_name("environment.json"))
    if environment["evaluatedCommit"] != matrix["evaluatedCommit"]:
        _fail("transition environment commit differs from matrix")
    transcript = evidence_path.parent / BREAK_PANE_TRANSCRIPT
    try:
        resolved = transcript.resolve(strict=True)
        resolved.relative_to(repository.resolve())
        if transcript.is_symlink() or not resolved.is_file():
            _fail("break-pane transition transcript cannot be read")
        lines = resolved.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError, ValueError) as exception:
        _fail("break-pane transition transcript cannot be read", exception)
    observed: dict[tuple[str, str], tuple[str, str, str]] = {}
    for line in lines:
        match = BREAK_PANE_TRANSCRIPT_PATTERN.fullmatch(line)
        if match is None:
            _fail("break-pane transition transcript structure is invalid")
        pair = (match["framework"], match["version"])
        if pair in observed:
            _fail("break-pane transition transcript contains a duplicate record")
        observed[pair] = (
            match["source_commit"],
            match["workaround"],
            match["outcome"],
        )
    required = {
        (framework, version)
        for framework in REQUIRED_FRAMEWORKS
        for version in ("3.7", "3.7a")
    }
    if set(observed) != required:
        _fail("break-pane transition transcript lane coverage is invalid")
    transition_commits = t.cast(
        dict[str, str],
        environment["transitionTmuxSourceCommits"],
    )
    matrix_commits = t.cast(dict[str, str], matrix["tmuxSourceCommits"])
    for framework in REQUIRED_FRAMEWORKS:
        if observed[(framework, "3.7")] != (
            transition_commits["3.7"],
            "applied",
            "passed",
        ) or observed[(framework, "3.7a")] != (
            matrix_commits["3.7a"],
            "omitted",
            "passed",
        ):
            _fail("break-pane transition transcript source or outcome is invalid")
    relative_transcript = resolved.relative_to(repository.resolve()).as_posix()
    return {
        "transitionTmuxSourceCommits": transition_commits,
        "transitionTranscript": relative_transcript,
    }


def _inspect_matrix(path: pathlib.Path) -> dict[str, t.Any]:
    """Validate a full matrix and return normalized reconciliation fields.

    Examples
    --------
    >>> callable(_inspect_matrix)
    True
    """
    environment = _load_environment(path.with_name("environment.json"))
    required_versions = t.cast(
        "tuple[str, ...]",
        _known_required_tmux_versions(environment["tmuxVersions"]),
    )
    observed: dict[tuple[str, str], dict[str, t.Any]] = {}
    for row in _load_matrix(path):
        if set(row) != MATRIX_ROW_KEYS:
            _fail("matrix row schema is not exact")
        version = row["tmuxVersion"]
        framework = row["framework"]
        if (
            not isinstance(version, str)
            or not isinstance(framework, str)
            or version not in {*required_versions, "master"}
            or framework not in REQUIRED_FRAMEWORKS
        ):
            _fail("matrix contains an unknown row")
        pair = (version, framework)
        if pair in observed:
            _fail("matrix contains a duplicate row")
        evaluated_commit = row["evaluatedCommit"]
        if (
            not isinstance(evaluated_commit, str)
            or COMMIT_PATTERN.fullmatch(evaluated_commit) is None
        ):
            _fail("matrix evaluated commit is invalid")
        count = row["testCount"]
        source_commit = row["tmuxSourceCommit"]
        if version in required_versions:
            if (
                row["advisory"] is not False
                or row["status"] != "passed"
                or not isinstance(count, int)
                or isinstance(count, bool)
                or count <= 0
                or not isinstance(source_commit, str)
                or COMMIT_PATTERN.fullmatch(source_commit) is None
            ):
                _fail("required matrix observations are invalid")
        elif (
            row["advisory"] is not True
            or not isinstance(row["status"], str)
            or row["status"] not in {"passed", "failed"}
            or not isinstance(count, int)
            or isinstance(count, bool)
            or count < 0
            or (row["status"] == "passed" and count == 0)
            or (
                source_commit is not None
                and (
                    not isinstance(source_commit, str)
                    or COMMIT_PATTERN.fullmatch(source_commit) is None
                )
            )
            or (source_commit is None and (count != 0 or row["status"] != "failed"))
        ):
            _fail("master matrix observation is invalid")
        observed[pair] = row
    required = {
        (version, framework)
        for version in required_versions
        for framework in REQUIRED_FRAMEWORKS
    }
    if not required.issubset(observed):
        _fail("required matrix row is missing")
    required_rows = [observed[pair] for pair in sorted(required)]
    commits = {row["evaluatedCommit"] for row in required_rows}
    counts = {row["testCount"] for row in required_rows}
    if len(commits) != 1 or len(counts) != 1:
        _fail("required matrix observations are invalid")
    source_commits: dict[str, str] = {}
    for version in required_versions:
        version_commits = {
            t.cast(str, observed[(version, framework)]["tmuxSourceCommit"])
            for framework in REQUIRED_FRAMEWORKS
        }
        if len(version_commits) != 1:
            _fail("required framework rows have different source commits")
        source_commits[version] = version_commits.pop()
    master = [
        row for (version, _framework), row in observed.items() if version == "master"
    ]
    if master and (
        len(master) != len(REQUIRED_FRAMEWORKS)
        or len({row["tmuxSourceCommit"] for row in master}) != 1
        or {row["evaluatedCommit"] for row in master} != commits
    ):
        _fail("master matrix observations are incomplete or inconsistent")
    return {
        "evaluatedCommit": commits.pop(),
        "frameworks": list(REQUIRED_FRAMEWORKS),
        "testCount": counts.pop(),
        "tmuxSourceCommits": source_commits,
        "tmuxVersions": list(required_versions),
    }


def _read_evaluated_source(
    repository: pathlib.Path,
    commit: str,
    path: str,
    source_state_value: str,
) -> str:
    """Read one mapped test from its bound commit or worktree.

    Examples
    --------
    >>> callable(_read_evaluated_source)
    True
    """
    if source_state_value == "clean":
        try:
            return subprocess.run(
                ["git", "-C", str(repository), "show", f"{commit}:{path}"],
                check=True,
                capture_output=True,
                text=True,
            ).stdout
        except (OSError, subprocess.CalledProcessError) as exception:
            _fail(
                "mapped capability test is absent from the evaluated commit",
                exception,
            )
    repository = repository.resolve()
    candidate = repository / pathlib.PurePosixPath(path)
    try:
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(repository)
        if candidate.is_symlink() or not resolved.is_file():
            _fail("mapped capability test is not a regular worktree file")
        return resolved.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError, ValueError) as exception:
        _fail("mapped capability test is absent from the bound worktree", exception)


def _verify_capability_tests(
    repository: pathlib.Path,
    commit: str,
    tests: t.Iterable[str],
    source_state_value: str,
) -> None:
    """Require every mapped method in the exact evaluated commit.

    Examples
    --------
    >>> callable(_verify_capability_tests)
    True
    """
    for test in tests:
        path, separator, method = test.partition("::")
        if not separator or not method or not _is_relative_path(path):
            _fail("capability test identifier is invalid")
        source = _read_evaluated_source(
            repository,
            commit,
            path,
            source_state_value,
        )
        test_pattern = re.compile(
            rf"\[(?:UnixFact|Fact|Theory)(?:\([^\]\r\n]*\))?\]\s*"
            rf"(?:\[[^\]\r\n]+\]\s*)*"
            rf"\b(?:public|internal)\s+(?:async\s+)?(?:Task|void)\s+"
            rf"{re.escape(method)}\s*\("
        )
        if test_pattern.search(source) is None:
            _fail("mapped capability test is absent from the evaluated commit")


def capability_tests_for_evidence(
    evidence_path: pathlib.Path,
) -> t.Mapping[str, tuple[str, ...]]:
    """Return the explicit capability cohort for one decision bundle.

    Examples
    --------
    >>> callable(capability_tests_for_evidence)
    True
    """
    environment = _load_environment(evidence_path.with_name("environment.json"))
    return EVIDENCE_COHORT_TESTS.get(environment["capabilityCohort"], {})


def reconcile(
    document: dict[str, t.Any],
    evidence_path: pathlib.Path,
    *,
    repository: pathlib.Path = REPOSITORY_ROOT,
    document_path: pathlib.Path = DOCUMENT_PATH,
    capability_tests: t.Mapping[str, tuple[str, ...]] | None = None,
) -> dict[str, t.Any]:
    """Return an evidence-backed copy of the version document.

    Examples
    --------
    >>> callable(reconcile)
    True
    """
    violations = validate(document)
    if violations:
        _fail("; ".join(violations))
    try:
        relative_results = evidence_path.resolve().relative_to(repository.resolve())
    except ValueError as exception:
        _fail("matrix evidence must be repository-relative", exception)
    matrix = _inspect_matrix(evidence_path)
    source_environment = _inspect_source_environment(
        evidence_path,
        repository,
        matrix,
    )
    cohort = source_environment["capabilityCohort"]
    if cohort == CAPABILITY_COHORT:
        _inspect_break_pane_transition(
            evidence_path,
            repository,
            matrix,
        )
    content_fingerprint = source_content_fingerprint(
        repository,
        excluded_paths=[evidence_path.parent, document_path],
    )
    selected_tests = (
        capability_tests
        if capability_tests is not None
        else capability_tests_for_evidence(evidence_path)
    )
    if cohort == CAPABILITY_COHORT and set(selected_tests) & COMMAND_GATE_CAPABILITIES:
        _fail("command policy evidence must remain pending for capability cohort 0001")
    if cohort == CLOSURE_COHORT and dict(selected_tests) != POLICY_WRAPPER_TESTS:
        _fail("closure capability mapping is not exact")
    reconciled = json.loads(json.dumps(document))
    for row in reconciled["capabilities"]:
        capability = row["capability"]
        tests = selected_tests.get(capability)
        if tests is None:
            continue
        _verify_capability_tests(
            repository,
            t.cast(str, matrix["evaluatedCommit"]),
            tests,
            source_environment["sourceState"],
        )
        row["evidenceStatus"] = "verified"
        row_evidence = {
            **matrix,
            "capabilityCohort": cohort,
            "results": relative_results.as_posix(),
            "sourceContentFingerprint": content_fingerprint,
            **source_environment,
            "tests": list(tests),
        }
        row["evidence"] = row_evidence
    violations = validate(t.cast(dict[str, t.Any], reconciled))
    if violations:
        _fail("; ".join(violations))
    return t.cast(dict[str, t.Any], reconciled)


def _persisted_results_path(
    repository: pathlib.Path,
    value: object,
) -> pathlib.Path:
    """Resolve one safe persisted matrix path inside the repository.

    Examples
    --------
    >>> callable(_persisted_results_path)
    True
    """
    if not _is_relative_path(value):
        _fail("persisted matrix evidence path is invalid")
    repository = repository.resolve()
    candidate = repository / pathlib.PurePosixPath(t.cast(str, value))
    try:
        resolved = candidate.resolve(strict=True)
        resolved.relative_to(repository)
    except (OSError, ValueError) as exception:
        _fail("persisted matrix evidence cannot be read", exception)
    if candidate.is_symlink() or not resolved.is_file():
        _fail("persisted matrix evidence cannot be read")
    return resolved


def _validate_persisted_result_group(
    records: list[dict[str, t.Any]],
    *,
    repository: pathlib.Path,
    document_path: pathlib.Path,
) -> None:
    """Revalidate rows sharing one persisted result matrix.

    Examples
    --------
    >>> callable(_validate_persisted_result_group)
    True
    """
    evidence = t.cast(dict[str, t.Any], records[0]["evidence"])
    results = _persisted_results_path(repository, evidence["results"])
    try:
        matrix = _inspect_matrix(results)
    except VersionReconciliationError as exception:
        _fail("persisted matrix evidence cannot be read", exception)
    try:
        environment = _load_environment(results.with_name("environment.json"))
    except VersionReconciliationError as exception:
        _fail("persisted matrix environment cannot be read", exception)
    if environment["evaluatedCommit"] != matrix["evaluatedCommit"]:
        _fail("persisted matrix environment differs from results")
    cohort = t.cast(str, environment["capabilityCohort"])
    if cohort == CAPABILITY_COHORT:
        _inspect_break_pane_transition(
            results,
            repository,
            matrix,
        )
    for key in (
        "capabilityCohort",
        "evaluatedCommit",
        "frameworks",
        "testCount",
        "tmuxSourceCommits",
        "tmuxVersions",
    ):
        expected = cohort if key == "capabilityCohort" else matrix[key]
        if evidence[key] != expected:
            _fail("persisted matrix evidence differs from results")
    for key in ("sourceState", "sourceTreeFingerprint"):
        if evidence[key] != environment[key]:
            _fail("persisted matrix evidence differs from environment")
    current_content_fingerprint = source_content_fingerprint(
        repository,
        excluded_paths=[results.parent, document_path],
    )
    if evidence["sourceContentFingerprint"] != current_content_fingerprint:
        _fail("persisted source content fingerprint differs")
    verified_tests: set[tuple[str, ...]] = set()
    for row in records:
        row_evidence = t.cast(dict[str, t.Any], row["evidence"])
        capability = t.cast(str, row["capability"])
        expected_tests = EVIDENCE_COHORT_TESTS[cohort].get(capability)
        if cohort == CLOSURE_COHORT and (
            expected_tests is None or row_evidence["tests"] != list(expected_tests)
        ):
            _fail("persisted capability tests differ from the cohort contract")
        for key in (
            "capabilityCohort",
            "evaluatedCommit",
            "frameworks",
            "results",
            "sourceContentFingerprint",
            "sourceState",
            "sourceTreeFingerprint",
            "testCount",
            "tmuxSourceCommits",
            "tmuxVersions",
        ):
            if row_evidence[key] != evidence[key]:
                _fail("persisted evidence cohort metadata differs")
        tests = tuple(t.cast(list[str], row_evidence["tests"]))
        if tests in verified_tests:
            continue
        _verify_capability_tests(
            repository,
            t.cast(str, row_evidence["evaluatedCommit"]),
            tests,
            t.cast(str, environment["sourceState"]),
        )
        verified_tests.add(tests)


def _persisted_group_violation(
    records: list[dict[str, t.Any]],
    *,
    repository: pathlib.Path,
    document_path: pathlib.Path,
) -> str | None:
    """Return one persisted evidence violation without raising.

    Examples
    --------
    >>> callable(_persisted_group_violation)
    True
    """
    try:
        _validate_persisted_result_group(
            records,
            repository=repository,
            document_path=document_path,
        )
    except VersionReconciliationError as exception:
        return str(exception)
    return None


def validate_persisted_evidence(
    document: dict[str, t.Any],
    *,
    repository: pathlib.Path = REPOSITORY_ROOT,
    document_path: pathlib.Path = DOCUMENT_PATH,
) -> list[str]:
    """Revalidate persisted matrix files, source content, and mapped tests.

    Examples
    --------
    >>> validate_persisted_evidence({"capabilities": []})
    ['missing required capabilities']
    """
    violations = validate(document)
    if violations:
        return violations
    groups: dict[str, list[dict[str, t.Any]]] = {}
    for row in t.cast(list[dict[str, t.Any]], document["capabilities"]):
        if row["evidenceStatus"] != "verified":
            continue
        evidence = t.cast(dict[str, t.Any], row["evidence"])
        groups.setdefault(t.cast(str, evidence["results"]), []).append(row)
    for records in groups.values():
        message = _persisted_group_violation(
            records,
            repository=repository,
            document_path=document_path,
        )
        if message is not None and message not in violations:
            violations.append(message)
    return violations


def write_document(path: pathlib.Path, document: dict[str, t.Any]) -> None:
    """Atomically replace one deterministic JSON document.

    Examples
    --------
    >>> callable(write_document)
    True
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    candidate = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        candidate.write_text(
            json.dumps(document, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        candidate.replace(path)
    finally:
        candidate.unlink(missing_ok=True)


def parse_args(arguments: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse validation and reconciliation arguments.

    Examples
    --------
    >>> parse_args([]).evidence is None
    True
    >>> parse_args(["--evidence", "results.ndjson", "--write"]).write
    True
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=pathlib.Path)
    parser.add_argument("--write", action="store_true")
    parsed = parser.parse_args(arguments)
    if parsed.write and parsed.evidence is None:
        parser.error("--write requires --evidence")
    return parsed


def main(
    arguments: t.Sequence[str] | None = None,
    *,
    document_path: pathlib.Path = DOCUMENT_PATH,
    repository: pathlib.Path = REPOSITORY_ROOT,
    capability_tests: t.Mapping[str, tuple[str, ...]] | None = None,
) -> int:
    """Validate the ledger or reconcile supplied matrix evidence.

    Examples
    --------
    >>> callable(main)
    True
    """
    parsed = parse_args(arguments)
    try:
        document = load_document(document_path)
        if parsed.evidence is None:
            violations = validate_persisted_evidence(
                document,
                repository=repository,
                document_path=document_path,
            )
            if violations:
                _fail("; ".join(violations))
        else:
            reconciled = reconcile(
                document,
                parsed.evidence,
                repository=repository,
                document_path=document_path,
                capability_tests=capability_tests,
            )
            if parsed.write:
                write_document(document_path, reconciled)
                reconciled = load_document(document_path)
            violations = validate_persisted_evidence(
                reconciled,
                repository=repository,
                document_path=document_path,
            )
            if violations:
                _fail("; ".join(violations))
    except (OSError, json.JSONDecodeError, VersionReconciliationError) as exception:
        print(str(exception), file=sys.stderr)
        return 1
    else:
        return 0


if __name__ == "__main__":
    sys.exit(main())
