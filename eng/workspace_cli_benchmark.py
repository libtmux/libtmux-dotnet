#!/usr/bin/env python3
"""Exercise and time an installed tmux-workspace tool on private sockets."""

import argparse
import json
import os
from pathlib import Path
import statistics
import site
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--reference-python", default=os.environ.get("TMUX_WORKSPACE_PYTHON", sys.executable))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.samples < 2:
        parser.error("--samples must be at least 2 to report spread")
    executable = str(args.executable.resolve())
    owned = Path(tempfile.gettempdir()) / "libtmux-dotnet-test"
    owned.mkdir(exist_ok=True)
    timings = {}
    verified = []

    def summarize(samples):
        return {"samples_ms": samples, "median_ms": statistics.median(samples),
                "minimum_ms": min(samples), "maximum_ms": max(samples),
                "stdev_ms": statistics.stdev(samples)}

    with tempfile.TemporaryDirectory(prefix="installed-", dir=owned) as temporary:
        root = Path(temporary)
        socket = str(root / "tmux")
        config = root / "configs"
        config.mkdir()
        env = dict(os.environ, HOME=str(root), TMUXP_CONFIGDIR=str(config),
                   TMUX_TMPDIR=str(root), NO_COLOR="1", EDITOR="/bin/true",
                   PYTHONUSERBASE=site.getuserbase())
        env.pop("TMUX", None)
        env.pop("TMUX_PANE", None)
        document = {"session_name": "benchmark", "start_directory": str(root),
                    "windows": [{"window_name": "editor", "window_index": 0,
                                 "panes": [None, {"focus": True}]},
                                {"window_name": "shell", "window_index": 4,
                                 "panes": [None]}]}
        workspace = config / "benchmark.json"
        workspace.write_text(json.dumps(document))
        teamocil = root / "teamocil.json"
        teamocil.write_text(json.dumps({"name": "imported", "windows": [
            {"name": "editor", "panes": [{"cmd": "echo ready"}]}]}))
        tmuxinator = root / "tmuxinator.json"
        tmuxinator.write_text(json.dumps({"name": "imported", "windows": [
            {"editor": "echo ready"}]}))

        def run(arguments, *, expected=0, prefix=None):
            start = time.perf_counter_ns()
            result = subprocess.run([*(prefix or [executable]), *arguments], cwd=root, env=env,
                                    text=True, capture_output=True, timeout=30)
            elapsed = (time.perf_counter_ns() - start) / 1e6
            assert result.returncode == expected, (arguments, result.returncode,
                                                    result.stdout, result.stderr)
            return result, elapsed

        def machine(arguments, check):
            for mode in ("--json", "--ndjson"):
                result, _ = run([*arguments, mode])
                assert "\x1b" not in result.stdout
                records = [json.loads(line) for line in result.stdout.splitlines()]
                check(records[0] if mode == "--json" else records)
            verified.append(" ".join(arguments[:2]))

        def kill():
            subprocess.run([env.get("LIBTMUX_TMUX", "tmux"), "-S", socket, "kill-server"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=5, check=False)

        try:
            for leaf in ("load", "freeze", "convert", "import teamocil", "import tmuxinator",
                         "ls", "search", "edit", "debug-info", "shell"):
                result, _ = run([*leaf.split(), "--help"])
                assert "Usage:" in result.stdout
            for generated in ("reference", "man", "bash", "zsh", "fish"):
                result, _ = run(["--generate", generated])
                assert "tmux-workspace" in result.stdout.lower()
            for name, arguments in {
                "startup_version": ["--version"],
                "list_one_workspace": ["ls", "--json"],
                "search_one_workspace": ["search", "benchmark", "--json"],
            }.items():
                samples = [run(arguments)[1] for _ in range(args.samples)]
                timings[name] = summarize(samples)
            machine(["ls"], lambda value: None)
            machine(["search", "benchmark"], lambda value: None)
            machine(["convert", str(workspace)], lambda value: None)
            machine(["import", "teamocil", str(teamocil)], lambda value: None)
            machine(["import", "tmuxinator", str(tmuxinator)], lambda value: None)
            machine(["edit", str(workspace)], lambda value: None)
            machine(["debug-info"], lambda value: None)
            samples = []
            for _ in range(args.samples):
                kill()
                loaded, load_ms = run(["load", str(workspace), "-d", "-S", socket,
                                       "-f", "/dev/null", "--json"])
                assert json.loads(loaded.stdout)["status"] == "ok"
                frozen, freeze_ms = run(["freeze", "benchmark", "-S", socket, "--json"])
                capture = json.loads(frozen.stdout)
                assert [w["window_index"] for w in capture["windows"]] == [0, 4]
                assert [len(w["panes"]) for w in capture["windows"]] == [2, 1]
                assert all(p["start_directory"] == str(root)
                           for w in capture["windows"] for p in w["panes"])
                samples.append({"load_ms": load_ms, "freeze_ms": freeze_ms})
            timings["load_freeze"] = {"samples": samples,
                "load_median_ms": statistics.median(s["load_ms"] for s in samples),
                "freeze_median_ms": statistics.median(s["freeze_ms"] for s in samples)}
            machine(["freeze", "benchmark", "-S", socket], lambda value: None)
            machine(["shell", "benchmark", "-S", socket, "--code", "--no-startup",
                     "-c", "print(session.session_name)"], lambda value: None)
            stream = dict(document, session_name="stream",
                          before_script="/bin/sh -c 'printf \"\\033[31mstart\\n\"; sleep 0.4; printf done'")
            stream_path = config / "stream.json"
            stream_path.write_text(json.dumps(stream))
            process = subprocess.Popen([executable, "load", str(stream_path), "-d", "-S",
                                        socket, "--ndjson"], cwd=root, env=env,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            first = process.stdout.readline()
            assert json.loads(first)["event"] == "started"
            assert process.poll() is None, "first event must arrive while load is running"
            remainder, errors = process.communicate(timeout=30)
            assert process.returncode == 0, errors
            events = [json.loads(line) for line in (first + remainder).splitlines()]
            assert [event["sequence"] for event in events] == list(range(1, len(events) + 1))
            assert sum(event["event"] in ("completed", "failed") for event in events) == 1
            assert any("\x1b" in event.get("data", {}).get("text", "") for event in events)
            verified.extend(["load", "freeze", "stream-first-event", "stream-control-bytes"])
            stream_path.unlink()

            version, _ = run(["-c", "import importlib.metadata; print(importlib.metadata.version('tmuxp'))"], prefix=[args.reference_python])
            assert version.stdout.strip() == "1.74.0", "comparison requires tmuxp 1.74.0"
            reference = [args.reference_python, "-u", "-c",
                         "from tmuxp.cli import cli; import sys; cli(sys.argv[1:])"]
            comparison = {}
            for lane, prefix in (("native", [executable]), ("tmuxp_1.74.0", reference)):
                measured = {}
                for name, arguments in {
                    "startup_version": ["--version"],
                    "list_one_workspace": ["ls", "--json"],
                    "search_one_workspace": ["search", "benchmark", "--json"],
                }.items():
                    samples = []
                    for _ in range(args.samples):
                        result, elapsed = run(arguments, prefix=prefix)
                        if name == "startup_version":
                            assert "Usage:" not in result.stdout and "usage:" not in result.stdout
                        elif name == "list_one_workspace":
                            assert [row["name"] for row in json.loads(result.stdout)["workspaces"]] == ["benchmark"]
                        else:
                            assert [row["name"] for row in json.loads(result.stdout)] == ["benchmark"]
                        samples.append(elapsed)
                    measured[name] = summarize(samples)
                load_times, freeze_times = [], []
                capture_path = root / "capture.yaml"
                for _ in range(args.samples):
                    kill()
                    _, elapsed = run(["load", str(workspace), "-d", "-S", socket,
                                      "-f", "/dev/null", "--no-progress"], prefix=prefix)
                    load_times.append(elapsed)
                    state = subprocess.run([env.get("LIBTMUX_TMUX", "tmux"), "-S", socket,
                                            "list-windows", "-t", "benchmark", "-F",
                                            "#{window_index}:#{window_panes}"],
                                           capture_output=True, text=True, timeout=5, check=True)
                    assert state.stdout.splitlines() == ["0:2", "4:1"], (lane, state.stdout)
                    _, elapsed = run(["freeze", "benchmark", "-S", socket,
                                      "--save-to", str(capture_path), "--workspace-format", "yaml",
                                      "--yes", "--force"], prefix=prefix)
                    freeze_times.append(elapsed)
                    checked, _ = run(["-c", "import json,sys,yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1]))))",
                                      str(capture_path)], prefix=[args.reference_python])
                    captured = json.loads(checked.stdout)
                    assert captured["session_name"] == "benchmark"
                    assert [window["window_name"] for window in captured["windows"]] == ["editor", "shell"]
                    assert [len(window["panes"]) for window in captured["windows"]] == [2, 1]
                measured["detached_load"] = summarize(load_times)
                measured["freeze_yaml_file"] = summarize(freeze_times)
                comparison[lane] = measured
            timings["comparison"] = comparison
            verified.append("pinned-reference-comparison")
        finally:
            kill()
    args.output.write_text(json.dumps({"timings": timings, "verified": verified,
                                       "samples": args.samples,
                                       "timing_boundary": "subprocess start through exit; correctness checks excluded; detached load starts from a stopped private server; freeze writes YAML in both lanes"}, indent=2) + "\n")
    print(json.dumps({"output": str(args.output), "checks": len(verified)}))


if __name__ == "__main__":
    main()
