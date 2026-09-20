"""Exact compiler identities must control documentation membership."""
import json
import pathlib
import runpy

import pytest


def test_inventory_preserves_compiled_fsharp_contracts():
    root = pathlib.Path(__file__).parents[3]
    inventory = json.loads((root / "artifacts/api-inventory.json").read_text())
    members = {
        member["id"]: member
        for member in inventory["members"]
        if member["package"] == "LibTmux.FSharp"
    }
    assert members, "F# compiled symbols are missing from the inventory"
    capture = members[
        "M:LibTmux.FSharp.Server.capture(System.Threading.CancellationToken,"
        "LibTmux.SnapshotDepth,LibTmux.Server)"
    ]
    assert capture["argumentGroups"] == [1, 1, 1]
    assert capture["documentation"]
    assert members["T:LibTmux.FSharp.CardinalityError.NoMatches"]["kind"] == "unionCase"
    value = members["M:LibTmux.FSharp.Snapshot.value``1(LibTmux.CapturedValue{``0})"]
    assert "not struct" in value["signature"]


def test_same_arity_overload_cannot_supply_missing_documentation(tmp_path):
    renderer = runpy.run_path(str(pathlib.Path(__file__).parents[1] / "render_api_reference.py"))
    xml = tmp_path / "Api.xml"
    xml.write_text('<doc><members><member name="M:Example.Read(System.String)"><summary>Reads text.</summary></member></members></doc>')
    with pytest.raises(ValueError, match="M:Example.Read\\(System.Int32\\)"):
        renderer["read_members"](xml, frozenset({"M:Example.Read(System.Int32)"}))


@pytest.mark.parametrize("identifier", [
    "M:Example.#ctor(System.Int32)",
    "M:Example.Read``1(``0)",
    "M:Example.Read(System.Int32)",
])
def test_compiler_identity_is_preserved(tmp_path, identifier):
    renderer = runpy.run_path(str(pathlib.Path(__file__).parents[1] / "render_api_reference.py"))
    xml = tmp_path / "Api.xml"
    xml.write_text(f'<doc><members><member name="{identifier}"><summary>Reads a value.</summary></member></members></doc>')
    assert renderer["read_members"](xml, frozenset({identifier})) == {identifier: "Reads a value."}


def test_membership_uses_compiler_visibility_and_implicit_flag(tmp_path):
    import json
    renderer = runpy.run_path(str(pathlib.Path(__file__).parents[1] / "render_api_reference.py"))
    base = {"package": "LibTmux", "visibility": "public", "implicitDeclaration": False, "accessor": False}
    inventory = tmp_path / "inventory.json"
    inventory.write_text(json.dumps({"members": [
        {**base, "id": "M:Example.Read(System.Int32)"},
        {**base, "id": "M:Example.Read(System.String)", "visibility": "internal"},
        {**base, "id": "M:Example.#ctor", "implicitDeclaration": True},
        {**base, "id": "M:Example.get_Value", "accessor": True},
    ]}))
    assert renderer["public_member_ids"](inventory) == {"M:Example.Read(System.Int32)"}


def test_check_rejects_stale_generated_output(tmp_path, capsys):
    import json
    renderer = runpy.run_path(str(pathlib.Path(__file__).parents[1] / "render_api_reference.py"))
    inventory = tmp_path / "inventory.json"
    inventory.write_text(json.dumps({"members": [{
        "id": "T:LibTmux.Example", "package": "LibTmux", "visibility": "public",
        "implicitDeclaration": False, "accessor": False,
        "documentation": '<member name="T:LibTmux.Example"><summary>Describes an example.</summary></member>',
    }]}))
    output = tmp_path / "README.md"
    context = renderer["main"].__globals__
    context["INVENTORY_PATH"] = inventory
    context["OUTPUT_PATH"] = output
    assert renderer["main"]([]) == 0
    assert renderer["main"](["--check"]) == 0
    output.write_text("stale")
    assert renderer["main"](["--check"]) == 1
    assert "differs" in capsys.readouterr().err
