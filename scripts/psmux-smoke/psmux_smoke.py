#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = ["psutil>=6; sys_platform == 'win32'"]
# ///
"""Check the psmux preview from native Windows, and from WSL, against one psmux build.

A maintainer runs this by hand on a Windows x64 machine with WSL: CI does not run
psmux. It verifies the audited ``psmux.exe`` before its first launch, starts one
session in a fresh isolated namespace, prints a UTF-8 fixture into it, and runs
the unit-test smoke, the example and the packed consumer against that server,
natively and optionally through WSL. It removes only the exact session it created
and the data directory it owns, and a failing cleanup is a failing run.

```console
$ py -3 scripts\\psmux-smoke\\psmux_smoke.py `
    --psmux-path C:\\Tools\\psmux-v3.3.8\\psmux.exe `
    --expected-sha256 54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d `
    --data-directory C:\\Users\\me\\AppData\\Local\\Temp\\libtmux-psmux-smoke-01a00fd3 `
    --namespace-name libtmux_smoke_01a00fd3 `
    --dotnet-path "C:\\Program Files\\dotnet\\dotnet.exe" `
    --test-assembly LibTmux.UnitTests.dll `
    --example-assembly LibTmux.Examples.dll `
    --package-consumer-assembly LibTmux.PackageConsumer.dll `
    --target-framework net10.0
```
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import hashlib
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
import typing as t
import xml.etree.ElementTree as ElementTree

SUPPORTED_SHA256 = "54e5c54db259218348f966b5d0d0b5153fdef6350074855ea9ce627d20537b0d"
BUILD_MARKERS = ("66cf613", "2026-08-18")
EXPECTED_BANNER = ["tmux 3.3.8", "psmux 3.3.8 (66cf613 2026-08-18)"]
FIXTURE_TEXT = "h\u00e9llo-\u96ea-\U0001f600"
FIXTURE_COMMAND = (
    "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);"
    "[Console]::WriteLine([Text.Encoding]::UTF8.GetString([byte[]]("
    + ",".join(str(byte) for byte in FIXTURE_TEXT.encode("utf-8"))
    + ")))"
)
IDENTITY_FORMAT = "#{pid}:#{start_time}\t#{session_id}\t#{session_name}"
WINDOWS_DRIVE = re.compile(r"^[A-Za-z]:\\")
WINDOWS_ABSOLUTE = re.compile(r"^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)")
RESERVED_DEVICE = re.compile(r"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", re.IGNORECASE)
INVALID_FILE_NAME_CHARACTERS = '<>:"/\\|?*'
OWNED_ENVIRONMENT = (
    "LIBTMUX_PSMUX_BINARY",
    "LIBTMUX_PSMUX_EXPECTED_TEXT",
    "LIBTMUX_PSMUX_NAMESPACE",
    "LIBTMUX_PSMUX_SHA256",
    "LIBTMUX_PSMUX_SMOKE",
    "PSMUX_DATA_DIR",
    "PSMUX_NO_WARM",
    "TMUX",
    "TMUX_PANE",
)
SMOKE_CLASS = "LibTmux.UnitTests.Connection.PsmuxProcessSmokeTests"

# These scripts run under /bin/sh inside WSL, and a carriage return would turn
# the first line into "expecting in", so they are LF-only here.
WSL_TIMEOUT_SCRIPT = """case "$(LC_ALL=C /usr/bin/timeout --version 2>/dev/null)" in
    'timeout (GNU coreutils) '*) ;;
    *) exit 125 ;;
esac
deadline=$1
shift
exec /usr/bin/timeout --signal=KILL "$deadline" "$@"
"""
WSL_DOTNET_VALIDATION_SCRIPT = """set -eu
candidate=$1
framework_major=$2
case "$candidate" in
    /*) ;;
    *) exit 126 ;;
esac
resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126
case "$resolved" in
    /*) ;;
    *) exit 126 ;;
esac
[ -f "$resolved" ] && [ -x "$resolved" ] || exit 126
"$resolved" --info >/dev/null || exit 126
runtimes=$("$resolved" --list-runtimes) || exit 126
case "
$runtimes
" in
    *"
Microsoft.NETCore.App $framework_major."*) ;;
    *)
        printf '%s\\n' \\
            "required Microsoft.NETCore.App $framework_major runtime is unavailable" >&2
        exit 126
        ;;
esac
printf '%s\\n' "$resolved"
"""
WSL_PATH_RESOLUTION_SCRIPT = """set -eu
mode=$1
requirement=$2
candidate=$3
case "$mode" in
    windows)
        candidate=$(/usr/bin/wslpath -u -- "$candidate") || exit 126
        ;;
    linux) ;;
    *) exit 126 ;;
esac
case "$candidate" in
    /*) ;;
    *) exit 126 ;;
esac
if [ "$requirement" = directory ]; then
    resolved=$(/usr/bin/readlink -f -- "$candidate") || exit 126
    case "$resolved" in
        /*) ;;
        *) exit 126 ;;
    esac
    [ -d "$resolved" ] || exit 126
    candidate=$resolved
elif [ "$requirement" != path ]; then
    exit 126
fi
printf '%s\\n' "$candidate"
"""


class SmokeError(Exception):
    """The smoke cannot proceed or a leg did not hold."""


@dataclasses.dataclass(frozen=True)
class Result:
    """What one captured process printed and how it ended."""

    output: list[str]
    error: str
    exit_code: int


@dataclasses.dataclass(frozen=True)
class Config:
    """The validated command line."""

    psmux_path: str
    expected_sha256: str
    data_directory: str
    namespace_name: str
    dotnet_path: str
    test_assembly: str
    example_assembly: str
    package_consumer_assembly: str
    target_framework: str
    wsl_distribution: str = ""
    wsl_repository: str = ""
    wsl_dotnet_path: str = ""
    run_wsl_smoke: bool = False
    session_name: str = "smoke"


class Platform(t.Protocol):
    """The Windows facts the smoke reads, so tests can stand in for them."""

    def fs_path(self, value: str) -> pathlib.Path:
        """Return the filesystem path a Windows path string names."""
        ...

    def drive_is_fixed(self, path: str) -> bool:
        """Say whether ``path`` is on a fixed local drive."""
        ...

    def is_reparse_point(self, path: pathlib.Path) -> bool:
        """Say whether ``path`` is a symbolic link or reparse point."""
        ...

    def process_start(self, pid: int) -> float | None:
        """Return a process's start time, or ``None`` when it is not running."""
        ...


class WindowsPlatform:
    """The Windows implementation of :class:`Platform`."""

    def fs_path(self, value: str) -> pathlib.Path:
        """Return the filesystem path a Windows path string names."""
        return pathlib.Path(value)

    def drive_is_fixed(self, path: str) -> bool:
        """Say whether ``path`` is on a fixed local drive."""
        if sys.platform != "win32":
            return False
        import ctypes

        root = pathlib.PureWindowsPath(path).anchor
        drive_fixed = 3
        return bool(ctypes.WinDLL("kernel32").GetDriveTypeW(root) == drive_fixed)

    def is_reparse_point(self, path: pathlib.Path) -> bool:
        """Say whether ``path`` is a symbolic link or reparse point."""
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
        return bool(attributes & 0x400)

    def process_start(self, pid: int) -> float | None:
        """Return a process's start time, or ``None`` when it is not running."""
        import psutil

        try:
            return float(psutil.Process(pid).create_time())
        except psutil.NoSuchProcess:
            return None


def has_control_character(text: str) -> bool:
    """Say whether ``text`` has a control character.

    >>> has_control_character("a\\x00b"), has_control_character("ab")
    (True, False)
    """
    return any(ord(character) < 32 or 127 <= ord(character) < 160 for character in text)


def isolated_data_directory(path: str, platform: Platform) -> str:
    """Return a data directory fit to own, or refuse an ambiguous or shared one."""
    if not WINDOWS_DRIVE.match(path):
        raise SmokeError("DataDirectory must be an absolute path on a local Windows drive.")
    full = pathlib.PureWindowsPath(path)
    if not platform.drive_is_fixed(path):
        raise SmokeError("DataDirectory must be on a fixed local Windows drive.")
    if not full.parts[1:]:
        raise SmokeError("DataDirectory must not be a filesystem root.")
    for segment in full.parts[1:]:
        if segment in (".", "..") or segment.endswith((" ", ".")):
            raise SmokeError("DataDirectory contains an ambiguous Windows path segment.")
        if any(character in INVALID_FILE_NAME_CHARACTERS for character in segment) or (
            has_control_character(segment)
        ):
            raise SmokeError("DataDirectory contains an invalid Windows path segment.")
        if RESERVED_DEVICE.match(segment.split(".")[0]):
            raise SmokeError("DataDirectory contains a reserved Windows device name.")
    return str(full).rstrip("\\")


def bounded_detail(text: str) -> str:
    """Return a one-line excerpt of ``text``, at most 512 characters from its end.

    >>> bounded_detail("  a\\n\\n b  ")
    'a b'
    >>> len(bounded_detail("x" * 1000))
    512
    """
    if not text.strip():
        return ""
    detail = re.sub(
        r"[\x00-\x1f\x7f-\x9f\u200b-\u200f\u2028-\u202e\u2060-\u2064\ufeff]+", " ", text.strip()
    )
    detail = re.sub(r"\s+", " ", detail).strip()
    if len(detail) > 512:
        detail = "..." + detail[-509:]
    return detail


def exit_message(leg: str, exit_code: int, error: str, output: str = "") -> str:
    """Describe a non-zero exit with stderr, or stdout when stderr is empty.

    A test runner reports the failed assertion on stdout and leaves stderr empty,
    so an exit code alone says nothing about which check broke.

    >>> exit_message("leg", 3, "", "boom")
    'leg exited 3. stdout: boom'
    """
    message = f"{leg} exited {exit_code}."
    if detail := bounded_detail(error):
        return f"{message} stderr: {detail}"
    if detail := bounded_detail(output):
        return f"{message} stdout: {detail}"
    return message


def normalize_output(text: str) -> list[str]:
    """Split captured output into lines, dropping one trailing newline.

    >>> normalize_output("a\\r\\nb\\r\\n"), normalize_output("")
    (['a', 'b'], [])
    """
    if not text:
        return []
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    normalized = normalized.removesuffix("\n")
    return normalized.split("\n") if normalized else []


def assert_query_program(
    output: list[str], exit_code: int, error: str, expected_text: str, leg: str
) -> None:
    """Require a clean exit and the UTF-8 fixture in the program's output."""
    if exit_code != 0:
        raise SmokeError(exit_message(leg, exit_code, error))
    if not any(expected_text in line for line in output):
        raise SmokeError(f"{leg} did not report the UTF-8 fixture text.")


def assert_one_passing_test(result_path: pathlib.Path, leg: str) -> None:
    """Require an xUnit result with exactly one passing, non-skipped test."""
    if not result_path.is_file():
        raise SmokeError(f"{leg} did not write an xUnit result file.")
    assemblies = ElementTree.parse(result_path).getroot().findall("assembly")
    expected = {"total": 1, "passed": 1, "failed": 0, "skipped": 0, "not-run": 0, "errors": 0}
    if len(assemblies) != 1 or any(
        int(assemblies[0].get(name, "-1")) != value for name, value in expected.items()
    ):
        raise SmokeError(f"{leg} did not run exactly one passing, non-skipped psmux smoke test.")


class Runner:
    """Run native processes with a deadline, strict UTF-8 and explicit status."""

    def run(
        self,
        file_path: str,
        arguments: list[str],
        capture_directory: str,
        timeout_seconds: float,
        leg: str,
        *,
        kill_tree: bool = False,
    ) -> Result:
        """Run one process to completion, or fail with what the leg did."""
        if timeout_seconds <= 0:
            raise SmokeError("Native command timeouts must be positive.")
        process = subprocess.Popen(
            [file_path, *arguments],
            cwd=capture_directory,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            self.terminate(process, leg, capture_directory, kill_tree=kill_tree)
            stdout, stderr = process.communicate()
        exit_code = process.returncode
        try:
            out_text = self.decode(stdout, leg, "stdout")
            err_text = self.decode(stderr, leg, "stderr")
        except SmokeError as error:
            if timed_out:
                raise SmokeError(
                    f"{leg} exceeded its {timeout_seconds}-second timeout. {error}"
                ) from error
            if exit_code != 0:
                raise SmokeError(f"{leg} exited {exit_code}. {error}") from error
            raise
        if timed_out:
            message = f"{leg} exceeded its {timeout_seconds}-second timeout."
            if detail := bounded_detail(err_text):
                message += f" stderr: {detail}"
            raise SmokeError(message)
        return Result(normalize_output(out_text), err_text, exit_code)

    def terminate(
        self, process: subprocess.Popen[bytes], leg: str, capture_directory: str, *, kill_tree: bool
    ) -> None:
        """Stop a process that outran its deadline, and its tree when asked."""
        if process.poll() is None:
            if kill_tree:
                taskkill = pathlib.Path(
                    os.environ.get("SYSTEMROOT", ""), "System32", "taskkill.exe"
                )
                tree = self.run(
                    str(taskkill),
                    ["/PID", str(process.pid), "/T", "/F"],
                    capture_directory,
                    10,
                    f"{leg} process-tree termination",
                )
                if tree.exit_code != 0 and process.poll() is None:
                    raise SmokeError(
                        exit_message(
                            f"{leg} process-tree termination",
                            tree.exit_code,
                            tree.error,
                            "\n".join(tree.output),
                        )
                    )
            else:
                process.kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired as error:
            raise SmokeError(
                f"{leg} exceeded its timeout and its exact process survived termination."
            ) from error

    @staticmethod
    def decode(data: bytes, leg: str, stream: str) -> str:
        """Decode a stream as strict UTF-8."""
        try:
            return data.decode("utf-8")
        except UnicodeDecodeError as error:
            raise SmokeError(f"{leg} emitted invalid UTF-8 on {stream}.") from error


def wsl_command(
    config: Config, deadline: str, working_directory: str | None, *command: str
) -> list[str]:
    """Return the ``wsl.exe`` arguments that run ``command`` under an inner deadline.

    Killing ``wsl.exe`` alone would leave its Linux workload alive, so the
    process group carries its own deadline.
    """
    arguments = ["--distribution", config.wsl_distribution]
    if working_directory is not None:
        arguments += ["--cd", working_directory]
    return [
        *arguments,
        "--exec",
        "/bin/sh",
        "-eu",
        "-c",
        WSL_TIMEOUT_SCRIPT,
        "libtmux-timeout",
        deadline,
        *command,
    ]


class Smoke:
    """One run: its environment, the session it created and its cleanup."""

    def __init__(self, config: Config, runner: Runner, platform: Platform) -> None:
        """Hold the validated configuration and the process and platform seams."""
        self.config = config
        self.runner = runner
        self.platform = platform
        self.data_directory = config.data_directory
        self.psmux = ""
        self.directory_created = False
        self.config_path: pathlib.Path | None = None
        self.config_created = False
        self.result_paths: list[pathlib.Path] = []
        self.creation_attempted = False
        self.created_identity = ""
        self.created_pid = 0
        self.created_start: float | None = None
        self.created_session_id = ""
        self.saved_environment: dict[str, str | None] = {}
        self.wsl_repository = ""
        self.wsl_dotnet = ""

    def isolate_environment(self) -> None:
        """Clear every variable the smoke owns, remembering what it held."""
        owned: dict[str, str] = {name.upper(): name for name in OWNED_ENVIRONMENT}
        for name in list(os.environ):
            if name.upper().startswith(("PSMUX_", "LIBTMUX_PSMUX_")):
                owned[name.upper()] = name
        for name in owned.values():
            self.saved_environment[name] = os.environ.pop(name, None)

    def restore_environment(self) -> None:
        """Put back every owned variable as :meth:`isolate_environment` found it."""
        for name, value in self.saved_environment.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value

    def psmux_run(self, arguments: list[str], leg: str) -> Result:
        """Run the audited psmux with the data directory as its working directory."""
        return self.runner.run(self.psmux, arguments, self.data_directory, 30, leg)

    def require_zero(self, result: Result, leg: str) -> Result:
        """Fail with the leg's own message when it did not exit zero."""
        if result.exit_code != 0:
            raise SmokeError(
                exit_message(leg, result.exit_code, result.error, "\n".join(result.output))
            )
        return result

    def validate_files(self) -> dict[str, pathlib.Path]:
        """Check every input path and verify the psmux binary before it ever runs."""
        config = self.config
        for value in (
            config.psmux_path,
            config.dotnet_path,
            config.test_assembly,
            config.example_assembly,
            config.package_consumer_assembly,
        ):
            if not WINDOWS_ABSOLUTE.match(value):
                raise SmokeError(
                    "Every executable and assembly path must be an absolute Windows path."
                )
        if not WINDOWS_DRIVE.match(config.psmux_path):
            raise SmokeError("PsmuxPath must be local to a Windows drive.")

        def existing(value: str, suffix: str, message: str) -> pathlib.Path:
            path = self.platform.fs_path(value)
            if not path.is_file() or path.suffix.lower() != suffix:
                raise SmokeError(message)
            return path

        psmux = existing(
            config.psmux_path, ".exe", "PsmuxPath must identify an existing .exe file."
        )
        if self.platform.is_reparse_point(psmux):
            raise SmokeError("PsmuxPath must not be a symbolic link or reparse point.")
        if not self.platform.drive_is_fixed(str(psmux)):
            raise SmokeError("PsmuxPath must be on a fixed local Windows drive.")
        files = {
            "psmux": psmux,
            "dotnet": existing(
                config.dotnet_path, ".exe", "DotnetPath must identify an existing dotnet.exe file."
            ),
            "test": existing(
                config.test_assembly, ".dll", "TestAssembly must identify the built unit-test DLL."
            ),
            "example": existing(
                config.example_assembly,
                ".dll",
                "ExampleAssembly must identify the built examples DLL.",
            ),
            "package": existing(
                config.package_consumer_assembly,
                ".dll",
                "PackageConsumerAssembly must identify the built package-consumer DLL.",
            ),
        }
        for key in ("test", "example", "package"):
            if files[key].parent.name != config.target_framework:
                raise SmokeError(
                    f"Every smoke assembly must come from the {config.target_framework} output directory."
                )
        content = psmux.read_bytes()
        actual = hashlib.sha256(content).hexdigest()
        if actual.lower() != config.expected_sha256.lower():
            raise SmokeError(
                f"psmux SHA-256 mismatch: expected {config.expected_sha256}, got {actual}."
            )
        if any(marker.encode("ascii") not in content for marker in BUILD_MARKERS):
            raise SmokeError("psmux does not contain the audited build markers.")
        return files

    def convert_to_wsl_path(self, path: str, kind: str) -> str:
        """Translate or canonicalize one path inside the selected WSL distribution."""
        mode = "linux" if path.startswith("/") else "windows"
        requirement = "directory" if kind == "repository" else "path"
        operation = "resolution" if kind == "repository" else "translation"
        leg = f"WSL {kind} path {operation}"
        result = self.runner.run(
            "wsl.exe",
            wsl_command(
                self.config,
                "15s",
                None,
                "/bin/sh",
                "-eu",
                "-c",
                WSL_PATH_RESOLUTION_SCRIPT,
                "libtmux-path-resolution",
                mode,
                requirement,
                path,
            ),
            self.data_directory,
            30,
            leg,
            kill_tree=True,
        )
        self.require_zero(result, leg)
        if len(result.output) != 1 or not result.output[0].strip():
            raise SmokeError(f"WSL could not translate the {kind} path.")
        resolved = result.output[0].strip()
        if not resolved.startswith("/") or has_control_character(resolved):
            raise SmokeError(f"WSL returned an invalid {kind} path.")
        return resolved

    def validate_wsl_inputs(self) -> None:
        """Refuse WSL arguments that are missing or not paths."""
        config = self.config
        if not (
            config.wsl_distribution.strip()
            and config.wsl_repository.strip()
            and config.wsl_dotnet_path.strip()
        ):
            raise SmokeError(
                "RunWslSmoke requires WslDistribution, WslRepository, and WslDotnetPath."
            )
        dotnet = config.wsl_dotnet_path
        if dotnet != dotnet.strip() or not dotnet.startswith("/") or has_control_character(dotnet):
            raise SmokeError("WslDotnetPath must be a control-free absolute Linux path.")
        repository = config.wsl_repository
        if (
            repository != repository.strip()
            or not (repository.startswith("/") or WINDOWS_ABSOLUTE.match(repository))
            or has_control_character(repository)
        ):
            raise SmokeError("WslRepository must be a control-free absolute Linux or Windows path.")

    def wsl_leg(
        self,
        deadline: str,
        timeout: int,
        leg: str,
        repository: str,
        environment: list[str],
        command: list[str],
    ) -> Result:
        """Run one command through WSL with a Windows-absolute data directory."""
        arguments = wsl_command(
            self.config,
            deadline,
            repository,
            "/usr/bin/env",
            *environment,
            f"PSMUX_DATA_DIR={self.data_directory}",
            "WSLENV=PSMUX_DATA_DIR/w",
            *command,
        )
        return self.runner.run(
            "wsl.exe", arguments, self.data_directory, timeout, leg, kill_tree=True
        )

    def create_session(self) -> None:
        """Start exactly one session, and record its exact identity before anything else."""
        config = self.config
        self.config_path = self.platform.fs_path(self.data_directory) / "libtmux-smoke.conf"
        self.config_path.write_text("set -g warm off\n", encoding="utf-8", newline="\n")
        self.config_created = True

        existing = self.psmux_run(
            ["-L", config.namespace_name, "list-sessions", "-F", "#{session_name}"],
            "psmux isolated namespace inspection",
        )
        self.require_zero(existing, "psmux isolated namespace inspection")
        if existing.output:
            raise SmokeError(f"The isolated namespace is not empty: {', '.join(existing.output)}")

        self.creation_attempted = True
        creation = self.psmux_run(
            [
                "-f", str(self.config_path),
                "-L", config.namespace_name,
                "new-session", "-d",
                "-s", config.session_name,
                "--", "powershell.exe", "-NoLogo", "-NoProfile", "-NoExit",
            ],
            "psmux session creation",
        )  # fmt: skip
        pattern = re.compile(
            r"^[1-9][0-9]*:[1-9][0-9]*\t(\$[0-9]+)\t" + re.escape(config.session_name) + "$"
        )
        for _ in range(50):
            identity = self.psmux_run(
                [
                    "-L",
                    config.namespace_name,
                    "display-message",
                    "-p",
                    "-t",
                    config.session_name,
                    IDENTITY_FORMAT,
                ],
                "psmux session identity query",
            )
            if identity.exit_code == 0 and len(identity.output) == 1:
                match = pattern.match(identity.output[0])
                if match:
                    pid = int(identity.output[0].split(":")[0])
                    start = self.platform.process_start(pid)
                    if start is None:
                        raise SmokeError(f"The psmux session process {pid} is not running.")
                    self.created_identity = identity.output[0]
                    self.created_pid = pid
                    self.created_start = start
                    self.created_session_id = match.group(1)
                    break
            time.sleep(0.1)
        if creation.exit_code != 0:
            raise SmokeError(
                exit_message(
                    "psmux new-session after the creation attempt",
                    creation.exit_code,
                    creation.error,
                    "\n".join(creation.output),
                )
            )
        if not self.created_session_id:
            raise SmokeError("psmux created no session with an exact verifiable identity.")

    def show_fixture(self) -> None:
        """Print the UTF-8 fixture into the session and wait until it can be captured."""
        namespace, session = self.config.namespace_name, self.config.session_name
        sent = self.psmux_run(
            ["-L", namespace, "send-keys", "-t", f"{session}:0.0", FIXTURE_COMMAND, "Enter"],
            "psmux fixture input",
        )
        self.require_zero(sent, "psmux fixture input")
        for _ in range(50):
            captured = self.psmux_run(
                ["-L", namespace, "capture-pane", "-p", "-t", f"{session}:0.0"],
                "psmux pane capture",
            )
            self.require_zero(captured, "psmux pane capture")
            if any(FIXTURE_TEXT in line for line in captured.output):
                return
            time.sleep(0.1)
        raise SmokeError("The UTF-8 fixture did not become visible in the smoke pane.")

    def run_legs(self, files: dict[str, pathlib.Path]) -> None:
        """Run the native legs, and the WSL legs when they were asked for."""
        config = self.config
        dotnet = str(files["dotnet"])
        environment = {
            "LIBTMUX_PSMUX_BINARY": str(files["psmux"]),
            "LIBTMUX_PSMUX_EXPECTED_TEXT": FIXTURE_TEXT,
            "LIBTMUX_PSMUX_NAMESPACE": config.namespace_name,
            "LIBTMUX_PSMUX_SHA256": config.expected_sha256.lower(),
            "LIBTMUX_PSMUX_SMOKE": "1",
        }
        os.environ.update(environment)
        data = self.platform.fs_path(self.data_directory)
        native_result = data / "libtmux-native-result.xml"
        self.result_paths.append(native_result)
        smoke = self.runner.run(
            dotnet,
            [
                str(files["test"]),
                "-noColor",
                "-noLogo",
                "-failSkips",
                "-result-xml",
                str(native_result),
                "-class",
                SMOKE_CLASS,
            ],
            self.data_directory,
            180,
            "Native Windows .NET smoke",
            kill_tree=True,
        )
        self.require_zero(smoke, "Native Windows .NET smoke")
        assert_one_passing_test(native_result, "Native Windows .NET")
        for key, leg in (
            ("example", "Native Windows example"),
            ("package", "Native Windows packed consumer"),
        ):
            result = self.runner.run(
                dotnet, [str(files[key]), "--psmux"], self.data_directory, 60, leg, kill_tree=True
            )
            assert_query_program(result.output, result.exit_code, result.error, FIXTURE_TEXT, leg)
        if config.run_wsl_smoke:
            self.run_wsl_legs(files, environment)

    def run_wsl_legs(self, files: dict[str, pathlib.Path], environment: dict[str, str]) -> None:
        """Query the same server through WSL, which is only a client here."""
        repository = self.wsl_repository
        dotnet = self.wsl_dotnet
        converted = {
            name: self.convert_to_wsl_path(str(files[key]), label)
            for name, key, label in (
                ("psmux", "psmux", "audited psmux"),
                ("test", "test", "unit-test assembly"),
                ("example", "example", "example assembly"),
                ("package", "package", "package-consumer assembly"),
            )
        }
        wsl_data = self.convert_to_wsl_path(self.data_directory, "isolated data-directory")
        result_path = self.platform.fs_path(self.data_directory) / "libtmux-wsl-result.xml"
        self.result_paths.append(result_path)
        common = [
            f"LIBTMUX_PSMUX_BINARY={converted['psmux']}",
            f"LIBTMUX_PSMUX_EXPECTED_TEXT={FIXTURE_TEXT}",
            f"LIBTMUX_PSMUX_NAMESPACE={self.config.namespace_name}",
        ]
        smoke = self.wsl_leg(
            "240s",
            300,
            "WSL .NET smoke",
            repository,
            [*common, f"LIBTMUX_PSMUX_SHA256={environment['LIBTMUX_PSMUX_SHA256']}", "LIBTMUX_PSMUX_SMOKE=1"],
            [
                dotnet, converted["test"], "-noColor", "-noLogo", "-failSkips",
                "-result-xml", f"{wsl_data.rstrip('/')}/libtmux-wsl-result.xml",
                "-class", SMOKE_CLASS,
            ],
        )  # fmt: skip
        self.require_zero(smoke, "WSL .NET smoke")
        assert_one_passing_test(result_path, "WSL .NET")
        for key, leg in (("example", "WSL example"), ("package", "WSL packed consumer")):
            result = self.wsl_leg(
                "120s", 180, leg, repository, common, [dotnet, converted[key], "--psmux"]
            )
            assert_query_program(result.output, result.exit_code, result.error, FIXTURE_TEXT, leg)

    def prepare_wsl(self, repository_path: str) -> None:
        """Resolve the WSL repository and validate ``dotnet`` once, by absolute path."""
        config = self.config
        self.wsl_repository = repository_path
        major = "10" if config.target_framework == "net10.0" else "8"
        validation = self.runner.run(
            "wsl.exe",
            wsl_command(
                config,
                "15s",
                repository_path,
                "/bin/sh",
                "-eu",
                "-c",
                WSL_DOTNET_VALIDATION_SCRIPT,
                "libtmux-dotnet-validation",
                config.wsl_dotnet_path,
                major,
            ),
            self.data_directory,
            30,
            "WSL .NET executable validation",
            kill_tree=True,
        )
        self.require_zero(validation, "WSL .NET executable validation")
        if len(validation.output) != 1 or not validation.output[0].strip():
            raise SmokeError("WSL .NET executable validation returned no canonical path.")
        resolved = validation.output[0]
        if (
            resolved != resolved.strip()
            or not resolved.startswith("/")
            or has_control_character(resolved)
        ):
            raise SmokeError("WSL .NET executable validation returned an invalid canonical path.")
        self.wsl_dotnet = resolved

    def cleanup_session(self) -> None:
        """Kill the exact session this run created, and prove it is gone."""
        if not self.creation_attempted:
            return
        sid = (
            self.platform.fs_path(self.data_directory)
            / f"{self.config.namespace_name}__{self.config.session_name}.sid"
        )
        if not self.created_session_id:
            raise SmokeError(
                "A creation attempt has no exact identity; retaining its data directory."
            )
        # A $N target resolves to its complete namespaced registry base, so -L
        # here would prefix the namespace a second time.
        current = self.psmux_run(
            ["display-message", "-p", "-t", self.created_session_id, IDENTITY_FORMAT],
            "psmux cleanup identity query",
        )
        if current.exit_code != 0:
            if sid.exists():
                raise SmokeError(
                    exit_message(
                        "psmux cleanup identity query",
                        current.exit_code,
                        current.error,
                        "\n".join(current.output),
                    )
                )
        elif len(current.output) != 1:
            if sid.exists():
                raise SmokeError(
                    "The created session still exists but its identity cannot be verified."
                )
        elif current.output[0] != self.created_identity:
            raise SmokeError(
                "The created session identity changed; refusing to kill its replacement."
            )
        else:
            killed = self.psmux_run(
                ["kill-session", "-t", self.created_session_id], "psmux exact session cleanup"
            )
            self.require_zero(killed, "psmux exact session cleanup")
            for _ in range(50):
                if not sid.exists():
                    break
                time.sleep(0.1)
            if sid.exists():
                raise SmokeError("The exact created session registry survived cleanup.")
        for _ in range(50):
            if not self.process_alive():
                break
            time.sleep(0.1)
        if self.process_alive():
            raise SmokeError("The exact created session process survived cleanup.")

    def process_alive(self) -> bool:
        """Say whether the created session's exact process, by start time, is running."""
        return self.platform.process_start(self.created_pid) == self.created_start

    def cleanup_files(self, *, retain_directory: bool) -> list[Exception]:
        """Remove the files and the directory this run owns, collecting every failure."""
        failures: list[Exception] = []
        owned = [*self.result_paths, *([self.config_path] if self.config_path else [])]
        for path in owned:
            try:
                if path.is_file():
                    if path == self.config_path and not self.config_created:
                        raise SmokeError(
                            "Refusing to remove a configuration file this run did not create."
                        )
                    path.unlink()
            except (OSError, SmokeError) as error:
                failures.append(error)
        directory = self.platform.fs_path(self.data_directory)
        try:
            if (
                self.directory_created
                and not failures
                and not retain_directory
                and directory.is_dir()
            ):
                if list(directory.rglob("*.port")):
                    raise SmokeError(
                        "The owned data directory still contains a live psmux registry."
                    )
                shutil.rmtree(directory)
        except (OSError, SmokeError) as error:
            failures.append(error)
        return failures

    def run(self) -> None:
        """Run the whole smoke and its cleanup; raise the first failure with cleanup's."""
        config = self.config
        primary: Exception | None = None
        cleanup: list[Exception] = []
        try:
            self.isolate_environment()
            if config.run_wsl_smoke:
                self.validate_wsl_inputs()
            files = self.validate_files()
            self.psmux = str(files["psmux"])
            self.data_directory = isolated_data_directory(config.data_directory, self.platform)
            if self.platform.fs_path(self.data_directory).exists():
                raise SmokeError(
                    "DataDirectory must not exist; choose a fresh high-entropy path for this run."
                )
            self.platform.fs_path(self.data_directory).mkdir(parents=True)
            self.directory_created = True
            os.environ["PSMUX_DATA_DIR"] = self.data_directory
            os.environ["PSMUX_NO_WARM"] = "1"
            if config.run_wsl_smoke:
                self.prepare_wsl(self.convert_to_wsl_path(config.wsl_repository, "repository"))
            banner = self.psmux_run(["-V"], "psmux version query")
            self.require_zero(banner, "psmux version query")
            if banner.output != EXPECTED_BANNER:
                raise SmokeError(f"psmux reported an unaudited banner: {' | '.join(banner.output)}")
            self.create_session()
            self.show_fixture()
            self.run_legs(files)
        except Exception as error:
            primary = error
        finally:
            try:
                self.cleanup_session()
            except Exception as error:
                cleanup.append(error)
            cleanup.extend(self.cleanup_files(retain_directory=bool(cleanup)))
            with contextlib.suppress(Exception):
                self.restore_environment()
        if primary is not None:
            if cleanup:
                failures = "; ".join(str(error) for error in cleanup)
                raise SmokeError(f"{primary} Cleanup also failed: {failures}") from primary
            raise primary
        if cleanup:
            raise SmokeError(
                "The psmux smoke cleanup failed: " + "; ".join(str(error) for error in cleanup)
            )


def parse_arguments(argv: list[str] | None = None) -> Config:
    """Parse and validate the command line, exiting with status 2 on a usage error."""
    parser = argparse.ArgumentParser(
        prog="psmux_smoke.py", description=(__doc__ or "").split("\n\n")[0]
    )
    parser.add_argument("--psmux-path", required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--data-directory", required=True)
    parser.add_argument("--namespace-name", required=True)
    parser.add_argument("--dotnet-path", required=True)
    parser.add_argument("--test-assembly", required=True)
    parser.add_argument("--example-assembly", required=True)
    parser.add_argument("--package-consumer-assembly", required=True)
    parser.add_argument("--target-framework", required=True, choices=("net8.0", "net10.0"))
    parser.add_argument("--wsl-distribution", default="")
    parser.add_argument("--wsl-repository", default="")
    parser.add_argument("--wsl-dotnet-path", default="")
    parser.add_argument("--run-wsl-smoke", action="store_true")
    parser.add_argument("--session-name", default="smoke")
    arguments = parser.parse_args(argv)
    if not re.fullmatch(r"[0-9A-Fa-f]{64}", arguments.expected_sha256):
        parser.error("--expected-sha256 must be 64 hexadecimal digits")
    if arguments.expected_sha256.lower() != SUPPORTED_SHA256:
        parser.error("--expected-sha256 must match the exact audited psmux client build.")
    if not re.fullmatch(r"(?!default$)(?!.*__)[a-z0-9_-]{16,64}", arguments.namespace_name):
        parser.error(
            "--namespace-name must be 16 to 64 of a-z, 0-9, _ and -, not default, with no '__'"
        )
    if not re.fullmatch(r"(?!.*__)[A-Za-z0-9_-]+", arguments.session_name):
        parser.error("--session-name must be letters, digits, _ and - with no '__'")
    return Config(
        psmux_path=arguments.psmux_path,
        expected_sha256=SUPPORTED_SHA256,
        data_directory=arguments.data_directory,
        namespace_name=arguments.namespace_name,
        dotnet_path=arguments.dotnet_path,
        test_assembly=arguments.test_assembly,
        example_assembly=arguments.example_assembly,
        package_consumer_assembly=arguments.package_consumer_assembly,
        target_framework=arguments.target_framework,
        wsl_distribution=arguments.wsl_distribution,
        wsl_repository=arguments.wsl_repository,
        wsl_dotnet_path=arguments.wsl_dotnet_path,
        run_wsl_smoke=arguments.run_wsl_smoke,
        session_name=arguments.session_name,
    )


def main(argv: list[str] | None = None) -> int:
    """Run the smoke and answer the process status."""
    config = parse_arguments(argv)
    try:
        Smoke(config, Runner(), WindowsPlatform()).run()
    except SmokeError as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
