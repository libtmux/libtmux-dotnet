"""Regression tests for the generated Python parity inventory."""

from __future__ import annotations

import ast
import json
import pathlib
import runpy
import sys
import typing as t

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parents[3]))

from eng.parity import python_source  # noqa: E402

PROPERTY_TOMBSTONE_REPLACEMENTS = {
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


def documentation_path(filename: str) -> pathlib.Path:
    """Return the generated parity documentation path.

    Examples
    --------
    >>> documentation_path("python-public-api.json").name
    'python-public-api.json'
    """
    return pathlib.Path(__file__).parents[3] / "docs" / "parity" / filename


def load_document(filename: str) -> dict[str, t.Any]:
    """Load one generated parity document.

    Examples
    --------
    >>> isinstance(load_document("python-public-api.json"), dict)
    True
    """
    with documentation_path(filename).open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def load_generator() -> dict[str, t.Any]:
    """Load the parity generator as a test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "generate_inventory.py")
    )


def read_pinned_source(path: str) -> str:
    """Read a representative source file from the inventory boundary.

    Examples
    --------
    >>> "class Obj" in read_pinned_source("src/libtmux/neo.py")
    True
    """
    return python_source.show(path)


@pytest.fixture()
def inventory() -> dict[str, t.Any]:
    """Provide the generated Python public API inventory.

    Examples
    --------
    >>> isinstance(load_document("python-public-api.json"), dict)
    True
    """
    return load_document("python-public-api.json")


@pytest.fixture()
def ledger() -> dict[str, t.Any]:
    """Provide the generated C# parity ledger.

    Examples
    --------
    >>> isinstance(load_document("parity-ledger.json"), dict)
    True
    """
    return load_document("parity-ledger.json")


def test_inventory_contains_documented_public_modules(
    inventory: dict[str, t.Any],
) -> None:
    """Inventory every documented and compatibility-owned module."""
    modules = {row["module"] for row in inventory["symbols"]}
    assert {
        "libtmux",
        "libtmux.server",
        "libtmux.session",
        "libtmux.window",
        "libtmux.pane",
        "libtmux.client",
        "libtmux.common",
        "libtmux.constants",
        "libtmux.formats",
        "libtmux.options",
        "libtmux.hooks",
        "libtmux.exc",
        "libtmux.neo",
        "libtmux._internal.query_list",
        "libtmux.test",
        "libtmux.test.constants",
        "libtmux.test.environment",
        "libtmux.test.random",
        "libtmux.test.retry",
        "libtmux.test.temporary",
        "libtmux.pytest_plugin",
    } == modules


def test_every_inventory_symbol_has_one_ledger_row(
    inventory: dict[str, t.Any],
    ledger: dict[str, t.Any],
) -> None:
    """Map every inventoried symbol to exactly one parity row."""
    inventory_ids = {row["id"] for row in inventory["symbols"]}
    ledger_ids = [row["pythonSymbolId"] for row in ledger["rows"]]
    assert set(ledger_ids) == inventory_ids
    assert len(ledger_ids) == len(set(ledger_ids))


def test_inventory_preserves_pinned_public_fields_properties_and_aliases(
    inventory: dict[str, t.Any],
) -> None:
    """Inventory representative public symbols declared at the pinned source."""
    assert "pane_id: str | None" in read_pinned_source("src/libtmux/neo.py")
    query_list_source = read_pinned_source("src/libtmux/_internal/query_list.py")
    assert "data: Sequence[T]" in query_list_source
    assert "pk_key: str | None" in query_list_source
    assert 'Up = "UP"' in read_pinned_source("src/libtmux/constants.py")
    client_source = read_pinned_source("src/libtmux/client.py")
    assert "@property\n    def attached_session" in client_source
    assert "SessionDict = dict[str, t.Any]" in read_pinned_source(
        "src/libtmux/common.py"
    )

    rows = {row["id"]: row for row in inventory["symbols"]}
    expected = {
        "libtmux.neo:Obj.pane_id": "field",
        "libtmux._internal.query_list:QueryList.data": "field",
        "libtmux._internal.query_list:QueryList.pk_key": "field",
        "libtmux.constants:ResizeAdjustmentDirection.Up": "enum_member",
        "libtmux.client:Client.attached_session": "property",
        "libtmux.common:SessionDict": "type_alias",
    }
    assert {symbol_id: rows[symbol_id]["kind"] for symbol_id in expected} == expected


def test_property_tombstones_are_classified_before_properties() -> None:
    """Classify a raising property by behavior, not decorator shape."""
    generator = load_generator()
    rows: dict[str, dict[str, str]] = {}
    node = ast.parse(
        "@property\ndef retired(self):\n    raise DeprecatedError()\n"
    ).body[0]

    generator["add_function_record"](
        rows,
        "libtmux.sample",
        "src/libtmux/sample.py",
        "Sample.retired",
        node,
    )

    assert rows["libtmux.sample:Sample.retired"]["kind"] == "raising_tombstone"


def test_inventory_marks_every_raising_property_tombstone(
    inventory: dict[str, t.Any],
) -> None:
    """Keep all nine pinned raising properties out of the C# surface."""
    kinds = {row["id"]: row["kind"] for row in inventory["symbols"]}

    assert {
        symbol_id: kinds[symbol_id] for symbol_id in PROPERTY_TOMBSTONE_REPLACEMENTS
    } == dict.fromkeys(PROPERTY_TOMBSTONE_REPLACEMENTS, "raising_tombstone")


def test_generator_derives_every_pinned_tombstone_replacement(
    inventory: dict[str, t.Any],
) -> None:
    """Read each compatibility replacement from its exact DeprecatedError."""
    generator = load_generator()
    node = ast.parse(
        "def retired():\n    raise exc.DeprecatedError(replacement='Current.call()')\n"
    ).body[0]

    assert generator["raising_tombstone_replacement"](node) == "Current.call()"
    replacements = generator["pinned_tombstone_replacements"]()
    tombstone_ids = {
        row["id"] for row in inventory["symbols"] if row["kind"] == "raising_tombstone"
    }
    assert set(replacements) == tombstone_ids
    assert replacements["libtmux.pane:Pane.__getitem__"] == (
        "direct attribute access (e.g., pane.pane_id)"
    )
    assert replacements["libtmux.window:Window.split_window"] == "Window.split()"


def test_warning_aliases_are_only_callable_level_delegators(
    inventory: dict[str, t.Any],
) -> None:
    """Keep deprecated-parameter branches distinct from deprecated wrappers."""
    options_source = read_pinned_source("src/libtmux/options.py")
    hooks_source = read_pinned_source("src/libtmux/hooks.py")
    window_source = read_pinned_source("src/libtmux/window.py")
    assert "def set_option(" in options_source
    assert "def show_option(" in options_source
    assert "def set_hook(" in hooks_source
    assert "return self.set_option(option=option, value=value)" in window_source

    rows = {row["id"]: row["kind"] for row in inventory["symbols"]}
    assert {
        symbol_id: rows[symbol_id]
        for symbol_id in (
            "libtmux.options:OptionsMixin.set_option",
            "libtmux.options:OptionsMixin.show_option",
            "libtmux.hooks:HooksMixin.set_hook",
            "libtmux.window:Window.set_window_option",
            "libtmux.window:Window.show_window_option",
            "libtmux.window:Window.show_window_options",
        )
    } == {
        "libtmux.options:OptionsMixin.set_option": "method",
        "libtmux.options:OptionsMixin.show_option": "method",
        "libtmux.hooks:HooksMixin.set_hook": "method",
        "libtmux.window:Window.set_window_option": "warning_alias",
        "libtmux.window:Window.show_window_option": "warning_alias",
        "libtmux.window:Window.show_window_options": "warning_alias",
    }


def test_ledger_regeneration_preserves_only_source_bound_approval() -> None:
    """Keep reviewed destinations while forcing changed symbols back to planning."""
    generator = load_generator()
    inventory = {
        "symbols": [
            {
                "id": "libtmux.sample:Sample.call",
                "kind": "method",
                "module": "libtmux.sample",
                "qualifiedName": "Sample.call",
                "sourceUrl": "https://example.invalid/pinned",
            }
        ]
    }
    approved = generator["build_ledger"](inventory)
    approved_row = approved["rows"][0]
    approved_row.update(
        {
            "componentId": 3,
            "csharpDestination": "M:LibTmux.Sample.CallAsync(CancellationToken)",
            "destinationStatus": "approved",
            "testPath": "tests/Component03ParityTests.cs",
        }
    )

    preserved = generator["build_ledger"](inventory, approved)["rows"][0]
    assert preserved["destinationStatus"] == "approved"
    assert preserved["componentId"] == 3
    assert preserved["testPath"] == "tests/Component03ParityTests.cs"

    approved_row["sourceUrl"] = "https://example.invalid/other"
    reset = generator["build_ledger"](inventory, approved)["rows"][0]
    assert reset["destinationStatus"] == "planned"
    assert "componentId" not in reset


def test_ledger_generation_emits_maximum_version_adaptation() -> None:
    """Generate the reviewed semantic mapping for Python's version ceiling."""
    generator = load_generator()
    inventory = {
        "symbols": [
            {
                "id": "libtmux.common:TMUX_MAX_VERSION",
                "kind": "constant",
                "module": "libtmux.common",
                "qualifiedName": "TMUX_MAX_VERSION",
                "sourceUrl": "https://example.invalid/pinned",
            }
        ]
    }

    row = generator["build_ledger"](inventory)["rows"][0]

    assert row["behavior"] == (
        "Semantic adaptation: map Python TMUX_MAX_VERSION 3.7 to "
        "MaximumTestedTmuxVersion 3.7c, the highest required tested version"
    )


def test_ledger_regeneration_moves_only_canonical_window_and_pane_lookup() -> None:
    """Move lookup materialization to C4 without erasing unrelated evidence."""
    generator = load_generator()
    moved = {
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.window:Window.from_window_id",
    }
    current_inventory = {
        "symbols": [
            {
                "id": symbol_id,
                "kind": "method",
                "module": symbol_id.split(":", 1)[0],
                "qualifiedName": symbol_id.split(":", 1)[1],
                "sourceUrl": f"https://example.invalid/{index}",
            }
            for index, symbol_id in enumerate(
                (*sorted(moved), "libtmux.sample:Sample.call")
            )
        ]
    }
    current_ledger = generator["build_ledger"](current_inventory)
    for row in current_ledger["rows"]:
        row.update(
            {
                "componentId": 2,
                "evidenceStatus": "verified",
                "implementationStatus": "implemented",
                "testPath": "tests/Component02ParityTests.cs",
            }
        )
    regenerated = generator["build_ledger"](current_inventory, current_ledger)
    before = {row["pythonSymbolId"]: row for row in current_ledger["rows"]}
    after = {row["pythonSymbolId"]: row for row in regenerated["rows"]}

    assert {
        symbol_id: {
            key: value
            for key, value in after[symbol_id].items()
            if value != before[symbol_id].get(key)
        }
        for symbol_id in before
        if after[symbol_id] != before[symbol_id]
    } == {
        symbol_id: {
            "componentId": 4,
            "evidenceStatus": "none",
            "implementationStatus": "not_started",
            "testPath": (
                "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
            ),
        }
        for symbol_id in moved
    }

    for row in regenerated["rows"]:
        if row["pythonSymbolId"] in moved:
            row["implementationStatus"] = "implemented"
            row["evidenceStatus"] = "verified"
    assert generator["build_ledger"](current_inventory, regenerated) == regenerated
