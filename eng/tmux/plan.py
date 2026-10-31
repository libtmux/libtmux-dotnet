#!/usr/bin/env python3
"""Plan which tmux versions, frameworks and lanes a dotnet-tmux run covers.

Every pull request and push used to run the whole version table on both
frameworks, plus the refusal lanes, which is slow and mostly repeats itself:
the oldest and newest release bracket what the rest of the table does. This
script is the one place that decides what a run covers, so the workflow
carries no version list of its own and `eng/tmux/versions.json` stays the
source of truth.

Scopes:

- `edges`: the oldest and newest supported release. The default for a push, a
  pull request and a dispatch.
- `full`: the whole table, the refusal lanes, the lane for the one release
  that needs exact-version tests, and the tmux master lane where the event
  allows it. Scheduled runs, tag refs, `release.yml`, and a dispatch that asks.
- `custom`: only the tmux versions, tags, branches or commits a dispatch names.

A scope input wins over the event, because a workflow called by
`release.yml` sees the caller's event (a tag push) and the caller passes
`scope: full`.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys
import typing as t

MANIFEST = pathlib.Path(__file__).with_name("versions.json")

# The frameworks the integration assemblies are built for. The job that builds
# them compiles both whatever is selected here.
FRAMEWORKS = ("net8.0", "net10.0")

SCOPES = ("edges", "full", "custom")
TMATE_MODES = ("off", "before-tests", "on-failure")

# Every item of `tmux-ref` is checked against this before it reaches a shell
# or a git command.
REF_PATTERN = re.compile(r"^[A-Za-z0-9._/-]{1,64}$")
FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
ABBREVIATED_SHA = re.compile(r"^[0-9a-f]{7,39}$")

# A mistyped list should fail rather than fan out into a huge matrix.
MAX_REFS = 8

# Jobs every run has besides the test legs and lanes: the plan, the producer
# of the integration assemblies and the aggregate that gates on the rest.
FIXED_JOBS = 3


class PlanError(ValueError):
    """The inputs name a run that cannot or must not happen."""


def load_manifest(path: pathlib.Path = MANIFEST) -> dict[str, t.Any]:
    """Read the version manifest the plan is measured against."""
    with path.open(encoding="utf-8") as handle:
        return t.cast(dict[str, t.Any], json.load(handle))


def split_list(value: str) -> list[str]:
    """Split a comma-separated input, dropping blanks and repeats."""
    items: list[str] = []
    for each in value.split(","):
        item = each.strip()
        if item and item not in items:
            items.append(item)
    return items


def resolve_scope(event: str, ref: str, scope: str) -> str:
    """Decide how much of the table a run covers."""
    if scope:
        if scope not in SCOPES:
            raise PlanError(f"scope must be one of {', '.join(SCOPES)}, not {scope!r}")
        return scope
    if event == "schedule":
        return "full"
    if ref.startswith("refs/tags/"):
        return "full"
    if event in ("push", "pull_request", "workflow_dispatch"):
        return "edges"
    raise PlanError(f"no scope is defined for the {event!r} event")


def edge_versions(supported: list[str]) -> list[str]:
    """Pick the oldest and the newest supported release."""
    return list(dict.fromkeys([*supported[:1], *supported[-1:]]))


def check_scope_inputs(scope: str, tmux_ref: str) -> None:
    """Refuse a list of refs that the scope would silently ignore."""
    if tmux_ref and scope != "custom":
        raise PlanError("tmux-ref is only used with scope custom")


def require_cells(cells: list[dict[str, str]]) -> None:
    """Refuse a plan with no test leg, which GitHub would reject anyway."""
    if not cells:
        raise PlanError("the plan has no test leg")


def select_frameworks(requested: str) -> list[str]:
    """Narrow the target frameworks, or keep both when none is asked for."""
    chosen = split_list(requested)
    for framework in chosen:
        if framework not in FRAMEWORKS:
            raise PlanError(
                f"dotnet must name target frameworks from {', '.join(FRAMEWORKS)}, "
                f"not {framework!r}"
            )
    return chosen or list(FRAMEWORKS)


def check_ref(item: str) -> str:
    """Refuse a tmux ref that is malformed or could be read as an option."""
    if not REF_PATTERN.fullmatch(item):
        raise PlanError(f"tmux-ref item {item!r} must match {REF_PATTERN.pattern}")
    if item.startswith("-") or ".." in item or item.endswith((".lock", "/")):
        raise PlanError(f"tmux-ref item {item!r} is not a usable git ref")
    if ABBREVIATED_SHA.fullmatch(item):
        raise PlanError(
            f"tmux-ref item {item!r} looks like an abbreviated commit; "
            "give the full 40-hex SHA"
        )
    return item


def custom_refs(tmux_ref: str) -> list[str]:
    """Validate the refs a custom run was asked to build."""
    items = split_list(tmux_ref)
    if not items:
        raise PlanError("scope custom needs tmux-ref, a comma-separated list of tmux refs")
    if len(items) > MAX_REFS:
        raise PlanError(f"tmux-ref names {len(items)} refs; at most {MAX_REFS} fit one run")
    return [check_ref(item) for item in items]


def cell(tmux: str, framework: str, release: bool) -> dict[str, str]:
    """Describe one test leg.

    A release is built through the closed list in `build-version.sh` and its
    `tmux -V` must match. Anything else is resolved to a commit and built from
    source, and has no version to assert.
    """
    return {
        "tmux": tmux,
        "framework": framework,
        "kind": "release" if release else "ref",
        "expected": tmux if release else "",
        # Artifact names cannot hold a slash, which a branch name can.
        "slug": tmux.replace("/", "-"),
        "tmate": "off",
    }


def select_lanes(scope: str, event: str, rejected: list[str]) -> dict[str, t.Any]:
    """Pick the lanes that run beside the matrix."""
    full = scope == "full"
    return {
        "rejected": list(rejected) if full else [],
        "exact_3_7": full,
        # tmux master moves independently of this library, so it is tracked on
        # a schedule or by hand and never on a push or pull request. A release
        # reaches this workflow as a call whose event is the tag push.
        "advisory": full and event not in ("push", "pull_request"),
    }


def assign_tmate(
    event: str, mode: str, cells: list[dict[str, str]], lanes: dict[str, t.Any]
) -> None:
    """Give exactly one leg the debug session, or fail."""
    if mode not in TMATE_MODES:
        raise PlanError(f"tmate must be one of {', '.join(TMATE_MODES)}, not {mode!r}")
    if mode == "off":
        return
    if event != "workflow_dispatch":
        raise PlanError("tmate is only available on a workflow_dispatch run")
    others = len(lanes["rejected"]) + int(lanes["exact_3_7"]) + int(lanes["advisory"])
    if len(cells) + others != 1:
        raise PlanError(
            f"tmate needs exactly one leg and this run has {len(cells) + others}; "
            "use scope custom with one tmux-ref and one dotnet framework"
        )
    cells[0]["tmate"] = mode


def plan(
    manifest: dict[str, t.Any],
    *,
    event: str,
    ref: str = "",
    scope: str = "",
    tmux_ref: str = "",
    dotnet: str = "",
    tmate: str = "",
) -> dict[str, t.Any]:
    """Return the matrix, the lanes and the job count for one run."""
    supported: list[str] = manifest["supported"]
    chosen = resolve_scope(event, ref, scope)
    check_scope_inputs(chosen, tmux_ref)

    frameworks = select_frameworks(dotnet)
    if chosen == "custom":
        versions = custom_refs(tmux_ref)
    elif chosen == "full":
        versions = list(supported)
    else:
        versions = edge_versions(supported)

    cells = [
        cell(version, framework, version in supported)
        for version in versions
        for framework in frameworks
    ]
    require_cells(cells)
    lanes = select_lanes(chosen, event, manifest["rejected"])
    assign_tmate(event, tmate or "off", cells, lanes)

    expected = (
        FIXED_JOBS
        + len(cells)
        + len(lanes["rejected"])
        + int(lanes["exact_3_7"])
        + int(lanes["advisory"])
    )
    return {
        "scope": chosen,
        "matrix": {"include": cells},
        "rejected": lanes["rejected"],
        "exact_3_7": lanes["exact_3_7"],
        "advisory": lanes["advisory"],
        "expected_jobs": expected,
    }


def summary(result: dict[str, t.Any]) -> str:
    """Describe a plan for the job summary."""
    cells = result["matrix"]["include"]
    lines = [
        f"### tmux plan: {result['scope']}",
        "",
        f"Expected jobs in this workflow: {result['expected_jobs']}",
        "",
    ]
    lines += [f"- tmux {each['tmux']} on {each['framework']} ({each['kind']})" for each in cells]
    lines += [f"- tmux {each} is refused" for each in result["rejected"]]
    if result["exact_3_7"]:
        lines.append("- tmux 3.7 exactly")
    if result["advisory"]:
        lines.append("- tmux master (advisory)")
    return "\n".join(lines) + "\n"


def outputs(result: dict[str, t.Any]) -> dict[str, str]:
    """Flatten a plan into single-line workflow outputs."""
    compact = {"separators": (",", ":")}
    return {
        "scope": result["scope"],
        "matrix": json.dumps(result["matrix"], **compact),
        "rejected": json.dumps(result["rejected"], **compact),
        "exact_3_7": "true" if result["exact_3_7"] else "false",
        "advisory": "true" if result["advisory"] else "false",
        "expected_jobs": str(result["expected_jobs"]),
    }


def main(argv: list[str] | None = None) -> int:
    """Print the plan, and write it where a workflow can read it."""
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--manifest", type=pathlib.Path, default=MANIFEST)
    parser.add_argument("--event", required=True, help="github.event_name")
    parser.add_argument("--ref", default="", help="github.ref")
    parser.add_argument("--scope", default="", help="edges, full or custom")
    parser.add_argument("--tmux-ref", default="", help="comma-separated tmux refs")
    parser.add_argument("--dotnet", default="", help="comma-separated target frameworks")
    parser.add_argument("--tmate", default="", help="off, before-tests or on-failure")
    parser.add_argument("--github-output", type=pathlib.Path, help="the GITHUB_OUTPUT file")
    parser.add_argument("--summary", type=pathlib.Path, help="the GITHUB_STEP_SUMMARY file")
    arguments = parser.parse_args(argv)

    try:
        result = plan(
            load_manifest(arguments.manifest),
            event=arguments.event,
            ref=arguments.ref,
            scope=arguments.scope,
            tmux_ref=arguments.tmux_ref,
            dotnet=arguments.dotnet,
            tmate=arguments.tmate,
        )
    except PlanError as error:
        print(f"plan: {error}", file=sys.stderr)
        return 1

    print(json.dumps(result, indent=2))
    if arguments.github_output:
        with arguments.github_output.open("a", encoding="utf-8") as handle:
            for key, value in outputs(result).items():
                handle.write(f"{key}={value}\n")
    if arguments.summary:
        with arguments.summary.open("a", encoding="utf-8") as handle:
            handle.write(summary(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
