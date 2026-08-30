"""Resolve the XDG base directories this tool writes into.

Both the config files a swap edits and the releases it installs are located
this way, from two modules, so the rules for reading those variables live in
one place rather than being remembered twice.
"""

from __future__ import annotations

import os
import pathlib


def state_home() -> pathlib.Path:
    """Resolve ``$XDG_STATE_HOME`` per the XDG Base Directory spec.

    Defaults to ``~/.local/state`` when the env var is unset or empty.
    State is the right XDG bucket here (vs. cache / config / data): the
    file is machine-written, must persist across runs so ``revert`` can
    locate the right backup, but is not safely deletable like cache nor
    user-edited like config.
    """
    env = os.environ.get("XDG_STATE_HOME")
    if env:
        return pathlib.Path(env)
    return pathlib.Path.home() / ".local" / "state"


def config_home() -> pathlib.Path:
    """``$XDG_CONFIG_HOME`` when absolute, else ``~/.config``.

    The spec requires these variables to be absolute and says to ignore
    them otherwise. A relative value would resolve against the working
    directory, so the swap would record a backup path that revert could
    no longer find from anywhere else.
    """
    raw = os.environ.get("XDG_CONFIG_HOME")
    if raw and pathlib.Path(raw).is_absolute():
        return pathlib.Path(raw)
    return pathlib.Path.home() / ".config"
