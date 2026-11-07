"""Put `chrome-tabs` on PATH as a command that runs the installed runtime copy.

The command is a `chrome-tabs.cmd` shim in `~/.local/bin`, instead of
`uv tool install`, because the package's other entry points (`help`,
`workspace`, ...) would collide with tools already there. It points at the
`current` copy that `desktop-manager runtime install` maintains, so runtime
installs and rollbacks move it too. Scheduled organise runs belong to
`desktop-manager jobs install`.
"""

from __future__ import annotations

from pathlib import Path

SHIM_NAME = "chrome-tabs.cmd"


def shim_path() -> Path:
    return Path.home() / ".local" / "bin" / SHIM_NAME


def runtime_desktop_manager() -> Path:
    """`desktop-manager.exe` inside the runtime's `current` copy."""
    from desktop_manager import runtime

    return runtime.current_link() / ".venv" / "Scripts" / "desktop-manager.exe"


def find_checkout(start: Path) -> Path | None:
    """The shared-utilities checkout containing `start`, or None when running from an installed copy."""
    for directory in (start, *start.parents):
        pyproject = directory / "pyproject.toml"
        if pyproject.is_file():
            text = pyproject.read_text(encoding="utf-8")
            if 'name = "rig"' in text or 'name = "shared-utilities"' in text:
                return directory
    return None


def render_shim(desktop_manager: Path) -> str:
    return f'@echo off\r\n"{desktop_manager}" chrome-tabs %*\r\n'


def write_shim(desktop_manager: Path, shim: Path) -> None:
    shim.parent.mkdir(parents=True, exist_ok=True)
    _ = shim.write_text(render_shim(desktop_manager), encoding="utf-8", newline="")
