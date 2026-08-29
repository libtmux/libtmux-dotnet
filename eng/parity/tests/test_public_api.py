"""Contract tests for the approved C# public API boundary."""

# Canonical member IDs stay on one line so parity mappings remain reviewable.
# ruff: noqa: E501

from __future__ import annotations

import copy
import json
import pathlib
import runpy
import typing as t

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


def csharp_docs_root() -> pathlib.Path:
    """Return the C# documentation root.

    Examples
    --------
    >>> csharp_docs_root().name
    'docs'
    """
    return pathlib.Path(__file__).parents[3] / "docs"


def load_json(path: pathlib.Path) -> dict[str, t.Any]:
    """Load one JSON object.

    Examples
    --------
    >>> load_json(csharp_docs_root() / "parity" / "parity-ledger.json")["rows"]
    ... # doctest: +ELLIPSIS
    [...]
    """
    with path.open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def api_validator() -> t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]]:
    """Load the public API validator without importing a package."""
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_public_api.py")
    )
    return t.cast(
        t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
        namespace["validate"],
    )


def api_renderer() -> t.Callable[[dict[str, t.Any]], str]:
    """Load the deterministic Markdown renderer without importing a package.

    Examples
    --------
    >>> callable(api_renderer())
    True
    """
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "render_public_api.py")
    )
    return t.cast(t.Callable[[dict[str, t.Any]], str], namespace["render"])


def public_api_members() -> dict[str, dict[str, t.Any]]:
    """Return canonical public API members indexed by ID."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    return {member["id"]: member for member in public_api["members"]}


def test_canonical_public_api_appendix_exists() -> None:
    """Require the canonical machine-readable contract."""
    assert (csharp_docs_root() / "public-api.json").is_file()


def test_every_included_row_has_an_exact_api_destination() -> None:
    """Require one approved C# destination for every included row."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api = load_json(csharp_docs_root() / "public-api.json")
    signatures = {entry["id"] for entry in public_api["members"]}
    for row in ledger["rows"]:
        if row["destinationStatus"] in {"approved", "internalized"}:
            assert row["csharpDestination"] in signatures


def test_approval_does_not_claim_implementation() -> None:
    """Keep destination approval separate from production evidence."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    namespace = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_ledger.py")
    )
    approval_snapshot = t.cast(
        t.Callable[[dict[str, t.Any]], dict[str, t.Any]],
        namespace["approval_snapshot"],
    )
    approved_ledger = approval_snapshot(ledger)
    assert all(row["destinationStatus"] != "planned" for row in approved_ledger["rows"])
    production_rows = [
        row
        for row in approved_ledger["rows"]
        if row["destinationStatus"] in {"approved", "internalized"}
    ]
    assert all(row["implementationStatus"] == "not_started" for row in production_rows)
    assert all(row["evidenceStatus"] == "none" for row in production_rows)


def test_every_ledger_row_has_a_frozen_component() -> None:
    """Bind every parity row to one production component."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    component_ids = {row.get("componentId") for row in ledger["rows"]}
    assert component_ids == set(range(1, 19))


def test_contract_fixes_packages_types_and_unique_members() -> None:
    """Freeze the two-package boundary and one canonical member identity."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    assert public_api["schema"] == "libtmux-public-api"
    assert public_api["version"] == 1
    assert public_api["memberIdFormat"] == {
        "name": "libtmux-csharp-contract-v1",
        "xmlDocumentationIds": False,
        "prefixes": ["T:", "M:", "P:", "F:"],
        "typeNames": "short C# source-form names with nullable markers",
        "genericArity": "backticks on metadata names",
        "zeroArgumentMethods": "retain parentheses",
        "explicitInterfaceNames": "dotted C# source form",
    }
    assert [package["id"] for package in public_api["packages"]] == [
        "LibTmux",
        "LibTmux.Query.Json",
    ]
    members = public_api["members"]
    member_ids = [member["id"] for member in members]
    assert len(member_ids) == len(set(member_ids))
    assert all(
        member["package"] in {"LibTmux", "LibTmux.Query.Json"} for member in members
    )


def test_internalized_and_excluded_rows_stay_out_of_public_api() -> None:
    """Keep implementation mechanics and Python-only fixtures non-public."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api = load_json(csharp_docs_root() / "public-api.json")
    members = {member["id"]: member for member in public_api["members"]}
    for row in ledger["rows"]:
        if row["destinationStatus"] == "approved":
            assert members[row["csharpDestination"]]["visibility"] == "public"
        elif row["destinationStatus"] == "internalized":
            assert members[row["csharpDestination"]]["visibility"] == "internal"
        else:
            assert row["destinationStatus"] == "excluded"
            assert row["csharpDestination"] is None
            assert row["exclusionReason"]
            assert row["replacement"]


def test_io_is_async_and_cancellation_is_last() -> None:
    """Require explicit async I/O with one conventional cancellation shape."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    io_members = [
        member for member in public_api["members"] if member.get("performsIO")
    ]
    assert io_members
    for member in io_members:
        assert member["kind"] == "method"
        assert member["name"].endswith("Async")
        assert member["returnType"].startswith(("Task", "ValueTask"))
        cancellation = member["parameters"][-1]
        assert cancellation == {
            "name": "cancellationToken",
            "type": "CancellationToken",
            "default": "default",
        }


def test_process_api_is_windows_annotated_but_portable_api_is_not() -> None:
    """Keep tmux annotated while the cross-platform psmux facade stays portable."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    members = public_api["members"]
    for member in members:
        annotations = member.get("platformAnnotations", [])
        if member.get("processBacked"):
            if member["declaringType"].startswith("T:LibTmux.Psmux"):
                assert member["portable"] is True
                assert not annotations
            else:
                assert annotations == ['UnsupportedOSPlatform("windows")']
        if member.get("portable"):
            assert not annotations

    control = next(
        member
        for member in members
        if member["id"]
        == "M:LibTmux.Server.EnterControlModeAsync(string?,System.Threading.CancellationToken)"
    )
    assert control["performsIO"] is True
    assert control["processBacked"] is True
    assert control["portable"] is False
    assert control["platformAnnotations"] == ['UnsupportedOSPlatform("windows")']


def test_entities_are_immutable_handles_not_destructive_disposables() -> None:
    """Reserve asynchronous disposal for explicitly owned resources."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    for type_id in (
        "T:LibTmux.Server",
        "T:LibTmux.Session",
        "T:LibTmux.Window",
        "T:LibTmux.Pane",
        "T:LibTmux.Client",
    ):
        entity = types[type_id]
        assert {"public", "sealed"} <= set(entity["modifiers"])
        assert entity["interfaces"] == []
        assert entity["ownership"] == "borrowed"
    owned = [
        entry
        for entry in types.values()
        if entry["ownership"] == "owned" and "public" in entry["modifiers"]
    ]
    assert owned
    assert all(entry["interfaces"] == ["IAsyncDisposable"] for entry in owned)


def test_typed_ids_have_conventional_parse_contracts() -> None:
    """Freeze nonnegative, prefix-preserving ID parsing including TryParse."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    expected_prefixes = {
        "T:LibTmux.SessionId": "$",
        "T:LibTmux.WindowId": "@",
        "T:LibTmux.PaneId": "%",
    }
    for type_id, prefix in expected_prefixes.items():
        contract = types[type_id]
        assert contract["kind"] == "record struct"
        assert {"public", "readonly"} <= set(contract["modifiers"])
        assert contract["identity"] == {
            "prefix": prefix,
            "valueType": "int",
            "minimum": 0,
            "defaultIsValid": True,
            "parseRejects": ["null", "malformed", "negative", "wrongPrefix"],
            "tryParseFailure": "returns false and assigns default",
        }
        try_parse = next(
            member
            for member in public_api["members"]
            if member["declaringType"] == type_id and member["name"] == "TryParse"
        )
        assert try_parse["parameters"][1]["modifier"] == "out"


def test_exception_hierarchy_is_intentional() -> None:
    """Keep remote, cancellation, state, and translation failures distinct."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    expected_bases = {
        "T:LibTmux.LibTmuxException": "Exception",
        "T:LibTmux.TmuxTransportException": "LibTmuxException",
        "T:LibTmux.TmuxCleanupException": "LibTmuxException",
        "T:LibTmux.TmuxOperationCanceledException": "OperationCanceledException",
        "T:LibTmux.StaleServerGenerationException": "InvalidOperationException",
        "T:LibTmux.IncompleteSnapshotException": "InvalidOperationException",
        "T:LibTmux.UnsupportedQueryExpressionException": "NotSupportedException",
    }
    for type_id, base_type in expected_bases.items():
        assert types[type_id]["baseType"] == base_type
    canceled_state = set(types["T:LibTmux.TmuxOperationCanceledException"]["state"])
    assert {
        "CommandMayHaveExecuted",
        "ClientProcessId",
    } <= canceled_state
    assert "CancellationToken" not in canceled_state
    cleanup_state = set(types["T:LibTmux.TmuxCleanupException"]["state"])
    assert {"OriginalCancellation", "ClientProcessId"} <= cleanup_state


def test_query_surface_is_closed_local_and_json_shared() -> None:
    """Approve expression matching without publishing planner machinery."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    serialized = json.dumps(
        {"types": public_api["types"], "members": public_api["members"]},
        sort_keys=True,
    )
    assert "IQueryable" not in serialized
    assert "dynamic" not in serialized
    assert "QueryPlannerCapabilities" not in serialized
    assert "IFieldCatalogContender" not in serialized
    query = public_api["query"]
    assert query["entryPoint"] == "Matching"
    assert query["sourceType"] == "IEnumerable<T>"
    assert query["resultType"] == "IReadOnlyList<T>"
    assert query["cardinality"] == [
        "First",
        "FirstOrDefault",
        "Single",
        "SingleOrDefault",
        "Any",
        "Count",
    ]
    assert query["edgeLookups"] == ["name__contains"]
    assert query["jsonPackageConsumesCoreAst"] is True
    members = public_api_members()
    assert (
        members["P:LibTmux.Query.QueryDocument.RequiredSnapshotDepth"]["returnType"]
        == "SnapshotDepth"
    )


def test_source_generated_json_context_stays_internal() -> None:
    """Keep compiler-emitted serializer members behind the stable JSON facade."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    member_ids = {member["id"] for member in public_api["members"]}
    type_ids = {type_entry["id"] for type_entry in public_api["types"]}

    assert "T:LibTmux.Query.Json.LibTmuxQueryJsonContext" not in type_ids
    assert not any("LibTmuxQueryJsonContext" in member_id for member_id in member_ids)
    assert "M:LibTmux.Query.Json.QueryJson.Serialize(QueryDocument)" in member_ids
    assert (
        "M:LibTmux.Query.Json.QueryJson.Deserialize(string,QueryJsonLimits?)"
        in member_ids
    )


def test_every_static_class_member_is_static() -> None:
    """Require compilable declarations on public and internal static classes."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    static_types = {
        type_entry["id"]
        for type_entry in public_api["types"]
        if type_entry["kind"] == "static class"
    }
    declared_members = [
        member
        for member in public_api["members"]
        if member["kind"] != "type" and member["declaringType"] in static_types
    ]

    assert declared_members
    assert all(member["static"] is True for member in declared_members)


def test_entity_equality_is_generation_bound_and_relation_aware() -> None:
    """Freeze identity separately from mutable snapshot and relation-path state."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["name"]: entry for entry in public_api["types"]}
    assert types["Server"]["equality"] == "normalized connection endpoint"
    assert types["Session"]["equality"] == "ServerGeneration and SessionId"
    assert types["Window"]["equality"] == (
        "ServerGeneration and WindowId; relation edge excluded"
    )
    assert types["Pane"]["equality"] == "ServerGeneration and PaneId"
    assert types["Client"]["equality"] == ("ServerGeneration and Name; Tty excluded")


def test_list_failure_policy_is_frozen_per_accessor() -> None:
    """Distinguish lenient inventories, missing-daemon suppression, and loud search."""
    members = public_api_members()
    expected = {
        "M:LibTmux.Server.GetSessionsAsync(CancellationToken)": "empty-on-any-list-command-failure",
        "M:LibTmux.Server.GetAttachedSessionsAsync(CancellationToken)": "empty-on-any-list-command-failure",
        "M:LibTmux.Server.GetClientsAsync(CancellationToken)": "empty-on-any-list-command-failure",
        "M:LibTmux.Server.GetWindowsAsync(CancellationToken)": "empty-on-missing-daemon-or-socket",
        "M:LibTmux.Server.GetPanesAsync(CancellationToken)": "empty-on-missing-daemon-or-socket",
        "M:LibTmux.Session.GetWindowsAsync(CancellationToken)": "loud",
        "M:LibTmux.Window.GetLinkedSessionsAsync(CancellationToken)": "empty-if-either-required-list-fails",
    }
    assert {
        member_id: members[member_id]["listErrorPolicy"] for member_id in expected
    } == expected


def test_generic_helpers_declare_type_parameters_and_extension_receivers() -> None:
    """Freeze valid generic declarations for query and wait helpers."""
    members = public_api_members()
    compile_member = members[
        "M:LibTmux.Query.QueryExtensions.Compile``1(QueryDocument)"
    ]
    matching = members[
        "M:LibTmux.Query.QueryExtensions.Matching``1("
        "IEnumerable<T>,Expression<Func<T,bool>>)"
    ]
    wait = next(
        member
        for member in members.values()
        if member["id"].startswith("M:LibTmux.Testing.TmuxWait.UntilAsync``1(")
    )

    assert compile_member["genericParameters"] == ["T"]
    assert compile_member["parameters"][0]["modifier"] == "this"
    assert matching["genericParameters"] == ["T"]
    assert matching["parameters"][0]["modifier"] == "this"
    assert wait["genericParameters"] == ["T"]


def test_connection_options_freeze_all_connection_seams_and_defaults() -> None:
    """Keep endpoint precedence, initialization, environment, and logging explicit."""
    members = public_api_members()
    constructor = next(
        member
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.ServerConnectionOptions"
        and member["kind"] == "constructor"
    )
    assert constructor["parameters"] == [
        {"name": "tmuxBinaryPath", "type": "string", "default": '"tmux"'},
        {"name": "socketName", "type": "string?", "default": "null"},
        {"name": "socketPath", "type": "string?", "default": "null"},
        {
            "name": "socketNameFactory",
            "type": "Func<string>?",
            "default": "null",
        },
        {
            "name": "configurationFile",
            "type": "string?",
            "default": "null",
        },
        {
            "name": "colorMode",
            "type": "TmuxColorMode",
            "default": "TmuxColorMode.Default",
        },
        {
            "name": "initializeAsync",
            "type": "Func<Server,CancellationToken,ValueTask>?",
            "default": "null",
        },
        {
            "name": "childEnvironment",
            "type": "IReadOnlyDictionary<string,string?>?",
            "default": "null",
        },
        {"name": "logger", "type": "ILogger?", "default": "null"},
    ]
    public_api = load_json(csharp_docs_root() / "public-api.json")
    connection_type = next(
        entry
        for entry in public_api["types"]
        if entry["id"] == "T:LibTmux.ServerConnectionOptions"
    )
    assert connection_type["endpointPrecedence"] == [
        "SocketPath",
        "SocketName",
        "SocketNameFactory",
    ]
    json_package = next(
        package
        for package in public_api["packages"]
        if package["id"] == "LibTmux.Query.Json"
    )
    assert json_package["dependencies"] == [{"id": "LibTmux", "version": "same"}]


def test_capture_and_send_requests_cover_the_approved_tmux_flags() -> None:
    """Freeze complete request records instead of a truncated convenience subset."""
    members = public_api_members()
    capture_properties = {
        member["name"]
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.CapturePaneRequest"
        and member["kind"] == "property"
    }
    assert capture_properties == {
        "StartLine",
        "EndLine",
        "EscapeSequences",
        "EscapeNonPrintable",
        "JoinWrappedLines",
        "PreserveTrailingSpaces",
        "TrimTrailingSpaces",
        "AlternateScreen",
        "Quiet",
        "ModeScreen",
        "Pending",
        "Hyperlinks",
        "LineNumbers",
        "LineFlags",
    }
    send_properties = {
        member["name"]
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.SendKeysRequest"
        and member["kind"] == "property"
    }
    assert send_properties == {
        "Text",
        "Enter",
        "SuppressHistory",
        "Literal",
        "Reset",
        "CopyModeCommand",
        "Repeat",
        "ExpandFormats",
        "HexKeys",
        "TargetClient",
        "KeyName",
    }
    assert "T:LibTmux.CapturePanePosition" in members
    assert any(
        member["declaringType"] == "T:LibTmux.Pane"
        and member["name"] == "CaptureToBufferAsync"
        for member in members.values()
    )


def test_optional_request_methods_have_no_null_ambiguous_overloads() -> None:
    """Keep default calls unambiguous at each creation and capture boundary."""
    members = public_api_members().values()
    expected = {
        ("T:LibTmux.Server", "CreateSessionAsync"): "NewSessionRequest?",
        ("T:LibTmux.Server", "CreateOwnedSessionAsync"): "NewSessionRequest?",
        ("T:LibTmux.Session", "CreateWindowAsync"): "NewWindowRequest?",
        ("T:LibTmux.Session", "CreateOwnedWindowAsync"): "NewWindowRequest?",
        ("T:LibTmux.Pane", "CaptureAsync"): "CapturePaneRequest?",
        ("T:LibTmux.Window", "SplitPaneAsync"): "SplitPaneRequest?",
    }
    for (declaring_type, name), request_type in expected.items():
        overloads = [
            member
            for member in members
            if member["declaringType"] == declaring_type and member["name"] == name
        ]
        assert len(overloads) == 1
        assert overloads[0]["parameters"][0] == {
            "name": "request",
            "type": request_type,
            "default": "null",
        }


def test_constructor_signatures_render_parameter_defaults() -> None:
    """Keep the canonical signature text consistent with parameter metadata."""
    members = public_api_members()
    constructors = [
        member for member in members.values() if member["kind"] == "constructor"
    ]
    for constructor in constructors:
        for parameter in constructor["parameters"]:
            if "default" in parameter:
                expected = f"{parameter['name']} = {parameter['default']}"
                assert expected in constructor["signature"]


def test_cancellation_and_cleanup_exceptions_expose_only_owned_state() -> None:
    """Use inherited cancellation state and nonnullable post-start process identity."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    assert types["T:LibTmux.TmuxOperationCanceledException"]["state"] == [
        "CommandMayHaveExecuted",
        "ClientProcessId",
    ]
    assert types["T:LibTmux.TmuxCleanupException"]["state"] == [
        "OriginalCancellation",
        "ClientProcessId",
        "CleanupFailure",
    ]
    members = public_api_members()
    assert "P:LibTmux.TmuxOperationCanceledException.CancellationToken" not in members
    assert (
        members["P:LibTmux.TmuxCleanupException.ClientProcessId"]["returnType"] == "int"
    )


def test_captured_relation_freezes_nullability_and_list_behavior() -> None:
    """Expose safe probing plus conventional read-only-list members."""
    members = public_api_members()
    relation_members = {
        member["name"]: member
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.CapturedRelation`1"
    }
    assert {
        "Count",
        "Item",
        "GetEnumerator",
        "System.Collections.IEnumerable.GetEnumerator",
    } <= set(relation_members)
    # The relation is the list, so a caller reads it directly and asks
    # separately whether anything was captured at all.
    assert relation_members["IsCaptured"]["returnType"] == "bool"
    assert relation_members["OrEmpty"]["returnType"] == "IReadOnlyList<T>"
    assert relation_members["OrEmpty"]["parameters"] == []
    explicit_enumerator = relation_members[
        "System.Collections.IEnumerable.GetEnumerator"
    ]
    assert explicit_enumerator["id"] == (
        "M:LibTmux.CapturedRelation`1.System.Collections.IEnumerable.GetEnumerator()"
    )
    assert explicit_enumerator["visibility"] == "explicit"
    assert explicit_enumerator["signature"] == (
        "IEnumerator System.Collections.IEnumerable.GetEnumerator()"
    )


def test_unmaterialized_server_state_is_explicit_and_reachable() -> None:
    """Allow dead endpoint probing without fabricating generation or version."""
    members = public_api_members()
    from_environment = members[
        "M:LibTmux.Server.FromEnvironment(IReadOnlyDictionary<string,string>?)"
    ]
    assert from_environment["returnType"] == "Server"
    assert from_environment["portable"] is True
    assert from_environment.get("performsIO") is False
    assert from_environment["parameters"] == [
        {
            "name": "environment",
            "type": "IReadOnlyDictionary<string,string>?",
            "default": "null",
        }
    ]
    assert members["P:LibTmux.Server.Generation"]["returnType"] == ("ServerGeneration?")
    assert members["P:LibTmux.Server.Version"]["returnType"] == "TmuxVersion?"
    assert members["P:LibTmux.Server.IsMaterialized"]["returnType"] == "bool"
    assert "M:LibTmux.Server.Open(ServerConnectionOptions?)" in members
    assert "M:LibTmux.Server.ConnectAsync(CancellationToken)" in members
    assert (
        members["M:LibTmux.Server.StartServerAsync(CancellationToken)"]["returnType"]
        == "Task"
    )


def test_snapshot_entities_retain_raw_format_tokens() -> None:
    """Keep lossless tmux format values without publishing a second neo model."""
    members = public_api_members()
    for entity in ("Session", "Window", "Pane", "Client"):
        raw_fields = members[f"P:LibTmux.{entity}.RawFormatFields"]
        assert raw_fields["returnType"] == "IReadOnlyDictionary<string,string?>"
        assert raw_fields["summary"] == (
            "Gets copied raw tmux format tokens captured for this snapshot."
        )


def test_command_targets_and_sizes_preserve_tmux_grammar() -> None:
    """Do not narrow names, patterns, special indexes, or percentage sizes."""
    members = public_api_members()
    assert (
        members["M:LibTmux.Server.KillSessionAsync(string,CancellationToken)"][
            "parameters"
        ][0]["type"]
        == "string"
    )
    assert (
        members["M:LibTmux.Server.SwitchClientAsync(string,CancellationToken)"][
            "parameters"
        ][0]["type"]
        == "string"
    )
    assert members["P:LibTmux.LinkWindowRequest.TargetSession"]["returnType"] == (
        "string"
    )
    assert members["P:LibTmux.LinkWindowRequest.TargetIndex"]["returnType"] == (
        "string?"
    )
    assert members["P:LibTmux.RunShellRequest.TargetPane"]["returnType"] == "string?"
    assert members["P:LibTmux.DisplayMenuRequest.TargetPane"]["returnType"] == (
        "string?"
    )
    assert members["P:LibTmux.MovePaneRequest.Size"]["returnType"] == "string?"
    move_constructor = next(
        member
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.MovePaneRequest"
        and member["kind"] == "constructor"
    )
    assert move_constructor["parameters"] == [
        {"name": "target", "type": "string"},
        {
            "name": "direction",
            "type": "PaneDirection",
            "default": "PaneDirection.Below",
        },
        {"name": "size", "type": "string?", "default": "null"},
        {"name": "detach", "type": "bool", "default": "true"},
        {"name": "fullWindow", "type": "bool", "default": "false"},
        {"name": "before", "type": "bool", "default": "false"},
    ]


def test_rotation_and_last_pane_use_nonconflicting_flag_domains() -> None:
    """Represent optional paired flags with closed nullable enums."""
    members = public_api_members()
    rotate = members[
        "M:LibTmux.Window.RotateAsync(WindowRotationDirection?,bool,CancellationToken)"
    ]
    assert rotate["parameters"][0] == {
        "name": "direction",
        "type": "WindowRotationDirection?",
        "default": "null",
    }
    last_pane = members[
        "M:LibTmux.Window.SelectLastPaneAsync(PaneInputMode?,bool,CancellationToken)"
    ]
    assert last_pane["parameters"][0] == {
        "name": "inputMode",
        "type": "PaneInputMode?",
        "default": "null",
    }


def test_point_client_lookup_throws_and_sparse_options_stay_plural() -> None:
    """Distinguish point lookup from TryGet and retain every sparse option row."""
    members = public_api_members()
    client = members["M:LibTmux.Client.GetAsync(Server,string,CancellationToken)"]
    assert client["returnType"] == "Task<Client>"
    assert client["missingBehavior"] == "throws TmuxObjectNotFoundException"
    options = members[
        "M:LibTmux.TmuxOptions.GetAsync(GetOptionRequest,CancellationToken)"
    ]
    assert options["returnType"] == "Task<IReadOnlyList<TmuxOption>>"
    assert options["optionCardinality"] == "empty, scalar-one, or sparse-many"


def test_testkit_preserves_parent_scope_timeout_and_name_absence() -> None:
    """Keep caller-owned parents, nonthrowing waits, and collision checks explicit."""
    members = public_api_members()
    required = {
        "M:LibTmux.Testing.TmuxTestFactory.CreateSessionAsync(Server,TmuxTestOptions?,CancellationToken)",
        "M:LibTmux.Testing.TmuxTestFactory.CreateWindowAsync(Session,TmuxTestOptions?,CancellationToken)",
        "M:LibTmux.Testing.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>>,TimeSpan,TimeSpan,bool,CancellationToken)",
        "M:LibTmux.Testing.TmuxNameGenerator.CreateAvailableSessionNameAsync(Server,string?,CancellationToken)",
        "M:LibTmux.Testing.TmuxNameGenerator.CreateAvailableWindowNameAsync(Session,string?,CancellationToken)",
    }
    assert required <= set(members)
    wait = members[
        "M:LibTmux.Testing.TmuxWait.UntilAsync(Func<CancellationToken,Task<bool>>,TimeSpan,TimeSpan,bool,CancellationToken)"
    ]
    assert wait["returnType"] == "Task<bool>"
    assert wait["parameters"][-2] == {
        "name": "throwOnTimeout",
        "type": "bool",
        "default": "true",
    }


def test_unreferenced_query_value_kind_is_not_public() -> None:
    """Keep generated field-catalog value-kind machinery internal."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    assert "T:LibTmux.Query.QueryValueKind" not in {
        type_entry["id"] for type_entry in public_api["types"]
    }


def test_generic_type_declarations_use_csharp_source_names() -> None:
    """Keep metadata arity in IDs while rendering valid generic C# source."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    relation = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.CapturedRelation`1"
    )
    assert relation["name"] == "CapturedRelation"
    assert relation["genericParameters"] == ["T"]
    relation_members = [
        member
        for member in public_api["members"]
        if member["declaringType"] == relation["id"]
    ]
    assert relation_members
    assert all(
        "CapturedRelation`1" not in member["signature"] for member in relation_members
    )
    assert "CapturedRelation<T>" in next(
        member["signature"] for member in relation_members if member["kind"] == "type"
    )


def test_parameter_names_are_valid_unescaped_csharp_identifiers() -> None:
    """Reject reserved C# keywords from the consumer-facing parameter surface."""
    reserved = {
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
    public_api = load_json(csharp_docs_root() / "public-api.json")
    assert (
        not {
            parameter["name"]
            for member in public_api["members"]
            for parameter in member.get("parameters", [])
        }
        & reserved
    )


def test_enum_values_and_server_generation_validation_are_frozen() -> None:
    """Freeze public enum ABI values and positive live-generation identity."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    enum_types = {
        type_entry["id"]
        for type_entry in public_api["types"]
        if type_entry["kind"] == "enum" and "public" in type_entry["modifiers"]
    }
    for enum_type in enum_types:
        enum_members = [
            member
            for member in public_api["members"]
            if member["declaringType"] == enum_type and member["kind"] == "enum value"
        ]
        values = [member["value"] for member in enum_members]
        assert values
        assert all(type(value) is int for value in values)
        assert len(values) == len(set(values))
        assert all(member["static"] is True for member in enum_members)

    members = public_api_members()
    color_modes = {
        member_id: member["value"]
        for member_id, member in members.items()
        if member["declaringType"] == "T:LibTmux.TmuxColorMode"
        and member["kind"] == "enum value"
    }
    assert color_modes == {
        "F:LibTmux.TmuxColorMode.Default": 0,
        "F:LibTmux.TmuxColorMode.Colors256": 2,
        "F:LibTmux.TmuxColorMode.TrueColor": 3,
    }
    color_mode_type = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.TmuxColorMode"
    )
    assert color_mode_type["summary"] == (
        "Defines valid tmux color modes. Numeric value 1 is reserved; "
        "ServerConnectionOptions rejects undefined values with "
        "ArgumentOutOfRangeException."
    )
    assert members["F:LibTmux.SnapshotDepth.Server"]["value"] == 0
    assert members["F:LibTmux.SnapshotDepth.Sessions"]["value"] == 1
    assert members["F:LibTmux.SnapshotDepth.Windows"]["value"] == 2
    assert members["F:LibTmux.SnapshotDepth.Panes"]["value"] == 3
    generation = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.ServerGeneration"
    )
    assert generation["validation"] == (
        "ProcessId and StartTime must both be positive; default is invalid"
    )


def test_validator_rejects_unsupported_88_color_mode() -> None:
    """Reject the tmux mode that has no mapping in the supported span."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")

    assert "invalid TmuxColorMode members" not in api_validator()(public_api, ledger)

    rejected_contract = copy.deepcopy(public_api)
    rejected_contract["members"].append(
        {
            "id": "F:LibTmux.TmuxColorMode.Colors88",
            "declaringType": "T:LibTmux.TmuxColorMode",
            "name": "Colors88",
            "kind": "enum value",
            "visibility": "public",
            "package": "LibTmux",
            "signature": "TmuxColorMode.Colors88",
            "portable": True,
            "summary": "Requests 88-color mode.",
            "value": 1,
            "static": True,
        }
    )

    assert "invalid TmuxColorMode members" in api_validator()(rejected_contract, ledger)


def test_value_type_bases_match_the_clr_contract() -> None:
    """Distinguish direct enum and value-type bases from object ancestry."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    for type_entry in public_api["types"]:
        if type_entry["kind"] == "enum":
            assert type_entry["baseType"] == "Enum"
        elif "struct" in type_entry["kind"]:
            assert type_entry["baseType"] == "ValueType"


def test_rendered_api_exposes_declaration_and_invariant_details() -> None:
    """Make the human-review appendix disclose the canonical contract shape."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    markdown = api_renderer()(public_api)

    assert "not compiler XML documentation IDs" in markdown
    assert (
        "| Type | Kind | Modifiers | Interfaces | Base | Ownership | Contract | Package |"
        in markdown
    )
    assert "`T:LibTmux.CapturedRelation`1`" in markdown
    assert "`public, sealed`" in markdown
    assert "`IReadOnlyList<T>`" in markdown
    assert "Equality: normalized connection endpoint." in markdown
    assert (
        "Validation: ProcessId and StartTime must both be positive; default is invalid."
        in markdown
    )
    assert 'Default value: {"comparison":' in markdown
    assert (
        "| Member ID | Declaration | Visibility | Static | Platform | Notes |"
        in markdown
    )
    assert "`static Server LibTmux.Server.Open" in markdown
    assert '`UnsupportedOSPlatform("windows")`' in markdown
    assert "Value: `0`." in markdown
    assert (
        "| `F:LibTmux.ChooseTreeSort.Index` | `Index = 0` | Public | Implicit | "
        "Portable |" in markdown
    )
    assert (
        "`IEnumerator System.Collections.IEnumerable.GetEnumerator()` | "
        "Explicit interface | No | Portable" in markdown
    )
    assert "Compiler-generated by the record struct." in markdown
    assert "## TmuxVersion semantic contract" in markdown
    assert "`3.2a` inclusive" in markdown
    assert "`3.7c` is informational, not a support ceiling" in markdown
    assert "Stable releases use named capability intervals." in markdown
    assert "the exact lowercase prefix `tmux `" in markdown
    assert '"nonzeroExit": "TmuxCommandException carrying Result"' in markdown
    assert "exact preserved patch, prerelease, development, vendor, or next" in markdown


def test_every_public_owned_scope_has_a_factory() -> None:
    """Reject public ownership types that consumers cannot obtain."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    owned_type_names = {
        entry["name"]
        for entry in public_api["types"]
        if entry["ownership"] == "owned" and "public" in entry["modifiers"]
    }
    return_types = {member.get("returnType", "") for member in public_api["members"]}
    for name in owned_type_names:
        assert any(name in return_type for return_type in return_types)


def test_api_approval_includes_consumer_first_examples() -> None:
    """Require examples for the behaviors that define the C# experience."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    assert set(public_api["examples"]) == {
        "connect-and-own-session",
        "immutable-replacement",
        "capture-query-and-json",
        "real-tmux-testkit",
    }


def test_flag_heavy_requests_preserve_every_approved_behavior() -> None:
    """Freeze complete immutable request records for the Python flag surface."""
    members = public_api_members().values()
    expected_properties = {
        "SetHookRequest": {
            "Name",
            "Value",
            "Scope",
            "Global",
            "Unset",
            "RunImmediately",
            "Append",
        },
        "SetHooksRequest": {"Name", "Values", "Scope", "Global", "ClearExisting"},
        "SetOptionRequest": {
            "Name",
            "Value",
            "Scope",
            "ExpandFormat",
            "PreventOverwrite",
            "Quiet",
            "Append",
            "Global",
        },
        "UnsetOptionRequest": {
            "Name",
            "Scope",
            "Global",
            "UnsetPaneOverrides",
            "Quiet",
        },
        "GetOptionRequest": {
            "Name",
            "Scope",
            "Global",
            "IncludeHooks",
            "IncludeInherited",
            "Quiet",
        },
        "GetOptionsRequest": {
            "Scope",
            "Global",
            "IncludeHooks",
            "IncludeInherited",
            "Quiet",
        },
        "CapturePaneRequest": {
            "StartLine",
            "EndLine",
            "EscapeSequences",
            "EscapeNonPrintable",
            "JoinWrappedLines",
            "PreserveTrailingSpaces",
            "TrimTrailingSpaces",
            "AlternateScreen",
            "Quiet",
            "ModeScreen",
            "Pending",
            "Hyperlinks",
            "LineNumbers",
            "LineFlags",
        },
        "ChooseTreeRequest": {
            "SessionsCollapsed",
            "WindowsCollapsed",
            "Format",
            "NativeFilter",
            "Sort",
            "Reverse",
            "Zoom",
        },
        "CopyModeRequest": {
            "ScrollUp",
            "ExitOnBottom",
            "MouseDrag",
            "Cancel",
            "PageDown",
            "SourcePane",
        },
        "DisplayMessageRequest": {
            "Message",
            "ReturnText",
            "Format",
            "AllFormats",
            "Verbose",
            "NoExpand",
            "TargetClient",
            "Delay",
            "Notify",
            "UpdatePane",
        },
        "DisplayPopupRequest": {
            "Command",
            "CloseMode",
            "CloseExisting",
            "TargetClient",
            "Width",
            "Height",
            "X",
            "Y",
            "StartDirectory",
            "Title",
            "BorderLines",
            "Style",
            "BorderStyle",
            "Environment",
            "NoBorder",
            "CloseOnAnyKey",
            "NoKeys",
        },
        "FindWindowRequest": {
            "Pattern",
            "MatchContent",
            "IgnoreCase",
            "MatchName",
            "Regex",
            "MatchTitle",
        },
        "NewPaneRequest": {
            "Target",
            "StartDirectory",
            "Attach",
            "Command",
            "Environment",
            "Width",
            "Height",
            "X",
            "Y",
            "Zoom",
            "Empty",
            "Style",
            "ActiveBorderStyle",
            "InactiveBorderStyle",
            "Message",
            "KeepOpen",
        },
        "PasteBufferRequest": {
            "Name",
            "DeleteAfter",
            "UseLineFeedSeparator",
            "Bracketed",
            "Separator",
            "RawBytes",
        },
        "PipePaneRequest": {"Command", "OutputOnly", "InputOnly", "Toggle"},
        "ResizePaneRequest": {
            "Direction",
            "Adjustment",
            "Width",
            "Height",
            "Zoom",
            "Mouse",
            "TrimBelow",
        },
        "RespawnRequest": {
            "Command",
            "StartDirectory",
            "Environment",
            "KillExistingProcess",
        },
        "SelectPaneRequest": {
            "Direction",
            "KeepZoom",
            "Mark",
            "InputEnabled",
            "Last",
        },
        "SendKeysRequest": {
            "Text",
            "Enter",
            "SuppressHistory",
            "Literal",
            "Reset",
            "CopyModeCommand",
            "Repeat",
            "ExpandFormats",
            "HexKeys",
            "TargetClient",
            "KeyName",
        },
        "SplitPaneRequest": {
            "Target",
            "StartDirectory",
            "Attach",
            "Direction",
            "FullWindow",
            "Zoom",
            "Command",
            "Size",
            "Percentage",
            "Environment",
            "Empty",
            "Style",
            "ActiveBorderStyle",
            "InactiveBorderStyle",
            "Message",
            "KeepOpen",
        },
        "SwapPaneRequest": {"Target", "Direction", "Detach", "KeepZoom"},
        "BindKeyRequest": {"Key", "Command", "KeyTable", "Note", "Repeat"},
        "CommandPromptRequest": {
            "Template",
            "Prompt",
            "Inputs",
            "TargetClient",
            "OneKey",
            "KeyOnly",
            "OnInputChange",
            "Numeric",
            "Type",
            "ExpandFormat",
            "Literal",
            "BackspaceExits",
            "NoFreeze",
        },
        "ConfirmBeforeRequest": {
            "Command",
            "Prompt",
            "ConfirmKey",
            "DefaultYes",
            "TargetClient",
        },
        "DisplayMenuRequest": {
            "Items",
            "Title",
            "TargetPane",
            "TargetClient",
            "X",
            "Y",
            "StartingChoice",
            "BorderLines",
            "Style",
            "BorderStyle",
            "SelectedStyle",
            "Mouse",
            "StayOpen",
        },
        "IfShellRequest": {
            "ShellCommand",
            "ThenCommand",
            "ElseCommand",
            "Background",
            "TargetPane",
        },
        "ListBuffersRequest": {"Format", "Filter"},
        "NewSessionRequest": {
            "Name",
            "ReplaceExisting",
            "Attach",
            "StartDirectory",
            "WindowName",
            "Command",
            "Width",
            "Height",
            "Environment",
            "DetachOthers",
            "NoSize",
            "ClientFlags",
        },
        "RunShellRequest": {
            "Command",
            "Arguments",
            "Background",
            "Delay",
            "AsTmuxCommand",
            "TargetPane",
            "WorkingDirectory",
            "ShowStandardError",
        },
        "ServerAccessRequest": {
            "AllowUser",
            "DenyUser",
            "List",
            "ReadOnly",
            "ReadWrite",
        },
        "UnbindKeyRequest": {"Key", "KeyTable", "All", "Quiet"},
        "AttachSessionRequest": {
            "Target",
            "DetachOthers",
            "ReadOnly",
            "ExitOnDetach",
            "ClientFlags",
        },
        "NewWindowRequest": {
            "Name",
            "StartDirectory",
            "Attach",
            "Index",
            "Command",
            "Environment",
            "Direction",
            "TargetWindow",
            "KillExisting",
            "SelectExisting",
        },
        "ResizeWindowRequest": {"Direction", "Adjustment", "Width", "Height", "Mode"},
        "SelectLayoutRequest": {"Layout", "Mode"},
    }
    actual = {
        type_name: {
            member["name"]
            for member in members
            if member["declaringType"] == f"T:LibTmux.{type_name}"
            and member["kind"] == "property"
        }
        for type_name in expected_properties
    }
    assert actual == expected_properties


def test_internalized_constants_and_owner_context_have_exact_destinations() -> None:
    """Keep command flags and materialization ownership out of format lookup."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    rows = {row["pythonSymbolId"]: row for row in ledger["rows"]}
    expected = {
        "libtmux.constants:DEFAULT_OPTION_SCOPE": (
            "P:LibTmux.Internal.CommandFlagCatalog.DefaultOptionScope"
        ),
        "libtmux.constants:HOOK_SCOPE_FLAG_MAP": (
            "M:LibTmux.Internal.CommandFlagCatalog.GetHookScopeFlag(OptionScope)"
        ),
        "libtmux.constants:OPTION_SCOPE_FLAG_MAP": (
            "M:LibTmux.Internal.CommandFlagCatalog.GetOptionScopeFlag(OptionScope)"
        ),
        "libtmux.constants:PANE_DIRECTION_FLAG_MAP": (
            "M:LibTmux.Internal.CommandFlagCatalog.GetPaneDirectionFlags(PaneDirection)"
        ),
        "libtmux.constants:RESIZE_ADJUSTMENT_DIRECTION_FLAG_MAP": (
            "M:LibTmux.Internal.CommandFlagCatalog.GetResizeDirectionFlag(ResizeDirection)"
        ),
        "libtmux.constants:WINDOW_DIRECTION_FLAG_MAP": (
            "M:LibTmux.Internal.CommandFlagCatalog.GetWindowDirectionFlag(WindowDirection)"
        ),
        "libtmux.neo:Obj.server": ("P:LibTmux.Internal.MaterializationContext.Server"),
    }
    assert {
        symbol_id: rows[symbol_id]["csharpDestination"] for symbol_id in expected
    } == expected
    assert (
        "M:LibTmux.Internal.MaterializationContext.#ctor(Server)"
        in public_api_members()
    )


def test_xunit_harness_stays_out_of_the_production_contract() -> None:
    """Keep PTY and control-mode scopes in the integration-test project."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ids = {
        entry["id"]
        for collection in (public_api["types"], public_api["members"])
        for entry in collection
    }
    assert not any("XunitTmuxHarness" in identifier for identifier in ids)


def test_raising_property_tombstones_name_fresh_replacements() -> None:
    """Do not replace live Python lookups with possibly uncaptured relations."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    rows = {row["pythonSymbolId"]: row for row in ledger["rows"]}
    expected = {
        "libtmux.server:Server._sessions": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.server:Server.children": (
            "M:LibTmux.Server.GetSessionsAsync(CancellationToken)"
        ),
        "libtmux.session:Session._windows": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.session:Session.attached_pane": (
            "(await session.RefreshAsync(cancellationToken)).ActivePane"
        ),
        "libtmux.session:Session.attached_window": (
            "(await session.RefreshAsync(cancellationToken)).ActiveWindow"
        ),
        "libtmux.session:Session.children": (
            "M:LibTmux.Session.GetWindowsAsync(CancellationToken)"
        ),
        "libtmux.window:Window._panes": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
        "libtmux.window:Window.attached_pane": (
            "(await window.RefreshAsync(cancellationToken)).ActivePane"
        ),
        "libtmux.window:Window.children": (
            "M:LibTmux.Window.GetPanesAsync(CancellationToken)"
        ),
    }
    assert {symbol_id: rows[symbol_id]["replacement"] for symbol_id in expected} == (
        expected
    )


def test_materializers_require_explicit_owner_context() -> None:
    """Prevent entity handles from acquiring their Server through ambient state."""
    members = public_api_members()
    expected = {
        "M:LibTmux.Internal.Materializer.MaterializeFormatFields(MaterializationContext,ReadOnlySpan<byte>)",
        "M:LibTmux.Internal.Materializer.MaterializePane(MaterializationContext,ReadOnlySpan<byte>)",
        "M:LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext,ReadOnlySpan<byte>)",
        "M:LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext,ReadOnlySpan<byte>)",
    }
    assert expected <= members.keys()


def test_bad_session_name_replacement_uses_the_approved_parameter_name() -> None:
    """Keep conventional ArgumentException guidance aligned with C# signatures."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    row = next(
        row
        for row in ledger["rows"]
        if row["pythonSymbolId"] == "libtmux.exc:BadSessionName"
    )
    assert row["replacement"] == (
        "ArgumentException with parameter name name from NewSessionRequest "
        "construction or Session.RenameAsync"
    )


def test_pytest_exclusions_name_owned_test_resources() -> None:
    """Keep framework exclusions aligned with concrete integration-test scopes."""
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    pytest_rows = [
        row for row in ledger["rows"] if row["module"] == "libtmux.pytest_plugin"
    ]
    assert all("xUnit harness" not in row.get("replacement", "") for row in pytest_rows)
    control_mode = next(
        row
        for row in pytest_rows
        if row["pythonSymbolId"] == "libtmux.pytest_plugin:control_mode"
    )
    assert control_mode["replacement"] == "test-only ControlModeClientScope"


def test_format_metadata_and_framing_have_typed_internal_boundaries() -> None:
    """Separate catalog metadata and byte framing from materialized values."""
    members = public_api_members()
    resolve_id = "M:LibTmux.Internal.FormatCatalog.Resolve(string)"
    materialize_id = (
        "M:LibTmux.Internal.Materializer.MaterializeFormatFields("
        "MaterializationContext,ReadOnlySpan<byte>)"
    )
    assert members[resolve_id]["returnType"] == "FormatFieldDescriptor"
    assert members[materialize_id]["returnType"] == (
        "IReadOnlyDictionary<string,string?>"
    )
    assert (
        "M:LibTmux.Internal.FormatFieldDescriptor.#ctor("
        "string,string,TmuxVersion,IReadOnlySet<string>)" in members
    )
    version_id = "M:LibTmux.Internal.FormatCatalog.GetMinimumTmuxVersion(string)"
    scopes_id = "M:LibTmux.Internal.FormatCatalog.GetScopesForListCommand(string)"
    framer_id = "M:LibTmux.Internal.SeparatedRowFramer.Decode(ReadOnlySpan<byte>)"
    assert members[version_id]["returnType"] == "TmuxVersion"
    assert members[scopes_id]["returnType"] == "IReadOnlySet<string>"
    assert members[framer_id]["returnType"] == ("IReadOnlyList<ReadOnlyMemory<byte>>")
    projections = {
        "libtmux.formats:CLIENT_FORMATS": (
            "P:LibTmux.Internal.FormatCatalog.ClientProjection"
        ),
        "libtmux.formats:PANE_FORMATS": (
            "P:LibTmux.Internal.FormatCatalog.PaneProjection"
        ),
        "libtmux.formats:SESSION_FORMATS": (
            "P:LibTmux.Internal.FormatCatalog.SessionProjection"
        ),
        "libtmux.formats:WINDOW_FORMATS": (
            "P:LibTmux.Internal.FormatCatalog.WindowProjection"
        ),
    }
    assert all(
        members[member_id]["returnType"] == "IReadOnlyList<FormatFieldDescriptor>"
        for member_id in projections.values()
    )

    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    rows = {row["pythonSymbolId"]: row for row in ledger["rows"]}
    assert rows["libtmux.neo:FIELD_VERSION"]["csharpDestination"] == version_id
    assert rows["libtmux.neo:SCOPES_BY_LIST_CMD"]["csharpDestination"] == scopes_id
    separator = rows["libtmux.formats:FORMAT_SEPARATOR"]
    assert separator["destinationStatus"] == "excluded"
    assert separator["replacement"] == framer_id
    assert {
        symbol_id: rows[symbol_id]["csharpDestination"] for symbol_id in projections
    } == projections

    by_id = public_api_members()
    assert by_id["P:LibTmux.SetHooksRequest.Values"]["returnType"] == (
        "IReadOnlyDictionary<int,string>"
    )
    assert by_id["P:LibTmux.DisplayPopupRequest.CloseMode"]["returnType"] == (
        "PopupCloseMode?"
    )
    assert by_id["P:LibTmux.SplitPaneRequest.Percentage"]["returnType"] == "int?"
    public_api = load_json(csharp_docs_root() / "public-api.json")
    request_types = {entry["name"]: entry for entry in public_api["types"]}
    assert request_types["SwapPaneRequest"]["validation"] == (
        "exactly one of Target or Direction"
    )
    assert request_types["SplitPaneRequest"]["validation"] == (
        "Size and Percentage are mutually exclusive"
    )
    assert request_types["SetHooksRequest"]["validation"] == (
        "sparse hook indices are nonnegative and preserved"
    )


def test_c4_projection_framing_and_materialization_contract_is_complete() -> None:
    """Freeze the byte-safe C4 path from generated projection to typed handles."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    members = public_api_members()

    assert {
        "T:LibTmux.Internal.FormatProjection",
        "T:LibTmux.Internal.MaterializationQuery",
    } <= types.keys()
    assert "T:LibTmux.Internal.EntityMaterializationState" not in types
    assert "T:LibTmux.Internal.TmuxTransportLimits" not in types
    assert types["T:LibTmux.Internal.FormatProjection"]["behavior"] == (
        C4_FORMAT_PROJECTION_BEHAVIOR
    )
    assert types["T:LibTmux.Internal.SeparatedRowFramer"]["validation"] == (
        C4_FRAMING_VALIDATION
    )

    expected_members = {
        "M:LibTmux.Internal.FormatProjection.Create(string,TmuxVersion)": (
            "FormatProjection",
            ["string", "TmuxVersion"],
        ),
        "P:LibTmux.Internal.FormatProjection.Fields": (
            "IReadOnlyList<FormatFieldDescriptor>",
            [],
        ),
        "P:LibTmux.Internal.FormatProjection.TmuxFormat": ("string", []),
        "P:LibTmux.Internal.FormatProjection.FramedFieldCount": ("int", []),
        "P:LibTmux.Internal.FormatCatalog.ObjProjection": (
            "IReadOnlyList<FormatFieldDescriptor>",
            [],
        ),
        "M:LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte>,int,int)": (
            "IReadOnlyList<IReadOnlyDictionary<string,ReadOnlyMemory<byte>>>",
            ["ReadOnlySpan<byte>", "int", "int"],
        ),
        "M:LibTmux.Internal.MaterializationQuery.FetchAsync(MaterializationContext,string,IReadOnlyList<string>?,string?,CancellationToken)": (
            "Task<IReadOnlyList<IReadOnlyDictionary<string,string?>>>",
            [
                "MaterializationContext",
                "string",
                "IReadOnlyList<string>?",
                "string?",
                "CancellationToken",
            ],
        ),
        "M:LibTmux.Internal.MaterializationQuery.FetchOneAsync(MaterializationContext,string,string,string,IReadOnlyList<string>?,CancellationToken)": (
            "Task<IReadOnlyDictionary<string,string?>>",
            [
                "MaterializationContext",
                "string",
                "string",
                "string",
                "IReadOnlyList<string>?",
                "CancellationToken",
            ],
        ),
        "M:LibTmux.Internal.Materializer.MaterializeSession(MaterializationContext,IReadOnlyDictionary<string,string?>)": (
            "Session",
            ["MaterializationContext", "IReadOnlyDictionary<string,string?>"],
        ),
        "M:LibTmux.Internal.Materializer.MaterializeWindow(MaterializationContext,IReadOnlyDictionary<string,string?>)": (
            "Window",
            ["MaterializationContext", "IReadOnlyDictionary<string,string?>"],
        ),
        "M:LibTmux.Internal.Materializer.MaterializePane(MaterializationContext,IReadOnlyDictionary<string,string?>)": (
            "Pane",
            ["MaterializationContext", "IReadOnlyDictionary<string,string?>"],
        ),
        "M:LibTmux.Internal.ServerProjectionDescriptor.#ctor(string,string)": (
            "ServerProjectionDescriptor",
            ["string", "string"],
        ),
        "P:LibTmux.Internal.ServerProjectionDescriptor.ChildIdAttribute": (
            "string",
            [],
        ),
        "P:LibTmux.Internal.ServerProjectionDescriptor.FormatterPrefix": (
            "string",
            [],
        ),
    }
    assert {
        member_id: (
            members[member_id]["returnType"],
            [parameter["type"] for parameter in members[member_id]["parameters"]],
        )
        for member_id in expected_members
    } == expected_members
    assert (
        members["P:LibTmux.Internal.FormatCatalog.ObjProjection"]["behavior"]
        == C4_OBJ_PROJECTION_BEHAVIOR
    )
    assert (
        members[
            "M:LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte>,int,int)"
        ]["validation"]
        == C4_FRAMING_VALIDATION
    )
    assert members[
        "M:LibTmux.Internal.MaterializationQuery.FetchAsync(MaterializationContext,string,IReadOnlyList<string>?,string?,CancellationToken)"
    ]["failureMapping"] == {
        "framing": "TmuxTransportException carrying logical tmux arguments",
        "lowLevel": "InvalidDataException",
    }
    assert types["T:LibTmux.Internal.MaterializationQuery"]["behavior"] == (
        C4_QUERY_GENERATION_BEHAVIOR
    )
    assert types["T:LibTmux.Internal.Materializer"]["behavior"] == (
        C4_ROW_GENERATION_BEHAVIOR
    )


def test_c4_contract_validator_rejects_projection_and_framing_drift() -> None:
    """Make generated counts and hostile byte framing enforceable decisions."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    types = {entry["id"]: entry for entry in public_api["types"]}
    members = {entry["id"]: entry for entry in public_api["members"]}
    types["T:LibTmux.Internal.FormatProjection"]["behavior"]["emittedFieldCounts"][
        "list-clients"
    ]["3.7a+"] = 160
    members[
        "M:LibTmux.Internal.SeparatedRowFramer.DecodeRows(ReadOnlySpan<byte>,int,int)"
    ]["validation"] = "delimiter-separated rows"
    members[
        "M:LibTmux.Internal.MaterializationQuery.FetchOneAsync(MaterializationContext,string,string,string,IReadOnlyList<string>?,CancellationToken)"
    ]["parameters"][2]["name"] = "target"
    types["T:LibTmux.Internal.MaterializationQuery"]["behavior"][
        "requiredUniversalFields"
    ] = ["pid"]
    public_api["members"] = [
        entry
        for entry in public_api["members"]
        if entry["id"]
        != "P:LibTmux.Internal.ServerProjectionDescriptor.FormatterPrefix"
    ]

    violations = api_validator()(public_api, ledger)

    assert "invalid C4 format projection contract" in violations
    assert "invalid C4 framing contract" in violations
    assert "invalid C4 API surface contract" in violations
    assert "invalid C4 generation contract" in violations


def test_every_public_request_record_is_reachable() -> None:
    """Reject request records that no approved operation consumes."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    members = public_api["members"]
    for type_entry in public_api["types"]:
        request_name = type_entry["name"]
        if not request_name.endswith("Request"):
            continue
        assert any(
            member["declaringType"] != type_entry["id"]
            and (
                request_name in member.get("returnType", "")
                or any(
                    request_name in parameter["type"]
                    for parameter in member.get("parameters", [])
                )
            )
            for member in members
        ), type_entry["id"]


def test_all_48_flag_heavy_rows_bind_exact_operations() -> None:
    """Bind every audited callable to the method carrying all of its flags."""
    expected = {
        "libtmux.common:EnvironmentMixin.set_environment": "M:LibTmux.TmuxEnvironment.SetAsync(string,string,bool,bool,CancellationToken)",
        "libtmux.hooks:HooksMixin.run_hook": "M:LibTmux.TmuxHooks.RunAsync(HookRequest,CancellationToken)",
        "libtmux.hooks:HooksMixin.set_hook": "M:LibTmux.TmuxHooks.SetAsync(SetHookRequest,CancellationToken)",
        "libtmux.hooks:HooksMixin.set_hooks": "M:LibTmux.TmuxHooks.SetAsync(SetHooksRequest,CancellationToken)",
        "libtmux.options:OptionsMixin.set_option": "M:LibTmux.TmuxOptions.SetAsync(SetOptionRequest,CancellationToken)",
        "libtmux.options:OptionsMixin.unset_option": "M:LibTmux.TmuxOptions.UnsetAsync(UnsetOptionRequest,CancellationToken)",
        "libtmux.options:OptionsMixin.show_option": "M:LibTmux.TmuxOptions.GetAsync(GetOptionRequest,CancellationToken)",
        "libtmux.options:OptionsMixin.show_options": "M:LibTmux.TmuxOptions.GetAllAsync(GetOptionsRequest?,CancellationToken)",
        "libtmux.pane:Pane.capture_pane": "M:LibTmux.Pane.CaptureAsync(CapturePaneRequest?,CancellationToken)",
        "libtmux.pane:Pane.choose_tree": "M:LibTmux.Pane.ChooseTreeAsync(ChooseTreeRequest?,CancellationToken)",
        "libtmux.pane:Pane.copy_mode": "M:LibTmux.Pane.EnterCopyModeAsync(CopyModeRequest?,CancellationToken)",
        "libtmux.pane:Pane.display_message": "M:LibTmux.Pane.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)",
        "libtmux.pane:Pane.display_panes": "M:LibTmux.Pane.DisplayPaneNumbersAsync(TimeSpan?,bool,CancellationToken)",
        "libtmux.pane:Pane.display_popup": "M:LibTmux.Pane.DisplayPopupAsync(DisplayPopupRequest?,CancellationToken)",
        "libtmux.pane:Pane.find_window": "M:LibTmux.Pane.FindWindowAsync(FindWindowRequest,CancellationToken)",
        "libtmux.pane:Pane.new_pane": "M:LibTmux.Pane.CreatePaneAsync(NewPaneRequest?,CancellationToken)",
        "libtmux.pane:Pane.paste_buffer": "M:LibTmux.Pane.PasteBufferAsync(PasteBufferRequest?,CancellationToken)",
        "libtmux.pane:Pane.pipe": "M:LibTmux.Pane.PipeAsync(PipePaneRequest?,CancellationToken)",
        "libtmux.pane:Pane.resize": "M:LibTmux.Pane.ResizeAsync(ResizePaneRequest,CancellationToken)",
        "libtmux.pane:Pane.respawn": "M:LibTmux.Pane.RespawnAsync(RespawnRequest?,CancellationToken)",
        "libtmux.pane:Pane.select": "M:LibTmux.Pane.SelectAsync(SelectPaneRequest?,CancellationToken)",
        "libtmux.pane:Pane.send_keys": "M:LibTmux.Pane.SendKeysAsync(SendKeysRequest,CancellationToken)",
        "libtmux.pane:Pane.split": "M:LibTmux.Pane.SplitAsync(SplitPaneRequest?,CancellationToken)",
        "libtmux.pane:Pane.swap": "M:LibTmux.Pane.SwapAsync(SwapPaneRequest,CancellationToken)",
        "libtmux.server:Server.bind_key": "M:LibTmux.Server.BindKeyAsync(BindKeyRequest,CancellationToken)",
        "libtmux.server:Server.command_prompt": "M:LibTmux.Server.ShowCommandPromptAsync(CommandPromptRequest,CancellationToken)",
        "libtmux.server:Server.confirm_before": "M:LibTmux.Server.ConfirmBeforeAsync(ConfirmBeforeRequest,CancellationToken)",
        "libtmux.server:Server.display_menu": "M:LibTmux.Server.ShowMenuAsync(DisplayMenuRequest,CancellationToken)",
        "libtmux.server:Server.display_message": "M:LibTmux.Server.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)",
        "libtmux.server:Server.if_shell": "M:LibTmux.Server.IfShellAsync(IfShellRequest,CancellationToken)",
        "libtmux.server:Server.list_buffers": "M:LibTmux.Server.GetBufferLinesAsync(ListBuffersRequest?,CancellationToken)",
        "libtmux.server:Server.list_keys": "M:LibTmux.Server.GetKeysAsync(string?,string?,CancellationToken)",
        "libtmux.server:Server.new_session": "M:LibTmux.Server.CreateSessionAsync(NewSessionRequest?,CancellationToken)",
        "libtmux.server:Server.run_shell": "M:LibTmux.Server.RunShellAsync(RunShellRequest,CancellationToken)",
        "libtmux.server:Server.server_access": "M:LibTmux.Server.ConfigureAccessAsync(ServerAccessRequest,CancellationToken)",
        "libtmux.server:Server.show_messages": "M:LibTmux.Server.GetMessagesAsync(string?,ShowMessagesMode,CancellationToken)",
        "libtmux.server:Server.source_file": "M:LibTmux.Server.SourceFileAsync(string,bool,bool,bool,CancellationToken)",
        "libtmux.server:Server.unbind_key": "M:LibTmux.Server.UnbindKeyAsync(UnbindKeyRequest,CancellationToken)",
        "libtmux.session:Session.attach": "M:LibTmux.Session.AttachAsync(AttachSessionRequest?,CancellationToken)",
        "libtmux.session:Session.kill": "M:LibTmux.Session.KillAsync(bool,bool,bool,CancellationToken)",
        "libtmux.session:Session.new_window": "M:LibTmux.Session.CreateWindowAsync(NewWindowRequest?,CancellationToken)",
        "libtmux.window:Window.display_message": "M:LibTmux.Window.DisplayMessageAsync(DisplayMessageRequest,CancellationToken)",
        "libtmux.window:Window.new_pane": "M:LibTmux.Window.CreatePaneAsync(NewPaneRequest?,CancellationToken)",
        "libtmux.window:Window.new_window": "M:LibTmux.Window.CreateWindowAsync(NewWindowRequest?,CancellationToken)",
        "libtmux.window:Window.resize": "M:LibTmux.Window.ResizeAsync(ResizeWindowRequest,CancellationToken)",
        "libtmux.window:Window.respawn": "M:LibTmux.Window.RespawnAsync(RespawnRequest?,CancellationToken)",
        "libtmux.window:Window.select_layout": "M:LibTmux.Window.SelectLayoutAsync(SelectLayoutRequest?,CancellationToken)",
        "libtmux.window:Window.split": "M:LibTmux.Window.SplitPaneAsync(SplitPaneRequest?,CancellationToken)",
    }
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    rows = {row["pythonSymbolId"]: row for row in ledger["rows"]}
    assert len(expected) == 48
    assert {
        python_id: rows[python_id]["csharpDestination"] for python_id in expected
    } == expected
    assert all(
        rows[python_id]["destinationStatus"] == "approved" for python_id in expected
    )


def test_internalized_rows_have_typed_destinations() -> None:
    """Reject a string/object compatibility catch-all in the approved boundary."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    serialized = json.dumps(public_api, sort_keys=True)
    assert "ApplyParityAdapter" not in serialized
    assert all(
        "ApplyParityAdapter" not in str(row.get("csharpDestination"))
        for row in ledger["rows"]
    )
    rows = {row["pythonSymbolId"]: row for row in ledger["rows"]}
    assert rows["libtmux.common:PaneDict"]["csharpDestination"] == (
        "M:LibTmux.Internal.Materializer.MaterializePane("
        "MaterializationContext,ReadOnlySpan<byte>)"
    )
    assert rows["libtmux.options:convert_value"]["csharpDestination"] == (
        "M:LibTmux.Internal.OptionParser.ParseValue(string?)"
    )
    assert rows["libtmux.hooks:HookValues"]["csharpDestination"] == (
        "T:LibTmux.TmuxHookEntry"
    )


def test_version_surface_is_lossless_ordered_and_executable() -> None:
    """Freeze preserved version text, comparisons, and executable discovery."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    members = public_api_members()
    assert members["P:LibTmux.TmuxVersion.Raw"]["returnType"] == "string"
    assert members["P:LibTmux.TmuxVersion.IsValid"]["returnType"] == "bool"
    version_type = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.TmuxVersion"
    )
    assert version_type["defaultValue"] == {
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
    operator_declarations = {
        "op_Equality": "bool operator ==(TmuxVersion left, TmuxVersion right)",
        "op_Inequality": "bool operator !=(TmuxVersion left, TmuxVersion right)",
        "op_GreaterThan": "bool operator >(TmuxVersion left, TmuxVersion right)",
        "op_GreaterThanOrEqual": (
            "bool operator >=(TmuxVersion left, TmuxVersion right)"
        ),
        "op_LessThan": "bool operator <(TmuxVersion left, TmuxVersion right)",
        "op_LessThanOrEqual": ("bool operator <=(TmuxVersion left, TmuxVersion right)"),
    }
    assert {
        member["name"]: member["signature"]
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.TmuxVersion"
        and member["name"] in operator_declarations
    } == operator_declarations
    version_operators = {
        member["name"]: member
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.TmuxVersion"
        and member["name"] in operator_declarations
    }
    assert version_operators["op_Equality"]["compilerGenerated"] is True
    assert version_operators["op_Inequality"]["compilerGenerated"] is True
    assert all(
        not version_operators[name].get("compilerGenerated", False)
        for name in (
            "op_GreaterThan",
            "op_GreaterThanOrEqual",
            "op_LessThan",
            "op_LessThanOrEqual",
        )
    )
    minimum_members = {
        member["name"]: member
        for member in members.values()
        if member["declaringType"] == "T:LibTmux.TmuxVersion"
        and "MinimumSupportedVersion" in member["name"]
    }
    assert set(minimum_members) == {
        "CheckMinimumSupportedVersionAsync",
        "IsMinimumSupportedVersionInstalledAsync",
        "EnsureMinimumSupportedVersionAsync",
    }
    assert minimum_members["CheckMinimumSupportedVersionAsync"]["parameters"][0] == {
        "name": "throwIfUnsupported",
        "type": "bool",
        "default": "true",
    }
    assert (
        minimum_members["IsMinimumSupportedVersionInstalledAsync"]["returnType"]
        == "Task<bool>"
    )
    assert minimum_members["EnsureMinimumSupportedVersionAsync"]["returnType"] == (
        "Task"
    )
    for name in (
        "CompareTo",
        "IsAtLeast",
        "EnsureAtLeast",
        "op_Equality",
        "op_Inequality",
        "op_GreaterThan",
        "op_GreaterThanOrEqual",
        "op_LessThan",
        "op_LessThanOrEqual",
    ):
        assert any(
            member["declaringType"] == "T:LibTmux.TmuxVersion"
            and member["name"] == name
            for member in members.values()
        )
    assert any(
        member["declaringType"] == "T:LibTmux.TmuxVersion"
        and member["name"] == "DetectAsync"
        and member["processBacked"] is True
        for member in members.values()
    )


def test_tmux_version_semantics_are_canonical_and_ledger_adaptation_is_explicit() -> (
    None
):
    """Freeze parsing, ordering, detection, support, and Python adaptation."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    version_type = next(
        row for row in public_api["types"] if row["id"] == "T:LibTmux.TmuxVersion"
    )
    assert version_type["versionContract"] == TMUX_VERSION_CONTRACT
    members = {member["id"]: member for member in public_api["members"]}
    assert members["P:LibTmux.TmuxVersion.Suffix"]["summary"] == (
        "Gets the exact preserved patch, prerelease, development, vendor, or next "
        "suffix projection."
    )
    support = version_type["versionContract"]["support"]
    assert public_api["supportedTmuxVersions"]["minimum"] == "3.2a"
    assert public_api["supportedTmuxVersions"]["stableSupport"] == (
        "every canonical stable release at or above the minimum"
    )
    required = public_api["supportedTmuxVersions"]["required"]
    assert support["minimum"] == required[0] == "3.2a"
    assert support["maximumTested"] == required[-1] == "3.7c"
    max_row = next(
        row
        for row in ledger["rows"]
        if row["pythonSymbolId"] == "libtmux.common:TMUX_MAX_VERSION"
    )
    assert max_row["behavior"] == TMUX_MAX_VERSION_ADAPTATION
    assert (
        max_row["component"],
        max_row["componentId"],
        max_row["csharpDestination"],
        max_row["destinationStatus"],
    ) == (
        "common",
        3,
        "P:LibTmux.LibTmuxInfo.MaximumTestedTmuxVersion",
        "approved",
    )


def test_validator_rejects_stable_tmux_support_drift() -> None:
    """Reject an exact-list interpretation of the open-ended support floor."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api["supportedTmuxVersions"]["stableSupport"] = "required versions only"

    violations = api_validator()(public_api, ledger)

    assert "invalid stable tmux support boundary" in violations


def test_examples_are_canonical_coherent_and_executable_sources() -> None:
    """Keep examples in JSON and show one coherent query JSON round trip."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    for example in public_api["examples"].values():
        assert set(example) == {"title", "description", "source"}
        assert "static async Task Main" in example["source"]
        assert "if (OperatingSystem.IsWindows())" in example["source"]
        assert (
            'throw new PlatformNotSupportedException("tmux process execution is '
            'unavailable on Windows.");' in example["source"]
        )
    query_source = public_api["examples"]["capture-query-and-json"]["source"]
    assert "QueryExtensions.Translate<Session>" in query_source
    assert "QueryExtensions.Matching(sessions, document)" in query_source
    assert "QueryJson.Serialize(document)" in query_source
    assert "QueryJson.Deserialize(json)" in query_source
    assert "if (roundTripped != document)" in query_source
    testkit_source = public_api["examples"]["real-tmux-testkit"]["source"]
    assert 'SendKeysRequest(text: "echo libtmux-$(printf %s ready)")' in testkit_source
    assert "string.Equals(" in testkit_source
    assert '"libtmux-ready"' in testkit_source
    assert "StringComparison.Ordinal" in testkit_source
    assert 'Contains("ready"' not in testkit_source


def test_validator_rejects_duplicate_member_and_bad_cancellation() -> None:
    """Reject ambiguous overload identity and non-final cancellation."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api["members"].append(public_api["members"][0])
    io_member = next(
        member for member in public_api["members"] if member.get("performsIO")
    )
    io_member["parameters"].insert(0, io_member["parameters"].pop())

    violations = api_validator()(public_api, ledger)

    assert "duplicate public API member IDs" in violations
    assert f"invalid cancellation parameter: {io_member['id']}" in violations


def test_validator_rejects_unbound_generic_parameters() -> None:
    """Reject signatures that use a method type parameter without declaring it."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    matching = next(
        member
        for member in public_api["members"]
        if member["id"].startswith(
            "M:LibTmux.Query.QueryExtensions.Matching``1(IEnumerable<T>"
        )
    )
    matching["genericParameters"] = []
    matching["id"] = matching["id"].replace("``1", "")
    matching["signature"] = matching["signature"].replace("Matching<T>", "Matching")

    violations = api_validator()(public_api, ledger)

    assert f"unbound generic parameter: {matching['id']}" in violations


def test_validator_rejects_instance_members_on_static_classes() -> None:
    """Reject an instance declaration on a static class."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    static_member = next(
        member
        for member in public_api["members"]
        if member["id"]
        == "M:LibTmux.Query.QueryEdgeParser.ParseNameContains(QueryTarget,string)"
    )
    static_member["static"] = False

    violations = api_validator()(public_api, ledger)

    assert f"instance member on static class: {static_member['id']}" in violations


def test_validator_rejects_public_explicit_interface_declarations() -> None:
    """Reject an explicit interface implementation carrying public visibility."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    enumerator = next(
        member
        for member in public_api["members"]
        if member["id"]
        == "M:LibTmux.CapturedRelation`1.System.Collections.IEnumerable.GetEnumerator()"
    )
    enumerator["visibility"] = "public"
    enumerator["id"] = enumerator["id"].replace(
        "System.Collections", "System#Collections"
    )
    public_api["memberIdFormat"]["xmlDocumentationIds"] = True

    violations = api_validator()(public_api, ledger)

    assert f"invalid explicit interface visibility: {enumerator['id']}" in violations
    assert f"invalid explicit interface ID: {enumerator['id']}" in violations
    assert "invalid member ID format" in violations


def test_validator_rejects_public_type_and_value_contract_drift() -> None:
    """Reject unreachable types, implicit enum ABI, and invalid generation identity."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api["types"].append(
        {
            "id": "T:LibTmux.Query.OrphanKind",
            "namespace": "LibTmux.Query",
            "name": "OrphanKind",
            "kind": "enum",
            "package": "LibTmux",
            "modifiers": ["public"],
            "baseType": "object",
            "interfaces": [],
            "ownership": "value",
            "state": [],
            "summary": "An unreachable test enum.",
        }
    )
    public_api["members"].extend(
        [
            {
                "id": "T:LibTmux.Query.OrphanKind",
                "declaringType": "T:LibTmux.Query.OrphanKind",
                "name": "OrphanKind",
                "kind": "type",
                "visibility": "public",
                "package": "LibTmux",
                "signature": "enum LibTmux.Query.OrphanKind",
                "portable": True,
            },
            {
                "id": "F:LibTmux.Query.OrphanKind.Value",
                "declaringType": "T:LibTmux.Query.OrphanKind",
                "name": "Value",
                "kind": "enum value",
                "visibility": "public",
                "package": "LibTmux",
                "signature": "OrphanKind.Value",
                "portable": True,
                "summary": "The Value value.",
            },
        ]
    )
    generation = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.ServerGeneration"
    )
    generation["validation"] = "accepts default"

    violations = api_validator()(public_api, ledger)

    assert "unreachable public type: T:LibTmux.Query.OrphanKind" in violations
    assert "missing enum value: F:LibTmux.Query.OrphanKind.Value" in violations
    assert "invalid ServerGeneration contract" in violations


def test_validator_rejects_invalid_value_type_bases() -> None:
    """Reject value types whose direct CLR base is recorded as object."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    snapshot_depth = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.SnapshotDepth"
    )
    session_id = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.SessionId"
    )
    snapshot_depth["baseType"] = "object"
    session_id["baseType"] = "object"

    violations = api_validator()(public_api, ledger)

    assert "invalid enum base: T:LibTmux.SnapshotDepth" in violations
    assert "invalid value-type base: T:LibTmux.SessionId" in violations


def test_validator_rejects_generic_type_and_parameter_keyword_drift() -> None:
    """Reject metadata/source generic drift and reserved parameter names."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    relation = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.CapturedRelation`1"
    )
    relation["genericParameters"] = []
    comparison = next(
        member
        for member in public_api["members"]
        if member["declaringType"] == "T:LibTmux.Query.ComparisonNode"
        and member["kind"] == "constructor"
    )
    comparison["parameters"][0]["name"] = "operator"

    violations = api_validator()(public_api, ledger)

    assert "invalid type generic arity: T:LibTmux.CapturedRelation`1" in violations
    assert f"reserved parameter name: {comparison['id']}: operator" in violations


def test_validator_rejects_tmux_version_default_drift() -> None:
    """Reject a public value-type default that violates its nullability contract."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    version_type = next(
        type_entry
        for type_entry in public_api["types"]
        if type_entry["id"] == "T:LibTmux.TmuxVersion"
    )
    version_type["defaultValue"] = {"raw": None}

    violations = api_validator()(public_api, ledger)

    assert "invalid TmuxVersion default contract" in violations


def test_validator_rejects_tmux_version_semantic_drift() -> None:
    """Make one ordering mutation fail canonical semantic validation."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    version_type = next(
        row for row in public_api["types"] if row["id"] == "T:LibTmux.TmuxVersion"
    )
    version_type["versionContract"] = copy.deepcopy(TMUX_VERSION_CONTRACT)
    version_type["versionContract"]["ordering"]["sameCore"] = "final < rcN"

    violations = api_validator()(public_api, ledger)

    assert "invalid TmuxVersion semantic contract" in violations


def test_validator_rejects_tmux_version_detection_failure_mapping_drift() -> None:
    """Reject wrapping a nonzero detection result as a transport failure."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    version_type = next(
        row for row in public_api["types"] if row["id"] == "T:LibTmux.TmuxVersion"
    )
    version_type["versionContract"] = copy.deepcopy(TMUX_VERSION_CONTRACT)
    version_type["versionContract"]["detection"]["failureMapping"]["nonzeroExit"] = (
        "TmuxTransportException"
    )

    violations = api_validator()(public_api, ledger)

    assert "invalid TmuxVersion detection failure contract" in violations


def test_validator_rejects_tmux_version_suffix_projection_drift() -> None:
    """Reject omitting patch, vendor, and next projections from Suffix."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    suffix = next(
        member
        for member in public_api["members"]
        if member["id"] == "P:LibTmux.TmuxVersion.Suffix"
    )
    suffix["summary"] = "Gets the preserved prerelease or development suffix."

    violations = api_validator()(public_api, ledger)

    assert "invalid TmuxVersion Suffix projection" in violations


def test_validator_rejects_tmux_max_version_preservation_regression() -> None:
    """Reject false exact preservation of Python TMUX_MAX_VERSION."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    version_type = next(
        row for row in public_api["types"] if row["id"] == "T:LibTmux.TmuxVersion"
    )
    version_type["versionContract"] = copy.deepcopy(TMUX_VERSION_CONTRACT)
    max_row = next(
        row
        for row in ledger["rows"]
        if row["pythonSymbolId"] == "libtmux.common:TMUX_MAX_VERSION"
    )
    max_row["behavior"] = "Preserve constant TMUX_MAX_VERSION"

    violations = api_validator()(public_api, ledger)

    assert "invalid TMUX_MAX_VERSION semantic adaptation" in violations


def test_validator_rejects_public_internal_and_disposal_leaks() -> None:
    """Reject visibility drift and destructive disposal on listed handles."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    internalized = next(
        row for row in ledger["rows"] if row["destinationStatus"] == "internalized"
    )
    internal_member = next(
        member
        for member in public_api["members"]
        if member["id"] == internalized["csharpDestination"]
    )
    internal_member["visibility"] = "public"
    public_api["members"].append(
        {
            "id": "M:LibTmux.Session.DisposeAsync()",
            "declaringType": "T:LibTmux.Session",
            "name": "DisposeAsync",
            "kind": "method",
            "visibility": "public",
            "package": "LibTmux",
            "returnType": "ValueTask",
            "parameters": [],
            "signature": "ValueTask Session.DisposeAsync()",
            "portable": True,
        }
    )

    violations = api_validator()(public_api, ledger)

    assert (
        f"internalized destination is public: {internalized['pythonSymbolId']}"
        in violations
    )
    assert "listed entity exposes destructive disposal: T:LibTmux.Session" in violations


def test_validator_rejects_detached_examples_and_catchall_adapters() -> None:
    """Keep canonical examples coherent and internal destinations strongly typed."""
    public_api = load_json(csharp_docs_root() / "public-api.json")
    ledger = load_json(csharp_docs_root() / "parity" / "parity-ledger.json")
    public_api["examples"]["capture-query-and-json"]["source"] = (
        "static async Task Main() { await Task.CompletedTask; }"
    )
    internalized = next(
        row for row in ledger["rows"] if row["destinationStatus"] == "internalized"
    )
    old_destination = internalized["csharpDestination"]
    adapter_id = "M:LibTmux.Internal.Materializer.ApplyParityAdapter(string,object?)"
    adapter = next(
        member for member in public_api["members"] if member["id"] == old_destination
    ).copy()
    adapter.update(
        {
            "id": adapter_id,
            "name": "ApplyParityAdapter",
            "signature": "object? ApplyParityAdapter(string symbolId, object? value)",
        }
    )
    public_api["members"].append(adapter)
    internalized["csharpDestination"] = adapter_id

    violations = api_validator()(public_api, ledger)

    assert "query example does not round-trip one canonical document" in violations
    assert "untyped parity adapter is forbidden" in violations
