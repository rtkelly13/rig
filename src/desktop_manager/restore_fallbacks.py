"""Notes restore opens when a launch path is missing or there are too few screens."""

from __future__ import annotations

import contextlib
import os
import re
import subprocess
import tempfile
import time
from collections.abc import Sequence
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from desktop_manager import launching
from desktop_manager.launching import detached_creation_flags
from desktop_manager.models import (
    LaunchEntry,
    RestoreRunLogger,
)


@dataclass(frozen=True, slots=True)
class MissingPathGroup:
    """Launch entries on one desktop that failed with the same missing-path error."""

    desktop_index: int
    entries: list[LaunchEntry]


@dataclass(frozen=True, slots=True)
class MissingPathFallback:
    entry: LaunchEntry
    note_path: Path


@dataclass(slots=True)
class RestoreFailureFallbacks:
    missing_path_entries: dict[str, MissingPathGroup] = field(default_factory=dict)

    def record_missing_path(self, desktop_index: int, entry: LaunchEntry, message: str) -> None:
        normalized_message = normalize_missing_path_error_message(message)
        group = self.missing_path_entries.setdefault(normalized_message, MissingPathGroup(desktop_index, []))
        if group.desktop_index != desktop_index:
            return
        if any(existing.name == entry.name for existing in group.entries):
            return
        group.entries.append(entry)

    def has_missing_paths(self) -> bool:
        return bool(self.missing_path_entries)


def is_missing_path_error(err: Exception) -> bool:
    message = str(err)
    return "resolved path does not exist" in message and "Launch entry `" in message


_MISSING_PATH_ERROR_PATTERN = re.compile(
    r"^Launch entry `(?P<entry>.+?)` resolved (?P<field>path|cwd|arg) does not exist: (?P<value>.+)$"
)


def normalize_missing_path_error_message(message: str) -> str:
    match = _MISSING_PATH_ERROR_PATTERN.match(message)
    if match is None:
        return message
    field_name = match.group("field")
    path_value = match.group("value")
    return f"Launch target resolved {field_name} does not exist: {path_value}"


def _sanitize_notepad_stem(value: str) -> str:
    return "".join(ch if ch.isalnum() or ch in {"-", "_"} else "-" for ch in value).strip("-") or "workspace-error"


def _build_missing_path_note(error_message: str, entries: list[LaunchEntry]) -> str:
    lines = [
        "Workspace restore fallback",
        "",
        error_message,
        "",
        "Affected windows:",
    ]
    lines.extend(f"- {entry.name}" for entry in entries)
    return "\n".join(lines) + "\n"


def create_missing_path_fallback_entry(error_message: str, entries: list[LaunchEntry]) -> MissingPathFallback:
    primary_entry = entries[0]
    note_dir = Path(tempfile.gettempdir()) / "workspace-manager"
    note_dir.mkdir(parents=True, exist_ok=True)
    note_path = note_dir / f"{_sanitize_notepad_stem(primary_entry.name)}-missing-app.txt"
    note_path.write_text(_build_missing_path_note(error_message, entries), encoding="utf-8")
    fallback_entry = LaunchEntry(
        name=f"Restore Error - {primary_entry.name}",
        type="app",
        path="notepad.exe",
        args=[str(note_path)],
        delay_ms=300,
        placement=primary_entry.placement,
        window_title=note_path.name,
        window_process_name="notepad.exe",
        summary=f"Fallback for missing app: {primary_entry.name}",
    )
    return MissingPathFallback(fallback_entry, note_path)


def _notepad_path() -> str:
    return str(Path(os.environ.get("SYSTEMROOT") or r"C:\Windows") / "System32" / "notepad.exe")


def open_note(file_name: str, lines: Sequence[str], *, quiet_since: datetime | None = None) -> Path:
    """Write a note to the workspace-manager temp folder and open it in Notepad; return its path.

    With `quiet_since`, a note already written since then is rewritten but not opened
    again, so a job a tick reruns does not stack up Notepad windows.
    """
    note_dir = Path(tempfile.gettempdir()) / "workspace-manager"
    note_dir.mkdir(parents=True, exist_ok=True)
    note_path = note_dir / file_name
    already_open = (
        quiet_since is not None
        and note_path.exists()
        and datetime.fromtimestamp(note_path.stat().st_mtime).astimezone() >= quiet_since
    )
    note_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    if not already_open:
        with contextlib.suppress(OSError):
            subprocess.Popen([_notepad_path(), str(note_path)], creationflags=detached_creation_flags())
    return note_path


def open_monitor_shortfall_note(required: int, live: int, *, quiet_since: datetime | None = None) -> Path:
    """Write and open a Notepad note explaining a skipped restore, then return its path.

    Mirrors the missing-app fallback convention (a visible Notepad note instead of a
    crash) so a cold-boot monitor shortfall degrades gracefully rather than raising.
    """
    return open_note(
        "workspace-restore-skipped.txt",
        [
            "Workspace restore skipped",
            "",
            f"Plan needs {required} screens; only {live} detected.",
            "",
            "Reconnect displays (or wait for them to wake). The scheduled jobs retry the restore",
            "every 15 minutes until the screens are back; to run it now:",
            "  desktop-manager workspace restore",
        ],
        quiet_since=quiet_since,
    )


def open_missing_path_fallbacks(
    fallback_state: RestoreFailureFallbacks,
    *,
    logger: RestoreRunLogger | None,
) -> list[str]:
    from desktop_manager.win32.desktops import ensure_current_desktop_number
    from desktop_manager.win32.entry_windows import apply_window_placement

    actions: list[str] = []
    for error_message, group in fallback_state.missing_path_entries.items():
        desktop_index, entries = group.desktop_index, group.entries
        fallback = create_missing_path_fallback_entry(error_message, entries)
        fallback_entry, note_path = fallback.entry, fallback.note_path
        actions.append(f"Fallback note `{fallback_entry.name}` -> {note_path}")
        if logger is not None:
            entry_label = "entry" if len(entries) == 1 else "entries"
            logger.log(f"Opening fallback note for {len(entries)} missing-path {entry_label}: {note_path}")
        try:
            ensure_current_desktop_number(desktop_index, settle_seconds=0.1)
            launching.launch_entry(fallback_entry, {})
            time.sleep(max(fallback_entry.delay_ms, 0) / 1000)
            if fallback_entry.placement is not None:
                apply_window_placement(fallback_entry, target_desktop_number=desktop_index)
                if logger is not None:
                    logger.log(f"Applied placement {fallback_entry.placement.to_dict()} to {fallback_entry.name}")
        except Exception as err:
            actions.append(f"Fallback FAILED `{fallback_entry.name}`: {err}")
            if logger is not None:
                logger.exception(f"Fallback FAILED {fallback_entry.name}: {err}")
    return actions
