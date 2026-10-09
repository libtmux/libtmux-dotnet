"""Run the unchanged F# quickstart on owned endpoints and verify cleanup."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]


def process_identity(pid: int) -> str | None:
    """Read a live Linux process's start tick without accepting a reused PID."""
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    except FileNotFoundError:
        return None
    return None if fields[0] == "Z" else fields[19]


def scenario(dotnet: str, program: Path, selector: str, body: bool, cleanup: bool) -> str:
    base = Path(os.environ.get("TMUX_TMPDIR", "/tmp/libtmux-dotnet-test"))
    base.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="fsharp-quickstart-", dir=base))
    binary = shutil.which(os.environ.get("LIBTMUX_TMUX", "tmux"))
    if binary is None:
        raise RuntimeError("The configured tmux executable was not found.")
    binary = str(Path(binary).absolute())
    socket = root / (f"tmux-{os.getuid()}/ordinary" if selector == "name" else "ordinary.sock")
    socket.parent.mkdir(mode=0o700, exist_ok=True)
    environment = dict(os.environ)
    environment.pop("TMUX", None)
    environment.pop("TMUX_PANE", None)
    environment["TMUX_TMPDIR"] = str(root)

    def tmux(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [binary, "-S", str(socket), "-f", "/dev/null", *arguments],
            env=environment, text=True, capture_output=True, check=check, timeout=10,
        )

    pid = None
    identity = None
    failure = None
    output = ""
    stopped = False
    try:
        tmux("new-session", "-d", "-s", "keeper", "/bin/sh")
        pid = int(tmux("display-message", "-p", "#{pid}").stdout)
        identity = process_identity(pid)
        if identity is None:
            raise AssertionError("The owned daemon exited before the example.")
        tmux("set-hook", "-g", "after-new-session", "set-option -g @ordinary-created yes")
        wrapper = root / "tmux"
        faults = ""
        if body:
            faults += "case \"$*\" in *new-window*) echo 'injected body failure' >&2; exit 1;; esac\n"
        if cleanup:
            faults += "case \"$*\" in *kill-session*) echo 'injected cleanup failure' >&2; exit 1;; esac\n"
        wrapper.write_text("#!/bin/sh\n" + faults + "exec " + shlex.quote(binary) + ' "$@"\n')
        wrapper.chmod(0o700)
        child = dict(environment)
        child["LIBTMUX_SOCKET_PATH"] = str(socket) if selector == "path" else ""
        child["LIBTMUX_SOCKET_NAME"] = "../ignored" if selector == "path" else "ordinary"
        child["TMUX"] = "ignored malformed context"
        child["TMUX_PANE"] = "%77"
        child["PATH"] = str(root) + os.pathsep + child.get("PATH", "")
        result = subprocess.run([dotnet, str(program)], env=child, text=True, capture_output=True, timeout=40)
        output = result.stdout
        if (result.returncode == 0) != (not body and not cleanup):
            raise AssertionError(f"Unexpected example exit {result.returncode}: {result.stderr}")
        for requested, marker in ((body, "injected body failure"), (cleanup, "injected cleanup failure")):
            if requested and marker not in result.stderr:
                raise AssertionError(f"Missing {marker!r}: {result.stderr}")
        if not body and output != "window: tests\nwindows: 2\n":
            raise AssertionError(f"Unexpected example output: {output!r}")
        if tmux("show-option", "-gqv", "@ordinary-created").stdout.strip() != "yes":
            raise AssertionError("The example did not create a session on the selected endpoint.")
        expected = ["build", "keeper"] if cleanup else ["keeper"]
        sessions = sorted(tmux("list-sessions", "-F", "#{session_name}").stdout.splitlines())
        if sessions != expected:
            raise AssertionError(f"Unexpected remaining sessions: {sessions!r}; expected {expected!r}.")
    except Exception as error:
        failure = error
    finally:
        try:
            if pid is not None and identity is not None and process_identity(pid) == identity:
                current = int(tmux("display-message", "-p", "#{pid}").stdout)
                if current != pid:
                    raise RuntimeError("The endpoint now belongs to another daemon; cleanup refused.")
                tmux("kill-server")
                deadline = time.monotonic() + 5
                while process_identity(pid) == identity and time.monotonic() < deadline:
                    time.sleep(0.025)
                if process_identity(pid) == identity:
                    raise RuntimeError("The owned tmux daemon did not terminate.")
            stopped = pid is not None and identity is not None and process_identity(pid) != identity
            if not stopped:
                raise RuntimeError(f"Daemon termination was not verified; retained {root}.")
            shutil.rmtree(root)
        except Exception as error:
            if failure is not None:
                raise ExceptionGroup("Example and fixture cleanup failed.", [failure, error]) from None
            raise
    if failure is not None:
        raise failure
    print(f"PASS {program.parent.name} {selector} body_failure={body} cleanup_failure={cleanup}; daemon terminated", file=sys.stderr)
    return output


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    arguments = parser.parse_args()
    if not sys.platform.startswith("linux"):
        parser.error("This daemon-termination harness requires Linux /proc.")
    program = ROOT / "examples/LibTmux.FSharp.Quickstart/bin/Release" / arguments.framework / "LibTmux.FSharp.Quickstart.dll"
    if not program.is_file():
        parser.error(f"Build the quickstart before running it: {program}")
    output = scenario(arguments.dotnet, program, "name", False, False)
    for body, cleanup in ((False, False), (True, False), (False, True), (True, True)):
        scenario(arguments.dotnet, program, "path", body, cleanup)
    print(output, end="")


if __name__ == "__main__":
    main()
