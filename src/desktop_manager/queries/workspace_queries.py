from __future__ import annotations

from collections.abc import Callable

from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow, WindowMatch, WorkspacePlan


class WorkspaceQueries:
    """Read-only adapter for restore planning and verification.

    Some query methods rely on OS navigation to inspect state across desktops,
    but this module remains the single place where restore code reads system
    state rather than performing restorative side effects.
    """

    def current_workspace_status(self, desktop_count: int | None = None) -> WorkspacePlan:
        from desktop_manager.restore import current_workspace_status

        return current_workspace_status(desktop_count=desktop_count)

    def current_workspace_status_for_desktop_indexes(self, desktop_indexes: list[int]) -> WorkspacePlan:
        from desktop_manager.restore import current_workspace_status_for_desktop_indexes

        return current_workspace_status_for_desktop_indexes(desktop_indexes)

    def current_desktop_status(self, *, desktop_name: str | None = None) -> DesktopPlan:
        from desktop_manager.restore import current_desktop_status

        return current_desktop_status(desktop_name=desktop_name)

    def current_window_handles_to_preserve(self) -> set[int]:
        from desktop_manager.win32.geometry import current_window_handles_to_preserve

        return current_window_handles_to_preserve()

    def get_current_desktop_number(self) -> int | None:
        from desktop_manager.win32.desktops import get_current_desktop_number

        return get_current_desktop_number()

    def inventory_reusable_windows(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        from desktop_manager.win32.inventory import inventory_reusable_windows

        return inventory_reusable_windows(excluded_hwnds=excluded_hwnds)

    def collect_windows_matching_process(self, process_name: str) -> set[int]:
        from desktop_manager.win32.inventory import collect_windows_matching_process

        return set(collect_windows_matching_process(process_name.casefold(), current_desktop_only=False))

    def wait_for_window_for_entry(
        self,
        entry: LaunchEntry,
        *,
        timeout_seconds: float,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        allow_process_fallback_for_terminals: bool = False,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> WindowMatch:
        from desktop_manager.win32.entry_windows import wait_for_window_for_entry

        return wait_for_window_for_entry(
            entry,
            timeout_seconds=timeout_seconds,
            current_desktop_only=False,
            target_desktop_number=target_desktop_number,
            move_to_target_desktop=False,
            excluded_hwnds=excluded_hwnds,
            allow_process_fallback_for_terminals=allow_process_fallback_for_terminals,
            diagnostic_callback=diagnostic_callback,
        )

    def wait_for_window_title_change(
        self,
        hwnd: int,
        title: str,
        *,
        timeout_seconds: float,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> str | None:
        from desktop_manager.win32.entry_windows import wait_for_window_title_change

        return wait_for_window_title_change(
            hwnd,
            title,
            timeout_seconds=timeout_seconds,
            diagnostic_callback=diagnostic_callback,
        )
