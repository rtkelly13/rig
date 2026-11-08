"""Drive the running Windows Terminal through UI Automation and real keystrokes.

This is the "computer use" layer: it works on the windows and tabs the user already
has, instead of spawning new ones from the command line. Callers decide which session
goes where; this module only knows how to reach a tab and type into it safely.

Lessons that shaped it, learned against a live terminal:

- **The tab strip lives in the XAML island, not the top-level window.** `ElementFromHandle`
  on the `CASCADIA_HOSTING_WINDOW_CLASS` window exposes only the caption buttons. The
  tabs (`TabItem` under `TabListView`), the `NewTabButton` and the `TermControl` hang off
  the child `Windows.UI.Input.InputSite.WindowClass` window.
- **New tabs come from invoking `NewTabButton`.** That opens the user's *default profile*,
  with its colours, font and shell. `wt new-tab <commandline>` from a script opens a bare
  shell with the wrong look, and `-w <name>` opens new windows. Both were the failures
  this module replaces.
- **Posted `WM_CHAR` arrives mangled** (`^E^C`), so text goes in through `SendInput`
  Unicode keystrokes. That needs the window in the foreground. Every send first checks
  that the foreground window and the selected tab are still the target, and refuses
  otherwise, so a click elsewhere mid-run can never send a command to the wrong prompt.
- **The selected tab's buffer is readable** through the `TermControl` TextPattern. That
  is how readiness is detected, without timing guesses. Only the selected tab's control
  is in the tree.
"""

from __future__ import annotations

import ctypes
import subprocess
import sys
import time
from collections.abc import Callable
from contextlib import suppress
from ctypes import wintypes
from pathlib import Path
from typing import TYPE_CHECKING, final

from desktop_manager.claude_sessions.terminal import TerminalTab, TerminalWindow
from desktop_manager.win32 import dlls
from desktop_manager.win32.send_input import INPUT_SIZE, KEYEVENTF_KEYUP, KeyEvent, input_batch, key_tap, unicode_events

if TYPE_CHECKING:
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationCondition,
        IUIAutomationElement,
        IUIAutomationSelectionItemPattern,
    )

WT_WINDOW_CLASS = "CASCADIA_HOSTING_WINDOW_CLASS"
_INPUT_SITE_CLASS = "Windows.UI.Input.InputSite.WindowClass"
_WT_PROCESS_NAME = "windowsterminal.exe"
_POLL_SECONDS = 0.1


class TerminalAutomationError(RuntimeError):
    """The terminal did not reach the state a step needed; nothing further was typed."""


# --- win32 plumbing -----------------------------------------------------------------------------

_EnumProc = None
if sys.platform == "win32":
    _EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def _uia_errors() -> tuple[type[Exception], ...]:
    if sys.platform == "win32":
        from _ctypes import COMError

        return (TerminalAutomationError, OSError, COMError)
    return (TerminalAutomationError, OSError)


def _user32() -> ctypes.CDLL:
    return dlls.system_dll("user32")


#: What a UI Automation read can raise: our own refusals, Win32 errors and, on Windows, COM failures.
UIA_ERRORS = _uia_errors()


def _class_name(hwnd: int) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    _user32().GetClassNameW(hwnd, buffer, 256)
    return ctypes.wstring_at(buffer)


def _process_image(hwnd: int) -> str:

    pid = wintypes.DWORD()
    _user32().GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    kernel32 = dlls.system_dll("kernel32")
    handle = dlls.call_int(kernel32.OpenProcess, 0x1000, False, pid.value)
    if not handle:
        return ""
    try:
        size = wintypes.DWORD(1024)
        buffer = ctypes.create_unicode_buffer(1024)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return ""
        return Path(ctypes.wstring_at(buffer)).name.lower()
    finally:
        kernel32.CloseHandle(handle)


def _enum(parent: int | None) -> list[int]:
    found: list[int] = []

    def _collect(hwnd: int, _: int) -> bool:
        found.append(hwnd)
        return True

    if _EnumProc is None:
        return found
    callback = _EnumProc(_collect)
    if parent is None:
        _user32().EnumWindows(callback, 0)
    else:
        _user32().EnumChildWindows(parent, callback, 0)
    return found


def terminal_window_handles() -> list[int]:
    """Visible top-level Windows Terminal windows, in z-order (most recently active first)."""
    return [
        hwnd
        for hwnd in _enum(None)
        if _class_name(hwnd) == WT_WINDOW_CLASS
        and _user32().IsWindowVisible(hwnd)
        and _process_image(hwnd) == _WT_PROCESS_NAME
    ]


_VK_RETURN = 0x0D
_VK_ESCAPE = 0x1B
_VK_MENU = 0x12


def keystrokes_for(text: str, *, clear_line: bool = True, submit: bool = True) -> list[KeyEvent]:
    """Key events for `text`: Esc to clear the line, Unicode chars, then Enter."""
    events: list[KeyEvent] = []
    if clear_line:
        events += key_tap(_VK_ESCAPE)
    events += unicode_events(text)
    if submit:
        events += key_tap(_VK_RETURN)
    return events


def echoed(before: str, after: str, text: str) -> bool:
    """Whether `text` has appeared on screen since `before`, wherever the terminal wrapped it."""
    wanted = _squash(text)
    return bool(wanted) and _squash(after).count(wanted) > _squash(before).count(wanted)


def _squash(text: str) -> str:
    return "".join(text.split())


def _send(events: list[KeyEvent]) -> None:
    sent = dlls.call_int(_user32().SendInput, len(events), input_batch(events), INPUT_SIZE)
    if sent != len(events):
        raise TerminalAutomationError(f"SendInput delivered {sent} of {len(events)} key events")


def _bring_to_front(hwnd: int) -> bool:
    """Foreground `hwnd`. A synthetic Alt tap satisfies the foreground-lock rule for background callers."""
    if _user32().IsIconic(hwnd):
        _user32().ShowWindow(hwnd, 9)  # SW_RESTORE
    _user32().keybd_event(_VK_MENU, 0, 0, 0)
    _user32().keybd_event(_VK_MENU, 0, KEYEVENTF_KEYUP, 0)
    _user32().SetForegroundWindow(hwnd)
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        if _user32().GetForegroundWindow() == hwnd:
            return True
        time.sleep(_POLL_SECONDS)
    return False


def _wait_for[T](probe: Callable[[], T | None], *, timeout: float, what: str) -> T:
    deadline = time.monotonic() + timeout
    while True:
        value = probe()
        if value is not None:
            return value
        if time.monotonic() >= deadline:
            raise TerminalAutomationError(f"timed out after {timeout:.0f}s waiting for {what}")
        time.sleep(_POLL_SECONDS)


# --- UI Automation ------------------------------------------------------------------------------


@final
class WindowsTerminalUia:
    """A UIA session against the running Windows Terminal. Windows only; construct once per command."""

    def __init__(self) -> None:
        from desktop_manager.win32 import ensure_windows

        ensure_windows()
        import comtypes.client

        comtypes.client.GetModule("UIAutomationCore.dll")
        from comtypes.gen import UIAutomationClient

        self._u = UIAutomationClient
        self._uia = comtypes.client.CreateObject(
            UIAutomationClient.CUIAutomation, interface=UIAutomationClient.IUIAutomation
        )

    def _condition(self, prop: int, value: int | str) -> IUIAutomationCondition:
        return self._uia.CreatePropertyCondition(prop, value)

    def _island(self, hwnd: int) -> IUIAutomationElement:
        sites = [child for child in _enum(hwnd) if _class_name(child) == _INPUT_SITE_CLASS]
        if not sites:
            raise TerminalAutomationError(f"window {hwnd} has no XAML input site")
        return self._uia.ElementFromHandle(sites[0])

    def _tab_elements(self, hwnd: int) -> list[IUIAutomationElement]:
        strip = self._island(hwnd).FindFirst(
            self._u.TreeScope_Descendants, self._condition(self._u.UIA_AutomationIdPropertyId, "TabListView")
        )
        if not strip:
            return []
        items = strip.FindAll(
            self._u.TreeScope_Children,
            self._condition(self._u.UIA_ControlTypePropertyId, self._u.UIA_TabItemControlTypeId),
        )
        return [items.GetElement(i) for i in range(items.Length)]

    def _selection(self, element: IUIAutomationElement) -> IUIAutomationSelectionItemPattern:
        return element.GetCurrentPattern(self._u.UIA_SelectionItemPatternId).QueryInterface(
            self._u.IUIAutomationSelectionItemPattern
        )

    def _to_tab(self, index: int, element: IUIAutomationElement) -> TerminalTab:
        return TerminalTab(
            index=index,
            title=element.CurrentName or "",
            selected=bool(self._selection(element).CurrentIsSelected),
            runtime_id=tuple(element.GetRuntimeId()),
        )

    def tabs(self, hwnd: int) -> tuple[TerminalTab, ...]:
        return tuple(self._to_tab(i, element) for i, element in enumerate(self._tab_elements(hwnd)))

    def list_windows(self) -> list[TerminalWindow]:
        windows: list[TerminalWindow] = []
        for hwnd in terminal_window_handles():
            buffer = ctypes.create_unicode_buffer(512)
            _user32().GetWindowTextW(hwnd, buffer, 512)
            windows.append(TerminalWindow(hwnd=hwnd, title=ctypes.wstring_at(buffer), tabs=self.tabs(hwnd)))
        return windows

    def selected_tab(self, hwnd: int) -> TerminalTab | None:
        return next((tab for tab in self.tabs(hwnd) if tab.selected), None)

    def select_tab(self, hwnd: int, runtime_id: tuple[int, ...]) -> TerminalTab:
        for index, element in enumerate(self._tab_elements(hwnd)):
            if tuple(element.GetRuntimeId()) == runtime_id:
                self._selection(element).Select()
                return _wait_for(
                    lambda: next((t for t in self.tabs(hwnd) if t.runtime_id == runtime_id and t.selected), None),
                    timeout=5,
                    what=f"tab {index} to become selected",
                )
        raise TerminalAutomationError(f"tab {runtime_id} is no longer in window {hwnd}")

    def new_tab(self, hwnd: int) -> TerminalTab:
        """Open a tab in the user's default profile via the New Tab button; returns it once selected."""
        before = {tab.runtime_id for tab in self.tabs(hwnd)}
        button = self._island(hwnd).FindFirst(
            self._u.TreeScope_Descendants, self._condition(self._u.UIA_AutomationIdPropertyId, "NewTabButton")
        )
        if not button:
            raise TerminalAutomationError(f"window {hwnd} has no New Tab button")
        button.GetCurrentPattern(self._u.UIA_InvokePatternId).QueryInterface(
            self._u.IUIAutomationInvokePattern
        ).Invoke()
        return _wait_for(
            lambda: next((t for t in self.tabs(hwnd) if t.runtime_id not in before and t.selected), None),
            timeout=10,
            what="the new tab",
        )

    def selected_text(self, hwnd: int) -> str:
        """The selected tab's visible buffer; empty until the shell has drawn anything."""
        control = self._island(hwnd).FindFirst(
            self._u.TreeScope_Descendants, self._condition(self._u.UIA_ClassNamePropertyId, "TermControl")
        )
        if not control:
            return ""
        pattern = control.GetCurrentPattern(self._u.UIA_TextPatternId)
        if not pattern:
            return ""
        return pattern.QueryInterface(self._u.IUIAutomationTextPattern).DocumentRange.GetText(-1) or ""

    def wait_for_prompt(self, hwnd: int, *, timeout: float = 20) -> None:
        """Wait until the selected tab's shell has drawn something and stopped changing."""
        last: list[str] = [""]
        stable_since: list[float] = [0.0]

        def settled() -> bool | None:
            text = self.selected_text(hwnd).strip()
            now = time.monotonic()
            if not text or text != last[0]:
                last[0], stable_since[0] = text, now
                return None
            return True if now - stable_since[0] >= 0.75 else None

        _wait_for(settled, timeout=timeout, what="the shell prompt")

    def type_line(self, hwnd: int, runtime_id: tuple[int, ...], text: str, *, echo_timeout: float = 15) -> None:
        """Type `text` + Enter into the given tab, refusing if focus has moved anywhere else.

        Enter goes only once the text shows on the tab's screen. At logon a restored
        window can hold the foreground with keyboard focus outside its terminal control:
        SendInput then reports every key delivered and the shell sees none of them. So
        the control is given focus first, and if the text still does not appear it is
        typed once more before giving up with nothing submitted.
        """
        before = self.selected_text(hwnd)
        for attempt in range(2):
            self._take_focus(hwnd, runtime_id)
            _send(keystrokes_for(text, submit=False))
            try:
                _wait_for(
                    lambda: True if echoed(before, self.selected_text(hwnd), text) else None,
                    timeout=echo_timeout,
                    what="the typed text to appear",
                )
                break
            except TerminalAutomationError:
                if attempt:
                    raise TerminalAutomationError(
                        f"the typed text never appeared in window {hwnd} after {echo_timeout:.0f}s; nothing submitted"
                    ) from None
        self._take_focus(hwnd, runtime_id)
        _send(keystrokes_for("", clear_line=False))

    def _take_focus(self, hwnd: int, runtime_id: tuple[int, ...]) -> None:
        """Foreground `hwnd` with keyboard focus in its terminal control, on the target tab."""
        if not _bring_to_front(hwnd):
            raise TerminalAutomationError(f"could not bring window {hwnd} to the foreground")
        selected = self.selected_tab(hwnd)
        if selected is None or selected.runtime_id != runtime_id:
            raise TerminalAutomationError("the target tab is no longer selected; typed nothing")
        control = self._island(hwnd).FindFirst(
            self._u.TreeScope_Descendants, self._condition(self._u.UIA_ClassNamePropertyId, "TermControl")
        )
        if control:
            with suppress(*UIA_ERRORS):  # focus is a best effort; the echo check is the guard
                control.SetFocus()
        if _user32().GetForegroundWindow() != hwnd:
            raise TerminalAutomationError("focus moved away before typing; typed nothing")

    def open_window(self) -> int:
        """Open a new Windows Terminal window in the default profile and return its handle."""
        before = set(terminal_window_handles())
        subprocess.Popen(["wt.exe", "-w", "new"])
        return _wait_for(
            lambda: next((h for h in terminal_window_handles() if h not in before), None),
            timeout=15,
            what="a new terminal window",
        )


def foreground_window() -> int:
    return int(_user32().GetForegroundWindow() or 0)


def restore_foreground(hwnd: int) -> None:
    if hwnd and _user32().IsWindow(hwnd):
        _bring_to_front(hwnd)
