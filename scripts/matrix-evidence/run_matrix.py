#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Run one test project against every supported tmux and record the matrix.

A maintainer runs this at a release boundary. It builds the solution, runs the
project once per supported tmux version and framework, and with
``--evidence-dir`` stages the results as a candidate bundle that is validated
and published as one atomic step. Without it, the run only reports.

Every version lane needs a tmux build under ``--tmux-root``, laid out as
``<root>/<version>/bin/tmux``. The runner checks that each reports its own
version and binds it to the commit the release tag names, which it reads from
the tmux repository (``LIBTMUX_TMUX_REPOSITORY`` overrides the URL).

xUnit runs one collection per logical processor by default, so a lane drives
that many tmux servers and shells at once. ``dotnet-tmux.yml`` gets away with it
on a dedicated runner; anywhere else the readiness budgets are starved by the
suite's own concurrency. Set ``LIBTMUX_MATRIX_MAX_THREADS`` when the host is
idle.

```console
$ uv run scripts/matrix-evidence/run_matrix.py \\
    --tmux-root artifacts/tmux \\
    --evidence-dir docs/parity/evidence/0014 \\
    --capability-cohort workspace \\
    tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj
```
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import typing as t

import assemble_bundle
import validate

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = REPOSITORY / "eng" / "tmux" / "versions.json"
GLOBAL_JSON = REPOSITORY / "global.json"
DEFAULT_TMUX_ROOT = REPOSITORY / "artifacts" / "tmux"
TMUX_REPOSITORY = "https://github.com/tmux/tmux.git"

FRAMEWORKS = ("net10.0", "net8.0")
COMPONENT_THREE_COHORT = "0001"
CLOSURE_COHORT = "closure"
WORKSPACE_COHORT = "workspace"
COHORTS = (COMPONENT_THREE_COHORT, CLOSURE_COHORT, WORKSPACE_COHORT)
TRANSITION_VERSION = "3.7"
TRANSITION_SUCCESSOR = "3.7a"
INTEGRATION_PROJECT = "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
TRANSITION_TEST = "LibTmux.IntegrationTests.Versioning.VersionParityTests.BreakPane37Workaround"
MIRROR_REFRESH_SKIP = (
    "LibTmux.IntegrationTests.Snapshots.ServerMirrorTests."
    "A_change_tmux_does_not_announce_is_seen_within_the_refresh_interval"
)
REDACTION_REJECTED = [
    "absolute-paths",
    "emails",
    "environment-values",
    "executable-paths",
    "hostnames",
    "socket-names",
    "temporary-directories",
    "terminal-device-names",
    "tokens",
    "usernames",
]

TOTAL = re.compile(r"^\s*total:\s*(\d+)")
SKIPPED = re.compile(r"^\s*skipped:\s*(\d+)")
SKIPPED_NAME = re.compile(r"^\s*skipped\s+(\S+)")
FULL_COMMIT = re.compile(r"[0-9a-f]{40}")


class MatrixError(Exception):
    """The matrix cannot run or record what was asked."""


@dataclasses.dataclass(frozen=True)
class TmuxBuild:
    """One tmux binary bound to the commit its release tag names."""

    version: str
    binary: pathlib.Path
    commit: str


@dataclasses.dataclass(frozen=True)
class Options:
    """What one invocation asks for, after validation."""

    project: str
    tmux_root: pathlib.Path
    evidence_directory: pathlib.Path | None
    capability_cohort: str | None

    @property
    def transition_proof(self) -> bool:
        """Say whether the exact tmux 3.7 transition proof runs."""
        return self.capability_cohort in (COMPONENT_THREE_COHORT, WORKSPACE_COHORT)


def supported_versions() -> list[str]:
    """Return the tmux versions the matrix runs, from the repository manifest."""
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    return t.cast("list[str]", manifest["supported"])


def pinned_sdk_version() -> str:
    """Return the .NET SDK version ``global.json`` pins."""
    document = json.loads(GLOBAL_JSON.read_text(encoding="utf-8"))
    return t.cast("str", document["sdk"]["version"])


def parse_arguments(argv: list[str] | None = None) -> Options:
    """Validate the command line, exiting with status 2 for a usage error."""
    parser = argparse.ArgumentParser(
        prog="run_matrix.py", description=(__doc__ or "").split("\n\n")[0]
    )
    parser.add_argument("--evidence-dir", type=pathlib.Path, help="publish the bundle here")
    parser.add_argument("--capability-cohort", help=f"one of {', '.join(COHORTS)}")
    parser.add_argument(
        "--tmux-root",
        type=pathlib.Path,
        default=pathlib.Path(os.environ.get("LIBTMUX_TMUX_ROOT", DEFAULT_TMUX_ROOT)),
        help="directory of <version>/bin/tmux builds",
    )
    parser.add_argument("project", help="path of the test project, from the repository root")
    arguments = parser.parse_args(argv)

    project: str = arguments.project
    if not (REPOSITORY / project).is_file():
        parser.error(f"test project does not exist: {project}")

    evidence: pathlib.Path | None = arguments.evidence_dir
    if evidence is not None:
        if str(evidence) in ("/", ".", ".."):
            parser.error(f"unsafe evidence directory: {evidence}")
        if not evidence.is_absolute():
            evidence = REPOSITORY / evidence

    cohort: str | None = arguments.capability_cohort
    if cohort is not None:
        if cohort not in COHORTS:
            parser.error(f"unknown capability cohort: {cohort}")
        if evidence is None:
            parser.error("--capability-cohort requires --evidence-dir")
        if cohort == WORKSPACE_COHORT and project != INTEGRATION_PROJECT:
            parser.error("workspace cohort requires the complete integration project")
    return Options(project, arguments.tmux_root.absolute(), evidence, cohort)


def resolve_commit(repository: str, version: str) -> str:
    """Return the commit a release tag names, peeling an annotated tag."""
    listing = subprocess.run(
        [
            "git",
            "ls-remote",
            repository,
            f"refs/tags/{version}^{{}}",
            f"refs/tags/{version}",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if listing.returncode != 0:
        raise MatrixError(f"cannot read tags from {repository}: {listing.stderr.strip()}")
    refs = {
        parts[1]: parts[0]
        for parts in (line.split() for line in listing.stdout.splitlines())
        if len(parts) == 2
    }
    for name in (f"refs/tags/{version}^{{}}", f"refs/tags/{version}"):
        commit = refs.get(name, "")
        if FULL_COMMIT.fullmatch(commit):
            return commit
    raise MatrixError(f"{version} is not a tag of {repository}")


def locate_build(root: pathlib.Path, version: str, repository: str) -> TmuxBuild:
    """Find ``<root>/<version>/bin/tmux`` and bind it to its release commit."""
    binary = root / version / "bin" / "tmux"
    if not (binary.is_file() and os.access(binary, os.X_OK)):
        raise MatrixError(f"no tmux {version} at {binary}; build it there first")
    reported = subprocess.run(
        [str(binary), "-V"], capture_output=True, text=True, check=False
    ).stdout.strip()
    if reported != f"tmux {version}":
        raise MatrixError(f"{binary} reports {reported!r}, not 'tmux {version}'")
    return TmuxBuild(version, binary, resolve_commit(repository, version))


def lane_passed(
    project: str, version: str, framework: str, exit_status: int, output: str
) -> tuple[bool, int]:
    """Judge one lane from its exit status and output, and count its tests.

    Only the named tmux 3.3a mirror-refresh test may skip, and only in the
    integration project on a supported framework; any other skip, an empty run
    or a lane whose tests all skipped fails.

    >>> lane_passed("p.csproj", "3.8", "net8.0", 0, "total: 5\\nskipped: 0\\n")
    (True, 5)
    >>> lane_passed("p.csproj", "3.8", "net8.0", 0, "")
    (False, 0)
    """
    lines = output.splitlines()
    totals = [int(m.group(1)) for line in lines if (m := TOTAL.match(line))]
    skips = [int(m.group(1)) for line in lines if (m := SKIPPED.match(line))]
    names = [m.group(1) for line in lines if (m := SKIPPED_NAME.match(line))]
    total = totals[-1] if totals else None
    skipped = skips[-1] if skips else None

    expected_skips = 0
    expected_name = ""
    if project == INTEGRATION_PROJECT and version == "3.3a" and framework in FRAMEWORKS:
        expected_skips = 1
        expected_name = MIRROR_REFRESH_SKIP

    passed = (
        exit_status == 0
        and total is not None
        and skipped is not None
        and total > skipped
        and skipped == expected_skips
        and "\n".join(names) == expected_name
    )
    return passed, total or 0


class Matrix:
    """One run: its candidate bundle, failure logs and results file."""

    def __init__(self, options: Options, versions: list[str]) -> None:
        """Hold the options and the versions to run; nothing starts yet."""
        self.options = options
        self.versions = versions
        self.candidate: pathlib.Path | None = None
        self.ownership_nonce: str | None = None
        self.results_file: pathlib.Path | None = None
        self.failure_directory = pathlib.Path(
            tempfile.mkdtemp(prefix="libtmux-matrix-failures.", dir=os.environ.get("TMPDIR"))
        )
        self.kept_failures = 0
        self.evaluated_commit = ""
        self.transition: TmuxBuild | None = None

    def cleanup(self) -> None:
        """Discard an unpublished candidate and an empty failure directory."""
        evidence = self.options.evidence_directory
        if self.candidate is not None and self.candidate.is_dir() and evidence is not None:
            assert self.ownership_nonce is not None
            assemble_bundle.discard_owned_candidate(self.candidate, evidence, self.ownership_nonce)
        with contextlib.suppress(OSError):
            self.failure_directory.rmdir()

    def record_result(
        self, version: str, framework: str, status: str, source_commit: str, test_count: int
    ) -> None:
        """Append one lane to the staged results."""
        if self.results_file is None:
            return
        row = {
            "advisory": False,
            "evaluatedCommit": self.evaluated_commit,
            "framework": framework,
            "status": status,
            "testCount": test_count,
            "tmuxSourceCommit": source_commit,
            "tmuxVersion": version,
        }
        with self.results_file.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(row, sort_keys=True, separators=(",", ":")) + "\n")

    def keep_failure_output(self, output: str, version: str, framework: str) -> None:
        """Keep a failing lane's output, which says what failed.

        A count says that something failed but never what, and a failure that
        nobody can name costs more reruns than keeping the file.
        """
        self.kept_failures += 1
        kept = self.failure_directory / f"{self.kept_failures}-{version}-{framework}.log"
        kept.write_text(output, encoding="utf-8")
        print(f"kept failing output at {kept}", file=sys.stderr)

    def dotnet_test(
        self, framework: str, environment: dict[str, str], *extra: str
    ) -> tuple[int, str]:
        """Run the project's tests on one framework, echoing and capturing output."""
        command = [
            "mise",
            "exec",
            "--",
            "dotnet",
            "test",
            "--project",
            self.options.project,
            "--configuration",
            "Release",
            "--framework",
            framework,
            "--no-build",
            "--minimum-expected-tests",
            "1",
            *extra,
            "--",
            "--max-threads",
            os.environ.get("LIBTMUX_MATRIX_MAX_THREADS", "2"),
        ]
        process = subprocess.Popen(
            command,
            cwd=REPOSITORY,
            env=environment,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        assert process.stdout is not None
        captured = []
        for line in process.stdout:
            sys.stdout.write(line)
            captured.append(line)
        return process.wait(), "".join(captured)

    def lane_environment(
        self, build: TmuxBuild, expected_version: str, framework: str, *, transition: bool
    ) -> dict[str, str]:
        """Return the environment one lane runs under.

        Part of what a lane proves runs tmux from inside tmux, the way a user's
        configuration does, which finds it by name rather than by LIBTMUX_TMUX.
        ``dotnet-tmux.yml`` puts the build under test on the path for that
        reason; a developer machine has another tmux, and reaching a different
        version proves nothing about the lane.
        """
        transcripts = self.candidate / "protocol-transcripts" if self.candidate else None
        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{build.binary.parent}{os.pathsep}{environment.get('PATH', '')}",
                "LIBTMUX_TMUX": str(build.binary),
                "LIBTMUX_TMUX_SOURCE_COMMIT": build.commit,
                "LIBTMUX_EXPECTED_TMUX_VERSION": expected_version,
                "LIBTMUX_INTEGRATION_REQUIRED": "1",
                "LIBTMUX_PROTOCOL_TRANSCRIPT_DIR": str(transcripts) if transcripts else "",
                "LIBTMUX_TRANSITION_TMUX_3_7": str(self.transition.binary)
                if self.transition
                else "",
                "LIBTMUX_TRANSITION_TMUX_3_7_SOURCE_COMMIT": (
                    self.transition.commit if self.transition else ""
                ),
            }
        )
        if transition:
            environment["LIBTMUX_BREAK_PANE_TRANSITION_PROOF"] = "1"
            environment["LIBTMUX_TEST_FRAMEWORK"] = framework
        return environment

    def run_one(self, build: TmuxBuild, framework: str) -> bool:
        """Run one version on one framework, and record whether it passed."""
        environment = self.lane_environment(build, build.version, framework, transition=False)
        status, output = self.dotnet_test(framework, environment)
        passed, count = lane_passed(self.options.project, build.version, framework, status, output)
        if not passed:
            self.keep_failure_output(output, build.version, framework)
        self.record_result(
            build.version, framework, "passed" if passed else "failed", build.commit, count
        )
        return passed

    def run_transition_one(self, build: TmuxBuild, framework: str) -> bool:
        """Run the break-pane transition proof against one build and framework."""
        environment = self.lane_environment(build, build.version, framework, transition=True)
        status, output = self.dotnet_test(
            framework, environment, "--filter-method", TRANSITION_TEST
        )
        total = [int(m.group(1)) for line in output.splitlines() if (m := TOTAL.match(line))]
        skipped = [int(m.group(1)) for line in output.splitlines() if (m := SKIPPED.match(line))]
        if status != 0 or (total[-1:] != [1]) or (skipped[-1:] != [0]):
            self.keep_failure_output(output, build.version, framework)
            print(
                f"break-pane transition failed for tmux {build.version} on {framework}",
                file=sys.stderr,
            )
            return False
        return True

    def stage_bundle_files(self, candidate: pathlib.Path, source: dict[str, str]) -> None:
        """Write the environment, redaction proof and producer manifest."""
        options = self.options
        environment: dict[str, t.Any] = {
            "evaluatedCommit": self.evaluated_commit,
            "frameworks": list(FRAMEWORKS),
            "includeMasterAdvisory": False,
            "platform": platform_name(),
            "redactionProof": True,
            "schemaVersion": 1,
            "sdkVersion": pinned_sdk_version(),
            "sourceState": source["state"],
            "sourceTreeFingerprint": source["fingerprint"],
            "tmuxVersions": self.versions,
        }
        if options.capability_cohort is not None:
            environment["capabilityCohort"] = options.capability_cohort
            environment["evaluatedCommitTree"] = source["tree"]
        if options.transition_proof and self.transition is not None:
            environment["transitionTmuxSourceCommits"] = {
                TRANSITION_VERSION: self.transition.commit
            }
        write_json(candidate / "environment.json", environment)
        write_json(
            candidate / "redaction-proof.json", {"passed": True, "rejected": REDACTION_REJECTED}
        )
        files = sorted(
            path.relative_to(candidate).as_posix()
            for path in candidate.rglob("*")
            if path.is_file() and path.name != "producer.json"
        )
        write_json(
            candidate / "producer.json",
            {
                "evaluatedCommit": self.evaluated_commit,
                "files": files,
                "producer": "matrix",
                "schemaVersion": 1,
                "sourceTreeFingerprint": source["fingerprint"],
            },
        )

    def run(self) -> int:
        """Build, run every lane, and publish the bundle when one was asked for."""
        options = self.options
        check_sdk()
        dotnet("restore", "LibTmux.slnx", "--locked-mode")
        dotnet("format", "LibTmux.slnx", "--verify-no-changes", "--no-restore")
        dotnet(
            "build", "LibTmux.slnx", "--configuration", "Release", "--no-restore", "--warnaserror"
        )

        repository_url = os.environ.get("LIBTMUX_TMUX_REPOSITORY", TMUX_REPOSITORY)
        self.evaluated_commit = git("rev-parse", "HEAD")
        evaluated_tree = git("rev-parse", "HEAD^{tree}")
        excluded = [options.evidence_directory] if options.evidence_directory else []
        source = {
            "fingerprint": assemble_bundle.source_tree_fingerprint(REPOSITORY, excluded),
            "state": assemble_bundle.source_state(REPOSITORY, excluded),
            "tree": evaluated_tree,
        }
        if options.transition_proof:
            self.transition = locate_build(options.tmux_root, TRANSITION_VERSION, repository_url)

        if options.evidence_directory is not None:
            options.evidence_directory.parent.mkdir(parents=True, exist_ok=True)
            self.candidate, self.ownership_nonce = assemble_bundle.create_owned_candidate(
                options.evidence_directory
            )
            (self.candidate / "protocol-transcripts").mkdir(parents=True, exist_ok=True)
            self.results_file = self.candidate / "results.ndjson"

        for version in self.versions:
            build = locate_build(options.tmux_root, version, repository_url)
            for framework in FRAMEWORKS:
                if not self.run_one(build, framework):
                    return 1

        if options.transition_proof and self.transition is not None:
            successor = locate_build(options.tmux_root, TRANSITION_SUCCESSOR, repository_url)
            for framework in FRAMEWORKS:
                for build in (self.transition, successor):
                    if not self.run_transition_one(build, framework):
                        return 1

        if self.candidate is not None and options.evidence_directory is not None:
            self.stage_bundle_files(self.candidate, source)
            validate.validate_bundle(self.candidate, phase="matrix")
            assert self.ownership_nonce is not None
            assemble_bundle.publish_owned_candidate(
                self.candidate, options.evidence_directory, self.ownership_nonce
            )
            self.candidate = None
            self.ownership_nonce = None
        return 0


def platform_name() -> str:
    """Name the platform the matrix recorded on, or refuse an unsupported one."""
    if sys.platform.startswith("linux"):
        return "linux"
    if sys.platform == "darwin":
        return "macos"
    raise MatrixError("matrix runner requires Linux or macOS")


def write_json(path: pathlib.Path, document: dict[str, t.Any]) -> None:
    """Write a JSON document the way the validators read it."""
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def git(*arguments: str) -> str:
    """Run git in the repository and return its trimmed output."""
    return subprocess.run(
        ["git", "-C", str(REPOSITORY), *arguments],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()


def dotnet(*arguments: str) -> None:
    """Run ``dotnet`` through mise in the repository, echoing its output."""
    subprocess.run(["mise", "exec", "--", "dotnet", *arguments], cwd=REPOSITORY, check=True)


def check_sdk() -> None:
    """Refuse an SDK other than the one ``global.json`` pins."""
    found = subprocess.run(
        ["mise", "exec", "--", "dotnet", "--version"],
        cwd=REPOSITORY,
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()
    expected = pinned_sdk_version()
    if found != expected:
        raise MatrixError(f"expected .NET SDK {expected}, got {found}")


def main(argv: list[str] | None = None) -> int:
    """Run the matrix and answer the process status."""
    options = parse_arguments(argv)
    matrix: Matrix | None = None
    try:
        platform_name()
        matrix = Matrix(options, supported_versions())
        return matrix.run()
    except (
        MatrixError,
        validate.EvidenceValidationError,
        assemble_bundle.BundleAssemblyError,
    ) as error:
        print(error, file=sys.stderr)
        return 1
    except subprocess.CalledProcessError as error:
        print(f"{error.cmd[0]} failed with status {error.returncode}", file=sys.stderr)
        return error.returncode or 1
    finally:
        if matrix is not None:
            matrix.cleanup()


if __name__ == "__main__":
    raise SystemExit(main())
