"""Generate the C# parity inventory from the pinned Python source tree."""

from __future__ import annotations

import argparse
import ast
import json
import pathlib
import sys
import typing as t

REPOSITORY_ROOT = pathlib.Path(__file__).parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from eng.parity import python_source  # noqa: E402
from eng.parity.verify_public_api import TMUX_MAX_VERSION_ADAPTATION  # noqa: E402

SOURCE_REVISION = python_source.REVISION
SOURCE_BASE_URL = python_source.BLOB_URL_PREFIX.rstrip("/")
TMUX_SOURCE_BASE_URL = "https://github.com/tmux/tmux/tree"
TMUX_SOURCE_BLOB_URL = "https://github.com/tmux/tmux/blob"
MODULE_PATHS = {
    "libtmux": "src/libtmux/__init__.py",
    "libtmux.client": "src/libtmux/client.py",
    "libtmux.common": "src/libtmux/common.py",
    "libtmux.constants": "src/libtmux/constants.py",
    "libtmux.exc": "src/libtmux/exc.py",
    "libtmux.formats": "src/libtmux/formats.py",
    "libtmux.hooks": "src/libtmux/hooks.py",
    "libtmux.neo": "src/libtmux/neo.py",
    "libtmux.options": "src/libtmux/options.py",
    "libtmux.pane": "src/libtmux/pane.py",
    "libtmux.pytest_plugin": "src/libtmux/pytest_plugin.py",
    "libtmux.server": "src/libtmux/server.py",
    "libtmux.session": "src/libtmux/session.py",
    "libtmux.test": "src/libtmux/test/__init__.py",
    "libtmux.test.constants": "src/libtmux/test/constants.py",
    "libtmux.test.environment": "src/libtmux/test/environment.py",
    "libtmux.test.random": "src/libtmux/test/random.py",
    "libtmux.test.retry": "src/libtmux/test/retry.py",
    "libtmux.test.temporary": "src/libtmux/test/temporary.py",
    "libtmux.window": "src/libtmux/window.py",
    "libtmux._internal.query_list": "src/libtmux/_internal/query_list.py",
}
DOCUMENT_ROOT = pathlib.Path(__file__).parents[2] / "docs" / "parity"


def read_pinned_source(path: str) -> str:
    """Read one path from the fixed Python source revision.

    Examples
    --------
    >>> "typed, pythonic API" in read_pinned_source("src/libtmux/__init__.py")
    True
    """
    return python_source.show(path)


def symbol_id(module: str, qualified_name: str) -> str:
    """Build a stable inventory identifier.

    Examples
    --------
    >>> symbol_id("libtmux.server", "Server.sessions")
    'libtmux.server:Server.sessions'
    """
    return f"{module}:{qualified_name}"


def public_name(name: str) -> bool:
    """Return whether a name is public in ordinary source declarations.

    Examples
    --------
    >>> public_name("Server")
    True
    >>> public_name("_fetch_or_empty")
    False
    """
    return not name.startswith("_")


def node_names(node: ast.Assign | ast.AnnAssign) -> list[str]:
    """Return simple assignment targets from one AST node.

    Examples
    --------
    >>> node_names(ast.parse("ANSWER = 42").body[0])
    ['ANSWER']
    """
    if isinstance(node, ast.AnnAssign):
        return [node.target.id] if isinstance(node.target, ast.Name) else []
    return [target.id for target in node.targets if isinstance(target, ast.Name)]


def literal_all(tree: ast.Module) -> set[str]:
    """Resolve a literal module ``__all__`` declaration.

    Examples
    --------
    >>> literal_all(ast.parse("__all__ = ('Pane', 'Server')")) == {'Pane', 'Server'}
    True
    """
    for node in tree.body:
        if isinstance(node, ast.Assign) and "__all__" in node_names(node):
            value = ast.literal_eval(node.value)
            return {item for item in value if isinstance(item, str)}
    return set()


def has_property_decorator(node: ast.FunctionDef | ast.AsyncFunctionDef) -> bool:
    r"""Return whether a callable is declared as a property.

    Examples
    --------
    >>> source = "@property\ndef value():\n    return 1"
    >>> has_property_decorator(ast.parse(source).body[0])
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


def is_deprecation_warning(statement: ast.stmt) -> bool:
    """Return whether a statement emits a deprecation warning.

    Examples
    --------
    >>> node = ast.parse("warnings.warn('deprecated')").body[0]
    >>> is_deprecation_warning(node)
    True
    """
    if not (
        isinstance(statement, ast.Expr)
        and isinstance(statement.value, ast.Call)
        and isinstance(statement.value.func, ast.Attribute)
        and isinstance(statement.value.func.value, ast.Name)
        and statement.value.func.value.id == "warnings"
        and statement.value.func.attr == "warn"
    ):
        return False
    call = statement.value
    return any(
        keyword.arg == "category"
        and isinstance(keyword.value, ast.Name)
        and keyword.value.id == "DeprecationWarning"
        for keyword in call.keywords
    ) or bool(
        call.args
        and isinstance(call.args[0], ast.Constant)
        and isinstance(call.args[0].value, str)
        and "deprecated" in call.args[0].value.lower()
    )


def is_delegating_return(statement: ast.stmt) -> bool:
    """Return whether a statement returns a direct call through ``self``.

    Examples
    --------
    >>> node = ast.parse("return self.current()").body[0]
    >>> is_delegating_return(node)
    True
    """
    return (
        isinstance(statement, ast.Return)
        and isinstance(statement.value, ast.Call)
        and isinstance(statement.value.func, ast.Attribute)
        and isinstance(statement.value.func.value, ast.Name)
        and statement.value.func.value.id == "self"
    )


def is_warning_alias(node: ast.FunctionDef | ast.AsyncFunctionDef) -> bool:
    r"""Return whether a callable is a warning-emitting delegation wrapper.

    Examples
    --------
    >>> node = ast.parse(
    ...     "def old(self):\n"
    ...     "    warnings.warn('deprecated')\n"
    ...     "    return self.new()"
    ... ).body[0]
    >>> is_warning_alias(node)
    True
    """
    body = list(node.body)
    if ast.get_docstring(node, clean=False) is not None:
        body.pop(0)
    return (
        len(body) == 2
        and is_deprecation_warning(body[0])
        and is_delegating_return(body[1])
    )


def is_raising_tombstone(node: ast.FunctionDef | ast.AsyncFunctionDef) -> bool:
    r"""Return whether a callable exists solely to raise a compatibility error.

    Examples
    --------
    >>> is_raising_tombstone(ast.parse("def f():\n    raise RuntimeError()").body[0])
    True
    """
    body = [statement for statement in node.body if not isinstance(statement, ast.Expr)]
    return len(body) == 1 and isinstance(body[0], ast.Raise)


def raising_tombstone_replacement(
    node: ast.FunctionDef | ast.AsyncFunctionDef,
) -> str | None:
    r"""Return the literal replacement from one raising DeprecatedError.

    Examples
    --------
    >>> node = ast.parse(
    ...     "def old():\n"
    ...     "    raise exc.DeprecatedError(replacement='New.call()')"
    ... ).body[0]
    >>> raising_tombstone_replacement(node)
    'New.call()'
    """
    if not is_raising_tombstone(node):
        return None
    for child in ast.walk(node):
        if not (
            isinstance(child, ast.Call)
            and isinstance(child.func, ast.Attribute)
            and child.func.attr == "DeprecatedError"
        ):
            continue
        for keyword in child.keywords:
            if (
                keyword.arg == "replacement"
                and isinstance(keyword.value, ast.Constant)
                and isinstance(keyword.value.value, str)
            ):
                return keyword.value.value
    return None


def pinned_tombstone_replacements() -> dict[str, str]:
    """Derive every tombstone's replacement from the pinned source body.

    Examples
    --------
    >>> replacements = pinned_tombstone_replacements()
    >>> replacements["libtmux.window:Window.kill_window"]
    'Window.kill()'
    """
    replacements: dict[str, str] = {}
    for module, path in MODULE_PATHS.items():
        tree = ast.parse(read_pinned_source(path), filename=path)
        for node in tree.body:
            candidates: list[tuple[str, ast.FunctionDef | ast.AsyncFunctionDef]] = []
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                candidates.append((node.name, node))
            elif isinstance(node, ast.ClassDef) and public_name(node.name):
                candidates.extend(
                    (f"{node.name}.{member.name}", member)
                    for member in node.body
                    if isinstance(member, (ast.FunctionDef, ast.AsyncFunctionDef))
                )
            for qualified_name, candidate in candidates:
                if not is_raising_tombstone(candidate):
                    continue
                replacement = raising_tombstone_replacement(candidate)
                if replacement is None:
                    msg = (
                        "missing literal tombstone replacement: "
                        f"{module}:{qualified_name}"
                    )
                    raise ValueError(msg)
                replacements[symbol_id(module, qualified_name)] = replacement
    return dict(sorted(replacements.items()))


def is_enum_class(node: ast.ClassDef) -> bool:
    r"""Return whether a class directly derives from :class:`enum.Enum`.

    Examples
    --------
    >>> is_enum_class(ast.parse("class Direction(enum.Enum):\n    Up = 'UP'").body[0])
    True
    """
    return any(
        (isinstance(base, ast.Name) and base.id == "Enum")
        or (isinstance(base, ast.Attribute) and base.attr == "Enum")
        for base in node.bases
    )


def is_type_alias_assignment(node: ast.Assign | ast.AnnAssign) -> bool:
    """Return whether an assignment declares a type alias.

    Examples
    --------
    >>> is_type_alias_assignment(ast.parse("Names = list[str]").body[0])
    True
    >>> is_type_alias_assignment(ast.parse("count: int = 1").body[0])
    False
    """
    if isinstance(node, ast.Assign):
        return isinstance(node.value, ast.Subscript)
    annotation = node.annotation
    return (isinstance(annotation, ast.Name) and annotation.id == "TypeAlias") or (
        isinstance(annotation, ast.Attribute) and annotation.attr == "TypeAlias"
    )


def record(
    module: str,
    path: str,
    qualified_name: str,
    kind: str,
) -> dict[str, str]:
    """Build one source-pinned inventory row.

    Examples
    --------
    >>> record("libtmux", "src/libtmux/__init__.py", "Client", "class")["id"]
    'libtmux:Client'
    """
    return {
        "id": symbol_id(module, qualified_name),
        "kind": kind,
        "module": module,
        "qualifiedName": qualified_name,
        "sourceUrl": f"{SOURCE_BASE_URL}/{path}",
    }


def add_assignment_records(
    records: dict[str, dict[str, str]],
    module: str,
    path: str,
    node: ast.Assign | ast.AnnAssign,
    *,
    class_name: str | None = None,
    enum_members: bool = False,
) -> None:
    """Add public constants, aliases, fields, or enum members from an assignment.

    Examples
    --------
    >>> rows: dict[str, dict[str, str]] = {}
    >>> node = ast.parse("value: str | None = None").body[0]
    >>> add_assignment_records(rows, "libtmux", "x.py", node, class_name="Obj")
    >>> rows["libtmux:Obj.value"]["kind"]
    'field'
    """
    for name in node_names(node):
        if not public_name(name):
            continue
        if enum_members:
            kind = "enum_member"
        elif class_name is not None:
            kind = "type_alias" if is_type_alias_assignment(node) else "field"
        else:
            kind = "type_alias" if is_type_alias_assignment(node) else "constant"
        qualified_name = f"{class_name}.{name}" if class_name is not None else name
        row = record(module, path, qualified_name, kind)
        records[row["id"]] = row


def add_function_record(
    records: dict[str, dict[str, str]],
    module: str,
    path: str,
    qualified_name: str,
    node: ast.FunctionDef | ast.AsyncFunctionDef,
) -> None:
    r"""Add a public, warning-alias, or raising-tombstone callable row.

    Examples
    --------
    >>> rows: dict[str, dict[str, str]] = {}
    >>> node = ast.parse("def visible():\n    return None").body[0]
    >>> add_function_record(rows, "libtmux", "x.py", "visible", node)
    >>> rows["libtmux:visible"]["kind"]
    'function'
    """
    name = qualified_name.rsplit(".", 1)[-1]
    if not public_name(name) and not is_raising_tombstone(node):
        return
    kind = "method" if "." in qualified_name else "function"
    if is_raising_tombstone(node):
        kind = "raising_tombstone"
    elif has_property_decorator(node):
        kind = "property"
    elif is_warning_alias(node):
        kind = "warning_alias"
    row = record(module, path, qualified_name, kind)
    records[row["id"]] = row


def collect_module_symbols(module: str, path: str) -> list[dict[str, str]]:
    """Collect declared and package-re-exported symbols for one module.

    Examples
    --------
    >>> any(row["qualifiedName"] == "Server" for row in collect_module_symbols(
    ...     "libtmux", "src/libtmux/__init__.py"
    ... ))
    True
    """
    tree = ast.parse(read_pinned_source(path), filename=path)
    exported_names = literal_all(tree)
    records: dict[str, dict[str, str]] = {}
    module_row = record(module, path, "<module>", "module")
    records[module_row["id"]] = module_row

    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            add_function_record(records, module, path, node.name, node)
        elif isinstance(node, ast.ClassDef) and public_name(node.name):
            class_row = record(module, path, node.name, "class")
            records[class_row["id"]] = class_row
            enum_members = is_enum_class(node)
            for child in node.body:
                if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    add_function_record(
                        records,
                        module,
                        path,
                        f"{node.name}.{child.name}",
                        child,
                    )
                elif isinstance(child, (ast.Assign, ast.AnnAssign)):
                    add_assignment_records(
                        records,
                        module,
                        path,
                        child,
                        class_name=node.name,
                        enum_members=enum_members,
                    )
        elif isinstance(node, (ast.Assign, ast.AnnAssign)):
            add_assignment_records(records, module, path, node)
        elif isinstance(node, ast.ImportFrom) and exported_names:
            for imported in node.names:
                name = imported.asname or imported.name
                if name in exported_names:
                    row = record(module, path, name, "reexport")
                    records[row["id"]] = row

    return [records[key] for key in sorted(records)]


def build_inventory() -> dict[str, t.Any]:
    """Build the deterministic Python public API inventory document.

    Examples
    --------
    >>> build_inventory()["sourceRevision"]
    'c4a980b'
    """
    symbols = [
        row
        for module, path in sorted(MODULE_PATHS.items())
        for row in collect_module_symbols(module, path)
    ]
    return {
        "sourceRevision": SOURCE_REVISION,
        "symbols": sorted(symbols, key=lambda row: row["id"]),
    }


def component(module: str) -> str:
    """Return the parity component owning a Python module.

    Examples
    --------
    >>> component("libtmux.test.random")
    'test_helpers'
    """
    if module == "libtmux.neo":
        return "query_model"
    if module.startswith("libtmux.test") or module == "libtmux.pytest_plugin":
        return "test_helpers"
    if module == "libtmux._internal.query_list":
        return "query_list"
    return module.rsplit(".", 1)[-1]


MATCHING_REPLACEMENT = (
    "M:LibTmux.Query.QueryExtensions.Matching``1("
    "IEnumerable<T>,Expression<Func<T,bool>>)"
)
PROPERTY_TOMBSTONE_IDS = {
    "libtmux.server:Server._sessions",
    "libtmux.server:Server.children",
    "libtmux.session:Session._windows",
    "libtmux.session:Session.attached_pane",
    "libtmux.session:Session.attached_window",
    "libtmux.session:Session.children",
    "libtmux.window:Window._panes",
    "libtmux.window:Window.attached_pane",
    "libtmux.window:Window.children",
}
CSHARP_TOMBSTONE_REPLACEMENTS = {
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
    "libtmux.server:Server._sessions": (
        "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
    ),
    "libtmux.server:Server._update_panes": (
        "M:LibTmux.Server.GetPanesAsync(CancellationToken)"
    ),
    "libtmux.server:Server._update_windows": (
        "M:LibTmux.Server.GetWindowsAsync(CancellationToken)"
    ),
    "libtmux.server:Server.children": (
        "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
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
    "libtmux.server:Server.where": MATCHING_REPLACEMENT,
    "libtmux.session:Session.__getitem__": (
        "typed Session property, otherwise P:LibTmux.Session.RawFormatFields"
    ),
    "libtmux.session:Session._list_windows": (
        "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
    ),
    "libtmux.session:Session._windows": (
        "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
    ),
    "libtmux.session:Session.attach_session": (
        "M:LibTmux.Session.AttachAsync(AttachSessionRequest?,CancellationToken)"
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
    "libtmux.session:Session.find_where": "Matching(predicate).SingleOrDefault()",
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
    "libtmux.session:Session.where": MATCHING_REPLACEMENT,
    "libtmux.window:Window.__getitem__": (
        "typed Window property, otherwise P:LibTmux.Window.RawFormatFields"
    ),
    "libtmux.window:Window._list_panes": (
        "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
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
    "libtmux.window:Window.where": MATCHING_REPLACEMENT,
}


LEDGER_SOURCE_KEYS = (
    "behavior",
    "component",
    "module",
    "pythonSymbolId",
    "sourceUrl",
    "tmuxVersions",
)
LEDGER_REVIEW_KEYS = (
    "componentId",
    "csharpDestination",
    "destinationStatus",
    "evidenceStatus",
    "implementationStatus",
    "testPath",
    "exclusionReason",
    "replacement",
)
C4_LOOKUP_SYMBOL_IDS = frozenset(
    {
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.window:Window.from_window_id",
    }
)
C4_PARITY_TEST_PATH = (
    "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
)
LEDGER_BEHAVIOR_OVERRIDES = {
    "libtmux.common:TMUX_MAX_VERSION": TMUX_MAX_VERSION_ADAPTATION,
}


def preserve_ledger_reconciliation(
    seed: dict[str, t.Any], existing: object
) -> dict[str, t.Any]:
    """Preserve reviewed fields only for source-identical inventory rows."""
    if not isinstance(existing, dict):
        return seed
    if existing.get("sourceRevision") != seed["sourceRevision"]:
        return seed
    existing_rows = existing.get("rows")
    if not isinstance(existing_rows, list):
        return seed
    by_id = {
        row.get("pythonSymbolId"): row for row in existing_rows if isinstance(row, dict)
    }
    if len(by_id) != len(existing_rows):
        return seed
    for seed_row in seed["rows"]:
        existing_row = by_id.get(seed_row["pythonSymbolId"])
        if not isinstance(existing_row, dict) or any(
            existing_row.get(key) != seed_row[key] for key in LEDGER_SOURCE_KEYS
        ):
            continue
        for key in LEDGER_REVIEW_KEYS:
            if key in existing_row:
                seed_row[key] = existing_row[key]
            else:
                seed_row.pop(key, None)
    return seed


def build_ledger(
    inventory: dict[str, t.Any], existing: object = None
) -> dict[str, t.Any]:
    """Build planned parity rows from the source-owned inventory.

    Examples
    --------
    >>> build_ledger({"symbols": []})["rows"]
    []
    """
    rows: list[dict[str, t.Any]] = []
    for symbol in inventory["symbols"]:
        module = t.cast(str, symbol["module"])
        rows.append(
            {
                "behavior": LEDGER_BEHAVIOR_OVERRIDES.get(
                    symbol["id"],
                    f"Preserve {symbol['kind']} {symbol['qualifiedName']}",
                ),
                "component": component(module),
                "csharpDestination": (
                    "LibTmux.Internal.Materialization"
                    if module == "libtmux.neo"
                    else None
                ),
                "destinationStatus": "internalized"
                if module == "libtmux.neo"
                else "planned",
                "evidenceStatus": "none",
                "implementationStatus": "not_started",
                "module": module,
                "pythonSymbolId": symbol["id"],
                "sourceUrl": symbol["sourceUrl"],
                "testPath": "tests/RealServer/ParityInventoryTests.cs",
                "tmuxVersions": "3.2a-3.7b",
            }
        )
    ledger = preserve_ledger_reconciliation(
        {"rows": rows, "sourceRevision": SOURCE_REVISION}, existing
    )
    for row in ledger["rows"]:
        if (
            row["pythonSymbolId"] not in C4_LOOKUP_SYMBOL_IDS
            or row.get("componentId") == 4
        ):
            continue
        row["componentId"] = 4
        row["implementationStatus"] = "not_started"
        row["evidenceStatus"] = "none"
        row["testPath"] = C4_PARITY_TEST_PATH
    inventory_tombstones = {
        symbol["id"]
        for symbol in inventory["symbols"]
        if symbol["kind"] == "raising_tombstone"
    }
    if inventory.get("sourceRevision") == SOURCE_REVISION:
        source_tombstones = set(pinned_tombstone_replacements())
        if not (
            inventory_tombstones
            == source_tombstones
            == set(CSHARP_TOMBSTONE_REPLACEMENTS)
        ):
            message = "raising tombstone replacement catalog is incomplete"
            raise ValueError(message)
    for row in ledger["rows"]:
        symbol_id_value = t.cast(str, row["pythonSymbolId"])
        if symbol_id_value not in inventory_tombstones:
            continue
        if symbol_id_value not in CSHARP_TOMBSTONE_REPLACEMENTS:
            continue
        row["csharpDestination"] = None
        row["destinationStatus"] = "excluded"
        row["exclusionReason"] = (
            "The Python property exists only to raise DeprecatedError."
            if symbol_id_value in PROPERTY_TOMBSTONE_IDS
            else "The Python member exists only to raise DeprecatedError."
        )
        row["replacement"] = CSHARP_TOMBSTONE_REPLACEMENTS[symbol_id_value]
    return ledger


def existing_ledger() -> object:
    """Load the current reviewed parity ledger when it is valid JSON."""
    path = DOCUMENT_ROOT / "parity-ledger.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return None


def version_deltas() -> dict[str, t.Any]:
    """Build the seed tmux-version delta matrix.

    Examples
    --------
    >>> len(version_deltas()["capabilities"]) > 6
    True
    """
    capability_names = (
        "attachment_accounting",
        "byte_length_framing",
        "control_notifications",
        "format_fields_and_operators",
        "option_dollar_double_escape",
        "semicolon_grouping",
    )
    # Most of what the protocol reads is true of every supported tmux, so
    # a capability names its bounds only when it holds for some of them.
    capability_bounds = {
        "option_dollar_double_escape": ("3.4", "3.5"),
    }
    capabilities: list[dict[str, t.Any]] = [
        {
            "capability": name,
            "evidenceStatus": "pending",
            "introducedIn": capability_bounds.get(name, ("unknown", "unknown"))[0],
            "namedRealServerTest": (
                "tests/LibTmux.IntegrationTests/Versioning/"
                "VersionParityTests.cs::"
                + "".join(part.title() for part in name.split("_"))
            ),
            "removedIn": capability_bounds.get(name, ("unknown", "unknown"))[1],
            "tmuxSourceEndpoints": {
                "3.2a": f"{TMUX_SOURCE_BASE_URL}/3.2a",
                "3.7b": f"{TMUX_SOURCE_BASE_URL}/3.7b",
            },
        }
        for name in capability_names
    ]
    policy_owner_components = {
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
    policy_test_files_by_component = {
        10: (
            "tests/LibTmux.IntegrationTests/Hierarchy/"
            "ServerSessionLifecycleTests.cs"
        ),
        11: ("tests/LibTmux.IntegrationTests/Hierarchy/WindowTopologyTests.cs"),
        12: ("tests/LibTmux.IntegrationTests/Hierarchy/PaneOperationsTests.cs"),
        13: (
            "tests/LibTmux.IntegrationTests/Clients/ClientAdministrationTests.cs"
        ),
        15: "tests/LibTmux.IntegrationTests/Hooks/HookOperationsTests.cs",
        16: ("tests/LibTmux.IntegrationTests/Utilities/ServerUtilitiesTests.cs"),
    }
    unsupported_proof_by_behavior = {
        "apply_only_in_affected_version": "exact_3_7_and_3_7a_transition",
        "not_applicable_below_supported_floor": "not_applicable",
        "throw_unsupported_version": "typed_version_exception_zero_dispatch",
        "warn_and_ignore": "warn_omit_single_dispatch",
    }
    command_gates = (
        (
            "break_pane_3_7_workaround",
            "workaround",
            "break-pane",
            ("-n",),
            "3.7",
            "3.7a",
            "apply_only_in_affected_version",
            ("libtmux.pane:Pane.break_pane",),
        ),
        (
            "capture_pane_3_7_metadata",
            "flags",
            "capture-pane",
            ("-H", "-L", "-F"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        (
            "capture_pane_mode_screen",
            "flag",
            "capture-pane",
            ("-M",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        (
            "capture_pane_trim_trailing",
            "flag",
            "capture-pane",
            ("-T",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.capture_pane",),
        ),
        (
            "choose_tree_sort_time",
            "removed_flag",
            "choose-tree",
            ("-O",),
            "unknown",
            "3.7",
            "warn_and_ignore",
            ("libtmux.pane:Pane.choose_tree",),
        ),
        (
            "clear_history_hyperlinks",
            "flag",
            "clear-history",
            ("-H",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.clear_history",),
        ),
        (
            "clear_prompt_history_command",
            "command",
            "clear-prompt-history",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.clear_prompt_history",),
        ),
        (
            "command_prompt_3_7_behavior",
            "flags",
            "command-prompt",
            ("-e", "-C"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.command_prompt",),
        ),
        (
            "command_prompt_background",
            "flags",
            "command-prompt",
            ("-b", "-F"),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.command_prompt",),
        ),
        (
            "command_prompt_literal",
            "flag",
            "command-prompt",
            ("-l",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.command_prompt",),
        ),
        (
            "confirm_before_acceptance",
            "flags",
            "confirm-before",
            ("-c", "-y"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.confirm_before",),
        ),
        (
            "confirm_before_background",
            "flag",
            "confirm-before",
            ("-b",),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.confirm_before",),
        ),
        (
            "copy_mode_page_down",
            "flag",
            "copy-mode",
            ("-d",),
            "3.5",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.copy_mode",),
        ),
        (
            "display_menu_mouse",
            "flag",
            "display-menu",
            ("-M",),
            "3.5",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.display_menu",),
        ),
        (
            "display_menu_styles",
            "flags",
            "display-menu",
            ("-C", "-b", "-s", "-S", "-H"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.display_menu",),
        ),
        (
            "display_message_client",
            "flag",
            "display-message",
            ("-c",),
            "3.3",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.cmd",),
        ),
        (
            "display_message_literal",
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
        (
            "display_message_update_pane",
            "flag",
            "display-message",
            ("-C",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_message",),
        ),
        (
            "display_popup_3_3_options",
            "flags",
            "display-popup",
            ("-T", "-b", "-s", "-S", "-e", "-B"),
            "3.3",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_popup",),
        ),
        (
            "display_popup_3_6_key_policy",
            "flags",
            "display-popup",
            ("-k", "-N"),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.display_popup",),
        ),
        (
            "hook_scope_pane_window_set",
            "flags",
            "set-hook",
            ("-p", "-w"),
            "3.2",
            "unknown",
            "not_applicable_below_supported_floor",
            (
                "libtmux.hooks:HooksMixin.run_hook",
                "libtmux.hooks:HooksMixin.set_hook",
                "libtmux.hooks:HooksMixin.unset_hook",
            ),
        ),
        (
            "hook_scope_pane_window_show",
            "flags",
            "show-hooks",
            ("-p", "-w"),
            "3.2",
            "unknown",
            "not_applicable_below_supported_floor",
            (
                "libtmux.hooks:HooksMixin.show_hook",
                "libtmux.hooks:HooksMixin.show_hooks",
            ),
        ),
        (
            "kill_session_group",
            "flag",
            "kill-session",
            ("-g",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.session:Session.kill",),
        ),
        (
            "list_keys_format",
            "flag",
            "list-keys",
            ("-F",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.list_keys",),
        ),
        (
            "new_pane_command",
            "command",
            "new-pane",
            (),
            "3.7",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.pane:Pane.new_pane",),
        ),
        (
            "paste_buffer_no_vis",
            "flag",
            "paste-buffer",
            ("-S",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.paste_buffer",),
        ),
        (
            "refresh_client_clipboard_query",
            "semantic_transition",
            "refresh-client",
            ("-l",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.refresh_client",),
        ),
        (
            "run_shell_arguments",
            "positional_arguments",
            "run-shell",
            (),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        (
            "run_shell_show_stderr",
            "flag",
            "run-shell",
            ("-E",),
            "3.6",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        (
            "run_shell_working_directory",
            "flag",
            "run-shell",
            ("-c",),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.server:Server.run_shell",),
        ),
        (
            "send_keys_client_keys",
            "flags",
            "send-keys",
            ("-K", "-c"),
            "3.4",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.send_keys",),
        ),
        (
            "server_access_command",
            "command",
            "server-access",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.server_access",),
        ),
        (
            "show_prompt_history_command",
            "command",
            "show-prompt-history",
            (),
            "3.3",
            "unknown",
            "throw_unsupported_version",
            ("libtmux.server:Server.show_prompt_history",),
        ),
        (
            "split_window_appearance",
            "flags",
            "split-window",
            ("-s", "-S", "-R", "-m", "-k"),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.split",),
        ),
        (
            "split_window_empty",
            "flag",
            "split-window",
            ("-E",),
            "3.7",
            "unknown",
            "warn_and_ignore",
            ("libtmux.pane:Pane.split",),
        ),
    )
    capabilities.extend(
        {
            "capability": name,
            "evidenceStatus": "pending",
            "featureKind": feature_kind,
            "introducedIn": introduced_in,
            "kind": "command_gate",
            "namedRealServerTest": (
                "tests/LibTmux.IntegrationTests/Versioning/"
                "VersionParityTests.cs::"
                + "".join(part.title() for part in name.split("_"))
            ),
            "pythonSourceSymbolIds": list(source_symbol_ids),
            "policyOwnerComponents": list(policy_owner_components[name]),
            "policyProofContract": {
                "supportedBoundary": "exact_argv_single_dispatch",
                "unsupportedBoundary": unsupported_proof_by_behavior[
                    unsupported_behavior
                ],
            },
            "removedIn": removed_in,
            "tmuxCommand": tmux_command,
            "tmuxFlags": list(tmux_flags),
            "tmuxSourceEndpoints": {
                "3.2a": f"{TMUX_SOURCE_BASE_URL}/3.2a",
                "3.7b": f"{TMUX_SOURCE_BASE_URL}/3.7b",
            },
            "tmuxTransitionSources": (
                {
                    version: f"{TMUX_SOURCE_BLOB_URL}/{version}/cmd-break-pane.c"
                    for version in sorted({introduced_in, removed_in} - {"unknown"})
                }
                if name == "break_pane_3_7_workaround"
                else (
                    {
                        "3.7": [
                            f"{TMUX_SOURCE_BLOB_URL}/3.7/CHANGES",
                            f"{TMUX_SOURCE_BLOB_URL}/3.7/cmd-refresh-client.c",
                        ]
                    }
                    if name == "refresh_client_clipboard_query"
                    else {
                        version: f"{TMUX_SOURCE_BLOB_URL}/{version}/tmux.1"
                        for version in sorted({introduced_in, removed_in} - {"unknown"})
                    }
                )
            ),
            "unsupportedBehavior": unsupported_behavior,
            "wrapperPolicyTests": [
                (
                    f"{policy_test_files_by_component[component]}::"
                    + "".join(part.title() for part in name.split("_"))
                    + "VersionPolicy"
                )
                for component in policy_owner_components[name]
            ],
            **(
                {"supportRange": "baseline"}
                if name.startswith("hook_scope_pane_window_")
                else {}
            ),
            **(
                {
                    "semanticTransition": {
                        "after": "query_and_store_buffer_only",
                        "before": "query_with_optional_target_pane_forwarding",
                    },
                    "surfacePresentBy": "3.2a",
                }
                if name == "refresh_client_clipboard_query"
                else {}
            ),
            **(
                {"proofTmuxVersions": ["3.7", "3.7a"]}
                if name == "break_pane_3_7_workaround"
                else {}
            ),
        }
        for (
            name,
            feature_kind,
            tmux_command,
            tmux_flags,
            introduced_in,
            removed_in,
            unsupported_behavior,
            source_symbol_ids,
        ) in command_gates
    )
    return {"capabilities": capabilities}


def preserve_version_reconciliation(
    seed: dict[str, t.Any],
    existing: object,
) -> dict[str, t.Any]:
    """Preserve evidence-owned fields when immutable seed fields still match.

    Examples
    --------
    >>> current = version_deltas()
    >>> preserve_version_reconciliation(version_deltas(), current) == current
    True
    """
    if not isinstance(existing, dict):
        return seed
    existing_rows = existing.get("capabilities")
    if not isinstance(existing_rows, list):
        return seed
    by_capability = {
        row.get("capability"): row for row in existing_rows if isinstance(row, dict)
    }
    for seed_row in seed["capabilities"]:
        existing_row = by_capability.get(seed_row["capability"])
        if (
            not isinstance(existing_row, dict)
            or existing_row.get("evidenceStatus") != "verified"
            or not isinstance(existing_row.get("evidence"), dict)
            or any(
                existing_row.get(key) != value
                for key, value in seed_row.items()
                if key != "evidenceStatus"
            )
        ):
            continue
        seed_row["evidenceStatus"] = "verified"
        seed_row["evidence"] = existing_row["evidence"]
    return seed


def existing_version_deltas() -> object:
    """Load the current mutable version evidence when it is valid JSON.

    Examples
    --------
    >>> isinstance(existing_version_deltas(), (dict, type(None)))
    True
    """
    path = DOCUMENT_ROOT / "version-deltas.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return None


def error_policies(
    inventory: dict[str, t.Any] | None = None,
) -> dict[str, t.Any]:
    """Build the seed error and compatibility policy document.

    Examples
    --------
    >>> error_policies()["policies"][0]["name"]
    'command_specific_errors'
    """
    source_inventory = build_inventory() if inventory is None else inventory
    raising_tombstone_ids = sorted(
        row["id"]
        for row in source_inventory["symbols"]
        if row["kind"] == "raising_tombstone"
    )
    return {
        "policies": [
            {
                "name": "command_specific_errors",
                "mappings": [
                    {
                        "csharpExceptionId": "T:LibTmux.TmuxCommandException",
                        "errorSymbolId": "libtmux.exc:LibTmuxException",
                        "errorHandlerSymbolId": "libtmux.common:raise_if_stderr",
                        "sourceSymbolId": "libtmux.server:Server.attach_session",
                        "tmuxCommand": "attach-session",
                    },
                    {
                        "condition": "preexisting_named_session_without_replacement",
                        "csharpExceptionId": "T:LibTmux.TmuxSessionExistsException",
                        "errorSymbolId": "libtmux.exc:TmuxSessionExists",
                        "sourceSymbolId": "libtmux.server:Server.new_session",
                        "tmuxCommand": "new-session",
                    },
                ],
            },
            {
                "name": "display_message_stderr",
                "mappings": [
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
                ],
            },
            {
                "name": "has_session",
                "mappings": [
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.HasSessionAsync("
                            "string,bool,CancellationToken)"
                        ),
                        "exitCodeDisposition": "zero_true_nonzero_false",
                        "sourceSymbolId": "libtmux.server:Server.has_session",
                        "tmuxCommand": "has-session",
                        "transportFailureDisposition": "throw",
                    }
                ],
            },
            {
                "name": "list_accessors",
                "mappings": [
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.GetAttachedSessionsAsync("
                            "CancellationToken)"
                        ),
                        "failureDisposition": "return_empty_on_any_list_failure",
                        "sourceSymbolId": "libtmux.server:Server.attached_sessions",
                        "tmuxCommands": ["list-sessions"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.GetClientsAsync(CancellationToken)"
                        ),
                        "failureDisposition": "return_empty_on_any_list_failure",
                        "sourceSymbolId": "libtmux.server:Server.clients",
                        "tmuxCommands": ["list-clients"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.GetPanesAsync(CancellationToken)"
                        ),
                        "failureDisposition": (
                            "return_empty_on_missing_daemon_or_socket"
                        ),
                        "sourceSymbolId": "libtmux.server:Server.panes",
                        "tmuxCommands": ["list-panes"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.SearchPanesAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.search_panes",
                        "tmuxCommands": ["list-panes"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.SearchSessionsAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.search_sessions",
                        "tmuxCommands": ["list-sessions"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.SearchWindowsAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.search_windows",
                        "tmuxCommands": ["list-windows"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
                        ),
                        "failureDisposition": "return_empty_on_any_list_failure",
                        "sourceSymbolId": "libtmux.server:Server.sessions",
                        "tmuxCommands": ["list-sessions"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.GetWindowsAsync(CancellationToken)"
                        ),
                        "failureDisposition": (
                            "return_empty_on_missing_daemon_or_socket"
                        ),
                        "sourceSymbolId": "libtmux.server:Server.windows",
                        "tmuxCommands": ["list-windows"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Session.GetPanesAsync(CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.session:Session.panes",
                        "tmuxCommands": ["list-panes"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Session.SearchPanesAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.session:Session.search_panes",
                        "tmuxCommands": ["list-panes"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Session.SearchWindowsAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.session:Session.search_windows",
                        "tmuxCommands": ["list-windows"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.session:Session.windows",
                        "tmuxCommands": ["list-windows"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Window.GetLinkedSessionsAsync(CancellationToken)"
                        ),
                        "failureDisposition": "return_empty_if_either_list_fails",
                        "sourceSymbolId": "libtmux.window:Window.linked_sessions",
                        "tmuxCommands": ["list-windows", "list-sessions"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.window:Window.panes",
                        "tmuxCommands": ["list-panes"],
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Window.SearchPanesAsync("
                            "UnsafeTmuxFilter,CancellationToken)"
                        ),
                        "failureDisposition": "throw",
                        "sourceSymbolId": "libtmux.window:Window.search_panes",
                        "tmuxCommands": ["list-panes"],
                    },
                ],
            },
            {
                "name": "liveness",
                "mappings": [
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.IsAliveAsync(CancellationToken)"
                        ),
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
                        "csharpMemberId": (
                            "M:LibTmux.Server.RaiseIfDeadAsync(CancellationToken)"
                        ),
                        "disposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.raise_if_dead",
                        "thrownFailures": [
                            "T:LibTmux.TmuxCommandException",
                            "T:LibTmux.TmuxCommandNotFoundException",
                            "T:LibTmux.TmuxTransportException",
                        ],
                        "tmuxCommand": "list-sessions",
                    },
                ],
            },
            {
                "name": "missing_daemon_commands",
                "mappings": [
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.KillAsync(CancellationToken)"
                        ),
                        "missingDaemonDisposition": "return_success",
                        "otherFailureDisposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.kill",
                        "tmuxCommand": "kill-server",
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Server.KillSessionAsync("
                            "string,CancellationToken)"
                        ),
                        "missingDaemonDisposition": "throw",
                        "otherFailureDisposition": "throw",
                        "sourceSymbolId": "libtmux.server:Server.kill_session",
                        "tmuxCommand": "kill-session",
                    },
                    {
                        "csharpMemberId": (
                            "M:LibTmux.Session.KillAsync("
                            "bool,bool,bool,CancellationToken)"
                        ),
                        "missingDaemonDisposition": "throw",
                        "otherFailureDisposition": "throw",
                        "sourceSymbolId": "libtmux.session:Session.kill",
                        "tmuxCommand": "kill-session",
                    },
                ],
            },
            {
                "name": "non_suppressible_errors",
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
            },
            {
                "name": "option_failures",
                "commands": [
                    "set-hook",
                    "set-option",
                    "show-hooks",
                    "show-options",
                ],
                "csharpExceptionId": "T:LibTmux.TmuxOptionException",
                "csharpHandlerId": (
                    "M:LibTmux.Internal.OptionFailure.ThrowIfFailed("
                    "TmuxCommandResult,string)"
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
                    {
                        "match": "fallback",
                        "pythonErrorSymbolId": "libtmux.exc:OptionError",
                    },
                ],
                "pythonHandlerSymbolId": "libtmux.options:handle_option_error",
            },
            {
                "name": "raising_tombstones",
                "symbolIds": raising_tombstone_ids,
            },
            {
                "name": "warning_aliases",
                "symbolIds": [
                    "libtmux.window:Window.set_window_option",
                    "libtmux.window:Window.show_window_option",
                    "libtmux.window:Window.show_window_options",
                ],
            },
        ]
    }


def encode(document: dict[str, t.Any]) -> str:
    r"""Encode a deterministic JSON document with a trailing newline.

    Examples
    --------
    >>> encode({"b": 1, "a": 2})
    '{\n  "a": 2,\n  "b": 1\n}\n'
    """
    return json.dumps(document, indent=2, sort_keys=True) + "\n"


def documents() -> dict[str, dict[str, t.Any]]:
    """Build every checked-in document emitted by this generator.

    Examples
    --------
    >>> "python-public-api.json" in documents()
    True
    """
    inventory = build_inventory()
    return {
        "error-policies.json": error_policies(inventory),
        "parity-ledger.json": build_ledger(inventory, existing_ledger()),
        "python-public-api.json": inventory,
        "version-deltas.json": preserve_version_reconciliation(
            version_deltas(), existing_version_deltas()
        ),
    }


def write_documents() -> None:
    """Write every generated parity JSON document."""
    DOCUMENT_ROOT.mkdir(parents=True, exist_ok=True)
    for filename, document in documents().items():
        (DOCUMENT_ROOT / filename).write_text(encode(document), encoding="utf-8")


def documents_are_current() -> bool:
    """Return whether checked-in JSON matches a fresh source generation.

    Examples
    --------
    >>> isinstance(documents_are_current(), bool)
    True
    """
    return all(
        path.is_file() and path.read_text(encoding="utf-8") == encode(document)
        for filename, document in documents().items()
        for path in [DOCUMENT_ROOT / filename]
    )


def parse_args(arguments: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse generator command-line arguments.

    Examples
    --------
    >>> parse_args(["--check"]).check
    True
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    return parser.parse_args(arguments)


def main(arguments: t.Sequence[str] | None = None) -> int:
    """Generate JSON documents or verify their source-derived contents.

    Examples
    --------
    >>> callable(main)
    True
    """
    arguments_parsed = parse_args(arguments)
    if arguments_parsed.check:
        return 0 if documents_are_current() else 1
    write_documents()
    return 0


if __name__ == "__main__":
    sys.exit(main())
