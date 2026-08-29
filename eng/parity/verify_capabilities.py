"""Validate that the C# capability model and the recorded deltas agree."""

from __future__ import annotations

import json
import pathlib
import re
import sys
import typing as t

SOURCE_ROOT = pathlib.Path(__file__).parents[2] / "src" / "LibTmux"
TESTS_ROOT = pathlib.Path(__file__).parents[2] / "tests"
REPOSITORY_ROOT = pathlib.Path(__file__).parents[2]
MODEL_PATH = SOURCE_ROOT / "Versioning" / "TmuxCapabilities.cs"
DELTAS_PATH = (
    pathlib.Path(__file__).parents[2] / "docs" / "parity" / "version-deltas.json"
)
# Every capability name carries an underscore, which the other literals in the
# profile source -- versions, messages, platform names -- do not.
CAPABILITY_LITERAL = re.compile(r'"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"')
CAPABILITY_CALL_LITERAL = re.compile(
    r"\b(?:TmuxCapabilities\.(?:GetState|IsSupported)|Supports)\("
    r'[^;]*?,\s*"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)"\s*\)',
    re.DOTALL,
)
CAPABILITY_CONST = re.compile(
    r'const\s+string\s+\w*Capability\s*=\s*\n?\s*"([^"]+)"',
)
CAPABILITY_GROUP = re.compile(
    r"private static readonly string\[\] (?P<name>\w+)\s*=\s*"
    r"\[(?P<body>.*?)\];",
    re.DOTALL,
)
VERSION_BINDING = re.compile(
    r"TmuxVersion\s+(?P<name>\w+)\s*=\s*"
    r'(?:LibTmuxInfo\.MinimumTmuxVersion|TmuxVersion\.Parse\("(?P<raw>[^"]+)"\));',
)
INTERVAL_ADD = re.compile(
    r"Add\(intervals,\s*(?P<capabilities>\w+|\[[^]]+\]),\s*"
    r"(?P<start>\w+)(?:,\s*(?P<end>\w+))?\);",
)
STABLE_VERSION = re.compile(
    r"(?P<major>0|[1-9][0-9]*)\.(?P<minor>0|[1-9][0-9]*)"
    r"(?:\.(?P<micro>0|[1-9][0-9]*)|(?P<patch>[a-z]+))?",
)
MINIMUM_VERSION = "3.2a"
# A proof is an xunit fact, which is a public method on a test class. The
# modifiers are listed rather than skipped over, so the return type cannot be
# mistaken for the name.
TEST_MEMBER = re.compile(
    r"^\s*public\s+(?:(?:static|async|override|sealed|virtual|new|partial)\s+)*"
    r"[\w<>\[\],.?]+\s+(\w+)\s*\(",
    re.MULTILINE,
)


def declared_capabilities(source: str) -> set[str]:
    """Return the capability names the interval model is built from.

    Parameters
    ----------
    source : str
        Contents of the capability model source file.

    Returns
    -------
    set[str]
        Declared capability names.

    Examples
    --------
    >>> sorted(declared_capabilities('["a_b", "c_d"];  // "3.7a"'))
    ['a_b', 'c_d']
    """
    return set(CAPABILITY_LITERAL.findall(source))


def declared_intervals(source: str) -> dict[str, tuple[str, str | None]]:
    """Return each checked-in capability interval.

    Parameters
    ----------
    source : str
        Contents of the capability model source file.

    Returns
    -------
    dict[str, tuple[str, str | None]]
        Capability name to inclusive start and exclusive end.

    Examples
    --------
    >>> sample = '''
    ... private static readonly string[] Base = ["a_b"];
    ... TmuxVersion minimum = LibTmuxInfo.MinimumTmuxVersion;
    ... TmuxVersion end = TmuxVersion.Parse("3.7");
    ... Add(intervals, Base, minimum, end);
    ... '''
    >>> declared_intervals(sample)
    {'a_b': ('3.2a', '3.7')}
    """
    groups = {
        match.group("name"): CAPABILITY_LITERAL.findall(match.group("body"))
        for match in CAPABILITY_GROUP.finditer(source)
    }
    versions = {
        match.group("name"): match.group("raw") or MINIMUM_VERSION
        for match in VERSION_BINDING.finditer(source)
    }
    intervals: dict[str, tuple[str, str | None]] = {}
    for match in INTERVAL_ADD.finditer(source):
        expression = match.group("capabilities")
        capabilities = (
            CAPABILITY_LITERAL.findall(expression)
            if expression.startswith("[")
            else groups.get(expression, [])
        )
        start = versions.get(match.group("start"))
        end_name = match.group("end")
        end = versions.get(end_name) if end_name else None
        if start is None or (end_name and end is None):
            continue
        for capability in capabilities:
            intervals[capability] = (start, end)
    return intervals


def stable_version_key(raw: str) -> tuple[int, int, int, int | str]:
    """Return the ordering key used by stable boundaries in the ledger."""
    match = STABLE_VERSION.fullmatch(raw)
    if match is None:
        raise ValueError(f"not a stable tmux boundary: {raw}")
    if match.group("micro") is not None:
        kind = 1
        suffix: int | str = int(match.group("micro"))
    elif match.group("patch") is not None:
        kind = 2
        patch = match.group("patch")
        suffix = f"{len(patch):08d}:{patch}"
    else:
        kind = 0
        suffix = 0
    return int(match.group("major")), int(match.group("minor")), kind, suffix


def recorded_intervals(
    document: dict[str, t.Any],
) -> dict[str, tuple[str, str | None]]:
    """Project recorded deltas onto the supported stable version floor."""
    minimum_key = stable_version_key(MINIMUM_VERSION)
    intervals = {}
    for row in t.cast(list[dict[str, t.Any]], document.get("capabilities", [])):
        introduced = t.cast(str, row.get("introducedIn", "unknown"))
        supported_from = (
            MINIMUM_VERSION
            if introduced == "unknown" or stable_version_key(introduced) < minimum_key
            else introduced
        )
        removed = t.cast(str, row.get("removedIn", "unknown"))
        intervals[t.cast(str, row["capability"])] = (
            supported_from,
            None if removed == "unknown" else removed,
        )
    return intervals


def referenced_capabilities(root: pathlib.Path) -> dict[str, set[str]]:
    """Return each capability a version gate names, and where it names it.

    Parameters
    ----------
    root : pathlib.Path
        Library source root.

    Returns
    -------
    dict[str, set[str]]
        Capability name to the file names that gate on it.

    Examples
    --------
    >>> "option_dollar_double_escape" in referenced_capabilities(SOURCE_ROOT)
    True
    """
    references: dict[str, set[str]] = {}
    for path in sorted(root.rglob("*.cs")):
        text = path.read_text(encoding="utf-8")
        names = CAPABILITY_CALL_LITERAL.findall(text) + CAPABILITY_CONST.findall(text)
        for name in names:
            references.setdefault(name, set()).add(path.name)

    return references


def declared_test_members(root: pathlib.Path) -> dict[str, set[str]]:
    """Return the members each test file declares, by repository path.

    Parameters
    ----------
    root : pathlib.Path
        Test source root.

    Returns
    -------
    dict[str, set[str]]
        Repository-relative file path to the members declared in it.

    Examples
    --------
    >>> members = declared_test_members(TESTS_ROOT)
    >>> "AttachmentAccounting" in members[
    ...     "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs"
    ... ]
    True
    """
    members: dict[str, set[str]] = {}
    for path in sorted(root.rglob("*.cs")):
        members[path.relative_to(REPOSITORY_ROOT).as_posix()] = set(
            TEST_MEMBER.findall(path.read_text(encoding="utf-8")),
        )

    return members


def unproven_capabilities(
    document: dict[str, t.Any],
    members: dict[str, set[str]],
) -> list[str]:
    """Return capabilities whose named real-server proof is not there.

    Every row names the test that proves its difference against a running
    tmux. That name is otherwise only held to its shape, which a renamed or
    never-written test still satisfies, so a row can read as proven while
    nothing runs. Resolving the name against the tests that exist is what
    makes the proof a claim this repository can fail.

    Parameters
    ----------
    document : dict[str, typing.Any]
        Parsed version-delta document.
    members : dict[str, set[str]]
        Repository-relative file path to the members declared in it.

    Returns
    -------
    list[str]
        Validation violations.

    Examples
    --------
    >>> rows = {
    ...     "capabilities": [
    ...         {"capability": "a_b", "namedRealServerTest": "t.cs::Gone"},
    ...     ],
    ... }
    >>> unproven_capabilities(rows, {"t.cs": {"Present"}})
    ['capability names a proof that is not there: a_b (t.cs::Gone)']
    >>> unproven_capabilities(rows, {"t.cs": {"Gone"}})
    []
    """
    violations = []
    for row in t.cast(list[dict[str, t.Any]], document.get("capabilities", [])):
        proof = str(row.get("namedRealServerTest", ""))
        path, separator, member = proof.partition("::")
        if not separator or member not in members.get(path, set()):
            violations.append(
                f"capability names a proof that is not there: "
                f"{row['capability']} ({proof})",
            )

    return sorted(violations)


def recorded_capabilities(document: dict[str, t.Any]) -> set[str]:
    """Return the capability names the version matrix records.

    Parameters
    ----------
    document : dict[str, typing.Any]
        Parsed version-delta document.

    Returns
    -------
    set[str]
        Recorded capability names.

    Examples
    --------
    >>> recorded_capabilities({"capabilities": [{"capability": "a_b"}]})
    {'a_b'}
    """
    return {
        t.cast(str, row["capability"])
        for row in t.cast(list[dict[str, t.Any]], document.get("capabilities", []))
    }


def validate(
    source: str,
    references: dict[str, set[str]],
    document: dict[str, t.Any],
) -> list[str]:
    """Return capability-model violations.

    A capability the library gates on without a recorded delta is a version
    difference nobody has to prove, and a recorded delta the library never
    declares describes a gate that cannot fire. Both are drift, so the two
    sets have to match rather than merely overlap.

    Parameters
    ----------
    source : str
        Contents of the capability model source file.
    references : dict[str, set[str]]
        Capability name to the file names that gate on it.
    document : dict[str, typing.Any]
        Parsed version-delta document.

    Returns
    -------
    list[str]
        Validation violations.

    Examples
    --------
    >>> sample = '''
    ... private static readonly string[] Base = ["a_b"];
    ... TmuxVersion minimum = LibTmuxInfo.MinimumTmuxVersion;
    ... Add(intervals, Base, minimum);
    ... '''
    >>> validate(sample, {}, {"capabilities": [{"capability": "a_b"}]})
    []
    >>> validate('"a_b"', {}, {"capabilities": []})
    ['capability is declared but not recorded: a_b']
    """
    declared = declared_capabilities(source)
    recorded = recorded_capabilities(document)
    model_intervals = declared_intervals(source)
    delta_intervals = recorded_intervals(document)
    violations = [
        f"capability is declared but not recorded: {name}"
        for name in sorted(declared - recorded)
    ]
    violations.extend(
        f"capability is recorded but not declared: {name}"
        for name in sorted(recorded - declared)
    )
    violations.extend(
        f"version gate names an unknown capability: {name} "
        f"({', '.join(sorted(references[name]))})"
        for name in sorted(set(references) - declared)
    )
    violations.extend(
        f"capability interval differs from recorded delta: {name} "
        f"(model {model_intervals.get(name)}, ledger {delta_intervals[name]})"
        for name in sorted(recorded & declared)
        if model_intervals.get(name) != delta_intervals[name]
    )
    return violations


def main() -> int:
    """Validate the checked-in capability model.

    Returns
    -------
    int
        Process exit code.

    Examples
    --------
    >>> main()
    0
    """
    document = json.loads(DELTAS_PATH.read_text(encoding="utf-8"))
    violations = validate(
        MODEL_PATH.read_text(encoding="utf-8"),
        referenced_capabilities(SOURCE_ROOT),
        document,
    )
    violations.extend(
        unproven_capabilities(document, declared_test_members(TESTS_ROOT)),
    )
    if violations:
        for violation in violations:
            print(violation, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
