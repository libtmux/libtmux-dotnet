"""Prove the actionlint runner refuses an archive it cannot trust."""

from __future__ import annotations

import hashlib
import io
import pathlib
import tarfile

import pytest

import run_actionlint


def archive(members: dict[str, bytes]) -> bytes:
    """Return a gzip tarball that holds the named files."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as bundle:
        for name, content in members.items():
            entry = tarfile.TarInfo(name)
            entry.size = len(content)
            bundle.addfile(entry, io.BytesIO(content))
    return buffer.getvalue()


def test_the_checked_in_pin_names_a_version() -> None:
    pin = run_actionlint.ROOT / ".tool-versions"

    assert run_actionlint.pinned_version(pin.read_text(encoding="utf-8"))


def test_a_changed_archive_is_refused() -> None:
    expected = hashlib.sha256(b"release").hexdigest()

    run_actionlint.verify_digest(b"release", expected)
    with pytest.raises(ValueError, match="digest is"):
        run_actionlint.verify_digest(b"replaced", expected)


def test_the_binary_is_extracted_executable(tmp_path: pathlib.Path) -> None:
    binary = run_actionlint.extract_binary(archive({"actionlint": b"binary"}), tmp_path)

    assert binary.read_bytes() == b"binary"
    assert binary.stat().st_mode & 0o111


def test_an_archive_without_the_binary_is_refused(tmp_path: pathlib.Path) -> None:
    with pytest.raises(ValueError, match="no actionlint file"):
        run_actionlint.extract_binary(archive({"README.md": b"docs"}), tmp_path)


@pytest.fixture
def pinned(tmp_path: pathlib.Path) -> pathlib.Path:
    path = tmp_path / ".tool-versions"
    path.write_text("actionlint 9.9.9\n", encoding="utf-8")
    return path


def test_a_verified_archive_runs_and_its_status_is_returned(
    pinned: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    release = archive({"actionlint": b"binary"})
    urls: list[str] = []
    monkeypatch.setattr(run_actionlint, "fetch", lambda url: urls.append(url) or release)
    monkeypatch.setattr(run_actionlint, "run", lambda binary: 3 if binary.is_file() else 0)

    status = run_actionlint.main(
        ["--tool-versions", str(pinned), "--sha256", hashlib.sha256(release).hexdigest()]
    )

    assert status == 3
    assert urls == [run_actionlint.archive_url("9.9.9")]


def test_an_unverified_archive_is_never_run(
    pinned: pathlib.Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    ran: list[pathlib.Path] = []
    monkeypatch.setattr(run_actionlint, "fetch", lambda url: archive({"actionlint": b"swapped"}))
    monkeypatch.setattr(run_actionlint, "run", lambda binary: ran.append(binary) or 0)

    status = run_actionlint.main(
        ["--tool-versions", str(pinned), "--sha256", hashlib.sha256(b"release").hexdigest()]
    )

    assert status == 1
    assert ran == []
    assert "digest is" in capsys.readouterr().err
