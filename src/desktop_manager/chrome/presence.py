"""Whether the user is at the keyboard or on a call, for runs that must not move the mouse under them.

Windows records the time of the last keyboard or mouse input, from any source.
Synthetic input (the grouping's own clicks) counts too, so `InputWatch` notes
the time after each of its own steps and treats anything later as the user's.
"""

from __future__ import annotations

import ctypes
import sys
import time
from collections.abc import Callable
from ctypes import wintypes
from dataclasses import dataclass
from typing import ClassVar, final

from desktop_manager.win32 import dlls

_TICK_WRAP = 2**32
_DESKTOP_SWITCHDESKTOP = 0x0100
_CONSENT_STORE = r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore"
#: The privacy settings' names for the devices, and how a block reports them.
_DEVICES = (("microphone", "microphone"), ("webcam", "camera"))


@final
class _LastInputInfo(ctypes.Structure):
    _fields_ = [("cb_size", wintypes.UINT), ("dw_time", wintypes.DWORD)]
    cb_size: int
    dw_time: int

    def __init__(self) -> None:
        super().__init__()
        self.cb_size = ctypes.sizeof(_LastInputInfo)
        self.dw_time = 0


def last_input_tick() -> int:
    """`GetTickCount` value at the last input event."""
    info = _LastInputInfo()
    if not dlls.system_dll("user32").GetLastInputInfo(ctypes.byref(info)):
        return _now_tick()
    return int(info.dw_time)


def idle_seconds() -> float:
    return ((_now_tick() - last_input_tick()) % _TICK_WRAP) / 1000


def session_locked() -> bool:
    """Whether the lock screen holds the input desktop.

    While it does, nothing on the user's desktop can take the foreground or read the
    cursor, so a run that needs the mouse has to wait for an unlock.
    """
    desktop = int(dlls.system_dll("user32").OpenInputDesktop(0, False, _DESKTOP_SWITCHDESKTOP) or 0)
    if not desktop:
        return True
    try:
        return not dlls.system_dll("user32").SwitchDesktop(desktop)
    finally:
        dlls.system_dll("user32").CloseDesktop(desktop)


def devices_in_use(executable: str = "chrome.exe") -> list[str]:
    """The devices ("microphone", "camera") a desktop app is using right now.

    Windows keeps, per app, when it last started and stopped using each device (the
    privacy settings' "recent activity"); a stop time of 0 means it still is. That
    holds while a call is muted, and for a Meet app window, which has no tab to read.
    """
    if sys.platform != "win32":
        return []
    import winreg

    exe = executable.lower()
    using: list[str] = []
    for store, device in _DEVICES:
        try:
            apps = winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"{_CONSENT_STORE}\{store}\NonPackaged")
        except OSError:
            continue
        with apps:
            for index in range(winreg.QueryInfoKey(apps)[0]):
                name = winreg.EnumKey(apps, index)
                if not name.lower().endswith(f"#{exe}"):
                    continue
                with winreg.OpenKey(apps, name) as app:
                    try:
                        started = dlls.registry_value(winreg.QueryValueEx, app, "LastUsedTimeStart")
                        stopped = dlls.registry_value(winreg.QueryValueEx, app, "LastUsedTimeStop")
                    except OSError:
                        continue
                    if started and not stopped:
                        using.append(device)
                        break
    return using


@dataclass(frozen=True, slots=True)
class Block:
    """Why the caller may not go ahead yet."""

    reason: str
    #: Seconds until it might clear; None when unknown.
    clears_in: float | None = None


@dataclass(frozen=True)
class WaitOutcome:
    ready: bool
    waited_seconds: float
    checks: int
    last_block: str | None


def wait_until_clear(
    blocked: Callable[[], Block | None],
    *,
    budget_seconds: float,
    first_pause: float = 5.0,
    max_pause: float = 60.0,
    sleep: Callable[[float], None] = time.sleep,
    clock: Callable[[], float] = time.monotonic,
) -> WaitOutcome:
    """Ask `blocked` until it clears or the budget runs out, sleeping in between.

    Each pause doubles up to `max_pause`, so a busy machine costs a handful of cheap
    checks an hour. A block that says when it may clear (an idle count still short of
    the threshold) is checked again exactly then rather than after the full pause.
    """
    started = clock()
    pause = first_pause
    checks = 0
    last: str | None = None
    while True:
        checks += 1
        block = blocked()
        if block is None:
            return WaitOutcome(True, clock() - started, checks, last)
        last = block.reason
        remaining = budget_seconds - (clock() - started)
        if remaining <= 0:
            return WaitOutcome(False, clock() - started, checks, last)
        step = pause if block.clears_in is None else min(pause, max(block.clears_in, 0.5))
        sleep(min(step, remaining))
        pause = min(pause * 2, max_pause)


def _now_tick() -> int:
    return dlls.call_int(dlls.system_dll("kernel32").GetTickCount) % _TICK_WRAP


class InputWatch:
    """Notices input that arrives after the caller's own last step."""

    #: Our own events land a moment after the call that sends them returns.
    _GRACE_MS: ClassVar[int] = 300

    def __init__(self) -> None:
        self._mark: int = _now_tick()

    def mark(self) -> None:
        """Call after each step that sends input of its own."""
        self._mark = _now_tick()

    def user_moved(self) -> bool:
        since_mark = (last_input_tick() - self._mark) % _TICK_WRAP
        return self._GRACE_MS < since_mark < _TICK_WRAP // 2
