"""Exercise release policy against real workflows and structural mutations."""

from __future__ import annotations

import pathlib
import shutil

import pytest
import yaml

from eng.parity.verify_workflows import verify

ROOT = pathlib.Path(__file__).parents[3]


@pytest.fixture
def repository(tmp_path: pathlib.Path) -> pathlib.Path:
    """Copy only the inputs owned by the policy checker."""
    shutil.copytree(ROOT / ".github/workflows", tmp_path / ".github/workflows")
    (tmp_path / "eng/tmux").mkdir(parents=True)
    for name in ("versions.json", "plan.py"):
        shutil.copyfile(ROOT / "eng/tmux" / name, tmp_path / "eng/tmux" / name)
    return tmp_path


def test_current_workflows_pass(repository: pathlib.Path) -> None:
    assert verify(repository) == []


def test_fsharp_reference_check_is_required(repository: pathlib.Path) -> None:
    """Generated F# API documentation must fail CI when it drifts."""
    path = repository / ".github/workflows/dotnet.yml"
    path.write_text(
        path.read_text().replace(
            "          uv run python eng/docs/render_api_reference.py --fsharp --check\n",
            "",
        )
    )

    assert any("F# API reference" in error for error in verify(repository))


def test_fsharp_snippet_check_is_required(repository: pathlib.Path) -> None:
    """Published F# snippets must not drift from their compiled example source."""
    path = repository / ".github/workflows/dotnet.yml"
    path.write_text(
        path.read_text().replace(
            "          uv run python eng/docs/sync_fsharp_snippets.py --check\n",
            "",
        )
    )

    assert any("F# snippets" in error for error in verify(repository))


@pytest.mark.parametrize(
    "removed",
    [
        "python3 eng/docs/verify_api_examples.py",
        "--packages artifacts/packages",
        '--output "${RUNNER_TEMP}/libtmux-api-examples"',
        "for framework in net8.0 net10.0; do",
        "-p:UsePackageReferences=true",
    ],
)
def test_packed_complete_programs_are_required(
    repository: pathlib.Path, removed: str
) -> None:
    """A workflow cannot skip a displayed program or substitute source references."""
    path = repository / ".github/workflows/dotnet.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step for step in document["jobs"]["build"]["steps"]
        if step.get("id") == "fsharp-packed-examples"
    )
    step["run"] = step["run"].replace(removed, "")
    path.write_text(yaml.safe_dump(document))

    assert any("execute every complete program" in error for error in verify(repository))


def test_fsdocs_reference_check_is_required(repository: pathlib.Path) -> None:
    """The generated member pages must be checked, not only built."""
    path = repository / ".github/workflows/dotnet.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step for step in document["jobs"]["build"]["steps"]
        if step.get("id") == "fsharp-fsdocs"
    )
    step["run"] = "true"
    path.write_text(yaml.safe_dump(document))

    assert any("fsharp-fsdocs must check" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("before", "after"),
    [
        ("-p:PublishTrimmed=true", "-p:PublishTrimmed=false"),
        (
            '            "${RUNNER_TEMP}/libtmux-fsharp-trimmed/${framework}/LibTmux.FSharp.AotSmoke"',
            '            echo "${RUNNER_TEMP}/libtmux-fsharp-trimmed/${framework}/LibTmux.FSharp.AotSmoke"',
        ),
    ],
)
def test_fsharp_trimmed_publish_is_required(
    repository: pathlib.Path, before: str, after: str
) -> None:
    """A non-trimmed or unexecuted artifact cannot satisfy deployment."""
    path = repository / ".github/workflows/dotnet.yml"
    source = path.read_text()
    assert before in source
    path.write_text(source.replace(before, after))

    assert any("fsharp-trimmed-smoke must publish and run" in error for error in verify(repository))


def test_fsharp_example_is_required_in_every_tmux_matrix_cell(
    repository: pathlib.Path,
) -> None:
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    steps = document["jobs"]["matrix"]["steps"]
    document["jobs"]["matrix"]["steps"] = [
        step for step in steps if step.get("id") != "fsharp-examples"
    ]
    path.write_text(yaml.safe_dump(document))

    assert any("fsharp-examples" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("job", "step_name", "removed"),
    [
        ("build", "Build F# examples for the tmux matrix", '-p:ExampleProgram="${program}"'),
        ("build", "Archive runnable assemblies", "examples/LibTmux.FSharp.Examples/bin/Release/programs"),
        ("matrix", "F# example", "Programs/*.fs"),
        ("matrix", "F# example", 'dotnet "examples/LibTmux.FSharp.Examples/bin/Release/programs/'),
        ("build", "Build C# programs for the tmux matrix", '-p:ExampleProgram="${program}"'),
        ("build", "Archive runnable assemblies", "examples/LibTmux.Examples/bin/Release/programs"),
        ("matrix", "C# complete programs", "Programs/*.cs"),
        ("matrix", "C# complete programs", 'dotnet "examples/LibTmux.Examples/bin/Release/programs/'),
    ],
)
def test_query_programs_survive_matrix_build_and_execution(
    repository: pathlib.Path, job: str, step_name: str, removed: str
) -> None:
    """Every displayed program must reach and run in the tmux matrix."""
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step for step in document["jobs"][job]["steps"]
        if step.get("name") == step_name
    )
    assert removed in step["run"]
    step["run"] = step["run"].replace(removed, "")
    path.write_text(yaml.safe_dump(document))

    expected = "every F# query program" if "F#" in step_name or "FSharp" in removed else "every complete C# program"
    assert any(expected in error for error in verify(repository))


def test_commented_dependencies_do_not_gate_publication(
    repository: pathlib.Path,
) -> None:
    path = repository / ".github/workflows/release.yml"
    path.write_text(
        path.read_text().replace(
            "needs: [dotnet, compatibility]",
            "needs: [validate] # needs: [dotnet, compatibility]",
        )
    )
    assert any("publish.needs" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("workflow", "keys", "value", "diagnostic"),
    [
        ("release", ("jobs", "publish", "needs"), ["dotnet"], "publish.needs"),
        ("dotnet-tmux", ("jobs", "matrix", "needs"), [], "matrix.needs"),
        ("release", ("jobs", "publish", "permissions"), "write-all", "permissions"),
        ("release", ("jobs", "publish", "if"), "always()", "publish.if"),
        (
            "release",
            ("jobs", "dotnet", "continue-on-error"),
            "true",
            "continue-on-error",
        ),
        (
            "release",
            ("jobs", "dotnet", "uses"),
            "./.github/workflows/other.yml",
            "dotnet.uses",
        ),
        ("dotnet", ("jobs", "gate", "needs"), ["build"], "gate.needs"),
        (
            "dotnet-tmux",
            ("jobs", "matrix", "strategy", "matrix"),
            {"tmux": ["3.7c"], "framework": ["net8.0", "net10.0"]},
            "take its legs from the plan",
        ),
        (
            "dotnet-tmux",
            ("jobs", "matrix", "strategy", "matrix"),
            "${{ fromJSON(needs.build.outputs.matrix) }}",
            "take its legs from the plan",
        ),
        (
            "dotnet-tmux",
            ("jobs", "rejected", "strategy", "matrix", "rejected-version"),
            ["3.2a"],
            "rejected must take its versions from the plan",
        ),
        ("dotnet-tmux", ("jobs", "rejected", "if"), "true", "rejected.if"),
        ("dotnet-tmux", ("jobs", "exact_3_7", "if"), "true", "exact_3_7.if"),
        ("dotnet-tmux", ("jobs", "advisory", "if"), "true", "advisory must run"),
        ("dotnet-tmux", ("jobs", "plan", "if"), "false", "plan.if"),
        (
            "dotnet-tmux",
            ("jobs", "plan", "steps", 1, "env", "SCOPE"),
            "full",
            "read the event and every input through env",
        ),
        (
            "dotnet-tmux",
            ("jobs", "plan", "outputs", "rejected"),
            "[]",
            "publish the rejected output",
        ),
        (
            "dotnet-tmux",
            ("on", "workflow_call", "inputs", "scope", "required"),
            "false",
            "require the scope input",
        ),
        (
            "dotnet-tmux",
            ("on", "workflow_dispatch", "inputs", "scope", "default"),
            "full",
            "default to edges",
        ),
        (
            "dotnet-tmux",
            ("on", "workflow_dispatch", "inputs", "scope", "options"),
            ["edges", "full"],
            "default to edges and offer every scope",
        ),
        ("release", ("jobs", "compatibility", "with", "scope"), "edges", "full scope"),
        ("release", ("jobs", "compatibility", "with"), {"scope": "custom"}, "full scope"),
        (
            "dotnet-tmux",
            ("jobs", "matrix", "strategy", "fail-fast"),
            "true",
            "fail-fast",
        ),
    ],
)
def test_weakened_policy_fails(
    repository: pathlib.Path,
    workflow: str,
    keys: tuple[str, ...],
    value: object,
    diagnostic: str,
) -> None:
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    target = document
    for key in keys[:-1]:
        target = target[key]
    target[keys[-1]] = value
    path.write_text(yaml.safe_dump(document, sort_keys=True))
    assert any(diagnostic in error for error in verify(repository))


@pytest.mark.parametrize(
    ("workflow", "keys", "diagnostic"),
    [
        ("release", ("jobs", "compatibility", "with"), "full scope"),
        ("dotnet-tmux", ("on", "workflow_call", "inputs"), "require the scope input"),
        ("dotnet-tmux", ("on", "workflow_dispatch", "inputs", "tmux-ref"), "offer the tmux-ref"),
        ("dotnet-tmux", ("on", "workflow_dispatch", "inputs", "dotnet"), "offer the dotnet"),
        ("dotnet-tmux", ("on", "workflow_dispatch", "inputs", "scope"), "offer the scope"),
        ("dotnet-tmux", ("jobs", "rejected", "if"), "rejected.if"),
        ("dotnet-tmux", ("jobs", "exact_3_7", "if"), "exact_3_7.if"),
        ("dotnet-tmux", ("jobs", "advisory", "if"), "advisory must run"),
        ("dotnet-tmux", ("jobs", "plan"), "missing required job"),
        ("dotnet-tmux", ("on", "workflow_dispatch", "inputs", "tmate"), "offer the tmate"),
        ("dotnet-tmux", ("jobs", "plan", "steps", 1, "env", "TMATE"), "through env"),
        ("dotnet-tmux", ("jobs", "matrix", "timeout-minutes"), "cap the job"),
    ],
)
def test_removed_policy_fails(
    repository: pathlib.Path,
    workflow: str,
    keys: tuple[str, ...],
    diagnostic: str,
) -> None:
    """A lane, an input or a release argument that goes missing is a finding."""
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    target = document
    for key in keys[:-1]:
        target = target[key]
    del target[keys[-1]]
    path.write_text(yaml.safe_dump(document, sort_keys=True))
    assert any(diagnostic in error for error in verify(repository))


def debug_steps(document: dict) -> list[dict]:
    """Return the two debugging-session steps of the tmux matrix job."""
    return [
        step
        for step in document["jobs"]["matrix"]["steps"]
        if str(step.get("uses", "")).startswith("mxschmitt/action-tmate@")
    ]


@pytest.mark.parametrize(
    ("change", "diagnostic"),
    [
        (lambda step: step.update(uses="mxschmitt/action-tmate@v3.24"), "pinned to a full commit SHA"),
        (lambda step: step.update(uses="mxschmitt/action-tmate@" + "g" * 40), "pinned to a full commit SHA"),
        (lambda step: step["with"].update({"limit-access-to-actor": "false"}), "limited to the actor"),
        (lambda step: step["with"].pop("limit-access-to-actor"), "limited to the actor"),
        (lambda step: step.pop("timeout-minutes"), "own time limit"),
        (
            lambda step: step.update({"if": "matrix.tmate == 'before-tests'"}),
            "runs only on a dispatch",
        ),
        (
            lambda step: step.update({"if": "github.event_name == 'workflow_dispatch'"}),
            "on the leg the plan chose",
        ),
    ],
)
def test_the_debugging_session_stays_pinned_limited_and_bounded(
    repository: pathlib.Path, change, diagnostic: str
) -> None:
    """The session is a way in to the runner, so each safeguard is checked."""
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    change(debug_steps(document)[0])
    path.write_text(yaml.safe_dump(document))

    assert any(diagnostic in error for error in verify(repository))


def test_a_debugging_session_cannot_be_dropped(repository: pathlib.Path) -> None:
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["jobs"]["matrix"]["steps"].remove(debug_steps(document)[1])
    path.write_text(yaml.safe_dump(document))

    assert any("before the tests and after a failure" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("workflow", "job_name"),
    [("release", "publish"), ("dotnet", "build"), ("dotnet-tmux", "rejected")],
)
def test_a_debugging_session_cannot_reach_another_job(
    repository: pathlib.Path, workflow: str, job_name: str
) -> None:
    """Publishing, releasing and documenting never hold a session open."""
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    session = {
        "uses": "mxschmitt/action-tmate@" + "a" * 40,
        "with": {"limit-access-to-actor": "true"},
        "timeout-minutes": "30",
        "if": "github.event_name == 'workflow_dispatch' && matrix.tmate == 'before-tests'",
    }
    document["jobs"][job_name]["steps"].append(session)
    path.write_text(yaml.safe_dump(document))

    assert any("belongs only in the tmux matrix" in error for error in verify(repository))


def test_a_debugging_session_cannot_reach_a_workflow_the_policy_does_not_read(
    repository: pathlib.Path,
) -> None:
    path = repository / ".github/workflows/docs.yml"
    path.write_text(path.read_text() + "      - uses: mxschmitt/action-tmate@" + "a" * 40 + "\n")

    assert any("docs.yml" in error and "belongs only" in error for error in verify(repository))


@pytest.mark.parametrize("workflow", ["dotnet", "dotnet-tmux"])
@pytest.mark.parametrize(
    ("group", "diagnostic"),
    [
        ("{name}-${{ github.ref }}", "add github.run_id"),
        ("{name}-${{ github.ref }}-${{ github.run_id }}", "add github.run_id"),
        (
            "{name}-${{ github.ref }}${{ github.event_name == 'push' && format('-{{0}}', github.run_id) || '' }}",
            "add github.run_id",
        ),
    ],
)
def test_a_dispatch_run_must_not_share_a_cancelling_group(
    repository: pathlib.Path, workflow: str, group: str, diagnostic: str
) -> None:
    """Two dispatches, or a dispatch and a push, must not cancel one another."""
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["concurrency"]["group"] = group.replace("{name}", workflow)
    path.write_text(yaml.safe_dump(document))

    assert any(f"{workflow}.concurrency" in error and diagnostic in error for error in verify(repository))


@pytest.mark.parametrize("workflow", ["dotnet", "dotnet-tmux"])
def test_a_superseded_push_is_still_cancelled(repository: pathlib.Path, workflow: str) -> None:
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["concurrency"]["cancel-in-progress"] = "false"
    path.write_text(yaml.safe_dump(document))

    assert any("still cancel a superseded push" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("quoted", "bare"),
    [
        ("default: 'off'", "default: off"),
        ("options: ['off', before-tests, on-failure]", "options: [off, before-tests, on-failure]"),
    ],
)
def test_a_dispatch_value_that_yaml_reads_as_a_boolean_is_refused(
    repository: pathlib.Path, quoted: str, bare: str
) -> None:
    """A bare off is false to GitHub, so a dispatch without tmate failed in the plan."""
    path = repository / ".github/workflows/dotnet-tmux.yml"
    source = path.read_text()
    assert quoted in source
    path.write_text(source.replace(quoted, bare))

    assert any("tmate: quote the values" in error for error in verify(repository))


def test_the_plan_and_the_workflow_agree_on_the_frameworks(
    repository: pathlib.Path,
) -> None:
    """A framework the plan offers must be one the workflow's legs are built for."""
    path = repository / "eng/tmux/plan.py"
    source = path.read_text()
    assert 'FRAMEWORKS = ("net8.0", "net10.0")' in source
    path.write_text(source.replace('("net8.0", "net10.0")', '("net8.0", "net10.0", "net9.0")'))

    assert any("framework matrix differs" in error for error in verify(repository))


def test_an_input_in_a_run_step_is_refused(repository: pathlib.Path) -> None:
    """An input reaches a shell only through env, never inside the script."""
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["jobs"]["plan"]["steps"][1]["run"] += '\necho "${{ inputs.tmux-ref }}"'
    path.write_text(yaml.safe_dump(document))

    assert any("through env" in error for error in verify(repository))


def test_the_aggregate_cannot_pass_a_planned_lane_that_was_skipped(
    repository: pathlib.Path,
) -> None:
    """A skipped lane that the plan asked for is a failure, not a pass."""
    path = repository / ".github/workflows/dotnet-tmux.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step
        for step in document["jobs"]["compatibility"]["steps"]
        if step.get("id") == "require-success"
    )
    step["if"] = step["if"].replace(
        "needs.exact_3_7.result != (needs.plan.outputs.exact_3_7 == 'true' && 'success' || 'skipped')",
        "needs.exact_3_7.result == 'failure'",
    )
    path.write_text(yaml.safe_dump(document))

    assert any("compatibility.require-success.if" in error for error in verify(repository))


def test_reformatting_and_key_order_are_irrelevant(repository: pathlib.Path) -> None:
    for path in (repository / ".github/workflows").glob("*.yml"):
        document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
        path.write_text(
            yaml.safe_dump(document, sort_keys=True, default_flow_style=True)
        )
    assert verify(repository) == []


def test_duplicate_keys_fail(repository: pathlib.Path) -> None:
    path = repository / ".github/workflows/release.yml"
    path.write_text(path.read_text() + "\npermissions: write-all\n")
    assert any("duplicate" in error for error in verify(repository))


@pytest.mark.parametrize("workflow,job_name,step_name", [
    ("dotnet-tmux", "matrix", "Integration tests"),
    ("dotnet-tmux", "matrix", "F# example"),
    ("dotnet-tmux", "build", "Build F# examples for the tmux matrix"),
    ("release", "validate", "Check the tag matches the version"),
    ("dotnet", "build", "F# formatting"),
    ("dotnet", "build", "F# unit tests (net8.0)"),
    ("dotnet", "build", "F# unit tests (net10.0)"),
    ("dotnet", "build", "F# package consumer"),
    ("dotnet", "build", "F# packed example console"),
    ("dotnet", "build", "F# ahead-of-time smoke test"),
    ("dotnet", "build", "F# trimmed smoke test"),
    ("dotnet", "build", "F# example console"),
    ("dotnet", "build", "F# member reference"),
])
@pytest.mark.parametrize("key,value", [("if", "false"), ("continue-on-error", "true")])
def test_required_steps_cannot_skip_or_forgive_failures(
    repository, workflow, job_name, step_name, key, value
):
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step for step in document["jobs"][job_name]["steps"]
        if step.get("name") == step_name
    )
    step[key] = value
    path.write_text(yaml.safe_dump(document))
    assert any(
        f"{workflow}.{job_name}" in error and key in error
        for error in verify(repository)
    )


@pytest.mark.parametrize("workflow,job_name", [
    ("dotnet", "gate"), ("dotnet-tmux", "compatibility"), ("dotnet-tmux", "build"),
])
def test_required_aggregate_and_producer_cannot_skip(repository, workflow, job_name):
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["jobs"][job_name]["if"] = "false"
    path.write_text(yaml.safe_dump(document))
    assert any(f"{workflow}.{job_name}.if" in error for error in verify(repository))


@pytest.mark.parametrize("workflow,job_name", [
    ("dotnet", "gate"), ("dotnet-tmux", "compatibility"),
])
@pytest.mark.parametrize("key,value", [
    ("run", "exit 0"), ("if", "false"), ("continue-on-error", "true"),
])
def test_aggregate_rejection_cannot_be_weakened(
    repository, workflow, job_name, key, value
):
    path = repository / f".github/workflows/{workflow}.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    step = next(
        step for step in document["jobs"][job_name]["steps"]
        if step.get("id") == "require-success"
    )
    step[key] = value
    path.write_text(yaml.safe_dump(document))
    assert any(
        f"{workflow}.{job_name}.require-success" in error
        for error in verify(repository)
    )


def test_aggregate_checks_every_declared_dependency(repository):
    path = repository / ".github/workflows/dotnet.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["jobs"]["gate"]["needs"].append("extra")
    path.write_text(yaml.safe_dump(document))
    assert any("gate.require-success.if" in error for error in verify(repository))


@pytest.mark.parametrize("key,value", [
    ("if", "github.event_name != 'pull_request'"), ("continue-on-error", "true"),
])
def test_the_windows_lane_cannot_skip_pull_requests_or_forgive_failures(
    repository, key, value
):
    path = repository / ".github/workflows/dotnet.yml"
    document = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
    document["jobs"]["windows"][key] = value
    path.write_text(yaml.safe_dump(document))
    assert any(f"dotnet.windows.{key}" in error for error in verify(repository))


RAW_APT_FIX = "run it through eng/apt-get.sh"


def add_run_step(repository: pathlib.Path, run: str, *, action: bool = False) -> str:
    """Write a workflow, or a composite action, whose one step runs ``run``."""
    indent = " " * (8 if action else 10)
    body = "".join(f"{indent}{line}\n" for line in run.splitlines())
    if action:
        path = repository / ".github/actions/setup/action.yml"
        path.parent.mkdir(parents=True)
        path.write_text(
            "name: setup\nruns:\n  using: composite\n  steps:\n"
            f"    - name: Install tmux\n      shell: bash\n      run: |\n{body}"
        )
    else:
        path = repository / ".github/workflows/extra.yml"
        path.write_text(
            "name: extra\non: push\njobs:\n  build:\n    runs-on: ubuntu-24.04\n"
            f"    steps:\n      - name: Install tmux\n        run: |\n{body}"
        )
    return path.relative_to(repository).as_posix()


@pytest.mark.parametrize(
    "run",
    [
        "sudo apt-get update",
        "sudo apt-get install --yes tmux",
        "sudo -E apt install tmux",
        "apt-get update",
        "set -e\nsudo apt-get update",
        "echo start && sudo apt-get update",
    ],
)
@pytest.mark.parametrize("action", [False, True])
def test_a_raw_apt_call_is_refused(
    repository: pathlib.Path, run: str, action: bool
) -> None:
    """The wrapper bounds a hung mirror only while every call goes through it."""
    where = add_run_step(repository, run, action=action)

    errors = [error for error in verify(repository) if RAW_APT_FIX in error]

    assert len(errors) == 1
    assert errors[0].startswith(f"{where}: ")
    assert "'Install tmux'" in errors[0]
    assert run.splitlines()[-1].strip() in errors[0]


@pytest.mark.parametrize(
    "run",
    [
        "./eng/apt-get.sh update",
        "./eng/apt-get.sh install --yes tmux",
        "# sudo apt-get update is wrapped by eng/apt-get.sh",
        'echo "the runner\'s apt tmux can be older"',
    ],
)
@pytest.mark.parametrize("action", [False, True])
def test_the_wrapper_and_prose_about_apt_pass(
    repository: pathlib.Path, run: str, action: bool
) -> None:
    add_run_step(repository, run, action=action)

    assert verify(repository) == []
