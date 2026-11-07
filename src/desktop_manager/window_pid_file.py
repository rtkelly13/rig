"""Tell apart windows of one executable by the pid an app records for each instance.

Orca runs one window per profile (`--user-data-dir`), and every profile's window is
`Orca.exe` titled "Orca", so title, process and position cannot say which profile a
window belongs to. Each running profile does write `orca-runtime.json` into its
user-data folder with the `pid` of the main process, which is the process that owns
the window. An entry's `window_pid_file` names that file: a window qualifies for the
entry only when its owner pid is the one recorded there. A missing or unreadable file
means that instance is not running, so no window qualifies and restore launches it.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictInt

from desktop_manager.models import LaunchEntry
from desktop_manager.placeholders import substitute_placeholders
from rig.typed_json import try_decode_json


@dataclass(frozen=True, slots=True)
class _PidFile:
    pid: StrictInt | None = None


def recorded_pid(path: Path) -> int | None:
    """The `pid` in the JSON file at ``path``, or None when it is missing or malformed."""
    try:
        text = path.read_bytes()
    except OSError:
        return None
    parsed = try_decode_json(_PidFile, text)
    return parsed.pid if parsed is not None else None


def window_owner_allowed(entry: LaunchEntry, window_pid: int | None, substitutions: dict[str, str]) -> bool:
    """False when the entry pins its window to a recorded pid and this window is not it."""
    if not entry.window_pid_file:
        return True
    resolved = substitute_placeholders(entry.window_pid_file, substitutions) or entry.window_pid_file
    expected = recorded_pid(Path(resolved))
    return expected is not None and window_pid == expected


def pid_verified(entry: LaunchEntry, window_pid: int | None, substitutions: dict[str, str]) -> bool:
    """True when the entry pins its window to a recorded pid and this window is that one.

    Such a window is the entry's own instance wherever it sits, so restore may claim it
    from another desktop and move it, rather than launch a single-instance app again.
    """
    return bool(entry.window_pid_file) and window_owner_allowed(entry, window_pid, substitutions)
