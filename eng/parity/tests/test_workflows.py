"""Prove the workflow check notices a workflow that stops covering the range."""

from __future__ import annotations

import json
import pathlib
import runpy
import shutil
import typing as t

import pytest


def load_checker() -> dict[str, t.Any]:
    """Load the workflow check as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_workflows.py")
    )


REPOSITORY_ROOT = pathlib.Path(__file__).parents[3]
MANIFEST = REPOSITORY_ROOT / "eng" / "tmux" / "versions.json"
SUPPORTED_TMUX_VERSIONS: tuple[str, ...] = tuple(
    json.loads(MANIFEST.read_text(encoding="utf-8"))["supported"]
)


def verify(root: pathlib.Path) -> list[str]:
    """Run the workflow check against one repository root."""
    checked: list[str] = load_checker()["verify"](root)
    return checked


BUILD = """
on:
  workflow_call:
jobs:
  build:
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - run: dotnet restore --locked-mode
      - run: dotnet format --verify-no-changes
      - run: dotnet build --warnaserror
      - run: dotnet pack src/LibTmux/LibTmux.csproj
      - env:
          NUGET_PACKAGES: ${{ runner.temp }}/libtmux-aot-smoke
        run: |
          dotnet restore tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --configfile tests/NuGet.config
          dotnet publish tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --no-restore
      - env:
          NUGET_PACKAGES: ${{ runner.temp }}/libtmux-package-consumer
        run: dotnet run --project tests/LibTmux.PackageConsumer
      - run: dotnet run --project examples/LibTmux.Examples
      - run: dotnet test --project tests/LibTmux.ExampleTests
      - run: uv run python eng/docs/render_api_reference.py --check
      - run: uv run python eng/parity/render_public_api.py --check
      - run: uv run python eng/docs/sync_snippets.py --check
"""

MATRIX = """
on:
  workflow_call:
jobs:
  matrix:
    strategy:
      fail-fast: false
      matrix:
        tmux: [{versions}]
        framework: ['net8.0', 'net10.0']
    steps:
      - env:
          LIBTMUX_INTEGRATION_REQUIRED: '1'
        run: dotnet test
"""

RELEASE = """
jobs:
  dotnet:
    uses: ./.github/workflows/dotnet.yml
  compatibility:
    uses: ./.github/workflows/dotnet-tmux.yml
  psmux:
    runs-on: [self-hosted, Windows, X64, psmux]
    steps:
      - run: |
          $env:NUGET_PACKAGES = 'fresh'
          dotnet restore LibTmux.slnx
      - env:
          ARTIFACT_URL: https://github.com/psmux/psmux/releases/download/v3.3.8/psmux-v3.3.8-windows-x64.zip
          ARCHIVE_SHA256: 1ad127ba937194a890b933a73d9b023e297bd73dc742abd841bf159984c2effe
          WSL_DISTRIBUTION: ${{ vars.PSMUX_WSL_DISTRIBUTION }}
          WSL_DOTNET_PATH: ${{ vars.PSMUX_WSL_DOTNET_PATH }}
        run: Invoke-PsmuxSmoke.ps1 -RunWslSmoke -WslDotnetPath /dotnet -WslRepository $env:GITHUB_WORKSPACE 54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d
      - run: echo 'net8.0' 'net10.0'
  publish:
    needs: [dotnet, compatibility, psmux]
    steps:
      - uses: actions/download-artifact@pinned
      - env:
          REF_TYPE: ${{ github.ref_type }}
        run: dotnet nuget push package.nupkg
"""


def write(
    root: pathlib.Path,
    build: str,
    matrix: str,
    release: str = RELEASE,
) -> pathlib.Path:
    """Lay out a repository holding the release's workflow set."""
    workflows = root / ".github" / "workflows"
    workflows.mkdir(parents=True)
    (workflows / "dotnet.yml").write_text(build, encoding="utf-8")
    (workflows / "dotnet-tmux.yml").write_text(matrix, encoding="utf-8")
    (workflows / "release.yml").write_text(release, encoding="utf-8")

    # The check measures a root against that root's own version manifest, so a
    # laid-out repository needs one.
    manifest = root / "eng" / "tmux" / "versions.json"
    manifest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(MANIFEST, manifest)
    return root


def every_version() -> str:
    """Return the matrix entry naming every supported tmux."""
    return ", ".join(f"'{version}'" for version in SUPPORTED_TMUX_VERSIONS)


def test_complete_workflows_pass(tmp_path: pathlib.Path) -> None:
    """A pair of workflows covering the whole range has nothing to report."""
    root = write(tmp_path, BUILD, MATRIX.format(versions=every_version()))

    assert verify(root) == []


def test_a_dropped_tmux_version_is_reported(tmp_path: pathlib.Path) -> None:
    """Quietly dropping a lane is exactly what this exists to catch."""
    versions = ", ".join(f"'{version}'" for version in SUPPORTED_TMUX_VERSIONS[:-1])
    root = write(tmp_path, BUILD, MATRIX.format(versions=versions))

    assert verify(root) == [f"dotnet-tmux.yml omits tmux {SUPPORTED_TMUX_VERSIONS[-1]}"]


def test_a_matrix_that_stops_early_is_reported(tmp_path: pathlib.Path) -> None:
    """One lane failing should not hide what the others would have said."""
    matrix = MATRIX.format(versions=every_version()).replace("fail-fast: false", "")
    root = write(tmp_path, BUILD, matrix)

    assert "dotnet-tmux.yml stops the matrix at the first failure" in verify(root)


def test_skipped_integration_tests_are_reported(tmp_path: pathlib.Path) -> None:
    """A lane whose tests skipped would pass while proving nothing."""
    matrix = MATRIX.format(versions=every_version()).replace(
        "LIBTMUX_INTEGRATION_REQUIRED", "SOMETHING_ELSE"
    )
    root = write(tmp_path, BUILD, matrix)

    assert "dotnet-tmux.yml does not require its integration tests to run" in verify(
        root
    )


@pytest.mark.parametrize(
    "step",
    [
        "--locked-mode",
        "--warnaserror",
        "dotnet pack",
        "NUGET_PACKAGES: ${{ runner.temp }}/libtmux-aot-smoke",
        "--configfile tests/NuGet.config",
        "LibTmux.PackageConsumer",
        "LibTmux.ExampleTests",
        "render_api_reference.py --check",
        "render_public_api.py --check",
        "sync_snippets.py --check",
    ],
)
def test_a_dropped_build_step_is_reported(tmp_path: pathlib.Path, step: str) -> None:
    """A workflow gating less than the repository does lets changes through."""
    root = write(
        tmp_path,
        BUILD.replace(step, "echo skipped"),
        MATRIX.format(versions=every_version()),
    )

    assert f"dotnet.yml omits {step}" in verify(root)


def test_a_shared_package_consumer_cache_is_reported(tmp_path: pathlib.Path) -> None:
    """A warm solution cache can hide an incomplete package restore graph."""
    isolation = "NUGET_PACKAGES: ${{ runner.temp }}/libtmux-package-consumer"
    root = write(
        tmp_path,
        BUILD.replace(isolation, "NUGET_PACKAGES: shared"),
        MATRIX.format(versions=every_version()),
    )

    assert f"dotnet.yml omits {isolation}" in verify(root)


def test_a_missing_workflow_is_reported(tmp_path: pathlib.Path) -> None:
    """Deleting a workflow is the loudest way to stop testing."""
    root = write(tmp_path, BUILD, MATRIX.format(versions=every_version()))
    (root / ".github" / "workflows" / "dotnet-tmux.yml").unlink()

    assert verify(root) == ["missing workflow: dotnet-tmux.yml"]


@pytest.mark.parametrize(
    "step",
    [
        "uses: ./.github/workflows/dotnet.yml",
        "uses: ./.github/workflows/dotnet-tmux.yml",
        "needs: [dotnet, compatibility, psmux]",
        "github.ref_type",
        "actions/download-artifact@",
        "https://github.com/psmux/psmux/releases/download/v3.3.8/psmux-v3.3.8-windows-x64.zip",
        "1ad127ba937194a890b933a73d9b023e297bd73dc742abd841bf159984c2effe",
        "PSMUX_WSL_DISTRIBUTION",
        "PSMUX_WSL_DOTNET_PATH",
        "runs-on: [self-hosted, Windows, X64, psmux]",
        "Invoke-PsmuxSmoke.ps1",
        "-RunWslSmoke",
        "-WslDotnetPath",
        "-WslRepository $env:GITHUB_WORKSPACE",
    ],
)
def test_a_dropped_release_gate_is_reported(
    tmp_path: pathlib.Path,
    step: str,
) -> None:
    """Publishing must consume every same-commit and psmux proof."""
    root = write(
        tmp_path,
        BUILD,
        MATRIX.format(versions=every_version()),
        RELEASE.replace(step, "echo skipped"),
    )

    assert f"release.yml omits {step}" in verify(root)


def test_release_cannot_hide_an_existing_version(tmp_path: pathlib.Path) -> None:
    """NuGet's immutable duplicate must fail rather than look published."""
    root = write(
        tmp_path,
        BUILD,
        MATRIX.format(versions=every_version()),
        RELEASE + "\n# --skip-duplicate\n",
    )

    assert "release.yml can hide an existing immutable package version" in verify(root)


def test_release_seeds_its_fresh_cache_before_the_solution_restore(
    tmp_path: pathlib.Path,
) -> None:
    """The packed consumer needs external dependencies in its isolated cache."""
    root = write(
        tmp_path,
        BUILD,
        MATRIX.format(versions=every_version()),
        RELEASE.replace(
            "$env:NUGET_PACKAGES = 'fresh'\n          dotnet restore LibTmux.slnx",
            "dotnet restore LibTmux.slnx\n          $env:NUGET_PACKAGES = 'fresh'",
        ),
    )

    assert (
        "release.yml isolates NuGet only after restoring dependencies" in verify(root)
    )


@pytest.mark.parametrize("name", ["dotnet.yml", "dotnet-tmux.yml"])
def test_release_gate_workflows_must_be_callable(
    tmp_path: pathlib.Path,
    name: str,
) -> None:
    """A same-commit release call needs a workflow_call entry point."""
    build = BUILD if name != "dotnet.yml" else BUILD.replace("workflow_call:", "manual:")
    matrix = (
        MATRIX.format(versions=every_version())
        if name != "dotnet-tmux.yml"
        else MATRIX.format(versions=every_version()).replace("workflow_call:", "manual:")
    )
    root = write(tmp_path, build, matrix)

    assert f"{name} cannot be called by release.yml" in verify(root)
