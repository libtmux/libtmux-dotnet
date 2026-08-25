"""Exercise source-grounded snippet rendering."""

from __future__ import annotations

import pathlib
import runpy
import typing as t

import pytest


def load_synchronizer() -> dict[str, t.Any]:
    """Load the synchronizer without making its directory a package."""
    return runpy.run_path(str(pathlib.Path(__file__).parents[1] / "sync_snippets.py"))


def test_composed_anchor_concatenates_regions_in_declared_order() -> None:
    """A README can publish setup and a shared body without duplicating either."""
    synchronizer = load_synchronizer()
    used: set[str] = set()

    rendered = synchronizer["render"](
        "ConnectAndBuild+BuildHierarchy",
        "usings: LibTmux",
        {
            "ConnectAndBuild": "Server server = await Server.ConnectAsync();",
            "BuildHierarchy": "await server.CreateSessionAsync(new NewSessionRequest(name: \"build\"));",
        },
        used,
    )

    assert rendered == """```csharp
using LibTmux;

Server server = await Server.ConnectAsync();
await server.CreateSessionAsync(new NewSessionRequest(name: \"build\"));
```
"""
    assert used == {"ConnectAndBuild", "BuildHierarchy"}


def test_composed_anchor_requires_every_region() -> None:
    """A misspelled component must fail instead of silently publishing half an example."""
    synchronizer = load_synchronizer()

    with pytest.raises(SystemExit, match="BuildHierarchy"):
        synchronizer["render"](
            "ConnectAndBuild+BuildHierarchy",
            "",
            {"ConnectAndBuild": "Server server = await Server.ConnectAsync();"},
            set(),
        )
