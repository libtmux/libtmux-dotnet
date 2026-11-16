"""Validate a sanitized, complete tmux matrix evidence bundle."""

# Precise validation errors are intentionally raised at each failed gate.

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import socket
import sys
import typing as t

#: Read rather than repeated, so a new version is decided in one place.
TMUX_VERSION_MANIFEST = pathlib.Path(__file__).parents[2] / "eng" / "tmux" / "versions.json"


def _supported_tmux_versions() -> tuple[str, ...]:
    with TMUX_VERSION_MANIFEST.open(encoding="utf-8") as handle:
        return tuple(json.load(handle)["supported"])


REQUIRED_TMUX_VERSIONS = _supported_tmux_versions()
LEGACY_REQUIRED_TMUX_VERSIONS = REQUIRED_TMUX_VERSIONS[:-1]
OLDER_REQUIRED_TMUX_VERSIONS = REQUIRED_TMUX_VERSIONS[:-2]


def _pinned_sdk_version() -> str:
    document = json.loads((pathlib.Path(__file__).parents[2] / "global.json").read_text("utf-8"))
    return str(document["sdk"]["version"])


# Recorded evidence keeps the SDK it ran under; run_matrix.py stamps new runs
# with the SDK global.json pins.
KNOWN_SDK_VERSIONS = {"10.0.302", "10.0.303", _pinned_sdk_version()}

KNOWN_REQUIRED_TMUX_VERSION_SETS = {
    OLDER_REQUIRED_TMUX_VERSIONS,
    LEGACY_REQUIRED_TMUX_VERSIONS,
    REQUIRED_TMUX_VERSIONS,
}
REQUIRED_FRAMEWORKS = ("net10.0", "net8.0")
REDACTION_CATEGORIES = (
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
)
LEGACY_ENVIRONMENT_KEYS = {
    "evaluatedCommit",
    "frameworks",
    "includeMasterAdvisory",
    "platform",
    "redactionProof",
    "schemaVersion",
    "sdkVersion",
    "sourceState",
    "sourceTreeFingerprint",
    "tmuxVersions",
}
COMPONENT_THREE_COHORT = "0001"
CLOSURE_COHORT = "closure"
WORKSPACE_COHORT = "workspace"
MARKED_COHORT_ENVIRONMENT_KEYS = LEGACY_ENVIRONMENT_KEYS | {
    "capabilityCohort",
    "evaluatedCommitTree",
}
COMPONENT_THREE_ENVIRONMENT_KEYS = MARKED_COHORT_ENVIRONMENT_KEYS | {
    "transitionTmuxSourceCommits",
}
MATRIX_ROW_KEYS = {
    "advisory",
    "evaluatedCommit",
    "framework",
    "status",
    "testCount",
    "tmuxSourceCommit",
    "tmuxVersion",
}
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")
FINGERPRINT_PATTERN = re.compile(r"^[0-9a-f]{64}$")
EMAIL_PATTERN = re.compile(rb"\b[^\s@]+@[^\s@]+\.[^\s@]+\b")
ABSOLUTE_PATH_PATTERN = re.compile(
    rb"(?<![A-Za-z0-9:/\\])(?:"
    rb"/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*"
    rb"|[A-Za-z]:[\\/][^\s;\"']+"
    rb"|(?:(?:\\\\){1,2})"
    rb"(?!u[0-9A-Fa-f]{4}-(?:\\\\)u[0-9A-Fa-f]{4})"
    rb"[A-Za-z0-9_.-]+(?:\\){1,2}[^\s;\"']+"
    rb")"
)
TOKEN_PATTERN = re.compile(rb"(?i)\b(?:token|secret|password|ghp_)[=:][^\s\"']{8,}")
CREDENTIAL_ENVIRONMENT_KEY_PATTERN = re.compile(
    r"(?:^|_)(?:ACCESS_KEY|API_KEY|CREDENTIALS?|PASSWORD|PASSWD|PRIVATE_KEY|"
    r"SECRET|TOKEN)(?:_|$)",
    re.IGNORECASE,
)
TRANSCRIPT_PATTERN = re.compile(
    r"^event=(?:client-attachment|client-hook|client-observability|"
    r"break-pane-transition|"
    r"control-cancellation|control-following-request|control-receive|"
    r"control-send|control-tombstone|pty-attach|pty-detach|semicolon-member)"
    r"(?: [a-zA-Z][a-zA-Z0-9-]*=[^\s]+)+$"
)
BREAK_PANE_TRANSCRIPT = "break-pane-transition.txt"
BREAK_PANE_TRANSCRIPT_PATTERN = re.compile(
    r"^event=break-pane-transition "
    r"framework=(?P<framework>net10\.0|net8\.0) "
    r"tmux-source-commit=(?P<source_commit>[0-9a-f]{40}) "
    r"tmux-version=(?P<version>3\.7a?) "
    r"workaround=(?P<workaround>applied|omitted) "
    r"outcome=(?P<outcome>passed)$"
)


class EvidenceValidationError(ValueError):
    """Evidence is incomplete, inconsistent, or unsafe."""


def load_json(path: pathlib.Path) -> dict[str, t.Any]:
    """Load one JSON object.

    Parameters
    ----------
    path : pathlib.Path
        JSON path.

    Returns
    -------
    dict[str, Any]
        Decoded object.
    """
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise EvidenceValidationError(f"invalid JSON evidence: {path.name}") from exception
    if not isinstance(value, dict):
        raise EvidenceValidationError(f"JSON evidence must be an object: {path.name}")
    return t.cast("dict[str, t.Any]", value)


def load_ndjson(path: pathlib.Path) -> list[dict[str, t.Any]]:
    """Load nonempty newline-delimited JSON objects.

    Parameters
    ----------
    path : pathlib.Path
        NDJSON path.

    Returns
    -------
    list[dict[str, Any]]
        Decoded rows.
    """
    rows: list[dict[str, t.Any]] = []
    try:
        for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                raise EvidenceValidationError(f"NDJSON row {line_number} is not an object")
            rows.append(t.cast("dict[str, t.Any]", value))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise EvidenceValidationError(f"invalid NDJSON evidence: {path.name}") from exception
    if not rows:
        raise EvidenceValidationError(f"NDJSON evidence is empty: {path.name}")
    return rows


def _meaningful(value: str) -> bool:
    return (
        len(value) >= 4
        and value.casefold() not in {"true", "false", "none", "null", "linux", "unix"}
        and not value.isdigit()
    )


def sensitive_needles() -> tuple[bytes, ...]:
    """Return meaningful local values that must never enter evidence.

    Returns
    -------
    tuple[bytes, ...]
        Local identity and environment values.
    """
    values = {
        str(pathlib.Path.home()),
        pathlib.Path(os.getenv("TMPDIR", "/tmp")).as_posix(),
        os.environ.get("USER", ""),
        os.environ.get("USERNAME", ""),
        socket.gethostname(),
        os.path.realpath(sys.executable),
    }
    values.update(
        value for key, value in os.environ.items() if CREDENTIAL_ENVIRONMENT_KEY_PATTERN.search(key)
    )
    return tuple(sorted(value.encode() for value in values if _meaningful(value)))


def reject_sensitive_content(root: pathlib.Path) -> None:
    """Reject embedded paths, identities, endpoints, and credentials.

    Parameters
    ----------
    root : pathlib.Path
        Candidate evidence tree.
    """
    needles = sensitive_needles()
    for path in sorted(candidate for candidate in root.rglob("*") if candidate.is_file()):
        content = path.read_bytes()
        lowered = content.lower()
        if (
            EMAIL_PATTERN.search(content)
            or ABSOLUTE_PATH_PATTERN.search(content)
            or TOKEN_PATTERN.search(content)
            or re.search(rb"(?<![A-Za-z0-9])(?:/dev/)?(?:pts/\d+|tty[a-z]*\d+)\b", lowered)
            or re.search(rb"\blt-(?:socket-)?[a-z0-9]{8,}\b", lowered)
            or any(needle in content for needle in needles)
        ):
            relative = path.relative_to(root).as_posix()
            raise EvidenceValidationError(f"sensitive content found in {relative}")


def _validate_transcripts(root: pathlib.Path) -> None:
    transcript_root = root / "protocol-transcripts"
    required = {"control.txt", "pty.txt"}
    if not transcript_root.is_dir():
        raise EvidenceValidationError("protocol transcripts are required")
    paths = sorted(transcript_root.glob("*.txt"))
    if not required.issubset(path.name for path in paths):
        raise EvidenceValidationError("control and PTY protocol transcripts are required")
    events: set[str] = set()
    for path in paths:
        content = path.read_text(encoding="utf-8")
        if len(content.encode()) > 64 * 1024:
            raise EvidenceValidationError("protocol transcript exceeds bounded size")
        lines = content.splitlines()
        if (
            not lines
            or len(lines) > 256
            or not all(TRANSCRIPT_PATTERN.fullmatch(line) for line in lines)
        ):
            raise EvidenceValidationError("protocol transcript structure is invalid")
        events.update(line.split(" ", maxsplit=1)[0].removeprefix("event=") for line in lines)
    if not {"control-send", "control-receive", "pty-attach", "pty-detach"}.issubset(events):
        raise EvidenceValidationError("protocol transcript events are incomplete")


def _validate_environment(
    environment: dict[str, t.Any],
) -> tuple[str, bool, str | None, dict[str, str] | None, tuple[str, ...]]:
    if set(environment) not in {
        frozenset(LEGACY_ENVIRONMENT_KEYS),
        frozenset(MARKED_COHORT_ENVIRONMENT_KEYS),
        frozenset(COMPONENT_THREE_ENVIRONMENT_KEYS),
    }:
        raise EvidenceValidationError("environment schema is not exact")
    commit = environment["evaluatedCommit"]
    if not isinstance(commit, str) or not COMMIT_PATTERN.fullmatch(commit):
        raise EvidenceValidationError("environment evaluated commit is invalid")
    transition_commits = environment.get("transitionTmuxSourceCommits")
    capability_cohort = environment.get("capabilityCohort")
    evaluated_tree = environment.get("evaluatedCommitTree")
    tmux_versions = environment.get("tmuxVersions")
    required_versions = (
        tuple(tmux_versions)
        if isinstance(tmux_versions, list)
        and all(isinstance(version, str) for version in tmux_versions)
        else ()
    )
    if capability_cohort is not None and (
        not isinstance(evaluated_tree, str) or COMMIT_PATTERN.fullmatch(evaluated_tree) is None
    ):
        raise EvidenceValidationError("environment evaluated tree is invalid")
    if (
        environment["schemaVersion"] != 1
        or environment["frameworks"] != list(REQUIRED_FRAMEWORKS)
        or not isinstance(environment["includeMasterAdvisory"], bool)
        or required_versions not in KNOWN_REQUIRED_TMUX_VERSION_SETS
        or (capability_cohort == WORKSPACE_COHORT and required_versions != REQUIRED_TMUX_VERSIONS)
        or environment["platform"] not in {"linux", "macos"}
        or environment["redactionProof"] is not True
        or environment["sdkVersion"] not in KNOWN_SDK_VERSIONS
        or environment["sourceState"] not in {"clean", "uncommitted"}
        or not isinstance(environment["sourceTreeFingerprint"], str)
        or not FINGERPRINT_PATTERN.fullmatch(environment["sourceTreeFingerprint"])
        or (
            transition_commits is not None
            and (
                not isinstance(transition_commits, dict)
                or set(transition_commits) != {"3.7"}
                or not isinstance(transition_commits["3.7"], str)
                or not COMMIT_PATTERN.fullmatch(transition_commits["3.7"])
            )
        )
    ):
        raise EvidenceValidationError("environment observations are invalid")
    if capability_cohort not in {
        None,
        COMPONENT_THREE_COHORT,
        CLOSURE_COHORT,
        WORKSPACE_COHORT,
    }:
        raise EvidenceValidationError("capability cohort observations are invalid")
    if capability_cohort is not None and environment["includeMasterAdvisory"] is not False:
        raise EvidenceValidationError("capability cohort observations are invalid")
    requires_transition = capability_cohort in {
        COMPONENT_THREE_COHORT,
        WORKSPACE_COHORT,
    }
    if requires_transition != (transition_commits is not None):
        raise EvidenceValidationError("capability cohort observations are invalid")
    return (
        commit,
        environment["includeMasterAdvisory"],
        t.cast("str | None", capability_cohort),
        t.cast(
            "dict[str, str] | None",
            transition_commits,
        ),
        required_versions,
    )


def _validate_matrix_rows(
    rows: list[dict[str, t.Any]],
    commit: str,
    include_master_advisory: bool,
    capability_cohort: str | None,
    required_versions: tuple[str, ...],
) -> dict[str, str]:
    observed: dict[tuple[str, str], dict[str, t.Any]] = {}
    for row in rows:
        if set(row) != MATRIX_ROW_KEYS:
            raise EvidenceValidationError("matrix row schema is not exact")
        version = row["tmuxVersion"]
        framework = row["framework"]
        if not isinstance(version, str) or not isinstance(framework, str):
            raise EvidenceValidationError("matrix row identity is invalid")
        if version not in {*required_versions, "master"} or framework not in REQUIRED_FRAMEWORKS:
            raise EvidenceValidationError("matrix contains an unknown row")
        pair = (version, framework)
        if pair in observed:
            raise EvidenceValidationError("matrix contains a duplicate row")
        observed[pair] = row
        if row["evaluatedCommit"] != commit:
            raise EvidenceValidationError("matrix row observation is invalid")
        count = row["testCount"]
        if not isinstance(count, int) or isinstance(count, bool):
            raise EvidenceValidationError("matrix row observation is invalid")
        source_commit = row["tmuxSourceCommit"]
        if version in required_versions:
            if (
                row["advisory"] is not False
                or row["status"] != "passed"
                or count <= 0
                or not isinstance(source_commit, str)
                or not COMMIT_PATTERN.fullmatch(source_commit)
            ):
                raise EvidenceValidationError("matrix row observation is invalid")
        elif (
            row["advisory"] is not True
            or row["status"] not in {"passed", "failed"}
            or count < 0
            or (row["status"] == "passed" and count == 0)
            or (
                source_commit is not None
                and (
                    not isinstance(source_commit, str)
                    or not COMMIT_PATTERN.fullmatch(source_commit)
                )
            )
            or (source_commit is None and (count != 0 or row["status"] != "failed"))
        ):
            raise EvidenceValidationError("master matrix row observation is invalid")
    required = {
        (version, framework) for version in required_versions for framework in REQUIRED_FRAMEWORKS
    }
    if not required.issubset(observed):
        raise EvidenceValidationError("a required matrix row is missing")
    if (
        capability_cohort in {COMPONENT_THREE_COHORT, CLOSURE_COHORT, WORKSPACE_COHORT}
        and set(observed) != required
    ):
        raise EvidenceValidationError(
            "capability cohort matrix must contain exactly the required rows"
        )
    source_commits: dict[str, str] = {}
    for version in required_versions:
        commits = {
            observed[(version, framework)]["tmuxSourceCommit"] for framework in REQUIRED_FRAMEWORKS
        }
        if len(commits) != 1:
            raise EvidenceValidationError("required framework rows have different source commits")
        source_commits[version] = t.cast(str, commits.pop())
    master = [row for (version, _framework), row in observed.items() if version == "master"]
    if (include_master_advisory and len(master) != 2) or (not include_master_advisory and master):
        raise EvidenceValidationError("master matrix rows do not match the environment declaration")
    if master and len({row["tmuxSourceCommit"] for row in master}) != 1:
        raise EvidenceValidationError("master matrix rows are incomplete or inconsistent")
    return source_commits


def _validate_break_pane_transition(
    root: pathlib.Path,
    transition_commits: dict[str, str],
    matrix_commits: dict[str, str],
) -> None:
    """Require the exact source-bound 3.7 to 3.7a workaround proof."""
    path = root / "protocol-transcripts" / BREAK_PANE_TRANSCRIPT
    if not path.is_file() or path.is_symlink():
        raise EvidenceValidationError("break-pane transition transcript is required")
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError) as exception:
        raise EvidenceValidationError(
            "break-pane transition transcript cannot be read"
        ) from exception
    observed: dict[tuple[str, str], tuple[str, str, str]] = {}
    for line in lines:
        match = BREAK_PANE_TRANSCRIPT_PATTERN.fullmatch(line)
        if match is None:
            raise EvidenceValidationError("break-pane transition transcript structure is invalid")
        framework = match["framework"]
        version = match["version"]
        pair = (framework, version)
        if pair in observed:
            raise EvidenceValidationError(
                "break-pane transition transcript contains a duplicate record"
            )
        observed[pair] = (
            match["source_commit"],
            match["workaround"],
            match["outcome"],
        )
    required = {
        (framework, version) for framework in REQUIRED_FRAMEWORKS for version in ("3.7", "3.7a")
    }
    if set(observed) != required:
        raise EvidenceValidationError("break-pane transition transcript lane coverage is invalid")
    for framework in REQUIRED_FRAMEWORKS:
        transition = observed[(framework, "3.7")]
        release = observed[(framework, "3.7a")]
        if transition != (
            transition_commits["3.7"],
            "applied",
            "passed",
        ) or release != (matrix_commits["3.7a"], "omitted", "passed"):
            raise EvidenceValidationError(
                "break-pane transition transcript source or outcome is invalid"
            )


def validate_matrix(root: pathlib.Path) -> str:
    """Validate required matrix files and return the evaluated commit.

    Parameters
    ----------
    root : pathlib.Path
        Candidate evidence tree.

    Returns
    -------
    str
        Evaluated commit.
    """
    for relative in ("environment.json", "results.ndjson", "redaction-proof.json"):
        if not (root / relative).is_file():
            raise EvidenceValidationError(f"{relative} is required")
    environment = load_json(root / "environment.json")
    (
        commit,
        include_master_advisory,
        capability_cohort,
        transition_commits,
        required_versions,
    ) = _validate_environment(environment)
    proof = load_json(root / "redaction-proof.json")
    if (
        set(proof) != {"passed", "rejected"}
        or proof["passed"] is not True
        or proof["rejected"] != list(REDACTION_CATEGORIES)
    ):
        raise EvidenceValidationError("redaction proof is incomplete or invalid")
    matrix_commits = _validate_matrix_rows(
        load_ndjson(root / "results.ndjson"),
        commit,
        include_master_advisory,
        capability_cohort,
        required_versions,
    )
    _validate_transcripts(root)
    transition_path = root / "protocol-transcripts" / BREAK_PANE_TRANSCRIPT
    if transition_commits is None:
        if transition_path.exists():
            raise EvidenceValidationError(
                "break-pane transition transcript is missing source metadata"
            )
    else:
        _validate_break_pane_transition(root, transition_commits, matrix_commits)
    reject_sensitive_content(root)
    return commit


def validate_bundle(root: pathlib.Path) -> None:
    """Validate one evidence bundle.

    Parameters
    ----------
    root : pathlib.Path
        Evidence directory.
    """
    if root.is_symlink():
        raise EvidenceValidationError("evidence root symlink is not allowed")
    validate_matrix(root.resolve())


def parse_args(argv: t.Sequence[str] | None = None) -> argparse.Namespace:
    """Parse validator arguments.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    argparse.Namespace
        Parsed arguments.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=pathlib.Path)
    return parser.parse_args(argv)


def main(argv: t.Sequence[str] | None = None) -> int:
    """Validate one evidence directory.

    Parameters
    ----------
    argv : Sequence[str] | None
        Optional argument vector.

    Returns
    -------
    int
        Process status.
    """
    arguments = parse_args(argv)
    validate_bundle(arguments.root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
