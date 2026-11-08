"""Put `rig` on PATH as a command that runs the installed runtime copy.

`desktop-manager runtime install` writes `~/.local/bin/rig` (`rig.cmd` on Windows). It runs `rig` from
the runtime's `current` link, not from one copy, so later installs and rollbacks move it with no
rewrite. That gives every shell, including an agent's non-interactive one in another repo's worktree,
the same pinned rig the web service and background jobs run. A file at that path that rig did not
write is left alone.
"""

from __future__ import annotations

import sys
from enum import StrEnum
from pathlib import Path

from desktop_manager import runtime

#: In every shim rig writes; a file without it belongs to someone else.
MARKER = "Written by `desktop-manager runtime install`"


class Outcome(StrEnum):
    WRITTEN = "written"
    UNCHANGED = "unchanged"
    FOREIGN = "foreign"


def shim_path(home: Path | None = None) -> Path:
    name = "rig.cmd" if sys.platform == "win32" else "rig"
    return (home or Path.home()) / ".local" / "bin" / name


def render(target: Path, *, windows: bool = sys.platform == "win32") -> str:
    """The shim's text: run *target* with every argument passed through."""
    if windows:
        return f'@echo off\r\nrem {MARKER}. Runs rig from the runtime copy.\r\n"{target}" %*\r\n'
    quoted = target.as_posix().replace('"', '\\"')
    return f'#!/bin/sh\n# {MARKER}. Runs rig from the runtime copy.\nexec "{quoted}" "$@"\n'


def install(shim: Path | None = None, root: Path | None = None) -> Outcome:
    """Write the shim to the `current` copy's `rig`, unless the path holds a file rig didn't write."""
    path = shim or shim_path()
    text = render(runtime.executable(runtime.current_link(root), "rig"))
    if path.is_file():
        # Raw bytes: universal newlines would turn the Windows shim's CRLF into LF, and a lossy
        # decode could let a replacement character stand in for an invalid byte.
        existing = path.read_bytes()
        if MARKER.encode("utf-8") not in existing:
            return Outcome.FOREIGN
        if existing == text.encode("utf-8"):
            return Outcome.UNCHANGED
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text(text, encoding="utf-8", newline="")
    if sys.platform != "win32":
        path.chmod(0o755)
    return Outcome.WRITTEN
