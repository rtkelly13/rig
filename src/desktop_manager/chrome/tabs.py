"""What a Chrome tab looks like from outside the browser, as plain data.

UI Automation exposes a tab only through its accessible name, and Chrome packs
several facts into that one string: the page title, then optional alert
suffixes (" - Audio playing", " - Camera recording"...; some builds join them
with an en dash instead of a hyphen), then a memory suffix
(" - Memory usage - 123 MB") on tabs that are loaded. A discarded tab has no
memory suffix, which makes the suffix a cheap loaded/unloaded signal.

Everything here is pure so the parsing is testable without a browser; the UIA
adapter in `desktop_manager.chrome.uia` is the only thing that fills these in.
A tab in a tab group also carries " – Part of group <title>" (or "Part
of unnamed group") before the memory suffix. That suffix is stripped too, but
never trusted: Chrome does not refresh it when a group is renamed, so group
membership comes from the group's header element instead (`TabGroup`).

The suffixes are Chrome's English strings - another UI language defeats the
parser, and the tab then looks like an unloaded tab with a longer title.
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass

_MEMORY_SUFFIX = re.compile(
    r" [-\u2013] (?:High memory usage|Memory usage) [-\u2013] (?P<amount>[\d.,]+)\s?(?P<unit>[KMGT]?B)$"
)
_GROUP_SUFFIX = re.compile(r" [-\u2013] Part of (?:unnamed group|group .*)$")
_PINNED_SUFFIXES = (" - Pinned", " \u2013 Pinned")
#: macOS Chrome's Memory Saver label on a discarded tab.
_INACTIVE_SUFFIX = re.compile(r" [-\u2013] Inactive tab [-\u2013] [\d.,]+\s?[KMGT]?B freed up$")
#: A group header's accessible name: " group <title> – 3 tabs, •  <tab titles> – Expanded",
#: or " unnamed group – ..." when the group has no title.
_GROUP_HEADER = re.compile(r"^\s*(?:unnamed group|group (?P<title>.*?)) \u2013 \d+ [Tt]abs?\b")
_UNIT_MB = {"B": 1 / 1024**2, "KB": 1 / 1024, "MB": 1.0, "GB": 1024.0, "TB": 1024.0**2}

#: Chrome's tab alert suffixes (`IDS_TAB_AX_LABEL_*_FORMAT`). Tabs with any of the
#: "live media" ones count as audible, so nothing closes a call or a recording.
_AUDIBLE_ALERTS = (
    "Audio playing",
    "Audio muted",
    "Camera or microphone recording",
    "Camera and microphone recording",
    "Microphone recording",
    "Camera recording",
    "Tab content shared",
    "Desktop content shared",
    "Picture-in-picture",
    "Sending content to headset",
)
#: The alerts that say the user is on a call or showing their screen: an unattended run leaves Chrome alone.
LIVE_ALERTS = frozenset(
    {
        "Camera or microphone recording",
        "Camera and microphone recording",
        "Microphone recording",
        "Camera recording",
        "Tab content shared",
        "Desktop content shared",
    }
)
_OTHER_ALERTS = (
    "Connected to a Bluetooth device",
    "Connected to a USB device",
    "Connected to a serial port",
    "Connected to a HID device",
    "Network error",
    "Crashed",
)


@dataclass(frozen=True)
class TabName:
    """A tab's accessible name, split into the title and what Chrome appended."""

    title: str
    loaded: bool
    alerts: tuple[str, ...]
    #: The renderer's memory footprint Chrome reports on a loaded tab; None when unloaded.
    memory_mb: float | None = None
    #: Chrome labelled the tab pinned. Builds without the label never set this.
    pinned: bool = False

    @property
    def audible(self) -> bool:
        return any(alert in _AUDIBLE_ALERTS for alert in self.alerts)


def parse_tab_name(name: str) -> TabName:
    """Strip Chrome's memory and alert suffixes off a tab's accessible name."""
    text = _INACTIVE_SUFFIX.sub("", name.rstrip())
    loaded = False
    memory_mb: float | None = None
    memory = _MEMORY_SUFFIX.search(text)
    if memory is not None:
        loaded = True
        memory_mb = float(memory.group("amount").replace(",", "")) * _UNIT_MB[memory.group("unit")]
        text = text[: memory.start()]

    alerts: list[str] = []
    pinned = False
    stripped = True
    while stripped:
        stripped = False
        if text.endswith(_PINNED_SUFFIXES):
            pinned = True
            text = text[: -len(_PINNED_SUFFIXES[0])]
            stripped = True
            continue
        group = _GROUP_SUFFIX.search(text)
        if group is not None and not _ends_with_alert(text):
            text = text[: group.start()]
            stripped = True
            continue
        for alert in (*_AUDIBLE_ALERTS, *_OTHER_ALERTS):
            for dash in ("-", "\u2013"):
                suffix = f" {dash} {alert}"
                if text.endswith(suffix):
                    alerts.insert(0, alert)
                    text = text[: -len(suffix)]
                    stripped = True
                    break
            if stripped:
                break
    return TabName(title=text, loaded=loaded, alerts=tuple(alerts), memory_mb=memory_mb, pinned=pinned)


def _ends_with_alert(text: str) -> bool:
    return any(
        text.endswith(f" {dash} {alert}") for alert in (*_AUDIBLE_ALERTS, *_OTHER_ALERTS) for dash in ("-", "\u2013")
    )


def parse_group_header(name: str) -> str | None:
    """A tab group's title from its header's accessible name; "" for an unnamed group, None if unrecognised."""
    match = _GROUP_HEADER.match(name)
    if match is None:
        return None
    return match.group("title") or ""


@dataclass(frozen=True)
class TabGroup:
    """The tab group a tab sits in. `title` is "" for a group nobody has named."""

    runtime_id: tuple[int, ...]
    title: str


@dataclass(frozen=True)
class ChromeTab:
    """One tab-strip item. `runtime_id` is the only stable identity across a session."""

    runtime_id: tuple[int, ...]
    window_hwnd: int
    #: Visual position within its window, 0-based. Shifts as tabs close; never use it as identity.
    index: int
    name: str
    title: str
    selected: bool
    pinned: bool
    loaded: bool
    audible: bool
    alerts: tuple[str, ...] = ()
    memory_mb: float | None = None
    group: TabGroup | None = None
    #: The page address, where the platform exposes one (macOS scripting). UI Automation does not: None.
    url: str | None = None


@dataclass(slots=True, frozen=True)
class ScreenPoint:
    """A point in virtual-screen pixels; monitors left of or above the primary go negative."""

    x: int
    y: int


@dataclass(slots=True, frozen=True)
class WindowRect:
    """A window's edges in screen pixels."""

    left: int
    top: int
    right: int
    bottom: int


@dataclass(frozen=True)
class ChromeWindow:
    """A top-level Chrome browser window and its tabs, in tab-strip order."""

    hwnd: int
    title: str
    #: Chrome's profile directory ("Default", "Profile 4"), from the window's relaunch command.
    profile_dir: str | None
    #: The profile's display name ("Ryan (work)"), from the window's relaunch display name.
    profile_name: str | None
    #: Creation order across all windows of the process: lower is older. Derived from
    #: Chrome's monotonically assigned accessibility ids, so it is a heuristic, not a timestamp.
    age_rank: int
    minimized: bool
    desktop_number: int | None
    tabs: tuple[ChromeTab, ...]
    #: The restored position when minimised.
    rect: WindowRect | None = None
    #: Top-left corner of the monitor the window is on.
    monitor: ScreenPoint | None = None
    #: Set by a driver that cannot tie this window to a known profile (or it is private), so
    #: it is planned on its own instead of sharing one "(unknown profile)" scope with others.
    profile_scope: str | None = None

    @property
    def profile_key(self) -> str:
        return self.profile_scope or self.profile_dir or "(unknown profile)"

    @property
    def selected_tab(self) -> ChromeTab | None:
        return next((tab for tab in self.tabs if tab.selected), None)


def screen_order(windows: Sequence[ChromeWindow], *, current_desktop: int | None) -> list[ChromeWindow]:
    """Windows in the order a person scans them: the current desktop first, then monitor
    by monitor (left to right), then top to bottom and left to right on each monitor."""
    far = 1 << 30
    unknown = ScreenPoint(far, far)

    def key(window: ChromeWindow) -> _ScanPosition:
        desktop = window.desktop_number
        monitor = window.monitor or unknown
        corner = ScreenPoint(window.rect.left, window.rect.top) if window.rect else unknown
        return _ScanPosition(
            elsewhere=not (desktop is not None and desktop == current_desktop),
            desktop=desktop if desktop is not None else far,
            monitor_x=monitor.x,
            monitor_y=monitor.y,
            top=corner.y,
            left=corner.x,
        )

    return sorted(windows, key=key)


@dataclass(slots=True, frozen=True, order=True)
class _ScanPosition:
    """Where a window falls in a person's scan; compared field by field."""

    elsewhere: bool
    desktop: int
    monitor_x: int
    monitor_y: int
    top: int
    left: int
