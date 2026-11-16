#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Regenerate wrapped-captures.json from real tmux.

Each screen is printed into a 20-column pane, then captured twice: as rows
(capture-pane -p) and as joined lines (capture-pane -p -J). The unit tests
pair the two and look for every pattern the screen was built to hold, so
they need no tmux at test time.

```console
$ uv run scripts/wrapped-captures/generate_wrapped_captures.py \
    tests/Fixtures/wrapped-captures.json /path/to/tmux-3.3a /path/to/tmux-3.8
```
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shlex
import subprocess
import tempfile
import time
import typing as t

WIDTH = 20
HEIGHT = 18
FILL = "abcdefghijklmnopq"
SHAPES = {
    "plain wrapped line": [FILL + "NEEDLE_ONE_here"],
    "line indented by a pane width": ["x", " " * WIDTH + FILL + "NEEDLE_ONE_here"],
    "two lines indented by a pane width, then a wrapped line": [
        "x",
        " " * WIDTH + "first",
        " " * WIDTH + "second",
        FILL + "NEEDLE_TWO_here_zzzz",
    ],
    "pane width indent, a blank line, then a wrapped line": [
        "x",
        " " * WIDTH + "first",
        "   ",
        FILL + "NEEDLE_TWO_here_zzzz",
    ],
    "indent of two panes": [
        " " * 2 * WIDTH + FILL + "NEEDLE_ONE_here",
        FILL + "NEEDLE_TWO_here_zzzz",
    ],
    "mixed leading spaces": [
        "  a",
        " " * 7 + "b",
        " " * (WIDTH - 1) + "c",
        " " * (WIDTH + 1) + "d" + FILL + "NEEDLE_ONE_here",
        "e",
        FILL + "NEEDLE_TWO_here_zzzz",
    ],
    "empty lines between wrapped lines": [
        FILL + "NEEDLE_ONE_here",
        "",
        "",
        FILL + "NEEDLE_TWO_here_zzzz",
        "",
        "tail",
    ],
    "blank lines of every width": ["", " ", " " * 5, " " * WIDTH, " " * (WIDTH + 3), "after"],
    "exact width lines": ["a" * WIDTH, "b" * WIDTH, FILL + "NEEDLE_ONE_here", "c" * WIDTH, "end"],
    "spaces across the fill": [
        "abc" + " " * 17 + "DEF",
        "abc" + " " * 18 + "DEF" + FILL + "NEEDLE_ONE",
    ],
    "spaces only middle row": [
        "a" * WIDTH + " " * WIDTH + "END_OF_LINE",
        FILL + "NEEDLE_TWO_here_zzzz",
    ],
    "wide characters": ["你好世界" * 6 + "NEEDLE_ONE", "你" * 10, "你" * 9 + "ab" + "cd"],
    "wide characters after a pane width indent": [" " * WIDTH + "你好" * 12, "plain"],
    "trailing spaces before a wrapped line": ["abc   ", FILL + "NEEDLE_ONE_here"],
}
WORDS = ["ab", "xyz", "NEEDLE", "q", "hello", "0123456789", "W", "你好"]


def patterns_of(lines: list[str]) -> list[str]:
    """Return the words and the trimmed lines a capture of ``lines`` must contain."""
    found = []
    for line in lines:
        for word in line.split(" "):
            if word:
                found.append(word)
        stripped = line.rstrip(" ")
        if stripped.strip(" "):
            found.append(stripped)
    return sorted(set(found))


def random_line(rnd: random.Random) -> str:
    """Build one line from words, runs of spaces and whole pane widths of spaces."""
    parts = []
    for _ in range(rnd.randint(1, 8)):
        kind = rnd.randrange(4)
        if kind == 0:
            parts.append(" " * rnd.randint(1, 3 * WIDTH))
        elif kind == 1:
            parts.append(" " * WIDTH)
        else:
            parts.append(rnd.choice(WORDS))
    line = "".join(parts)
    if rnd.randrange(2):
        line += " " * rnd.randint(0, 2 * WIDTH)
    return line


def random_screen(rnd: random.Random) -> list[str]:
    """Build the lines of one screen that wrap but still fit the pane without scrolling."""
    lines = []
    rows = 0
    for _ in range(rnd.randint(1, 5)):
        line = "" if rnd.randrange(5) == 0 else random_line(rnd)
        cells = sum(2 if ord(c) > 0x2E7F else 1 for c in line)
        need = max(1, -(-cells // WIDTH)) + 1
        if rows + need > HEIGHT - 2:
            break
        lines.append(line)
        rows += need
    return lines


def capture(tmux: str, sock: str, *args: str) -> list[str]:
    """Run one tmux command against the socket and return its stdout as rows."""
    out = subprocess.run(
        [tmux, "-S", sock, "-f", "/dev/null", *args], capture_output=True, text=True, check=True
    ).stdout
    rows = out.split("\n")
    if rows and rows[-1] == "":
        rows.pop()
    return rows


def pane_command(tmux: str, sock: str, path: str, channel: str) -> str:
    """Return the shell command a pane runs: print the screen, signal, then wait.

    Every path is quoted, so a temporary directory with a space or a quote in
    its name still reaches the shell as one word.
    """
    quoted = [shlex.quote(value) for value in (path, tmux, sock, channel)]
    return f"cat {quoted[0]}; {quoted[1]} -S {quoted[2]} wait-for -S {quoted[3]}; sleep 600"


def record(
    tmux: str, sock: str, name: str, lines: list[str], scratch: str
) -> dict[str, t.Any] | None:
    """Print one screen into the pane and capture it as rows and as joined lines.

    Returns ``None`` when the screen scrolled, because a scrolled pane no longer
    holds the whole screen.
    """
    path = os.path.join(scratch, "screen.txt")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    channel = "done" + str(time.monotonic_ns())
    subprocess.run(
        [
            tmux,
            "-S",
            sock,
            "respawn-pane",
            "-k",
            "-t",
            "f:0.0",
            pane_command(tmux, sock, path, channel),
        ],
        check=True,
        capture_output=True,
    )
    subprocess.run([tmux, "-S", sock, "wait-for", channel], check=True, timeout=20)
    history = subprocess.run(
        [tmux, "-S", sock, "display-message", "-p", "-t", "f:0.0", "#{history_size}"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()
    if history != "0":
        return None
    return {
        "name": name,
        "lines": lines,
        "patterns": patterns_of(lines),
        "rows": capture(tmux, sock, "capture-pane", "-p", "-t", "f:0.0"),
        "joined": capture(tmux, sock, "capture-pane", "-p", "-J", "-t", "f:0.0"),
    }


def generate(
    builds: list[str],
    random_count: int = 60,
    shapes: dict[str, list[str]] | None = None,
) -> list[dict[str, t.Any]]:
    """Capture every named and random screen once per tmux build."""
    screens: list[dict[str, t.Any]] = []
    for tmux in builds:
        version = subprocess.run(
            [tmux, "-V"], capture_output=True, text=True, check=True
        ).stdout.split()[1]
        with tempfile.TemporaryDirectory() as scratch:
            sock = os.path.join(scratch, "sock")
            subprocess.run(
                [
                    tmux,
                    "-S",
                    sock,
                    "-f",
                    "/dev/null",
                    "new-session",
                    "-d",
                    "-x",
                    str(WIDTH),
                    "-y",
                    str(HEIGHT),
                    "-s",
                    "f",
                    "sleep 600",
                ],
                check=True,
            )
            try:
                rnd = random.Random(20261024)
                named = list((SHAPES if shapes is None else shapes).items())
                randoms = [(f"random {i}", random_screen(rnd)) for i in range(random_count)]
                for name, lines in named + randoms:
                    screen = record(tmux, sock, name, lines, scratch)
                    if screen is not None:
                        screen["tmux"] = version
                        screens.append(screen)
            finally:
                subprocess.run([tmux, "-S", sock, "kill-server"], capture_output=True, check=False)
    return screens


def main(argv: list[str] | None = None) -> int:
    """Write the fixture for the tmux builds named on the command line."""
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n\n")[0])
    parser.add_argument("output", help="the fixture to write, such as wrapped-captures.json")
    parser.add_argument("tmux", nargs="+", help="a tmux binary to capture with; repeat for each")
    parsed = parser.parse_args(argv)
    screens = generate(parsed.tmux)
    with open(parsed.output, "w", encoding="utf-8") as handle:
        json.dump({"width": WIDTH, "screens": screens}, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
