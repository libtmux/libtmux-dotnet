"""Validate sanitized, complete bakeoff evidence by lifecycle phase."""

# Precise validation errors are intentionally raised at each failed gate.
# ruff: noqa: EM101, EM102, TRY003

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import socket
import stat
import subprocess
import sys
import typing as t

REQUIRED_TMUX_VERSIONS = (
    "3.2a",
    "3.3a",
    "3.4",
    "3.5",
    "3.6",
    "3.7a",
    "3.7b",
    "3.7c",
)
LEGACY_REQUIRED_TMUX_VERSIONS = REQUIRED_TMUX_VERSIONS[:-1]
KNOWN_REQUIRED_TMUX_VERSION_SETS = {
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
CRITIC_KEYS = {"schemaVersion", "evaluatedCommit", "reviews"}
CRITIC_ROW_KEYS = {"critic", "findings"}
FINDING_KEYS = {"finding", "severity", "disposition", "resolution", "evidence"}
CRITICS = {
    "framework-design-guidelines",
    "python-parity",
    "tmux-protocol",
}
DECISION_KEYS = {
    "schemaVersion",
    "decisionId",
    "evaluatedCommit",
    "decisionInputs",
    "commands",
    "hardGates",
    "winner",
    "grafts",
    "rejectedRisks",
    "remainingUnknowns",
    "capabilities",
    "evidenceFiles",
    "criticDispositions",
}
HARD_GATE_KEYS = {"name", "status", "evidence"}
DELETION_KEYS = {
    "solution",
    "absentDirectories",
    "absentGlobs",
    "trackedPrefixes",
    "projectTokens",
    "remainingSolutionProjects",
    "expectedSolutionProjectCount",
    "evaluatedCommit",
    "passed",
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
PLACEHOLDER_PATTERN = re.compile(
    r"\b(?:pending|placeholder|tbd|todo|unresolved)\b", re.IGNORECASE
)
DELETION_CLAIM_PATTERN = re.compile(
    r"\b(?:deletion[- ]complete|deleted)\b", re.IGNORECASE
)
TRANSCRIPT_PATTERN = re.compile(
    r"^event=(?:client-attachment|client-hook|client-observability|"
    r"break-pane-transition|"
    r"control-cancellation|control-following-request|control-receive|"
    r"control-send|control-tombstone|pty-attach|pty-detach|semicolon-member)"
    r"(?: [a-zA-Z][a-zA-Z0-9-]*=[^\s]+)+$"
)
TRANSPORT_SEMANTIC_TRANSCRIPTS = {
    "client-observability.txt": {
        "event=client-attachment phase=before primary=attached selected=unattached",
        "event=client-attachment phase=during primary=attached selected=attached",
        "event=client-observability phase=during control-client=visible",
        "event=client-attachment phase=after primary=attached selected=unattached",
        "event=client-hook kind=attached count=1",
        "event=client-hook kind=detached count=1",
    },
    "control-cancellation.txt": {
        (
            "event=control-cancellation phase=after-write result=typed "
            "command-may-have-executed=true"
        ),
        "event=control-tombstone phase=before-drain state=blocking",
        "event=control-following-request phase=before-drain state=blocked",
        "event=control-tombstone phase=after-drain state=drained",
        "event=control-following-request phase=after-drain state=completed",
    },
    "semicolon-middle-failure.txt": {
        "event=semicolon-member position=prefix outcome=completed side-effect=present",
        "event=semicolon-member position=middle outcome=failed side-effect=none",
        "event=semicolon-member position=suffix outcome=skipped side-effect=absent",
    },
}
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
        raise EvidenceValidationError(
            f"invalid JSON evidence: {path.name}"
        ) from exception
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
        for line_number, line in enumerate(
            path.read_text(encoding="utf-8").splitlines(), start=1
        ):
            if not line.strip():
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                raise EvidenceValidationError(
                    f"NDJSON row {line_number} is not an object"
                )
            rows.append(t.cast("dict[str, t.Any]", value))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise EvidenceValidationError(
            f"invalid NDJSON evidence: {path.name}"
        ) from exception
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
        value
        for key, value in os.environ.items()
        if CREDENTIAL_ENVIRONMENT_KEY_PATTERN.search(key)
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
    for path in sorted(
        candidate for candidate in root.rglob("*") if candidate.is_file()
    ):
        if path == root / "SHA256SUMS":
            continue
        content = path.read_bytes()
        lowered = content.lower()
        if (
            EMAIL_PATTERN.search(content)
            or ABSOLUTE_PATH_PATTERN.search(content)
            or TOKEN_PATTERN.search(content)
            or re.search(
                rb"(?<![A-Za-z0-9])(?:/dev/)?(?:pts/\d+|tty[a-z]*\d+)\b", lowered
            )
            or re.search(rb"\blt-(?:socket-)?[a-z0-9]{8,}\b", lowered)
            or any(needle in content for needle in needles)
        ):
            relative = path.relative_to(root).as_posix()
            raise EvidenceValidationError(f"sensitive content found in {relative}")


def _regular_files(root: pathlib.Path) -> list[pathlib.Path]:
    files: list[pathlib.Path] = []
    for path in root.rglob("*"):
        if path.is_symlink():
            raise EvidenceValidationError("evidence symlinks are not allowed")
        mode = path.stat(follow_symlinks=False).st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            raise EvidenceValidationError("unsupported evidence entry type")
        files.append(path)
    return sorted(files)


def verify_hashes(root: pathlib.Path) -> None:
    """Verify a complete root ``SHA256SUMS`` manifest.

    Parameters
    ----------
    root : pathlib.Path
        Durable evidence tree.
    """
    manifest_path = root / "SHA256SUMS"
    if not manifest_path.is_file() or manifest_path.is_symlink():
        raise EvidenceValidationError("SHA256SUMS is required")
    expected: dict[str, str] = {}
    for line in manifest_path.read_text(encoding="utf-8").splitlines():
        parts = line.split("  ", maxsplit=1)
        if len(parts) != 2 or not re.fullmatch(r"[0-9a-f]{64}", parts[0]):
            raise EvidenceValidationError("SHA256SUMS contains an invalid row")
        relative_path = pathlib.PurePosixPath(parts[1])
        if (
            relative_path.is_absolute()
            or ".." in relative_path.parts
            or parts[1] in expected
        ):
            raise EvidenceValidationError("SHA256SUMS contains an unsafe path")
        expected[parts[1]] = parts[0]
    actual_paths = sorted(
        path.relative_to(root).as_posix()
        for path in _regular_files(root)
        if path != manifest_path
    )
    if sorted(expected) != actual_paths:
        raise EvidenceValidationError("SHA256SUMS is incomplete")
    for relative, digest in expected.items():
        actual = hashlib.sha256((root / relative).read_bytes()).hexdigest()
        if actual != digest:
            raise EvidenceValidationError(f"SHA256SUMS mismatch for {relative}")


def _validate_transcripts(root: pathlib.Path) -> None:
    transcript_root = root / "protocol-transcripts"
    required = {"control.txt", "pty.txt"}
    if not transcript_root.is_dir():
        raise EvidenceValidationError("protocol transcripts are required")
    paths = sorted(transcript_root.glob("*.txt"))
    if not required.issubset(path.name for path in paths):
        raise EvidenceValidationError(
            "control and PTY protocol transcripts are required"
        )
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
        events.update(
            line.split(" ", maxsplit=1)[0].removeprefix("event=") for line in lines
        )
    if not {"control-send", "control-receive", "pty-attach", "pty-detach"}.issubset(
        events
    ):
        raise EvidenceValidationError("protocol transcript events are incomplete")


def _validate_decision_transcripts(root: pathlib.Path) -> None:
    environment = load_json(root / "environment.json")
    if environment.get("capabilityCohort") != COMPONENT_THREE_COHORT:
        return
    transcript_root = root / "protocol-transcripts"
    present = {path.name for path in transcript_root.glob("*.txt")}
    if not set(TRANSPORT_SEMANTIC_TRANSCRIPTS).issubset(present):
        raise EvidenceValidationError(
            "transport semantic transcript files are incomplete"
        )
    required_lanes = [
        (row["framework"], row["tmuxVersion"])
        for row in load_ndjson(root / "results.ndjson")
        if row["advisory"] is False
    ]
    for filename, events in TRANSPORT_SEMANTIC_TRANSCRIPTS.items():
        lines = (transcript_root / filename).read_text(encoding="utf-8").splitlines()
        expected = [
            f"{event} framework={framework} tmux-version={version}"
            for framework, version in required_lanes
            for event in events
        ]
        if sorted(lines) != sorted(expected):
            raise EvidenceValidationError(
                f"transport semantic transcript lane coverage is invalid: {filename}"
            )


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
        not isinstance(evaluated_tree, str)
        or COMMIT_PATTERN.fullmatch(evaluated_tree) is None
    ):
        raise EvidenceValidationError("environment evaluated tree is invalid")
    if (
        environment["schemaVersion"] != 1
        or environment["frameworks"] != list(REQUIRED_FRAMEWORKS)
        or not isinstance(environment["includeMasterAdvisory"], bool)
        or required_versions not in KNOWN_REQUIRED_TMUX_VERSION_SETS
        or environment["platform"] not in {"linux", "macos"}
        or environment["redactionProof"] is not True
        or environment["sdkVersion"] != "10.0.302"
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
    if capability_cohort not in {None, COMPONENT_THREE_COHORT, CLOSURE_COHORT}:
        raise EvidenceValidationError("capability cohort observations are invalid")
    if (
        capability_cohort is not None
        and environment["includeMasterAdvisory"] is not False
    ):
        raise EvidenceValidationError("capability cohort observations are invalid")
    if (capability_cohort == COMPONENT_THREE_COHORT and transition_commits is None) or (
        capability_cohort != COMPONENT_THREE_COHORT and transition_commits is not None
    ):
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
        if (
            version not in {*required_versions, "master"}
            or framework not in REQUIRED_FRAMEWORKS
        ):
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
        (version, framework)
        for version in required_versions
        for framework in REQUIRED_FRAMEWORKS
    }
    if not required.issubset(observed):
        raise EvidenceValidationError("a required matrix row is missing")
    if (
        capability_cohort in {COMPONENT_THREE_COHORT, CLOSURE_COHORT}
        and set(observed) != required
    ):
        raise EvidenceValidationError(
            "capability cohort matrix must contain exactly the required rows"
        )
    source_commits: dict[str, str] = {}
    for version in required_versions:
        commits = {
            observed[(version, framework)]["tmuxSourceCommit"]
            for framework in REQUIRED_FRAMEWORKS
        }
        if len(commits) != 1:
            raise EvidenceValidationError(
                "required framework rows have different source commits"
            )
        source_commits[version] = t.cast(str, commits.pop())
    master = [
        row for (version, _framework), row in observed.items() if version == "master"
    ]
    if (include_master_advisory and len(master) != 2) or (
        not include_master_advisory and master
    ):
        raise EvidenceValidationError(
            "master matrix rows do not match the environment declaration"
        )
    if master and len({row["tmuxSourceCommit"] for row in master}) != 1:
        raise EvidenceValidationError(
            "master matrix rows are incomplete or inconsistent"
        )
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
            raise EvidenceValidationError(
                "break-pane transition transcript structure is invalid"
            )
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
        (framework, version)
        for framework in REQUIRED_FRAMEWORKS
        for version in ("3.7", "3.7a")
    }
    if set(observed) != required:
        raise EvidenceValidationError(
            "break-pane transition transcript lane coverage is invalid"
        )
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


def _load_fenced_json(path: pathlib.Path, label: str) -> dict[str, t.Any]:
    try:
        content = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exception:
        raise EvidenceValidationError(f"{label} cannot be read") from exception
    blocks = re.findall(r"```json\s*\n(.*?)\n```", content, flags=re.DOTALL)
    if len(blocks) != 1:
        raise EvidenceValidationError(f"{label} must contain exactly one JSON block")
    try:
        value = json.loads(blocks[0])
    except json.JSONDecodeError as exception:
        raise EvidenceValidationError(f"{label} JSON block is invalid") from exception
    if not isinstance(value, dict):
        raise EvidenceValidationError(f"{label} JSON block must be an object")
    return t.cast("dict[str, t.Any]", value)


def _validate_critics(root: pathlib.Path, commit: str) -> None:
    path = root / "critic-reviews.md"
    if not path.is_file():
        raise EvidenceValidationError("critic-reviews.md is required")
    value = _load_fenced_json(path, "critic reviews")
    if (
        set(value) != CRITIC_KEYS
        or value["schemaVersion"] != 1
        or value["evaluatedCommit"] != commit
    ):
        raise EvidenceValidationError("critic review schema or commit is invalid")
    if PLACEHOLDER_PATTERN.search(json.dumps(value, sort_keys=True)):
        raise EvidenceValidationError(
            "critic reviews contain pending or unresolved text"
        )
    reviews = value["reviews"]
    if not isinstance(reviews, list) or len(reviews) != 3:
        raise EvidenceValidationError(
            "critic reviews must contain exactly three critics"
        )
    seen: set[str] = set()
    for row in reviews:
        if not isinstance(row, dict) or set(row) != CRITIC_ROW_KEYS:
            raise EvidenceValidationError("critic review row schema is invalid")
        critic = row["critic"]
        findings = row["findings"]
        if (
            critic not in CRITICS
            or critic in seen
            or not isinstance(findings, list)
            or not findings
        ):
            raise EvidenceValidationError("critic review rows are incomplete")
        seen.add(critic)
        for finding in findings:
            if not isinstance(finding, dict) or set(finding) != FINDING_KEYS:
                raise EvidenceValidationError("critic finding schema is invalid")
            if (
                not isinstance(finding["evidence"], str)
                or not finding["evidence"].strip()
            ):
                raise EvidenceValidationError("critic finding evidence is required")
            if finding["finding"] == "no findings":
                if (
                    finding["severity"] != "none"
                    or finding["disposition"] != "no-findings"
                    or finding["resolution"] != "not-applicable"
                ):
                    raise EvidenceValidationError(
                        "critic no-findings sentinel is invalid"
                    )
            elif (
                not isinstance(finding["finding"], str)
                or not finding["finding"].strip()
                or finding["severity"] not in {"high", "medium", "low"}
                or finding["disposition"] not in {"accepted", "rejected"}
                or finding["resolution"] not in {"resolved", "not-applicable"}
                or (
                    finding["disposition"] == "accepted"
                    and finding["resolution"] != "resolved"
                )
            ):
                raise EvidenceValidationError("critic finding disposition is invalid")
    if seen != CRITICS:
        raise EvidenceValidationError("critic review set is incomplete")


def _string_list(value: t.Any, *, nonempty: bool = True) -> bool:
    return (
        isinstance(value, list)
        and (not nonempty or bool(value))
        and all(isinstance(item, str) and item.strip() for item in value)
        and len(value) == len(set(value))
    )


def _validate_decision(
    repository: pathlib.Path, root: pathlib.Path, commit: str
) -> None:
    decision_id = root.name
    if decision_id not in {"0001", "0002", "0003"}:
        raise EvidenceValidationError("decision evidence directory name is invalid")
    paths = list(
        (repository / "csharp" / "docs" / "decisions").glob(f"{decision_id}-*.md")
    )
    if len(paths) != 1:
        raise EvidenceValidationError(
            "decision requires exactly one sibling Markdown file"
        )
    value = _load_fenced_json(paths[0], "decision inputs")
    serialized = json.dumps(value, sort_keys=True)
    if set(value) != DECISION_KEYS or value["schemaVersion"] != 1:
        raise EvidenceValidationError("decision input schema is invalid")
    if (
        value["decisionId"] != decision_id
        or value["evaluatedCommit"] != commit
        or not isinstance(value["decisionInputs"], dict)
        or not value["decisionInputs"]
        or not isinstance(value["winner"], str)
        or not value["winner"].strip()
        or value["criticDispositions"] != f"evidence/{decision_id}/critic-reviews.md"
        or PLACEHOLDER_PATTERN.search(serialized)
        or DELETION_CLAIM_PATTERN.search(serialized)
        or ABSOLUTE_PATH_PATTERN.search(serialized.encode())
    ):
        raise EvidenceValidationError("decision inputs are incomplete or unsafe")
    for key in ("commands", "grafts", "rejectedRisks", "capabilities", "evidenceFiles"):
        if not _string_list(value[key]):
            raise EvidenceValidationError(f"decision {key} list is invalid")
    if not _string_list(value["remainingUnknowns"], nonempty=False):
        raise EvidenceValidationError("decision remainingUnknowns list is invalid")
    gates = value["hardGates"]
    if not isinstance(gates, list) or not gates:
        raise EvidenceValidationError("decision hard gates are required")
    names: set[str] = set()
    for gate in gates:
        if (
            not isinstance(gate, dict)
            or set(gate) != HARD_GATE_KEYS
            or not isinstance(gate["name"], str)
            or not gate["name"].strip()
            or gate["name"] in names
            or gate["status"] not in {"passed", "failed"}
            or not isinstance(gate["evidence"], str)
            or not gate["evidence"].strip()
        ):
            raise EvidenceValidationError("decision hard gate schema is invalid")
        names.add(gate["name"])
    if any(gate["status"] != "passed" for gate in gates):
        raise EvidenceValidationError("decision winner requires all hard gates to pass")


def _run_git(repository: pathlib.Path, *arguments: str) -> str:
    try:
        return subprocess.run(
            ["git", "-C", str(repository), *arguments],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as exception:
        raise EvidenceValidationError(
            "deletion repository cannot be inspected"
        ) from exception


def _require_ancestor(repository: pathlib.Path, commit: str) -> None:
    try:
        subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "merge-base",
                "--is-ancestor",
                commit,
                "HEAD",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as exception:
        raise EvidenceValidationError(
            "deletion proof does not match repository ancestry"
        ) from exception


def _relative(value: t.Any, label: str) -> pathlib.Path:
    if not isinstance(value, str):
        raise EvidenceValidationError(f"deletion {label} must be a string")
    path = pathlib.Path(value)
    if path.is_absolute() or ".." in path.parts or path == pathlib.Path():
        raise EvidenceValidationError(f"deletion {label} must be repository-relative")
    return path


def _validate_deletion(
    repository: pathlib.Path, proof: dict[str, t.Any], commit: str
) -> None:
    if (
        set(proof) != DELETION_KEYS
        or proof["passed"] is not True
        or proof["evaluatedCommit"] != commit
    ):
        raise EvidenceValidationError("deletion proof schema or commit is invalid")
    _require_ancestor(repository, commit)
    solution = _relative(proof["solution"], "solution")
    solution_path = repository / solution
    if not solution_path.is_file():
        raise EvidenceValidationError("deletion solution is missing")
    solution_text = solution_path.read_text(encoding="utf-8")
    list_fields = (
        "absentDirectories",
        "absentGlobs",
        "trackedPrefixes",
        "projectTokens",
        "remainingSolutionProjects",
    )
    if any(not _string_list(proof[field], nonempty=False) for field in list_fields):
        raise EvidenceValidationError("deletion proof lists are invalid")
    for raw in proof["absentDirectories"]:
        if (repository / _relative(raw, "absent directory")).exists():
            raise EvidenceValidationError("deletion absent directory claim failed")
    for pattern in proof["absentGlobs"]:
        checked = _relative(pattern, "absent glob")
        if any(repository.glob(checked.as_posix())):
            raise EvidenceValidationError("deletion absent glob claim failed")
    for raw in proof["trackedPrefixes"]:
        prefix = _relative(raw, "tracked prefix")
        if _run_git(repository, "ls-files", "--cached", "--", prefix.as_posix()):
            raise EvidenceValidationError("deletion tracked prefix claim failed")
    for token in proof["projectTokens"]:
        if token in solution_text:
            raise EvidenceValidationError("deletion project token claim failed")
    remaining_snapshot = proof["remainingSolutionProjects"]
    expected_count = proof["expectedSolutionProjectCount"]
    if expected_count is not None and (
        not isinstance(expected_count, int)
        or isinstance(expected_count, bool)
        or expected_count < 0
        or len(remaining_snapshot) != expected_count
    ):
        raise EvidenceValidationError("deletion expected project count failed")


def validate_bundle(
    root: pathlib.Path,
    phase: str = "final",
    *,
    repository: pathlib.Path | None = None,
) -> None:
    """Validate matrix, pre-deletion, or final evidence.

    Parameters
    ----------
    root : pathlib.Path
        Evidence directory.
    phase : str
        Validation phase.
    repository : pathlib.Path | None
        Repository used to re-evaluate decision and deletion claims.
    """
    if phase not in {"matrix", "pre-deletion", "final"}:
        raise EvidenceValidationError(f"unknown validation phase: {phase}")
    if root.is_symlink():
        raise EvidenceValidationError("evidence root symlink is not allowed")
    root = root.resolve()
    commit = validate_matrix(root)
    if phase in {"pre-deletion", "final"}:
        if repository is None:
            repository = pathlib.Path(_run_git(root, "rev-parse", "--show-toplevel"))
        repository = repository.resolve()
        _validate_critics(root, commit)
        _validate_decision(repository, root, commit)
        _validate_decision_transcripts(root)
    if phase == "final":
        deletion = root / "deletion.json"
        if not deletion.is_file():
            raise EvidenceValidationError("deletion.json is required")
        assert repository is not None
        _validate_deletion(repository, load_json(deletion), commit)
        verify_hashes(root)


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
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--phase", choices=("matrix", "pre-deletion", "final"), default="final"
    )
    parser.add_argument("--repository", type=pathlib.Path)
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
    validate_bundle(
        arguments.root,
        phase=arguments.phase,
        repository=arguments.repository,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
