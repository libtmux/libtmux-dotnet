"""Prove the changelog check notices a package that loses its changelog."""

from __future__ import annotations

import pathlib
import runpy
import typing as t

import pytest

REPOSITORY_ROOT = pathlib.Path(__file__).parents[3]
PACKAGES = ("LibTmux", "LibTmux.Mcp")


def load_checker() -> dict[str, t.Any]:
    """Load the changelog check as an import-free test namespace."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_changelogs.py")
    )


def changelog(name: str) -> str:
    """Return a changelog that meets the policy for ``name``."""
    return f"# Changelog\n\nNotable changes to `{name}`.\n\n## [Unreleased]\n"


@pytest.fixture
def tree(tmp_path: pathlib.Path) -> pathlib.Path:
    """Return a small repository whose changelogs all meet the policy."""
    (tmp_path / "CHANGELOG.md").write_text(
        "# Changelog\n\n## [Unreleased]\n", encoding="utf-8"
    )
    for name in PACKAGES:
        directory = tmp_path / "src" / name
        directory.mkdir(parents=True)
        (directory / f"{name}.csproj").write_text(
            f"<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>{name}</PackageId></PropertyGroup></Project>",
            encoding="utf-8",
        )
        (directory / "CHANGELOG.md").write_text(changelog(name), encoding="utf-8")
    return tmp_path


def test_checked_in_changelogs_meet_the_policy() -> None:
    """Keep every packable project's changelog, and the main one, in place."""
    assert load_checker()["verify"](REPOSITORY_ROOT) == []


def test_every_packable_project_is_checked() -> None:
    """Fail if the discovery stops seeing a published package."""
    found = load_checker()["packages"](REPOSITORY_ROOT)

    assert sorted(found) == [
        "LibTmux",
        "LibTmux.Extensions.DependencyInjection",
        "LibTmux.FSharp",
        "LibTmux.Mcp",
        "LibTmux.Query.Json",
        "LibTmux.Testing",
        "LibTmux.Workspace",
        "LibTmux.Workspace.Cli",
    ]


def test_a_tree_that_meets_the_policy_passes(tree: pathlib.Path) -> None:
    """Show the fixture is clean, so each failure below is the mutation's."""
    assert load_checker()["verify"](tree) == []


def test_a_package_without_a_changelog_is_rejected(tree: pathlib.Path) -> None:
    """Reject a published package whose changelog was deleted."""
    (tree / "src" / "LibTmux.Mcp" / "CHANGELOG.md").unlink()

    assert load_checker()["verify"](tree) == [
        "src/LibTmux.Mcp/CHANGELOG.md: missing; every published package keeps a changelog"
    ]


def test_a_missing_main_changelog_is_rejected(tree: pathlib.Path) -> None:
    """Reject a tree whose main changelog was deleted."""
    (tree / "CHANGELOG.md").unlink()

    assert load_checker()["verify"](tree) == [
        "CHANGELOG.md: missing; every published package keeps a changelog"
    ]


def test_another_packages_changelog_is_rejected(tree: pathlib.Path) -> None:
    """Reject a changelog pasted over another package's."""
    (tree / "src" / "LibTmux.Mcp" / "CHANGELOG.md").write_text(
        changelog("LibTmux"), encoding="utf-8"
    )

    assert load_checker()["verify"](tree) == [
        'src/LibTmux.Mcp/CHANGELOG.md: does not open with "Notable changes to `LibTmux.Mcp`."'
    ]


@pytest.mark.parametrize(
    "text",
    [
        "# Changelog\n\nNotable changes to `LibTmux.Mcp`.\n",
        "# Changelog\n\nNotable changes to `LibTmux.Mcp`.\n\n```markdown\n## [Unreleased]\n```\n",
    ],
    ids=["no-heading", "heading-inside-a-fence"],
)
def test_a_changelog_without_an_unreleased_section_is_rejected(
    tree: pathlib.Path, text: str
) -> None:
    """Reject a changelog the next release has nowhere to land in."""
    (tree / "src" / "LibTmux.Mcp" / "CHANGELOG.md").write_text(text, encoding="utf-8")

    assert load_checker()["verify"](tree) == [
        'src/LibTmux.Mcp/CHANGELOG.md: has no "## [Unreleased]" section'
    ]
