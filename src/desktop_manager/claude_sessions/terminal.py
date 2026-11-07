"""Terminal windows and tabs as a backend reports them. Pure data, importable on any platform."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class TerminalTab:
    index: int
    title: str
    selected: bool
    runtime_id: tuple[int, ...]


@dataclass(frozen=True)
class TerminalWindow:
    hwnd: int
    title: str
    tabs: tuple[TerminalTab, ...]
