"""Prove the matrix runner records the lanes it ran, and refuses the ones it cannot."""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
import typing as t

import psutil
import pytest

import assemble_bundle
import run_matrix
import validate

COMMIT_TREE = "e" * 40
INTEGRATION_PROJECT = run_matrix.INTEGRATION_PROJECT
MIRROR_REFRESH_SKIP = run_matrix.MIRROR_REFRESH_SKIP
VERSIONS = run_matrix.supported_versions()
LANES = len(VERSIONS) * len(run_matrix.FRAMEWORKS)
TRANSITION = run_matrix.TRANSITION_VERSION


@pytest.mark.parametrize(
    ("version", "framework", "project", "names", "total", "exit_code", "accepted"),
    [
        ("3.3a", "net8.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 2, 0, True),
        ("3.3a", "net10.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 2, 0, True),
        ("3.2a", "net8.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 2, 0, False),
        ("3.3a", "net9.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 2, 0, False),
        ("3.3a", "net8.0", "other.csproj", [MIRROR_REFRESH_SKIP], 2, 0, False),
        ("3.3a", "net8.0", INTEGRATION_PROJECT, ["OtherTest"], 2, 0, False),
        ("3.3a", "net8.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP, "OtherTest"], 3, 0, False),
        ("3.3a", "net8.0", INTEGRATION_PROJECT, [], 2, 0, False),
        ("3.3a", "net8.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 1, 0, False),
        ("3.3a", "net8.0", INTEGRATION_PROJECT, [MIRROR_REFRESH_SKIP], 2, 1, False),
        ("3.7c", "net10.0", INTEGRATION_PROJECT, [], 1, 0, True),
    ],
    ids=[
        "allowed-net8",
        "allowed-net10",
        "wrong-version",
        "wrong-framework",
        "wrong-project",
        "wrong-name",
        "extra-skip",
        "missing-expected-skip",
        "all-skipped",
        "failed-run",
        "ordinary-no-skips",
    ],
)
def test_lane_admits_only_the_named_tmux_3_3a_mirror_skip(
    version: str,
    framework: str,
    project: str,
    names: list[str],
    total: int,
    exit_code: int,
    accepted: bool,
) -> None:
    """A version exception must not turn other skipped or empty lanes green."""
    output = "".join(
        [
            *(f"skipped {name}\n" for name in names),
            f"  See the tracked condition for {MIRROR_REFRESH_SKIP}.\n",
            f"total: {total}\n",
            f"skipped: {len(names)}\n",
        ]
    )

    passed, count = run_matrix.lane_passed(project, version, framework, exit_code, output)

    assert passed is accepted
    assert count == total


class Toolchain:
    """A fake ``mise``, tmux builds bound to release tags, and recorded source calls."""

    def __init__(self, tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
        """Lay out the tmux builds, the tag repository and the fake ``mise``."""
        self.root = tmp_path / "tmux"
        self.log = tmp_path / "lanes.txt"
        self.source_calls: list[tuple[str, list[pathlib.Path]]] = []
        self.commits = self.tag_repository(tmp_path / "tags.git", [*VERSIONS, TRANSITION])
        for version in [*VERSIONS, TRANSITION]:
            binary = self.root / version / "bin" / "tmux"
            binary.parent.mkdir(parents=True)
            binary.write_text(f"#!{sys.executable}\nprint('tmux {version}')\n", encoding="utf-8")
            binary.chmod(0o755)

        bin_directory = tmp_path / "bin"
        bin_directory.mkdir()
        mise = bin_directory / "mise"
        mise.write_text(self.mise_script(), encoding="utf-8")
        mise.chmod(0o755)
        monkeypatch.setenv("PATH", f"{bin_directory}{os.pathsep}{os.environ['PATH']}")
        monkeypatch.setenv("MATRIX_LOG", str(self.log))
        monkeypatch.setenv("LIBTMUX_TMUX_REPOSITORY", str(tmp_path / "tags.git"))
        monkeypatch.setenv("TMPDIR", str(tmp_path))
        monkeypatch.setattr(assemble_bundle, "source_tree_fingerprint", self.fingerprint)
        monkeypatch.setattr(assemble_bundle, "source_state", self.state)

    @staticmethod
    def tag_repository(path: pathlib.Path, versions: list[str]) -> dict[str, str]:
        """Create a repository with one tagged commit per version."""
        env = {
            **os.environ,
            "GIT_AUTHOR_DATE": "2026-01-01T00:00:00",
            "GIT_COMMITTER_DATE": "2026-01-01T00:00:00",
        }
        base = [
            "git",
            "-c",
            "user.name=Matrix Test",
            "-c",
            "user.email=matrix@example.invalid",
            "-C",
            str(path),
        ]
        subprocess.run(["git", "init", "--quiet", str(path)], check=True)
        commits = {}
        for version in versions:
            (path / "version.txt").write_text(version + "\n", encoding="utf-8")
            subprocess.run([*base, "add", "version.txt"], check=True)
            subprocess.run([*base, "commit", "--quiet", "-m", version], check=True, env=env)
            subprocess.run([*base, "tag", version], check=True)
            commits[version] = subprocess.run(
                [*base, "rev-parse", "HEAD"], check=True, capture_output=True, text=True
            ).stdout.strip()
        return commits

    @staticmethod
    def mise_script() -> str:
        """Return a ``mise`` that reports the pinned SDK and answers ``dotnet test``."""
        pinned = run_matrix.pinned_sdk_version()
        return f"""#!/bin/sh
if [ "$4" = "--version" ]; then
    printf '%s\\n' '{pinned}'
elif [ "$4" = "test" ]; then
    if [ -n "${{LIBTMUX_PROTOCOL_TRANSCRIPT_DIR-}}" ]; then
        mkdir -p "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR"
        printf '%s\\n' 'event=control-send sequence=1 bytes=31' \\
            'event=control-receive sequence=1 marker=%begin' \\
            'event=control-receive sequence=1 marker=%end' \\
            > "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/control.txt"
        printf '%s\\n' 'event=pty-attach state=visible' 'event=pty-detach state=gone' \\
            > "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/pty.txt"
        case "$*" in
            *--filter-method*)
                if [ "${{LIBTMUX_EXPECTED_TMUX_VERSION-}}" = {TRANSITION} ]; then
                    workaround=applied
                else
                    workaround=omitted
                fi
                printf '%s\\n' "event=break-pane-transition framework=${{LIBTMUX_TEST_FRAMEWORK-}} tmux-source-commit=${{LIBTMUX_TMUX_SOURCE_COMMIT-}} tmux-version=${{LIBTMUX_EXPECTED_TMUX_VERSION-}} workaround=$workaround outcome=passed" \\
                    >> "$LIBTMUX_PROTOCOL_TRANSCRIPT_DIR/break-pane-transition.txt"
                ;;
        esac
    fi
    printf '%s|%s|%s|%s\\n' "${{LIBTMUX_EXPECTED_TMUX_VERSION-}}" \\
        "${{LIBTMUX_TRANSITION_TMUX_3_7-}}" \\
        "${{LIBTMUX_TRANSITION_TMUX_3_7_SOURCE_COMMIT-}}" "$*" >> "$MATRIX_LOG"
    mirror_skip=
    if [ "${{LIBTMUX_EXPECTED_TMUX_VERSION-}}" = 3.3a ]; then
        case " $* " in
            *" --filter-method "*) ;;
            *" --project {INTEGRATION_PROJECT} "*) mirror_skip=1 ;;
        esac
    fi
    if [ "${{MATRIX_HANG_VERSION-}}" = "${{LIBTMUX_EXPECTED_TMUX_VERSION-}}" ]; then
        sleep 60 &
        printf '%s\\n' "$!" > "$MATRIX_HANG_PID"
        wait
    fi
    if [ "${{MATRIX_FAIL_VERSION-}}" = "${{LIBTMUX_EXPECTED_TMUX_VERSION-}}" ]; then
        printf '%s\\n' 'total: 1' 'skipped: 0'
        exit 1
    fi
    if [ -n "$mirror_skip" ]; then
        printf '%s\\n' 'skipped {MIRROR_REFRESH_SKIP}' 'total: 2' 'skipped: 1'
    else
        printf '%s\\n' 'total: 1' 'skipped: 0'
    fi
fi
"""

    def fingerprint(self, repository: pathlib.Path, excluded: t.Iterable[pathlib.Path] = ()) -> str:
        """Record the call and answer a fixed fingerprint."""
        self.source_calls.append(("fingerprint", list(excluded)))
        return "0" * 64

    def state(self, repository: pathlib.Path, excluded: t.Iterable[pathlib.Path] = ()) -> str:
        """Record the call and answer a clean tree."""
        self.source_calls.append(("state", list(excluded)))
        return "clean"

    def run(self, *arguments: str) -> int:
        """Run the matrix against this toolchain's tmux builds."""
        return run_matrix.main(["--tmux-root", str(self.root), *arguments])

    def observations(self) -> list[list[str]]:
        """Return one ``version|transition binary|transition commit|argv`` row per lane."""
        return [line.split("|", maxsplit=3) for line in self.log.read_text().splitlines()]


@pytest.fixture
def toolchain(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> Toolchain:
    return Toolchain(tmp_path, monkeypatch)


def test_the_manifest_decides_the_versions() -> None:
    manifest = json.loads(run_matrix.MANIFEST.read_text(encoding="utf-8"))

    assert manifest["supported"] == VERSIONS


def test_an_ordinary_run_skips_the_transition(toolchain: Toolchain) -> None:
    """Do not locate or run the 3.7 transition for ordinary component matrices."""
    shutil.rmtree(toolchain.root / TRANSITION)

    assert toolchain.run(INTEGRATION_PROJECT) == 0

    rows = toolchain.observations()
    assert len(rows) == LANES
    assert {row[1] for row in rows} == {""}
    assert {row[2] for row in rows} == {""}


def test_lane_environment_sets_LIBTMUX_TEST_TMUX_and_clears_ambient_selectors(
    toolchain: Toolchain, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Each lane receives LIBTMUX_TEST_TMUX without inherited selectors or LIBTMUX_TMUX."""
    for decoy in (
        "TMUX",
        "TMUX_PANE",
        "LIBTMUX_SOCKET",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_SOCKET_NAME",
        "LIBTMUX_TMUX",
    ):
        monkeypatch.setenv(decoy, "decoy")
    options = run_matrix.parse_arguments(["--tmux-root", str(toolchain.root), INTEGRATION_PROJECT])
    matrix = run_matrix.Matrix(options, ["3.8"])
    build = run_matrix.locate_build(toolchain.root, "3.8", os.environ["LIBTMUX_TMUX_REPOSITORY"])

    lane = matrix.lane_environment(build, "3.8", "net10.0", transition=False)

    assert lane["LIBTMUX_TEST_TMUX"] == str(build.binary)
    assert "LIBTMUX_TMUX" not in lane
    for decoy in (
        "TMUX",
        "TMUX_PANE",
        "LIBTMUX_SOCKET",
        "LIBTMUX_SOCKET_PATH",
        "LIBTMUX_SOCKET_NAME",
    ):
        assert decoy not in lane


@pytest.mark.parametrize("cohort", [run_matrix.COMPONENT_THREE_COHORT, "workspace"])
def test_an_exact_transition_proof_binds_the_tmux_3_7_source(
    toolchain: Toolchain, tmp_path: pathlib.Path, cohort: str
) -> None:
    """Run all stable lanes and four source-bound transition tests."""
    evidence = tmp_path / "0001"

    status = toolchain.run(
        "--evidence-dir", str(evidence), "--capability-cohort", cohort, INTEGRATION_PROJECT
    )

    assert status == 0
    rows = toolchain.observations()
    ordinary = [row for row in rows if "--filter-method" not in row[3]]
    transition = [row for row in rows if "--filter-method" in row[3]]
    assert len(ordinary) == LANES
    assert {row[0] for row in ordinary} == set(VERSIONS)
    assert len(transition) == 4
    assert {row[0] for row in transition} == {TRANSITION, run_matrix.TRANSITION_SUCCESSOR}
    assert {row[1] for row in ordinary} == {str(toolchain.root / TRANSITION / "bin" / "tmux")}
    assert {row[2] for row in ordinary} == {toolchain.commits[TRANSITION]}
    assert all(run_matrix.TRANSITION_TEST in row[3] for row in transition)

    lines = (
        (evidence / "protocol-transcripts" / "break-pane-transition.txt").read_text().splitlines()
    )
    assert len(lines) == 4
    assert all(validate.BREAK_PANE_TRANSCRIPT_PATTERN.fullmatch(line) for line in lines)
    results = [json.loads(line) for line in (evidence / "results.ndjson").read_text().splitlines()]
    assert len(results) == LANES
    assert {row["tmuxSourceCommit"] for row in results if row["tmuxVersion"] == "3.8"} == {
        toolchain.commits["3.8"]
    }
    environment = json.loads((evidence / "environment.json").read_text(encoding="utf-8"))
    assert environment["capabilityCohort"] == cohort
    assert environment["includeMasterAdvisory"] is False
    assert environment["sdkVersion"] == run_matrix.pinned_sdk_version()
    assert environment["transitionTmuxSourceCommits"] == {TRANSITION: toolchain.commits[TRANSITION]}
    assert [call[1] for call in toolchain.source_calls] == [[evidence], [evidence]]


def test_the_cohort_is_not_inferred_from_the_evidence_basename(
    toolchain: Toolchain, tmp_path: pathlib.Path
) -> None:
    """Treat an unrelated directory named 0001 as ordinary matrix evidence."""
    evidence = tmp_path / "0001"

    assert toolchain.run("--evidence-dir", str(evidence), INTEGRATION_PROJECT) == 0

    assert all("--filter-method" not in row[3] for row in toolchain.observations())
    environment = json.loads((evidence / "environment.json").read_text(encoding="utf-8"))
    assert "capabilityCohort" not in environment
    assert "transitionTmuxSourceCommits" not in environment


def test_the_closure_cohort_records_no_transition(
    toolchain: Toolchain, tmp_path: pathlib.Path
) -> None:
    """Retain the explicit closure cohort without replaying raw transition proof."""
    evidence = tmp_path / "closure"

    status = toolchain.run(
        "--evidence-dir",
        str(evidence),
        "--capability-cohort",
        run_matrix.CLOSURE_COHORT,
        INTEGRATION_PROJECT,
    )

    assert status == 0
    assert all("--filter-method" not in row[3] for row in toolchain.observations())
    environment = json.loads((evidence / "environment.json").read_text(encoding="utf-8"))
    assert environment["capabilityCohort"] == run_matrix.CLOSURE_COHORT
    assert "transitionTmuxSourceCommits" not in environment
    assert not (evidence / "protocol-transcripts" / "break-pane-transition.txt").exists()
    assert len((evidence / "results.ndjson").read_text().splitlines()) == LANES


def test_a_failing_lane_stops_the_run_and_publishes_nothing(
    toolchain: Toolchain, tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A red lane keeps its output for diagnosis and leaves no half-recorded bundle."""
    evidence = tmp_path / "evidence"
    monkeypatch.setenv("MATRIX_FAIL_VERSION", "3.5")

    status = toolchain.run("--evidence-dir", str(evidence), INTEGRATION_PROJECT)

    assert status == 1
    assert not evidence.exists()
    assert not list(tmp_path.glob(".evidence*"))
    ran = {row[0] for row in toolchain.observations()}
    assert "3.5" in ran
    assert VERSIONS[VERSIONS.index("3.5") + 1] not in ran
    assert list(tmp_path.glob("libtmux-matrix-failures.*/1-3.5-*.log"))


def test_a_build_that_reports_another_version_is_refused(
    toolchain: Toolchain, capsys: pytest.CaptureFixture[str]
) -> None:
    binary = toolchain.root / "3.4" / "bin" / "tmux"
    binary.write_text(f"#!{sys.executable}\nprint('tmux 3.5')\n", encoding="utf-8")

    assert toolchain.run(INTEGRATION_PROJECT) == 1

    assert "reports 'tmux 3.5', not 'tmux 3.4'" in capsys.readouterr().err


def test_a_missing_build_is_refused(
    toolchain: Toolchain, capsys: pytest.CaptureFixture[str]
) -> None:
    (toolchain.root / "3.6" / "bin" / "tmux").unlink()

    assert toolchain.run(INTEGRATION_PROJECT) == 1

    assert "no tmux 3.6 at" in capsys.readouterr().err


def test_an_untagged_version_is_refused(toolchain: Toolchain) -> None:
    with pytest.raises(run_matrix.MatrixError, match="is not a tag"):
        run_matrix.resolve_commit(os.environ["LIBTMUX_TMUX_REPOSITORY"], "9.9")


@pytest.mark.parametrize(
    "arguments",
    [
        ["--capability-cohort", run_matrix.COMPONENT_THREE_COHORT],
        ["--evidence-dir", "evidence", "--capability-cohort", "unknown"],
        ["--include-master-advisory"],
    ],
)
def test_an_invalid_combination_is_a_usage_error(
    toolchain: Toolchain, arguments: list[str]
) -> None:
    """Require a known retained cohort with evidence, and no master lane."""
    with pytest.raises(SystemExit) as raised:
        toolchain.run(*arguments, INTEGRATION_PROJECT)

    assert raised.value.code == 2


def test_the_workspace_cohort_requires_the_complete_integration_project(
    toolchain: Toolchain, tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """Do not admit a smaller test project as the complete native proof cohort."""
    with pytest.raises(SystemExit) as raised:
        toolchain.run(
            "--evidence-dir",
            str(tmp_path / "workspace"),
            "--capability-cohort",
            "workspace",
            "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        )

    assert raised.value.code == 2
    assert "requires the complete integration project" in capsys.readouterr().err


@pytest.mark.parametrize("directory", ["/", ".", ".."])
def test_an_unsafe_evidence_directory_is_a_usage_error(
    toolchain: Toolchain, directory: str
) -> None:
    with pytest.raises(SystemExit) as raised:
        toolchain.run("--evidence-dir", directory, INTEGRATION_PROJECT)

    assert raised.value.code == 2


def _gone(pid: int) -> bool:
    """Say whether a process has ended; a zombie awaiting its parent has."""
    try:
        return psutil.Process(pid).status() == psutil.STATUS_ZOMBIE
    except psutil.NoSuchProcess:
        return True


def test_a_lane_that_never_finishes_is_stopped_with_its_children(
    toolchain: Toolchain, tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A hung lane fails at its deadline and leaves no process behind."""
    child = tmp_path / "hung-child.pid"
    monkeypatch.setenv("MATRIX_HANG_VERSION", "3.5")
    monkeypatch.setenv("MATRIX_HANG_PID", str(child))
    monkeypatch.setenv("LIBTMUX_MATRIX_LANE_TIMEOUT", "1")

    started = time.monotonic()
    status = toolchain.run(INTEGRATION_PROJECT)

    assert status == 1
    assert time.monotonic() - started < 30
    assert _gone(int(child.read_text()))
    (log,) = tmp_path.glob("libtmux-matrix-failures.*/1-3.5-*.log")
    assert "exceeded its limit of 1s" in log.read_text()
    assert VERSIONS[VERSIONS.index("3.5") + 1] not in {row[0] for row in toolchain.observations()}


def test_an_interrupted_lane_stops_its_process_group(
    toolchain: Toolchain, tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Ctrl-C while a lane runs must not leave dotnet test running behind it."""
    child = tmp_path / "hung-child.pid"
    monkeypatch.setenv("MATRIX_HANG_VERSION", VERSIONS[0])
    monkeypatch.setenv("MATRIX_HANG_PID", str(child))
    interrupted: list[int] = []

    class InterruptedOnce(subprocess.Popen[str]):
        def wait(self, timeout: float | None = None) -> int:
            if not interrupted and isinstance(self.args, list) and "test" in self.args:
                while not child.exists():
                    time.sleep(0.01)
                interrupted.append(self.pid)
                raise KeyboardInterrupt
            return super().wait(timeout)

    monkeypatch.setattr(run_matrix.subprocess, "Popen", InterruptedOnce)

    with pytest.raises(KeyboardInterrupt):
        toolchain.run(INTEGRATION_PROJECT)

    assert interrupted
    assert _gone(int(child.read_text()))


def test_a_stalled_tag_lookup_is_an_error_not_a_hang(
    toolchain: Toolchain, monkeypatch: pytest.MonkeyPatch
) -> None:
    """``git ls-remote`` gets a deadline, so an unreachable mirror ends the run."""

    def stalled(*arguments: t.Any, **keywords: t.Any) -> t.NoReturn:
        assert keywords.get("timeout"), "git ls-remote must carry a timeout"
        raise subprocess.TimeoutExpired(arguments[0], keywords["timeout"])

    monkeypatch.setattr(run_matrix.subprocess, "run", stalled)

    with pytest.raises(run_matrix.MatrixError, match="did not answer within"):
        run_matrix.resolve_commit("https://example.invalid/tmux.git", "3.5")


def test_a_build_that_never_reports_its_version_is_an_error(
    toolchain: Toolchain, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    """``tmux -V`` gets a deadline too."""
    binary = toolchain.root / "3.4" / "bin" / "tmux"
    binary.write_text(f"#!{sys.executable}\nimport time\ntime.sleep(60)\n", encoding="utf-8")
    monkeypatch.setattr(run_matrix, "QUERY_TIMEOUT_SECONDS", 1.0)

    assert toolchain.run(INTEGRATION_PROJECT) == 1

    assert "did not report its version within" in capsys.readouterr().err
