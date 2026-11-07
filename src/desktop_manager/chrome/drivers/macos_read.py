"""Pure matching of native AX evidence to the scripting snapshot.

Scripting gives stable IDs, titles and URLs; AX gives what only the browser UI
shows: pinned, audible, memory, group membership and the profile avatar. The two
are paired only when the pairing is one-to-one and the tab strips agree title
for title. Anything uncertain leaves a tab "pinned and audible" with an
explicit alert, so no planner acts on it.
"""

from __future__ import annotations

import re
from collections.abc import Iterable, Iterator, Sequence
from dataclasses import dataclass

from desktop_manager.chrome.drivers.macos_ax import SKIPPED_ROLES, AXNode
from desktop_manager.chrome.drivers.macos_jxa import JxaTab, JxaWindow, ProfileInfo
from desktop_manager.chrome.tabs import ChromeTab, TabGroup, parse_group_header, parse_tab_name

#: The alert a tab carries when native metadata could not be tied to it.
METADATA_UNAVAILABLE = "Accessibility metadata unavailable"
_CLOSE_NAMES = ("Close", "Close tab")
#: The app menu that follows the profile avatar in Chrome's toolbar.
_APP_MENU_NAMES = ("Customize and control Google Chrome", "Chrome")
_GROUP_COUNT = re.compile(r" – (\d+) [Tt]abs?\b")
_GROUP_SUFFIX = re.compile(r" [-–] Part of (?:unnamed group|group )")


@dataclass(frozen=True, slots=True)
class TabSlot:
    """One tab in the native strip, with the group whose header precedes it."""

    node: AXNode
    group: TabGroup | None


@dataclass(frozen=True, slots=True)
class ProfileMatch:
    directory: str
    name: str


def walk(node: AXNode) -> Iterator[AXNode]:
    yield node
    if node.role not in SKIPPED_ROLES:
        for child in node.children:
            yield from walk(child)


def _shows_close(node: AXNode) -> bool:
    return any(child.role == "AXButton" and child.name in _CLOSE_NAMES for child in walk(node))


def profile_of(node: AXNode | None, profiles: dict[str, ProfileInfo]) -> ProfileMatch | None:
    """The one profile this window's avatar names, or None when it names none or several."""
    if node is None:
        return None
    # Chrome's normal toolbar places the avatar immediately before its app menu.
    # Some macOS builds omit AXIdentifier, so use this UI structure as evidence.
    toolbar_avatars: set[int] = set()
    for toolbar in walk(node):
        if toolbar.role != "AXToolbar":
            continue
        for parent in walk(toolbar):
            for previous, current in zip(parent.children, parent.children[1:], strict=False):
                if current.name in _APP_MENU_NAMES:
                    toolbar_avatars.add(id(previous))
    matches: set[ProfileMatch] = set()
    for item in walk(node):
        if item.role not in ("AXButton", "AXPopUpButton"):
            continue
        labels = (item.title, item.description, item.help)
        identifier = item.identifier.lower()
        # Restrict bare-name matching to a positively identified avatar control.
        avatar = "avatar" in identifier or "profile" in identifier or id(item) in toolbar_avatars
        for directory, info in profiles.items():
            if not info.name:
                continue
            names = info.aliases()
            explicit = {f"Profile: {alias}" for alias in names} | {f"Profile {alias}" for alias in names}
            # A signed-in avatar reads "<account name> (<profile name>)".
            badged = names | {f"{alias} ({info.name})" for alias in names}
            if any(label in explicit or (avatar and label in badged) for label in labels):
                matches.add(ProfileMatch(directory, info.name))
    return next(iter(matches)) if len(matches) == 1 else None


def tab_slots(node: AXNode, window_id: int) -> list[TabSlot]:
    """Read macOS tab slots: each group header precedes its sibling tabs.

    Chromium exposes both TabContainerImpl and TabGroupHeader as AXTabGroup.
    Only an expanded header with a complete consecutive run establishes group
    membership. An uncertain run invalidates the window's AX tab evidence.
    """
    result: list[TabSlot] = []

    def is_tab(item: AXNode) -> bool:
        return item.role == "AXRadioButton" and (item.subrole == "AXTabButton" or item.description == "tab")

    def visit(item: AXNode) -> bool:
        if item.role in SKIPPED_ROLES:
            return True
        if is_tab(item):
            result.append(TabSlot(item, None))
            return True
        index = 0
        while index < len(item.children):
            child = item.children[index]
            title = parse_group_header(child.name)
            if title is None:
                if not visit(child):
                    return False
                index += 1
                continue
            # Titles and tab previews may themselves contain a count phrase.
            # Without an unambiguous delimiter, none of those counts is safe.
            counts = list(_GROUP_COUNT.finditer(child.name))
            expanded = child.expanded is True or child.name.endswith(" – Expanded")
            if (
                item.role != "AXTabGroup"
                or child.role != "AXTabGroup"
                or child.expanded is False
                or child.name.endswith(" – Collapsed")
                or not expanded
                or len(counts) != 1
            ):
                return False
            count = int(counts[0].group(1))
            members = item.children[index + 1 : index + 1 + count]
            if count == 0 or len(members) != count or not all(is_tab(member) for member in members):
                return False
            group = TabGroup((window_id, child.identity, -1), title)
            result.extend(TabSlot(member, group) for member in members)
            index += count + 1
        return True

    if not visit(node):
        return []
    # Native AX is a graph: one tab can occur through several parent paths.
    # Collapse only identical nonzero identities; conflicting evidence fails closed.
    unique: list[TabSlot] = []
    seen: dict[int, TabSlot] = {}
    for slot in result:
        identity = slot.node.identity
        if identity and identity in seen:
            if seen[identity] != slot:
                return []
            continue
        unique.append(slot)
        if identity:
            seen[identity] = slot
    return unique


def pinned_labels_consistent(nodes: Sequence[AXNode]) -> bool:
    """Pinned tabs lead the strip and never show a close control.

    A label breaking either rule means the names cannot be trusted for pinning.
    """
    pinned = [parse_tab_name(node.name).pinned for node in nodes]
    if pinned != sorted(pinned, reverse=True):
        return False
    return not any(flag and _shows_close(node) for flag, node in zip(pinned, nodes, strict=True))


def matches_window_title(node: AXNode, raw: JxaWindow) -> bool:
    """Scripting abbreviates window titles; AX uses the full active page title."""
    if node.title == raw.title:
        return True
    active = raw.active_tab
    if active is None:
        return False
    full = active.title + " - Google Chrome"
    return node.title == full or node.title.startswith(full + " – ")


def _agrees(slots: Sequence[TabSlot], raw: JxaWindow) -> bool:
    """The native slots repeat the scripting window's titles and selection, in order."""
    return len(slots) == len(raw.tabs) and all(
        parse_tab_name(slot.node.name).title == tab.title and slot.node.selected == (index + 1 == raw.active)
        for index, (slot, tab) in enumerate(zip(slots, raw.tabs, strict=True))
    )


def strip_matches(node: AXNode, raw: JxaWindow) -> bool:
    """The AX tab strip repeats the scripting window's titles and selection, in order."""
    return _agrees(tab_slots(node, raw.id), raw)


def window_node(ax_windows: Iterable[AXNode], raw: JxaWindow, records: Sequence[JxaWindow]) -> AXNode | None:
    """The one AX window for a scripting window, by title, else by tab strip.

    Chrome leaves the AX window title empty on some pages (a New Tab page),
    so the strip is the fallback. Either way the pairing must be one-to-one.
    """
    candidates = list(ax_windows)
    for matches in (matches_window_title, strip_matches):
        nodes = [node for node in candidates if matches(node, raw)]
        if nodes:
            unique = len(nodes) == 1 and sum(matches(nodes[0], other) for other in records) == 1
            return nodes[0] if unique else None
    return None


def aligned_slots(node: AXNode | None, raw: JxaWindow) -> list[TabSlot] | None:
    """Each scripting tab's native slot, in order, or None when the strips disagree."""
    if node is None:
        return None
    slots = tab_slots(node, raw.id)
    if not _agrees(slots, raw) or not pinned_labels_consistent([slot.node for slot in slots]):
        return None
    return slots


def chrome_tab(raw: JxaTab, index: int, window: JxaWindow, slot: TabSlot | None) -> ChromeTab:
    """A scripting tab with its native evidence; without evidence, pinned and audible so nothing acts on it."""
    node = slot.node if slot else None
    group = slot.group if slot else None
    parsed = parse_tab_name(node.name) if node else None
    # Chrome labels pinned tabs "– Pinned". Inactive tabs hide their close
    # control, so its absence is normal; `pinned_labels_consistent` rejects
    # labels that contradict the pinned-first strip order.
    group_known = node is not None and (group is not None or _GROUP_SUFFIX.search(node.name) is None)
    known = parsed is not None and parsed.title == raw.title and group_known
    return ChromeTab(
        runtime_id=(raw.id,),
        window_hwnd=window.id,
        index=index,
        name=node.name if node else raw.title,
        title=raw.title,
        selected=index + 1 == window.active,
        pinned=not known or (parsed is not None and parsed.pinned),
        loaded=parsed.loaded if parsed else False,
        audible=parsed.audible if known and parsed else True,
        alerts=parsed.alerts if known and parsed else (METADATA_UNAVAILABLE,),
        memory_mb=parsed.memory_mb if parsed else None,
        group=group,
        url=raw.url,
    )
