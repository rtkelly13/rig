"""What the coordinator asks Windows about the session: boot time, and whether the shell is up."""

from __future__ import annotations

import ctypes
import sys
import time
from datetime import datetime, timedelta
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import Callable


def _call_int(function: Callable[[], int]) -> int:
    return function()


def boot_time() -> datetime:
    """When this machine booted; the epoch where that cannot be read."""
    if sys.platform != "win32":
        return datetime.fromtimestamp(0).astimezone()
    kernel32 = ctypes.WinDLL("kernel32")
    kernel32.GetTickCount64.restype = ctypes.c_ulonglong
    milliseconds = _call_int(kernel32.GetTickCount64)
    return datetime.now().astimezone() - timedelta(milliseconds=milliseconds)


def wait_for_shell(timeout_seconds: float) -> bool:
    """Wait for Explorer to own a shell window. False on timeout."""
    if sys.platform != "win32":
        return True
    user32 = ctypes.WinDLL("user32")
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if user32.GetShellWindow():
            return True
        time.sleep(0.25)
    return False
