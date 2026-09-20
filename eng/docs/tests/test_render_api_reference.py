"""Prove the API reference contains only the approved public surface."""

from __future__ import annotations

import pathlib
import runpy
import typing as t



def load_renderer() -> dict[str, t.Any]:
    """Load the renderer as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "render_api_reference.py")
    )


def test_member_reader_keeps_public_facade_and_drops_generated_types(
    tmp_path: pathlib.Path,
) -> None:
    """Compiler-generated documentation must not become package API docs."""
    documentation = tmp_path / "LibTmux.xml"
    documentation.write_text(
        """<?xml version="1.0"?>
<doc>
  <members>
    <member name="T:LibTmux.PsmuxConnectionOptions">
      <summary>Configures the preview.</summary>
    </member>
    <member name="P:LibTmux.PsmuxConnectionOptions.DataDirectory">
      <summary>Gets the data directory.</summary>
    </member>
    <member name="M:LibTmux.PsmuxConnectionOptions.ValidateInternalState">
      <summary>Must not expose an internal member of a public type.</summary>
    </member>
    <member name="T:LibTmux.Internal.HiddenType">
      <summary>Must not be published.</summary>
    </member>
    <member name="T:System.Text.RegularExpressions.Generated.VersionRegex_0">
      <summary>Must not be published.</summary>
    </member>
  </members>
</doc>
""",
        encoding="utf-8",
    )
    read_members = load_renderer()["read_members"]

    members = read_members(
        documentation,
        frozenset(
            {
                "T:LibTmux.PsmuxConnectionOptions",
                "P:LibTmux.PsmuxConnectionOptions.DataDirectory",
            }
        ),
    )

    assert members == {
        "T:LibTmux.PsmuxConnectionOptions": "Configures the preview.",
        "P:LibTmux.PsmuxConnectionOptions.DataDirectory": "Gets the data directory.",
    }


def test_member_reader_uses_one_canonical_type_summary(tmp_path: pathlib.Path) -> None:
    """A canonical partial-type summary is the summary readers receive."""
    documentation = tmp_path / "LibTmux.xml"
    documentation.write_text(
        """<?xml version="1.0"?>
<doc>
  <members>
    <member name="T:LibTmux.Server">
      <summary>Represents an immutable server handle and snapshot.</summary>
    </member>
  </members>
</doc>
""",
        encoding="utf-8",
    )
    read_members = load_renderer()["read_members"]

    members = read_members(documentation, frozenset({"T:LibTmux.Server"}))

    assert members == {
        "T:LibTmux.Server": "Represents an immutable server handle and snapshot."
    }


def test_renderer_preserves_generic_metadata_names_as_code() -> None:
    """Generic arity markers must not terminate their Markdown code span."""
    render = load_renderer()["render"]

    rendered = render(
        {"T:LibTmux.CapturedRelation`1": "Holds captured children."}
    )

    assert "| ``LibTmux.CapturedRelation`1`` | Holds captured children. |" in rendered

    rendered = render(
        {"M:LibTmux.Query.QueryExtensions.Compile``1": "Compiles a query."}
    )

    assert "| ```LibTmux.Query.QueryExtensions.Compile``1``` | Compiles a query. |" in rendered
