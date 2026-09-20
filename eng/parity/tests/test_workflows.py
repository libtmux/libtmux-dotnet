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
    manifest = pathlib.Path("eng/tmux/versions.json")
    (tmp_path / manifest).parent.mkdir(parents=True)
    shutil.copyfile(ROOT / manifest, tmp_path / manifest)
    return tmp_path


def test_current_workflows_pass(repository: pathlib.Path) -> None:
    assert verify(repository) == []


def test_commented_dependencies_do_not_gate_publication(
    repository: pathlib.Path,
) -> None:
    path = repository / ".github/workflows/release.yml"
    path.write_text(
        path.read_text().replace(
            "needs: [dotnet, compatibility, psmux]",
            "needs: [validate] # needs: [dotnet, compatibility, psmux]",
        )
    )
    assert any("publish.needs" in error for error in verify(repository))


@pytest.mark.parametrize(
    ("workflow", "keys", "value", "diagnostic"),
    [
        ("release", ("jobs", "psmux", "needs"), ["validate"], "psmux.needs"),
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
            ("jobs", "matrix", "strategy", "matrix", "tmux"),
            ["3.7c"],
            "tmux matrix",
        ),
        (
            "dotnet-tmux",
            ("jobs", "matrix", "strategy", "matrix", "framework"),
            ["net10.0"],
            "framework matrix",
        ),
        (
            "dotnet-tmux",
            ("jobs", "matrix", "strategy", "matrix", "exclude"),
            [{"tmux": "3.7c"}],
            "matrix exclusions",
        ),
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
    ("release", "validate", "Check the tag matches the version"),
    ("dotnet", "build", "F# formatting"),
    ("dotnet", "build", "F# unit tests (net8.0)"),
    ("dotnet", "build", "F# unit tests (net10.0)"),
    ("dotnet", "build", "F# package consumer"),
    ("dotnet", "build", "F# packed example console"),
    ("dotnet", "build", "F# ahead-of-time smoke test"),
    ("dotnet", "build", "F# example console"),
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
    document["jobs"]["gate"]["needs"].append("macos")
    path.write_text(yaml.safe_dump(document))
    assert any("gate.require-success.if" in error for error in verify(repository))
