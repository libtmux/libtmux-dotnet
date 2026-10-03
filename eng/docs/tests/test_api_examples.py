"""Reject incomplete setup and stale compiler attachments for complete programs."""

from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import shlex
import shutil

import pytest

from eng.docs.verify_api_examples import ROOT, run_consumers, unique_object, validate


@pytest.fixture
def example_tree(tmp_path: Path):
    """Copy only the source files needed by the manifest, without build outputs."""
    manifest = json.loads((ROOT / "examples/api/manifest.json").read_text())
    inventory = json.loads((ROOT / "artifacts/api-inventory.json").read_text())
    paths = ["Directory.Build.props", *manifest["setupFiles"]]
    paths.extend(profile["projectFile"] for profile in manifest["profiles"].values())
    paths.extend(entry["sourceFile"] for entry in manifest["examples"])
    for value in paths:
        destination = tmp_path / value
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / value, destination)
    return tmp_path, manifest, inventory


def test_native_manifest_covers_every_complete_program(example_tree):
    root, manifest, inventory = example_tree
    validate(root, manifest, inventory)
    fsharp = [entry for entry in manifest["examples"] if entry["profile"] == "fsharp"]
    csharp = [entry for entry in manifest["examples"] if entry["profile"] == "csharp"]
    assert len(fsharp) == 13
    assert len({target for entry in fsharp for target in entry["targets"]}) == 46
    assert len(csharp) == 7
    assert len({target for entry in csharp for target in entry["targets"]}) == 33


def test_duplicate_json_fields_are_rejected():
    with pytest.raises(ValueError, match="Duplicate JSON key"):
        json.loads('{"targets": ["real"], "targets": ["other"]}', object_pairs_hook=unique_object)


@pytest.mark.parametrize("change, message", [
    (lambda data: data["examples"].pop(), "every complete program"),
    (lambda data: data["examples"].append(copy.deepcopy(data["examples"][0])), "Duplicate example"),
    (lambda data: data["examples"][0].update(targets=[]), "needs compiler targets"),
    (lambda data: data["examples"][0]["targets"].append("M:LibTmux.FSharp.Server.missing"), "no public compiler target"),
    (lambda data: data["examples"][0]["targets"].append(data["examples"][0]["targets"][0]), "repeats a compiler target"),
    (lambda data: data["examples"][0].update(sourceFile="../outside.fs"), "escapes the repository"),
    (lambda data: data["examples"][0].update(output="missing newline"), "output must use LF"),
    (lambda data: data["examples"][0].update(prelude="hidden helper"), "Unexpected fields"),
    (lambda data: data.update(setupFiles=[]), "Setup must include"),
])
def test_manifest_negative_controls(example_tree, change, message):
    root, manifest, inventory = example_tree
    change(manifest)
    with pytest.raises(ValueError, match=message):
        validate(root, manifest, inventory)


@pytest.mark.parametrize("field, value", [
    ("visibility", "internal"), ("implicitDeclaration", True),
    ("accessor", True), ("package", "LibTmux"),
])
def test_target_must_be_a_public_explicit_fsharp_declaration(example_tree, field, value):
    root, manifest, inventory = example_tree
    target = manifest["examples"][0]["targets"][0]
    for member in inventory["members"]:
        if member["id"] == target:
            member[field] = value
    with pytest.raises(ValueError, match="no public compiler target"):
        validate(root, manifest, inventory)


@pytest.mark.parametrize("old, new, message", [
    ('<Compile Include="Program.fs" />', '<Compile Include="Helper.fs" />', "displayed entrypoint"),
    ("net8.0;net10.0", "net10.0", "both supported frameworks"),
    ("[0.0.0-alpha.18]", "[0.0.0-alpha.17]", "exact library"),
    ("</Project>", '<Import Project="hidden.props" /></Project>', "hidden source"),
    ("</Project>", '<Target Name="Hidden" BeforeTargets="Compile" /></Project>', "hidden source"),
])
def test_standalone_project_cannot_hide_context(example_tree, old, new, message):
    root, manifest, inventory = example_tree
    project = root / manifest["profiles"]["fsharp"]["projectFile"]
    project.write_text(project.read_text().replace(old, new))
    with pytest.raises(ValueError, match=message):
        validate(root, manifest, inventory)


def test_local_package_mapping_cannot_fall_back_to_a_published_library(example_tree):
    root, manifest, inventory = example_tree
    config = root / "examples/api/NuGet.config"
    config.write_text(config.read_text().replace('pattern="LibTmux.*"', 'pattern="Other.*"'))
    with pytest.raises(ValueError, match="only from the native local pack"):
        validate(root, manifest, inventory)


def test_symlink_cannot_supply_an_external_program(example_tree, tmp_path_factory):
    root, manifest, inventory = example_tree
    outside = tmp_path_factory.mktemp("external") / "Program.fs"
    outside.write_text("printfn \"external\"\n")
    source = root / manifest["examples"][0]["sourceFile"]
    source.unlink()
    source.symlink_to(outside)
    with pytest.raises(ValueError, match="escapes the repository"):
        validate(root, manifest, inventory)


@pytest.mark.packaging
@pytest.mark.parametrize("profile", ["fsharp", "csharp"])
def test_native_command_failure_rejects_consumer_and_keeps_cleanup(tmp_path, monkeypatch, profile):
    """A real capture failure must fail the gate after the owned server is stopped."""
    packages = os.environ.get("LIBTMUX_PACKAGE_ARTIFACTS")
    if packages is None:
        pytest.skip("outer loop: set LIBTMUX_PACKAGE_ARTIFACTS after Release pack")
    native = shutil.which(os.environ.get("LIBTMUX_TMUX", "tmux"))
    assert native, "the native failure control requires tmux"
    wrapper = tmp_path / "tmux-fail-capture"
    wrapper.write_text(
        "#!/bin/sh\n"
        'case "$*" in\n'
        "  *capture-pane*) echo 'intentional capture failure' >&2; exit 73 ;;\n"
        "esac\n"
        f"exec {shlex.quote(native)} \"$@\"\n"
    )
    wrapper.chmod(0o755)
    monkeypatch.setenv("LIBTMUX_TMUX", str(wrapper))
    monkeypatch.setenv("TMUX_TMPDIR", "/tmp/libtmux-dotnet-test")
    manifest = json.loads((ROOT / "examples/api/manifest.json").read_text())
    manifest["examples"] = [entry for entry in manifest["examples"] if entry["id"] == f"{profile}-InputCapture"]
    output = tmp_path / "consumer"
    with pytest.raises(RuntimeError, match="intentional capture failure"):
        run_consumers(ROOT, manifest, Path(packages), output, ROOT / "artifacts/api-inventory.json")
    receipt = json.loads((output / "receipt.json").read_text())
    assert len(receipt["programs"]) == 1
    assert receipt["programs"][0]["ownedServersRemaining"] == 0
    assert not Path(receipt["programs"][0]["socketRoot"]).exists()
    assert receipt["commands"][-1]["exitCode"] != 0
    assert "intentional capture failure" in receipt["commands"][-1]["stderr"]


@pytest.mark.parametrize("profile", ["fsharp", "csharp"])
def test_each_language_rejects_an_omitted_program(example_tree, profile):
    root, manifest, inventory = example_tree
    entry = next(entry for entry in manifest["examples"] if entry["profile"] == profile)
    manifest["examples"].remove(entry)
    with pytest.raises(ValueError, match=f"every complete program for {profile}"):
        validate(root, manifest, inventory)


def test_csharp_source_cannot_claim_a_fsharp_profile(example_tree):
    root, manifest, inventory = example_tree
    entry = next(entry for entry in manifest["examples"] if entry["profile"] == "csharp")
    entry["profile"] = "fsharp"
    with pytest.raises(ValueError, match="does not match its language profile"):
        validate(root, manifest, inventory)


def test_csharp_targets_must_belong_to_the_core_package(example_tree):
    root, manifest, inventory = example_tree
    entry = next(entry for entry in manifest["examples"] if entry["profile"] == "csharp")
    entry["targets"] = manifest["examples"][0]["targets"]
    with pytest.raises(ValueError, match="no public compiler target"):
        validate(root, manifest, inventory)


@pytest.mark.parametrize("old, new, message", [
    ('<Compile Include="Program.cs" />', '<Compile Include="Helper.cs" />', "displayed entrypoint"),
    ("<ImplicitUsings>disable", "<ImplicitUsings>enable", "declare nullability, imports"),
    ("<EnableDefaultCompileItems>false", "<EnableDefaultCompileItems>true", "sole entrypoint"),
    ("[0.0.0-alpha.18]", "[0.0.0-alpha.17]", "exact library"),
    ("</Project>", '<Import Project="hidden.props" /></Project>', "hidden source"),
])
def test_csharp_standalone_setup_cannot_hide_context(example_tree, old, new, message):
    root, manifest, inventory = example_tree
    project = root / manifest["profiles"]["csharp"]["projectFile"]
    project.write_text(project.read_text().replace(old, new))
    with pytest.raises(ValueError, match=message):
        validate(root, manifest, inventory)
