"""The few `launchctl` operations rig's LaunchAgents need, for the logged-in user's GUI domain.

Used by the always-on OTel collector agent (`otel_stack.service`).

`bootstrap`/`bootout` are the current verbs; `load`/`unload` are deprecated.
"""

from __future__ import annotations

import os
import pathlib
import subprocess
import sys


def agents_dir() -> pathlib.Path:
    return pathlib.Path.home() / "Library" / "LaunchAgents"


def domain() -> str:
    if sys.platform == "win32":
        raise OSError("LaunchAgents are macOS only")
    return f"gui/{os.getuid()}"


def load(path: pathlib.Path, plist: bytes) -> None:
    """Write *plist* to *path* and (re)load it, replacing any earlier copy."""
    path.parent.mkdir(parents=True, exist_ok=True)
    unload(path)
    _ = path.write_bytes(plist)
    _ = subprocess.run(["launchctl", "bootstrap", domain(), str(path)], capture_output=True, text=True, check=True)


def unload(path: pathlib.Path) -> None:
    """Stop and unload the agent at *path*, if it is loaded. The file stays."""
    _ = subprocess.run(["launchctl", "bootout", domain(), str(path)], capture_output=True, check=False)


def remove(path: pathlib.Path) -> bool:
    """Unload and delete the agent at *path*. False when it was not installed."""
    if not path.is_file():
        return False
    unload(path)
    path.unlink()
    return True


def loaded(label: str) -> bool:
    done = subprocess.run(["launchctl", "print", f"{domain()}/{label}"], capture_output=True, check=False)
    return done.returncode == 0


def kickstart(label: str, *, restart: bool = False) -> None:
    """Start the agent now; with *restart*, kill a running instance first."""
    args = ["launchctl", "kickstart", *(["-k"] if restart else []), f"{domain()}/{label}"]
    _ = subprocess.run(args, capture_output=True, check=True)
