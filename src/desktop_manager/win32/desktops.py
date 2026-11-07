from __future__ import annotations

import sys
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass

from desktop_manager import win32
from desktop_manager.win32 import dlls
from desktop_manager.win32.errors import PYVDA_ERRORS, pyvda_errors
from rig import tracing

_SCOPE = "desktop_manager.win32"


@dataclass(slots=True)
class _DesktopEventSink:
    callback: Callable[[str], None] | None = None


_EVENT_SINK = _DesktopEventSink()


def set_virtual_desktop_event_callback(callback: Callable[[str], None] | None) -> None:
    _EVENT_SINK.callback = callback


def clear_virtual_desktop_event_callback() -> None:
    set_virtual_desktop_event_callback(None)


def _emit_virtual_desktop_event(message: str) -> None:
    callback = _EVENT_SINK.callback
    if callback is None:
        return
    callback(message)


def _record_virtual_desktop_change(before: int | None, after: int | None, *, reason: str) -> None:
    if before == after:
        return
    before_label = "?" if before is None else str(before)
    after_label = "?" if after is None else str(after)
    _emit_virtual_desktop_event(f"Virtual desktop changed: {before_label} -> {after_label} ({reason})")


def _send_virtual_desktop_hotkey(direction: str, *, settle_seconds: float, reason: str) -> None:
    with tracing.span(
        "win32.send_virtual_desktop_hotkey",
        scope=_SCOPE,
        **{"hotkey.direction": direction, "hotkey.reason": reason},
    ) as s:
        before = get_current_desktop_number()
        send_hotkey(direction)
        if settle_seconds > 0:
            time.sleep(settle_seconds)
        after = get_current_desktop_number()
        s.set(
            **{
                "desktop.before": before if before is not None else 0,
                "desktop.after": after if after is not None else 0,
                "desktop.reason": reason,
            }
        )
        _record_virtual_desktop_change(before, after, reason=reason)


def switch_leftmost(steps: int) -> None:
    for _ in range(max(1, steps)):
        _send_virtual_desktop_hotkey("left", settle_seconds=0.08, reason="switch_leftmost")


def move_right(steps: int) -> None:
    for _ in range(max(1, steps)):
        _send_virtual_desktop_hotkey("right", settle_seconds=0.08, reason="move_right")


def send_hotkey(direction: str) -> None:
    win32.ensure_windows()
    import win32api
    import win32con

    combos = {
        "left": [win32con.VK_LCONTROL, win32con.VK_LWIN, win32con.VK_LEFT],
        "right": [win32con.VK_LCONTROL, win32con.VK_LWIN, win32con.VK_RIGHT],
        "create": [win32con.VK_LCONTROL, win32con.VK_LWIN, 0x44],
    }
    keys = [int(key) for key in combos[direction]]
    for key in keys:
        win32api.keybd_event(key, 0, 0, 0)
    for key in reversed(keys):
        win32api.keybd_event(key, 0, win32con.KEYEVENTF_KEYUP, 0)


def ensure_desktop_count(target_count: int, switch_delay_seconds: float) -> None:
    with tracing.span("win32.ensure_desktop_count", scope=_SCOPE, **{"desktop.target_count": target_count}) as s:
        current_count = get_virtual_desktop_count()
        s.set(**{"desktop.current_count": current_count, "desktop.created": max(0, target_count - current_count)})
        if current_count >= target_count:
            return

        needed = target_count - current_count if current_count > 0 else target_count - 1
        com_succeeded = False
        try:
            win32.ensure_windows()
            import pyvda

            with pyvda_errors():
                for _ in range(needed):
                    pyvda.VirtualDesktop.create()
                current_count = get_virtual_desktop_count()
                com_succeeded = current_count >= target_count
        except PYVDA_ERRORS:
            com_succeeded = False

        if com_succeeded:
            return

        current_count = get_virtual_desktop_count()
        if current_count >= target_count:
            return

        effective_count = max(1, current_count)
        switch_leftmost(effective_count + 2)
        time.sleep(switch_delay_seconds)
        for _ in range(target_count - effective_count):
            _send_virtual_desktop_hotkey("create", settle_seconds=switch_delay_seconds, reason="ensure_desktop_count")

        deadline = time.monotonic() + 3.0
        while time.monotonic() < deadline:
            if get_virtual_desktop_count() >= target_count:
                break
            time.sleep(0.1)


def get_virtual_desktop_count() -> int:
    try:
        win32.ensure_windows()
        with pyvda_errors():
            import pyvda

            desktops = pyvda.get_virtual_desktops()
            if desktops:
                return len(desktops)
    except PYVDA_ERRORS:
        pass

    if sys.platform != "win32":
        return 0
    import winreg

    registry_paths = [
        r"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops",
    ]
    for registry_path in registry_paths:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry_path) as key:
                value = dlls.registry_value(winreg.QueryValueEx, key, "VirtualDesktopIDs")
        except FileNotFoundError:
            continue

        if isinstance(value, bytes) and value:
            return max(1, len(value) // 16)

    return 0


def get_current_desktop_number() -> int | None:
    win32.ensure_windows()
    try:
        with pyvda_errors():
            import pyvda

            return int(pyvda.VirtualDesktop.current().number)
    except PYVDA_ERRORS:
        return None


def go_to_desktop_number(desktop_index: int) -> None:
    win32.ensure_windows()
    count = get_virtual_desktop_count()
    if desktop_index < 1 or (count > 0 and desktop_index > count):
        _emit_virtual_desktop_event(
            f"Target desktop {desktop_index} exceeds desktop count {count}; skipping navigation"
        )
        return
    import pyvda

    with tracing.span("win32.go_to_desktop", scope=_SCOPE, **{"desktop.target": desktop_index}) as s:
        before = get_current_desktop_number()
        with pyvda_errors():
            pyvda.VirtualDesktop(number=desktop_index).go()
        after = get_current_desktop_number()
        s.set(**{"desktop.before": before or 0, "desktop.after": after or 0})
    _record_virtual_desktop_change(before, after, reason=f"go_to_desktop_number target={desktop_index}")


def rename_desktop(desktop_index: int, name: str) -> bool:
    """Set the Windows name of the virtual desktop at ``desktop_index`` (1-based).

    Windows periodically drops user-assigned virtual-desktop names (e.g. across
    some updates and desktop add/remove churn), so restore re-applies the plan's
    names on every run. Returns True if the desktop already had the target name
    or was successfully renamed, False if the rename could not be applied — a
    rename failure must never abort a restore.
    """
    with tracing.span(
        "win32.rename_desktop", scope=_SCOPE, **{"desktop.index": desktop_index, "desktop.name": name}
    ) as s:
        try:
            win32.ensure_windows()
            import pyvda

            with pyvda_errors():
                desktop = pyvda.VirtualDesktop(number=desktop_index)
                previous = str(desktop.name or "")
                s.set(**{"desktop.previous_name": previous})
                if previous == name:
                    s.set(**{"desktop.changed": False})
                    return True
                desktop.rename(name)
                renamed = pyvda.VirtualDesktop(number=desktop_index).name == name
            s.set(**{"desktop.changed": renamed})
            return renamed
        except PYVDA_ERRORS as err:
            s.set(**{"desktop.rename_error": f"{type(err).__name__}: {err}"})
            return False


def retire_switched_off_desktops(keep_count: int, retired_names: Sequence[str]) -> list[str]:
    """Remove every virtual desktop named after a switched-off plan desktop, wherever it sits.

    ``ensure_desktop_count`` only ever adds desktops, so a desktop whose feature is
    switched off would otherwise linger after the plan stops managing it. A leftover
    can sit among the planned desktops, not just after them: with Personal switched
    off ahead of Lab, the old Personal desktop sits at Lab's position and restore
    would rename it to Lab, leaving the real Lab desktop as an unmanaged extra. Only
    desktops named after a switched-off plan desktop are removed; any other desktop
    is left alone. Windows moves a removed desktop's windows onto the desktop that
    ends up last among the first ``keep_count`` kept ones, so nothing is closed. At
    least one desktop is always kept. Returns the names removed; never raises.
    """
    with tracing.span(
        "win32.retire_switched_off_desktops",
        scope=_SCOPE,
        **{"desktop.keep_count": keep_count, "desktop.retire_candidates": list(retired_names)},
    ) as s:
        removed = _retire_switched_off_desktops(keep_count, retired_names)
        s.set(**{"desktop.retired": removed})
        return removed


def _retire_switched_off_desktops(keep_count: int, retired_names: Sequence[str]) -> list[str]:
    wanted = {name.casefold() for name in retired_names if name}
    if not wanted or keep_count < 1:
        return []
    removed: list[str] = []
    try:
        win32.ensure_windows()
        import pyvda

        with pyvda_errors():
            desktops = sorted(pyvda.get_virtual_desktops(), key=lambda item: item.number)
            doomed = [desktop for desktop in desktops if str(desktop.name or "").casefold() in wanted]
            kept = [desktop for desktop in desktops if desktop not in doomed]
            if not doomed or not kept:
                return []
            fallback = kept[min(keep_count, len(kept)) - 1]
            # Highest number first so removing one does not renumber the rest still to remove.
            for desktop in reversed(doomed):
                name = str(desktop.name or "")
                desktop.remove(fallback=fallback)
                removed.append(name)
    except PYVDA_ERRORS:
        return removed
    return removed


def _anchor_to_desktop_via_hotkeys(desktop_index: int, *, settle_seconds: float) -> int | None:
    count = get_virtual_desktop_count()
    if desktop_index < 1 or (count > 0 and desktop_index > count):
        return None
    switch_leftmost(desktop_index + 2)
    anchored_desktop = get_current_desktop_number()
    if anchored_desktop != 1 and desktop_index > 1:
        anchored_desktop = 1
    if desktop_index > 1:
        move_right(desktop_index - 1)
    if settle_seconds > 0:
        time.sleep(settle_seconds)
    return get_current_desktop_number()


def ensure_current_desktop_number(desktop_index: int, *, settle_seconds: float = 0.0, max_attempts: int = 3) -> bool:
    with tracing.span("win32.ensure_current_desktop", scope=_SCOPE, **{"desktop.target": desktop_index}) as s:
        confirmed = _ensure_current_desktop_number(
            desktop_index, settle_seconds=settle_seconds, max_attempts=max_attempts
        )
        s.set(**{"desktop.confirmed": confirmed})
        return confirmed


def _ensure_current_desktop_number(desktop_index: int, *, settle_seconds: float, max_attempts: int) -> bool:
    win32.ensure_windows()
    count = get_virtual_desktop_count()
    if desktop_index < 1 or (count > 0 and desktop_index > count):
        _emit_virtual_desktop_event(
            f"Target desktop {desktop_index} exceeds desktop count {count}; skipping navigation"
        )
        return False

    for _ in range(max(1, max_attempts)):
        current_desktop = get_current_desktop_number()
        if current_desktop == desktop_index:
            return True
        try:
            go_to_desktop_number(desktop_index)
        except PYVDA_ERRORS:
            break
        if settle_seconds > 0:
            time.sleep(settle_seconds)
        current_desktop = get_current_desktop_number()
        if current_desktop == desktop_index:
            return True

    current_desktop = get_current_desktop_number()
    if current_desktop == desktop_index:
        return True

    if current_desktop is None:
        final_desktop = _anchor_to_desktop_via_hotkeys(desktop_index, settle_seconds=settle_seconds)
        return final_desktop == desktop_index
    elif current_desktop > desktop_index:
        for _ in range(current_desktop - desktop_index):
            _send_virtual_desktop_hotkey("left", settle_seconds=0.08, reason="ensure_current_desktop_number")
    else:
        for _ in range(desktop_index - current_desktop):
            _send_virtual_desktop_hotkey("right", settle_seconds=0.08, reason="ensure_current_desktop_number")

    if settle_seconds > 0:
        time.sleep(settle_seconds)
    final_desktop = get_current_desktop_number()
    if final_desktop == desktop_index:
        return True

    final_desktop = _anchor_to_desktop_via_hotkeys(desktop_index, settle_seconds=settle_seconds)
    return final_desktop == desktop_index


def get_window_desktop_number(hwnd: int) -> int | None:
    win32.ensure_windows()
    try:
        with pyvda_errors():
            import pyvda

            return int(pyvda.AppView(hwnd=hwnd).desktop.number)
    except PYVDA_ERRORS:
        return None


def virtual_desktop_names() -> list[str]:
    """The Windows name of each virtual desktop, in order; ``""`` where a desktop has none.

    Empty when the names cannot be read, so callers treat that as "unknown", not "unnamed".
    """
    try:
        win32.ensure_windows()
        with pyvda_errors():
            import pyvda

            return [str(desktop.name or "") for desktop in sorted(pyvda.get_virtual_desktops(), key=lambda d: d.number)]
    except PYVDA_ERRORS:
        return []


def move_window_to_desktop_number(hwnd: int, desktop_index: int) -> None:
    win32.ensure_windows()
    count = get_virtual_desktop_count()
    if desktop_index < 1 or (count > 0 and desktop_index > count):
        _emit_virtual_desktop_event(
            f"Target desktop {desktop_index} exceeds desktop count {count}; skipping window move for hwnd={hwnd}"
        )
        return
    import pyvda

    with pyvda_errors():
        pyvda.AppView(hwnd=hwnd).move(pyvda.VirtualDesktop(number=desktop_index))


def is_window_on_current_desktop(hwnd: int) -> bool:
    win32.ensure_windows()
    try:
        with pyvda_errors():
            import pyvda

            return bool(pyvda.AppView(hwnd=hwnd).is_on_current_desktop())
    except PYVDA_ERRORS:
        return True
