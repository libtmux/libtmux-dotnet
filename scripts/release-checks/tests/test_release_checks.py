"""Prove each release check passes on a good package set and fails on each way it can be wrong."""

from __future__ import annotations

import json
import pathlib
import zipfile

import pytest

import release_checks as checks


def package(
    directory: pathlib.Path, identifier: str, version: str = "1.0.0", *, signed: bool = False
) -> pathlib.Path:
    """Write a package whose nuspec names `identifier`, optionally with a signature."""
    path = directory / f"{identifier}.{version}.nupkg"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(
            f"{identifier}.nuspec",
            f"<package><metadata><id>{identifier}</id><version>{version}</version>"
            "</metadata></package>",
        )
        archive.writestr("lib/a.dll", "x")
        if signed:
            archive.writestr(checks.SIGNATURE, "x")
    return path


@pytest.fixture
def uploaded(tmp_path: pathlib.Path) -> pathlib.Path:
    directory = tmp_path / "packages"
    directory.mkdir()
    package(directory, "LibTmux")
    package(directory, "LibTmux.Mcp")
    return directory


def test_the_run_must_be_at_a_v_tag() -> None:
    with pytest.raises(checks.ReleaseCheckError, match=r"must run at a v\* tag, not branch main"):
        checks.check_tag("branch", "main", "abc", lambda: "1.0.0")
    with pytest.raises(checks.ReleaseCheckError, match=r"not tag 1\.0\.0"):
        checks.check_tag("tag", "1.0.0", "abc", lambda: "1.0.0")


def test_the_tag_must_name_the_version_the_library_builds() -> None:
    with pytest.raises(
        checks.ReleaseCheckError, match=r"tag v1\.0\.1 does not match version 1\.0\.0"
    ):
        checks.check_tag("tag", "v1.0.1", "abc", lambda: "1.0.0")
    assert checks.check_tag("tag", "v1.0.0", "abc", lambda: "1.0.0") == "validated v1.0.0 at abc"


def test_each_package_is_unpacked_into_a_directory_named_for_it(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    output = tmp_path / "sbom-input"

    checks.unpack(uploaded, output)

    assert sorted(path.name for path in output.iterdir()) == [
        "LibTmux.1.0.0",
        "LibTmux.Mcp.1.0.0",
    ]
    assert (output / "LibTmux.1.0.0" / "lib" / "a.dll").is_file()


def write_sbom(path: pathlib.Path, *names: str, kind: str = "library") -> pathlib.Path:
    path.write_text(
        json.dumps({"components": [{"type": kind, "name": name} for name in names]}),
        encoding="utf-8",
    )
    return path


def test_an_sbom_that_names_every_package_passes(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    checks.check_sbom(uploaded, write_sbom(tmp_path / "sbom.json", "LibTmux", "LibTmux.Mcp"))


def test_an_sbom_missing_a_package_names_it(uploaded: pathlib.Path, tmp_path: pathlib.Path) -> None:
    with pytest.raises(checks.ReleaseCheckError) as raised:
        checks.check_sbom(uploaded, write_sbom(tmp_path / "sbom.json", "LibTmux"))

    assert str(raised.value) == "::error::The SBOM does not name LibTmux.Mcp."


def test_a_component_that_is_not_a_library_does_not_count(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    sbom = write_sbom(tmp_path / "sbom.json", "LibTmux", "LibTmux.Mcp", kind="application")

    with pytest.raises(checks.ReleaseCheckError):
        checks.check_sbom(uploaded, sbom)


def registry(root: pathlib.Path, identifier: str, *, signed: bool) -> None:
    """Serve one package the way the flat container lays it out."""
    lower = identifier.lower()
    directory = root / lower / "1.0.0"
    directory.mkdir(parents=True)
    package(directory, lower, signed=signed).rename(directory / f"{lower}.1.0.0.nupkg")


def test_signed_packages_the_registry_serves_are_fetched(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    served = tmp_path / "served"
    registry(served, "LibTmux", signed=True)
    registry(served, "LibTmux.Mcp", signed=True)
    output = tmp_path / "published"

    checks.fetch_published(uploaded, output, base_url=served.as_uri(), deadline=5, interval=0.01)

    assert sorted(path.name for path in output.iterdir()) == [
        "LibTmux.1.0.0.nupkg",
        "LibTmux.Mcp.1.0.0.nupkg",
    ]


def test_a_package_without_the_repository_signature_is_refused(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    served = tmp_path / "served"
    registry(served, "LibTmux", signed=False)

    with pytest.raises(
        checks.ReleaseCheckError, match=r"LibTmux 1\.0\.0 without a repository signature"
    ):
        checks.fetch_published(
            uploaded, tmp_path / "published", base_url=served.as_uri(), deadline=5, interval=0.01
        )


def test_a_package_the_registry_never_serves_ends_the_wait_at_the_deadline(
    uploaded: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    with pytest.raises(checks.ReleaseCheckError, match=r"did not serve LibTmux 1\.0\.0 within"):
        checks.fetch_published(
            uploaded,
            tmp_path / "published",
            base_url=(tmp_path / "empty").as_uri(),
            deadline=0.2,
            interval=0.01,
        )


def test_the_command_reports_a_failed_check_as_status_1(
    uploaded: pathlib.Path, tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    sbom = write_sbom(tmp_path / "sbom.json")

    status = checks.main(["sbom", "--packages", str(uploaded), "--sbom", str(sbom)])

    assert status == 1
    assert "::error::The SBOM does not name LibTmux." in capsys.readouterr().err
