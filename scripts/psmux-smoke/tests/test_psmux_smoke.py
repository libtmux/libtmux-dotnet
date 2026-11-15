"""Prove the psmux smoke verifies before it launches, and cleans up only what it owns."""

from __future__ import annotations

import os
import pathlib
import sys
import typing as t

import pytest

import psmux_smoke as smoke

SHA = smoke.SUPPORTED_SHA256
IDENTITY = "4242:99\t$7\tsmoke"
FIXTURE = smoke.FIXTURE_TEXT
NAMESPACE = "libtmux_smoke_01a00fd3"


class FakePlatform:
    """Map Windows path strings into a temporary directory and fake the process table."""

    def __init__(self, root: pathlib.Path) -> None:
        """Root every Windows path under ``root``."""
        self.root = root
        self.alive = True

    def fs_path(self, value: str) -> pathlib.Path:
        """Map ``C:\\a\\b`` and ``\\\\host\\share\\b`` under the root."""
        parts = [part for part in value.replace("\\", "/").replace(":", "").split("/") if part]
        return self.root.joinpath(*parts)

    def drive_is_fixed(self, path: str) -> bool:
        """Treat every drive as fixed."""
        return True

    def is_reparse_point(self, path: pathlib.Path) -> bool:
        """Treat no path as a link."""
        return False

    def process_start(self, pid: int) -> float | None:
        """Report the session process as running until it is killed."""
        return 1.0 if self.alive else None


class FakeRunner:
    """Answer each leg by name, recording every call."""

    def __init__(self, platform: FakePlatform, data_directory: pathlib.Path) -> None:
        """Start with the answers a clean run gives."""
        self.platform = platform
        self.data = data_directory
        self.calls: list[tuple[str, list[str], str]] = []
        self.answers: dict[str, t.Callable[[list[str]], smoke.Result]] = {}
        self.test_count = 1

    def sid(self) -> pathlib.Path:
        """Return the registry file the session creates."""
        return self.data / f"{NAMESPACE}__smoke.sid"

    def ok(self, *lines: str) -> smoke.Result:
        """Return a successful result with ``lines`` as its output."""
        return smoke.Result(list(lines), "", 0)

    def run(
        self,
        file_path: str,
        arguments: list[str],
        capture_directory: str,
        timeout_seconds: float,
        leg: str,
        *,
        kill_tree: bool = False,
    ) -> smoke.Result:
        """Record the call and answer it."""
        self.calls.append((leg, arguments, file_path))
        if leg in self.answers:
            return self.answers[leg](arguments)
        if leg == "psmux version query":
            return self.ok(*smoke.EXPECTED_BANNER)
        if leg == "psmux session creation":
            self.sid().write_text("x", encoding="utf-8")
            return self.ok()
        if leg in ("psmux session identity query", "psmux cleanup identity query"):
            return self.ok(IDENTITY)
        if leg == "psmux pane capture":
            return self.ok(f"PS> {FIXTURE}")
        if leg == "psmux exact session cleanup":
            self.sid().unlink()
            self.platform.alive = False
            return self.ok()
        if leg.endswith(".NET smoke"):
            path = (
                str(self.data / "libtmux-wsl-result.xml")
                if leg.startswith("WSL")
                else arguments[arguments.index("-result-xml") + 1]
            )
            pathlib.Path(path).write_text(
                f'<assemblies><assembly total="{self.test_count}" passed="{self.test_count}" '
                'failed="0" skipped="0" not-run="0" errors="0"/></assemblies>',
                encoding="utf-8",
            )
            return self.ok()
        if leg.startswith("WSL") and "path" in leg:
            return self.ok("/mnt/translated")
        if "example" in leg or "consumer" in leg:
            return self.ok(FIXTURE)
        if leg == "WSL .NET executable validation":
            return self.ok("/usr/share/dotnet/dotnet")
        return self.ok()


class Harness:
    """One configured smoke over fake processes."""

    def __init__(self, tmp_path: pathlib.Path, *, wsl: bool = False) -> None:
        """Lay out the files the smoke validates."""
        self.platform = FakePlatform(tmp_path)
        self.data_string = "C:\\Temp\\libtmux-psmux-smoke-01a00fd3"
        data = self.platform.fs_path(self.data_string)
        self.runner = FakeRunner(self.platform, data)
        self.paths: dict[str, str] = {}
        for name, windows, content in (
            ("psmux", "C:\\Tools\\psmux.exe", b"MZ 66cf613 2026-08-18"),
            ("dotnet", "C:\\dotnet\\dotnet.exe", b"x"),
            ("test", "C:\\build\\net10.0\\LibTmux.UnitTests.dll", b"x"),
            ("example", "C:\\build\\net10.0\\LibTmux.Examples.dll", b"x"),
            ("package", "C:\\build\\net10.0\\LibTmux.PackageConsumer.dll", b"x"),
        ):
            file = self.platform.fs_path(windows)
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(content)
            self.paths[name] = windows
        self.sha = __import__("hashlib").sha256(b"MZ 66cf613 2026-08-18").hexdigest()
        self.wsl = wsl

    def config(self, **changes: object) -> smoke.Config:
        """Return a valid configuration, with ``changes`` applied."""
        values: dict[str, t.Any] = {
            "psmux_path": self.paths["psmux"],
            "expected_sha256": SHA,
            "data_directory": self.data_string,
            "namespace_name": NAMESPACE,
            "dotnet_path": self.paths["dotnet"],
            "test_assembly": self.paths["test"],
            "example_assembly": self.paths["example"],
            "package_consumer_assembly": self.paths["package"],
            "target_framework": "net10.0",
        }
        if self.wsl:
            values.update(
                run_wsl_smoke=True,
                wsl_distribution="Ubuntu",
                wsl_repository="/home/me/libtmux-dotnet",
                wsl_dotnet_path="/home/me/dotnet",
            )
        values.update(changes)
        return smoke.Config(**values)

    def run(self, **changes: object) -> None:
        """Run the smoke against the fake processes."""
        runner = t.cast("smoke.Runner", self.runner)
        smoke.Smoke(self.config(**changes), runner, self.platform).run()


@pytest.fixture
def harness(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> Harness:
    for name in list(os.environ):
        if name.upper().startswith(("PSMUX_", "LIBTMUX_PSMUX_")):
            monkeypatch.delenv(name)
    result = Harness(tmp_path)
    monkeypatch.setattr(smoke.time, "sleep", lambda seconds: None)
    # The hash and markers are those of the audited build; the fake psmux is
    # given the same hash by overriding the configured one.
    monkeypatch.setattr(smoke, "SUPPORTED_SHA256", result.sha)
    return result


def test_a_clean_run_launches_only_after_verification_and_removes_what_it_owns(
    harness: Harness, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("PSMUX_KEEP", "caller")
    monkeypatch.setenv("TMUX", "/tmp/tmux-1/default,1,0")

    harness.run(expected_sha256=harness.sha)

    legs = [call[0] for call in harness.runner.calls]
    assert legs[0] == "psmux version query"
    assert legs.index("psmux session creation") < legs.index("psmux fixture input")
    assert not harness.platform.fs_path(harness.data_string).exists()
    assert os.environ["PSMUX_KEEP"] == "caller"
    assert os.environ["TMUX"] == "/tmp/tmux-1/default,1,0"
    assert "PSMUX_DATA_DIR" not in os.environ
    assert "LIBTMUX_PSMUX_SMOKE" not in os.environ


def test_cleanup_kills_the_exact_session_by_id_and_never_the_server(harness: Harness) -> None:
    harness.run(expected_sha256=harness.sha)

    killed = [call for call in harness.runner.calls if call[0] == "psmux exact session cleanup"]
    assert [call[1] for call in killed] == [["kill-session", "-t", "$7"]]
    assert all("kill-server" not in call[1] for call in harness.runner.calls)


def test_a_wrong_binary_is_refused_before_it_runs(harness: Harness) -> None:
    harness.platform.fs_path(harness.paths["psmux"]).write_bytes(b"MZ 66cf613 2026-08-18 changed")

    with pytest.raises(smoke.SmokeError, match="SHA-256 mismatch"):
        harness.run(expected_sha256=harness.sha)

    assert harness.runner.calls == []


def test_a_binary_without_the_audited_markers_is_refused(harness: Harness) -> None:
    content = b"MZ other build"
    harness.platform.fs_path(harness.paths["psmux"]).write_bytes(content)
    harness.sha = __import__("hashlib").sha256(content).hexdigest()

    with pytest.raises(smoke.SmokeError, match="audited build markers"):
        harness.run(expected_sha256=harness.sha)

    assert harness.runner.calls == []


def test_an_existing_data_directory_is_never_removed(harness: Harness) -> None:
    existing = harness.platform.fs_path(harness.data_string)
    existing.mkdir(parents=True)
    (existing / "keep.txt").write_text("caller data", encoding="utf-8")

    with pytest.raises(smoke.SmokeError, match="must not exist"):
        harness.run(expected_sha256=harness.sha)

    assert (existing / "keep.txt").read_text(encoding="utf-8") == "caller data"


def test_an_unaudited_banner_stops_before_any_session_exists(harness: Harness) -> None:
    harness.runner.answers["psmux version query"] = lambda arguments: harness.runner.ok("tmux 9.9")

    with pytest.raises(smoke.SmokeError, match="unaudited banner"):
        harness.run(expected_sha256=harness.sha)

    assert "psmux session creation" not in [call[0] for call in harness.runner.calls]
    assert not harness.platform.fs_path(harness.data_string).exists()


def test_a_replaced_session_identity_is_not_killed_and_the_directory_is_kept(
    harness: Harness,
) -> None:
    harness.runner.answers["psmux cleanup identity query"] = lambda arguments: harness.runner.ok(
        "4243:100\t$7\tsmoke"
    )

    with pytest.raises(smoke.SmokeError, match="refusing to kill its replacement"):
        harness.run(expected_sha256=harness.sha)

    assert "psmux exact session cleanup" not in [call[0] for call in harness.runner.calls]
    assert harness.platform.fs_path(harness.data_string).exists()


def test_a_surviving_session_process_fails_the_run(harness: Harness) -> None:
    def kill(arguments: list[str]) -> smoke.Result:
        harness.runner.sid().unlink()
        return harness.runner.ok()

    harness.runner.answers["psmux exact session cleanup"] = kill

    with pytest.raises(smoke.SmokeError, match="process survived cleanup"):
        harness.run(expected_sha256=harness.sha)


def test_a_smoke_that_ran_two_tests_is_refused(harness: Harness) -> None:
    harness.runner.test_count = 2

    with pytest.raises(smoke.SmokeError, match="exactly one passing"):
        harness.run(expected_sha256=harness.sha)


def test_an_example_that_misses_the_fixture_text_fails(harness: Harness) -> None:
    harness.runner.answers["Native Windows example"] = lambda arguments: harness.runner.ok("hello")

    with pytest.raises(smoke.SmokeError, match="did not report the UTF-8 fixture text"):
        harness.run(expected_sha256=harness.sha)


def test_the_wsl_legs_run_the_validated_dotnet_by_absolute_path(tmp_path: pathlib.Path) -> None:
    harness = Harness(tmp_path, wsl=True)
    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(smoke.time, "sleep", lambda seconds: None)
        patch.setattr(smoke, "SUPPORTED_SHA256", harness.sha)
        harness.run(expected_sha256=harness.sha)

    wsl = {call[0]: call[1] for call in harness.runner.calls if call[2] == "wsl.exe"}
    smoke_call = wsl["WSL .NET smoke"]
    assert "/usr/share/dotnet/dotnet" in smoke_call
    assert smoke_call[smoke_call.index("--cd") + 1] == "/mnt/translated"
    assert f"PSMUX_DATA_DIR={harness.data_string}" in smoke_call
    assert "WSLENV=PSMUX_DATA_DIR/w" in smoke_call
    assert "240s" in smoke_call
    assert smoke_call[smoke_call.index("--exec") + 1] == "/bin/sh"
    assert "120s" in wsl["WSL example"]
    assert wsl["WSL .NET executable validation"][-1] == "10"


@pytest.mark.parametrize(
    "changes",
    [
        {"wsl_distribution": ""},
        {"wsl_dotnet_path": "relative/dotnet"},
        {"wsl_repository": "relative"},
    ],
)
def test_wsl_inputs_are_checked_before_anything_runs(
    tmp_path: pathlib.Path, changes: dict[str, str]
) -> None:
    harness = Harness(tmp_path, wsl=True)

    with pytest.raises(smoke.SmokeError):
        harness.run(**changes)

    assert harness.runner.calls == []


@pytest.mark.parametrize(
    ("path", "message"),
    [
        ("relative\\dir", "absolute path on a local Windows drive"),
        ("C:\\", "must not be a filesystem root"),
        ("C:\\tmp\\..\\x", "ambiguous"),
        ("C:\\tmp\\trailing.", "ambiguous"),
        ("C:\\tmp\\CON.txt", "reserved Windows device name"),
        ("C:\\tmp\\bad|name", "invalid Windows path segment"),
    ],
)
def test_an_unsafe_data_directory_is_refused(
    tmp_path: pathlib.Path, path: str, message: str
) -> None:
    with pytest.raises(smoke.SmokeError, match=message):
        smoke.isolated_data_directory(path, FakePlatform(tmp_path))


def test_a_fresh_data_directory_is_normalized(tmp_path: pathlib.Path) -> None:
    assert (
        smoke.isolated_data_directory("C:\\Temp\\run\\", FakePlatform(tmp_path)) == "C:\\Temp\\run"
    )


def test_the_expected_banner_and_fixture_are_the_audited_ones() -> None:
    assert smoke.EXPECTED_BANNER == ["tmux 3.3.8", "psmux 3.3.8 (66cf613 2026-08-18)"]
    assert smoke.FIXTURE_TEXT == "héllo-雪-😀"
    assert smoke.FIXTURE_COMMAND.count("104,195,169") == 1


def test_the_real_runner_reports_output_status_and_strict_utf8(tmp_path: pathlib.Path) -> None:
    runner = smoke.Runner()
    here = str(tmp_path)

    done = runner.run(sys.executable, ["-c", "print('a'); print('b')"], here, 20, "leg")
    failed = runner.run(
        sys.executable, ["-c", "import sys; sys.stderr.write('no'); sys.exit(3)"], here, 20, "leg"
    )

    assert (done.output, done.exit_code) == (["a", "b"], 0)
    assert (failed.exit_code, failed.error) == (3, "no")
    with pytest.raises(smoke.SmokeError, match="emitted invalid UTF-8 on stdout"):
        runner.run(
            sys.executable,
            ["-c", "import sys; sys.stdout.buffer.write(b'\\xff')"],
            here,
            20,
            "leg",
        )


def test_the_real_runner_kills_a_process_that_outruns_its_deadline(tmp_path: pathlib.Path) -> None:
    with pytest.raises(smoke.SmokeError, match=r"exceeded its 0\.3-second timeout"):
        smoke.Runner().run(
            sys.executable, ["-c", "import time; time.sleep(30)"], str(tmp_path), 0.3, "leg"
        )


def test_error_details_are_one_bounded_line() -> None:
    assert smoke.bounded_detail("a\x00b\n\n c") == "a b c"
    assert smoke.bounded_detail("x" * 600) == "..." + "x" * 509
    assert smoke.exit_message("leg", 5, "bad", "out") == "leg exited 5. stderr: bad"
    assert smoke.exit_message("leg", 5, "", "out") == "leg exited 5. stdout: out"
    assert smoke.exit_message("leg", 5, "", "") == "leg exited 5."
