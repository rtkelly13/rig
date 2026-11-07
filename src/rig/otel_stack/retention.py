"""Keep 7 days of the collector's rotated files.

The file exporter's own `max_days` only prunes when a file rotates (at 100 MB),
which a quiet week may never trigger. `collector ensure` runs every tick, so it
prunes by age as well. The active `<signal>.jsonl` is never touched: the
collector holds it open and it is the newest data.
"""

from __future__ import annotations

import datetime as dt
import pathlib

from rig.otel_stack.paths import SIGNALS

KEEP_DAYS = 7


def expired(data: pathlib.Path, *, keep_days: int = KEEP_DAYS, now: dt.datetime) -> list[pathlib.Path]:
    """Rotated files (`<signal>-<timestamp>.jsonl`) last written more than *keep_days* ago."""
    if not data.is_dir():
        return []
    cutoff = (now - dt.timedelta(days=keep_days)).timestamp()
    rotated = (path for path in data.glob("*.jsonl") if any(path.name.startswith(f"{s}-") for s in SIGNALS))
    return sorted(path for path in rotated if path.stat().st_mtime < cutoff)


def prune(data: pathlib.Path, *, keep_days: int = KEEP_DAYS, now: dt.datetime, apply: bool) -> list[pathlib.Path]:
    """Delete (with *apply*) the expired rotated files; returns what was, or would be, removed."""
    doomed = expired(data, keep_days=keep_days, now=now)
    if apply:
        for path in doomed:
            path.unlink(missing_ok=True)
    return doomed
