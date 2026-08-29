"""Contract tests for the C# capability model and the recorded deltas."""

from __future__ import annotations

import json
import pathlib
import runpy
import typing as t


def load_verifier() -> dict[str, t.Any]:
    """Load the capability verifier without importing a package."""
    return runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "verify_capabilities.py")
    )


def checked_in_source() -> str:
    """Return the checked-in capability interval source."""
    return t.cast(
        pathlib.Path,
        load_verifier()["MODEL_PATH"],
    ).read_text(encoding="utf-8")


def checked_in_document() -> dict[str, t.Any]:
    """Return the checked-in version-delta document."""
    path = t.cast(pathlib.Path, load_verifier()["DELTAS_PATH"])
    return t.cast(dict[str, t.Any], json.loads(path.read_text(encoding="utf-8")))


def test_checked_in_capability_model_matches_the_recorded_deltas() -> None:
    """Keep the intervals the library ships and the version matrix in step."""
    namespace = load_verifier()
    violations = namespace["validate"](
        checked_in_source(),
        namespace["referenced_capabilities"](namespace["SOURCE_ROOT"]),
        checked_in_document(),
    )

    assert violations == []


def test_capability_without_a_recorded_delta_is_rejected() -> None:
    """Reject a version difference nobody has to prove on a real server."""
    namespace = load_verifier()
    source = checked_in_source()

    violations = namespace["validate"](
        source + '\n"invented_capability_name"\n',
        {},
        checked_in_document(),
    )

    assert violations == [
        "capability is declared but not recorded: invented_capability_name"
    ]


def test_recorded_delta_without_a_capability_is_rejected() -> None:
    """Reject a recorded gate the library cannot ever consult."""
    namespace = load_verifier()
    document = checked_in_document()
    document["capabilities"].append({"capability": "recorded_only_capability"})

    violations = namespace["validate"](checked_in_source(), {}, document)

    assert violations == [
        "capability is recorded but not declared: recorded_only_capability"
    ]


def test_gate_naming_an_unknown_capability_is_rejected() -> None:
    """Reject a gate whose name the model does not carry."""
    namespace = load_verifier()

    violations = namespace["validate"](
        checked_in_source(),
        {"misspelled_capability": {"Pane.Operations.cs"}},
        checked_in_document(),
    )

    assert violations == [
        "version gate names an unknown capability: misspelled_capability "
        "(Pane.Operations.cs)"
    ]


def test_every_gate_in_the_library_names_a_declared_capability() -> None:
    """Read the real gates rather than trusting a curated list of them."""
    namespace = load_verifier()
    references = namespace["referenced_capabilities"](namespace["SOURCE_ROOT"])
    declared = namespace["declared_capabilities"](checked_in_source())

    assert references
    assert set(references) <= declared


def test_the_dollar_escape_gate_is_read_from_the_option_scopes() -> None:
    """Name the gate whose absence from the matrix this check was added for."""
    namespace = load_verifier()
    references = namespace["referenced_capabilities"](namespace["SOURCE_ROOT"])

    assert references["option_dollar_double_escape"] == {
        "Pane.Options.cs",
        "Server.Options.cs",
        "Session.Options.cs",
        "Window.Options.cs",
    }


def test_interval_boundary_drift_is_rejected() -> None:
    """Reject a source boundary that disagrees with the real-server ledger."""
    namespace = load_verifier()
    source = checked_in_source().replace(
        "Add(intervals, Added37, version37);",
        "Add(intervals, Added37, version36);",
    )

    violations = namespace["validate"](
        source,
        namespace["referenced_capabilities"](namespace["SOURCE_ROOT"]),
        checked_in_document(),
    )

    assert (
        "capability interval differs from recorded delta: new_pane_command "
        "(model ('3.6', None), ledger ('3.7', None))"
    ) in violations


def test_every_recorded_capability_names_a_proof_that_exists() -> None:
    """Resolve the proofs against the tests rather than their spelling."""
    namespace = load_verifier()
    members = namespace["declared_test_members"](namespace["TESTS_ROOT"])

    assert namespace["unproven_capabilities"](checked_in_document(), members) == []


def test_capability_naming_a_proof_that_was_never_written_is_rejected() -> None:
    """Reject a row that reads as proven while nothing runs for it."""
    namespace = load_verifier()
    document = checked_in_document()
    proven = document["capabilities"][0]
    path = proven["namedRealServerTest"].split("::")[0]
    proven["namedRealServerTest"] = f"{path}::NeverWritten"

    violations = namespace["unproven_capabilities"](
        document,
        namespace["declared_test_members"](namespace["TESTS_ROOT"]),
    )

    expected = (
        "capability names a proof that is not there: "
        f"{proven['capability']} ({path}::NeverWritten)"
    )
    assert violations == [expected]
