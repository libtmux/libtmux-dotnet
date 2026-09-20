"""Verify the generated inventory and parity ledger contract."""

from __future__ import annotations

import ast
import copy
import functools
import json
import pathlib
import subprocess
import sys
import typing as t

REPOSITORY_ROOT = pathlib.Path(__file__).parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from eng.parity import python_source  # noqa: E402

DOCUMENT_ROOT = pathlib.Path(__file__).parents[2] / "docs" / "parity"
CSHARP_INVENTORY_PATH = DOCUMENT_ROOT.parent.parent / "artifacts/api-inventory.json"
GENERATOR_PATH = pathlib.Path(__file__).with_name("generate_inventory.py")
SOURCE_URL_PREFIX = python_source.BLOB_URL_PREFIX
DESTINATION_STATUSES = {"approved", "internalized", "excluded"}
COMPONENT_IDS = set(range(1, 19))
LENIENT_ACCESSOR_IDS = {
    "libtmux.server:Server.attached_sessions",
    "libtmux.server:Server.clients",
    "libtmux.server:Server.sessions",
}
LIST_POLICY_IDS = {
    "libtmux.server:Server.attached_sessions",
    "libtmux.server:Server.clients",
    "libtmux.server:Server.panes",
    "libtmux.server:Server.search_panes",
    "libtmux.server:Server.search_sessions",
    "libtmux.server:Server.search_windows",
    "libtmux.server:Server.sessions",
    "libtmux.server:Server.windows",
    "libtmux.session:Session.panes",
    "libtmux.session:Session.search_panes",
    "libtmux.session:Session.search_windows",
    "libtmux.session:Session.windows",
    "libtmux.window:Window.linked_sessions",
    "libtmux.window:Window.panes",
    "libtmux.window:Window.search_panes",
}
LIST_DISPOSITIONS = {
    "return_empty_if_either_list_fails",
    "return_empty_on_any_list_failure",
    "return_empty_on_missing_daemon_or_socket",
    "throw",
}
DISPLAY_MESSAGE_IDS = {
    "libtmux.pane:Pane.display_message",
    "libtmux.server:Server.display_message",
    "libtmux.window:Window.display_message",
}
LIVENESS_IDS = {
    "libtmux.server:Server.is_alive",
    "libtmux.server:Server.raise_if_dead",
}
MISSING_DAEMON_IDS = {
    "libtmux.server:Server.kill",
    "libtmux.server:Server.kill_session",
    "libtmux.session:Session.kill",
}
NON_SUPPRESSIBLE_TYPES = [
    "T:System.ArgumentException",
    "T:System.InvalidOperationException",
    "T:System.NotSupportedException",
    "T:System.OperationCanceledException",
]
OPTION_ERROR_IDS = [
    ("unknown option", "libtmux.exc:UnknownOption"),
    ("invalid option", "libtmux.exc:InvalidOption"),
    ("ambiguous option", "libtmux.exc:AmbiguousOption"),
    ("fallback", "libtmux.exc:OptionError"),
]
WARNING_ALIAS_IDS = [
    "libtmux.window:Window.set_window_option",
    "libtmux.window:Window.show_window_option",
    "libtmux.window:Window.show_window_options",
]


def load_document(filename: str) -> dict[str, t.Any]:
    """Load a generated parity JSON document.

    Examples
    --------
    >>> isinstance(load_document("parity-ledger.json"), dict)
    True
    """
    with (DOCUMENT_ROOT / filename).open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def approval_snapshot(ledger: dict[str, t.Any]) -> dict[str, t.Any]:
    """Return a ledger copy with progressive production claims removed.

    This validator owns the frozen approval contract, so it reads dispositions
    from a snapshot rather than the progressive statuses that the phase-aware
    plan validator owns.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Current parity ledger.

    Returns
    -------
    dict[str, typing.Any]
        Deep-copied approval snapshot.

    Examples
    --------
    >>> source = {"rows": [{"evidenceStatus": "verified"}]}
    >>> approval_snapshot(source)["rows"][0]["implementationStatus"]
    'not_started'
    >>> source["rows"][0]["evidenceStatus"]
    'verified'
    """
    snapshot = copy.deepcopy(ledger)
    for row in t.cast(list[dict[str, t.Any]], snapshot.get("rows", [])):
        row["implementationStatus"] = "not_started"
        row["evidenceStatus"] = "none"
    return snapshot


def validate(inventory: dict[str, t.Any], ledger: dict[str, t.Any]) -> list[str]:
    """Return parity ledger contract violations.

    Examples
    --------
    An empty inventory satisfies the per-row contracts but reports every
    whole-inventory contract it cannot demonstrate.

    >>> for violation in validate({"symbols": []}, {"rows": []}):
    ...     print(violation)
    neo capabilities are not internalized
    raising tombstones are not inventoried
    warning aliases are not inventoried
    public test helpers are not inventoried
    lenient list accessors are not inventoried
    """
    symbols = t.cast(list[dict[str, str]], inventory.get("symbols", []))
    rows = t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
    inventory_ids = {symbol["id"] for symbol in symbols}
    ledger_ids = [row["pythonSymbolId"] for row in rows]
    violations: list[str] = []
    if set(ledger_ids) != inventory_ids or len(ledger_ids) != len(set(ledger_ids)):
        violations.append("inventory and ledger IDs differ")
    for row in rows:
        required = {
            "pythonSymbolId",
            "sourceUrl",
            "component",
            "componentId",
            "behavior",
            "tmuxVersions",
            "destinationStatus",
            "implementationStatus",
            "csharpDestination",
            "testPath",
            "evidenceStatus",
        }
        missing = required - set(row)
        if missing:
            violations.append(
                f"missing ledger fields: {row.get('pythonSymbolId', '<unknown>')}"
            )
        destination_status = row.get("destinationStatus")
        if destination_status not in DESTINATION_STATUSES:
            violations.append(f"unexpected destination status: {row['pythonSymbolId']}")
        if row.get("componentId") not in COMPONENT_IDS:
            violations.append(f"invalid component ID: {row['pythonSymbolId']}")
        destination = row.get("csharpDestination")
        if destination_status in {"approved", "internalized"} and not (
            isinstance(destination, str) and destination
        ):
            violations.append(
                f"missing {destination_status} destination: {row['pythonSymbolId']}"
            )
        if (
            destination_status == "internalized"
            and isinstance(destination, str)
            and ":LibTmux.Internal." not in destination
        ):
            violations.append(
                f"public internalized destination: {row['pythonSymbolId']}"
            )
        if destination_status == "excluded":
            if destination is not None:
                violations.append(
                    f"invalid excluded destination: {row['pythonSymbolId']}"
                )
            if not isinstance(row.get("exclusionReason"), str) or not row.get(
                "exclusionReason"
            ):
                violations.append(f"missing exclusion reason: {row['pythonSymbolId']}")
            if not isinstance(row.get("replacement"), str) or not row.get(
                "replacement"
            ):
                violations.append(
                    f"missing exclusion replacement: {row['pythonSymbolId']}"
                )
        if row.get("implementationStatus") != "not_started":
            violations.append(
                f"unexpected implementation status: {row['pythonSymbolId']}"
            )
        if row.get("evidenceStatus") != "none":
            violations.append(f"unexpected evidence status: {row['pythonSymbolId']}")
    neo_rows = [row for row in rows if row.get("module") == "libtmux.neo"]
    if not neo_rows or any(
        row.get("destinationStatus") != "internalized" for row in neo_rows
    ):
        violations.append("neo capabilities are not internalized")
    symbol_kinds = {symbol["id"]: symbol["kind"] for symbol in symbols}
    if "raising_tombstone" not in set(symbol_kinds.values()):
        violations.append("raising tombstones are not inventoried")
    if "warning_alias" not in set(symbol_kinds.values()):
        violations.append("warning aliases are not inventoried")
    helper_ids = {
        symbol_id
        for symbol_id, kind in symbol_kinds.items()
        if kind != "module"
        and symbol_id.startswith(
            ("libtmux.test:", "libtmux.test.", "libtmux.pytest_plugin:")
        )
    }
    if not helper_ids:
        violations.append("public test helpers are not inventoried")
    if not inventory_ids >= LENIENT_ACCESSOR_IDS:
        violations.append("lenient list accessors are not inventoried")
    return violations


def source_path(source_url: object) -> str | None:
    """Return the pinned Git path represented by a source URL.

    Examples
    --------
    >>> source_path(SOURCE_URL_PREFIX + "src/libtmux/common.py")
    'src/libtmux/common.py'
    >>> source_path("https://example.invalid/source.py") is None
    True
    """
    if not isinstance(source_url, str) or not source_url.startswith(SOURCE_URL_PREFIX):
        return None
    return source_url.removeprefix(SOURCE_URL_PREFIX)


def source_contains(source_url: object, text: str) -> bool:
    """Return whether a pinned source file contains a source-grounding token.

    Examples
    --------
    >>> source_contains(SOURCE_URL_PREFIX + "src/libtmux/common.py", "raise_if_stderr")
    True
    """
    path = source_path(source_url)
    if path is None:
        return False
    return text in python_source.show(path)


@functools.cache
def pinned_source(path: str) -> str:
    """Read and cache one path from the pinned Python revision.

    Examples
    --------
    >>> "raise_if_stderr" in pinned_source("src/libtmux/common.py")
    True
    """
    return python_source.show(path)


def named_definition(
    body: list[ast.stmt],
    name: str,
) -> ast.ClassDef | ast.FunctionDef | ast.AsyncFunctionDef | None:
    r"""Return the last concrete definition with one name.

    Overload stubs precede the concrete implementation in the pinned source,
    so the final definition is the source-grounding boundary.

    Examples
    --------
    >>> tree = ast.parse("def value(): ...\ndef value(): return 1")
    >>> named_definition(tree.body, "value").lineno
    2
    """
    definitions = [
        node
        for node in body
        if isinstance(node, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef))
        and node.name == name
    ]
    return definitions[-1] if definitions else None


def symbol_definition(
    tree: ast.Module,
    qualified_name: str,
) -> tuple[
    ast.FunctionDef | ast.AsyncFunctionDef | None,
    ast.ClassDef | None,
]:
    r"""Resolve a callable and its owning class from a qualified name.

    Examples
    --------
    >>> tree = ast.parse("class Sample:\n    def value(self): return 1")
    >>> symbol_definition(tree, "Sample.value")[0].name
    'value'
    """
    body = tree.body
    owner: ast.ClassDef | None = None
    parts = qualified_name.split(".")
    for index, part in enumerate(parts):
        node = named_definition(body, part)
        if node is None:
            return None, None
        if index == len(parts) - 1:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                return node, owner
            return None, None
        if not isinstance(node, ast.ClassDef):
            return None, None
        owner = node
        body = node.body
    return None, None


def callable_body_source(
    source: str,
    node: ast.FunctionDef | ast.AsyncFunctionDef,
) -> str:
    r"""Return executable source for one callable without its docstring.

    Examples
    --------
    >>> source = "def value():\n    '''docs'''\n    return 1"
    >>> callable_body_source(source, ast.parse(source).body[0]).strip()
    'return 1'
    """
    body = list(node.body)
    if ast.get_docstring(node, clean=False) is not None:
        body = body[1:]
    return "\n".join(
        ast.get_source_segment(source, statement) or ast.unparse(statement)
        for statement in body
    )


def has_property_decorator(
    node: ast.FunctionDef | ast.AsyncFunctionDef,
) -> bool:
    r"""Return whether a callable is a property implementation.

    Examples
    --------
    >>> has_property_decorator(ast.parse("@property\ndef value(): return 1").body[0])
    True
    """
    return any(
        (isinstance(decorator, ast.Name) and decorator.id == "property")
        or (
            isinstance(decorator, ast.Attribute)
            and decorator.attr in {"getter", "setter", "deleter"}
        )
        for decorator in node.decorator_list
    )


def source_symbol_contains(
    source_url: object,
    symbol_id: object,
    text: str,
) -> bool:
    """Return whether one exact pinned callable grounds a token.

    Property-to-property delegation remains inside the owning class so an
    accessor such as ``attached_sessions`` can ground the command implemented
    by ``sessions`` without accepting unrelated same-file methods.

    Examples
    --------
    >>> source_symbol_contains(
    ...     SOURCE_URL_PREFIX + "src/libtmux/server.py",
    ...     "libtmux.server:Server.attach_session",
    ...     "attach-session",
    ... )
    True
    >>> source_symbol_contains(
    ...     SOURCE_URL_PREFIX + "src/libtmux/server.py",
    ...     "libtmux.server:Server.has_session",
    ...     "attach-session",
    ... )
    False
    """
    path = source_path(source_url)
    if path is None or not isinstance(symbol_id, str) or ":" not in symbol_id:
        return False
    source = pinned_source(path)
    tree = ast.parse(source, filename=path)
    node, owner = symbol_definition(tree, symbol_id.split(":", 1)[1])
    if node is None:
        return False
    bodies = [callable_body_source(source, node)]
    if owner is not None and has_property_decorator(node):
        properties = {
            member.name: member
            for member in owner.body
            if isinstance(member, (ast.FunctionDef, ast.AsyncFunctionDef))
            and has_property_decorator(member)
        }
        pending = [node]
        visited = {node.name}
        while pending:
            current = pending.pop()
            dependencies = {
                child.attr
                for child in ast.walk(current)
                if isinstance(child, ast.Attribute)
                and isinstance(child.value, ast.Name)
                and child.value.id == "self"
            }
            for dependency in sorted(dependencies - visited):
                target = properties.get(dependency)
                if target is None:
                    continue
                visited.add(dependency)
                pending.append(target)
                bodies.append(callable_body_source(source, target))
    return text in "\n".join(bodies)


def symbol_kind(
    symbols: dict[str, dict[str, str]],
    symbol_id: object,
    expected_kind: str,
    violations: list[str],
) -> dict[str, str] | None:
    """Validate one policy symbol reference and return its inventory row.

    Examples
    --------
    >>> violations: list[str] = []
    >>> symbol_kind({}, "libtmux:missing", "class", violations) is None
    True
    >>> violations
    ['unknown policy symbol: libtmux:missing']
    """
    if not isinstance(symbol_id, str) or symbol_id not in symbols:
        violations.append(f"unknown policy symbol: {symbol_id}")
        return None
    symbol = symbols[symbol_id]
    if symbol["kind"] != expected_kind:
        violations.append(f"unexpected policy symbol kind: {symbol_id}")
        return None
    return symbol


def policy_symbol(
    symbols: dict[str, dict[str, str]],
    symbol_id: object,
    expected_kinds: set[str],
    violations: list[str],
) -> dict[str, str] | None:
    """Validate a policy source whose callable shape may vary.

    Examples
    --------
    >>> violations: list[str] = []
    >>> policy_symbol({}, "libtmux:missing", {"method"}, violations) is None
    True
    >>> violations
    ['unknown policy symbol: libtmux:missing']
    """
    if not isinstance(symbol_id, str) or symbol_id not in symbols:
        violations.append(f"unknown policy symbol: {symbol_id}")
        return None
    symbol = symbols[symbol_id]
    if symbol["kind"] not in expected_kinds:
        violations.append(f"unexpected policy symbol kind: {symbol_id}")
        return None
    return symbol


def validate_error_policies(
    document: dict[str, t.Any],
    inventory: dict[str, t.Any],
) -> list[str]:
    """Return error-policy document contract violations.

    Examples
    --------
    >>> validate_error_policies({"policies": []}, {"symbols": []})
    ['missing error policies']
    """
    if not isinstance(document, dict) or set(document) != {"policies"}:
        return ["error policy schema is invalid"]
    raw_rows = document.get("policies")
    if not isinstance(raw_rows, list) or not all(
        isinstance(row, dict) for row in raw_rows
    ):
        return ["error policy rows are invalid"]
    policy_rows = t.cast(list[dict[str, t.Any]], raw_rows)
    policies = {row.get("name"): row for row in policy_rows}
    required = {
        "command_specific_errors",
        "display_message_stderr",
        "has_session",
        "list_accessors",
        "liveness",
        "missing_daemon_commands",
        "non_suppressible_errors",
        "option_failures",
        "raising_tombstones",
        "warning_aliases",
    }
    if len(policies) != len(policy_rows) or set(policies) != required:
        return ["missing error policies"]

    symbols = {
        symbol["id"]: symbol
        for symbol in t.cast(list[dict[str, str]], inventory.get("symbols", []))
    }
    violations: list[str] = []
    command_mappings = policies["command_specific_errors"].get("mappings")
    if not isinstance(command_mappings, list) or not command_mappings:
        return ["missing command mappings"]
    for mapping in t.cast(list[dict[str, t.Any]], command_mappings):
        command = mapping.get("tmuxCommand")
        source = symbol_kind(
            symbols,
            mapping.get("sourceSymbolId"),
            "method",
            violations,
        )
        error = symbol_kind(
            symbols,
            mapping.get("errorSymbolId"),
            "class",
            violations,
        )
        handler_id = mapping.get("errorHandlerSymbolId")
        handler = (
            symbol_kind(symbols, handler_id, "function", violations)
            if handler_id is not None
            else None
        )
        if not isinstance(command, str) or not command:
            violations.append("invalid command mapping")
            continue
        if source is None or error is None:
            continue
        error_name = t.cast(str, mapping["errorSymbolId"]).rsplit(":", 1)[-1]
        source_id = mapping.get("sourceSymbolId")
        if not source_symbol_contains(source["sourceUrl"], source_id, command):
            violations.append(f"ungrounded command mapping: {command}")
        if handler is None and not source_symbol_contains(
            source["sourceUrl"], source_id, error_name
        ):
            violations.append(f"ungrounded error mapping: {command}")
        if handler is not None:
            handler_name = t.cast(str, handler_id).rsplit(":", 1)[-1]
            if not source_symbol_contains(
                source["sourceUrl"], source_id, handler_name
            ) or not source_symbol_contains(
                handler["sourceUrl"], handler_id, error_name
            ):
                violations.append(f"ungrounded error mapping: {command}")

    display_mappings = policies["display_message_stderr"].get("mappings")
    if not isinstance(display_mappings, list):
        violations.append("invalid display-message policy")
    else:
        display_ids = {
            mapping.get("sourceSymbolId")
            for mapping in display_mappings
            if isinstance(mapping, dict)
        }
        if display_ids != DISPLAY_MESSAGE_IDS or len(display_mappings) != len(
            DISPLAY_MESSAGE_IDS
        ):
            violations.append("invalid display-message policy")
        for mapping in display_mappings:
            if not isinstance(mapping, dict):
                continue
            source = policy_symbol(
                symbols,
                mapping.get("sourceSymbolId"),
                {"method"},
                violations,
            )
            if (
                set(mapping)
                != {
                    "csharpMemberId",
                    "disposition",
                    "logLevel",
                    "sourceSymbolId",
                    "tmuxCommand",
                }
                or mapping.get("disposition") != "log_warning_and_return"
                or mapping.get("logLevel") != "Warning"
                or mapping.get("tmuxCommand") != "display-message"
                or not isinstance(mapping.get("csharpMemberId"), str)
            ):
                violations.append("invalid display-message policy")
            if source is not None and (
                not source_symbol_contains(
                    source["sourceUrl"],
                    mapping.get("sourceSymbolId"),
                    "display-message",
                )
                or not source_symbol_contains(
                    source["sourceUrl"],
                    mapping.get("sourceSymbolId"),
                    "warnings.warn",
                )
            ):
                violations.append("ungrounded display-message policy")

    has_session = policies["has_session"]
    has_session_mappings = has_session.get("mappings")
    expected_has_session = [
        {
            "csharpMemberId": (
                "M:LibTmux.Server.HasSessionAsync(System.String,System.Boolean,System.Threading.CancellationToken)"
            ),
            "exitCodeDisposition": "zero_true_nonzero_false",
            "sourceSymbolId": "libtmux.server:Server.has_session",
            "tmuxCommand": "has-session",
            "transportFailureDisposition": "throw",
        }
    ]
    if has_session_mappings != expected_has_session:
        violations.append("invalid has-session policy")
    else:
        mapping = expected_has_session[0]
        source = policy_symbol(
            symbols,
            mapping["sourceSymbolId"],
            {"method"},
            violations,
        )
        if source is not None and not source_symbol_contains(
            source["sourceUrl"],
            mapping["sourceSymbolId"],
            mapping["tmuxCommand"],
        ):
            violations.append("ungrounded has-session policy")

    list_mappings = policies["list_accessors"].get("mappings")
    if not isinstance(list_mappings, list):
        violations.append("invalid list policy")
    else:
        list_ids = {
            mapping.get("sourceSymbolId")
            for mapping in list_mappings
            if isinstance(mapping, dict)
        }
        if list_ids != LIST_POLICY_IDS or len(list_mappings) != len(LIST_POLICY_IDS):
            violations.append("invalid list policy")
        for mapping in list_mappings:
            if not isinstance(mapping, dict):
                violations.append("invalid list policy")
                continue
            source = policy_symbol(
                symbols,
                mapping.get("sourceSymbolId"),
                {"method", "property"},
                violations,
            )
            commands = mapping.get("tmuxCommands")
            if (
                set(mapping)
                != {
                    "csharpMemberId",
                    "failureDisposition",
                    "sourceSymbolId",
                    "tmuxCommands",
                }
                or mapping.get("failureDisposition") not in LIST_DISPOSITIONS
                or not isinstance(mapping.get("csharpMemberId"), str)
                or not isinstance(commands, list)
                or not commands
                or not all(isinstance(command, str) and command for command in commands)
                or len(commands) != len(set(commands))
            ):
                violations.append("invalid list policy")
            if (
                source is not None
                and isinstance(commands, list)
                and not all(
                    source_symbol_contains(
                        source["sourceUrl"],
                        mapping.get("sourceSymbolId"),
                        command,
                    )
                    for command in commands
                )
            ):
                violations.append("ungrounded list policy")

    liveness_mappings = policies["liveness"].get("mappings")
    if not isinstance(liveness_mappings, list):
        violations.append("invalid liveness policy")
    else:
        liveness_ids = {
            mapping.get("sourceSymbolId")
            for mapping in liveness_mappings
            if isinstance(mapping, dict)
        }
        if liveness_ids != LIVENESS_IDS or len(liveness_mappings) != len(LIVENESS_IDS):
            violations.append("invalid liveness policy")
        expected_failures = [
            "T:LibTmux.TmuxCommandException",
            "T:LibTmux.TmuxCommandNotFoundException",
            "T:LibTmux.TmuxTransportException",
        ]
        for mapping in liveness_mappings:
            if not isinstance(mapping, dict):
                violations.append("invalid liveness policy")
                continue
            source_id = mapping.get("sourceSymbolId")
            policy_symbol(symbols, source_id, {"method"}, violations)
            if source_id == "libtmux.server:Server.is_alive":
                valid = (
                    mapping.get("disposition") == "return_false"
                    and mapping.get("suppressedFailures") == expected_failures
                    and "thrownFailures" not in mapping
                )
            else:
                valid = (
                    mapping.get("disposition") == "throw"
                    and mapping.get("thrownFailures") == expected_failures
                    and "suppressedFailures" not in mapping
                )
            if (
                not valid
                or mapping.get("tmuxCommand") != "list-sessions"
                or not isinstance(mapping.get("csharpMemberId"), str)
            ):
                violations.append("invalid liveness policy")

    daemon_mappings = policies["missing_daemon_commands"].get("mappings")
    if not isinstance(daemon_mappings, list):
        violations.append("invalid missing-daemon policy")
    else:
        daemon_ids = {
            mapping.get("sourceSymbolId")
            for mapping in daemon_mappings
            if isinstance(mapping, dict)
        }
        if daemon_ids != MISSING_DAEMON_IDS or len(daemon_mappings) != len(
            MISSING_DAEMON_IDS
        ):
            violations.append("invalid missing-daemon policy")
        for mapping in daemon_mappings:
            if not isinstance(mapping, dict):
                violations.append("invalid missing-daemon policy")
                continue
            source_id = mapping.get("sourceSymbolId")
            source = policy_symbol(symbols, source_id, {"method"}, violations)
            expected_missing = (
                "return_success"
                if source_id == "libtmux.server:Server.kill"
                else "throw"
            )
            if (
                mapping.get("missingDaemonDisposition") != expected_missing
                or mapping.get("otherFailureDisposition") != "throw"
                or mapping.get("tmuxCommand")
                != (
                    "kill-server"
                    if source_id == "libtmux.server:Server.kill"
                    else "kill-session"
                )
                or not isinstance(mapping.get("csharpMemberId"), str)
            ):
                violations.append("invalid missing-daemon policy")
            if source is not None and not source_symbol_contains(
                source["sourceUrl"],
                source_id,
                t.cast(str, mapping.get("tmuxCommand")),
            ):
                violations.append("ungrounded missing-daemon policy")

    non_suppressible = policies["non_suppressible_errors"]
    if non_suppressible != {
        "appliesTo": [
            "has_session",
            "list_accessors",
            "liveness",
            "missing_daemon_commands",
        ],
        "disposition": "propagate",
        "exceptionTypes": NON_SUPPRESSIBLE_TYPES,
        "name": "non_suppressible_errors",
    }:
        violations.append("invalid non-suppressible policy")

    option_policy = policies["option_failures"]
    if (
        option_policy.get("commands")
        != ["set-hook", "set-option", "show-hooks", "show-options"]
        or option_policy.get("csharpExceptionId") != "T:LibTmux.TmuxOptionException"
        or option_policy.get("csharpHandlerId")
        != ("M:LibTmux.Internal.OptionFailure.ThrowIfFailed(LibTmux.TmuxCommandResult,System.String)")
        or option_policy.get("pythonHandlerSymbolId")
        != "libtmux.options:handle_option_error"
        or [
            (mapping.get("match"), mapping.get("pythonErrorSymbolId"))
            for mapping in option_policy.get("mappings", [])
            if isinstance(mapping, dict)
        ]
        != OPTION_ERROR_IDS
    ):
        violations.append("invalid option policy")
    handler = symbol_kind(
        symbols,
        option_policy.get("pythonHandlerSymbolId"),
        "function",
        violations,
    )
    for _match, error_id in OPTION_ERROR_IDS:
        symbol_kind(symbols, error_id, "class", violations)
    if handler is not None and any(
        not source_symbol_contains(
            handler["sourceUrl"],
            option_policy.get("pythonHandlerSymbolId"),
            token,
        )
        for token in ("unknown option", "invalid option", "ambiguous option")
    ):
        violations.append("ungrounded option policy")

    raising_tombstone_ids = sorted(
        symbol_id
        for symbol_id, symbol in symbols.items()
        if symbol["kind"] == "raising_tombstone"
    )
    expected_policy_symbols = (
        ("warning_aliases", WARNING_ALIAS_IDS, "warning_alias"),
        ("raising_tombstones", raising_tombstone_ids, "raising_tombstone"),
    )
    for policy_name, expected_ids, expected_kind in expected_policy_symbols:
        symbol_ids = policies[policy_name].get("symbolIds")
        if symbol_ids != expected_ids:
            violations.append(f"invalid policy symbols: {policy_name}")
            continue
        for symbol_id in expected_ids:
            symbol_kind(symbols, symbol_id, expected_kind, violations)

    try:
        public_api = json.loads(CSHARP_INVENTORY_PATH.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        violations.append("C# compiler inventory cannot be read")
        return violations
    public_ids = {
        row["id"]
        for section in ("members",)
        for row in public_api.get(section, [])
        if isinstance(row, dict) and isinstance(row.get("id"), str)
    }
    csharp_references: list[object] = []
    for policy in policy_rows:
        containers = [policy]
        mappings = policy.get("mappings")
        if isinstance(mappings, list):
            containers.extend(
                mapping for mapping in mappings if isinstance(mapping, dict)
            )
        for container in containers:
            csharp_references.extend(
                container[key]
                for key in (
                    "csharpExceptionId",
                    "csharpHandlerId",
                    "csharpMemberId",
                )
                if key in container
            )
    for reference in csharp_references:
        if not isinstance(reference, str):
            violations.append("invalid C# policy ID")
        elif reference not in public_ids:
            violations.append(f"unknown C# policy ID: {reference}")
    return violations


def main() -> int:
    """Run ledger and version-matrix validation."""
    violations = validate(
        load_document("python-public-api.json"),
        approval_snapshot(load_document("parity-ledger.json")),
    )
    violations.extend(
        validate_error_policies(
            load_document("error-policies.json"),
            load_document("python-public-api.json"),
        )
    )
    version_result = subprocess.run(
        [sys.executable, str(GENERATOR_PATH.with_name("reconcile_versions.py"))],
        check=False,
    )
    if violations:
        print("\n".join(violations), file=sys.stderr)
    return 1 if violations or version_result.returncode else 0


if __name__ == "__main__":
    sys.exit(main())
