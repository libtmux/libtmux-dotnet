"""Prove the example programs are built and run from their directories, one by one."""

from __future__ import annotations

import json
import os
import pathlib
import stat
import sys

import pytest

import example_programs as programs


def stand_in(directory: pathlib.Path, name: str, body: str) -> pathlib.Path:
    """Write an executable Python program named `name`."""
    path = directory / name
    path.write_text(f"#!{sys.executable}\n{body}", encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IXUSR)
    return path


@pytest.fixture
def world(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> pathlib.Path:
    """A repository with two programs per language, and a `dotnet` that records its calls."""
    root = tmp_path / "repo"
    for kind in programs.KINDS.values():
        directory = root / kind.programs
        directory.mkdir(parents=True)
        for name in ("Beta", "Alpha"):
            (directory / f"{name}{kind.suffix}").write_text("", encoding="utf-8")
    manifest = root / "examples" / "api" / "manifest.json"
    manifest.parent.mkdir(parents=True)
    manifest.write_text(
        json.dumps(
            {
                "examples": [
                    {"id": "fsharp-Alpha"},
                    {"id": "fsharp-Beta", "requiresTmux": "3.7"},
                ]
            }
        ),
        encoding="utf-8",
    )
    bin_directory = tmp_path / "bin"
    bin_directory.mkdir()
    log = tmp_path / "calls.log"
    stand_in(
        bin_directory,
        "dotnet",
        "import os, sys\n"
        f"open({str(log)!r}, 'a').write(' '.join(sys.argv[1:]) + '\\n')\n"
        "print('output of ' + os.path.basename(os.path.dirname(os.path.dirname(sys.argv[-1]))))\n",
    )
    stand_in(bin_directory, "tmux", "print('tmux 3.8')\n")
    monkeypatch.setenv("PATH", f"{bin_directory}{os.pathsep}{os.environ['PATH']}")
    monkeypatch.setattr(programs, "ROOT", root)
    monkeypatch.setattr(programs, "MANIFEST", manifest)
    expected = tmp_path / "expected.log"
    monkeypatch.setattr(
        programs,
        "SYNC_SNIPPETS",
        stand_in(
            tmp_path,
            "sync",
            "import sys\n"
            f"open({str(expected)!r}, 'a').write(sys.argv[2] + ':' + sys.stdin.read() + '|')\n",
        ),
    )
    return tmp_path


def calls(world: pathlib.Path) -> list[str]:
    return (world / "calls.log").read_text(encoding="utf-8").splitlines()


def test_each_program_in_the_directory_is_built_with_its_own_name(world: pathlib.Path) -> None:
    assert programs.main(["build", "csharp"]) == 0

    assert calls(world) == [
        "build examples/LibTmux.Examples/LibTmux.Examples.csproj --configuration Release"
        f" --no-restore --warnaserror -p:ExampleProgram={name}"
        for name in ("Alpha", "Beta")
    ]


def test_a_build_that_fails_stops_the_run(world: pathlib.Path) -> None:
    stand_in(world / "bin", "dotnet", "raise SystemExit(3)\n")

    assert programs.main(["build", "fsharp"]) == 1


def test_each_csharp_program_runs_on_the_framework_asked_for(world: pathlib.Path) -> None:
    assert programs.main(["run", "csharp", "--framework", "net8.0"]) == 0

    assert calls(world) == [
        f"examples/LibTmux.Examples/bin/Release/programs/{name}/net8.0/LibTmux.Examples.dll"
        for name in ("Alpha", "Beta")
    ]


def test_an_fsharp_program_needing_a_newer_tmux_is_skipped_and_the_rest_compared(
    world: pathlib.Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("LIBTMUX_EXPECTED_TMUX_VERSION", "3.5")

    assert programs.main(["run", "fsharp", "--framework", "net10.0"]) == 0

    assert "skip Beta: needs tmux 3.7" in capsys.readouterr().out
    assert calls(world) == [
        "examples/LibTmux.FSharp.Examples/bin/Release/net10.0/LibTmux.FSharp.Examples.dll",
        "examples/LibTmux.FSharp.Examples/bin/Release/programs/Alpha/net10.0/LibTmux.FSharp.Examples.dll",
    ]
    assert (world / "expected.log").read_text(encoding="utf-8").startswith("Alpha:")


def test_a_ref_that_names_no_release_is_judged_by_what_it_built(
    world: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("LIBTMUX_EXPECTED_TMUX_VERSION", raising=False)
    monkeypatch.setenv("LIBTMUX_TMUX", str(stand_in(world, "legacy-tmux", "print('tmux 3.4')\n")))
    monkeypatch.setenv(
        "LIBTMUX_TEST_TMUX", str(stand_in(world, "next-tmux", "print('tmux next-3.8')\n"))
    )

    assert programs.main(["run", "fsharp", "--framework", "net8.0"]) == 0

    assert len(calls(world)) == 3


@pytest.mark.parametrize(
    ("test_tmux", "legacy_tmux", "expected"),
    [
        ("/opt/test/bin/tmux", "/opt/legacy/bin/tmux", "/opt/test/bin/tmux"),
        ("", "/opt/legacy/bin/tmux", "/opt/legacy/bin/tmux"),
        ("   \t\n", "/opt/legacy/bin/tmux", "/opt/legacy/bin/tmux"),
        ("   ", "   ", "tmux"),
        (" /tmp/with spaces/tmux ", "/opt/legacy/bin/tmux", " /tmp/with spaces/tmux "),
    ],
)
def test_test_tmux_precedence_and_whitespace_handling(
    test_tmux: str, legacy_tmux: str, expected: str
) -> None:
    environment = {"LIBTMUX_TEST_TMUX": test_tmux, "LIBTMUX_TMUX": legacy_tmux}

    assert programs.resolve_tmux(environment) == expected


def test_isolated_environment_clears_ambient_selectors_and_shims_custom_tmux_binary() -> None:
    dirty = {
        "PATH": "/usr/local/bin:/usr/bin:/bin",
        "LIBTMUX_TEST_TMUX": "/opt/tmux-3.8/bin/tmux-custom",
        "TMUX": "/tmp/tmux-1000/default,1,0",
        "TMUX_PANE": "%0",
        "LIBTMUX_SOCKET": "dev",
        "LIBTMUX_SOCKET_PATH": "/tmp/dev.sock",
        "LIBTMUX_SOCKET_NAME": "retired",
    }

    with programs.isolated_environment(dirty) as clean:
        for selector in programs.AMBIENT_SELECTORS:
            assert selector not in clean
        first_entry, remainder = clean["PATH"].split(os.pathsep, 1)
        assert remainder == "/usr/local/bin:/usr/bin:/bin"
        assert os.readlink(pathlib.Path(first_entry) / "tmux") == "/opt/tmux-3.8/bin/tmux-custom"
    assert not pathlib.Path(first_entry).exists()


def test_output_that_the_guide_rejects_fails_the_run(
    world: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("LIBTMUX_EXPECTED_TMUX_VERSION", "3.8")
    monkeypatch.setattr(
        programs, "SYNC_SNIPPETS", stand_in(world, "refuse", "raise SystemExit(1)\n")
    )

    assert programs.main(["run", "fsharp", "--framework", "net8.0"]) == 1


def test_the_quickstart_is_skipped_loudly_below_tmux_3_7(
    world: pathlib.Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    stand_in(world / "bin", "tmux", "print('tmux 3.4')\n")
    summary = world / "summary.md"
    monkeypatch.setenv("GITHUB_STEP_SUMMARY", str(summary))

    assert programs.main(["quickstart", "--framework", "net8.0", "--framework", "net10.0"]) == 0

    assert "::warning title=F# quickstart skipped::It needs tmux 3.7; this runner has 3.4." in (
        capsys.readouterr().out
    )
    assert "skipped: it needs tmux 3.7, this runner has 3.4." in summary.read_text(encoding="utf-8")
    assert not (world / "calls.log").exists()


def test_the_quickstart_runs_each_framework_and_compares_its_output(world: pathlib.Path) -> None:
    assert programs.main(["quickstart", "--framework", "net8.0", "--framework", "net10.0"]) == 0

    assert [line.split()[4] for line in calls(world)] == ["Release", "Release"]
    assert [line.split()[6] for line in calls(world)] == ["net8.0", "net10.0"]
    assert (world / "expected.log").read_text(encoding="utf-8").count("Quickstart:") == 2


@pytest.mark.parametrize(
    ("older", "newer"),
    [("3.7", "3.7a"), ("3.7a", "3.10"), ("3.3a", "3.4"), ("3.7c", "3.8")],
)
def test_versions_order_as_sort_v_does(older: str, newer: str) -> None:
    assert programs.version_key(older) < programs.version_key(newer)
