"""``win32api.GetMonitorInfo``'s dict, decoded once into a record."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Annotated

from pydantic import BeforeValidator, Field

from desktop_manager.models import Rect
from rig.typed_json import decode, try_decode

_EDGE_COUNT = 4


@dataclass(frozen=True, slots=True)
class Edges:
    """A Win32 ``RECT``: the four edges in screen pixels, right and bottom exclusive."""

    left: int
    top: int
    right: int
    bottom: int

    def as_rect(self) -> Rect:
        return Rect(left=self.left, top=self.top, width=self.right - self.left, height=self.bottom - self.top)


def _edges(value: object) -> object:
    """pywin32's ``(left, top, right, bottom)`` sequence as ``Edges``."""
    edges = try_decode(list[int], value)
    if edges is None or len(edges) != _EDGE_COUNT:
        raise ValueError("expected four edges: (left, top, right, bottom)")
    left, top, right, bottom = edges
    return Edges(left=left, top=top, right=right, bottom=bottom)


@dataclass(frozen=True, slots=True)
class Win32MonitorInfo:
    """One monitor's ``MONITORINFOEX``: its full bounds, its work area (less the taskbar), flags and device."""

    monitor: Annotated[Edges, BeforeValidator(_edges), Field(alias="Monitor")]
    work: Annotated[Edges, BeforeValidator(_edges), Field(alias="Work")]
    flags: Annotated[int, Field(alias="Flags")] = 0
    device: Annotated[str, Field(alias="Device")] = "?"


def get_monitor_info(monitor: int) -> Win32MonitorInfo:
    """``GetMonitorInfo`` for an ``HMONITOR``; raises ``pydantic.ValidationError`` on an unexpected shape."""
    import win32api

    return decode(Win32MonitorInfo, win32api.GetMonitorInfo(monitor))
