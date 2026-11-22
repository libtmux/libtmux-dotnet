#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Build and run the complete example programs the tmux matrix proves.

Each program under `examples/LibTmux.Examples/Programs` (C#) and
`examples/LibTmux.FSharp.Examples/Programs` (F#) is built into an assembly of
its own, so the matrix can run every one against every tmux. The program lists
are the directories, so adding a program adds it here:

```console
$ uv run scripts/example-programs/example_programs.py build csharp
```

```console
$ uv run scripts/example-programs/example_programs.py run csharp --framework net10.0
```

An F# program prints what it found, and its guide shows what it must print:
`run fsharp` pipes each program's output to `sync_fsharp_snippets.py
--expect-output`. A program the manifest says needs a newer tmux than the one
under test is skipped, and the tmux under test is `LIBTMUX_EXPECTED_TMUX_VERSION`
or, for a ref that names no release, what `$LIBTMUX_TEST_TMUX -V` (or
`$LIBTMUX_TMUX -V` when unset) reports.
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import typing as t

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "examples" / "api" / "manifest.json"
SYNC_SNIPPETS = ROOT / "scripts" / "docs" / "sync_fsharp_snippets.py"
QUICKSTART_PROJECT = "examples/LibTmux.FSharp.Quickstart/LibTmux.FSharp.Quickstart.fsproj"
QUICKSTART_MINIMUM = "3.7"
AMBIENT_SELECTORS = (
    "TMUX",
    "TMUX_PANE",
    "LIBTMUX_SOCKET",
    "LIBTMUX_SOCKET_PATH",
    "LIBTMUX_SOCKET_NAME",
)


@dataclasses.dataclass(frozen=True)
class Kind:
    """Where one language's example programs live and what they build to."""

    project: str
    programs: str
    suffix: str
    assembly: str
    built: str


KINDS = {
    "csharp": Kind(
        project="examples/LibTmux.Examples/LibTmux.Examples.csproj",
        programs="examples/LibTmux.Examples/Programs",
        suffix=".cs",
        assembly="LibTmux.Examples.dll",
        built="examples/LibTmux.Examples/bin/Release/programs",
    ),
    "fsharp": Kind(
        project="examples/LibTmux.FSharp.Examples/LibTmux.FSharp.Examples.fsproj",
        programs="examples/LibTmux.FSharp.Examples/Programs",
        suffix=".fs",
        assembly="LibTmux.FSharp.Examples.dll",
        built="examples/LibTmux.FSharp.Examples/bin/Release/programs",
    ),
}


class ExampleError(Exception):
    """A program could not be built, run or compared."""


def program_names(kind: Kind) -> list[str]:
    """Return the name of each program in a kind's directory, in order."""
    return sorted(path.stem for path in (ROOT / kind.programs).glob(f"*{kind.suffix}"))


def version_key(version: str) -> list[tuple[int, int, str]]:
    """Order tmux versions the way `sort -V` does: 3.7 before 3.7a before 3.10.

    >>> version_key("3.7") < version_key("3.7a") < version_key("3.10")
    True
    """
    return [
        (0, int(part), "") if part.isdigit() else (1, 0, part)
        for part in re.findall(r"\d+|[A-Za-z]+", version)
    ]


def tmux_release(report: str) -> str:
    """Return the release a `tmux -V` line names, without `tmux ` or `next-`.

    >>> tmux_release("tmux next-3.8")
    '3.8'
    >>> tmux_release("tmux 3.3a")
    '3.3a'
    """
    return report.strip().removeprefix("tmux ").removeprefix("next-")


def required_tmux(manifest: pathlib.Path, program: str) -> str:
    """Return the release the manifest says an F# program needs, or an empty string."""
    for entry in json.loads(manifest.read_text(encoding="utf-8"))["examples"]:
        if entry.get("id") == f"fsharp-{program}":
            return str(entry.get("requiresTmux") or "")
    return ""


def configured_tmux(environment: t.Mapping[str, str]) -> str | None:
    """Return the configured test tmux path, preserving non-blank values untrimmed."""
    for variable in ("LIBTMUX_TEST_TMUX", "LIBTMUX_TMUX"):
        candidate = environment.get(variable)
        if candidate is not None and candidate.strip():
            return candidate
    return None


def resolve_tmux(environment: t.Mapping[str, str]) -> str:
    """Return the tmux executable under test."""
    return configured_tmux(environment) or "tmux"


@contextlib.contextmanager
def isolated_environment(
    environment: t.Mapping[str, str],
) -> t.Generator[dict[str, str], None, None]:
    """Yield a child environment with ambient selectors removed and `tmux` shimmed."""
    clean = {
        key: value
        for key, value in environment.items()
        if key not in AMBIENT_SELECTORS and not key.startswith("LIBTMUX_SOCKET")
    }
    configured = configured_tmux(environment)
    if configured is None or os.path.sep not in configured:
        yield clean
        return
    with tempfile.TemporaryDirectory(prefix="libtmux-tmux-") as shim_root:
        target = (
            configured
            if os.path.isabs(configured) or configured[:1].isspace()
            else os.path.abspath(configured)
        )
        (pathlib.Path(shim_root) / "tmux").symlink_to(target)
        existing = clean.get("PATH", "")
        clean["PATH"] = f"{shim_root}{os.pathsep}{existing}" if existing else shim_root
        yield clean


def running_tmux(environment: t.Mapping[str, str]) -> str:
    """Return the release under test: the expected one, or what the built tmux reports."""
    expected = environment.get("LIBTMUX_EXPECTED_TMUX_VERSION", "")
    if expected:
        return expected
    binary = resolve_tmux(environment)
    done = subprocess.run([binary, "-V"], capture_output=True, text=True, check=True)
    return tmux_release(done.stdout)


def check_expected_output(program: str, printed: str) -> None:
    """Fail when `printed` is not the output the guide shows for `program`."""
    done = subprocess.run(
        [sys.executable, str(SYNC_SNIPPETS), "--expect-output", program],
        input=printed,
        text=True,
        check=False,
    )
    if done.returncode != 0:
        message = f"{program} printed output its guide does not show"
        raise ExampleError(message)


def build(kind_name: str) -> None:
    """Build every program of a kind into an assembly of its own."""
    kind = KINDS[kind_name]
    for program in program_names(kind):
        subprocess.run(
            [
                "dotnet",
                "build",
                kind.project,
                "--configuration",
                "Release",
                "--no-restore",
                "--warnaserror",
                f"-p:ExampleProgram={program}",
            ],
            cwd=ROOT,
            check=True,
        )


def run_csharp(framework: str, environment: t.Mapping[str, str]) -> None:
    """Run every C# program on one framework."""
    kind = KINDS["csharp"]
    with isolated_environment(environment) as clean:
        for program in program_names(kind):
            assembly = pathlib.Path(kind.built) / program / framework / kind.assembly
            subprocess.run(["dotnet", str(assembly)], cwd=ROOT, env=clean, check=True)


def run_fsharp(framework: str, environment: t.Mapping[str, str]) -> None:
    """Run the F# console and every program, comparing each to its guide."""
    kind = KINDS["fsharp"]
    built = running_tmux(environment)
    console = f"examples/LibTmux.FSharp.Examples/bin/Release/{framework}/{kind.assembly}"
    with isolated_environment(environment) as clean:
        subprocess.run(["dotnet", console], cwd=ROOT, env=clean, check=True)
        for program in program_names(kind):
            minimum = required_tmux(MANIFEST, program)
            if minimum and version_key(built) < version_key(minimum):
                print(f"skip {program}: needs tmux {minimum}")
                continue
            assembly = pathlib.Path(kind.built) / program / framework / kind.assembly
            done = subprocess.run(
                ["dotnet", str(assembly)],
                cwd=ROOT,
                env=clean,
                stdout=subprocess.PIPE,
                text=True,
                check=True,
            )
            check_expected_output(program, done.stdout)


def quickstart(frameworks: t.Sequence[str], environment: t.Mapping[str, str]) -> None:
    """Run the F# quickstart on each framework, or say it was skipped.

    The quickstart waits through a control client, which tmux promises from 3.7;
    the runner's apt tmux can be older. A skip is a warning annotation and a
    summary line, so the run reads as skipped rather than as a pass.
    """
    with isolated_environment(environment) as clean:
        done = subprocess.run(
            [resolve_tmux(environment), "-V"],
            capture_output=True,
            text=True,
            env=clean,
            check=True,
        )
        running = tmux_release(done.stdout)
        if version_key(running) < version_key(QUICKSTART_MINIMUM):
            print(
                "::warning title=F# quickstart skipped::"
                f"It needs tmux {QUICKSTART_MINIMUM}; this runner has {running}."
            )
            summary = environment.get("GITHUB_STEP_SUMMARY")
            if summary:
                with open(summary, "a", encoding="utf-8") as handle:
                    handle.write(
                        f"F# README quickstart skipped: it needs tmux {QUICKSTART_MINIMUM}, "
                        f"this runner has {running}.\n"
                    )
            return
        for framework in frameworks:
            # Built before, so only what the program prints reaches the comparison.
            printed = subprocess.run(
                [
                    "dotnet",
                    "run",
                    "--project",
                    QUICKSTART_PROJECT,
                    "--configuration",
                    "Release",
                    "--framework",
                    framework,
                    "--no-build",
                    "-p:UsePackageReferences=true",
                ],
                cwd=ROOT,
                env=clean,
                stdout=subprocess.PIPE,
                text=True,
                check=True,
            )
            check_expected_output("Quickstart", printed.stdout)


def main(argv: t.Sequence[str] | None = None) -> int:
    """Run the requested command and answer the process status."""
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0] if __doc__ else None)
    commands = parser.add_subparsers(dest="command", required=True)
    build_parser = commands.add_parser("build", help="build each program into its own assembly")
    build_parser.add_argument("kind", choices=sorted(KINDS))
    run_parser = commands.add_parser("run", help="run each built program on one framework")
    run_parser.add_argument("kind", choices=sorted(KINDS))
    run_parser.add_argument("--framework", required=True)
    quickstart_parser = commands.add_parser("quickstart", help="run the F# README quickstart")
    quickstart_parser.add_argument("--framework", action="append", required=True)
    options = parser.parse_args(argv)
    try:
        if options.command == "build":
            build(options.kind)
        elif options.command == "run" and options.kind == "csharp":
            run_csharp(options.framework, os.environ)
        elif options.command == "run":
            run_fsharp(options.framework, os.environ)
        else:
            quickstart(options.framework, os.environ)
    except (ExampleError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
