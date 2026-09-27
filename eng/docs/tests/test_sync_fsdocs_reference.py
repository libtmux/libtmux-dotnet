"""Check the generated F# member reference and its GitHub links."""

from __future__ import annotations

import pathlib
import runpy

import pytest


def synchronizer() -> dict:
    root = pathlib.Path(__file__).parents[3]
    return runpy.run_path(str(root / "eng/docs/sync_fsdocs_reference.py"))


def test_reference_links_and_source_icons_render_on_github() -> None:
    pages = {
        "index.md": (
            "# API Reference\n\n## Available Namespaces\n\n"
            "* [Filter](../reference/filter) -  \n  \n"
        ),
        "filter.md": (
            "## Filter Module\n"
            '<a href="../reference/index">Index</a>\n'
            '<code><a href="https://learn.microsoft.com/dotnet/api/libtmux.server">'
            'Server</a></code>\n'
            '<code><a href="https://learn.microsoft.com/dotnet/api/system.string">'
            'String</a></code>\n'
            '[IncompleteSnapshotException](https://learn.microsoft.com/dotnet/api/'
            'libtmux.incompletesnapshotexception)The value is unavailable.\n'
            "[![Link to source code](../content/img/github.png)]"
            "(https://github.com/libtmux/libtmux-dotnet/blob/master/src/Query.fs#L1-L2)\n"
        ),
    }

    normalized = synchronizer()["normalize_documents"](pages)

    assert "[Filter](../reference/filter.md)" in normalized["index.md"]
    assert normalized["index.md"].startswith("# API reference\n\n## Available namespaces")
    assert "[Filter](../reference/filter.md) -" not in normalized["index.md"]
    assert not any(line.endswith(" ") for line in normalized["index.md"].splitlines())
    assert normalized["filter.md"].startswith("## Filter module\n")
    assert 'href="../reference/index.md"' in normalized["filter.md"]
    assert "<code>Server</code>" in normalized["filter.md"]
    assert "`IncompleteSnapshotException` The" in normalized["filter.md"]
    assert 'href="https://learn.microsoft.com/dotnet/api/system.string"' in normalized["filter.md"]
    assert "dotnet/api/libtmux." not in normalized["filter.md"]
    assert "[Source](https://github.com/" in normalized["filter.md"]
    assert "../content/" not in normalized["filter.md"]


@pytest.mark.parametrize(
    "body",
    [
        "[Missing](../reference/missing)",
        "[Local](/home/example/src/Query.fs)",
        "[Filter](https://github.com/libtmux/libtmux-dotnet/reference/filter)",
        "![Source](../content/img/github.png)",
    ],
)
def test_broken_or_local_links_fail(body: str) -> None:
    with pytest.raises(ValueError):
        synchronizer()["normalize_documents"]({"index.md": body})


def test_check_rejects_stale_and_extra_pages(tmp_path: pathlib.Path) -> None:
    expected = {"index.md": "# API\n"}
    (tmp_path / "index.md").write_text("old\n", encoding="utf-8")
    (tmp_path / "obsolete.md").write_text("old\n", encoding="utf-8")

    compare = synchronizer()["compare_documents"]
    errors = compare(expected, tmp_path)

    assert any("index.md" in error for error in errors)
    assert any("obsolete.md" in error for error in errors)


@pytest.mark.parametrize(
    "target",
    ["", "https://github.com/libtmux/libtmux-dotnet/blob/mastersrc/Query.fs#L1"],
)
def test_invalid_fsdocs_source_link_is_rejected(target: str) -> None:
    """CI path mapping must yield a working source URL, not an orphan icon."""
    page = f"[![Link to source code](../content/img/github.png)]({target})\n"

    with pytest.raises(ValueError, match="invalid fsdocs source link"):
        synchronizer()["normalize_documents"]({"index.md": page})
