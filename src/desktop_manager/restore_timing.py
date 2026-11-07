"""How long restore waits for a launched entry's window, and when it polls for readiness."""

from __future__ import annotations

import re
import time
import uuid
from collections.abc import Callable, Generator, Mapping
from contextlib import contextmanager
from dataclasses import dataclass, field

from desktop_manager.models import (
    LaunchEntry,
    RestoreRunLogger,
)
from rig import tracing

#: A restore phase taking longer than this is logged as slow when it ends.
SLOW_STEP_SECONDS = 15.0
#: A desktop launches and places every entry on it, so it gets longer before it counts as slow.
SLOW_DESKTOP_SECONDS = 90.0


@dataclass(slots=True)
class StepTimer:
    """Times restore phases: each is a trace span, logged when slow, and listed in one closing line."""

    logger: RestoreRunLogger | None
    scope: str
    clock: Callable[[], float] = time.perf_counter
    taken: list[str] = field(default_factory=list[str])

    @contextmanager
    def step(
        self,
        span: str,
        attrs: Mapping[str, tracing.AttributeValue] | None = None,
        *,
        label: str | None = None,
        slow_after: float = SLOW_STEP_SECONDS,
    ) -> Generator[tracing.Span]:
        name = label or span.removeprefix("restore.")
        started = self.clock()
        try:
            with tracing.span(span, scope=self.scope, **(attrs or {})) as current:
                yield current
        finally:
            seconds = self.clock() - started
            slow = seconds > slow_after
            self.taken.append(f"{name} {seconds:.1f}s" + (" (slow)" if slow else ""))
            if slow and self.logger is not None:
                self.logger.log(f"Slow step: {name} took {seconds:.1f}s (over {slow_after:.0f}s)")

    def log_summary(self) -> None:
        if self.logger is not None and self.taken:
            self.logger.log(f"Step timings: {', '.join(self.taken)}")


def logger_callback(logger: RestoreRunLogger | None) -> Callable[[str], None] | None:
    if logger is None:
        return None

    def _callback(message: str) -> None:
        logger.log(message)

    return _callback


def launch_settle_delay_seconds(entry: LaunchEntry) -> float:
    delay_seconds = max(entry.delay_ms, 0) / 1000
    return delay_seconds


_DEFAULT_WINDOW_WAIT_TIMEOUT_SECONDS = 10.0
_DEFAULT_TITLE_TAKEOVER_TIMEOUT_SECONDS = 15.0
_DEFAULT_WINDOW_WAIT_GRACE_SECONDS = 40.0


def entry_window_wait_timeout_seconds(entry: LaunchEntry) -> float:
    if entry.window_wait_timeout_ms is not None and entry.window_wait_timeout_ms > 0:
        return entry.window_wait_timeout_ms / 1000
    return _DEFAULT_WINDOW_WAIT_TIMEOUT_SECONDS


def entry_window_wait_grace_seconds(entry: LaunchEntry) -> float:
    """Extra window-wait time granted while the entry's process is already running.

    The base timeout is sized for a warm machine. At logon the launched process
    typically exists within a second but can take far longer to paint its first
    window -- Fork took over 20s on 2026-08-05 -- and the process-name poll has no
    way to distinguish "still starting" from "failed to start". Timing out then
    makes the retry pass launch a *second* instance, which is how one Fork entry
    ended up spawning four processes while its planned slot stayed empty.

    Terminals are excluded: they are matched by restore token and title takeover,
    where a missing window means the launch really did fail.
    """
    if entry.type == "terminal":
        return 0.0
    return _DEFAULT_WINDOW_WAIT_GRACE_SECONDS


def entry_title_takeover_timeout_seconds(entry: LaunchEntry) -> float:
    """Ceiling for waiting on a terminal's UI to repaint its window title.

    The wait returns the instant the title changes, so this only bounds the
    case where the takeover never happens. Honors ``window_wait_timeout_ms`` so
    a slow tool (OpenCode's banner can take ~10s) can be given more headroom and
    a fast one can fail sooner, defaulting to the historical 15s.
    """
    if entry.window_wait_timeout_ms is not None and entry.window_wait_timeout_ms > 0:
        return entry.window_wait_timeout_ms / 1000
    return _DEFAULT_TITLE_TAKEOVER_TIMEOUT_SECONDS


def _should_poll_for_launch_readiness(entry: LaunchEntry) -> bool:
    return entry.type == "terminal" and entry.shell == "wt-pwsh"


def should_force_terminal_relaunch(entry: LaunchEntry) -> bool:
    return _should_poll_for_launch_readiness(entry)


def new_terminal_restore_token(entry: LaunchEntry) -> str:
    normalized_name = re.sub(r"[^a-z0-9]+", "-", entry.name.casefold()).strip("-") or "terminal"
    return f"{normalized_name}-{uuid.uuid4().hex[:8]}"
