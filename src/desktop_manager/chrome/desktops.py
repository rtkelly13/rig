"""The virtual desktops a platform's Chrome windows live on.

Windows numbers its virtual desktops, and the run loops bring a window on
another desktop over while they work on it, then send it back. macOS Spaces are
not addressable, so its desktops report none and the loops skip that handling.
Each driver carries the `Desktops` of its platform.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


class Desktops(Protocol):
    def current(self) -> int | None:
        """The desktop the user is on, or None when desktops are not addressable."""
        ...

    def of_window(self, hwnd: int) -> int | None: ...

    def go_to(self, number: int) -> None: ...

    def move_window(self, hwnd: int, number: int) -> None: ...

    def window_exists(self, hwnd: int) -> bool: ...


@dataclass(frozen=True, slots=True)
class NoDesktops:
    """Desktops that cannot be addressed: macOS Spaces."""

    def current(self) -> int | None:
        return None

    def of_window(self, hwnd: int) -> int | None:
        del hwnd
        return None

    def go_to(self, number: int) -> None:
        del number

    def move_window(self, hwnd: int, number: int) -> None:
        del hwnd, number

    def window_exists(self, hwnd: int) -> bool:
        del hwnd
        return False


@dataclass(frozen=True, slots=True)
class WindowsDesktops:
    """Windows' numbered virtual desktops; the win32 modules load on first use."""

    def current(self) -> int | None:
        from desktop_manager.win32.desktops import get_current_desktop_number

        return get_current_desktop_number()

    def of_window(self, hwnd: int) -> int | None:
        from desktop_manager.win32.desktops import get_window_desktop_number

        return get_window_desktop_number(hwnd)

    def go_to(self, number: int) -> None:
        from desktop_manager.win32.desktops import go_to_desktop_number

        go_to_desktop_number(number)

    def move_window(self, hwnd: int, number: int) -> None:
        from desktop_manager.win32.desktops import move_window_to_desktop_number

        move_window_to_desktop_number(hwnd, number)

    def window_exists(self, hwnd: int) -> bool:
        import win32gui

        return bool(win32gui.IsWindow(hwnd))
