"""Prove the API reference contains only the approved public surface."""

from __future__ import annotations

import pathlib
import runpy
import typing as t

import pytest


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


def test_fsharp_renderer_uses_compiled_signatures_and_declaring_modules() -> None:
    """F# readers need source signatures, not CLR documentation identifiers."""
    render_fsharp = load_renderer()["render_fsharp"]

    rendered = render_fsharp(
        [
            {
                "id": "M:LibTmux.FSharp.Filter.eq``2(``0,LibTmux.FSharp.Field{``1,``0})",
                "declaringType": "T:LibTmux.FSharp.Filter",
                "kind": "member",
                "signature": "val eq: value: 'Value -> field: Field<'T,'Value> -> Filter<'T>",
                "summary": "Matches a field against a constant.",
            },
        ]
    )

    assert "# F# API reference" in rendered
    assert "The [member reference](../fsharp-reference/reference/index.md) is the" in rendered
    assert "[LibTmux API reference](../api/README.md)" in rendered
    assert "## Filter" in rendered
    assert "`val eq: value: 'Value -> field: Field<'T,'Value> -> Filter<'T>`" in rendered
    assert "M:LibTmux.FSharp.Filter.eq" not in rendered


def test_fsharp_renderer_writes_names_as_source_spells_them() -> None:
    """Opened namespaces are noise; a core type named like a facade module keeps its own."""
    short_names = load_renderer()["short_names"]

    assert short_names(
        "val enter: cancellationToken: System.Threading.CancellationToken -> server: LibTmux.Server"
        " -> System.Threading.Tasks.Task<LibTmux.IControlModeSession>",
        {"Server"},
    ) == "val enter: cancellationToken: CancellationToken -> server: LibTmux.Server -> Task<IControlModeSession>"
    assert short_names("val toDocument: filter: Filter<'T> -> LibTmux.Query.QueryDocument", {"Query"}) == (
        "val toDocument: filter: Filter<'T> -> LibTmux.Query.QueryDocument"
    )
    assert short_names(
        "val cleanupFailure: error: Microsoft.FSharp.Core.exn -> Microsoft.FSharp.Core.exn Microsoft.FSharp.Core.option"
    ) == "val cleanupFailure: error: exn -> exn option"
    assert short_names("val name: LibTmux.FSharp.Field<LibTmux.Client,System.String>") == (
        "val name: Field<Client,String>"
    )
    assert short_names("val p: System.Reflection.PropertyInfo -> System.TimeSpan") == (
        "val p: System.Reflection.PropertyInfo -> TimeSpan"
    )


def test_fsharp_check_rejects_stale_generated_output(tmp_path: pathlib.Path, capsys) -> None:
    """The F# reference must be checked from the compiler inventory."""
    import json

    renderer = load_renderer()
    inventory = tmp_path / "inventory.json"
    inventory.write_text(json.dumps({"members": [{
        "id": "M:LibTmux.FSharp.Filter.eq``2(``0,LibTmux.FSharp.Field{``1,``0})",
        "package": "LibTmux.FSharp",
        "visibility": "public",
        "implicitDeclaration": False,
        "accessor": False,
        "declaringType": "T:LibTmux.FSharp.Filter",
        "kind": "member",
        "signature": "val eq: value: 'Value -> field: Field<'T,'Value> -> Filter<'T>",
        "documentation": '<member name="M:LibTmux.FSharp.Filter.eq``2(``0,LibTmux.FSharp.Field{``1,``0})"><summary>Matches a field against a constant.</summary></member>',
    }]}))
    output = tmp_path / "api.md"
    context = renderer["main"].__globals__
    context["INVENTORY_PATH"] = inventory
    context["FSHARP_OUTPUT_PATH"] = output

    assert renderer["main"](["--fsharp"]) == 0
    assert renderer["main"](["--fsharp", "--check"]) == 0
    output.write_text("stale")
    assert renderer["main"](["--fsharp", "--check"]) == 1
    assert "differs" in capsys.readouterr().err


def test_fsharp_renderer_names_fields_and_folds_a_module_into_its_type() -> None:
    """A record field reads as name and type; NameModule is the Name a reader writes."""
    render_fsharp = load_renderer()["render_fsharp"]

    rendered = render_fsharp(
        [
            {"id": "T:LibTmux.FSharp.SessionSpec", "declaringType": "", "kind": "record",
             "signature": "SessionSpec", "summary": "Describes a session."},
            {"id": "P:LibTmux.FSharp.SessionSpec.Name", "declaringType": "T:LibTmux.FSharp.SessionSpec",
             "kind": "field", "signature": "string", "summary": "The session's name."},
            {"id": "T:LibTmux.FSharp.SessionSpecModule", "declaringType": "", "kind": "module",
             "signature": "SessionSpecModule", "summary": "Starts session descriptions."},
            {"id": "M:LibTmux.FSharp.SessionSpecModule.named(System.String)",
             "declaringType": "T:LibTmux.FSharp.SessionSpecModule", "kind": "member",
             "signature": "val named: name: string -> SessionSpec", "summary": "Names a session."},
        ]
    )

    assert "| `Name: string` | The session's name. |" in rendered
    assert "| `module SessionSpec` | Starts session descriptions. |" in rendered
    assert "SessionSpecModule" not in rendered
    assert "[SessionSpec](#sessionspec)" in rendered


def test_fsharp_renderer_refuses_a_section_the_task_index_does_not_place() -> None:
    """A new module must be placed in the page's index before it renders."""
    render_fsharp = load_renderer()["render_fsharp"]

    with pytest.raises(ValueError, match="Unplaced"):
        render_fsharp(
            [{"id": "T:LibTmux.FSharp.Unplaced", "declaringType": "", "kind": "module",
              "signature": "Unplaced", "summary": "Not in the index."}]
        )


def test_fsharp_renderer_refuses_a_task_call_the_facade_lacks() -> None:
    """The task index names calls; one that was renamed or removed fails the render."""
    render_fsharp = load_renderer()["render_fsharp"]

    with pytest.raises(ValueError, match="Retry.ifNotSent, Retry.ifNotSentAfter"):
        render_fsharp(
            [{"id": "M:LibTmux.FSharp.Retry.retrying``1(System.Int32)", "declaringType": "T:LibTmux.FSharp.Retry",
              "kind": "member", "signature": "val retrying: int -> unit", "summary": "Not a task's call."}]
        )

    rendered = render_fsharp(
        [{"id": f"M:LibTmux.FSharp.Retry.{name}``1(System.Int32)", "declaringType": "T:LibTmux.FSharp.Retry",
          "kind": "member", "signature": f"val {name}: int -> unit", "summary": "Retries."}
         for name in ("ifNotSent", "ifNotSentAfter")]
    )
    assert "| Failures and retries | [`Retry.ifNotSent`](#retry), [`Retry.ifNotSentAfter`](#retry) |" in rendered
