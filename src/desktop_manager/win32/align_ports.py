"""The live machine behind ``workspace align``: reads windows and moves them without taking focus.

Satisfies :class:`desktop_manager.align.AlignPorts`. Moves never switch the current
virtual desktop (pyvda moves the view directly) and never activate a window, so a
run in the background does not disturb whatever the user is doing.
"""

from __future__ import annotations

from collections.abc import Sequence

from desktop_manager import win32
from desktop_manager.display_guard import ShowState
from desktop_manager.models import LiveWindow, PlacementSpec, Rect
from desktop_manager.placement import placement_rect_for
from desktop_manager.win32 import desktops, geometry, inventory
from desktop_manager.win32.errors import PYVDA_ERRORS, WIN32_ERRORS


class Win32AlignPorts:
    def desktop_names(self) -> list[str]:
        return desktops.virtual_desktop_names()

    def desktop_count(self) -> int:
        return desktops.get_virtual_desktop_count()

    def ensure_desktop_count(self, count: int, settle_seconds: float = 0.65) -> None:
        desktops.ensure_desktop_count(count, settle_seconds)

    def rename_desktop(self, desktop_index: int, name: str) -> bool:
        return desktops.rename_desktop(desktop_index, name)

    def retire_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]:
        return desktops.retire_switched_off_desktops(keep_count, retired_names)

    def current_desktop(self) -> int | None:
        return desktops.get_current_desktop_number()

    def foreground_hwnd(self) -> int | None:
        return geometry.foreground_window_handle()

    def console_hwnd(self) -> int | None:
        return geometry.console_window_handle()

    def inventory(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        return inventory.inventory_reusable_windows(excluded_hwnds=excluded_hwnds)

    def show_state(self, hwnd: int) -> ShowState:
        try:
            return geometry.window_show_state(hwnd)
        except WIN32_ERRORS:
            return ShowState.NORMAL

    def target_rect(self, placement: PlacementSpec) -> Rect:
        return placement_rect_for(placement)

    def is_window(self, hwnd: int) -> bool:
        return geometry.is_window(hwnd)

    def move_to_desktop(self, hwnd: int, desktop_index: int) -> str | None:
        try:
            desktops.move_window_to_desktop_number(hwnd, desktop_index)
        except PYVDA_ERRORS as err:
            return f"{type(err).__name__}: {err}"
        return None

    def place(self, hwnd: int, rect: Rect) -> str | None:
        try:
            geometry.place_window_without_activating(hwnd, rect)
        except WIN32_ERRORS as err:  # typically an elevated window a normal process cannot move
            return f"{type(err).__name__}: {err}"
        return None

    def restore_view(self, *, desktop: int | None, foreground: int | None, moved_hwnds: set[int]) -> bool:
        """Undo a desktop switch or focus change that a moved window caused.

        Only acts when a moved window has ended up in the foreground. Any other change
        is the user's own doing (clicking another window, switching desktop) and stays.
        """
        win32.ensure_windows()
        import win32gui

        if geometry.foreground_window_handle() not in moved_hwnds:
            return False
        if desktop is not None and desktops.get_current_desktop_number() not in {None, desktop}:
            desktops.go_to_desktop_number(desktop)
        if foreground is not None and geometry.is_window(foreground):
            try:
                win32gui.SetForegroundWindow(foreground)
            except WIN32_ERRORS:
                return True
        return True
