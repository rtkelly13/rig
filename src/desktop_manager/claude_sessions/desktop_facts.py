"""Best-effort facts about where terminal windows sit: virtual desktops and workspace-plan slots.

Read-only listings report these and never act on them. pyvda raises bare ``Exception`` (a
desktop removed mid-read, a window mid-move); :func:`pyvda_errors` turns that into a
catchable error, so a failed read shows as "unknown" instead of aborting the listing.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass

from desktop_manager.win32.errors import PYVDA_ERRORS, WIN32_ERRORS, pyvda_errors


@dataclass(frozen=True, slots=True)
class VirtualDesktopFacts:
    number: int
    name: str
    current: bool


def virtual_desktops() -> list[VirtualDesktopFacts]:
    """Every virtual desktop, in task-view order; empty when they cannot be read."""
    if sys.platform != "win32":
        return []
    try:
        with pyvda_errors():
            from pyvda import VirtualDesktop, get_virtual_desktops

            current: int = VirtualDesktop.current().number
            return [VirtualDesktopFacts(d.number, d.name, d.number == current) for d in get_virtual_desktops()]
    except PYVDA_ERRORS:
        return []


def window_desktop(hwnd: int) -> int | None:
    """The virtual desktop a window is on; None when it cannot be read."""
    if sys.platform != "win32":
        return None
    try:
        with pyvda_errors():
            from pyvda import AppView

            number: int = AppView(hwnd).desktop.number
            return number
    except PYVDA_ERRORS:
        return None


def window_slot(hwnd: int) -> str | None:
    """The workspace-plan screen slot a window fills, as `place` prints it; None when it fills none."""
    if sys.platform != "win32":
        return None
    try:
        from desktop_manager.placement import format_placement
        from desktop_manager.win32.geometry import infer_placement_for_window

        placement = infer_placement_for_window(hwnd)
    except WIN32_ERRORS:
        return None
    return format_placement(placement) if placement else None
