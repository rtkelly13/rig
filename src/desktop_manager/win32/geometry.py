from __future__ import annotations

from collections.abc import Iterable

from desktop_manager import win32
from desktop_manager.display_guard import ShowState
from desktop_manager.models import DisplayArea, Rect
from desktop_manager.win32 import dlls
from desktop_manager.win32.errors import WIN32_ERRORS
from desktop_manager.win32.monitor_info import get_monitor_info


def _rect_from_edges(edges: Iterable[int]) -> Rect:
    """A ``Rect`` from Win32's ``(left, top, right, bottom)`` edge sequence."""
    left, top, right, bottom = (int(edge) for edge in edges)
    return Rect(left=left, top=top, width=right - left, height=bottom - top)


def window_bounds(hwnd: int) -> Rect | None:
    win32.ensure_windows()
    import win32gui

    try:
        raw_rect = win32gui.GetWindowRect(hwnd)
    except WIN32_ERRORS:
        return None
    return _rect_from_edges(raw_rect)


def window_title(hwnd: int) -> str | None:
    win32.ensure_windows()
    import win32gui

    try:
        return str(win32gui.GetWindowText(hwnd)).strip()
    except WIN32_ERRORS:
        return None


def extended_frame_bounds(hwnd: int) -> Rect | None:
    win32.ensure_windows()
    try:
        import ctypes.wintypes

        dwmwa_extended_frame_bounds = 9
        rect = ctypes.wintypes.RECT()
        result = dlls.call_int(
            dlls.system_dll("dwmapi").DwmGetWindowAttribute,
            hwnd,
            dwmwa_extended_frame_bounds,
            ctypes.byref(rect),
            ctypes.sizeof(rect),
        )
        if result != 0:
            return None
        return Rect(left=rect.left, top=rect.top, width=rect.right - rect.left, height=rect.bottom - rect.top)
    except WIN32_ERRORS:
        return None


def adjust_rect_for_window_frame(hwnd: int, target_rect: Rect) -> Rect:
    frame_rect = extended_frame_bounds(hwnd)
    window_rect = window_bounds(hwnd)
    if frame_rect is None or window_rect is None:
        return target_rect

    left_adjust = frame_rect.left - window_rect.left
    top_adjust = frame_rect.top - window_rect.top
    right_adjust = window_rect.right - frame_rect.right
    bottom_adjust = window_rect.bottom - frame_rect.bottom
    return Rect(
        left=target_rect.left - left_adjust,
        top=target_rect.top - top_adjust,
        width=target_rect.width + left_adjust + right_adjust,
        height=target_rect.height + top_adjust + bottom_adjust,
    )


def move_window(hwnd: int, rect: Rect) -> None:
    win32.ensure_windows()
    import win32con
    import win32gui

    adjusted_rect = adjust_rect_for_window_frame(hwnd, rect)
    win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
    win32gui.MoveWindow(hwnd, adjusted_rect.left, adjusted_rect.top, adjusted_rect.width, adjusted_rect.height, True)
    win32gui.SetWindowPos(
        hwnd,
        win32con.HWND_TOPMOST,
        0,
        0,
        0,
        0,
        win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE,
    )
    win32gui.SetWindowPos(
        hwnd,
        win32con.HWND_NOTOPMOST,
        0,
        0,
        0,
        0,
        win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE,
    )


def raise_window_to_front(hwnd: int) -> None:
    """Bring a window to the top of the z-order without stealing keyboard focus.

    Placement happens early in a long restore; orphan windows (login prompts, auth
    popups) can pop up afterwards and cover the tiled layout. Re-raising placed
    windows as the final action on a desktop keeps them visible above those orphans.
    """
    win32.ensure_windows()
    import win32con
    import win32gui

    try:
        win32gui.ShowWindow(hwnd, win32con.SW_SHOWNA)
        win32gui.SetWindowPos(
            hwnd,
            win32con.HWND_TOP,
            0,
            0,
            0,
            0,
            win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE,
        )
    except WIN32_ERRORS:
        return


def current_window_handles_to_preserve() -> set[int]:
    win32.ensure_windows()
    import win32gui

    handles: set[int] = set()
    foreground = _top_level_window_handle(int(win32gui.GetForegroundWindow()))
    if foreground:
        handles.add(foreground)

    console = _top_level_window_handle(dlls.call_int(dlls.system_dll("kernel32").GetConsoleWindow))
    if console:
        handles.add(console)
    return handles


def _top_level_window_handle(hwnd: int) -> int:
    if not hwnd:
        return 0
    win32.ensure_windows()
    import win32gui

    root_owner = int(win32gui.GetAncestor(hwnd, 3))  # GA_ROOTOWNER
    if root_owner:
        return root_owner
    root = int(win32gui.GetAncestor(hwnd, 2))  # GA_ROOT
    if root:
        return root
    return hwnd


def request_close_window(hwnd: int) -> None:
    win32.ensure_windows()
    import win32con
    import win32gui

    win32gui.PostMessage(hwnd, win32con.WM_CLOSE, 0, 0)


def monitor_display_areas() -> list[DisplayArea]:
    win32.ensure_windows()
    import win32api

    monitors = win32api.EnumDisplayMonitors()
    areas: list[DisplayArea] = []
    for monitor in monitors:
        info = get_monitor_info(int(monitor[0]))
        areas.append(DisplayArea(screen_number=0, bounds=info.work.as_rect()))
    areas.sort(key=lambda area: (area.bounds.left, area.bounds.top))
    for index, area in enumerate(areas, start=1):
        area.screen_number = index
    return areas


def monitor_work_areas() -> list[Rect]:
    return [area.bounds for area in monitor_display_areas()]


def monitor_work_area_by_slot(slot: str) -> Rect:
    areas = monitor_work_areas()
    if not areas:
        raise RuntimeError("No monitor work areas detected")

    if slot == "left":
        return areas[0]
    if slot == "right":
        return areas[-1]
    if slot == "center":
        return areas[len(areas) // 2]
    raise ValueError(f"Unknown monitor slot: {slot}")


def monitor_work_area_by_number(screen_number: int) -> Rect:
    areas = monitor_work_areas()
    if screen_number < 1 or screen_number > len(areas):
        raise ValueError(f"Screen number out of range: {screen_number}")
    return areas[screen_number - 1]


def primary_monitor_work_area() -> Rect:
    win32.ensure_windows()
    import win32api
    import win32con

    monitor = win32api.MonitorFromPoint((0, 0), win32con.MONITOR_DEFAULTTOPRIMARY)
    return get_monitor_info(monitor).work.as_rect()


def screen_number_for_window(hwnd: int) -> int | None:
    win32.ensure_windows()
    bounds = window_bounds(hwnd)
    if bounds is None:
        return None

    for screen in monitor_display_areas():
        if (
            screen.bounds.left <= bounds.center_x < screen.bounds.right
            and screen.bounds.top <= bounds.center_y < screen.bounds.bottom
        ):
            return screen.screen_number
    return None


def foreground_window_handle() -> int | None:
    """The top-level window the user is working in, or ``None`` when nothing has focus."""
    win32.ensure_windows()
    import win32gui

    return _top_level_window_handle(int(win32gui.GetForegroundWindow())) or None


def console_window_handle() -> int | None:
    """The top-level console window of this process, or ``None`` when it has none (e.g. pythonw)."""
    win32.ensure_windows()
    return _top_level_window_handle(dlls.call_int(dlls.system_dll("kernel32").GetConsoleWindow)) or None


def window_show_state(hwnd: int) -> ShowState:
    win32.ensure_windows()
    import win32con
    import win32gui

    if win32gui.IsIconic(hwnd):
        return ShowState.MINIMIZED
    _flags, show_cmd, _min_pos, _max_pos, _normal = win32gui.GetWindowPlacement(hwnd)
    if show_cmd == win32con.SW_SHOWMAXIMIZED:
        return ShowState.MAXIMIZED
    return ShowState.NORMAL


def is_window(hwnd: int) -> bool:
    win32.ensure_windows()
    import win32gui

    return bool(win32gui.IsWindow(hwnd))


def place_window_without_activating(hwnd: int, rect: Rect) -> None:
    """Move and size a normal (not minimized or maximized) window without touching focus or z-order.

    Unlike :func:`move_window` this never calls ``ShowWindow(SW_RESTORE)``, which
    activates the window, and never re-stacks it, so the user's foreground window
    and stacking order are left exactly as they were.
    """
    win32.ensure_windows()
    import win32con
    import win32gui

    adjusted_rect = adjust_rect_for_window_frame(hwnd, rect)
    win32gui.SetWindowPos(
        hwnd,
        0,
        adjusted_rect.left,
        adjusted_rect.top,
        adjusted_rect.width,
        adjusted_rect.height,
        win32con.SWP_NOACTIVATE | win32con.SWP_NOZORDER | win32con.SWP_NOOWNERZORDER,
    )
