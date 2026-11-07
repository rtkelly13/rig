from __future__ import annotations

from collections.abc import Callable, Sequence
from typing import TYPE_CHECKING

from desktop_manager.models import LaunchEntry, LiveWindow, PlacementOutcome, RestoreRunLogger, WorkspacePlan

if TYPE_CHECKING:
    from desktop_manager.restore_fallbacks import RestoreFailureFallbacks
    from desktop_manager.window_reuse import WindowMove


class WorkspaceCommands:
    """Side-effecting adapter for restore execution."""

    def ensure_windows(self) -> None:
        from desktop_manager.win32 import ensure_windows

        ensure_windows()

    def ensure_desktop_count(self, count: int, settle_seconds: float) -> None:
        from desktop_manager.win32.desktops import ensure_desktop_count

        ensure_desktop_count(count, settle_seconds)

    def ensure_current_desktop_number(self, desktop_index: int, *, settle_seconds: float) -> bool:
        from desktop_manager.win32.desktops import ensure_current_desktop_number

        return ensure_current_desktop_number(desktop_index, settle_seconds=settle_seconds)

    def rename_desktop(self, desktop_index: int, name: str) -> bool:
        from desktop_manager.win32.desktops import rename_desktop

        return rename_desktop(desktop_index, name)

    def retire_switched_off_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]:
        from desktop_manager.win32.desktops import retire_switched_off_desktops

        return retire_switched_off_desktops(keep_count, retired_names)

    def move_right(self, steps: int) -> None:
        from desktop_manager.win32.desktops import move_right

        move_right(steps)

    def switch_leftmost(self, steps: int) -> None:
        from desktop_manager.win32.desktops import switch_leftmost

        switch_leftmost(steps)

    def close_current_desktop_windows(self, *, preserved_hwnds: set[int]) -> int:
        from desktop_manager.win32.inventory import close_current_desktop_windows

        return close_current_desktop_windows(preserved_hwnds=preserved_hwnds)

    def move_existing_window_to_entry(self, window: LiveWindow, desktop_index: int, entry: LaunchEntry) -> WindowMove:
        from desktop_manager.window_reuse import move_existing_window_to_entry

        return move_existing_window_to_entry(window, desktop_index, entry)

    def launch_entry(
        self, entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None
    ) -> int | None:
        from desktop_manager.launching import launch_entry

        return launch_entry(entry, substitutions, restore_token=restore_token)

    def apply_window_placement(
        self,
        entry: LaunchEntry,
        *,
        hwnd: int | None = None,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        diagnostic_callback: Callable[[str], None] | None = None,
        timeout_seconds: float = 10.0,
    ) -> PlacementOutcome:
        from desktop_manager.win32.entry_windows import apply_window_placement

        return apply_window_placement(
            entry,
            timeout_seconds,
            hwnd=hwnd,
            target_desktop_number=target_desktop_number,
            excluded_hwnds=excluded_hwnds,
            diagnostic_callback=diagnostic_callback,
        )

    def raise_window_to_front(self, hwnd: int) -> None:
        from desktop_manager.win32.geometry import raise_window_to_front

        raise_window_to_front(hwnd)

    def raise_windows_for_processes(self, process_names: set[str]) -> int:
        """Raise every current-desktop window owned by one of the given processes.

        Used as the final action on a desktop so planned windows sit above any
        orphan windows (login prompts, auth popups, auto-started apps) that grabbed
        the foreground mid-restore. Orphans belong to other processes and stay put.
        """
        from desktop_manager.win32.geometry import raise_window_to_front
        from desktop_manager.win32.inventory import collect_windows_matching_process

        raised = 0
        for process_name in process_names:
            for hwnd in collect_windows_matching_process(process_name.casefold(), current_desktop_only=True):
                raise_window_to_front(hwnd)
                raised += 1
        return raised

    def open_missing_path_fallbacks(
        self,
        fallback_state: RestoreFailureFallbacks,
        *,
        logger: RestoreRunLogger | None,
    ) -> list[str]:
        from desktop_manager.restore_fallbacks import open_missing_path_fallbacks

        return open_missing_path_fallbacks(fallback_state, logger=logger)

    def apply_second_pass_placements(
        self,
        plan: WorkspacePlan,
        *,
        logger: RestoreRunLogger | None,
        entries_by_desktop: dict[int, list[LaunchEntry]],
    ) -> list[str]:
        from desktop_manager.window_reuse import apply_second_pass_placements

        return apply_second_pass_placements(plan, logger=logger, entries_by_desktop=entries_by_desktop)
