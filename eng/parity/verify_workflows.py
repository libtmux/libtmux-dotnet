# /// script
# requires-python = ">=3.10"
# dependencies = ["PyYAML>=6,<7"]
# ///
"""Validate the required CI dependency graph, permissions and coverage.

Actionlint owns workflow syntax and expressions. This checker owns the release
policy, using YAML values so comments cannot satisfy required dependencies.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import typing as t

import yaml

TARGET_FRAMEWORKS = {"net8.0", "net10.0"}
FSHARP_PROGRAM_LOOP = "for source in examples/LibTmux.FSharp.Examples/Programs/*.fs; do"


class WorkflowLoader(yaml.CBaseLoader):
    """Read scalars literally, including GitHub's YAML 1.2 `on` key."""

    def construct_mapping(self, node: yaml.MappingNode, deep: bool = False) -> dict:
        """Reject duplicate keys instead of silently replacing policy."""
        result = {}
        for key_node, value_node in node.value:
            key = self.construct_object(key_node, deep=deep)
            if key in result:
                raise ValueError(f"duplicate YAML key: {key}")
            result[key] = self.construct_object(value_node, deep=deep)
        return result


def names(value: object) -> set[str]:
    """Normalize a literal GitHub dependency or event list."""
    if isinstance(value, str):
        return {value}
    if isinstance(value, (list, dict)):
        return set(value)
    return set()


def verify(root: pathlib.Path) -> list[str]:
    """Return violations of the repository's required workflow policy."""
    errors: list[str] = []
    documents: dict[str, dict[str, t.Any]] = {}
    for name in ("dotnet", "dotnet-tmux", "release"):
        try:
            source = (root / f".github/workflows/{name}.yml").read_text(
                encoding="utf-8"
            )
            document = yaml.load(source, Loader=WorkflowLoader)
            if not isinstance(document, dict) or not isinstance(
                document.get("jobs"), dict
            ):
                raise TypeError("expected a workflow with jobs")
            documents[name] = document
        except (OSError, TypeError, ValueError, yaml.YAMLError) as error:
            errors.append(f"{name}.yml: {error}")
    if errors:
        return errors

    def require(condition: bool, message: str) -> None:
        if not condition:
            errors.append(message)

    def job(workflow: str, name: str) -> dict[str, t.Any]:
        value = documents[workflow]["jobs"].get(name)
        if not isinstance(value, dict):
            errors.append(f"{workflow}.{name}: missing required job")
            return {}
        require(
            value.get("continue-on-error", "false") == "false",
            f"{workflow}.{name}.continue-on-error must be false",
        )
        return value

    def needs(workflow: str, name: str, expected: set[str]) -> dict[str, t.Any]:
        value = job(workflow, name)
        require(
            expected <= names(value.get("needs")),
            f"{workflow}.{name}.needs must include {', '.join(sorted(expected))}",
        )
        return value

    def required_step(
        workflow: str,
        name: str,
        identifier: str,
        condition: str | None = None,
    ) -> dict[str, t.Any]:
        steps = [
            step for step in job(workflow, name).get("steps", [])
            if step.get("id") == identifier
        ]
        label = f"{workflow}.{name}.{identifier}"
        require(len(steps) == 1, f"{label}: missing required step")
        if len(steps) != 1:
            return {}
        step = steps[0]
        require(bool(step.get("run")), f"{label}: must execute a command")
        require(
            step.get("continue-on-error", "false") == "false",
            f"{label}.continue-on-error must be false",
        )
        if condition is None:
            require("if" not in step, f"{label}.if may not skip required execution")
        else:
            require(
                "".join(str(step.get("if", "")).split())
                == "".join(condition.split()),
                f"{label}.if must reject every unsuccessful dependency",
            )
        return step

    def aggregate(workflow: str, name: str, dependencies: set[str]) -> None:
        value = needs(workflow, name, dependencies)
        require(
            value.get("if") in {"always()", "${{ always() }}"},
            f"{workflow}.{name}.if must be always()",
        )
        checks = " || ".join(
            f"needs.{dependency}.result != 'success'"
            for dependency in sorted(names(value.get("needs")))
        )
        condition = "${{ always() && (" + checks + ") }}"
        step = required_step(workflow, name, "require-success", condition)
        require(
            step.get("run", "").strip() == "exit 1",
            f"{workflow}.{name}.require-success must fail the job",
        )

    for name, document in documents.items():
        require(
            document.get("permissions") == {"contents": "read"},
            f"{name}.permissions must default to contents: read",
        )
        for job_name, value in document["jobs"].items():
            if name == "release" and job_name == "publish":
                continue
            require(
                "permissions" not in value
                or value["permissions"] == {"contents": "read"},
                f"{name}.{job_name}.permissions may not elevate the workflow default",
            )

    for name in ("dotnet", "dotnet-tmux"):
        require(
            "workflow_call" in names(documents[name].get("on")),
            f"{name} must expose workflow_call",
        )

    aggregate("dotnet", "gate", {"build", "windows"})
    for name in ("build", "windows"):
        required = job("dotnet", name)
        require("if" not in required, f"dotnet.{name}.if may not skip a required build")
    for identifier in (
        "fsharp-format", "fsharp-unit-net8", "fsharp-unit-net10",
        "fsharp-package-consumer", "fsharp-sdk8-consumer", "fsharp-readme-quickstart",
        "fsharp-aot-smoke", "fsharp-examples",
        "fsharp-packed-examples", "fsharp-trimmed-smoke",
        "fsharp-fsdocs",
    ):
        required_step("dotnet", "build", identifier)

    examples = required_step("dotnet", "build", "fsharp-packed-examples")
    examples_run = examples.get("run", "")
    require(
        "for framework in net8.0 net10.0; do" in examples_run
        and "python3 eng/docs/verify_api_examples.py" in examples_run
        and "--packages artifacts/packages" in examples_run
        and '--output "${RUNNER_TEMP}/libtmux-api-examples"' in examples_run
        and examples_run.count("-p:UsePackageReferences=true") >= 2,
        "dotnet.build.fsharp-packed-examples must execute every complete program on both packed frameworks",
    )

    quickstart = required_step("dotnet", "build", "fsharp-readme-quickstart")
    quickstart_run = quickstart.get("run", "")
    # Counted in commands only, so a comment that names the flag cannot stand in for one.
    quickstart_commands = "\n".join(
        line for line in quickstart_run.splitlines() if not line.lstrip().startswith("#")
    )
    require(
        "python3 eng/docs/sync_fsharp_snippets.py --expect-output Quickstart" in quickstart_run
        and quickstart_commands.count("-p:UsePackageReferences=true") >= 3,
        "dotnet.build.fsharp-readme-quickstart must compare the packed program's output with the README",
    )

    sdk8 = required_step("dotnet", "build", "fsharp-sdk8-consumer")
    sdk8_run = sdk8.get("run", "")
    require(
        sdk8.get("env", {}).get("NUGET_PACKAGES")
        == "${{ runner.temp }}/libtmux-fsharp-sdk8-consumer"
        and sdk8.get("working-directory") == "tests/LibTmux.FSharp.Sdk8Consumer"
        and "dotnet --version | grep --quiet '^8\\.0\\.4'" in sdk8_run
        and "dotnet restore" in sdk8_run
        and "dotnet run" in sdk8_run,
        "dotnet.build.fsharp-sdk8-consumer must build and run with the .NET 8 SDK",
    )

    trimmed = required_step("dotnet", "build", "fsharp-trimmed-smoke")
    trimmed_run = trimmed.get("run", "")
    trimmed_project = "tests/LibTmux.FSharp.AotSmoke/LibTmux.FSharp.AotSmoke.fsproj"
    trimmed_output = "${RUNNER_TEMP}/libtmux-fsharp-trimmed/${framework}"
    require(
        trimmed.get("env", {}).get("NUGET_PACKAGES")
        == "${{ runner.temp }}/libtmux-fsharp-trimmed-smoke"
        and f"dotnet restore {trimmed_project}" in trimmed_run
        and f"dotnet publish {trimmed_project}" in trimmed_run
        and "--configfile tests/NuGet.config" in trimmed_run
        and "for framework in net8.0 net10.0; do" in trimmed_run
        and "--framework \"${framework}\"" in trimmed_run
        and trimmed_run.count("--runtime linux-x64") >= 2
        and "--no-restore" in trimmed_run
        and f'--output "{trimmed_output}"' in trimmed_run
        and trimmed_run.count("-p:PublishAot=false") >= 2
        and trimmed_run.count("-p:PublishTrimmed=true") >= 2
        and f'"{trimmed_output}/LibTmux.FSharp.AotSmoke"'
        in {line.strip() for line in trimmed_run.splitlines()},
        "dotnet.build.fsharp-trimmed-smoke must publish and run both packed trimmed frameworks",
    )

    fsdocs = required_step("dotnet", "build", "fsharp-fsdocs")
    require(
        "python3 eng/docs/sync_fsdocs_reference.py --check"
        in fsdocs.get("run", ""),
        "dotnet.build.fsharp-fsdocs must check the generated member reference",
    )

    fsharp_format = required_step("dotnet", "build", "fsharp-format")
    require(
        "examples" in fsharp_format.get("run", "").split(),
        "dotnet.build.fsharp-format must check F# examples",
    )
    document_steps = [
        step for step in job("dotnet", "build").get("steps", [])
        if step.get("name") == "Documents"
    ]
    require(len(document_steps) == 1, "dotnet.build: missing Documents step")
    if len(document_steps) == 1:
        require(
            "uv run python eng/docs/render_api_reference.py --fsharp --check"
            in document_steps[0].get("run", ""),
            "dotnet.build.Documents must check the F# API reference",
        )
        require(
            "uv run python eng/docs/sync_fsharp_snippets.py --check"
            in document_steps[0].get("run", ""),
            "dotnet.build.Documents must check F# snippets",
        )

    matrix = needs("dotnet-tmux", "matrix", {"build"})
    strategy = matrix.get("strategy", {})
    coverage = strategy.get("matrix", {})
    supported = set(
        json.loads((root / "eng/tmux/versions.json").read_text())["supported"]
    )
    require(
        names(coverage.get("tmux")) == supported,
        "supported tmux matrix differs from manifest",
    )
    require(
        names(coverage.get("framework")) == TARGET_FRAMEWORKS,
        "supported framework matrix differs",
    )
    require(not coverage.get("exclude"), "supported matrix exclusions are forbidden")
    require(
        strategy.get("fail-fast") == "false", "supported matrix fail-fast must be false"
    )
    require("if" not in matrix, "dotnet-tmux.matrix.if may not skip compatibility")
    producer = job("dotnet-tmux", "build")
    require("if" not in producer, "dotnet-tmux.build.if may not skip the producer")
    fsharp_build = required_step("dotnet-tmux", "build", "fsharp-examples-build")
    fsharp_project = "examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj"
    require(
        fsharp_build.get("run", "").count(fsharp_project) >= 2,
        "dotnet-tmux.build must restore and build the F# example",
    )
    require(
        FSHARP_PROGRAM_LOOP in fsharp_build.get("run", "")
        and '-p:ExampleProgram="${program}"' in fsharp_build.get("run", ""),
        "dotnet-tmux.build must compile every F# query program",
    )
    archives = [
        step for step in producer.get("steps", [])
        if step.get("name") == "Archive runnable assemblies"
    ]
    require(len(archives) == 1, "dotnet-tmux.build must archive runnable assemblies")
    if len(archives) == 1:
        archive_command = archives[0].get("run", "")
        require(
            "examples/LibTmux.FSharp.Examples/bin/Release/programs" in archive_command,
            "dotnet-tmux.build must archive every F# query program",
        )
        for framework in sorted(TARGET_FRAMEWORKS):
            require(
                f"examples/LibTmux.FSharp.Examples/bin/Release/{framework}"
                in archive_command,
                f"dotnet-tmux.build must archive the F# example for {framework}",
            )
    integration = required_step("dotnet-tmux", "matrix", "integration-tests")
    require(
        {**matrix.get("env", {}), **integration.get("env", {})}.get(
            "LIBTMUX_INTEGRATION_REQUIRED"
        ) == "1",
        "supported matrix must require integration execution",
    )
    fsharp_example = required_step("dotnet-tmux", "matrix", "fsharp-examples")
    example_env = {**matrix.get("env", {}), **fsharp_example.get("env", {})}
    require(
        example_env.get("LIBTMUX_TMUX") == "${{ steps.tmux.outputs.binary }}"
        and example_env.get("LIBTMUX_EXPECTED_TMUX_VERSION") == "${{ matrix.tmux }}"
        and example_env.get("MATRIX_FRAMEWORK") == "${{ matrix.framework }}",
        "dotnet-tmux.matrix.fsharp-examples must use the selected tmux and framework",
    )
    require(
        "LibTmux.FSharp.Examples/bin/Release/${MATRIX_FRAMEWORK}/LibTmux.FSharp.Examples.dll"
        in fsharp_example.get("run", "")
        and "LIBTMUX_EXPECTED_TMUX_VERSION" in fsharp_example.get("run", ""),
        "dotnet-tmux.matrix.fsharp-examples must verify and run the selected cell",
    )
    example_lines = {line.strip() for line in fsharp_example.get("run", "").splitlines()}
    require(
        FSHARP_PROGRAM_LOOP in fsharp_example.get("run", "")
        and 'printed="$(dotnet "examples/LibTmux.FSharp.Examples/bin/Release/programs/${program}/${MATRIX_FRAMEWORK}/LibTmux.FSharp.Examples.dll")"'
        in example_lines,
        "dotnet-tmux.matrix must execute every F# query program",
    )
    require(
        'printf \'%s\\n\' "${printed}" | python3 eng/docs/sync_fsharp_snippets.py --expect-output "${program}"'
        in example_lines,
        "dotnet-tmux.matrix must compare every F# program's output with its guide",
    )
    csharp_build = required_step("dotnet-tmux", "build", "csharp-examples-build")
    csharp_project = "examples/LibTmux.Examples/LibTmux.Examples.csproj"
    csharp_loop = "for source in examples/LibTmux.Examples/Programs/*.cs; do"
    require(
        csharp_build.get("run", "").count(csharp_project) >= 2
        and csharp_loop in csharp_build.get("run", "")
        and '-p:ExampleProgram="${program}"' in csharp_build.get("run", ""),
        "dotnet-tmux.build must restore and compile every complete C# program",
    )
    if len(archives) == 1:
        require(
            "examples/LibTmux.Examples/bin/Release/programs" in archives[0].get("run", ""),
            "dotnet-tmux.build must archive every complete C# program",
        )
    csharp_example = required_step("dotnet-tmux", "matrix", "csharp-examples")
    csharp_env = {**matrix.get("env", {}), **csharp_example.get("env", {})}
    require(
        csharp_env.get("LIBTMUX_TMUX") == "${{ steps.tmux.outputs.binary }}"
        and csharp_env.get("LIBTMUX_EXPECTED_TMUX_VERSION") == "${{ matrix.tmux }}"
        and csharp_env.get("MATRIX_FRAMEWORK") == "${{ matrix.framework }}",
        "dotnet-tmux.matrix.csharp-examples must use the selected tmux and framework",
    )
    require(
        csharp_loop in csharp_example.get("run", "")
        and "LIBTMUX_EXPECTED_TMUX_VERSION" in csharp_example.get("run", "")
        and 'dotnet "examples/LibTmux.Examples/bin/Release/programs/${program}/${MATRIX_FRAMEWORK}/LibTmux.Examples.dll"'
        in {line.strip() for line in csharp_example.get("run", "").splitlines()},
        "dotnet-tmux.matrix must execute every complete C# program",
    )
    aggregate("dotnet-tmux", "compatibility", {"build", "matrix"})

    release = documents["release"]
    triggers = release.get("on", {})
    require(
        isinstance(triggers, dict) and triggers.get("push", {}).get("tags") == ["v*"],
        "release must trigger on v* tags",
    )
    require(
        release.get("concurrency", {}).get("cancel-in-progress") == "false",
        "release must not cancel publication in progress",
    )
    for name in (
        "validate",
        "dotnet",
        "compatibility",
        "psmux-metadata",
        "psmux",
        "publish",
    ):
        required = job("release", name)
        require(
            "if" not in required,
            f"release.{name}.if may not bypass prerequisite success",
        )
    required_step("release", "validate", "validate-tag")
    for name, workflow in (("dotnet", "dotnet"), ("compatibility", "dotnet-tmux")):
        required = needs("release", name, {"validate"})
        require(
            required.get("uses") == f"./.github/workflows/{workflow}.yml",
            f"release.{name}.uses must run the same-commit {workflow} workflow",
        )
    needs("release", "psmux-metadata", {"validate"})
    psmux = needs("release", "psmux", {"validate", "psmux-metadata"})
    require(
        names(psmux.get("runs-on")) == {"self-hosted", "Windows", "X64", "psmux"},
        "release.psmux must use the provisioned Windows runner",
    )
    publish = needs("release", "publish", {"dotnet", "compatibility", "psmux"})
    require(
        publish.get("environment") == "nuget",
        "release.publish must use the nuget environment",
    )
    require(
        publish.get("permissions")
        == {
            "contents": "read",
            "id-token": "write",
            "attestations": "write",
        },
        "release.publish.permissions must grant only publication and attestation access",
    )
    return errors


def main(argv: list[str] | None = None) -> int:
    """Check release policy; actionlint separately checks workflow syntax."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2]
    )
    errors = verify(parser.parse_args(argv).root)
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
