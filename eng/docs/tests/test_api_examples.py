"""Reject incomplete setup and stale compiler attachments for complete programs."""

from __future__ import annotations

import copy
import json
import os
import re
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import zipfile

import pytest

from eng.docs.verify_api_examples import (
    LONGEST_PROGRAM_SOCKET_NAME,
    ROOT,
    maximum_socket_root_bytes,
    run_consumers,
    unique_object,
    validate,
)


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
    assert len(fsharp) == 14
    assert len({target for entry in fsharp for target in entry["targets"]}) == 56
    assert len(csharp) == 7
    assert len({target for entry in csharp for target in entry["targets"]}) == 33


# The docs site reads F# targets with this pattern and stops the build on any
# other, such as an active pattern's |Case|_| name; name its module instead.
SITE_FSHARP_TARGET = re.compile(r"^[MT]:LibTmux\.FSharp\.[\w.]+(?:`{1,2}\d+)?(?:\([^()\s]+\))?$")


def test_fsharp_targets_are_ones_the_docs_site_reads():
    manifest = json.loads((ROOT / "examples/api/manifest.json").read_text(encoding="utf-8"))
    unreadable = [
        target
        for entry in manifest["examples"]
        if entry["profile"] == "fsharp"
        for target in entry["targets"]
        if not SITE_FSHARP_TARGET.match(target)
    ]
    assert unreadable == []


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
    ("[0.1.1]", "[0.1.0]", "exact library"),
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


SELECTORS = ("TMUX", "TMUX_PANE", "LIBTMUX_SOCKET", "LIBTMUX_SOCKET_PATH", "LIBTMUX_SOCKET_NAME")


@pytest.fixture
def consumer_inputs(tmp_path: Path):
    """A one-program manifest, packed libraries and a tmux stand-in, without dotnet or tmux."""
    root = tmp_path / "repository"
    (root / "examples/api").mkdir(parents=True)
    (root / "examples/api/manifest.json").write_text("{}")
    for name in ("NuGet.config", "Example.csproj", "Demo.cs"):
        (root / name).write_text(name)
    manifest = {
        "setupFiles": ["NuGet.config"],
        "profiles": {"csharp": {"projectFile": "Example.csproj", "entrypoint": "Program.cs"}},
        "examples": [{"id": "demo", "profile": "csharp", "sourceFile": "Demo.cs", "output": "shown\n"}],
    }
    packages = tmp_path / "packages"
    packages.mkdir()
    with zipfile.ZipFile(packages / "LibTmux.nupkg", "w") as archive:
        for framework in ("net8.0", "net10.0"):
            for name in ("LibTmux.dll", "LibTmux.FSharp.dll"):
                archive.writestr(f"lib/{framework}/{name}", f"{framework}/{name}")
    inventory = tmp_path / "inventory.json"
    inventory.write_text("{}")
    tmux = tmp_path / "tmux"
    tmux.write_text("tmux")
    return root, manifest, packages, tmp_path / "output", inventory, tmux


def fake_commands(monkeypatch, seen: list[dict[str, str]]) -> None:
    """Answer every command run_consumers starts, writing down the environment it was given."""

    def check_output(arguments, **_):
        return "tmux 3.8a\n" if arguments[-1] == "-V" else ("" if "status" in arguments else "revision\n")

    def run(arguments, cwd, env, capture_output, timeout):
        seen.append(dict(env))
        if arguments[1] == "build":
            for framework in ("net8.0", "net10.0"):
                deployed = Path(cwd) / "bin/Release" / framework
                deployed.mkdir(parents=True, exist_ok=True)
                (deployed / "LibTmux.dll").write_text(f"{framework}/LibTmux.dll")
        output = b"shown\n" if arguments[1].endswith("Example.dll") else b""
        return subprocess.CompletedProcess(arguments, 0, stdout=output, stderr=b"")

    monkeypatch.setattr(subprocess, "check_output", check_output)
    monkeypatch.setattr(subprocess, "run", run)


def test_consumers_run_with_no_tmux_selector_in_their_environment(consumer_inputs, tmp_path, monkeypatch):
    root, manifest, packages, output, inventory, tmux = consumer_inputs
    for name in SELECTORS:
        monkeypatch.setenv(name, "decoy")
    monkeypatch.setenv("LIBTMUX_TMUX", str(tmux))
    # Short, as the script requires: a pytest temporary directory is not.
    sockets = Path(tempfile.mkdtemp(prefix="vae", dir="/tmp"))
    monkeypatch.setenv("TMUX_TMPDIR", str(sockets))
    seen: list[dict[str, str]] = []
    fake_commands(monkeypatch, seen)

    try:
        run_consumers(root, manifest, packages, output, inventory)
    finally:
        shutil.rmtree(sockets)

    # Names and booleans only: a failed assertion must not print an environment.
    ran = len(seen) > 0
    handed = sorted({name for each in seen for name in each if name in SELECTORS})
    kept = all("LIBTMUX_TMUX" in each and "NUGET_PACKAGES" in each for each in seen)
    assert ran, "run_consumers ran no command"
    assert handed == []
    assert kept, "run_consumers dropped a variable it should have kept"


def test_consumers_refuse_a_socket_root_one_byte_over_what_a_socket_path_allows(consumer_inputs, monkeypatch):
    root, manifest, packages, output, inventory, tmux = consumer_inputs
    monkeypatch.setenv("LIBTMUX_TMUX", str(tmux))
    sockets = Path("/tmp") / ("x" * (maximum_socket_root_bytes() + 1 - len("/tmp/")))
    assert len(os.fsencode(sockets)) == maximum_socket_root_bytes() + 1
    monkeypatch.setenv("TMUX_TMPDIR", str(sockets))
    fake_commands(monkeypatch, [])

    with pytest.raises(ValueError, match="TMUX_TMPDIR is longer than") as refusal:
        run_consumers(root, manifest, packages, output, inventory)
    assert str(sockets) not in str(refusal.value)
    assert not sockets.exists(), "a root over the limit was made"


def test_consumers_accept_a_socket_root_at_what_a_socket_path_allows(consumer_inputs, monkeypatch):
    root, manifest, packages, output, inventory, tmux = consumer_inputs
    monkeypatch.setenv("LIBTMUX_TMUX", str(tmux))
    sockets = Path("/tmp") / ("y" * (maximum_socket_root_bytes() - len("/tmp/")))
    assert len(os.fsencode(sockets)) == maximum_socket_root_bytes()
    monkeypatch.setenv("TMUX_TMPDIR", str(sockets))
    fake_commands(monkeypatch, [])
    try:
        run_consumers(root, manifest, packages, output, inventory)
    finally:
        shutil.rmtree(sockets)


def test_the_longest_socket_name_a_program_uses_is_covered():
    # The limit above is computed from this name, so a longer one must raise it.
    names = []
    for source in [*(ROOT / "examples").glob("*/Programs/*"), *(ROOT / "examples").glob("*/Snippets/*")]:
        for match in re.finditer(
            r'"([A-Za-z0-9-]+)"\s*\+\s*Guid\.NewGuid\(\)\.ToString\(\s*"N"\s*\)(?:\s*\+\s*"([A-Za-z0-9-]*)")?',
            source.read_text(),
        ):
            names.append(len(match.group(1)) + 32 + len(match.group(2) or ""))
    assert names, "no program names a socket"
    assert max(names) <= LONGEST_PROGRAM_SOCKET_NAME


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
    # F# programs run the tmux on PATH and C# ones LIBTMUX_TMUX; both reach this.
    wrapper = tmp_path / "bin" / "tmux"
    wrapper.parent.mkdir()
    wrapper.write_text(
        "#!/bin/sh\n"
        'case "$*" in\n'
        "  *capture-pane*) echo 'intentional capture failure' >&2; exit 73 ;;\n"
        "esac\n"
        f"exec {shlex.quote(native)} \"$@\"\n"
    )
    wrapper.chmod(0o755)
    monkeypatch.setenv("LIBTMUX_TMUX", str(wrapper))
    monkeypatch.setenv("PATH", f"{wrapper.parent}{os.pathsep}{os.environ['PATH']}")
    monkeypatch.setenv("TMUX_TMPDIR", "/tmp/libtmux-dotnet-test")
    # The wrapper matches argv, and the shared control client carries
    # capture-pane without a process of its own.
    monkeypatch.setenv("LIBTMUX_CONTROL_TRANSPORT", "0")
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
    ("[0.1.1]", "[0.1.0]", "exact library"),
    ("</Project>", '<Import Project="hidden.props" /></Project>', "hidden source"),
])
def test_csharp_standalone_setup_cannot_hide_context(example_tree, old, new, message):
    root, manifest, inventory = example_tree
    project = root / manifest["profiles"]["csharp"]["projectFile"]
    project.write_text(project.read_text().replace(old, new))
    with pytest.raises(ValueError, match=message):
        validate(root, manifest, inventory)
