"""Fail-closed native tab menus, with stable-ID postcondition checks.

English labels come from Chromium generated_resources.grd. Native references
belong to the action session; the scripting dictionary establishes tab identity.
"""

from __future__ import annotations

import re
import time
from collections.abc import Callable, Generator, Iterable, Sequence
from contextlib import contextmanager, suppress
from dataclasses import dataclass, field
from typing import Protocol

from desktop_manager.chrome.driver import GroupOutcome, MoveOutcome
from desktop_manager.chrome.drivers.macos_ax import AccessibilityUnavailableError, AXNode
from desktop_manager.chrome.drivers.macos_jxa import ChromeMacError, JxaWindow, ProfileInfo, Snapshot, action_ids
from desktop_manager.chrome.drivers.macos_read import (
    METADATA_UNAVAILABLE,
    TabSlot,
    aligned_slots,
    chrome_tab,
    profile_of,
    tab_slots,
    walk,
    window_node,
)
from desktop_manager.chrome.drivers.macos_ui import NativeUI
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup, parse_group_header, parse_tab_name


class MacDriver(Protocol):
    """What the menu actions need from `ChromeMac`; a protocol, so this module does not import it."""

    def snapshot(self) -> Snapshot: ...
    def profiles(self) -> dict[str, ProfileInfo]: ...
    def list_windows(self) -> list[ChromeWindow]: ...
    def foreground_window(self) -> int: ...
    def activate(self, hwnd: int, timeout_seconds: float) -> bool: ...


_ERRORS = (ChromeMacError, AccessibilityUnavailableError, OSError)
_FLAT = ("Add Tab to New Group", "Add tab to new group")
_GROUP = ("Add Tab to Group", "Add tab to group")
_NEW = ("New Group", "New group")
_MOVE = ("Move Tab to Another Window", "Move tab to another window")
_TITLE_FIELD = ("Tab group title", "Tab-group title")
_UNGROUP_DIALOG = ("Ungroup Tab Group?", "Ungroup tab group?")


def _dialog(node: AXNode) -> bool:
    # Chromium's kDialog maps to AXGroup / AXApplicationDialog on Cocoa.
    return node.role in ("AXDialog", "AXPopover", "AXSheet") or node.subrole in (
        "AXApplicationDialog",
        "AXApplicationAlertDialog",
        "AXDialog",
        "AXSystemDialog",
    )


def _innermost(nodes: Iterable[AXNode]) -> list[AXNode]:
    """Distinct matches that contain no other match: a sheet holds its alert, and both carry the title."""
    found = {node.identity: node for node in nodes}
    return [
        node
        for node in found.values()
        if not any(inner.identity != node.identity and inner.identity in found for inner in walk(node))
    ]


def _poll[T](query: Callable[[], T | None], timeout: float) -> T:
    deadline = time.monotonic() + max(0.0, timeout)
    while True:
        found = query()
        if found is not None:
            return found
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ChromeMacError("Timed out waiting for Chrome UI confirmation")
        time.sleep(min(0.05, remaining))


def _unique[T](items: list[T], detail: str) -> T:
    if len(items) != 1:
        raise ChromeMacError(detail)
    return items[0]


def _window(windows: list[ChromeWindow], hwnd: int) -> ChromeWindow:
    return _unique([window for window in windows if window.hwnd == hwnd], "Window missing or ambiguous")


def _known(window: ChromeWindow) -> None:
    if not window.profile_dir or window.profile_scope:
        raise ChromeMacError("Window profile is unknown or private; refusing action")


def _safe(tab: ChromeTab) -> None:
    if tab.url is None or tab.pinned or tab.audible or METADATA_UNAVAILABLE in tab.alerts:
        raise ChromeMacError("Tab URL or AX safety evidence unavailable, or tab is pinned/audible")


@dataclass(frozen=True, slots=True)
class _Located:
    window: ChromeWindow
    tab: ChromeTab


def _current(windows: list[ChromeWindow], expected: ChromeTab) -> _Located:
    action_ids(expected)
    _safe(expected)
    source = _window(windows, expected.window_hwnd)
    _known(source)
    current = _unique([tab for tab in source.tabs if tab.runtime_id == expected.runtime_id], "Tab missing or ambiguous")
    _safe(current)
    if current.title != expected.title or current.url != expected.url:
        raise ChromeMacError("Tab title or URL changed; refusing action")
    if current.group != expected.group:
        raise ChromeMacError("Tab group changed; refusing action")
    return _Located(source, current)


@dataclass(frozen=True)
class Live:
    windows: list[ChromeWindow]
    window: ChromeWindow
    tab: ChromeTab
    node: AXNode
    window_node: AXNode
    pid: int
    #: The scripting record and native strip the action was verified against.
    raw: JxaWindow
    slots: tuple[TabSlot, ...]


def _live(driver: MacDriver, ui: NativeUI, expected: ChromeTab) -> Live:
    windows = driver.list_windows()
    located = _current(windows, expected)
    window, current = located.window, located.tab
    snapshot = driver.snapshot()
    raw = _unique([raw for raw in snapshot.windows if raw.id == window.hwnd], "Native window missing or ambiguous")
    ax_windows = ui.browser_windows(snapshot.pid)
    if snapshot != driver.snapshot():
        raise ChromeMacError("Chrome changed during native UI validation")
    found = window_node(ax_windows, raw, snapshot.windows)
    if found is None:
        raise ChromeMacError("AX window missing or ambiguous")
    if sum(other.title == window.title for other in windows) != 1:
        raise ChromeMacError("Browser window title is ambiguous")
    profile = profile_of(found, driver.profiles())
    if (profile.directory if profile else None) != window.profile_dir:
        raise ChromeMacError("AX window profile changed or is ambiguous")
    slots = tab_slots(found, window.hwnd)
    if len(slots) != len(raw.tabs) or len(raw.tabs) != len(window.tabs):
        raise ChromeMacError("Native tab strip is incomplete")
    for index, (slot, record, tab) in enumerate(zip(slots, raw.tabs, window.tabs, strict=True)):
        if (
            (record.id,) != tab.runtime_id
            or record.title != tab.title
            or record.url != tab.url
            or parse_tab_name(slot.node.name).title != tab.title
            or slot.node.selected != (index + 1 == raw.active)
        ):
            raise ChromeMacError("Native tab strip no longer matches stable Chrome identities")
    slot = slots[current.index]
    _safe(chrome_tab(raw.tabs[current.index], current.index, raw, slot))
    if slot.group != current.group:
        raise ChromeMacError("Native group membership changed")
    return Live(windows, window, current, slot.node, found, snapshot.pid, raw, tuple(slots))


@dataclass(frozen=True, slots=True)
class _PlacedGroup:
    hwnd: int
    group: TabGroup


def _groups(windows: list[ChromeWindow], profile: str, title: str) -> list[_PlacedGroup]:
    groups: dict[TabGroup, _PlacedGroup] = {}
    for window in windows:
        if window.profile_dir != profile:
            continue
        _known(window)
        if any(METADATA_UNAVAILABLE in tab.alerts for tab in window.tabs):
            raise ChromeMacError("Profile group membership is incomplete")
        for tab in window.tabs:
            if tab.group and tab.group.title == title:
                groups[tab.group] = _PlacedGroup(window.hwnd, tab.group)
    return list(groups.values())


def _target_group(windows: list[ChromeWindow], source: ChromeWindow, title: str, home: int | None) -> TabGroup:
    placed = _unique(_groups(windows, source.profile_dir or "", title), "Target group missing or ambiguous")
    if placed.hwnd != (home if home is not None else source.hwnd):
        raise ChromeMacError("Target group is in a different window")
    target = _window(windows, placed.hwnd)
    _known(target)
    if target.profile_dir != source.profile_dir:
        raise ChromeMacError("Target group belongs to a different profile")
    return placed.group


def _foreground(driver: MacDriver, hwnd: int) -> None:
    if driver.foreground_window() != hwnd:
        raise ChromeMacError("Chrome window lost foreground focus")


def _menus(tree: AXNode) -> list[AXNode]:
    """Top-level menus only; submenu choices are resolved beneath their parent."""
    if tree.role == "AXMenu":
        return [tree]
    return [menu for child in tree.children for menu in _menus(child)] if tree.role != "AXMenuBar" else []


def _entry(ui: NativeUI, pid: int, names: tuple[str, ...], timeout: float) -> AXNode:
    def find() -> AXNode | None:
        menus = _menus(ui.application_tree(pid))
        if not menus:
            return None
        menu = _unique(menus, "More than one native context menu is open")
        entries = [child for child in menu.children if child.role == "AXMenuItem" and child.name in names]
        return _unique(entries, "Menu entry is ambiguous") if entries else None

    return _poll(find, timeout)


def _submenu(ui: NativeUI, pid: int, names: tuple[str, ...], timeout: float) -> AXNode:
    def find() -> AXNode | None:
        tree = ui.application_tree(pid)
        parents = [node for node in walk(tree) if node.role == "AXMenuItem" and node.name in names]
        if not parents:
            return None
        parent = _unique(parents, "Native submenu parent is ambiguous")
        menus = [child for child in parent.children if child.role == "AXMenu"]
        return _unique(menus, "Native submenu is ambiguous") if menus else None

    return _poll(find, timeout)


def _bubble(ui: NativeUI, pid: int, timeout: float) -> AXNode:
    """The open group editor: the one container holding the group-title field.

    The editor is a plain AXGroup, reachable through several paths of the AX
    graph, so repeated references collapse by identity. Page content is never
    walked, so a page's own text field cannot match."""

    def find() -> AXNode | None:
        found: dict[int, AXNode] = {}
        for node in walk(ui.application_tree(pid)):
            if any(child.role == "AXTextField" and child.name in _TITLE_FIELD for child in node.children):
                _ = found.setdefault(node.identity, node)
        return _unique(list(found.values()), "Group editor is ambiguous") if found else None

    return _poll(find, timeout)


@dataclass(frozen=True, slots=True)
class _Content:
    """A tab's identity and what it shows; equal only for the same tab still on the same page."""

    runtime_id: tuple[int, ...]
    title: str
    url: str | None

    @classmethod
    def of(cls, tab: ChromeTab) -> _Content:
        return cls(tab.runtime_id, tab.title, tab.url)


@dataclass(frozen=True, slots=True)
class _Strip:
    """A window's native tab strip as verified against scripting, to compare with later."""

    raw: JxaWindow
    slots: tuple[TabSlot, ...]

    def same_as(self, slots: Sequence[TabSlot]) -> bool:
        return len(slots) == len(self.slots) and all(
            (a.node.name, a.node.selected, a.group) == (b.node.name, b.node.selected, b.group)
            for a, b in zip(slots, self.slots, strict=True)
        )


@dataclass
class Action:
    driver: MacDriver
    ui: NativeUI
    live: Live
    opened: bool = False
    watched: list[_Strip] = field(default_factory=lambda: [])

    def watch(self, hwnd: int) -> None:
        """Also hold another window's strip still until the menu choice is pressed."""
        snapshot = self.driver.snapshot()
        raw = _unique([raw for raw in snapshot.windows if raw.id == hwnd], "Watched window missing or ambiguous")
        found = window_node(self.ui.browser_windows(snapshot.pid), raw, snapshot.windows)
        slots = aligned_slots(found, raw)
        if slots is None:
            raise ChromeMacError("Watched window's native strip does not match scripting")
        self.watched.append(_Strip(raw, tuple(slots)))

    def open(self, node: AXNode) -> None:
        """Open a context menu through accessibility, not the mouse.

        Chrome's tab strip sits in an overlay that hit-testing does not see into,
        so a click point cannot be proven to land on the tab; AXShowMenu needs none."""
        if _menus(self.ui.application_tree(self.live.pid)):
            raise ChromeMacError("A native menu is already open")
        _foreground(self.driver, self.live.window.hwnd)
        self.opened = True
        self.ui.show_menu(node)

    def unchanged(self) -> None:
        """Check, through accessibility alone, that each watched strip is as verified.

        Chrome does not answer scripting while one of its menus is open, so this
        compares the native strips with those verified before the menu opened:
        the same tabs, in order, with the same titles, selection and groups. A URL
        or ID change is invisible here; the arrival check catches it after."""
        windows = self.ui.browser_windows(self.live.pid)
        for strip in (_Strip(self.live.raw, self.live.slots), *self.watched):
            found = window_node(windows, strip.raw, [strip.raw])
            if found is None or not strip.same_as(tab_slots(found, strip.raw.id)):
                raise ChromeMacError("Tab strip changed during the action")

    def press(self, node: AXNode) -> None:
        # An open menu holds Chrome in its modal loop: focus cannot move without
        # closing it, and scripting cannot answer until it closes.
        if not _menus(self.ui.application_tree(self.live.pid)):
            _foreground(self.driver, self.live.window.hwnd)
        elif not self.ui.frontmost(self.live.pid):
            raise ChromeMacError("Chrome lost foreground focus")
        self.ui.press(node)


@contextmanager
def _action(driver: MacDriver, tab: ChromeTab, timeout: float) -> Generator[Action]:
    with NativeUI() as ui:
        cursor = ui.cursor()
        action: Action | None = None
        try:
            live = _live(driver, ui, tab)
            if not driver.activate(tab.window_hwnd, timeout):
                raise ChromeMacError("Chrome window would not come to the front")
            # Activation can change layout and selection; re-query retained UI.
            refreshed = _live(driver, ui, tab)
            if refreshed.pid != live.pid:
                raise ChromeMacError("Chrome process changed during activation")
            live = refreshed
            action = Action(driver, ui, live)
            yield action
        except _ERRORS:
            if action and action.opened:
                with suppress(*_ERRORS):
                    ui.escape(action.live.pid)
            raise
        finally:
            ui.restore_cursor(cursor)


def _arrival(
    driver: MacDriver,
    original: ChromeTab,
    hwnd: int,
    profile: str,
    title: str | None,
    group: TabGroup | None = None,
    members: tuple[ChromeTab, ...] = (),
) -> ChromeTab | None:
    windows = driver.list_windows()
    found = [tab for window in windows for tab in window.tabs if tab.runtime_id == original.runtime_id]
    tab = _unique(found, "Moved tab missing or duplicated") if found else None
    if tab is None or tab.window_hwnd != hwnd:
        return None
    window = _window(windows, hwnd)
    # Chrome is still redrawing the strip right after an action, so a read that
    # could not pair native evidence with scripting is "not yet", not a refusal.
    # The poll's timeout still bounds the wait.
    if window.profile_scope is not None or METADATA_UNAVAILABLE in tab.alerts:
        return None
    _known(window)
    if window.profile_dir != profile or tab.title != original.title or tab.url != original.url:
        raise ChromeMacError("Arriving tab identity, content or profile changed")
    _safe(tab)
    if title is None:
        return tab if tab.group is None else None
    if tab.group is None or tab.group.title != title or (group and tab.group != group):
        return None
    grouped = [member for member in window.tabs if member.group == tab.group]
    if {_Content.of(member) for member in grouped} != {_Content.of(member) for member in (*members, original)}:
        raise ChromeMacError("Resulting group membership differs from the requested tabs")
    return tab


def group_tab(
    driver: MacDriver,
    tab: ChromeTab,
    *,
    title: str,
    new: bool,
    home_hwnd: int | None,
    timeout_seconds: float,
) -> GroupOutcome:
    started = time.perf_counter()
    try:
        if not title or any(ord(char) < 32 for char in title) or re.search(r" – \d+ tabs?\b", title):
            raise ChromeMacError("Group title is empty or cannot be read unambiguously")
        if tab.group is not None:
            raise ChromeMacError("Tab is already grouped")
        home = tab.window_hwnd if home_hwnd is None else home_hwnd
        with _action(driver, tab, timeout_seconds) as action:
            live, ui = action.live, action.ui
            target: TabGroup | None = None
            if new:
                if home != tab.window_hwnd or _groups(live.windows, live.window.profile_dir or "", title):
                    raise ChromeMacError("New group needs its home window and a unique title")
            else:
                target = _target_group(live.windows, live.window, title, home)
            members = tuple(
                member for window in live.windows for member in window.tabs if target and member.group == target
            )
            if home != tab.window_hwnd:
                action.watch(home)
            action.open(live.node)
            entry = _entry(ui, live.pid, (*_FLAT, *_GROUP), timeout_seconds)
            if entry.name in _FLAT:
                if not new:
                    raise ChromeMacError("Target group no longer offered")
                chosen = entry
            else:
                action.press(entry)
                menu = _submenu(ui, live.pid, _GROUP, timeout_seconds)
                names = _NEW if new else (title,)
                chosen = _unique(
                    [node for node in menu.children if node.role == "AXMenuItem" and node.name in names],
                    "Target group menu entry missing or ambiguous",
                )
            # Chrome answers no scripting while its menu is open, so the strips are
            # checked natively; the arrival check confirms membership afterwards.
            action.unchanged()
            action.press(chosen)
            if new:
                bubble = _bubble(ui, live.pid, timeout_seconds)
                field = _unique(
                    [node for node in walk(bubble) if node.role == "AXTextField" and node.name in _TITLE_FIELD],
                    "Group title field is ambiguous",
                )
                _foreground(driver, tab.window_hwnd)
                if not ui.focused(field):
                    raise ChromeMacError("Group title field lost focus")
                # Confirm creating the group affected only the requested stable ID.
                unnamed = _poll(lambda: _arrival(driver, tab, home, live.window.profile_dir or "", ""), timeout_seconds)
                ui.set_text(field, title, lambda: _foreground(driver, tab.window_hwnd))
                _foreground(driver, tab.window_hwnd)
                if not ui.focused(field):
                    raise ChromeMacError("Group title field lost focus")
                ui.key(36)  # Return
                if unnamed.group is None:
                    raise ChromeMacError("New group identity could not be established")
            _poll(
                lambda: _arrival(driver, tab, home, live.window.profile_dir or "", title, target, members),
                timeout_seconds,
            )
        return GroupOutcome(True, "created" if new else "joined", time.perf_counter() - started)
    except _ERRORS as exc:
        return GroupOutcome(False, str(exc), time.perf_counter() - started)


def names_window(name: str, window: ChromeWindow) -> bool:
    """Chromium elides the active title, then appends an exact plural tab count.

    Custom window names have no count and are deliberately refused because the
    scripting dictionary cannot prove a custom name's identity.
    """
    active = window.selected_tab
    if active is None:
        return False
    count = len(window.tabs) - 1
    suffix = "" if count == 0 else f" and {count} other tab" + ("s" if count != 1 else "")
    if suffix and name.endswith(suffix.replace("other tab", "Other Tab")):
        suffix = suffix.replace("other tab", "Other Tab")
    if not name.endswith(suffix):
        return False
    label = name[: -len(suffix)] if suffix else name
    title = active.title.replace("\n", "")
    return label == title or (label.endswith("…") and len(label) > 4 and title.startswith(label[:-1]))


def move_to_window(
    driver: MacDriver,
    tab: ChromeTab,
    target_hwnd: int,
    *,
    timeout_seconds: float,
) -> MoveOutcome:
    started = time.perf_counter()
    try:
        if tab.group is not None or target_hwnd == tab.window_hwnd:
            raise ChromeMacError("Move requires an ungrouped tab and a different window")
        with _action(driver, tab, timeout_seconds) as action:
            live, ui = action.live, action.ui
            target = _window(live.windows, target_hwnd)
            _known(target)
            if target.profile_dir != live.window.profile_dir:
                raise ChromeMacError("Cannot move tabs across profiles")
            action.open(live.node)
            entry = _entry(ui, live.pid, _MOVE, timeout_seconds)
            action.press(entry)
            menu = _submenu(ui, live.pid, _MOVE, timeout_seconds)
            chosen = _unique(
                [node for node in menu.children if node.role == "AXMenuItem" and names_window(node.name, target)],
                "Target window menu entry missing or ambiguous",
            )
            peers = [
                window
                for window in live.windows
                if window.hwnd != tab.window_hwnd
                and window.profile_dir == live.window.profile_dir
                and names_window(chosen.name, window)
            ]
            if len(peers) != 1 or peers[0].hwnd != target_hwnd:
                raise ChromeMacError("Target window label identifies more than one window")
            action.unchanged()
            action.press(chosen)
            moved = _poll(lambda: _arrival(driver, tab, target_hwnd, target.profile_dir or "", None), timeout_seconds)
        return MoveOutcome(GroupOutcome(True, "moved", time.perf_counter() - started), moved)
    except _ERRORS as exc:
        return MoveOutcome(GroupOutcome(False, str(exc), time.perf_counter() - started))


def ungroup(driver: MacDriver, tab: ChromeTab, *, title: str, timeout_seconds: float) -> GroupOutcome:
    started = time.perf_counter()
    try:
        if tab.group is None or tab.group.title != title:
            raise ChromeMacError("Tab is no longer in the requested group")
        with _action(driver, tab, timeout_seconds) as action:
            live, ui = action.live, action.ui
            if _target_group([live.window], live.window, title, tab.window_hwnd) != tab.group:
                raise ChromeMacError("Group identity changed")
            members = tuple(member for member in live.window.tabs if member.group == tab.group)
            for member in members:
                _safe(member)
            # One header can be reached through several paths of the AX graph.
            headers = {
                node.identity: node
                for node in walk(live.window_node)
                if node.role == "AXTabGroup"
                and parse_group_header(node.name) == title
                and node.identity == tab.group.runtime_id[1]
            }
            header = _unique(list(headers.values()), "Native group header missing or ambiguous")
            action.open(header)
            bubble = _bubble(ui, live.pid, timeout_seconds)
            chosen = _unique(
                [node for node in walk(bubble) if node.role == "AXButton" and node.name == "Ungroup"],
                "Group editor Ungroup button missing or ambiguous",
            )
            action.unchanged()
            action.press(chosen)
            confirmed = False

            def finished() -> bool | None:
                nonlocal confirmed
                windows = driver.list_windows()
                window = _window(windows, tab.window_hwnd)
                _known(window)
                if window.profile_dir != live.window.profile_dir:
                    raise ChromeMacError("Group window profile changed")
                remaining: list[ChromeTab] = []
                for member in members:
                    current = _unique(
                        [other for other in window.tabs if other.runtime_id == member.runtime_id],
                        "Ungrouped member missing or ambiguous",
                    )
                    _safe(current)
                    if _Content.of(current) != _Content.of(member):
                        raise ChromeMacError("Group member content changed")
                    if current.group is not None:
                        if current.group != tab.group:
                            raise ChromeMacError("Group member moved to another group during confirmation")
                        remaining.append(current)
                if not remaining:
                    return True
                if len(remaining) != len(members) or {
                    other.runtime_id for other in window.tabs if other.group == tab.group
                } != {member.runtime_id for member in members}:
                    raise ChromeMacError("Group membership changed during confirmation")
                tree = ui.application_tree(live.pid)
                dialogs = _innermost(
                    node
                    for node in walk(tree)
                    if _dialog(node)
                    and (node.name in _UNGROUP_DIALOG or any(child.name in _UNGROUP_DIALOG for child in walk(node)))
                )
                if dialogs and not confirmed:
                    dialog = _unique(dialogs, "Ungroup confirmation dialog is ambiguous")
                    button = _unique(
                        list(
                            {
                                node.identity: node
                                for node in walk(dialog)
                                if node.role == "AXButton" and node.name == "Ungroup"
                            }.values()
                        ),
                        "Ungroup confirmation button is ambiguous",
                    )
                    action.press(button)
                    confirmed = True
                return None

            _poll(finished, timeout_seconds)
        return GroupOutcome(True, "ungrouped", time.perf_counter() - started)
    except _ERRORS as exc:
        return GroupOutcome(False, str(exc), time.perf_counter() - started)
