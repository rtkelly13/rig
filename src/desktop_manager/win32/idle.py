"""Seconds since the last keyboard or mouse input, from any source, as Windows records it."""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from typing import final

from desktop_manager.win32 import dlls

_TICK_WRAP = 2**32


@final
class _LastInputInfo(ctypes.Structure):
    _fields_ = [("cb_size", wintypes.UINT), ("dw_time", wintypes.DWORD)]
    cb_size: int
    dw_time: int

    def __init__(self) -> None:
        super().__init__()
        self.cb_size = ctypes.sizeof(_LastInputInfo)
        self.dw_time = 0


def idle_seconds() -> float:
    """Seconds since the last input event; `GetTickCount` wraps every 49.7 days, so the difference is taken modulo."""
    now = dlls.call_int(dlls.system_dll("kernel32").GetTickCount) % _TICK_WRAP
    info = _LastInputInfo()
    last = int(info.dw_time) if dlls.system_dll("user32").GetLastInputInfo(ctypes.byref(info)) else now
    return ((now - last) % _TICK_WRAP) / 1000
