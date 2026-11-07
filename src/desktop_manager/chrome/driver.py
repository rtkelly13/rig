"""What the chrome-tabs commands need from a platform's Chrome driver.

The planners are pure and the run loops (`pr_prune.apply_plans`, `group_run`,
`saved_consolidation`) are shared. Each platform supplies one `ChromeDriver`
that reads windows, carries out single, verified steps, and carries its
platform's `Desktops`. `backend.get_driver` picks it. This module stays a leaf:
no platform imports.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from contextlib import AbstractContextManager
from dataclasses import dataclass
from typing import Protocol

from desktop_manager.chrome.desktops import Desktops
from desktop_manager.chrome.saved_groups import SavedGroup
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow


class ChromeReadError(RuntimeError):
    """Chrome's windows could not be read reliably: its UI kept changing under the read.

    A driver raises this instead of returning a partial or guessed read, because a
    tab whose pinned state or group went unread could be closed or moved by mistake.
    A run that gets it acts on nothing and leaves Chrome for the next run.
    """


@dataclass(frozen=True)
class CloseOutcome:
    closed: bool
    detail: str
    seconds: float


@dataclass(frozen=True)
class GroupOutcome:
    grouped: bool
    detail: str
    seconds: float


@dataclass(frozen=True)
class SavedDeleteOutcome:
    deleted: bool
    detail: str
    #: The saved group's tab titles, as its menu listed them before the delete.
    tabs: tuple[str, ...]
    seconds: float


@dataclass(frozen=True, slots=True)
class MoveOutcome:
    outcome: GroupOutcome
    #: The tab as it now sits in the target window, with its new identity; None when it did not move.
    tab: ChromeTab | None = None


class ChromeDriver(Protocol):
    """One platform's way of reading and acting on Chrome's tab strip.

    Every action re-finds its target and checks it before acting, and reports
    what happened rather than raising on a refusal.
    """

    @property
    def desktops(self) -> Desktops:
        """The virtual desktops this platform's windows live on."""
        ...

    def list_windows(self) -> list[ChromeWindow]:
        """Every Chrome browser window with its tabs, oldest first."""
        ...

    def foreground_window(self) -> int:
        """The window the user is in, or 0 when that is not a Chrome window this driver can name."""
        ...

    def close_tab(self, tab: ChromeTab, *, timeout_seconds: float = 3.0) -> CloseOutcome: ...

    def restore_selection(
        self, before: Iterable[ChromeWindow], *, fallbacks: Mapping[int, ChromeTab] | None = None
    ) -> dict[int, ChromeTab | None]: ...

    def foreground(self, hwnd: int, *, timeout_seconds: float = 2.0) -> AbstractContextManager[bool]: ...

    def group_tab(
        self,
        tab: ChromeTab,
        *,
        title: str,
        new: bool,
        home_hwnd: int | None = None,
        timeout_seconds: float = 3.0,
    ) -> GroupOutcome: ...

    def ungroup(self, tab: ChromeTab, *, title: str, timeout_seconds: float = 3.0) -> GroupOutcome: ...

    def move_to_window(self, tab: ChromeTab, target_hwnd: int, *, timeout_seconds: float = 3.0) -> MoveOutcome: ...

    def saved_groups(self, hwnd: int, profile: str) -> list[SavedGroup]: ...

    def delete_saved_group(
        self, hwnd: int, profile: str, title: str, *, timeout_seconds: float = 3.0
    ) -> SavedDeleteOutcome: ...
