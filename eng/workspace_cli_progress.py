#!/usr/bin/env python3
"""Verify native load progress with isolated tmux sessions and stderr terminals."""

import argparse
import fcntl
import json
import os
from pathlib import Path
import pty
import selectors
import shlex
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("module", type=Path)
    parser.add_argument("--installed", action="store_true", help="Run an installed executable instead of a dotnet module.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--tmux", default=os.environ.get("LIBTMUX_TMUX", "tmux"))
    args = parser.parse_args()
    module = args.module.resolve()
    command = [str(module)] if args.installed else ["dotnet", str(module)]
    observations = []
    owned = Path(tempfile.gettempdir()) / "libtmux-dotnet-test"
    owned.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="progress-", dir=owned) as temporary:
        root = Path(temporary)
        socket = root / "tmux"
        trace = root / "tmux-argv"
        wrapper = root / "tmux-wrapper"
        wrapper.write_text("#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + shlex.quote(str(trace)) + "\n" + """
case " $* " in
  *" attach-session "*)
    if test "$PROGRESS_ATTACH" = 1; then printf 'INTERACTIVE-HANDOFF' >&2; exit 0; fi ;;
  *" send-keys "*)
    if test -n "$PROGRESS_GATE" && ! test -e "$PROGRESS_GATE-start"; then
      touch "$PROGRESS_GATE-start"
      while ! test -e "$PROGRESS_GATE-release"; do sleep .01; done
    fi ;;
esac
""" + "exec " + shlex.quote(args.tmux) + " \"$@\"\n")
        wrapper.chmod(0o700)
        environment = dict(os.environ, HOME=str(root), TMUX_TMPDIR=str(root), TMUXP_CONFIGDIR=str(root), TERM="xterm-256color", NO_COLOR="1", LIBTMUX_TMUX=str(wrapper))
        for name in ["TMUX", "TMUX_PANE", "TMUXP_PROGRESS", "TMUXP_PROGRESS_FORMAT", "TMUXP_PROGRESS_LINES"]:
            environment.pop(name, None)

        def tmux(*arguments):
            return subprocess.run([args.tmux, "-S", str(socket), *arguments], env=environment, capture_output=True, text=True, timeout=5)

        assert tmux("-f", "/dev/null", "new-session", "-d", "-s", "keeper").returncode == 0
        identity = tmux("display-message", "-p", "#{pid}:#{start_time}").stdout

        def invoke(arguments, *, variables=None, terminal=True, output_terminal=False, size=(80, 10), observe=None):
            master, slave = pty.openpty()
            output_master, output_slave = pty.openpty() if output_terminal else (None, None)
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", size[1], size[0], 0, 0))
            if output_terminal:
                fcntl.ioctl(output_slave, termios.TIOCSWINSZ, struct.pack("HHHH", size[1], size[0], 0, 0))
            child = subprocess.Popen([*command, *arguments], cwd=root,
                                     env={**environment, **(variables or {})},
                                     stdin=output_slave, stdout=output_slave if output_terminal else subprocess.PIPE,
                                     stderr=slave if terminal else subprocess.PIPE)
            os.close(slave)
            if output_terminal:
                os.close(output_slave)
            captured = {"stdout": bytearray(), "stderr": bytearray()}
            deadline = time.monotonic() + 12
            try:
                with selectors.DefaultSelector() as selector:
                    selector.register(output_master if output_terminal else child.stdout, selectors.EVENT_READ, "stdout")
                    selector.register(master if terminal else child.stderr, selectors.EVENT_READ, "stderr")
                    while selector.get_map():
                        if time.monotonic() > deadline:
                            raise TimeoutError("native invocation exceeded twelve seconds")
                        for key, _ in selector.select(0.02):
                            try:
                                part = os.read(key.fd, 65536)
                            except OSError as error:
                                if key.fd not in [master if terminal else None, output_master] or error.errno != 5:
                                    raise
                                part = b""
                            if part:
                                captured[key.data].extend(part)
                            else:
                                selector.unregister(key.fileobj)
                        if observe:
                            observe(child, master, captured)
                child.wait(timeout=2)
                return child.returncode, captured["stdout"].decode(), captured["stderr"].decode()
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait()
                os.close(master)
                if child.stdout:
                    child.stdout.close()
                if child.stderr:
                    child.stderr.close()
                if output_terminal:
                    os.close(output_master)

        def check(name, arguments, assertion, **options):
            trace.write_text("")
            observation = {"case": name}
            try:
                code, stdout, stderr = invoke(arguments, **options)
                observation.update(exit_code=code, stdout=stdout, stderr=stderr, tmux_argv=trace.read_text())
                assertion(observation)
                observation["passed"] = True
            except (AssertionError, TimeoutError) as error:
                observation["passed"] = False
                observation["failure"] = str(error)
            observations.append(observation)

        def document(name, script=None):
            file = root / (name + ".json")
            config = {"session_name": name, "windows": [{"panes": [None]}]}
            if script:
                config["before_script"] = shlex.join([sys.executable, "-c", script])
            file.write_text(json.dumps(config))
            return ["load", str(file), "-d", "-S", str(socket), "-f", "/dev/null"]

        def silent(result):
            assert result["exit_code"] == 0, "disabled progress changed the result"
            assert result["stderr"] == "", "disabled progress wrote to stderr"

        def raw(result):
            assert result["exit_code"] == 0
            assert result["stdout"].startswith("out\x1b[31m尾"), "raw stdout bytes changed"
            assert "err尾" in result["stderr"], "raw stderr fragment lost"
            assert "out\x1b[31m尾" not in result["stderr"], "stdout moved to stderr"

        def failure(result):
            assert result["exit_code"] == 1
            assert "before_script exited with status 7" in result["stderr"]
            assert result["stderr"].rfind("\x1b[2K") < result["stderr"].index("before_script exited"), "diagnostic preceded progress cleanup"
            assert tmux("has-session", "-t", "=failed").returncode != 0, "failed owned session survived"
            assert tmux("has-session", "-t", "=keeper").returncode == 0, "borrowed keeper was removed"

        workspace = root / "workspace.json"
        workspace.write_text(json.dumps({"session_name": "progress", "windows": [{"window_name": "editor", "panes": [{"shell_command": [{"cmd": "echo sent", "sleep_after": 0.08}]}, None]}]}))
        load = ["load", str(workspace), "-d", "-S", str(socket), "-f", "/dev/null"]

        def counters(result):
            assert result["exit_code"] == 0, "native load failed"
            assert "COUNT 0/2" in result["stderr"], "initial native progress absent"
            assert "COUNT 1/2" in result["stderr"], "completed pane progress absent"
            assert "COUNT 2/2" in result["stderr"], "final pane progress absent"
            assert "\x1b[2K" in result["stderr"], "progress was not cleared"
            assert "COUNT" not in result["stdout"], "progress leaked to stdout"

        def invalid_environment(result):
            assert result["exit_code"] == 2, "invalid active progress environment accepted"
            assert result["tmux_argv"] == "", "invalid environment reached tmux"

        try:
            check("native-counters", [*load, "--progress-format", "COUNT {session_pane_progress}"], counters)
            gated = root / "gated.json"
            gated.write_text(json.dumps({"session_name": "gated", "windows": [{"panes": ["echo gated"]}]}))
            gate = root / "send-gate"
            blocked = []
            def observe_command(child, master, captured):
                if Path(str(gate) + "-start").exists() and not blocked:
                    blocked.append(time.monotonic())
                if len(blocked) == 1 and time.monotonic() - blocked[0] >= .08:
                    blocked.append(b"COUNT 1/1" in captured["stderr"])
                    Path(str(gate) + "-release").touch()
            def after_command(result):
                assert result["exit_code"] == 0 and len(blocked) == 2, "send-keys barrier failed"
                assert not blocked[1], "pane reported complete before send-keys executed"
                assert "COUNT 1/1" in result["stderr"], "completed command was not counted"
            check("counter-after-send", ["load", str(gated), "-d", "-S", str(socket), "--progress-format", "COUNT {session_pane_progress}"], after_command,
                  variables={"PROGRESS_GATE": str(gate)}, observe=observe_command)
            first = document("first-input")[1]
            second = document("second-input")[1]
            Path(second).write_text(json.dumps({"session_name": "second-input", "windows": [{"panes": [None, None, None]}]}))
            def multi_input(result):
                assert result["exit_code"] == 0
                frames = result["stderr"]
                assert frames.index("first-input 0/1") < frames.index("first-input 1/1") < frames.index("second-input 0/3") < frames.index("second-input 3/3"), "workspace counters did not reset"
            arguments = ["load", first, second, "-d", "-S", str(socket), "--progress-format", "{session} {session_pane_progress}"]
            check("multiple-inputs-reset", arguments, multi_input)
            def reused(result):
                assert result["exit_code"] == 0 and result["stderr"] == "", "reuse fabricated progress"
                assert result["stdout"] == "Using existing session first-input\nUsing existing session second-input\n"
            check("reused-inputs", arguments, reused)

            def handoff(result):
                assert result["exit_code"] == 0 and "attach-session" in result["tmux_argv"], "interactive handoff was not reached"
                assert "INTERACTIVE-HANDOFF" in result["stderr"]
                assert 0 <= result["stderr"].rfind("\x1b[2K") < result["stderr"].index("INTERACTIVE-HANDOFF"), "progress was not cleared before handoff"
                assert "HANDOFF-PROGRESS" not in result["stdout"], "progress used stdout's terminal"
            for name, variables in [("handoff", {}), ("handoff-empty-context", {"TMUX": ""})]:
                arguments = document(name)
                arguments.remove("-d")
                check("clear-before-" + name, [*arguments, "--progress-format", "HANDOFF-PROGRESS"], handoff, output_terminal=True,
                      variables={"PROGRESS_ATTACH": "1", **variables})
            check("active-environment-preflight", load, invalid_environment, variables={"TMUXP_PROGRESS_LINES": "invalid"})
            for name, extra, variables, terminal in [
                ("flag-disabled", ["--no-progress"], {}, True),
                ("environment-disabled", [], {"TMUXP_PROGRESS": "0"}, True),
                ("dumb-terminal", [], {"TERM": "dumb"}, True),
                ("redirected", [], {}, False),
                ("json", ["--json"], {}, True),
                ("ndjson", ["--ndjson"], {}, True),
            ]:
                check(name, [*document(name), *extra], silent, variables={"TMUXP_PROGRESS_LINES": "invalid", **variables}, terminal=terminal)
            script = "import os; os.write(1, 'out\\x1b[31m尾'.encode()); os.write(2, 'err尾'.encode())"
            check("raw-panel-zero", [*document("raw-zero", script), "--progress-lines", "0"], raw)
            check("raw-redirected", document("raw-pipe", script), raw, terminal=False)
            check("script-failure", document("failed", "import sys; print('before failure'); sys.exit(7)"), failure)

            def panel(result):
                assert result["exit_code"] == 0
                assert "tail-5999" in result["stderr"], "final bounded tail was lost"
                assert "tail-" not in result["stdout"], "panel output escaped to stdout"
                assert result["stderr"].count("PANEL") < 20, "script chunks caused excessive redraws"
                assert "\x1b[36m" not in result["stderr"], "NO_COLOR was ignored"
            script = "import os; [os.write(1, ('tail-%d ' % i + 'x'*128 + '\\n').encode()) for i in range(6000)]"
            check("bounded-coalesced-panel", [*document("panel", script), "--progress-format", "PANEL", "--progress-lines", "-1"], panel, size=(20, 5))

            release = root / "resize-release"
            resized = []
            def resize(child, master, captured):
                if not resized and b"RESIZE" in captured["stderr"]:
                    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", 4, 15, 0, 0))
                    resized.append(len(captured["stderr"]))
                    release.touch()
            script = "import pathlib,time,os; p=pathlib.Path(" + repr(str(release)) + ");\nwhile not p.exists(): time.sleep(.01)\nos.write(2,b'after-resize')"
            def resized_output(result):
                assert result["exit_code"] == 0 and resized, "resize handshake failed"
                following = result["stderr"].encode()[resized[0]:]
                assert b"after-resize" in following, "resize did not restore raw output"
                assert b"\x1b[1A" not in following, "resize erased rows with unknown reflow"
            check("resize-stops-drawing", [*document("resized", script), "--progress-format", "RESIZE"], resized_output, observe=resize)

            ready = root / "cancel-ready"
            cancelled = []
            def cancel(child, master, captured):
                if not cancelled and ready.exists():
                    child.send_signal(signal.SIGINT)
                    cancelled.append(True)
            script = "import pathlib,time,os; pathlib.Path(" + repr(str(ready)) + ").write_text(str(os.getpid())); time.sleep(60)"
            def cancellation(result):
                assert cancelled and result["exit_code"] == 130, "SIGINT primary status changed"
                assert "\x1b[2K" in result["stderr"], "cancelled frame was not cleared"
                assert not Path("/proc", ready.read_text()).exists(), "owned script survived cancellation"
                assert tmux("has-session", "-t", "=cancelled").returncode != 0, "cancelled owned session survived"
            check("cancellation-clears", document("cancelled", script), cancellation, observe=cancel)

            python = root / "python-bridge"
            python.write_text("#!/bin/sh\nif test \"$1\" = -c; then printf '1.74.0\\n'; exit 0; fi\nprintf 'bridge-out'\nprintf 'bridge-err' >&2\nexit \"$BRIDGE_STATUS\"\n")
            python.chmod(0o700)
            extension = root / "extension.json"
            extension.write_text(json.dumps({"session_name": "extension", "plugins": ["fixture"], "windows": [{"panes": [None]}]}))
            for status in [0, 7]:
                def bridge(result, status=status):
                    assert result["exit_code"] == status, "opaque bridge status changed"
                    assert "Loading Python workspace extensions" in result["stderr"]
                    assert "FALSE-NATIVE-COUNT" not in result["stderr"], "opaque bridge fabricated counters"
                    assert result["stdout"].count("bridge-out") == 1, "bridge stdout body replayed"
                    assert result["stderr"].count("bridge-err") == 1, "bridge stderr body replayed"
                    assert "bridge-err" not in result["stdout"], "bridge stderr moved to stdout"
                    assert ("Loaded Python" in result["stdout"]) == (status == 0), "human bridge status misreported success"
                    if status:
                        assert "\x1b[91mPython workspace extensions failed." in result["stdout"], "bridge failure used success color"
                check("opaque-bridge-" + str(status), ["load", str(extension), "-d", "--progress-lines", "0", "--progress-format", "FALSE-NATIVE-COUNT", "--color", "always"], bridge,
                      variables={"TMUX_WORKSPACE_PYTHON": str(python), "BRIDGE_STATUS": str(status), "NO_COLOR": ""})
        finally:
            args.output.write_text(json.dumps(observations, indent=2) + "\n")
            current = tmux("display-message", "-p", "#{pid}:#{start_time}")
            if current.returncode == 0 and current.stdout == identity:
                tmux("kill-server")
            else:
                raise RuntimeError("owned daemon identity changed; cleanup refused")
    passed = sum(item["passed"] for item in observations)
    print(f"{passed}/{len(observations)} native progress checks passed")
    return 0 if passed == len(observations) else 1


if __name__ == "__main__":
    raise SystemExit(main())
