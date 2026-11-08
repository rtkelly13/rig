from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from desktop_manager import win32
from desktop_manager.models import (
    IGNORED_PROCESS_NAMES,
    IGNORED_WINDOW_TITLES,
    LiveWindow,
    ProcessDetails,
    WindowHandleCollector,
    WindowSnapshot,
)
from desktop_manager.win32 import desktops, geometry
from desktop_manager.win32.errors import WIN32_ERRORS
from desktop_manager.win32.geometry import infer_placement_for_window


@dataclass(frozen=True, slots=True)
class _ListedWindow:
    """A visible, titled, non-tool window owned by some other user process."""

    title: str
    pid: int
    process: ProcessDetails


def _listed_window(hwnd: int, *, current_pid: int, current_desktop_only: bool) -> _ListedWindow | None:
    import win32con
    import win32gui

    if current_desktop_only and not desktops.is_window_on_current_desktop(hwnd):
        return None
    if not win32gui.IsWindowVisible(hwnd):
        return None

    title = str(win32gui.GetWindowText(hwnd)).strip()
    if not title or title in IGNORED_WINDOW_TITLES:
        return None

    ex_style = int(win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE))
    if ex_style & win32con.WS_EX_TOOLWINDOW:
        return None

    pid = _window_process_id(hwnd)
    if pid in {0, current_pid}:
        return None

    process = get_process_details(pid)
    if process.name and process.name.lower() in IGNORED_PROCESS_NAMES:
        return None
    return _ListedWindow(title=title, pid=pid, process=process)


def _window_process_id(hwnd: int) -> int:
    import win32process

    _, pid = win32process.GetWindowThreadProcessId(hwnd)
    return int(pid)


def list_current_desktop_windows() -> list[WindowSnapshot]:
    win32.ensure_windows()
    import win32gui

    current_pid = os.getpid()
    snapshots: list[WindowSnapshot] = []

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        listed = _listed_window(hwnd, current_pid=current_pid, current_desktop_only=True)
        if listed is None:
            return True

        inferred_placement = infer_placement_for_window(hwnd)
        bounds = geometry.window_bounds(hwnd)
        snapshots.append(
            WindowSnapshot(
                title=listed.title,
                pid=listed.pid,
                process_name=listed.process.name,
                exe_path=listed.process.exe_path,
                class_name=win32gui.GetClassName(hwnd),
                screen_number=inferred_placement.screen
                if inferred_placement is not None
                else geometry.screen_number_for_window(hwnd),
                inferred_placement=inferred_placement,
                bounds=bounds,
            )
        )
        return True

    win32gui.EnumWindows(callback, 0)
    snapshots.sort(key=lambda snapshot: (snapshot.process_name or "", snapshot.title.lower()))
    return snapshots


def list_current_desktop_window_handles(*, preserved_hwnds: set[int] | None = None) -> list[int]:
    win32.ensure_windows()
    import win32gui

    current_pid = os.getpid()
    handles: list[int] = []
    preserved = preserved_hwnds or set()

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        if hwnd in preserved:
            return True
        if _listed_window(hwnd, current_pid=current_pid, current_desktop_only=True) is not None:
            handles.append(hwnd)
        return True

    win32gui.EnumWindows(callback, 0)
    return handles


def close_current_desktop_windows(*, preserved_hwnds: set[int] | None = None) -> int:
    handles = list_current_desktop_window_handles(preserved_hwnds=preserved_hwnds)
    for hwnd in handles:
        geometry.request_close_window(hwnd)
    return len(handles)


def inventory_reusable_windows(*, excluded_hwnds: set[int] | None = None) -> list[LiveWindow]:
    win32.ensure_windows()
    import win32gui

    current_pid = os.getpid()
    excluded = excluded_hwnds or set()
    windows: list[LiveWindow] = []

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        if hwnd in excluded:
            return True
        listed = _listed_window(hwnd, current_pid=current_pid, current_desktop_only=False)
        if listed is None:
            return True

        inferred_placement = infer_placement_for_window(hwnd)
        bounds = geometry.window_bounds(hwnd)

        windows.append(
            LiveWindow(
                hwnd=hwnd,
                title=listed.title,
                pid=listed.pid,
                process_name=listed.process.name,
                exe_path=listed.process.exe_path,
                class_name=win32gui.GetClassName(hwnd),
                desktop_number=desktops.get_window_desktop_number(hwnd),
                screen_number=inferred_placement.screen
                if inferred_placement is not None
                else geometry.screen_number_for_window(hwnd),
                inferred_placement=inferred_placement,
                bounds=bounds,
            )
        )
        return True

    win32gui.EnumWindows(callback, 0)
    windows.sort(key=lambda window: ((window.process_name or "").lower(), window.title.lower(), window.hwnd))
    return windows


def get_process_details(pid: int) -> ProcessDetails:
    win32.ensure_windows()
    import win32api
    import win32con
    import win32process

    access = win32con.PROCESS_QUERY_INFORMATION | win32con.PROCESS_VM_READ
    try:
        handle = win32api.OpenProcess(access, False, pid)
    except WIN32_ERRORS:
        try:
            handle = win32api.OpenProcess(win32con.PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        except WIN32_ERRORS:
            return ProcessDetails()

    try:
        exe_path = str(win32process.GetModuleFileNameEx(handle, 0))
        return ProcessDetails(name=Path(exe_path).name if exe_path else None, exe_path=exe_path or None)
    except WIN32_ERRORS:
        return ProcessDetails()
    finally:
        win32api.CloseHandle(handle)


def collect_matching_windows(target: str, *, exact: bool = False, current_desktop_only: bool = True) -> list[int]:
    win32.ensure_windows()
    import win32gui

    collector = WindowHandleCollector()

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        if current_desktop_only and not desktops.is_window_on_current_desktop(hwnd):
            return True
        if not win32gui.IsWindowVisible(hwnd):
            return True
        window_title = str(win32gui.GetWindowText(hwnd)).strip()
        normalized = window_title.casefold()
        if window_title and ((exact and normalized == target) or (not exact and target in normalized)):
            collector.add(hwnd)
        return True

    win32gui.EnumWindows(callback, 0)
    return collector.handles


@dataclass(frozen=True, slots=True)
class _ProcessWindow:
    hwnd: int
    pid: int


def _process_windows(target_process_name: str, *, current_desktop_only: bool) -> list[_ProcessWindow]:
    """Every visible window whose owning process has this casefolded image name."""
    import win32gui

    matches: list[_ProcessWindow] = []

    def callback(hwnd: int, lparam: int) -> bool:
        del lparam
        if current_desktop_only and not desktops.is_window_on_current_desktop(hwnd):
            return True
        if not win32gui.IsWindowVisible(hwnd):
            return True
        pid = _window_process_id(hwnd)
        if pid == 0:
            return True
        process_name = get_process_details(pid).name
        if process_name and process_name.casefold() == target_process_name:
            matches.append(_ProcessWindow(hwnd=hwnd, pid=pid))
        return True

    win32gui.EnumWindows(callback, 0)
    return matches


def process_ids_for_process_name(target_process_name: str, *, current_desktop_only: bool = False) -> set[int]:
    """Return the PIDs of every visible window whose owning process matches the name."""
    win32.ensure_windows()
    matches = _process_windows(target_process_name, current_desktop_only=current_desktop_only)
    return {match.pid for match in matches}


def running_process_names() -> set[str]:
    """Casefolded image names of every queryable process, windowed or not.

    ``process_ids_for_process_name`` only sees processes that already own a
    visible window, so it cannot answer "is this app starting up?". Cold-boot
    launches routinely have a live process for tens of seconds before the first
    window paints, and that distinction decides whether waiting longer is
    worthwhile or whether the launch genuinely failed.
    """
    win32.ensure_windows()
    import win32process

    names: set[str] = set()
    try:
        pids = [int(pid) for pid in win32process.EnumProcesses()]
    except WIN32_ERRORS:
        return names
    for pid in pids:
        if pid == 0:
            continue
        process_name = get_process_details(pid).name
        if process_name:
            names.add(process_name.casefold())
    return names


def process_ids_for_image_name(process_name: str) -> set[int]:
    """PIDs of every queryable process with this image name, windowed or not.

    Unlike ``process_ids_for_process_name`` this also returns helper and
    background processes, which is what a full shutdown has to wait for.
    """
    win32.ensure_windows()
    import win32process

    target = process_name.casefold()
    try:
        pids = [int(pid) for pid in win32process.EnumProcesses()]
    except WIN32_ERRORS:
        return set()
    matches: set[int] = set()
    for pid in pids:
        if pid == 0:
            continue
        name = get_process_details(pid).name
        if name and name.casefold() == target:
            matches.add(pid)
    return matches


def is_process_running(process_name: str) -> bool:
    """True when a process with this image name exists, with or without a window."""
    return process_name.casefold() in running_process_names()


def terminate_process_id(pid: int, *, exit_code: int = 1) -> bool:
    """Forcefully terminate a process by PID. Returns True if the kill was issued."""
    win32.ensure_windows()
    import win32api
    import win32con

    try:
        handle = win32api.OpenProcess(win32con.PROCESS_TERMINATE, False, pid)
    except WIN32_ERRORS:
        return False
    try:
        win32api.TerminateProcess(handle, exit_code)
        return True
    except WIN32_ERRORS:
        return False
    finally:
        win32api.CloseHandle(handle)


def wait_for_process_exit(pid: int, timeout_seconds: float = 5.0) -> bool:
    """Wait for a process to exit. Returns True once it has (or if it cannot be opened)."""
    win32.ensure_windows()
    import win32api
    import win32con
    import win32event

    try:
        handle = win32api.OpenProcess(win32con.SYNCHRONIZE, False, pid)
    except WIN32_ERRORS:
        return True
    try:
        result = win32event.WaitForSingleObject(handle, int(max(timeout_seconds, 0.0) * 1000))
        return result == win32event.WAIT_OBJECT_0
    except WIN32_ERRORS:
        return False
    finally:
        win32api.CloseHandle(handle)


def collect_windows_matching_process(target_process_name: str, *, current_desktop_only: bool = True) -> list[int]:
    win32.ensure_windows()
    matches = _process_windows(target_process_name, current_desktop_only=current_desktop_only)
    return [match.hwnd for match in matches]


def window_titles_for_process(target_process_name: str, *, current_desktop_only: bool = False) -> dict[int, str]:
    """hwnd -> window title for every visible window owned by this process.

    ``collect_windows_matching_process`` compares against an already-casefolded
    name, so callers must normalize; doing it here keeps that trap out of the
    caller and returning ``{}`` for "Code.exe" instead of "code.exe".
    """
    win32.ensure_windows()
    import win32gui

    titles: dict[int, str] = {}
    for hwnd in collect_windows_matching_process(
        target_process_name.casefold(), current_desktop_only=current_desktop_only
    ):
        try:
            titles[hwnd] = win32gui.GetWindowText(hwnd)
        except WIN32_ERRORS:
            continue
    return titles
