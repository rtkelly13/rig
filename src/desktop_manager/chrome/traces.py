"""Organise-run traces: where they land, how long they are kept, and the reports read from them.

Every `organise` run is one `chrome_tabs.organise` span, with a child span per
phase: reading the windows, the GitHub lookups (one `github.graphql` span per
query, so per repo), close planning, closing window by window, group planning,
the idle/lock wait (one `check` event per look), the re-plan, and grouping window
by window. The spans go through `shared_utilities.tracing`, as OTLP-JSON lines, to

    %LOCALAPPDATA%/shared-utilities/chrome-tabs/traces/<YYYY-MM-DD>.ndjson

beside `organise.log`, flushed every `FLUSH_INTERVAL_S` and at exit. Files older
than `KEEP_DAYS` are deleted by the first scheduled run of each day (`prune_daily`)
or by `chrome-tabs traces prune`.

Reports run only in C# (`rig chrome-tabs traces`). This module retains the writer
and daily retention still used by the Python organise scheduler.
"""

from __future__ import annotations

import re
from contextlib import suppress
from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta
from pathlib import Path

from desktop_manager.chrome.install_location import install_root

SCOPE = "desktop_manager.chrome"
RUN_SPAN = "chrome_tabs.organise"
KEEP_DAYS = 30
FLUSH_INTERVAL_S = 30.0
_PRUNE_MARKER = ".last-prune"
_FILE = re.compile(r"^(\d{4}-\d{2}-\d{2})\.ndjson$")


def traces_dir() -> Path:
    return install_root() / "traces"


def start(directory: Path | None = None) -> None:
    """Send this process's spans to the organise traces directory, flushed on a timer."""
    from rig import tracing

    tracing.set_trace_dir(directory or traces_dir())
    tracing.flush_periodically(FLUSH_INTERVAL_S)


# -- retention -----------------------------------------------------------------


@dataclass(frozen=True, slots=True, order=True)
class TraceFile:
    """One day's trace file."""

    day: date
    path: Path


def trace_files(directory: Path) -> list[TraceFile]:
    """The day files in `directory`, oldest first. Anything not named `<YYYY-MM-DD>.ndjson` is ignored."""
    found: list[TraceFile] = []
    with suppress(OSError):
        for path in directory.iterdir():
            match = _FILE.match(path.name)
            if match and path.is_file():
                with suppress(ValueError):
                    found.append(TraceFile(date.fromisoformat(match.group(1)), path))
    return sorted(found)


def prune(
    directory: Path, *, keep_days: int = KEEP_DAYS, today: date | None = None, dry_run: bool = False
) -> list[Path]:
    """Delete day files more than `keep_days` old (by the date in the name); the files removed, or due."""
    cutoff = (today or datetime.now(UTC).date()) - timedelta(days=keep_days)
    due = [found.path for found in trace_files(directory) if found.day < cutoff]
    if dry_run:
        return due
    removed: list[Path] = []
    for path in due:
        with suppress(OSError):
            path.unlink()
            removed.append(path)
    return removed


def prune_daily(directory: Path, *, today: date | None = None) -> list[Path] | None:
    """`prune`, at most once a day: None when today's prune has already run. Never raises."""
    today = today or datetime.now(UTC).date()
    marker = directory / _PRUNE_MARKER
    try:
        if marker.is_file() and marker.read_text(encoding="utf-8").strip() == today.isoformat():
            return None
        directory.mkdir(parents=True, exist_ok=True)
        marker.write_text(today.isoformat(), encoding="utf-8")
    except OSError:
        return None
    return prune(directory, today=today)
