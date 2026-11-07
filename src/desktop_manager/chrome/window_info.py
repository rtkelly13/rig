"""Chrome's top-level windows as Win32 sees them: which they are, where, and whose profile.

Imports Windows modules lazily, so the pure helpers work anywhere.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from desktop_manager.chrome.tabs import ScreenPoint, WindowRect
from desktop_manager.win32.errors import WIN32_ERRORS
from desktop_manager.win32.monitor_info import get_monitor_info

CHROME_WINDOW_CLASS = "Chrome_WidgetWin_1"
CHROME_PROCESS_NAME = "chrome.exe"

_PROFILE_DIR_PATTERN = re.compile(r'--profile-directory=(?:"([^"]+)"|(\S+))')


@dataclass(slots=True, frozen=True)
class WindowProfile:
    """The Chrome profile a window belongs to, as its shell relaunch properties name it."""

    directory: str | None
    name: str | None


def profile_dir_from_relaunch_command(command: str | None) -> str | None:
    """Pull `--profile-directory` out of a window's relaunch command.

    Chrome only stamps the flag on a window when more than one profile exists;
    a single-profile install relaunches the default profile.
    """
    if not command:
        return None
    match = _PROFILE_DIR_PATTERN.search(command)
    if match is None:
        return "Default"
    return match.group(1) or match.group(2)


def profile_name_from_relaunch_display_name(display_name: str | None) -> str | None:
    """ "Ryan (work) - Chrome" -> "Ryan (work)"."""
    if not display_name:
        return None
    return display_name.removesuffix(" - Chrome").removesuffix(" - Google Chrome").strip() or None


def window_rect(hwnd: int) -> WindowRect | None:
    """The window's rect, or its restored rect while minimised (a minimised window sits at -32000)."""
    import win32gui

    try:
        left, top, right, bottom = (
            win32gui.GetWindowPlacement(hwnd)[4] if win32gui.IsIconic(hwnd) else win32gui.GetWindowRect(hwnd)
        )
        return WindowRect(left, top, right, bottom)
    except WIN32_ERRORS:
        return None


def monitor_origin(hwnd: int) -> ScreenPoint | None:
    import win32api
    import win32con

    try:
        info = get_monitor_info(win32api.MonitorFromWindow(hwnd, win32con.MONITOR_DEFAULTTONEAREST))
        return ScreenPoint(info.monitor.left, info.monitor.top)
    except (*WIN32_ERRORS, ValueError):
        return None


def chrome_window_handles() -> list[int]:
    """Top-level Chrome browser-process windows with a title, on every virtual desktop."""
    import win32gui
    import win32process

    from desktop_manager.win32.inventory import get_process_details

    handles: list[int] = []
    pid_names: dict[int, str | None] = {}

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        if win32gui.GetClassName(hwnd) != CHROME_WINDOW_CLASS or not win32gui.GetWindowText(hwnd).strip():
            return True
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        if pid not in pid_names:
            pid_names[pid] = get_process_details(pid).name
        if (pid_names[pid] or "").lower() == CHROME_PROCESS_NAME:
            handles.append(hwnd)
        return True

    win32gui.EnumWindows(callback, 0)
    return handles


def window_profile(hwnd: int) -> WindowProfile:
    """The window's profile directory and display name, from its shell relaunch properties."""
    try:
        from win32com.propsys import propsys, pscon

        store = propsys.SHGetPropertyStoreForWindow(hwnd, propsys.IID_IPropertyStore)
        command = store.GetValue(pscon.PKEY_AppUserModel_RelaunchCommand).GetValue()
        display = store.GetValue(pscon.PKEY_AppUserModel_RelaunchDisplayNameResource).GetValue()
    except WIN32_ERRORS:
        return WindowProfile(None, None)
    return WindowProfile(
        profile_dir_from_relaunch_command(command if isinstance(command, str) else None),
        profile_name_from_relaunch_display_name(display if isinstance(display, str) else None),
    )
