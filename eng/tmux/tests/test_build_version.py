"""Prove build-version.sh builds a release from its closed list and a ref apart.

The tmux repository is a local stand-in whose build produces a script that
reports a version, so these tests need git, make and jq but no network and no
compiler.
"""

from __future__ import annotations

import os
import pathlib
import shutil
import subprocess
import typing as t

import pytest

SCRIPT = pathlib.Path(__file__).parents[1] / "build-version.sh"

pytestmark = pytest.mark.skipif(
    any(shutil.which(tool) is None for tool in ("bash", "git", "jq", "make", "awk", "sed")),
    reason="needs bash, git, jq, make, awk and sed",
)

AUTOGEN = """\
echo AUTOGEN-RAN
cat > configure <<'CONFIGURE'
case "$1" in --prefix=*) prefix="${1#--prefix=}" ;; esac
printf 'PREFIX = %s\\n' "$prefix" > Makefile
cat >> Makefile <<'MAKEFILE'
all:
	:
install:
	mkdir -p $(PREFIX)/bin
	printf '#!/bin/sh\\necho "tmux %s"\\n' "$$(cat VERSION)" > $(PREFIX)/bin/tmux
	chmod +x $(PREFIX)/bin/tmux
MAKEFILE
CONFIGURE
chmod +x configure
"""


def git(repository: pathlib.Path, *arguments: str) -> str:
    """Run git in the stand-in repository and return what it prints."""
    environment = {
        **os.environ,
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@example.invalid",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@example.invalid",
    }
    done = subprocess.run(
        ["git", "-C", str(repository), *arguments],
        check=True,
        capture_output=True,
        text=True,
        env=environment,
    )
    return done.stdout.strip()


def commit(repository: pathlib.Path, version: str) -> str:
    """Record a source tree that builds a tmux reporting `version`."""
    (repository / "VERSION").write_text(version + "\n", encoding="utf-8")
    (repository / "autogen.sh").write_text(AUTOGEN, encoding="utf-8")
    git(repository, "add", "-A")
    git(repository, "commit", "--quiet", "-m", f"tmux {version}")
    return git(repository, "rev-parse", "HEAD")


class Fixture(t.NamedTuple):
    """The stand-in repository and what it holds."""

    repository: pathlib.Path
    artifacts: pathlib.Path
    release: str
    topic: str
    annotated: str


@pytest.fixture
def fixture(tmp_path: pathlib.Path) -> Fixture:
    """Lay out a tmux repository with a release, an annotated tag and a branch."""
    repository = tmp_path / "tmux"
    repository.mkdir()
    git(repository, "init", "--quiet", "-b", "master")
    release = commit(repository, "3.8")
    git(repository, "tag", "3.8")
    git(repository, "checkout", "--quiet", "-b", "topic")
    annotated = commit(repository, "9.1")
    git(repository, "tag", "-a", "-m", "annotated", "9.1")
    topic = commit(repository, "9.2")
    git(repository, "checkout", "--quiet", "master")
    return Fixture(repository, tmp_path / "artifacts", release, topic, annotated)


def build(
    fixture: Fixture, *arguments: str
) -> subprocess.CompletedProcess[str]:
    """Run the script against the stand-in repository."""
    environment = {
        **os.environ,
        "LIBTMUX_TMUX_REPOSITORY": str(fixture.repository),
        "LIBTMUX_TMUX_ARTIFACT_DIRECTORY": str(fixture.artifacts),
        "LIBTMUX_BUILD_JOBS": "1",
    }
    return subprocess.run(
        ["bash", str(SCRIPT), *arguments],
        capture_output=True,
        text=True,
        env=environment,
        check=False,
    )


def fields(result: subprocess.CompletedProcess[str]) -> dict[str, str]:
    """Read the key=value lines a build prints for the workflow."""
    return dict(line.split("=", 1) for line in result.stdout.splitlines())


def test_a_tag_resolves_to_its_commit(fixture: Fixture) -> None:
    result = build(fixture, "--resolve", "3.8")

    assert result.returncode == 0, result.stderr
    assert result.stdout == f"commit={fixture.release}\n"


def test_an_annotated_tag_resolves_to_the_commit_not_the_tag_object(fixture: Fixture) -> None:
    result = build(fixture, "--resolve", "9.1")

    assert result.stdout == f"commit={fixture.annotated}\n"
    assert git(fixture.repository, "rev-parse", "9.1") != fixture.annotated


def test_a_branch_resolves_to_its_tip(fixture: Fixture) -> None:
    assert build(fixture, "--resolve", "topic").stdout == f"commit={fixture.topic}\n"


def test_a_full_commit_resolves_to_itself(fixture: Fixture) -> None:
    assert build(fixture, "--resolve", fixture.topic).stdout == f"commit={fixture.topic}\n"


def test_a_tag_wins_over_a_branch_of_the_same_name(fixture: Fixture) -> None:
    git(fixture.repository, "branch", "3.8", fixture.topic)

    assert build(fixture, "--resolve", "3.8").stdout == f"commit={fixture.release}\n"


def test_an_unknown_ref_does_not_resolve(fixture: Fixture) -> None:
    result = build(fixture, "--resolve", "no-such-ref")

    assert result.returncode != 0
    assert result.stdout == ""
    assert "is not a tag, a branch or a full commit" in result.stderr


@pytest.mark.parametrize(
    "ref",
    [
        "--upload-pack=touch-pwned",
        "-x",
        "a..b",
        "topic.lock",
        "topic/",
        "$(id)",
        "a b",
        "a;b",
        "a" * 65,
        "deadbeef",
        "",
    ],
)
@pytest.mark.parametrize("flag", ["--resolve", "--ref"])
def test_a_malformed_ref_is_refused_before_git_sees_it(
    fixture: Fixture, flag: str, ref: str
) -> None:
    result = build(fixture, flag, ref)

    assert result.returncode != 0
    assert result.stdout == ""
    assert "usable tmux ref" in result.stderr or "abbreviated commit" in result.stderr
    assert not (fixture.artifacts / "sources").exists()


def test_a_ref_builds_from_source_at_the_resolved_commit(fixture: Fixture) -> None:
    result = build(fixture, "--ref", "topic")

    assert result.returncode == 0, result.stderr
    assert "AUTOGEN-RAN" in result.stderr
    printed = fields(result)
    assert printed["commit"] == fixture.topic
    assert printed["version"] == "9.2"
    install = fixture.artifacts / "installs" / f"ref-{fixture.topic}"
    assert printed["binary"] == str(install / "bin" / "tmux")
    assert (install / "source-commit").read_text(encoding="utf-8").strip() == fixture.topic


def test_a_ref_given_as_a_commit_is_fetched_by_hash(fixture: Fixture) -> None:
    result = build(fixture, "--ref", fixture.annotated)

    assert result.returncode == 0, result.stderr
    assert fields(result)["commit"] == fixture.annotated
    assert fields(result)["version"] == "9.1"


def test_a_cached_ref_build_is_reused(fixture: Fixture) -> None:
    first = build(fixture, "--ref", "topic")
    second = build(fixture, "--ref", "topic")

    assert "AUTOGEN-RAN" in first.stderr
    assert second.returncode == 0, second.stderr
    assert "AUTOGEN-RAN" not in second.stderr
    assert second.stdout == first.stdout


def test_a_moved_branch_is_built_afresh_not_taken_from_the_cache(fixture: Fixture) -> None:
    first = build(fixture, "--ref", "topic")
    git(fixture.repository, "checkout", "--quiet", "topic")
    moved = commit(fixture.repository, "9.3")
    git(fixture.repository, "checkout", "--quiet", "master")
    second = build(fixture, "--ref", "topic")

    assert fields(first)["commit"] == fixture.topic
    assert fields(second)["commit"] == moved
    assert fields(second)["version"] == "9.3"
    assert "AUTOGEN-RAN" in second.stderr


def test_a_tampered_ref_cache_is_rebuilt(fixture: Fixture) -> None:
    first = build(fixture, "--ref", "topic")
    pathlib.Path(fields(first)["binary"]).write_text('#!/bin/sh\necho "tmux 0.0"\n', encoding="utf-8")
    second = build(fixture, "--ref", "topic")

    assert second.returncode == 0, second.stderr
    assert "cache validation failed" in second.stderr
    assert fields(second)["version"] == "9.2"


def test_a_cache_that_holds_another_commit_than_its_name_is_rebuilt(fixture: Fixture) -> None:
    first = build(fixture, "--ref", "topic")
    name = f"ref-{fixture.topic}"
    source = fixture.artifacts / "sources" / name
    install = fixture.artifacts / "installs" / name
    git(source, "fetch", "--quiet", "--depth", "1", str(fixture.repository), fixture.release)
    git(source, "checkout", "--quiet", "--detach", "FETCH_HEAD")
    (install / "source-commit").write_text(fixture.release + "\n", encoding="utf-8")
    metadata = install / "cache-metadata.json"
    rewritten = subprocess.run(
        ["jq", "--arg", "commit", fixture.release, ".sourceCommit = $commit", str(metadata)],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    metadata.write_text(rewritten, encoding="utf-8")
    second = build(fixture, "--ref", "topic")

    assert first.returncode == 0, first.stderr
    assert second.returncode == 0, second.stderr
    assert "cache validation failed" in second.stderr
    assert fields(second)["commit"] == fixture.topic
    assert fields(second)["version"] == "9.2"


def test_a_release_is_still_built_from_the_closed_list(fixture: Fixture) -> None:
    result = build(fixture, "3.8")

    assert result.returncode == 0, result.stderr
    assert result.stdout.splitlines() == [
        f"binary={fixture.artifacts}/installs/3.8/bin/tmux",
        f"commit={fixture.release}",
    ]
    assert (fixture.artifacts / "installs" / "3.8" / "cache-metadata.json").is_file()


@pytest.mark.parametrize("version", ["topic", "9.1", "3.9", "--ref", "a" * 40])
def test_a_release_build_refuses_anything_off_the_list(fixture: Fixture, version: str) -> None:
    result = build(fixture, version)

    assert result.returncode == 2
    assert result.stdout == ""
    assert not fixture.artifacts.exists()


def test_the_flags_take_exactly_one_ref(fixture: Fixture) -> None:
    for arguments in (("--ref",), ("--resolve",), ("--ref", "topic", "extra"), ("3.8", "3.7")):
        result = build(fixture, *arguments)

        assert result.returncode == 2
        assert "usage: build-version.sh" in result.stderr
