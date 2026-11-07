"""Keyboard, foreground and polling helpers for driving Chrome on Windows.

Imports Windows modules lazily, so it imports anywhere; calling it needs Windows.
"""

from __future__ import annotations

import ctypes
import time
from collections.abc import Callable
from contextlib import suppress
from typing import final

from desktop_manager.win32 import dlls

_POLL_SECONDS = 0.05


def foreground_is(pid: int) -> bool:
    import win32gui
    import win32process

    return win32process.GetWindowThreadProcessId(win32gui.GetForegroundWindow())[1] == pid


def bring_forward(hwnd: int, timeout_seconds: float = 1.0) -> bool:
    """Put the window back in front if something took the foreground, such as Chrome
    activating the window a tab was just moved into."""
    import win32con
    import win32gui

    if win32gui.GetForegroundWindow() == hwnd:
        return True
    press(win32con.VK_MENU)
    if not take_foreground(hwnd, timeout_seconds):
        return False
    time.sleep(0.2)
    return True


def take_foreground(hwnd: int, timeout_seconds: float) -> bool:
    """Ask for the foreground, and ask again half way through: a window just moved in
    from another virtual desktop can refuse the first request."""
    import win32con
    import win32gui

    def arrived() -> bool | None:
        return True if win32gui.GetForegroundWindow() == hwnd else None

    for attempt in range(2):
        if attempt:
            press(win32con.VK_MENU)
        with suppress(Exception):
            win32gui.SetForegroundWindow(hwnd)
        if poll(arrived, timeout_seconds / 2):
            return True
    return False


def press(virtual_key: int) -> None:
    import win32api
    import win32con

    win32api.keybd_event(virtual_key, 0, 0, 0)
    win32api.keybd_event(virtual_key, 0, win32con.KEYEVENTF_KEYUP, 0)


@final
class _KeyboardInput(ctypes.Structure):
    _fields_ = (
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", ctypes.c_size_t),
    )


@final
class _InputUnion(ctypes.Union):
    # As large as the biggest real member (MOUSEINPUT), or SendInput rejects the struct size.
    _fields_ = (("ki", _KeyboardInput), ("padding", ctypes.c_byte * 32))


@final
class _Input(ctypes.Structure):
    _fields_ = (("type", ctypes.c_ulong), ("union", _InputUnion))


_INPUT_KEYBOARD = 1
_KEYEVENTF_KEYUP = 0x0002
_KEYEVENTF_UNICODE = 0x0004


def type_text(text: str) -> None:
    """Type text into whatever has focus as Unicode key events, independent of keyboard layout.

    UIA's ValuePattern.SetValue on the group title field shows the text but
    Chrome never commits it, so the title has to arrive as typing.
    """
    data = text.encode("utf-16-le")
    events = [
        _Input(
            _INPUT_KEYBOARD, _InputUnion(ki=_KeyboardInput(0, int.from_bytes(data[i : i + 2], "little"), flags, 0, 0))
        )
        for i in range(0, len(data), 2)
        for flags in (_KEYEVENTF_UNICODE, _KEYEVENTF_UNICODE | _KEYEVENTF_KEYUP)
    ]
    array = (_Input * len(events))(*events)
    dlls.system_dll("user32").SendInput(len(events), array, ctypes.sizeof(_Input))


def poll[T](probe: Callable[[], T | None], timeout_seconds: float) -> T | None:
    deadline = time.perf_counter() + timeout_seconds
    while True:
        result = probe()
        if result is not None:
            return result
        if time.perf_counter() >= deadline:
            return None
        time.sleep(_POLL_SECONDS)
