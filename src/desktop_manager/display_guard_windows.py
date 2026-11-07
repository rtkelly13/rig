"""Win32 ports for the display guard: monitors, display events, and window placements."""

from __future__ import annotations

import contextlib
import ctypes
import os
import threading
from collections.abc import Callable, Sequence
from pathlib import Path

from desktop_manager.display_guard import (
    MonitorBounds,
    MonitorInfo,
    MonitorSignature,
    ShowState,
    WindowPlacementRecord,
    is_lock_held,
)
from desktop_manager.models import IGNORED_PROCESS_NAMES, IGNORED_WINDOW_TITLES, Rect
from desktop_manager.win32 import dlls, ensure_windows
from desktop_manager.win32.desktops import ensure_current_desktop_number, get_current_desktop_number
from desktop_manager.win32.errors import WIN32_ERRORS
from desktop_manager.win32.inventory import get_process_details
from desktop_manager.win32.monitor_info import get_monitor_info

_WM_DISPLAYCHANGE = 0x007E
_WM_POWERBROADCAST = 0x0218
_WM_WTSSESSION_CHANGE = 0x02B1
_PBT_APMRESUMESUSPEND = 0x0007
_PBT_APMRESUMEAUTOMATIC = 0x0012
_WTS_SESSION_UNLOCK = 0x8
_MONITORINFOF_PRIMARY = 0x1
_WPF_RESTORETOMAXIMIZED = 0x2
_SW_SHOWMINIMIZED = 2
_SW_SHOWMAXIMIZED = 3
_SW_SHOWNOACTIVATE = 4
_SW_SHOWMINNOACTIVE = 7
_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
_LISTENER_CLASS_NAME = "DesktopManagerDisplayGuard"


def make_process_per_monitor_dpi_aware() -> None:
    """Read and write real pixel coordinates on every monitor.

    The guard only round-trips placements it read itself, but a DPI-unaware process
    gets virtualised coordinates that differ by monitor. Asking for physical
    coordinates keeps snapshots and re-applies in the same space.
    """
    with contextlib.suppress(AttributeError, OSError):
        dlls.system_dll("user32").SetProcessDpiAwarenessContext(
            ctypes.c_void_p(_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
        )


def monitor_signature() -> MonitorSignature:
    ensure_windows()
    import win32api

    monitors: list[MonitorInfo] = []
    for monitor in win32api.EnumDisplayMonitors():
        info = get_monitor_info(int(monitor[0]))
        edges = info.monitor
        monitors.append(
            MonitorInfo(
                device=info.device.removeprefix("\\\\.\\"),
                bounds=MonitorBounds(edges.left, edges.top, edges.right, edges.bottom),
                primary=bool(info.flags & _MONITORINFOF_PRIMARY),
            )
        )
    monitors.sort(key=lambda monitor: (monitor.bounds.left, monitor.bounds.top))
    return tuple(monitors)


class DisplayEventListener:
    """Count display-change, resume, and unlock broadcasts on a hidden window.

    Polling the monitor set alone can miss a drop-and-return that completes between
    two polls. Every such flap still broadcasts WM_DISPLAYCHANGE, so the count shows
    it. Broadcasts only reach top-level windows, not message-only ones. The window
    is never shown, and it belongs to this process, so the guard skips it when it
    captures placements.
    """

    def __init__(self, log: Callable[[str], None]) -> None:
        self._count: int = 0
        self._lock: threading.Lock = threading.Lock()
        self._log: Callable[[str], None] = log
        self._thread: threading.Thread = threading.Thread(target=self._run, name="display-guard-events", daemon=True)

    @property
    def count(self) -> int:
        with self._lock:
            return self._count

    def start(self) -> None:
        self._thread.start()

    def _bump(self, reason: str) -> None:
        with self._lock:
            self._count += 1
        self._log(f"Event: {reason}")

    def _run(self) -> None:
        import win32api
        import win32gui

        def wndproc(hwnd: int, msg: int, wparam: int, lparam: int) -> int:
            if msg == _WM_DISPLAYCHANGE:
                self._bump(f"WM_DISPLAYCHANGE {lparam & 0xFFFF}x{(lparam >> 16) & 0xFFFF}")
            elif msg == _WM_POWERBROADCAST and wparam in {_PBT_APMRESUMESUSPEND, _PBT_APMRESUMEAUTOMATIC}:
                self._bump("power resume")
            elif msg == _WM_WTSSESSION_CHANGE and wparam == _WTS_SESSION_UNLOCK:
                self._bump("session unlock")
            return win32gui.DefWindowProc(hwnd, msg, wparam, lparam)

        window_class = win32gui.WNDCLASS()
        window_class.lpfnWndProc = wndproc
        window_class.lpszClassName = _LISTENER_CLASS_NAME
        window_class.hInstance = win32api.GetModuleHandle(None)
        try:
            win32gui.RegisterClass(window_class)
            hwnd = win32gui.CreateWindowEx(
                0,
                _LISTENER_CLASS_NAME,
                "Desktop Manager Display Guard",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                window_class.hInstance,
                None,
            )
        except WIN32_ERRORS as err:  # pragma: no cover - the poll still catches slower changes
            self._log(f"Display event listener unavailable ({err}); relying on polling only.")
            return
        try:
            import win32ts

            win32ts.WTSRegisterSessionNotification(hwnd, win32ts.NOTIFY_FOR_THIS_SESSION)
        except (ImportError, *WIN32_ERRORS) as err:  # pragma: no cover - optional; unlocks also show as display events
            self._log(f"Session unlock notifications unavailable ({err}).")
        win32gui.PumpMessages()


def _show_state(show_cmd: int) -> ShowState:
    if show_cmd == _SW_SHOWMINIMIZED:
        return ShowState.MINIMIZED
    if show_cmd == _SW_SHOWMAXIMIZED:
        return ShowState.MAXIMIZED
    return ShowState.NORMAL


def _rect_from_ltrb(ltrb: Sequence[int]) -> Rect:
    left, top, right, bottom = ltrb
    return Rect(left=left, top=top, width=right - left, height=bottom - top)


def capture_placements() -> list[WindowPlacementRecord]:
    """Placements of the windows a restore would manage, across every virtual desktop.

    Uses the same filter as ``inventory_reusable_windows``, but skips the placement
    inference and virtual-desktop lookups, which are too slow to run every snapshot.
    """
    ensure_windows()
    import win32con
    import win32gui
    import win32process

    own_pid = os.getpid()
    process_names: dict[int, str | None] = {}
    records: list[WindowPlacementRecord] = []

    def callback(hwnd: int, _lparam: int) -> bool:
        try:
            if not win32gui.IsWindowVisible(hwnd):
                return True
            title = win32gui.GetWindowText(hwnd).strip()
            if not title or title in IGNORED_WINDOW_TITLES:
                return True
            if win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) & win32con.WS_EX_TOOLWINDOW:
                return True
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid in {0, own_pid}:
                return True
            if pid not in process_names:
                process_names[pid] = get_process_details(pid).name
            process_name = process_names[pid]
            if process_name and process_name.lower() in IGNORED_PROCESS_NAMES:
                return True
            flags, show_cmd, _min_pos, _max_pos, normal = win32gui.GetWindowPlacement(hwnd)
            records.append(
                WindowPlacementRecord(
                    hwnd=hwnd,
                    pid=pid,
                    title=title,
                    process_name=process_name,
                    show_state=_show_state(show_cmd),
                    normal_rect=_rect_from_ltrb(normal),
                    window_rect=_rect_from_ltrb(win32gui.GetWindowRect(hwnd)),
                    restore_to_maximized=bool(flags & _WPF_RESTORETOMAXIMIZED),
                )
            )
        except WIN32_ERRORS:  # a window can close mid-enumeration; skip it
            return True
        return True

    win32gui.EnumWindows(callback, 0)
    return records


def _set_placement(hwnd: int, record: WindowPlacementRecord, show_cmd: int) -> None:
    import win32gui

    _flags, _show_cmd, min_pos, max_pos, _normal = win32gui.GetWindowPlacement(hwnd)
    flags = _WPF_RESTORETOMAXIMIZED if record.restore_to_maximized else 0
    win32gui.SetWindowPlacement(
        hwnd,
        (
            flags,
            show_cmd,
            min_pos,
            max_pos,
            (record.normal_rect.left, record.normal_rect.top, record.normal_rect.right, record.normal_rect.bottom),
        ),
    )


def apply_placements(records: Sequence[WindowPlacementRecord], *, log: Callable[[str], None]) -> list[int]:
    """Put each window back where its record says, without stealing focus or desktops.

    A maximized window is first restored onto the snapshot rect and then maximized.
    That makes it maximize on the monitor that rect is on, not on the monitor
    Windows parked it on. Maximizing activates the window, which can pull the user
    to another virtual desktop, so the foreground window and the current desktop
    are put back afterwards.
    """
    ensure_windows()
    import win32gui

    foreground = win32gui.GetForegroundWindow()
    starting_desktop = get_current_desktop_number()
    placed: list[int] = []
    for record in records:
        try:
            if not win32gui.IsWindow(record.hwnd):
                continue
            if record.show_state is ShowState.MAXIMIZED:
                _set_placement(record.hwnd, record, _SW_SHOWNOACTIVATE)
                _set_placement(record.hwnd, record, _SW_SHOWMAXIMIZED)
            elif record.show_state is ShowState.MINIMIZED:
                _set_placement(record.hwnd, record, _SW_SHOWMINNOACTIVE)
            else:
                _set_placement(record.hwnd, record, _SW_SHOWNOACTIVATE)
            placed.append(record.hwnd)
        except WIN32_ERRORS as err:  # one unmovable window must not stop the rest
            # Typically an elevated window, which a non-elevated process cannot move.
            log(f"  Could not place [{record.hwnd}] '{record.title}': {err}")

    if starting_desktop is not None and get_current_desktop_number() != starting_desktop:
        ensure_current_desktop_number(starting_desktop, settle_seconds=0.2)
    if foreground and win32gui.IsWindow(foreground):
        with contextlib.suppress(*WIN32_ERRORS):
            win32gui.SetForegroundWindow(foreground)
    return placed


class Win32DisplayGuardPorts:
    """The real machine, as the :class:`~desktop_manager.display_guard.DisplayGuard` sees it."""

    def __init__(
        self,
        *,
        restore_lock_path: Path,
        log: Callable[[str], None],
        clock: Callable[[], float],
        events: DisplayEventListener | None,
    ) -> None:
        self._restore_lock_path: Path = restore_lock_path
        self._log: Callable[[str], None] = log
        self._clock: Callable[[], float] = clock
        self._events: DisplayEventListener | None = events

    def now(self) -> float:
        return self._clock()

    def monitor_signature(self) -> MonitorSignature:
        return monitor_signature()

    def display_event_count(self) -> int:
        return 0 if self._events is None else self._events.count

    def restore_in_progress(self) -> bool:
        return is_lock_held(self._restore_lock_path)

    def capture_placements(self) -> list[WindowPlacementRecord]:
        return capture_placements()

    def apply_placements(self, records: Sequence[WindowPlacementRecord]) -> list[int]:
        return apply_placements(records, log=self._log)

    def log(self, message: str) -> None:
        self._log(message)
