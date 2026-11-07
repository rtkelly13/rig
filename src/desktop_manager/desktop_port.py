"""The machine as restore sees it: one port for every read, write, wait and sleep.

Restore reaches the desktop only through :class:`DesktopPort`, the way align reaches
it only through ``align.AlignPorts``. :class:`Win32DesktopPort` is the real machine:
each method imports its Win32 (or launcher) function at call time, so a test that
patches that function where it is defined still steers the restore.
"""

from __future__ import annotations

import time
from collections.abc import Callable, Sequence
from typing import TYPE_CHECKING, Protocol, override

from desktop_manager.commands.workspace_commands import WorkspaceCommands
from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    LiveWindow,
    PlacementOutcome,
    RestoreRunLogger,
    WindowMatch,
    WorkspacePlan,
)
from desktop_manager.queries.workspace_queries import WorkspaceQueries

if TYPE_CHECKING:
    from desktop_manager.restore_fallbacks import RestoreFailureFallbacks
    from desktop_manager.window_reuse import WindowMove


class DesktopPort(Protocol):
    """Everything restore reads from, does to, or waits on the machine."""

    # Reads.
    def current_workspace_status(self, desktop_count: int | None = None) -> WorkspacePlan: ...
    def current_workspace_status_for_desktop_indexes(self, desktop_indexes: list[int]) -> WorkspacePlan: ...
    def current_desktop_status(self, *, desktop_name: str | None = None) -> DesktopPlan: ...
    def current_window_handles_to_preserve(self) -> set[int]: ...
    def get_current_desktop_number(self) -> int | None: ...
    def inventory_reusable_windows(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]: ...
    def collect_windows_matching_process(self, process_name: str) -> set[int]: ...
    def window_titles_for_process(self, process_name: str) -> dict[int, str]: ...
    def is_process_running(self, process_name: str) -> bool: ...
    def is_window_target_confirmed(self, hwnd: int, desktop_index: int, entry: LaunchEntry) -> bool: ...
    def is_entry_target_confirmed(self, entry: LaunchEntry, desktop_index: int, *, timeout_seconds: float) -> bool: ...
    def wait_for_window_for_entry(
        self,
        entry: LaunchEntry,
        *,
        timeout_seconds: float,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        allow_process_fallback_for_terminals: bool = False,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> WindowMatch: ...
    def wait_for_window_title_change(
        self,
        hwnd: int,
        title: str,
        *,
        timeout_seconds: float,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> str | None: ...

    # Time.
    def clock(self) -> float: ...
    def sleep(self, seconds: float) -> None: ...

    # Writes.
    def ensure_windows(self) -> None: ...
    def settle_app_updates(self, plan: WorkspacePlan, *, logger: RestoreRunLogger | None) -> bool: ...
    def start_terminal_command_upgrades(self, plan: WorkspacePlan) -> None: ...
    def ensure_desktop_count(self, count: int, settle_seconds: float) -> None: ...
    def ensure_current_desktop_number(self, desktop_index: int, *, settle_seconds: float) -> bool: ...
    def rename_desktop(self, desktop_index: int, name: str) -> bool: ...
    def retire_switched_off_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]: ...
    def move_right(self, steps: int) -> None: ...
    def switch_leftmost(self, steps: int) -> None: ...
    def close_current_desktop_windows(self, *, preserved_hwnds: set[int]) -> int: ...
    def move_existing_window_to_entry(
        self, window: LiveWindow, desktop_index: int, entry: LaunchEntry
    ) -> WindowMove: ...
    def launch_entry(
        self, entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None
    ) -> int | None: ...
    def apply_window_placement(
        self,
        entry: LaunchEntry,
        *,
        hwnd: int | None = None,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        diagnostic_callback: Callable[[str], None] | None = None,
        timeout_seconds: float = 10.0,
    ) -> PlacementOutcome: ...
    def raise_windows_for_processes(self, process_names: set[str]) -> int: ...
    def open_missing_path_fallbacks(
        self,
        fallback_state: RestoreFailureFallbacks,
        *,
        logger: RestoreRunLogger | None,
    ) -> list[str]: ...
    def apply_second_pass_placements(
        self,
        plan: WorkspacePlan,
        *,
        logger: RestoreRunLogger | None,
        entries_by_desktop: dict[int, list[LaunchEntry]],
    ) -> list[str]: ...


class Win32DesktopPort(WorkspaceQueries, WorkspaceCommands):
    """The real machine: the restore queries and commands plus the reads, waits and sleeps they lacked."""

    def window_titles_for_process(self, process_name: str) -> dict[int, str]:
        from desktop_manager.win32.inventory import window_titles_for_process

        return window_titles_for_process(process_name)

    def is_process_running(self, process_name: str) -> bool:
        from desktop_manager.win32.inventory import is_process_running

        return is_process_running(process_name)

    def is_window_target_confirmed(self, hwnd: int, desktop_index: int, entry: LaunchEntry) -> bool:
        from desktop_manager.window_reuse import is_window_target_confirmed

        return is_window_target_confirmed(hwnd, desktop_index, entry)

    def is_entry_target_confirmed(self, entry: LaunchEntry, desktop_index: int, *, timeout_seconds: float) -> bool:
        from desktop_manager.window_reuse import is_entry_target_confirmed

        return is_entry_target_confirmed(entry, desktop_index, timeout_seconds=timeout_seconds)

    def clock(self) -> float:
        return time.monotonic()

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)

    def settle_app_updates(self, plan: WorkspacePlan, *, logger: RestoreRunLogger | None) -> bool:
        from desktop_manager.app_updates import settle_pending_app_updates

        return settle_pending_app_updates(plan, logger=logger)

    def start_terminal_command_upgrades(self, plan: WorkspacePlan) -> None:
        from desktop_manager.terminal_init import start_terminal_command_upgrades_for_plan

        start_terminal_command_upgrades_for_plan(plan)


class RestoreCompatibilityPort(Win32DesktopPort):
    """Port that preserves existing monkeypatch points in tests."""

    @override
    def launch_entry(
        self, entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None
    ) -> int | None:
        from desktop_manager.launching import launch_entry

        if restore_token is None:
            return launch_entry(entry, substitutions)
        try:
            return launch_entry(entry, substitutions, restore_token=restore_token)
        except TypeError as err:
            if "restore_token" not in str(err):
                raise
            return launch_entry(entry, substitutions)
