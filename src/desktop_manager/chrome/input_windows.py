"""Keyboard, foreground and polling helpers for driving Chrome on Windows.

Imports Windows modules lazily, so it imports anywhere; calling it needs Windows.
"""

from __future__ import annotations

import time
from collections.abc import Callable
from contextlib import suppress

from desktop_manager.win32 import dlls, send_input

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


def type_text(text: str) -> None:
    """Type text into whatever has focus as Unicode key events, independent of keyboard layout.

    UIA's ValuePattern.SetValue on the group title field shows the text but
    Chrome never commits it, so the title has to arrive as typing.
    """
    events = send_input.unicode_events(text)
    dlls.system_dll("user32").SendInput(len(events), send_input.input_batch(events), send_input.INPUT_SIZE)


def poll[T](probe: Callable[[], T | None], timeout_seconds: float) -> T | None:
    deadline = time.perf_counter() + timeout_seconds
    while True:
        result = probe()
        if result is not None:
            return result
        if time.perf_counter() >= deadline:
            return None
        time.sleep(_POLL_SECONDS)
