"""Waiting for a launched entry to be ready before restore moves on."""

from __future__ import annotations

import time

from desktop_manager.models import LaunchEntry, RestoreRunLogger
from desktop_manager.restore_timing import (
    entry_title_takeover_timeout_seconds,
    launch_settle_delay_seconds,
    logger_callback,
    should_force_terminal_relaunch,
)


def wait_for_launched_entry_ready(
    entry: LaunchEntry,
    *,
    desktop_index: int,
    logger: RestoreRunLogger | None,
    excluded_hwnds: set[int] | None = None,
) -> int | None:
    from desktop_manager.win32.entry_windows import wait_for_window_for_entry, wait_for_window_title_change

    if should_force_terminal_relaunch(entry):
        started_at = time.monotonic()
        if logger is not None:
            logger.log(
                f"Waiting for launch readiness for {entry.name} on desktop {desktop_index} "
                f"excluded_hwnds={sorted(excluded_hwnds or set())}"
            )
        hwnd = wait_for_window_for_entry(
            entry,
            timeout_seconds=10.0,
            current_desktop_only=False,
            target_desktop_number=desktop_index,
            move_to_target_desktop=False,
            excluded_hwnds=excluded_hwnds,
            allow_process_fallback_for_terminals=True,
            diagnostic_callback=logger_callback(logger),
        ).hwnd
        if hwnd is None:
            raise RuntimeError(f"Could not detect launched window for {entry.name}")
        if logger is not None:
            logger.log(f"Detected launched window {hwnd} for {entry.name} after {time.monotonic() - started_at:.2f}s")
        # A bare shell (command deferred at logon) runs nothing that would retitle the window.
        if entry.window_title and entry.command:
            changed_title = wait_for_window_title_change(
                hwnd,
                entry.window_title,
                timeout_seconds=entry_title_takeover_timeout_seconds(entry),
                diagnostic_callback=logger_callback(logger),
            )
            if changed_title is None:
                raise RuntimeError(f"Timed out waiting for UI title takeover for {entry.name}")
            if logger is not None:
                logger.log(f"Confirmed UI title takeover for {entry.name}: {changed_title!r}")
        return hwnd

    delay_seconds = launch_settle_delay_seconds(entry)
    if delay_seconds > 0:
        time.sleep(delay_seconds)
    return None
