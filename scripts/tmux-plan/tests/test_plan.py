"""Prove the tmux plan picks what each event should run, and that it can fail.

Every rule has a check, and every check has a deliberate break: the rule is
replaced with its absence and the check has to notice. A check that passes
with its rule removed would prove nothing about the rule.
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import sys
import typing as t

import pytest

SCRIPT = pathlib.Path(__file__).parents[1] / "plan.py"
spec = importlib.util.spec_from_file_location("libtmux_tmux_plan", SCRIPT)
assert spec is not None and spec.loader is not None
plan_module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = plan_module
spec.loader.exec_module(plan_module)
plan: t.Any = plan_module

MANIFEST = plan.load_manifest()
SUPPORTED: list[str] = MANIFEST["supported"]
REJECTED: list[str] = MANIFEST["rejected"]
OLDEST, NEWEST = SUPPORTED[0], SUPPORTED[-1]
FRAMEWORKS = list(plan.FRAMEWORKS)


def cells(result: dict[str, t.Any]) -> list[dict[str, str]]:
    """Return the test legs of a plan."""
    return t.cast(list[dict[str, str]], result["matrix"]["include"])


def versions(result: dict[str, t.Any]) -> list[str]:
    """Return the distinct tmux values of a plan, in order."""
    return list(dict.fromkeys(each["tmux"] for each in cells(result)))


def run(event: str, **inputs: str) -> dict[str, t.Any]:
    """Plan one event against the repository's own manifest."""
    return t.cast(dict[str, t.Any], plan.plan(MANIFEST, event=event, **inputs))


def refused(match: str, event: str = "workflow_dispatch", **inputs: str) -> None:
    """Assert that a plan is refused with a message naming the problem."""
    with pytest.raises(plan.PlanError, match=match):
        run(event, **inputs)


# One check per rule. Each reads the plan module at call time so that the
# deliberate breaks further down can replace a rule and watch the check fail.


def check_push_runs_the_edges_only() -> None:
    result = run("push", ref="refs/heads/master")
    assert result["scope"] == "edges"
    assert versions(result) == [OLDEST, NEWEST]
    assert [each["framework"] for each in cells(result)] == FRAMEWORKS * 2
    assert (result["rejected"], result["exact_3_7"], result["advisory"]) == ([], False, False)


def check_pull_request_runs_the_edges_only() -> None:
    result = run("pull_request", ref="refs/pull/1/merge")
    assert result["scope"] == "edges"
    assert versions(result) == [OLDEST, NEWEST]
    assert (result["rejected"], result["exact_3_7"], result["advisory"]) == ([], False, False)


def check_schedule_runs_everything() -> None:
    result = run("schedule", ref="refs/heads/master")
    assert result["scope"] == "full"
    assert versions(result) == SUPPORTED
    assert result["rejected"] == REJECTED
    assert result["exact_3_7"] is True
    assert result["advisory"] is True


def check_a_tag_ref_runs_everything() -> None:
    result = run("push", ref="refs/tags/v0.1.0")
    assert result["scope"] == "full"
    assert versions(result) == SUPPORTED
    assert result["rejected"] == REJECTED


def check_the_release_call_runs_everything_without_master() -> None:
    result = run("push", ref="refs/heads/master", scope="full")
    assert result["scope"] == "full"
    assert versions(result) == SUPPORTED
    assert result["rejected"] == REJECTED
    assert result["exact_3_7"] is True
    assert result["advisory"] is False


def check_dispatch_defaults_to_the_edges() -> None:
    result = run("workflow_dispatch", ref="refs/heads/topic")
    assert result["scope"] == "edges"
    assert versions(result) == [OLDEST, NEWEST]


def check_dispatch_can_ask_for_everything() -> None:
    result = run("workflow_dispatch", ref="refs/heads/topic", scope="full")
    assert versions(result) == SUPPORTED
    assert result["rejected"] == REJECTED
    assert result["exact_3_7"] is True
    assert result["advisory"] is True


def check_custom_builds_the_named_refs() -> None:
    sha = "a" * 40
    result = run("workflow_dispatch", scope="custom", tmux_ref=f"{NEWEST}, 3.6b,master,{sha}")
    assert versions(result) == [NEWEST, "3.6b", "master", sha]
    kinds = {each["tmux"]: each["kind"] for each in cells(result)}
    assert kinds == {NEWEST: "release", "3.6b": "ref", "master": "ref", sha: "ref"}
    expected = {each["tmux"]: each["expected"] for each in cells(result)}
    assert expected == {NEWEST: NEWEST, "3.6b": "", "master": "", sha: ""}
    slashed = run("workflow_dispatch", scope="custom", tmux_ref="feature/x")
    assert [each["slug"] for each in cells(slashed)] == ["feature-x"] * len(FRAMEWORKS)
    assert (result["rejected"], result["exact_3_7"], result["advisory"]) == ([], False, False)


def check_a_custom_run_needs_refs() -> None:
    refused("needs tmux-ref", scope="custom")


def check_a_ref_list_needs_the_custom_scope() -> None:
    refused("only used with scope custom", tmux_ref="3.6b")
    refused("only used with scope custom", scope="full", tmux_ref="3.6b")


def check_a_scope_must_be_known() -> None:
    refused("scope must be one of", scope="everything")


def check_an_unknown_event_is_refused() -> None:
    refused("no scope is defined", event="issues")


BAD_REFS = [
    "3.6b;rm",
    "$(id)",
    "a b",
    "x`id`",
    "a" * 65,
    "--upload-pack=x",
    "-x",
    "a..b",
    "topic.lock",
    "topic/",
    "deadbeef",
    "é",
]


def check_every_ref_is_validated() -> None:
    for bad in BAD_REFS:
        with pytest.raises(plan.PlanError):
            run("workflow_dispatch", scope="custom", tmux_ref=f"3.8,{bad}")
    for good in ["3.8", "next-3.8", "feature/x_y.z", "a" * 40, "A-b_c.1"]:
        assert versions(run("workflow_dispatch", scope="custom", tmux_ref=good)) == [good]


def check_too_many_refs_are_refused() -> None:
    assert len(versions(run("workflow_dispatch", scope="custom", tmux_ref=",".join(f"v{n}" for n in range(8))))) == 8
    refused("fit one run", scope="custom", tmux_ref=",".join(f"v{n}" for n in range(9)))


def check_a_framework_input_narrows_the_stack() -> None:
    result = run("workflow_dispatch", dotnet=FRAMEWORKS[0])
    assert {each["framework"] for each in cells(result)} == {FRAMEWORKS[0]}
    assert versions(result) == [OLDEST, NEWEST]


def check_an_unknown_framework_is_refused() -> None:
    refused("target frameworks", dotnet="net6.0")


def check_one_leg_gets_the_debug_session() -> None:
    result = run(
        "workflow_dispatch",
        scope="custom",
        tmux_ref="master",
        dotnet=FRAMEWORKS[0],
        tmate="on-failure",
    )
    assert [each["tmate"] for each in cells(result)] == ["on-failure"]


def check_no_debug_session_by_default() -> None:
    result = run("workflow_dispatch", scope="custom", tmux_ref="master", dotnet=FRAMEWORKS[0])
    assert [each["tmate"] for each in cells(result)] == ["off"]


def check_several_legs_cannot_share_a_debug_session() -> None:
    refused("exactly one leg", tmate="before-tests")
    refused("exactly one leg", scope="full", tmate="before-tests")
    refused("exactly one leg", scope="custom", tmux_ref="3.8,master", tmate="before-tests")
    refused("exactly one leg", scope="custom", tmux_ref="master", tmate="before-tests")


def check_a_debug_session_needs_a_dispatch() -> None:
    refused(
        "only available on a workflow_dispatch",
        event="push",
        scope="custom",
        tmux_ref="master",
        dotnet=FRAMEWORKS[0],
        tmate="on-failure",
    )


def check_a_debug_mode_must_be_known() -> None:
    refused("tmate must be one of", scope="custom", tmux_ref="master", tmate="always")


def check_the_job_count_adds_up() -> None:
    # The plan, the producer of the assemblies and the aggregate.
    fixed = 3
    edges = run("push", ref="refs/heads/master")
    assert edges["expected_jobs"] == fixed + 2 * len(FRAMEWORKS)
    full = run("schedule", ref="refs/heads/master")
    assert full["expected_jobs"] == (
        fixed + len(SUPPORTED) * len(FRAMEWORKS) + len(REJECTED) + 1 + 1
    )
    release = run("push", ref="refs/tags/v1")
    assert release["expected_jobs"] == full["expected_jobs"] - 1


def check_the_manifest_alone_decides_the_versions() -> None:
    other = {"supported": ["9.1", "9.2", "9.3"], "rejected": ["9.0"], "transition": "9.2"}
    edges = plan.plan(other, event="push", ref="refs/heads/master")
    assert versions(edges) == ["9.1", "9.3"]
    full = plan.plan(other, event="schedule")
    assert versions(full) == ["9.1", "9.2", "9.3"]
    assert full["rejected"] == ["9.0"]
    assert {each["kind"] for each in cells(full)} == {"release"}
    assert plan.plan(other, event="workflow_dispatch", scope="custom", tmux_ref="9.2")["matrix"][
        "include"
    ][0]["kind"] == "release"


def check_the_matrix_is_never_empty() -> None:
    with pytest.raises(plan.PlanError, match="no test leg"):
        plan.plan({"supported": [], "rejected": []}, event="push", ref="refs/heads/master")


def check_a_single_release_is_one_edge() -> None:
    only = {"supported": ["3.8"], "rejected": []}
    assert versions(plan.plan(only, event="push", ref="refs/heads/master")) == ["3.8"]


RULES = {
    name: getattr(plan_module, name)
    for name in ("resolve_scope", "select_lanes", "assign_tmate", "cell", "check_ref")
}


def original(name: str) -> t.Any:
    """Return a rule as the plan module defines it, before any break."""
    return RULES[name]


def resolving(event: str | None = None, ref: str | None = None, scope: str | None = None) -> t.Any:
    """Build a scope rule that sees some of its input replaced."""
    real = original("resolve_scope")

    def broken(got_event: str, got_ref: str, got_scope: str) -> str:
        return t.cast(
            str,
            real(
                got_event if event is None else event,
                got_ref if ref is None else ref,
                got_scope if scope is None else scope,
            ),
        )

    return broken


def laning(scope: str | None = None, event: str | None = None, nothing: bool = False) -> t.Any:
    """Build a lane rule that sees some of its input replaced."""
    real = original("select_lanes")

    def broken(got_scope: str, got_event: str, rejected: list[str]) -> dict[str, t.Any]:
        return t.cast(
            dict[str, t.Any],
            real(
                "edges" if nothing else (got_scope if scope is None else scope),
                got_event if event is None else event,
                rejected,
            ),
        )

    return broken


def no_debug_session(*arguments: t.Any) -> None:
    """Remove the debug-session rule altogether."""


def debug_session_everywhere(
    event: str, mode: str, got_cells: list[dict[str, str]], lanes: dict[str, t.Any]
) -> None:
    """Hand the debug session to every leg instead of counting them."""
    for each in got_cells:
        each["tmate"] = mode


def debug_session_without_event(
    event: str, mode: str, got_cells: list[dict[str, str]], lanes: dict[str, t.Any]
) -> None:
    """Hand out the debug session as though every event were a dispatch."""
    original("assign_tmate")("workflow_dispatch", mode, got_cells, lanes)


def release_cell(tmux: str, framework: str, release: bool) -> dict[str, str]:
    """Take every ref for a release."""
    return t.cast(dict[str, str], original("cell")(tmux, framework, True))


def every_framework(requested: str) -> list[str]:
    """Ignore the framework input."""
    return list(plan.FRAMEWORKS)


def unchecked_frameworks(requested: str) -> list[str]:
    """Accept any framework name."""
    return plan.split_list(requested) or list(plan.FRAMEWORKS)


def unchecked_refs(tmux_ref: str) -> list[str]:
    """Plan a custom run without demanding a ref."""
    return [original("check_ref")(each) for each in plan.split_list(tmux_ref)]


def hard_coded(supported: list[str]) -> list[str]:
    """Write the edges into the plan."""
    return ["3.3a", "3.8"]


def every_version(supported: list[str]) -> list[str]:
    """Take the whole table for the edges."""
    return list(supported)


def anything(item: str) -> str:
    """Accept any ref."""
    return item


def ignored(*arguments: t.Any) -> None:
    """Remove a rule that only refuses."""


def unchecked_event(event: str, ref: str, scope: str) -> str:
    """Give an unknown event the edges."""
    return "edges"


DELIBERATE_BREAKS: list[tuple[str, str, t.Any, t.Callable[[], None]]] = [
    (
        "the edges rule takes every version",
        "edge_versions",
        every_version,
        check_push_runs_the_edges_only,
    ),
    (
        "the edges are written into the plan",
        "edge_versions",
        hard_coded,
        check_the_manifest_alone_decides_the_versions,
    ),
    (
        "a pull request runs the full scope",
        "resolve_scope",
        resolving(event="schedule"),
        check_pull_request_runs_the_edges_only,
    ),
    (
        "a schedule runs only the edges",
        "resolve_scope",
        resolving(event="push"),
        check_schedule_runs_everything,
    ),
    (
        "a tag ref is not full scope",
        "resolve_scope",
        resolving(ref="refs/heads/master"),
        check_a_tag_ref_runs_everything,
    ),
    (
        "the scope input is ignored",
        "resolve_scope",
        resolving(scope=""),
        check_the_release_call_runs_everything_without_master,
    ),
    (
        "a dispatch ignores the scope it was given",
        "resolve_scope",
        resolving(scope=""),
        check_dispatch_can_ask_for_everything,
    ),
    (
        "a dispatch does not default to the edges",
        "resolve_scope",
        resolving(scope="full"),
        check_dispatch_defaults_to_the_edges,
    ),
    (
        "an unknown scope is accepted",
        "resolve_scope",
        resolving(scope="edges"),
        check_a_scope_must_be_known,
    ),
    (
        "an unknown event is accepted",
        "resolve_scope",
        unchecked_event,
        check_an_unknown_event_is_refused,
    ),
    (
        "the edges run the refusal lanes",
        "select_lanes",
        laning(scope="full", event="schedule"),
        check_push_runs_the_edges_only,
    ),
    (
        "the full scope drops its lanes",
        "select_lanes",
        laning(nothing=True),
        check_schedule_runs_everything,
    ),
    (
        "a release call gains the master lane",
        "select_lanes",
        laning(event="schedule"),
        check_the_release_call_runs_everything_without_master,
    ),
    (
        "a custom ref is taken for a release",
        "cell",
        release_cell,
        check_custom_builds_the_named_refs,
    ),
    (
        "a custom run without refs is planned",
        "custom_refs",
        unchecked_refs,
        check_a_custom_run_needs_refs,
    ),
    (
        "refs the scope ignores are accepted",
        "check_scope_inputs",
        ignored,
        check_a_ref_list_needs_the_custom_scope,
    ),
    (
        "a ref is not validated",
        "check_ref",
        anything,
        check_every_ref_is_validated,
    ),
    (
        "the ref count is unlimited",
        "MAX_REFS",
        1000,
        check_too_many_refs_are_refused,
    ),
    (
        "the framework input is ignored",
        "select_frameworks",
        every_framework,
        check_a_framework_input_narrows_the_stack,
    ),
    (
        "an unknown framework is accepted",
        "select_frameworks",
        unchecked_frameworks,
        check_an_unknown_framework_is_refused,
    ),
    (
        "no leg gets the debug session",
        "assign_tmate",
        no_debug_session,
        check_one_leg_gets_the_debug_session,
    ),
    (
        "every leg gets the debug session",
        "assign_tmate",
        debug_session_everywhere,
        check_several_legs_cannot_share_a_debug_session,
    ),
    (
        "the debug session runs outside a dispatch",
        "assign_tmate",
        debug_session_without_event,
        check_a_debug_session_needs_a_dispatch,
    ),
    (
        "an unknown debug mode is accepted",
        "assign_tmate",
        no_debug_session,
        check_a_debug_mode_must_be_known,
    ),
    (
        "the job count omits the fixed jobs",
        "FIXED_JOBS",
        0,
        check_the_job_count_adds_up,
    ),
    (
        "an empty matrix is emitted",
        "require_cells",
        ignored,
        check_the_matrix_is_never_empty,
    ),
]


ALL_CHECKS = [
    check_push_runs_the_edges_only,
    check_pull_request_runs_the_edges_only,
    check_schedule_runs_everything,
    check_a_tag_ref_runs_everything,
    check_the_release_call_runs_everything_without_master,
    check_dispatch_defaults_to_the_edges,
    check_dispatch_can_ask_for_everything,
    check_custom_builds_the_named_refs,
    check_a_custom_run_needs_refs,
    check_a_ref_list_needs_the_custom_scope,
    check_a_scope_must_be_known,
    check_an_unknown_event_is_refused,
    check_every_ref_is_validated,
    check_too_many_refs_are_refused,
    check_a_framework_input_narrows_the_stack,
    check_an_unknown_framework_is_refused,
    check_one_leg_gets_the_debug_session,
    check_no_debug_session_by_default,
    check_several_legs_cannot_share_a_debug_session,
    check_a_debug_session_needs_a_dispatch,
    check_a_debug_mode_must_be_known,
    check_the_job_count_adds_up,
    check_the_manifest_alone_decides_the_versions,
    check_the_matrix_is_never_empty,
    check_a_single_release_is_one_edge,
]


@pytest.mark.parametrize("check", ALL_CHECKS, ids=lambda each: each.__name__)
def test_the_plan_follows_its_rules(check: t.Callable[[], None]) -> None:
    check()


@pytest.mark.parametrize(
    ("description", "attribute", "replacement", "check"),
    DELIBERATE_BREAKS,
    ids=[each[0] for each in DELIBERATE_BREAKS],
)
def test_a_check_fails_when_its_rule_is_removed(
    monkeypatch: pytest.MonkeyPatch,
    description: str,
    attribute: str,
    replacement: t.Any,
    check: t.Callable[[], None],
) -> None:
    check()
    monkeypatch.setattr(plan_module, attribute, replacement)
    with pytest.raises((AssertionError, pytest.fail.Exception, plan.PlanError)):
        check()


def test_every_check_has_a_deliberate_break() -> None:
    broken = {each[3] for each in DELIBERATE_BREAKS}
    unbroken = [check.__name__ for check in ALL_CHECKS if check not in broken]
    assert unbroken == [
        "check_no_debug_session_by_default",
        "check_a_single_release_is_one_edge",
    ]


def test_the_outputs_are_single_line_json(tmp_path: pathlib.Path) -> None:
    output = tmp_path / "output"
    summary = tmp_path / "summary"
    status = plan.main(
        [
            "--event",
            "push",
            "--ref",
            "refs/heads/master",
            "--github-output",
            str(output),
            "--summary",
            str(summary),
        ]
    )

    assert status == 0
    lines = output.read_text(encoding="utf-8").splitlines()
    values = dict(line.split("=", 1) for line in lines)
    assert list(values) == ["scope", "matrix", "rejected", "exact_3_7", "advisory", "expected_jobs"]
    assert len(lines) == len(values)
    assert json.loads(values["matrix"])["include"] == cells(run("push", ref="refs/heads/master"))
    assert json.loads(values["rejected"]) == []
    assert (values["exact_3_7"], values["advisory"]) == ("false", "false")
    assert values["expected_jobs"] == str(3 + 2 * len(FRAMEWORKS))
    text = summary.read_text(encoding="utf-8")
    assert f"tmux {OLDEST} on {FRAMEWORKS[0]}" in text
    assert values["expected_jobs"] in text


def test_a_refused_plan_exits_nonzero_and_writes_nothing(
    tmp_path: pathlib.Path, capsys: pytest.CaptureFixture[str]
) -> None:
    output = tmp_path / "output"
    status = plan.main(
        ["--event", "workflow_dispatch", "--scope", "custom", "--github-output", str(output)]
    )

    assert status == 1
    assert "needs tmux-ref" in capsys.readouterr().err
    assert not output.exists()
