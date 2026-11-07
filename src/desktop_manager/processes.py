"""Process-tree inspection used to tell a disposable terminal from a working one.

Cleanup after a restore cannot work on image names alone. Warp and Windows Terminal
are single-instance: a launch hands the new window to the already-running process and
the launcher exits, so the PID a launch returns is not the PID that owns the window,
and the process that *does* own it also owns every other window the user has open --
including the one running the session that started the restore. Killing by image name
is therefore either useless or catastrophic.

What distinguishes a stray window from a live one is the shell inside it. Each Warp
window runs its own shell as a direct child of the Warp process. A window nobody is
using sits at a prompt with no child process; a window running something has one.
That is the signal this module exposes: ``idle_shell_pids_under`` finds shells with no
descendants, and ends up reaping exactly the windows a restore or a test leaked while
leaving anything with work in flight alone.
"""

from __future__ import annotations

import ctypes
import os
from ctypes import wintypes
from dataclasses import dataclass
from typing import final

from desktop_manager.win32 import dlls

# Shell images that a terminal host runs as its per-window child process.
SHELL_IMAGE_NAMES = frozenset({"pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe", "zsh.exe", "nu.exe"})

IS_WINDOWS: bool = os.name == "nt"

_TH32CS_SNAPPROCESS = 0x00000002
_INVALID_HANDLE_VALUE = -1
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_MAX_PATH = 260


@dataclass(frozen=True, slots=True)
class ProcessInfo:
    """One row of the process table: enough to walk parent/child links by image."""

    pid: int
    parent_pid: int
    image_name: str
    created_at: float | None = None


@final
class _ProcessEntry32(ctypes.Structure):
    _fields_ = (
        ("size", wintypes.DWORD),
        ("usage", wintypes.DWORD),
        ("process_id", wintypes.DWORD),
        ("default_heap_id", ctypes.POINTER(ctypes.c_ulong)),
        ("module_id", wintypes.DWORD),
        ("threads", wintypes.DWORD),
        ("parent_process_id", wintypes.DWORD),
        ("priority_class_base", ctypes.c_long),
        ("flags", wintypes.DWORD),
        ("exe_file", ctypes.c_char * _MAX_PATH),
    )
    size: int
    process_id: int
    parent_process_id: int
    exe_file: bytes

    def __init__(self) -> None:
        super().__init__()
        self.size = ctypes.sizeof(_ProcessEntry32)
        self.process_id = 0
        self.parent_process_id = 0
        self.exe_file = b""


def _process_created_at(pid: int) -> float | None:
    """Unix timestamp the process started, or None if it cannot be queried.

    Needed because a PID alone cannot distinguish a shell that predates the restore
    from one the restore leaked -- and killing the former loses the user's work.
    """
    if not IS_WINDOWS:
        return None
    kernel32 = dlls.system_dll("kernel32")
    handle = dlls.call_int(kernel32.OpenProcess, _PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        creation = wintypes.FILETIME()
        exit_time = wintypes.FILETIME()
        kernel_time = wintypes.FILETIME()
        user_time = wintypes.FILETIME()
        ok = dlls.call_int(
            kernel32.GetProcessTimes,
            handle,
            ctypes.byref(creation),
            ctypes.byref(exit_time),
            ctypes.byref(kernel_time),
            ctypes.byref(user_time),
        )
        if not ok:
            return None
        # FILETIME is 100ns ticks since 1601-01-01; 11644473600s to the Unix epoch.
        ticks = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        return ticks / 10_000_000.0 - 11_644_473_600.0
    finally:
        kernel32.CloseHandle(handle)


def snapshot_processes(*, with_times: bool = False) -> list[ProcessInfo]:
    """Every process with its parent, via CreateToolhelp32Snapshot.

    ``tasklist`` cannot report parents and ``wmic`` is deprecated, so this walks the
    snapshot directly. ``with_times`` costs an OpenProcess per row, so it is opt-in.
    """
    if not IS_WINDOWS:
        return []
    kernel32 = dlls.system_dll("kernel32")
    snapshot = dlls.call_int(kernel32.CreateToolhelp32Snapshot, _TH32CS_SNAPPROCESS, 0)
    if snapshot == _INVALID_HANDLE_VALUE:
        return []
    processes: list[ProcessInfo] = []
    try:
        entry = _ProcessEntry32()
        if not kernel32.Process32First(snapshot, ctypes.byref(entry)):
            return []
        while True:
            pid = entry.process_id
            processes.append(
                ProcessInfo(
                    pid=pid,
                    parent_pid=entry.parent_process_id,
                    image_name=entry.exe_file.decode("latin-1", errors="replace"),
                    created_at=_process_created_at(pid) if with_times else None,
                )
            )
            if not kernel32.Process32Next(snapshot, ctypes.byref(entry)):
                break
    finally:
        kernel32.CloseHandle(snapshot)
    return processes


def pids_with_children(processes: list[ProcessInfo]) -> set[int]:
    """PIDs that are the parent of at least one process in the snapshot."""
    return {process.parent_pid for process in processes}


def idle_shell_pids_under(
    parent_image: str,
    *,
    created_after: float | None = None,
    processes: list[ProcessInfo] | None = None,
) -> list[int]:
    """Shells hosted by ``parent_image`` that have nothing running inside them.

    A shell with any child process is running a command, so it is left alone: that is
    the guard that keeps an active session -- the one that launched the restore -- from
    being reaped. ``created_after`` narrows the result to shells started since a
    given time, so cleanup only claims windows that appeared on its own watch.

    Returned oldest-first so a caller closing them sees a stable order.
    """
    snapshot = processes if processes is not None else snapshot_processes(with_times=created_after is not None)
    parent_target = parent_image.casefold()
    host_pids = {process.pid for process in snapshot if process.image_name.casefold() == parent_target}
    if not host_pids:
        return []
    busy = pids_with_children(snapshot)
    idle = [
        process
        for process in snapshot
        if process.parent_pid in host_pids
        and process.image_name.casefold() in SHELL_IMAGE_NAMES
        and process.pid not in busy
        and (created_after is None or (process.created_at is not None and process.created_at >= created_after))
    ]
    idle.sort(key=lambda process: (process.created_at or 0.0, process.pid))
    return [process.pid for process in idle]


def is_idle_shell_under(pid: int, parent_image: str) -> bool:
    """Re-check a single candidate against a fresh snapshot, just before killing it.

    A scan is not atomic: a shell that was empty when listed may have started a command
    by the time the kill lands. Re-reading the table per PID keeps that window as small
    as it can be made without a handle.
    """
    snapshot = snapshot_processes()
    parent_target = parent_image.casefold()
    host_pids = {process.pid for process in snapshot if process.image_name.casefold() == parent_target}
    busy = pids_with_children(snapshot)
    return any(
        process.pid == pid
        and process.parent_pid in host_pids
        and process.image_name.casefold() in SHELL_IMAGE_NAMES
        and process.pid not in busy
        for process in snapshot
    )
