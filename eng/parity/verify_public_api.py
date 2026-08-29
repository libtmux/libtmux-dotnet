"""Validate the canonical C# public API and exhaustive parity destinations."""

from __future__ import annotations

import copy
import json
import pathlib
import re
import sys
import typing as t

DOCUMENT_ROOT = pathlib.Path(__file__).parents[2] / "docs"
API_PATH = DOCUMENT_ROOT / "public-api.json"
LEDGER_PATH = DOCUMENT_ROOT / "parity" / "parity-ledger.json"
PACKAGES_PATH = pathlib.Path(__file__).parents[2] / "Directory.Packages.props"
SOURCE_ROOT = pathlib.Path(__file__).parents[2] / "src"
PACKAGE_IDS = ["LibTmux", "LibTmux.Query.Json"]
COMPONENT_IDS = set(range(1, 19))
ENTITY_IDS = {
    "T:LibTmux.Server",
    "T:LibTmux.Session",
    "T:LibTmux.Window",
    "T:LibTmux.Pane",
    "T:LibTmux.Client",
}
IDENTITY_PREFIXES = {
    "T:LibTmux.SessionId": "$",
    "T:LibTmux.WindowId": "@",
    "T:LibTmux.PaneId": "%",
}
EXCEPTION_BASES = {
    "T:LibTmux.LibTmuxException": "Exception",
    "T:LibTmux.TmuxTransportException": "LibTmuxException",
    "T:LibTmux.TmuxCleanupException": "LibTmuxException",
    "T:LibTmux.TmuxOperationCanceledException": "OperationCanceledException",
    "T:LibTmux.StaleServerGenerationException": "InvalidOperationException",
    "T:LibTmux.IncompleteSnapshotException": "InvalidOperationException",
    "T:LibTmux.UnsupportedQueryExpressionException": "NotSupportedException",
}
TMUX_COLOR_MODE_VALUES = {
    "F:LibTmux.TmuxColorMode.Default": 0,
    "F:LibTmux.TmuxColorMode.Colors256": 2,
    "F:LibTmux.TmuxColorMode.TrueColor": 3,
}
EXACT_CANCELLATION = {
    "name": "cancellationToken",
    "type": "CancellationToken",
    "default": "default",
}
MEMBER_ID_FORMAT = {
    "name": "libtmux-csharp-contract-v1",
    "xmlDocumentationIds": False,
    "prefixes": ["T:", "M:", "P:", "F:"],
    "typeNames": "short C# source-form names with nullable markers",
    "genericArity": "backticks on metadata names",
    "zeroArgumentMethods": "retain parentheses",
    "explicitInterfaceNames": "dotted C# source form",
}
TMUX_VERSION_CONTRACT: dict[str, t.Any] = {
    "grammar": [
        "version = next / release / micro / prerelease",
        'next = "next-" core',
        'release = core [patch] ["-openbsd"]',
        'micro = core "." uint',
        'prerelease = core ("-rc" posint / "-dev" ["." uint])',
        'core = uint "." uint',
        "patch = 1*LOWER",
        'uint = "0" / (NZDIGIT *DIGIT)',
        "posint = NZDIGIT *DIGIT",
    ],
    "projection": {
        "raw": "the entire canonical token exactly",
        "majorMinor": "the two invariant-culture decimal core components",
        "suffixExamples": {
            "3.7": None,
            "3.3.7": "7",
            "3.7b": "b",
            "3.7c": "c",
            "3.0-rc3": "rc3",
            "3.3a-openbsd": "a-openbsd",
            "next-3.8": "next",
        },
        "toString": "Raw",
    },
    "parsing": {
        "acceptedInput": (
            "the whole canonical token; no whitespace trimming or case folding"
        ),
        "constructorNull": "throws ArgumentNullException",
        "constructorInvalid": "throws FormatException",
        "parseNull": "throws ArgumentNullException",
        "parseInvalid": "throws FormatException",
        "tryParseFailure": (
            "returns false and assigns default for null or invalid input"
        ),
        "rejectedExamples": [
            "",
            " 3.7",
            "3.7 ",
            "tmux 3.7",
            "master",
            "03.7",
            "3.07",
            "3.7B",
            "3.7.01",
            "3.7-",
            "+3.7",
            "integer component overflow",
        ],
    },
    "ordering": {
        "core": "major then minor, numerically ascending",
        "sameCore": "next < dev < rcN < final < vendor final < numeric micro < letter patch",
        "development": "a missing dev number precedes numeric dev numbers",
        "releaseCandidate": "N compares numerically",
        "micro": "N compares numerically",
        "patch": "bijective base-26 lowercase ordinal: a=1, z=26, aa=27",
        "vendor": (
            "-openbsd immediately follows its corresponding final or patch release"
        ),
        "exactIdentity": "CompareTo returns zero if and only if equality is true",
        "examples": [
            "next-3.7 < 3.7-dev < 3.7-dev.0 < 3.7-rc1 < 3.7-rc2",
            "3.7-rc2 < 3.7 < 3.7-openbsd < 3.7a < 3.7a-openbsd < 3.7b < 3.7c",
            "3.3 < 3.3.1 < 3.3.10 < 3.3a",
            "3.7c < next-3.8 < 3.8",
        ],
        "invalidOperands": (
            "CompareTo, <, <=, >, >=, IsAtLeast, and EnsureAtLeast throw "
            "InvalidOperationException if either operand is invalid"
        ),
        "ensureAtLeastFailure": (
            "a valid value below a valid minimum throws TmuxVersionTooLowException"
        ),
    },
    "detection": {
        "command": "tmuxBinaryPath -V",
        "output": "a successful process with exactly one stdout line",
        "line": 'the exact lowercase prefix "tmux " followed by one version token',
        "lineEnding": "remove only the single trailing line terminator",
        "token": "parse without whitespace trimming or case folding",
        "detectStringAsync": "returns the exact validated canonical token",
        "detectAsync": "returns Parse of that token",
        "invalidOutput": "throws FormatException",
        "failureMapping": {
            "nonzeroExit": "TmuxCommandException carrying Result",
            "nonemptyStderr": "TmuxCommandException carrying Result",
            "missingExecutable": "TmuxCommandNotFoundException",
            "otherLaunchOrReadFailure": "TmuxTransportException",
            "preStartCallerCancellation": "OperationCanceledException",
            "postStartCallerCancellation": "TmuxOperationCanceledException",
            "cleanupFailure": "TmuxCleanupException",
            "passthrough": (
                "do not wrap TmuxCommandException, TmuxCommandNotFoundException, "
                "TmuxTransportException, OperationCanceledException, "
                "TmuxOperationCanceledException, TmuxCleanupException, or "
                "FormatException"
            ),
        },
        "advisoryMaster": (
            "master is a matrix lane label, not a token; source must report next-X.Y"
        ),
    },
    "support": {
        "minimum": "3.2a",
        "minimumInclusive": True,
        "maximumTested": "3.7c",
        "maximumTestedSemantics": "informational; not a support ceiling",
        "minimumChecks": (
            "enforce only the minimum; newer untested versions may satisfy them"
        ),
        "exactVersionIdentity": "3.7, 3.7a, 3.7b, and 3.7c are distinct",
        "capabilitySelection": (
            "named support intervals apply to every stable release at or above the "
            "minimum; capabilities without a recorded end remain supported on later "
            "stable releases"
        ),
        "unknownCapabilityVersion": (
            "invalid, below-minimum, development, release-candidate, and next versions "
            "have unknown capability state"
        ),
    },
}
TMUX_MAX_VERSION_ADAPTATION = (
    "Semantic adaptation: map Python TMUX_MAX_VERSION 3.7 to "
    "MaximumTestedTmuxVersion 3.7c, the highest required tested version"
)
C4_FRAMING_VALIDATION = (
    "row := value{projection.Fields.Count}, each value terminated by "
    "FormatProjection.RowSeparator; wire names are not sent and values are read "
    "positionally from the same projection both ends build; every field is expanded "
    "exactly once, because a byte-count prefix would expand it a second time and a "
    "field that moved in between would desynchronise the payload; the separator is "
    "randomised per process so a caller-controlled name can neither contain nor "
    "predict it, and carries no '#' for tmux to expand; tmux LF separates rows and "
    "CRLF is accepted; a complete final row may end at EOF; embedded CR and LF remain "
    "value data; an empty value maps to null after Utf8BackslashDecoder, with its key "
    "present; maxFramedFieldBytes bounds one value; a row that ends before every field "
    "is read, a value that never closes, an oversized value, and a row not terminated "
    "by a newline each throw InvalidDataException; returned memories are copied"
)
C4_OBJ_PROJECTION_BEHAVIOR: dict[str, t.Any] = {
    "objFieldCount": 178,
    "existingCatalogUnionCount": 82,
    "existingCatalogOverlapCount": 72,
    "addedFieldCount": 106,
    "combinedCatalogCount": 188,
    "scopeCounts": {
        "universal": 9,
        "session": 23,
        "window": 34,
        "pane": 70,
        "client": 25,
        "buffer": 3,
        "event": 9,
        "context": 5,
    },
    "minimumTmuxVersions": {
        "3.3": [
            "client_uid",
            "client_user",
            "pane_dead_signal",
            "pane_dead_time",
        ],
        "3.7": [
            "pane_flags",
            "pane_floating_flag",
            "pane_x",
            "pane_y",
            "pane_z",
            "pane_zoomed_flag",
            "pane_pb_progress",
            "pane_pb_state",
            "pane_pipe_pid",
            "bracket_paste_flag",
            "synchronized_output_flag",
        ],
        "default": "3.2a",
    },
}
C4_FORMAT_PROJECTION_BEHAVIOR: dict[str, t.Any] = {
    "framedFieldCount": "Fields.Count * 2",
    "emittedFieldCounts": {
        "list-sessions": {"3.2a": 123, "3.3a-3.6": 125, "3.7a+": 136},
        "list-windows": {"3.2a": 123, "3.3a-3.6": 125, "3.7a+": 136},
        "list-panes": {"3.2a": 123, "3.3a-3.6": 125, "3.7a+": 136},
        "list-clients": {"3.2a": 146, "3.3a-3.6": 150, "3.7a+": 161},
    },
}
C4_ROW_GENERATION_BEHAVIOR = {
    "requiredUniversalFields": ["pid", "start_time"],
    "rowValidation": (
        "parse pid/start_time as ServerGeneration and require equality with "
        "MaterializationContext.Server.Generation"
    ),
    "mismatch": "StaleServerGenerationException",
}
C4_QUERY_GENERATION_BEHAVIOR = {
    **C4_ROW_GENERATION_BEHAVIOR,
    "liveAcquisition": (
        "reject unmaterialized MaterializationContext.Server.Generation"
    ),
}
C4_QUERY_FAILURE_MAPPING = {
    "framing": "TmuxTransportException carrying logical tmux arguments",
    "lowLevel": "InvalidDataException",
}
TMUX_VERSION_SUFFIX_SUMMARY = (
    "Gets the exact preserved patch, prerelease, development, vendor, or next "
    "suffix projection."
)
C_SHARP_RESERVED_KEYWORDS = {
    "abstract",
    "as",
    "base",
    "bool",
    "break",
    "byte",
    "case",
    "catch",
    "char",
    "checked",
    "class",
    "const",
    "continue",
    "decimal",
    "default",
    "delegate",
    "do",
    "double",
    "else",
    "enum",
    "event",
    "explicit",
    "extern",
    "false",
    "finally",
    "fixed",
    "float",
    "for",
    "foreach",
    "goto",
    "if",
    "implicit",
    "in",
    "int",
    "interface",
    "internal",
    "is",
    "lock",
    "long",
    "namespace",
    "new",
    "null",
    "object",
    "operator",
    "out",
    "override",
    "params",
    "private",
    "protected",
    "public",
    "readonly",
    "ref",
    "return",
    "sbyte",
    "sealed",
    "short",
    "sizeof",
    "stackalloc",
    "static",
    "string",
    "struct",
    "switch",
    "this",
    "throw",
    "true",
    "try",
    "typeof",
    "uint",
    "ulong",
    "unchecked",
    "unsafe",
    "ushort",
    "using",
    "virtual",
    "void",
    "volatile",
    "while",
}


def load_document(path: pathlib.Path) -> dict[str, t.Any]:
    """Load one JSON document.

    Parameters
    ----------
    path : pathlib.Path
        JSON path.

    Returns
    -------
    dict[str, typing.Any]
        Parsed object.

    Examples
    --------
    >>> load_document(API_PATH)["schema"]
    'libtmux-public-api'
    """
    with path.open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def type_index(contract: dict[str, t.Any]) -> dict[str, dict[str, t.Any]]:
    """Index contract types by canonical member ID.

    Parameters
    ----------
    contract : dict[str, typing.Any]
        Parsed API contract.

    Returns
    -------
    dict[str, dict[str, typing.Any]]
        Type index.

    Examples
    --------
    >>> "T:LibTmux.Server" in type_index(load_document(API_PATH))
    True
    """
    return {
        t.cast(str, entry["id"]): entry
        for entry in t.cast(list[dict[str, t.Any]], contract.get("types", []))
    }


def member_index(contract: dict[str, t.Any]) -> dict[str, dict[str, t.Any]]:
    """Index contract members by canonical member ID.

    Parameters
    ----------
    contract : dict[str, typing.Any]
        Parsed API contract.

    Returns
    -------
    dict[str, dict[str, typing.Any]]
        Member index.

    Examples
    --------
    >>> "T:LibTmux.Server" in member_index(load_document(API_PATH))
    True
    """
    return {
        t.cast(str, entry["id"]): entry
        for entry in t.cast(list[dict[str, t.Any]], contract.get("members", []))
    }


def validate_header(contract: dict[str, t.Any], violations: list[str]) -> None:
    """Validate package, support, and contract-only metadata."""
    if contract.get("schema") != "libtmux-public-api" or contract.get("version") != 1:
        violations.append("invalid public API schema")
    if contract.get("memberIdFormat") != MEMBER_ID_FORMAT:
        violations.append("invalid member ID format")
    if contract.get("status") != "approved-contract-only":
        violations.append("public API claims implementation")
    packages = t.cast(list[dict[str, t.Any]], contract.get("packages", []))
    if [package.get("id") for package in packages] != PACKAGE_IDS:
        violations.append("invalid package boundary")
    elif packages[1].get("dependencies") != [{"id": "LibTmux", "version": "same"}]:
        violations.append("JSON package must depend on the same LibTmux version")
    if contract.get("supportedTargetFrameworks") != ["net8.0", "net10.0"]:
        violations.append("invalid target framework boundary")
    versions = t.cast(dict[str, t.Any], contract.get("supportedTmuxVersions", {}))
    if (
        versions.get("minimum") != "3.2a"
        or versions.get("stableSupport")
        != "every canonical stable release at or above the minimum"
    ):
        violations.append("invalid stable tmux support boundary")
    if versions.get("required") != [
        "3.2a",
        "3.3a",
        "3.4",
        "3.5",
        "3.6",
        "3.7a",
        "3.7b",
        "3.7c",
    ]:
        violations.append("invalid required tmux versions")
    if (
        versions.get("advisory") != "master"
        or versions.get("advisoryStatus") != "unknown"
    ):
        violations.append("invalid advisory tmux boundary")


def validate_types_and_members(
    contract: dict[str, t.Any], violations: list[str]
) -> tuple[dict[str, dict[str, t.Any]], dict[str, dict[str, t.Any]]]:
    """Validate canonical type and member identities."""
    type_rows = t.cast(list[dict[str, t.Any]], contract.get("types", []))
    member_rows = t.cast(list[dict[str, t.Any]], contract.get("members", []))
    type_ids = [t.cast(str, entry.get("id")) for entry in type_rows]
    member_ids = [t.cast(str, entry.get("id")) for entry in member_rows]
    if len(type_ids) != len(set(type_ids)):
        violations.append("duplicate public API type IDs")
    if len(member_ids) != len(set(member_ids)):
        violations.append("duplicate public API member IDs")
    types = type_index(contract)
    members = member_index(contract)
    if not set(types) >= ENTITY_IDS:
        violations.append("missing hierarchy entity types")
    for entry in type_rows:
        package = entry.get("package")
        if package not in PACKAGE_IDS:
            violations.append(f"invalid type package: {entry.get('id')}")
        namespace = entry.get("namespace")
        name = entry.get("name")
        canonical_source_id = f"T:{namespace}.{name}"
        if entry.get("id") != canonical_source_id and not re.fullmatch(
            rf"{re.escape(canonical_source_id)}`[1-9][0-9]*",
            str(entry.get("id")),
        ):
            violations.append(f"invalid type ID: {entry.get('id')}")
        if entry.get("kind") == "enum" and entry.get("baseType") != "Enum":
            violations.append(f"invalid enum base: {entry.get('id')}")
        if "struct" in str(entry.get("kind")) and entry.get("baseType") != "ValueType":
            violations.append(f"invalid value-type base: {entry.get('id')}")
        type_member = members.get(t.cast(str, entry.get("id")))
        if type_member is None or type_member.get("kind") != "type":
            violations.append(f"missing type member: {entry.get('id')}")
        arity_match = re.search(r"`(?P<arity>[1-9][0-9]*)$", str(entry.get("id")))
        expected_arity = int(arity_match.group("arity")) if arity_match else 0
        generic_parameters = t.cast(list[str], entry.get("genericParameters", []))
        if len(generic_parameters) != expected_arity:
            violations.append(f"invalid type generic arity: {entry.get('id')}")
        if "`" in str(entry.get("name")):
            violations.append(
                f"metadata arity leaked into type name: {entry.get('id')}"
            )
        if expected_arity and type_member is not None:
            source_arguments = f"<{', '.join(generic_parameters)}>"
            if source_arguments not in str(type_member.get("signature", "")):
                violations.append(
                    f"missing generic type declaration: {entry.get('id')}"
                )
    for member in member_rows:
        member_id = member.get("id")
        if member.get("package") not in PACKAGE_IDS:
            violations.append(f"invalid member package: {member_id}")
        if member.get("visibility") not in {"public", "internal", "explicit"}:
            violations.append(f"invalid member visibility: {member_id}")
        declaring = member.get("declaringType")
        if declaring not in types:
            violations.append(f"unknown declaring type: {member_id}")
        if member.get("kind") != "type" and not str(member_id).startswith(
            ("M:", "P:", "F:")
        ):
            violations.append(f"invalid canonical member ID: {member_id}")
        member_name = str(member.get("name", ""))
        is_qualified_method = member.get("kind") == "method" and "." in member_name
        if is_qualified_method:
            if f".{member_name}(" not in str(member_id) or "#" in str(member_id):
                violations.append(f"invalid explicit interface ID: {member_id}")
            if member.get("visibility") != "explicit":
                violations.append(f"invalid explicit interface visibility: {member_id}")
        if member.get("visibility") == "explicit":
            interface_name = member_name.rsplit(".", 1)[0]
            declaring_interfaces = t.cast(
                list[str], types.get(str(declaring), {}).get("interfaces", [])
            )
            implements_interface = interface_name in declaring_interfaces or (
                interface_name == "System.Collections.IEnumerable"
                and any(
                    interface.startswith(("IReadOnlyList<", "IEnumerable<"))
                    for interface in declaring_interfaces
                )
            )
            if (
                member.get("kind") != "method"
                or member.get("static") is True
                or not implements_interface
                or interface_name not in str(member.get("signature", ""))
            ):
                violations.append(f"invalid explicit interface member: {member_id}")
        if "`" in str(member.get("signature", "")):
            violations.append(f"metadata arity leaked into signature: {member_id}")
        for parameter in t.cast(list[dict[str, t.Any]], member.get("parameters", [])):
            parameter_name = parameter.get("name")
            if parameter_name in C_SHARP_RESERVED_KEYWORDS:
                violations.append(
                    f"reserved parameter name: {member_id}: {parameter_name}"
                )
    return types, members


def validate_public_type_reachability(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Reject public types that consumers cannot create, obtain, or call."""
    public_members = [
        member for member in members.values() if member.get("visibility") == "public"
    ]
    for type_id, type_entry in types.items():
        if "public" not in t.cast(list[str], type_entry.get("modifiers", [])):
            continue
        source_name = str(type_entry.get("name", ""))
        type_token = re.compile(
            rf"(?<![A-Za-z0-9_]){re.escape(source_name)}(?![A-Za-z0-9_])"
        )
        referenced_elsewhere = any(
            member.get("declaringType") != type_id
            and (
                type_token.search(str(member.get("returnType", ""))) is not None
                or any(
                    type_token.search(str(parameter.get("type", ""))) is not None
                    for parameter in t.cast(
                        list[dict[str, t.Any]], member.get("parameters", [])
                    )
                )
            )
            for member in public_members
        )
        public_entry_point = any(
            member.get("declaringType") == type_id
            and member.get("kind") not in {"type", "enum value"}
            and (member.get("kind") == "constructor" or member.get("static") is True)
            for member in public_members
        )
        if not referenced_elsewhere and not public_entry_point:
            violations.append(f"unreachable public type: {type_id}")


def validate_enum_and_generation_contracts(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Freeze public enum ABI values and live server-generation validation."""
    for type_id, type_entry in types.items():
        if type_entry.get("kind") != "enum" or "public" not in t.cast(
            list[str], type_entry.get("modifiers", [])
        ):
            continue
        enum_members = [
            member
            for member in members.values()
            if member.get("declaringType") == type_id
            and member.get("kind") == "enum value"
        ]
        values: list[int] = []
        for member in enum_members:
            value = member.get("value")
            if type(value) is not int:
                violations.append(f"missing enum value: {member.get('id')}")
            else:
                values.append(value)
            if member.get("static") is not True:
                violations.append(f"enum value is not static: {member.get('id')}")
        if len(values) != len(set(values)):
            violations.append(f"duplicate enum values: {type_id}")

    required_values = {
        "F:LibTmux.TmuxColorMode.Default": 0,
        "F:LibTmux.SnapshotDepth.Server": 0,
        "F:LibTmux.SnapshotDepth.Sessions": 1,
        "F:LibTmux.SnapshotDepth.Windows": 2,
        "F:LibTmux.SnapshotDepth.Panes": 3,
    }
    for member_id, expected in required_values.items():
        if members.get(member_id, {}).get("value") != expected:
            violations.append(f"invalid enum sentinel: {member_id}")

    color_mode_values = {
        member_id: t.cast(int, member["value"])
        for member_id, member in members.items()
        if member.get("declaringType") == "T:LibTmux.TmuxColorMode"
        and member.get("kind") == "enum value"
    }
    if color_mode_values != TMUX_COLOR_MODE_VALUES:
        violations.append("invalid TmuxColorMode members")

    if types.get("T:LibTmux.ServerGeneration", {}).get("validation") != (
        "ProcessId and StartTime must both be positive; default is invalid"
    ):
        violations.append("invalid ServerGeneration contract")

    expected_version_default = {
        "isValid": False,
        "raw": "",
        "major": 0,
        "minor": 0,
        "suffix": None,
        "toString": "",
        "comparison": (
            "equality is valid; ordered comparison throws InvalidOperationException"
        ),
    }
    if (
        types.get("T:LibTmux.TmuxVersion", {}).get("defaultValue")
        != expected_version_default
        or members.get("P:LibTmux.TmuxVersion.IsValid", {}).get("returnType") != "bool"
    ):
        violations.append("invalid TmuxVersion default contract")
    if (
        types.get("T:LibTmux.TmuxVersion", {}).get("versionContract")
        != TMUX_VERSION_CONTRACT
    ):
        violations.append("invalid TmuxVersion semantic contract")
    version_contract = t.cast(
        dict[str, t.Any],
        types.get("T:LibTmux.TmuxVersion", {}).get("versionContract", {}),
    )
    detection = t.cast(dict[str, t.Any], version_contract.get("detection", {}))
    if (
        detection.get("failureMapping")
        != TMUX_VERSION_CONTRACT["detection"]["failureMapping"]
    ):
        violations.append("invalid TmuxVersion detection failure contract")
    if (
        members.get("P:LibTmux.TmuxVersion.Suffix", {}).get("summary")
        != TMUX_VERSION_SUFFIX_SUMMARY
    ):
        violations.append("invalid TmuxVersion Suffix projection")

    version_operator_signatures = {
        "M:LibTmux.TmuxVersion.op_Equality(TmuxVersion,TmuxVersion)": (
            "bool operator ==(TmuxVersion left, TmuxVersion right)"
        ),
        "M:LibTmux.TmuxVersion.op_Inequality(TmuxVersion,TmuxVersion)": (
            "bool operator !=(TmuxVersion left, TmuxVersion right)"
        ),
        "M:LibTmux.TmuxVersion.op_GreaterThan(TmuxVersion,TmuxVersion)": (
            "bool operator >(TmuxVersion left, TmuxVersion right)"
        ),
        "M:LibTmux.TmuxVersion.op_GreaterThanOrEqual(TmuxVersion,TmuxVersion)": (
            "bool operator >=(TmuxVersion left, TmuxVersion right)"
        ),
        "M:LibTmux.TmuxVersion.op_LessThan(TmuxVersion,TmuxVersion)": (
            "bool operator <(TmuxVersion left, TmuxVersion right)"
        ),
        "M:LibTmux.TmuxVersion.op_LessThanOrEqual(TmuxVersion,TmuxVersion)": (
            "bool operator <=(TmuxVersion left, TmuxVersion right)"
        ),
    }
    for member_id, signature in version_operator_signatures.items():
        member = members.get(member_id, {})
        if member.get("signature") != signature:
            violations.append(f"invalid operator declaration: {member_id}")
        compiler_generated = member_id.endswith(
            (
                "op_Equality(TmuxVersion,TmuxVersion)",
                "op_Inequality(TmuxVersion,TmuxVersion)",
            )
        )
        if member.get("compilerGenerated", False) is not compiler_generated:
            violations.append(f"invalid operator authorship: {member_id}")


def validate_c4_materialization_contracts(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Freeze C4 projection, byte framing, and typed materialization seams."""
    expected_types = {
        "T:LibTmux.Internal.FormatProjection": (
            "record",
            ["internal", "sealed"],
        ),
        "T:LibTmux.Internal.MaterializationQuery": (
            "static class",
            ["internal", "static"],
        ),
    }
    expected_members = {
        "M:LibTmux.Internal.FormatProjection.Create(string,TmuxVersion)": (
            "FormatProjection",
            (("listCommand", "string", None), ("tmuxVersion", "TmuxVersion", None)),
            True,
        ),
        "P:LibTmux.Internal.FormatProjection.Fields": (
            "IReadOnlyList<FormatFieldDescriptor>",
            (),
            False,
        ),
        "P:LibTmux.Internal.FormatProjection.TmuxFormat": ("string", (), False),
        "P:LibTmux.Internal.FormatProjection.FramedFieldCount": ("int", (), False),
        "P:LibTmux.Internal.FormatCatalog.ObjProjection": (
            "IReadOnlyList<FormatFieldDescriptor>",
            (),
            True,
        ),
        (
            "M:LibTmux.Internal.SeparatedRowFramer."
            "DecodeRows(ReadOnlySpan<byte>,int,int)"
        ): (
            "IReadOnlyList<IReadOnlyDictionary<string,ReadOnlyMemory<byte>>>",
            (
                ("payload", "ReadOnlySpan<byte>", None),
                ("expectedFieldCount", "int", None),
                ("maxFramedFieldBytes", "int", None),
            ),
            True,
        ),
        (
            "M:LibTmux.Internal.MaterializationQuery.FetchAsync("
            "MaterializationContext,string,IReadOnlyList<string>?,string?,"
            "CancellationToken)"
        ): (
            "Task<IReadOnlyList<IReadOnlyDictionary<string,string?>>>",
            (
                ("context", "MaterializationContext", None),
                ("listCommand", "string", None),
                ("arguments", "IReadOnlyList<string>?", "null"),
                ("target", "string?", "null"),
                ("cancellationToken", "CancellationToken", "default"),
            ),
            True,
        ),
        (
            "M:LibTmux.Internal.MaterializationQuery.FetchOneAsync("
            "MaterializationContext,string,string,string,IReadOnlyList<string>?,"
            "CancellationToken)"
        ): (
            "Task<IReadOnlyDictionary<string,string?>>",
            (
                ("context", "MaterializationContext", None),
                ("listCommand", "string", None),
                ("targetId", "string", None),
                ("idField", "string", None),
                ("arguments", "IReadOnlyList<string>?", "null"),
                ("cancellationToken", "CancellationToken", "default"),
            ),
            True,
        ),
        (
            "M:LibTmux.Internal.Materializer.MaterializeSession("
            "MaterializationContext,IReadOnlyDictionary<string,string?>)"
        ): (
            "Session",
            (
                ("context", "MaterializationContext", None),
                ("fields", "IReadOnlyDictionary<string,string?>", None),
            ),
            True,
        ),
        (
            "M:LibTmux.Internal.Materializer.MaterializeWindow("
            "MaterializationContext,IReadOnlyDictionary<string,string?>)"
        ): (
            "Window",
            (
                ("context", "MaterializationContext", None),
                ("fields", "IReadOnlyDictionary<string,string?>", None),
            ),
            True,
        ),
        (
            "M:LibTmux.Internal.Materializer.MaterializePane("
            "MaterializationContext,IReadOnlyDictionary<string,string?>)"
        ): (
            "Pane",
            (
                ("context", "MaterializationContext", None),
                ("fields", "IReadOnlyDictionary<string,string?>", None),
            ),
            True,
        ),
        "M:LibTmux.Internal.ServerProjectionDescriptor.#ctor(string,string)": (
            "ServerProjectionDescriptor",
            (
                ("childIdAttribute", "string", None),
                ("formatterPrefix", "string", None),
            ),
            False,
        ),
        "P:LibTmux.Internal.ServerProjectionDescriptor.ChildIdAttribute": (
            "string",
            (),
            False,
        ),
        "P:LibTmux.Internal.ServerProjectionDescriptor.FormatterPrefix": (
            "string",
            (),
            False,
        ),
    }
    type_shapes = {
        type_id: (
            types.get(type_id, {}).get("kind"),
            types.get(type_id, {}).get("modifiers"),
        )
        for type_id in expected_types
    }
    member_shapes = {
        member_id: (
            members.get(member_id, {}).get("returnType"),
            tuple(
                (
                    parameter.get("name"),
                    parameter.get("type"),
                    parameter.get("default"),
                )
                for parameter in members.get(member_id, {}).get("parameters", [])
            ),
            members.get(member_id, {}).get("static"),
        )
        for member_id in expected_members
    }
    if type_shapes != expected_types or member_shapes != expected_members:
        violations.append("invalid C4 API surface contract")

    if (
        types.get("T:LibTmux.Internal.Materializer", {}).get("behavior")
        != C4_ROW_GENERATION_BEHAVIOR
        or types.get("T:LibTmux.Internal.MaterializationQuery", {}).get("behavior")
        != C4_QUERY_GENERATION_BEHAVIOR
    ):
        violations.append("invalid C4 generation contract")

    projection_type = types.get("T:LibTmux.Internal.FormatProjection", {})
    if projection_type.get("behavior") != C4_FORMAT_PROJECTION_BEHAVIOR:
        violations.append("invalid C4 format projection contract")

    obj_projection = members.get("P:LibTmux.Internal.FormatCatalog.ObjProjection", {})
    if obj_projection.get("behavior") != C4_OBJ_PROJECTION_BEHAVIOR:
        violations.append("invalid C4 Obj projection contract")

    framer_type = types.get("T:LibTmux.Internal.SeparatedRowFramer", {})
    decode_rows = members.get(
        "M:LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte>,int,int)",
        {},
    )
    if (
        framer_type.get("validation") != C4_FRAMING_VALIDATION
        or decode_rows.get("validation") != C4_FRAMING_VALIDATION
        or decode_rows.get("returnType")
        != "IReadOnlyList<IReadOnlyDictionary<string,ReadOnlyMemory<byte>>>"
    ):
        violations.append("invalid C4 framing contract")

    fetch_ids = (
        "M:LibTmux.Internal.MaterializationQuery.FetchAsync(MaterializationContext,string,IReadOnlyList<string>?,string?,CancellationToken)",
        "M:LibTmux.Internal.MaterializationQuery.FetchOneAsync(MaterializationContext,string,string,string,IReadOnlyList<string>?,CancellationToken)",
    )
    if any(
        members.get(member_id, {}).get("failureMapping") != C4_QUERY_FAILURE_MAPPING
        for member_id in fetch_ids
    ):
        violations.append("invalid C4 materialization failure mapping")


def validate_async_and_platform(
    contract: dict[str, t.Any], violations: list[str]
) -> None:
    """Validate async-only I/O, cancellation order, and platform annotations."""
    for member in t.cast(list[dict[str, t.Any]], contract.get("members", [])):
        member_id = member.get("id")
        if member.get("performsIO"):
            if member.get("kind") != "method" or not str(member.get("name")).endswith(
                "Async"
            ):
                violations.append(f"synchronous I/O member: {member_id}")
            if not str(member.get("returnType", "")).startswith(("Task", "ValueTask")):
                violations.append(f"invalid async return: {member_id}")
            parameters = member.get("parameters")
            if (
                not isinstance(parameters, list)
                or not parameters
                or parameters[-1] != EXACT_CANCELLATION
            ):
                violations.append(f"invalid cancellation parameter: {member_id}")
        annotations = member.get("platformAnnotations", [])
        psmux_facade = str(member.get("declaringType", "")).startswith(
            "T:LibTmux.Psmux"
        )
        if member.get("processBacked"):
            if psmux_facade:
                if annotations or member.get("portable") is not True:
                    violations.append(f"invalid psmux platform contract: {member_id}")
            elif annotations != ['UnsupportedOSPlatform("windows")']:
                violations.append(f"missing Windows annotation: {member_id}")
        if member.get("portable") and annotations:
            violations.append(f"portable member has platform annotation: {member_id}")


def validate_generic_declarations(
    members: dict[str, dict[str, t.Any]], violations: list[str]
) -> None:
    """Validate method generic arity and reject free type parameters."""
    type_parameter = re.compile(r"(?<![A-Za-z0-9_])T(?![A-Za-z0-9_])")
    method_arity = re.compile(r"``(?P<arity>[1-9][0-9]*)")
    for member_id, member in members.items():
        declared = t.cast(list[str], member.get("genericParameters", []))
        match = method_arity.search(member_id)
        expected_arity = int(match.group("arity")) if match else 0
        if len(declared) != expected_arity:
            violations.append(f"invalid generic arity: {member_id}")
        signature = str(member.get("signature", ""))
        if declared and f"<{','.join(declared)}>" not in signature:
            violations.append(f"missing generic declaration: {member_id}")

        uses_t = any(
            type_parameter.search(value)
            for value in (
                str(member.get("returnType", "")),
                *(
                    str(parameter.get("type", ""))
                    for parameter in member.get("parameters", [])
                ),
            )
        )
        declaring_type = str(member.get("declaringType", ""))
        if uses_t and "T" not in declared and not declaring_type.endswith("`1"):
            violations.append(f"unbound generic parameter: {member_id}")


def validate_static_classes(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Require every member declared by a static class to be static."""
    static_types = {
        type_id
        for type_id, entry in types.items()
        if entry.get("kind") == "static class"
    }
    for member_id, member in members.items():
        if (
            member.get("kind") != "type"
            and member.get("declaringType") in static_types
            and member.get("static") is not True
        ):
            violations.append(f"instance member on static class: {member_id}")


def validate_ownership(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Validate immutable listed entities and explicit owned scopes."""
    for entity_id in ENTITY_IDS:
        entity = types[entity_id]
        if not {"public", "sealed"} <= set(entity.get("modifiers", [])):
            violations.append(f"invalid entity modifiers: {entity_id}")
        if entity.get("interfaces"):
            violations.append(f"listed entity is disposable: {entity_id}")
        if entity.get("ownership") != "borrowed":
            violations.append(f"invalid entity ownership: {entity_id}")
        if any(
            member.get("declaringType") == entity_id
            and member.get("name") in {"Dispose", "DisposeAsync"}
            and member.get("visibility") == "public"
            for member in members.values()
        ):
            violations.append(
                f"listed entity exposes destructive disposal: {entity_id}"
            )
    public_owned = [
        entry
        for entry in types.values()
        if entry.get("ownership") == "owned"
        and "public" in t.cast(list[str], entry.get("modifiers", []))
    ]
    if not public_owned:
        violations.append("missing public owned scopes")
    for owned in public_owned:
        type_id = t.cast(str, owned["id"])
        if owned.get("interfaces") != ["IAsyncDisposable"]:
            violations.append(f"invalid owned scope interface: {type_id}")
        dispose = [
            member
            for member in members.values()
            if member.get("declaringType") == type_id
            and member.get("name") == "DisposeAsync"
            and member.get("visibility") == "public"
        ]
        if (
            len(dispose) != 1
            or dispose[0].get("returnType") != "ValueTask"
            or dispose[0].get("parameters") != []
        ):
            violations.append(f"invalid owned scope disposal: {type_id}")


def validate_ids_and_exceptions(
    types: dict[str, dict[str, t.Any]],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Validate typed identifier and exception contracts."""
    for type_id, prefix in IDENTITY_PREFIXES.items():
        identity = types.get(type_id, {}).get("identity")
        expected = {
            "prefix": prefix,
            "valueType": "int",
            "minimum": 0,
            "defaultIsValid": True,
            "parseRejects": ["null", "malformed", "negative", "wrongPrefix"],
            "tryParseFailure": "returns false and assigns default",
        }
        if identity != expected:
            violations.append(f"invalid typed ID contract: {type_id}")
        declaring_members = {
            member.get("name")
            for member in members.values()
            if member.get("declaringType") == type_id
        }
        if not {".ctor", "Value", "Parse", "TryParse", "ToString"} <= declaring_members:
            violations.append(f"incomplete typed ID surface: {type_id}")
    for type_id, base_type in EXCEPTION_BASES.items():
        if types.get(type_id, {}).get("baseType") != base_type:
            violations.append(f"invalid exception base: {type_id}")
    canceled_state = set(
        t.cast(
            list[str],
            types.get("T:LibTmux.TmuxOperationCanceledException", {}).get("state", []),
        )
    )
    if not {"CommandMayHaveExecuted", "ClientProcessId"} <= canceled_state:
        violations.append("incomplete cancellation exception state")
    if "CancellationToken" in canceled_state:
        violations.append("cancellation exception hides inherited CancellationToken")
    cleanup_state = set(
        t.cast(
            list[str], types.get("T:LibTmux.TmuxCleanupException", {}).get("state", [])
        )
    )
    if not {"OriginalCancellation", "ClientProcessId"} <= cleanup_state:
        violations.append("incomplete cleanup exception state")


def validate_query(contract: dict[str, t.Any], violations: list[str]) -> None:
    """Validate the closed local-query and optional JSON boundary."""
    query = t.cast(dict[str, t.Any], contract.get("query", {}))
    if query.get("entryPoint") != "Matching":
        violations.append("invalid query entry point")
    if (
        query.get("sourceType") != "IEnumerable<T>"
        or query.get("resultType") != "IReadOnlyList<T>"
    ):
        violations.append("invalid query collection boundary")
    if query.get("cardinality") != [
        "First",
        "FirstOrDefault",
        "Single",
        "SingleOrDefault",
        "Any",
        "Count",
    ]:
        violations.append("invalid query cardinality boundary")
    if query.get("edgeLookups") != ["name__contains"]:
        violations.append("invalid query edge parser")
    surface = json.dumps(
        {"types": contract.get("types", []), "members": contract.get("members", [])},
        sort_keys=True,
    )
    forbidden_tokens = (
        "IQueryable",
        "dynamic",
        "IFieldCatalogContender",
        "QueryPlannerCapabilities",
        "InternalsVisibleTo",
    )
    violations.extend(
        f"forbidden public API token: {forbidden}"
        for forbidden in forbidden_tokens
        if forbidden in surface
    )
    type_packages = {
        entry.get("id"): entry.get("package")
        for entry in t.cast(list[dict[str, t.Any]], contract.get("types", []))
    }
    if type_packages.get("T:LibTmux.Query.QueryDocument") != "LibTmux":
        violations.append("core query document is outside LibTmux")
    if type_packages.get("T:LibTmux.Query.Json.QueryJson") != "LibTmux.Query.Json":
        violations.append("JSON adapter is outside LibTmux.Query.Json")


def validate_examples_and_reachability(
    contract: dict[str, t.Any], violations: list[str]
) -> None:
    """Validate canonical examples and reject unreachable request scaffolding."""
    examples = t.cast(dict[str, object], contract.get("examples", {}))
    expected_examples = {
        "connect-and-own-session",
        "immutable-replacement",
        "capture-query-and-json",
        "real-tmux-testkit",
    }
    if set(examples) != expected_examples:
        violations.append("invalid consumer example set")
    for name, value in examples.items():
        if not isinstance(value, dict) or set(value) != {
            "title",
            "description",
            "source",
        }:
            violations.append(f"invalid consumer example shape: {name}")
            continue
        if "static async Task Main" not in str(value.get("source", "")):
            violations.append(f"consumer example is not standalone: {name}")
    query_example = examples.get("capture-query-and-json")
    query_source = (
        str(query_example.get("source", "")) if isinstance(query_example, dict) else ""
    )
    if not all(
        token in query_source
        for token in (
            "QueryExtensions.Translate<Session>",
            "QueryExtensions.Matching(sessions, document)",
            "QueryJson.Serialize(document)",
            "QueryJson.Deserialize(json)",
            "roundTripped != document",
        )
    ):
        violations.append("query example does not round-trip one canonical document")

    types = t.cast(list[dict[str, t.Any]], contract.get("types", []))
    members = t.cast(list[dict[str, t.Any]], contract.get("members", []))
    for entry in types:
        request_name = str(entry.get("name", ""))
        if not request_name.endswith("Request"):
            continue
        if not any(
            member.get("declaringType") != entry.get("id")
            and (
                request_name in str(member.get("returnType", ""))
                or any(
                    request_name in str(parameter.get("type", ""))
                    for parameter in t.cast(
                        list[dict[str, t.Any]], member.get("parameters", [])
                    )
                )
            )
            for member in members
        ):
            violations.append(f"unreachable request record: {entry.get('id')}")

    if "ApplyParityAdapter" in json.dumps(members, sort_keys=True):
        violations.append("untyped parity adapter is forbidden")


def validate_ledger(
    contract: dict[str, t.Any],
    ledger: dict[str, t.Any],
    members: dict[str, dict[str, t.Any]],
    violations: list[str],
) -> None:
    """Validate exhaustive exact parity dispositions."""
    if ledger.get("sourceRevision") != contract.get("sourceRevision"):
        violations.append("contract and ledger source revisions differ")
    rows = t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
    if {row.get("componentId") for row in rows} != COMPONENT_IDS:
        violations.append("ledger component IDs are incomplete")
    row_ids = [row.get("pythonSymbolId") for row in rows]
    if len(row_ids) != len(set(row_ids)):
        violations.append("duplicate parity ledger row IDs")
    max_version_rows = [
        row
        for row in rows
        if row.get("pythonSymbolId") == "libtmux.common:TMUX_MAX_VERSION"
    ]
    if (
        len(max_version_rows) != 1
        or max_version_rows[0].get("behavior") != TMUX_MAX_VERSION_ADAPTATION
    ):
        violations.append("invalid TMUX_MAX_VERSION semantic adaptation")
    for row in rows:
        row_id = row.get("pythonSymbolId")
        status = row.get("destinationStatus")
        destination = row.get("csharpDestination")
        if status == "approved":
            if destination not in members:
                violations.append(f"unknown approved destination: {row_id}")
            elif members[t.cast(str, destination)].get("visibility") != "public":
                violations.append(f"approved destination is internal: {row_id}")
        elif status == "internalized":
            if destination not in members:
                violations.append(f"unknown internalized destination: {row_id}")
            elif members[t.cast(str, destination)].get("visibility") != "internal":
                violations.append(f"internalized destination is public: {row_id}")
        elif status == "excluded":
            if (
                destination is not None
                or not row.get("exclusionReason")
                or not row.get("replacement")
            ):
                violations.append(f"invalid exclusion: {row_id}")
        else:
            violations.append(f"unexpected parity disposition: {row_id}")
        if row.get("implementationStatus") != "not_started":
            violations.append(f"premature implementation claim: {row_id}")
        if row.get("evidenceStatus") != "none":
            violations.append(f"premature evidence claim: {row_id}")


def approval_snapshot(ledger: dict[str, t.Any]) -> dict[str, t.Any]:
    """Return a ledger copy with progressive production claims removed.

    This validator owns the frozen approval contract, so it reads destinations
    and exclusions from a snapshot rather than the progressive statuses that
    the phase-aware plan validator owns.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Current parity ledger.

    Returns
    -------
    dict[str, typing.Any]
        Deep-copied approval snapshot.

    Examples
    --------
    >>> source = {"rows": [{"implementationStatus": "implemented"}]}
    >>> approval_snapshot(source)["rows"][0]["evidenceStatus"]
    'none'
    >>> source["rows"][0]["implementationStatus"]
    'implemented'
    """
    snapshot = copy.deepcopy(ledger)
    for row in t.cast(list[dict[str, t.Any]], snapshot.get("rows", [])):
        row["implementationStatus"] = "not_started"
        row["evidenceStatus"] = "none"
    return snapshot


def validate(contract: dict[str, t.Any], ledger: dict[str, t.Any]) -> list[str]:
    """Return public API and parity contract violations.

    Parameters
    ----------
    contract : dict[str, typing.Any]
        Canonical API document.
    ledger : dict[str, typing.Any]
        Exhaustive parity ledger.

    Returns
    -------
    list[str]
        Validation violations.

    Examples
    --------
    >>> validate(load_document(API_PATH), approval_snapshot(load_document(LEDGER_PATH)))
    []
    """
    violations: list[str] = []
    validate_header(contract, violations)
    types, members = validate_types_and_members(contract, violations)
    validate_async_and_platform(contract, violations)
    validate_generic_declarations(members, violations)
    validate_static_classes(types, members, violations)
    validate_public_type_reachability(types, members, violations)
    validate_enum_and_generation_contracts(types, members, violations)
    validate_c4_materialization_contracts(types, members, violations)
    validate_ownership(types, members, violations)
    validate_ids_and_exceptions(types, members, violations)
    validate_query(contract, violations)
    validate_examples_and_reachability(contract, violations)
    validate_ledger(contract, ledger, members, violations)
    violations.extend(validate_visibility(members))
    return violations


def shipped_surface() -> set[str]:
    """Read the declarations the Roslyn analyzer holds each assembly to.

    Returns
    -------
    set[str]
        One entry per approved declaration, without its return type.

    Examples
    --------
    >>> "LibTmux.Pane" in shipped_surface()
    True
    """
    surface: set[str] = set()
    for path in sorted(SOURCE_ROOT.glob("*/PublicAPI.*.txt")):
        for line in path.read_text(encoding="utf-8").splitlines():
            entry = line.strip()
            if entry and not entry.startswith("#"):
                surface.add(entry.split(" -> ")[0].removeprefix("static "))
    return surface


def validate_visibility(members: dict[str, t.Any]) -> list[str]:
    """Hold the contract's internal members to being absent from the assembly.

    The analyzer baselines are generated from the built assembly, so a member
    the contract calls internal and the baseline lists is public in fact. That
    disagreement is invisible to the analyzer, which never reads the contract,
    and to the rest of this file, which never reads the assembly.

    Parameters
    ----------
    members
        Contract members keyed by member id.

    Returns
    -------
    list[str]
        One violation per member the contract and the assembly disagree on.

    Examples
    --------
    >>> validate_visibility({})
    []
    """
    surface = shipped_surface()
    violations = []
    for member_id, member in sorted(members.items()):
        if member.get("visibility") != "internal":
            continue
        declaration = member_id[2:].split("(")[0].replace("`1", "<T>")
        if any(entry.startswith(declaration) for entry in surface):
            violations.append(f"contract calls {member_id} internal; the assembly ships it")
    return violations


def validate_repository() -> list[str]:
    """Validate repository policy coupled to the approved API contract.

    Returns
    -------
    list[str]
        Repository-policy violations.

    Examples
    --------
    >>> validate_repository()
    []
    """
    text = PACKAGES_PATH.read_text(encoding="utf-8")
    required = (
        'PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.6.0"',
        (
            "PackageVersion "
            'Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="5.6.0"'
        ),
    )
    return (
        ["missing central Roslyn pin"]
        if any(token not in text for token in required)
        else []
    )


def main() -> int:
    """Validate the checked-in public API contract.

    Returns
    -------
    int
        Process exit code.

    Examples
    --------
    >>> main()
    0
    """
    violations = validate(
        load_document(API_PATH),
        approval_snapshot(load_document(LEDGER_PATH)),
    )
    violations.extend(validate_repository())
    if violations:
        for violation in violations:
            print(violation, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
