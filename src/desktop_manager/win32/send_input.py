"""Keyboard events for user32 ``SendInput``: the INPUT structures and the events that type text.

Building the batch is pure ctypes, so it imports and runs anywhere; only handing
it to ``SendInput`` needs Windows, and that call (with its error handling) stays
with each caller.
"""

from __future__ import annotations

import ctypes
from dataclasses import dataclass
from typing import final

INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004


@final
class KeyboardInput(ctypes.Structure):
    _fields_ = (
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", ctypes.c_size_t),
    )


@final
class MouseInput(ctypes.Structure):
    _fields_ = (
        ("dx", ctypes.c_long),
        ("dy", ctypes.c_long),
        ("mouseData", ctypes.c_ulong),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", ctypes.c_size_t),
    )


@final
class InputUnion(ctypes.Union):
    # MOUSEINPUT is the largest member; without it SendInput rejects the struct size.
    _fields_ = (("ki", KeyboardInput), ("mi", MouseInput))


@final
class Input(ctypes.Structure):
    _fields_ = (("type", ctypes.c_ulong), ("u", InputUnion))


INPUT_SIZE = ctypes.sizeof(Input)


@dataclass(frozen=True, slots=True)
class KeyEvent:
    """One keyboard event: a virtual key, or with KEYEVENTF_UNICODE a UTF-16 unit in `scan`."""

    vk: int
    scan: int
    flags: int


def key_tap(virtual_key: int) -> list[KeyEvent]:
    """Press and release one virtual key."""
    return [KeyEvent(virtual_key, 0, 0), KeyEvent(virtual_key, 0, KEYEVENTF_KEYUP)]


def unicode_events(text: str) -> list[KeyEvent]:
    """Type `text` as Unicode key events, one down/up pair per UTF-16 unit, whatever the keyboard layout."""
    encoded = text.encode("utf-16-le")
    events: list[KeyEvent] = []
    for i in range(0, len(encoded), 2):
        unit = int.from_bytes(encoded[i : i + 2], "little")
        events += [KeyEvent(0, unit, KEYEVENTF_UNICODE), KeyEvent(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]
    return events


def input_batch(events: list[KeyEvent]) -> ctypes.Array[Input]:
    """The INPUT array to pass to SendInput with ``len(events)`` and :data:`INPUT_SIZE`."""
    return (Input * len(events))(
        *(Input(INPUT_KEYBOARD, InputUnion(ki=KeyboardInput(e.vk, e.scan, e.flags, 0, 0))) for e in events)
    )
