#!/usr/bin/env python3
"""Regenerate wrapped-captures.json from real tmux.

Each screen is printed into a 20-column pane, then captured twice: as rows
(capture-pane -p) and as joined lines (capture-pane -p -J). The unit tests
pair the two and look for every pattern the screen was built to hold, so
they need no tmux at test time.

usage: generate-wrapped-captures.py OUT.json TMUX_3_3A TMUX_3_8
"""
import json
import os
import random
import subprocess
import sys
import tempfile
import time

WIDTH = 20
HEIGHT = 18
FILL = "abcdefghijklmnopq"
SHAPES = {
    "plain wrapped line": [FILL + "NEEDLE_ONE_here"],
    "line indented by a pane width": ["x", " " * WIDTH + FILL + "NEEDLE_ONE_here"],
    "two lines indented by a pane width, then a wrapped line": [
        "x", " " * WIDTH + "first", " " * WIDTH + "second", FILL + "NEEDLE_TWO_here_zzzz"],
    "pane width indent, a blank line, then a wrapped line": [
        "x", " " * WIDTH + "first", "   ", FILL + "NEEDLE_TWO_here_zzzz"],
    "indent of two panes": [" " * 2 * WIDTH + FILL + "NEEDLE_ONE_here", FILL + "NEEDLE_TWO_here_zzzz"],
    "mixed leading spaces": [
        "  a", " " * 7 + "b", " " * (WIDTH - 1) + "c", " " * (WIDTH + 1) + "d" + FILL + "NEEDLE_ONE_here",
        "e", FILL + "NEEDLE_TWO_here_zzzz"],
    "empty lines between wrapped lines": [
        FILL + "NEEDLE_ONE_here", "", "", FILL + "NEEDLE_TWO_here_zzzz", "", "tail"],
    "blank lines of every width": ["", " ", " " * 5, " " * WIDTH, " " * (WIDTH + 3), "after"],
    "exact width lines": ["a" * WIDTH, "b" * WIDTH, FILL + "NEEDLE_ONE_here", "c" * WIDTH, "end"],
    "spaces across the fill": ["abc" + " " * 17 + "DEF", "abc" + " " * 18 + "DEF" + FILL + "NEEDLE_ONE"],
    "spaces only middle row": ["a" * WIDTH + " " * WIDTH + "END_OF_LINE", FILL + "NEEDLE_TWO_here_zzzz"],
    "wide characters": ["你好世界" * 6 + "NEEDLE_ONE", "你" * 10, "你" * 9 + "ab" + "cd"],
    "wide characters after a pane width indent": [" " * WIDTH + "你好" * 12, "plain"],
    "trailing spaces before a wrapped line": ["abc   ", FILL + "NEEDLE_ONE_here"],
}
WORDS = ["ab", "xyz", "NEEDLE", "q", "hello", "0123456789", "W", "你好"]


def patterns_of(lines):
    found = []
    for line in lines:
        for word in line.split(" "):
            if word:
                found.append(word)
        stripped = line.rstrip(" ")
        if stripped.strip(" "):
            found.append(stripped)
    return sorted(set(found))


def random_line(rnd):
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


def random_screen(rnd):
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


def capture(tmux, sock, *args):
    out = subprocess.run([tmux, "-S", sock, "-f", "/dev/null", *args],
                         capture_output=True, text=True, check=True).stdout
    rows = out.split("\n")
    if rows and rows[-1] == "":
        rows.pop()
    return rows


def record(tmux, sock, name, lines, scratch):
    path = os.path.join(scratch, "screen.txt")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    channel = "done" + str(time.monotonic_ns())
    subprocess.run([tmux, "-S", sock, "respawn-pane", "-k", "-t", "f:0.0",
                    f"cat '{path}'; '{tmux}' -S '{sock}' wait-for -S {channel}; sleep 600"],
                   check=True, capture_output=True)
    subprocess.run([tmux, "-S", sock, "wait-for", channel], check=True, timeout=20)
    history = subprocess.run([tmux, "-S", sock, "display-message", "-p", "-t", "f:0.0", "#{history_size}"],
                             capture_output=True, text=True, check=True).stdout.strip()
    if history != "0":
        return None
    return {
        "name": name,
        "lines": lines,
        "patterns": patterns_of(lines),
        "rows": capture(tmux, sock, "capture-pane", "-p", "-t", "f:0.0"),
        "joined": capture(tmux, sock, "capture-pane", "-p", "-J", "-t", "f:0.0"),
    }


def main():
    out, *builds = sys.argv[1:]
    screens = []
    for tmux in builds:
        version = subprocess.run([tmux, "-V"], capture_output=True, text=True, check=True).stdout.split()[1]
        with tempfile.TemporaryDirectory() as scratch:
            sock = os.path.join(scratch, "sock")
            subprocess.run([tmux, "-S", sock, "-f", "/dev/null", "new-session", "-d", "-x", str(WIDTH),
                            "-y", str(HEIGHT), "-s", "f", "sleep 600"], check=True)
            try:
                rnd = random.Random(20261024)
                named = list(SHAPES.items())
                randoms = [(f"random {i}", random_screen(rnd)) for i in range(60)]
                for name, lines in named + randoms:
                    screen = record(tmux, sock, name, lines, scratch)
                    if screen is not None:
                        screen["tmux"] = version
                        screens.append(screen)
            finally:
                subprocess.run([tmux, "-S", sock, "kill-server"], capture_output=True)
    with open(out, "w", encoding="utf-8") as handle:
        json.dump({"width": WIDTH, "screens": screens}, handle, ensure_ascii=False, indent=1)
        handle.write("\n")


if __name__ == "__main__":
    main()
