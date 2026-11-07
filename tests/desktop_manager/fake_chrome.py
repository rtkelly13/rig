"""A `ChromeDriver` that keeps its windows in memory, for driving the chrome-tabs flows in tests.

Hand it to a command with ``obj=ChromeSession(lambda: fake)``, or to a run loop
directly. It records what it was asked to do and reports success unless told
otherwise. Override a step on the instance (``monkeypatch.setattr(fake, ...)``)
to make it refuse, raise or have a side effect.
"""

from __future__ import annotations

from collections.abc import Iterable, Iterator, Mapping
from contextlib import contextmanager
from dataclasses import dataclass, field, replace

from desktop_manager.chrome.driver import CloseOutcome, GroupOutcome, MoveOutcome, SavedDeleteOutcome
from desktop_manager.chrome.saved_groups import SavedGroup
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow

CLOSED = CloseOutcome(True, "closed", 0.1)
GROUPED = GroupOutcome(True, "ok", 0.0)


@dataclass(eq=False)
class FakeDesktops:
    """Numbered desktops, every window on desktop 1 unless `homes` says otherwise."""

    number: int | None = 1
    homes: dict[int, int] = field(default_factory=lambda: {})
    went: list[int] = field(default_factory=lambda: [])

    def current(self) -> int | None:
        return self.number

    def of_window(self, hwnd: int) -> int | None:
        return self.homes.get(hwnd, 1) if self.number is not None else None

    def go_to(self, number: int) -> None:
        self.went.append(number)
        self.number = number

    def move_window(self, hwnd: int, number: int) -> None:
        self.homes[hwnd] = number

    def window_exists(self, hwnd: int) -> bool:
        del hwnd
        return True


@dataclass(eq=False)
class FakeChrome:
    windows: list[ChromeWindow] = field(default_factory=lambda: [])
    foreground_hwnd: int = 0
    desktops: FakeDesktops = field(default_factory=FakeDesktops)
    close_outcome: CloseOutcome = CLOSED
    #: What `group_tab`, `ungroup` and `move_to_window` report.
    group_outcome: GroupOutcome = GROUPED
    saved: list[SavedGroup] = field(default_factory=lambda: [])
    #: What each `delete_saved_group` reports, in turn.
    deletes: list[SavedDeleteOutcome] = field(default_factory=lambda: [])
    #: Every step in order: ("close" | "restore" | "group", window, tab index or -1).
    log: list[tuple[str, int, int]] = field(default_factory=lambda: [])
    closed: list[ChromeTab] = field(default_factory=lambda: [])
    restored: list[tuple[list[int], dict[int, ChromeTab] | None]] = field(default_factory=lambda: [])

    def list_windows(self) -> list[ChromeWindow]:
        return list(self.windows)

    def foreground_window(self) -> int:
        return self.foreground_hwnd

    @contextmanager
    def foreground(self, hwnd: int, *, timeout_seconds: float = 2.0) -> Iterator[bool]:
        del hwnd, timeout_seconds
        yield True

    def close_tab(self, tab: ChromeTab, *, timeout_seconds: float = 3.0) -> CloseOutcome:
        del timeout_seconds
        self.closed.append(tab)
        self.log.append(("close", tab.window_hwnd, tab.index))
        return self.close_outcome

    def restore_selection(
        self, before: Iterable[ChromeWindow], *, fallbacks: Mapping[int, ChromeTab] | None = None
    ) -> dict[int, ChromeTab | None]:
        hwnds = [window.hwnd for window in before]
        self.restored.append((hwnds, None if fallbacks is None else dict(fallbacks)))
        self.log.extend(("restore", hwnd, -1) for hwnd in hwnds)
        return {}

    def group_tab(
        self,
        tab: ChromeTab,
        *,
        title: str,
        new: bool,
        home_hwnd: int | None = None,
        timeout_seconds: float = 3.0,
    ) -> GroupOutcome:
        del title, new, home_hwnd, timeout_seconds
        self.log.append(("group", tab.window_hwnd, tab.index))
        return self.group_outcome

    def ungroup(self, tab: ChromeTab, *, title: str, timeout_seconds: float = 3.0) -> GroupOutcome:
        del tab, title, timeout_seconds
        return self.group_outcome

    def move_to_window(self, tab: ChromeTab, target_hwnd: int, *, timeout_seconds: float = 3.0) -> MoveOutcome:
        del timeout_seconds
        moved = replace(tab, window_hwnd=target_hwnd) if self.group_outcome.grouped else None
        return MoveOutcome(self.group_outcome, moved)

    def saved_groups(self, hwnd: int, profile: str) -> list[SavedGroup]:
        del hwnd, profile
        return list(self.saved)

    def delete_saved_group(
        self, hwnd: int, profile: str, title: str, *, timeout_seconds: float = 3.0
    ) -> SavedDeleteOutcome:
        del hwnd, profile, timeout_seconds
        outcome = self.deletes.pop(0)
        if outcome.deleted:
            self.saved = [group for group in self.saved if group.title != title or group.opened]
        return outcome
