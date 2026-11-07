"""`estate wrapup --apply` in the background: one follower process asks the sessions, a few at a time.

Agents answer at their own pace (`estate wrapup report`), so the run that plans the asks does not wait
for them. It starts a follower, the same command with `--follow`, detached and logging to
`<app data>/logs/wrapup/`, and returns. The follower plans again (nothing has been sent yet, so this is
safe) and runs the rolling queue in `wrapup_watch`.

One follower at a time: it holds `<app data>/wrapup/follow.lock` for its whole run, so two runs never
ask the same session twice. The OS drops the lock if the follower dies.
"""

from __future__ import annotations

import contextlib
import datetime as dt
import sys
from collections.abc import Generator
from pathlib import Path
from typing import IO

from rig import roots, tracing


def lock_path() -> Path:
    return roots.app_data_dir() / "wrapup" / "follow.lock"


def log_dir() -> Path:
    return roots.app_data_dir() / "logs" / "wrapup"


def new_log(now: dt.datetime) -> Path:
    folder = log_dir()
    folder.mkdir(parents=True, exist_ok=True)
    return folder / f"follow-{now.astimezone(dt.UTC):%Y%m%dT%H%M%SZ}.log"


def _try_lock(handle: IO[bytes]) -> bool:
    try:
        if sys.platform == "win32":
            from rig.estate import follow_lock_windows

            follow_lock_windows.try_lock(handle)
        else:
            import fcntl

            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return False
    return True


def _unlock(handle: IO[bytes]) -> None:
    with contextlib.suppress(OSError):
        if sys.platform == "win32":
            from rig.estate import follow_lock_windows

            follow_lock_windows.unlock(handle)
        else:
            import fcntl

            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


@contextlib.contextmanager
def hold(path: Path | None = None) -> Generator[bool]:
    """Hold the follower lock for the block; yields False when another follower holds it."""
    target = path or lock_path()
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("a+b") as handle:
        if handle.seek(0, 2) == 0:
            _ = handle.write(b"0")
            handle.flush()
        acquired = _try_lock(handle)
        try:
            yield acquired
        finally:
            if acquired:
                _unlock(handle)


def running(path: Path | None = None) -> bool:
    """Whether a follower holds the lock now."""
    with hold(path) as acquired:
        return not acquired


def start(args: list[str], *, now: dt.datetime) -> Path:
    """Start `estate wrapup <args> --follow` detached; returns its log file."""
    log = new_log(now)
    _ = tracing.spawn_detached(
        [sys.executable, "-m", "rig.estate.cli", "wrapup", *args, "--follow"], scope="rig.estate.wrapup", log=log
    )
    return log
