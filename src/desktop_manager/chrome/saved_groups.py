"""Chrome's saved tab groups: read them off the bookmarks bar and plan away the duplicates.

Chrome saves every tab group. Closing a group (or the window holding it) keeps the
saved copy, and creating a group with the same title later saves another one rather
than reopening the first, so a profile collects several saved groups called "Google"
or "data-platform", only one of them open. Each shows as a chip on the bookmarks bar,
named `<title> group – Opened` or `<title> group – Closed`.

The plan deletes saved copies that hold nothing open: for each title with an open copy,
every closed copy; for a title with only closed copies, all but the leftmost. Open
copies are never touched, and a title with a single copy is left alone.
"""

from __future__ import annotations

import re
from collections.abc import Iterable, Sequence
from dataclasses import dataclass

_CHIP_PATTERN = re.compile(r"^\s*(?P<title>.*?) group – (?P<state>Opened|Closed)\s*$")


@dataclass(frozen=True)
class SavedGroup:
    """One saved-group chip as one window's bookmarks bar shows it, left to right by `position`."""

    profile: str
    title: str
    opened: bool
    position: int


@dataclass(frozen=True)
class SavedGroupDelete:
    """Delete `count` of the `closed_copies` closed saved copies titled `title`; open copies stay."""

    profile: str
    title: str
    count: int
    open_copies: int
    closed_copies: int


@dataclass(slots=True, frozen=True)
class ChipName:
    """What a saved-group chip's accessible name says: the group's title and whether it is open."""

    title: str
    opened: bool


@dataclass(slots=True, frozen=True)
class OpenDuplicate:
    """A title saved more than once with `count` copies open."""

    profile: str
    title: str
    count: int


@dataclass(slots=True, frozen=True)
class _ProfileTitle:
    profile: str
    title: str


@dataclass(frozen=True)
class ConsolidationPlan:
    deletes: tuple[SavedGroupDelete, ...]
    #: Titles with more than one open copy: Chrome must have them open, so they are only reported.
    open_duplicates: tuple[OpenDuplicate, ...]
    groups: tuple[SavedGroup, ...]

    @property
    def total(self) -> int:
        return sum(delete.count for delete in self.deletes)


def parse_chip_name(name: str) -> ChipName | None:
    """`' Google group – Opened'` -> `ChipName('Google', True)`; None for anything else on the bar."""
    match = _CHIP_PATTERN.match(name)
    if match is None:
        return None
    return ChipName(match.group("title"), match.group("state") == "Opened")


def union_of_bars(bars: Iterable[Sequence[SavedGroup]]) -> list[SavedGroup]:
    """One profile's saved groups from the bars of its windows.

    Every window shows the same chips in the same order, as many as fit its width, so
    the window showing the most has the fullest list. The overflow menu that holds the
    rest only opens on a foreground window, so chips hidden in every window go unseen
    until deletes free room for them.
    """
    empty: Sequence[SavedGroup] = ()
    return list(max(bars, key=len, default=empty))


def plan_consolidation(groups: Iterable[SavedGroup]) -> ConsolidationPlan:
    """What to delete so each title keeps one saved copy: the open one, else the leftmost closed one."""
    ordered = sorted(groups, key=lambda group: (group.profile, group.position))
    by_title: dict[_ProfileTitle, list[SavedGroup]] = {}
    for group in ordered:
        by_title.setdefault(_ProfileTitle(group.profile, group.title), []).append(group)

    deletes: list[SavedGroupDelete] = []
    open_duplicates: list[OpenDuplicate] = []
    for key, copies in by_title.items():
        opened = sum(copy.opened for copy in copies)
        closed = len(copies) - opened
        if opened > 1:
            open_duplicates.append(OpenDuplicate(key.profile, key.title, opened))
        surplus = closed if opened else closed - 1
        if surplus > 0:
            deletes.append(SavedGroupDelete(key.profile, key.title, surplus, opened, closed))
    return ConsolidationPlan(deletes=tuple(deletes), open_duplicates=tuple(open_duplicates), groups=tuple(ordered))
