"""Regression tests for the generated parity support documents."""

from __future__ import annotations

import copy
import json
import pathlib
import runpy
import typing as t


def documentation_path(filename: str) -> pathlib.Path:
    """Return the generated parity documentation path.

    Examples
    --------
    >>> documentation_path("version-deltas.json").suffix
    '.json'
    """
    return pathlib.Path(__file__).parents[3] / "docs" / "parity" / filename


def load_document(filename: str) -> dict[str, t.Any]:
    """Load one generated parity support document.

    Examples
    --------
    >>> isinstance(load_document("version-deltas.json"), dict)
    True
    """
    with documentation_path(filename).open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def version_validator() -> t.Callable[[dict[str, t.Any]], list[str]]:
    """Load the version matrix validator without importing a package.

    Examples
    --------
    >>> validate = version_validator()
    >>> validate({"capabilities": []})
    ['missing required capabilities']
    """
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "reconcile_versions.py")
    )
    return t.cast(t.Callable[[dict[str, t.Any]], list[str]], namespace["validate"])


def ledger_validator() -> t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]]:
    """Load the ledger validator without importing a package.

    Examples
    --------
    >>> callable(ledger_validator())
    True
    """
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_ledger.py")
    )
    return t.cast(
        t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
        namespace["validate"],
    )


def error_policy_validator() -> t.Callable[
    [dict[str, t.Any], dict[str, t.Any]], list[str]
]:
    """Load the error-policy validator without importing a package.

    Examples
    --------
    >>> callable(error_policy_validator())
    True
    """
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_ledger.py")
    )
    return t.cast(
        t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
        namespace["validate_error_policies"],
    )


def test_neo_capabilities_are_internalized() -> None:
    """Record neo capabilities as internalized rather than omitting them."""
    ledger = load_document("parity-ledger.json")
    neo_rows = [row for row in ledger["rows"] if row["module"] == "libtmux.neo"]
    assert neo_rows
    assert {row["destinationStatus"] for row in neo_rows} == {"internalized"}


def test_version_deltas_cover_the_required_capabilities() -> None:
    """Seed all version-sensitive parity capabilities."""
    document = load_document("version-deltas.json")
    capabilities = {row["capability"] for row in document["capabilities"]}
    assert {
        "format_fields_and_operators",
        "control_notifications",
        "semicolon_grouping",
        "byte_length_framing",
        "attachment_accounting",
        "missing_target_format_safety",
        "option_dollar_double_escape",
    } <= capabilities


def test_error_policies_cover_lenient_and_tombstone_contracts() -> None:
    """Seed command error policies and compatibility contracts."""
    document = load_document("error-policies.json")
    policy_names = {row["name"] for row in document["policies"]}
    assert policy_names == {
        "command_specific_errors",
        "display_message_stderr",
        "has_session",
        "list_accessors",
        "liveness",
        "missing_daemon_commands",
        "non_suppressible_errors",
        "option_failures",
        "warning_aliases",
        "raising_tombstones",
    }


def test_error_policies_freeze_command_specific_behavior() -> None:
    """Keep tmux failures distinct where callers observe different outcomes."""
    policies = {
        policy["name"]: policy
        for policy in load_document("error-policies.json")["policies"]
    }

    assert policies["display_message_stderr"]["mappings"] == [
        {
            "csharpMemberId": (
                "M:LibTmux.Pane.DisplayMessageAsync("
                "DisplayMessageRequest,CancellationToken)"
            ),
            "disposition": "log_warning_and_return",
            "logLevel": "Warning",
            "sourceSymbolId": "libtmux.pane:Pane.display_message",
            "tmuxCommand": "display-message",
        },
        {
            "csharpMemberId": (
                "M:LibTmux.Server.DisplayMessageAsync("
                "DisplayMessageRequest,CancellationToken)"
            ),
            "disposition": "log_warning_and_return",
            "logLevel": "Warning",
            "sourceSymbolId": "libtmux.server:Server.display_message",
            "tmuxCommand": "display-message",
        },
        {
            "csharpMemberId": (
                "M:LibTmux.Window.DisplayMessageAsync("
                "DisplayMessageRequest,CancellationToken)"
            ),
            "disposition": "log_warning_and_return",
            "logLevel": "Warning",
            "sourceSymbolId": "libtmux.window:Window.display_message",
            "tmuxCommand": "display-message",
        },
    ]
    assert policies["liveness"]["mappings"] == [
        {
            "csharpMemberId": "M:LibTmux.Server.IsAliveAsync(CancellationToken)",
            "disposition": "return_false",
            "sourceSymbolId": "libtmux.server:Server.is_alive",
            "suppressedFailures": [
                "T:LibTmux.TmuxCommandException",
                "T:LibTmux.TmuxCommandNotFoundException",
                "T:LibTmux.TmuxTransportException",
            ],
            "tmuxCommand": "list-sessions",
        },
        {
            "csharpMemberId": "M:LibTmux.Server.RaiseIfDeadAsync(CancellationToken)",
            "disposition": "throw",
            "sourceSymbolId": "libtmux.server:Server.raise_if_dead",
            "thrownFailures": [
                "T:LibTmux.TmuxCommandException",
                "T:LibTmux.TmuxCommandNotFoundException",
                "T:LibTmux.TmuxTransportException",
            ],
            "tmuxCommand": "list-sessions",
        },
    ]
    assert policies["has_session"]["mappings"] == [
        {
            "csharpMemberId": (
                "M:LibTmux.Server.HasSessionAsync(string,bool,CancellationToken)"
            ),
            "exitCodeDisposition": "zero_true_nonzero_false",
            "sourceSymbolId": "libtmux.server:Server.has_session",
            "tmuxCommand": "has-session",
            "transportFailureDisposition": "throw",
        },
    ]
    assert policies["missing_daemon_commands"]["mappings"] == [
        {
            "csharpMemberId": "M:LibTmux.Server.KillAsync(CancellationToken)",
            "missingDaemonDisposition": "return_success",
            "otherFailureDisposition": "throw",
            "sourceSymbolId": "libtmux.server:Server.kill",
            "tmuxCommand": "kill-server",
        },
        {
            "csharpMemberId": (
                "M:LibTmux.Server.KillSessionAsync(string,CancellationToken)"
            ),
            "missingDaemonDisposition": "throw",
            "otherFailureDisposition": "throw",
            "sourceSymbolId": "libtmux.server:Server.kill_session",
            "tmuxCommand": "kill-session",
        },
        {
            "csharpMemberId": (
                "M:LibTmux.Session.KillAsync(bool,bool,bool,CancellationToken)"
            ),
            "missingDaemonDisposition": "throw",
            "otherFailureDisposition": "throw",
            "sourceSymbolId": "libtmux.session:Session.kill",
            "tmuxCommand": "kill-session",
        },
    ]
    assert policies["non_suppressible_errors"] == {
        "appliesTo": [
            "has_session",
            "list_accessors",
            "liveness",
            "missing_daemon_commands",
        ],
        "disposition": "propagate",
        "exceptionTypes": [
            "T:System.ArgumentException",
            "T:System.InvalidOperationException",
            "T:System.NotSupportedException",
            "T:System.OperationCanceledException",
        ],
        "name": "non_suppressible_errors",
    }


def test_list_error_policies_are_member_specific() -> None:
    """Freeze empty, missing-daemon-only, and loud list behavior by member."""
    policies = {
        policy["name"]: policy
        for policy in load_document("error-policies.json")["policies"]
    }
    mappings = policies["list_accessors"]["mappings"]
    actual = {
        (row["sourceSymbolId"], row["csharpMemberId"]): (
            tuple(row["tmuxCommands"]),
            row["failureDisposition"],
        )
        for row in mappings
    }
    assert actual == {
        (
            "libtmux.server:Server.attached_sessions",
            "M:LibTmux.Server.GetAttachedSessionsAsync(CancellationToken)",
        ): (("list-sessions",), "return_empty_on_any_list_failure"),
        (
            "libtmux.server:Server.clients",
            "M:LibTmux.Server.GetClientsAsync(CancellationToken)",
        ): (("list-clients",), "return_empty_on_any_list_failure"),
        (
            "libtmux.server:Server.panes",
            "M:LibTmux.Server.GetPanesAsync(CancellationToken)",
        ): (("list-panes",), "return_empty_on_missing_daemon_or_socket"),
        (
            "libtmux.server:Server.search_panes",
            ("M:LibTmux.Server.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)"),
        ): (("list-panes",), "throw"),
        (
            "libtmux.server:Server.search_sessions",
            (
                "M:LibTmux.Server.SearchSessionsAsync("
                "UnsafeTmuxFilter,CancellationToken)"
            ),
        ): (("list-sessions",), "throw"),
        (
            "libtmux.server:Server.search_windows",
            ("M:LibTmux.Server.SearchWindowsAsync(UnsafeTmuxFilter,CancellationToken)"),
        ): (("list-windows",), "throw"),
        (
            "libtmux.server:Server.sessions",
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)",
        ): (("list-sessions",), "return_empty_on_any_list_failure"),
        (
            "libtmux.server:Server.windows",
            "M:LibTmux.Server.GetWindowsAsync(CancellationToken)",
        ): (("list-windows",), "return_empty_on_missing_daemon_or_socket"),
        (
            "libtmux.session:Session.panes",
            "M:LibTmux.Session.GetPanesAsync(CancellationToken)",
        ): (("list-panes",), "throw"),
        (
            "libtmux.session:Session.search_panes",
            ("M:LibTmux.Session.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)"),
        ): (("list-panes",), "throw"),
        (
            "libtmux.session:Session.search_windows",
            (
                "M:LibTmux.Session.SearchWindowsAsync("
                "UnsafeTmuxFilter,CancellationToken)"
            ),
        ): (("list-windows",), "throw"),
        (
            "libtmux.session:Session.windows",
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)",
        ): (("list-windows",), "throw"),
        (
            "libtmux.window:Window.linked_sessions",
            "M:LibTmux.Window.GetLinkedSessionsAsync(CancellationToken)",
        ): (
            ("list-windows", "list-sessions"),
            "return_empty_if_either_list_fails",
        ),
        (
            "libtmux.window:Window.panes",
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)",
        ): (("list-panes",), "throw"),
        (
            "libtmux.window:Window.search_panes",
            ("M:LibTmux.Window.SearchPanesAsync(UnsafeTmuxFilter,CancellationToken)"),
        ): (("list-panes",), "throw"),
    }


def test_option_failures_converge_on_one_typed_exception() -> None:
    """Classify Python option stderr while exposing one stable C# exception."""
    policy = next(
        row
        for row in load_document("error-policies.json")["policies"]
        if row["name"] == "option_failures"
    )

    assert policy == {
        "commands": ["set-hook", "set-option", "show-hooks", "show-options"],
        "csharpExceptionId": "T:LibTmux.TmuxOptionException",
        "csharpHandlerId": (
            "M:LibTmux.Internal.OptionFailure.ThrowIfFailed(TmuxCommandResult,string)"
        ),
        "mappings": [
            {
                "match": "unknown option",
                "pythonErrorSymbolId": "libtmux.exc:UnknownOption",
            },
            {
                "match": "invalid option",
                "pythonErrorSymbolId": "libtmux.exc:InvalidOption",
            },
            {
                "match": "ambiguous option",
                "pythonErrorSymbolId": "libtmux.exc:AmbiguousOption",
            },
            {"match": "fallback", "pythonErrorSymbolId": "libtmux.exc:OptionError"},
        ],
        "name": "option_failures",
        "pythonHandlerSymbolId": "libtmux.options:handle_option_error",
    }


def test_inventory_preserves_compatibility_and_test_helper_symbols() -> None:
    """Inventory public helpers and explicit compatibility behaviors."""
    document = load_document("python-public-api.json")
    symbols = document["symbols"]
    kinds = {symbol["kind"] for symbol in symbols}
    helper_modules = {symbol["module"] for symbol in symbols}
    assert {"raising_tombstone", "warning_alias"} <= kinds
    assert {
        "libtmux.test.random",
        "libtmux.test.retry",
        "libtmux.test.temporary",
        "libtmux.pytest_plugin",
    } <= helper_modules


def test_error_policies_have_source_grounded_symbol_mappings() -> None:
    """Map concrete source symbols to error and compatibility policies."""
    policies = {
        policy["name"]: policy
        for policy in load_document("error-policies.json")["policies"]
    }
    mappings = policies["command_specific_errors"]["mappings"]
    assert mappings
    assert {
        "tmuxCommand",
        "sourceSymbolId",
        "errorSymbolId",
    } <= set(mappings[0])
    list_symbols = {
        mapping["sourceSymbolId"] for mapping in policies["list_accessors"]["mappings"]
    }
    assert {
        "libtmux.server:Server.attached_sessions",
        "libtmux.server:Server.clients",
        "libtmux.server:Server.sessions",
    } <= list_symbols
    assert policies["warning_aliases"]["symbolIds"] == [
        "libtmux.window:Window.set_window_option",
        "libtmux.window:Window.show_window_option",
        "libtmux.window:Window.show_window_options",
    ]
    inventory = load_document("python-public-api.json")
    raising_tombstones = sorted(
        row["id"] for row in inventory["symbols"] if row["kind"] == "raising_tombstone"
    )
    assert policies["raising_tombstones"]["symbolIds"] == raising_tombstones


def test_error_policy_csharp_ids_exist_in_the_frozen_public_api() -> None:
    """Keep every policy destination bound to a reviewed C# API member or type."""
    public_api_path = documentation_path("error-policies.json").parent.parent / (
        "public-api.json"
    )
    public_api = json.loads(public_api_path.read_text(encoding="utf-8"))
    public_ids = {
        row["id"] for section in ("types", "members") for row in public_api[section]
    }
    references: set[str] = set()
    for policy in load_document("error-policies.json")["policies"]:
        for key in ("csharpExceptionId", "csharpHandlerId", "csharpMemberId"):
            if key in policy:
                references.add(policy[key])
        for mapping in policy.get("mappings", []):
            for key in ("csharpExceptionId", "csharpHandlerId", "csharpMemberId"):
                if key in mapping:
                    references.add(mapping[key])

    assert references <= public_ids


def test_version_deltas_target_the_planned_production_suite() -> None:
    """Keep compatibility evidence bound to production tests that will exist."""
    rows = load_document("version-deltas.json")["capabilities"]
    assert {row["evidenceStatus"] for row in rows} <= {"pending", "verified"}
    assert all(
        row["namedRealServerTest"].startswith(
            "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs::"
        )
        for row in rows
    )


def test_wrapper_policy_rows_stay_pending_until_their_owner_lands() -> None:
    """Separate cohort-verified protocol observations from wrapper-policy rows."""
    rows = load_document("version-deltas.json")["capabilities"]
    policy_rows = [row for row in rows if "policyOwnerComponents" in row]
    protocol_rows = [row for row in rows if "policyOwnerComponents" not in row]

    assert policy_rows
    assert protocol_rows
    assert all(row["evidenceStatus"] == "pending" for row in policy_rows)
    assert all("evidence" not in row for row in policy_rows)
    assert all(
        row["evidence"]["capabilityCohort"]
        for row in protocol_rows
        if row["evidenceStatus"] == "verified"
    )


def test_ledger_destinations_are_approved_internal_or_excluded() -> None:
    """Freeze exact destinations without claiming production implementation."""
    rows = load_document("parity-ledger.json")["rows"]
    approved = [row for row in rows if row["destinationStatus"] == "approved"]
    internalized = [row for row in rows if row["destinationStatus"] == "internalized"]
    excluded = [row for row in rows if row["destinationStatus"] == "excluded"]
    assert approved
    assert internalized
    assert excluded
    assert all(row["csharpDestination"] for row in approved + internalized)
    assert all(row["csharpDestination"] is None for row in excluded)
    assert all(row["exclusionReason"] and row["replacement"] for row in excluded)
    assert {row["componentId"] for row in rows} == set(range(1, 19))


def test_c4_owns_canonical_window_and_pane_lookup_materialization() -> None:
    """Bind ID lookup to the projection/query/materialization component."""
    rows = {
        row["pythonSymbolId"]: row
        for row in load_document("parity-ledger.json")["rows"]
    }
    moved = {
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.window:Window.from_window_id",
    }
    assert {
        symbol_id: {
            "componentId": rows[symbol_id]["componentId"],
            "testPath": rows[symbol_id]["testPath"],
        }
        for symbol_id in moved
    } == {
        symbol_id: {
            "componentId": 4,
            "testPath": (
                "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
            ),
        }
        for symbol_id in moved
    }
    assert sum(row["componentId"] == 2 for row in rows.values()) == 9
    assert sum(row["componentId"] == 4 for row in rows.values()) == 197


def test_raising_property_tombstones_have_exact_replacements() -> None:
    """Exclude raising-only properties in favor of their reviewed C# APIs."""
    expected_replacements = {
        "libtmux.server:Server._sessions": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.server:Server.children": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.session:Session._windows": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.session:Session.attached_pane": (
            "(await session.RefreshAsync(cancellationToken)).ActivePane"
        ),
        "libtmux.session:Session.attached_window": (
            "(await session.RefreshAsync(cancellationToken)).ActiveWindow"
        ),
        "libtmux.session:Session.children": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.window:Window._panes": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.window:Window.attached_pane": (
            "(await window.RefreshAsync(cancellationToken)).ActivePane"
        ),
        "libtmux.window:Window.children": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
    }
    rows = {
        row["pythonSymbolId"]: row
        for row in load_document("parity-ledger.json")["rows"]
        if row["pythonSymbolId"] in expected_replacements
    }

    assert set(rows) == set(expected_replacements)
    for symbol_id, replacement in expected_replacements.items():
        assert rows[symbol_id]["behavior"] == (
            "Preserve raising_tombstone " + symbol_id.split(":", 1)[1]
        )
        assert rows[symbol_id]["destinationStatus"] == "excluded"
        assert rows[symbol_id]["csharpDestination"] is None
        assert rows[symbol_id]["exclusionReason"] == (
            "The Python property exists only to raise DeprecatedError."
        )
        assert rows[symbol_id]["replacement"] == replacement


def test_raising_method_tombstones_have_exact_replacements() -> None:
    """Replace every raising-only method with one exact C# API or idiom."""
    matching = (
        "M:LibTmux.Query.QueryExtensions.Matching``1("
        "IEnumerable<T>,Expression<Func<T,bool>>)"
    )
    expected_replacements = {
        "libtmux.pane:Pane.__getitem__": (
            "typed Pane property, otherwise P:LibTmux.Pane.RawFormatFields"
        ),
        "libtmux.pane:Pane.get": (
            "typed Pane property, otherwise P:LibTmux.Pane.RawFormatFields"
        ),
        "libtmux.pane:Pane.resize_pane": (
            "M:LibTmux.Pane.ResizeAsync(ResizePaneRequest,CancellationToken)"
        ),
        "libtmux.pane:Pane.select_pane": (
            "M:LibTmux.Pane.SelectAsync(SelectPaneRequest?,CancellationToken)"
        ),
        "libtmux.pane:Pane.split_window": (
            "M:LibTmux.Pane.SplitAsync(SplitPaneRequest?,CancellationToken)"
        ),
        "libtmux.server:Server._list_panes": (
            "M:LibTmux.Server.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.server:Server._list_sessions": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.server:Server._list_windows": (
            "M:LibTmux.Server.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.server:Server._update_panes": (
            "M:LibTmux.Server.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.server:Server._update_windows": (
            "M:LibTmux.Server.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.server:Server.find_where": "Matching(predicate).SingleOrDefault()",
        "libtmux.server:Server.get_by_id": (
            "M:LibTmux.Server.GetSessionAsync(SessionId,CancellationToken)"
        ),
        "libtmux.server:Server.kill_server": (
            "M:LibTmux.Server.KillAsync(CancellationToken)"
        ),
        "libtmux.server:Server.list_sessions": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.server:Server.where": matching,
        "libtmux.session:Session.__getitem__": (
            "typed Session property, otherwise P:LibTmux.Session.RawFormatFields"
        ),
        "libtmux.session:Session._list_windows": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.session:Session.attach_session": (
            "M:LibTmux.Session.AttachAsync(AttachSessionRequest?,CancellationToken)"
        ),
        "libtmux.session:Session.find_where": ("Matching(predicate).SingleOrDefault()"),
        "libtmux.session:Session.get": (
            "typed Session property, otherwise P:LibTmux.Session.RawFormatFields"
        ),
        "libtmux.session:Session.get_by_id": (
            "M:LibTmux.Session.GetWindowAsync(string,CancellationToken)"
        ),
        "libtmux.session:Session.kill_session": (
            "M:LibTmux.Session.KillAsync(bool,bool,bool,CancellationToken)"
        ),
        "libtmux.session:Session.list_windows": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.session:Session.where": matching,
        "libtmux.window:Window.__getitem__": (
            "typed Window property, otherwise P:LibTmux.Window.RawFormatFields"
        ),
        "libtmux.window:Window._list_panes": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.window:Window.find_where": "Matching(predicate).SingleOrDefault()",
        "libtmux.window:Window.get": (
            "typed Window property, otherwise P:LibTmux.Window.RawFormatFields"
        ),
        "libtmux.window:Window.get_by_id": (
            "M:LibTmux.Window.GetPaneAsync(string,CancellationToken)"
        ),
        "libtmux.window:Window.kill_window": (
            "M:LibTmux.Window.KillAsync(bool,CancellationToken)"
        ),
        "libtmux.window:Window.list_panes": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.window:Window.select_window": (
            "M:LibTmux.Window.SelectAsync(CancellationToken)"
        ),
        "libtmux.window:Window.split_window": (
            "M:LibTmux.Window.SplitPaneAsync(SplitPaneRequest?,CancellationToken)"
        ),
        "libtmux.window:Window.where": matching,
    }
    rows = {
        row["pythonSymbolId"]: row
        for row in load_document("parity-ledger.json")["rows"]
        if row["pythonSymbolId"] in expected_replacements
    }

    assert set(rows) == set(expected_replacements)
    for symbol_id, replacement in expected_replacements.items():
        assert rows[symbol_id]["destinationStatus"] == "excluded"
        assert rows[symbol_id]["csharpDestination"] is None
        assert rows[symbol_id]["exclusionReason"] == (
            "The Python member exists only to raise DeprecatedError."
        )
        assert rows[symbol_id]["replacement"] == replacement


def test_ledger_validator_rejects_incorrect_destinations() -> None:
    """Reject missing approved destinations and malformed exclusions."""
    inventory = load_document("python-public-api.json")
    ledger = copy.deepcopy(load_document("parity-ledger.json"))
    approved = next(
        row for row in ledger["rows"] if row["destinationStatus"] == "approved"
    )
    approved["csharpDestination"] = None
    excluded = next(
        row for row in ledger["rows"] if row["destinationStatus"] == "excluded"
    )
    excluded["csharpDestination"] = "T:LibTmux.Server"

    violations = ledger_validator()(inventory, ledger)

    assert f"missing approved destination: {approved['pythonSymbolId']}" in violations
    assert f"invalid excluded destination: {excluded['pythonSymbolId']}" in violations


def test_ledger_validator_rejects_unknown_status_and_component() -> None:
    """Reject disposition drift and rows outside the frozen component map."""
    inventory = load_document("python-public-api.json")
    ledger = copy.deepcopy(load_document("parity-ledger.json"))
    row = ledger["rows"][0]
    row["destinationStatus"] = "maybe"
    row["componentId"] = 19

    violations = ledger_validator()(inventory, ledger)

    assert f"unexpected destination status: {row['pythonSymbolId']}" in violations
    assert f"invalid component ID: {row['pythonSymbolId']}" in violations


def test_error_policy_validator_rejects_unknown_symbol_references() -> None:
    """Reject policy mappings that are not grounded in inventoried symbols."""
    inventory = load_document("python-public-api.json")
    policies = copy.deepcopy(load_document("error-policies.json"))
    policies["policies"][0]["mappings"][0]["sourceSymbolId"] = "libtmux:missing"

    violations = error_policy_validator()(policies, inventory)

    assert "unknown policy symbol: libtmux:missing" in violations


def test_error_policy_validator_rejects_unknown_csharp_references() -> None:
    """Reject policy destinations absent from the frozen C# contract."""
    inventory = load_document("python-public-api.json")
    policies = copy.deepcopy(load_document("error-policies.json"))
    policies["policies"][1]["mappings"][0]["csharpMemberId"] = (
        "M:LibTmux.Missing.Member()"
    )

    violations = error_policy_validator()(policies, inventory)

    assert "unknown C# policy ID: M:LibTmux.Missing.Member()" in violations


def test_error_policy_grounding_is_symbol_body_specific() -> None:
    """Reject a same-module method that does not implement the mapped command."""
    inventory = load_document("python-public-api.json")
    policies = copy.deepcopy(load_document("error-policies.json"))
    policies["policies"][0]["mappings"][0]["sourceSymbolId"] = (
        "libtmux.server:Server.has_session"
    )

    violations = error_policy_validator()(policies, inventory)

    assert "ungrounded command mapping: attach-session" in violations


def test_error_policy_validator_rejects_incomplete_tombstone_catalog() -> None:
    """Require the compatibility policy to cover every raising-only symbol."""
    inventory = load_document("python-public-api.json")
    policies = copy.deepcopy(load_document("error-policies.json"))
    policy = next(
        row for row in policies["policies"] if row["name"] == "raising_tombstones"
    )
    policy["symbolIds"] = policy["symbolIds"][:1]

    violations = error_policy_validator()(policies, inventory)

    assert "invalid policy symbols: raising_tombstones" in violations


def test_error_policy_validator_rejects_semantic_policy_drift() -> None:
    """Reject broad suppression and display-message escalation changes."""
    inventory = load_document("python-public-api.json")
    policies = copy.deepcopy(load_document("error-policies.json"))
    by_name = {row["name"]: row for row in policies["policies"]}
    by_name["list_accessors"]["mappings"][0]["failureDisposition"] = (
        "return_empty_on_every_exception"
    )
    by_name["display_message_stderr"]["mappings"][0]["disposition"] = "throw"
    by_name["non_suppressible_errors"]["exceptionTypes"].remove(
        "T:System.OperationCanceledException"
    )

    violations = error_policy_validator()(policies, inventory)

    assert "invalid list policy" in violations
    assert "invalid display-message policy" in violations
    assert "invalid non-suppressible policy" in violations


def test_version_validator_rejects_malformed_contract_values() -> None:
    """Reject malformed endpoints, bounds, and real-server test identifiers."""
    document = copy.deepcopy(load_document("version-deltas.json"))
    row = document["capabilities"][0]
    row["tmuxSourceEndpoints"]["3.2a"] = "https://example.invalid/tmux"
    row["introducedIn"] = "not-a-tmux-version"
    row["namedRealServerTest"] = "VersionParityTests"

    violations = version_validator()(document)

    assert "invalid source endpoints: attachment_accounting" in violations
    assert "invalid version bounds: attachment_accounting" in violations
    assert "invalid real-server test: attachment_accounting" in violations


def test_version_validator_rejects_inexact_command_gate() -> None:
    """Reject command gates that lose source, fallback, or exact version data."""
    document = copy.deepcopy(load_document("version-deltas.json"))
    row = next(
        item for item in document["capabilities"] if item.get("kind") == "command_gate"
    )
    row["unsupportedBehavior"] = "guess"
    row["pythonSourceSymbolIds"] = []
    row["introducedIn"] = "unknown"

    violations = version_validator()(document)

    assert f"invalid command gate: {row['capability']}" in violations
