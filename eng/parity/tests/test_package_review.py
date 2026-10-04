"""Critical phase integrity checks; Git is real and only dotnet is replaced."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
from types import SimpleNamespace

import pytest


RECIPE = Path(__file__).resolve().parents[2] / "package_review.py"
VERSION = "0.0.0-review.1"
ENGINEERING = "eng/LibTmux.Engineering/bin/Release/net10.0/LibTmux.Engineering.dll"


@pytest.fixture
def review(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    root = tmp_path / "checkout"
    root.mkdir()
    files = {
        ".gitignore": "**/bin/\n**/obj/\n__pycache__/\n",
        "global.json": '{"sdk":{"version":"10.0.100"}}',
        "Directory.Build.props": "<Project><PropertyGroup><VersionPrefix>0.0.0</VersionPrefix><VersionSuffix>alpha.19</VersionSuffix></PropertyGroup></Project>",
        "Directory.Packages.props": "<Project />",
        "LibTmux.slnx": '<Solution><Project Path="src/LibTmux/LibTmux.csproj" /><Project Path="src/LibTmux.Mcp/LibTmux.Mcp.csproj" /><Project Path="eng/LibTmux.Engineering/LibTmux.Engineering.csproj" /></Solution>',
        "src/LibTmux/LibTmux.csproj": "<Project />",
        "src/LibTmux/packages.lock.json": "{}",
        "src/LibTmux/Pane.cs": "class Pane {}",
        "src/LibTmux.Mcp/LibTmux.Mcp.csproj": "<Project><PropertyGroup><PackAsTool>true</PackAsTool></PropertyGroup></Project>",
        "eng/LibTmux.Engineering/LibTmux.Engineering.csproj": "<Project />",
    }
    for name, contents in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(contents)
    shutil.copyfile(RECIPE, root / "eng/package_review.py")
    actual_run = subprocess.run
    actual_output = subprocess.check_output
    for command in (
        ["git", "init", "--quiet"],
        ["git", "add", "."],
        ["git", "-c", "user.name=Packaging fixture", "-c",
         "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "Fixture"],
    ):
        actual_run(command, cwd=root, check=True, capture_output=True)
    revision = actual_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    spec = importlib.util.spec_from_file_location("package_review_fixture", root / "eng/package_review.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    fixture = SimpleNamespace(
        root=root, output=tmp_path / "packages", revision=revision, module=module,
        calls=[], sdk="10.0.100", failure=None, mutation=None,
    )
    fixture.setenv = monkeypatch.setenv

    def output(command, **kwargs):
        if list(command) == ["dotnet", "--version"]:
            return fixture.sdk + "\n"
        return actual_output(command, **kwargs)

    def run(command, **kwargs):
        if command[0] != "dotnet":
            return actual_run(command, **kwargs)
        argv = list(command)
        fixture.calls.append(argv)
        operation = argv[2] if argv[1] == ENGINEERING else argv[1]
        if fixture.failure == operation:
            return subprocess.CompletedProcess(argv, 7)
        if operation == "restore":
            for project in ("src/LibTmux", "src/LibTmux.Mcp", "eng/LibTmux.Engineering"):
                directory = root / project / "obj"
                directory.mkdir()
                (directory / "project.assets.json").write_text('{"version":3}')
                (directory / "project.nuget.cache").write_text("{}")
                (directory / "fixture.csproj.nuget.g.props").write_text("<Project />")
        elif operation == "build":
            for project, name in (("src/LibTmux", "LibTmux"),
                                  ("src/LibTmux.Mcp", "LibTmux.Mcp"),
                                  ("eng/LibTmux.Engineering", "LibTmux.Engineering")):
                directory = root / project / "bin/Release/net10.0"
                directory.mkdir(parents=True)
                (directory / (name + ".dll")).write_bytes(b"compiled fixture")
                (directory / (name + ".pdb")).write_bytes(b"symbols fixture")
                (directory / (name + ".xml")).write_bytes(b"compiler documentation fixture")
                intermediate = root / project / "obj/Release/net10.0"
                intermediate.mkdir(parents=True)
                (intermediate / (name + ".dll")).write_bytes(b"intermediate fixture")
        elif operation == "api-inventory":
            Path(argv[-1]).write_text('{"revision":"' + revision + '"}')
        elif operation == "pack":
            for extension in ("nupkg", "snupkg"):
                (fixture.output / f"LibTmux.{VERSION}.{extension}").write_bytes(b"archive fixture")
        if fixture.mutation is not None:
            fixture.mutation(operation)
        return subprocess.CompletedProcess(argv, 0)

    monkeypatch.setattr(module.subprocess, "check_output", output)
    monkeypatch.setattr(module.subprocess, "run", run)

    def invoke(phase=None, *, version=VERSION, revision=None, destination=None):
        arguments = [str(RECIPE), "--version", version, "--revision",
                     revision or fixture.revision, "--output", str(destination or fixture.output)]
        if phase is not None:
            arguments.extend(["--phase", phase])
        monkeypatch.setattr(sys, "argv", arguments)
        return module.main()

    fixture.invoke = invoke
    return fixture


def expected_commands(review):
    inventory = str(review.output / "api-inventory.json")
    return [
        ["dotnet", "restore", "LibTmux.slnx", "--locked-mode"],
        ["dotnet", "build", "LibTmux.slnx", "--configuration", "Release", "--no-restore",
         "--warnaserror", "-p:ContinuousIntegrationBuild=true"],
        ["dotnet", ENGINEERING, "api-inventory", ".", inventory],
        ["dotnet", "pack", "LibTmux.slnx", "--configuration", "Release", "--no-build",
         "--output", str(review.output)],
        ["dotnet", ENGINEERING, "packages", ".", str(review.output), inventory],
    ]


def test_package_review_phases_preserve_all_gates(review):
    for phase, count in (("restore", 1), ("build", 2), ("pack", 5)):
        assert review.invoke(phase) == 0
        assert review.calls == expected_commands(review)[:count]
        assert (review.output / "provenance.json").exists() == (phase == "pack")
    provenance = json.loads((review.output / "provenance.json").read_text())
    assert set(provenance) == {
        "version", "revision", "tree", "sdk", "inspection", "packages",
        "inventory_sha256", "inputs",
    }
    assert provenance["revision"] == review.revision
    assert provenance["version"] == VERSION
    assert provenance["sdk"] == "10.0.100"
    assert provenance["inspection"] == "passed"
    assert len(provenance["packages"]) == 2
    assert len(json.loads((review.output / "commands.json").read_text())) == 5


def test_package_review_default_still_runs_full_recipe(review):
    assert review.invoke() == 0
    assert review.calls == expected_commands(review)
    assert (review.output / "provenance.json").exists()


@pytest.mark.parametrize("phased", [False, True])
def test_package_review_tool_pack_can_create_publish_payload(review, phased):
    """SDK PackTool publishes copies and writes metadata even with --no-build."""
    def publish(operation):
        if operation == "pack":
            build = review.root / "src/LibTmux.Mcp/bin/Release/net10.0"
            directory = build / "publish"
            directory.mkdir()
            for name in ("LibTmux.Mcp.dll", "LibTmux.Mcp.pdb", "LibTmux.Mcp.xml"):
                shutil.copyfile(build / name, directory / name)
            settings = review.root / "src/LibTmux.Mcp/obj/Release/net10.0/DotnetToolSettings.xml"
            settings.write_text('<DotNetCliTool Version="' + VERSION + '" />')

    review.mutation = publish
    if phased:
        for phase in ("restore", "build", "pack"):
            assert review.invoke(phase) == 0
    else:
        assert review.invoke() == 0
    assert review.calls == expected_commands(review)
    assert (review.output / "provenance.json").exists()


@pytest.mark.parametrize("phase", ["build", "pack"])
def test_package_review_requires_predecessor(review, phase):
    with pytest.raises((OSError, RuntimeError)):
        review.invoke(phase)
    assert review.calls == []


@pytest.mark.parametrize("phase", ["restore", "build", "pack"])
def test_package_review_rejects_repeated_or_wrong_phase(review, phase):
    assert review.invoke("restore") == 0
    calls = list(review.calls)
    if phase in {"build", "pack"}:
        assert review.invoke("build") == 0
        calls = list(review.calls)
    if phase == "pack":
        assert review.invoke("pack") == 0
        calls = list(review.calls)
    with pytest.raises((OSError, RuntimeError)):
        review.invoke(phase)
    assert review.calls == calls


@pytest.mark.parametrize("mutation", [
    "source", "revision", "version", "sdk", "props", "config", "assets",
    "assets-missing", "restore-metadata-added", "state-identity", "state-json",
    "environment", "commands",
])
def test_package_review_restore_identity_cannot_change(review, mutation):
    assert review.invoke("restore") == 0
    kwargs = {}
    if mutation == "source":
        (review.root / "src/LibTmux/Pane.cs").write_text("class Changed {}")
    elif mutation == "revision":
        kwargs["revision"] = "0" * 40
    elif mutation == "version":
        kwargs["version"] = "0.0.0-review.2"
    elif mutation == "sdk":
        review.sdk = "10.0.101"
    elif mutation in {"props", "config", "assets"}:
        path = {
            "props": review.output / "inputs/Directory.Build.props",
            "config": review.output / "NuGet.config",
            "assets": review.root / "src/LibTmux/obj/project.assets.json",
        }[mutation]
        path.write_text("changed")
    elif mutation == "assets-missing":
        (review.root / "src/LibTmux/obj/project.assets.json").unlink()
    elif mutation == "restore-metadata-added":
        (review.root / "src/LibTmux/obj/added.csproj.nuget.g.targets").write_text("changed")
    elif mutation == "environment":
        review.setenv("DefineConstants", "CHANGED_BETWEEN_PHASES")
    elif mutation == "commands":
        (review.output / "commands.json").write_text("[]")
    else:
        path = review.output / "review-state.json"
        if mutation == "state-json":
            path.write_text("{")
        else:
            data = json.loads(path.read_text())
            data["identity"]["version"] = "0.0.0-review.2"
            path.write_text(json.dumps(data))
    with pytest.raises((OSError, RuntimeError)):
        review.invoke("build", **kwargs)
    assert len(review.calls) == 1
    assert not (review.output / "provenance.json").exists()


@pytest.mark.parametrize("mutation", [
    "dll", "dll-missing", "dll-added", "intermediate", "archive",
    "tool-dll", "tool-pdb", "tool-xml", "non-tool-publish", "non-tool-settings",
])
def test_package_review_built_outputs_cannot_change(review, mutation):
    assert review.invoke("restore") == 0
    assert review.invoke("build") == 0
    dll = review.root / "src/LibTmux/bin/Release/net10.0/LibTmux.dll"
    if mutation == "dll":
        dll.write_bytes(b"different build")
    elif mutation == "dll-missing":
        dll.unlink()
    elif mutation == "dll-added":
        (dll.parent / "Added.dll").write_bytes(b"extra build")
    elif mutation == "intermediate":
        (review.root / "src/LibTmux/obj/Release/net10.0/LibTmux.dll").write_bytes(b"changed")
    elif mutation == "archive":
        (review.output / f"LibTmux.{VERSION}.nupkg").write_bytes(b"already used identity")
    elif mutation.startswith("tool-"):
        suffix = mutation.removeprefix("tool-")
        path = review.root / "src/LibTmux.Mcp/bin/Release/net10.0" / ("LibTmux.Mcp." + suffix)
        path.write_bytes(b"changed compiler input")
    elif mutation == "non-tool-publish":
        path = dll.parent / "publish"
        path.mkdir()
        (path / "Changed.dll").write_bytes(b"not a tool publish output")
    else:
        (review.root / "src/LibTmux/obj/Release/net10.0/DotnetToolSettings.xml").write_bytes(b"not tool metadata")
    with pytest.raises((OSError, RuntimeError)):
        review.invoke("pack")
    assert len(review.calls) == 2
    assert not (review.output / "provenance.json").exists()


@pytest.mark.parametrize("failure", ["restore", "build", "api-inventory", "pack", "packages"])
def test_package_review_failed_gate_has_no_provenance(review, failure):
    review.failure = failure
    with pytest.raises(RuntimeError, match="failed with exit 7"):
        for phase in ("restore", "build", "pack"):
            review.invoke(phase)
    assert not (review.output / "provenance.json").exists()
    commands = json.loads((review.output / "commands.json").read_text())
    assert commands[-1]["exit"] == 7
    if failure == "packages":
        assert list(review.output.glob("*.nupkg"))
        calls = list(review.calls)
        with pytest.raises(RuntimeError):
            review.invoke("pack")
        assert review.calls == calls


@pytest.mark.parametrize("mutation", ["dll", "archive", "inventory", "source", "props"])
def test_package_review_changed_output_after_inspection_has_no_provenance(review, mutation):
    assert review.invoke("restore") == 0
    assert review.invoke("build") == 0

    def mutate(operation):
        if operation == "packages":
            path = {
                "dll": review.root / "src/LibTmux/bin/Release/net10.0/LibTmux.dll",
                "archive": review.output / f"LibTmux.{VERSION}.nupkg",
                "inventory": review.output / "api-inventory.json",
                "source": review.root / "src/LibTmux/Pane.cs",
                "props": review.output / "inputs/Directory.Build.props",
            }[mutation]
            path.write_bytes(b"changed during inspection")

    review.mutation = mutate
    with pytest.raises(RuntimeError):
        review.invoke("pack")
    assert len(review.calls) == 5
    assert not (review.output / "provenance.json").exists()
