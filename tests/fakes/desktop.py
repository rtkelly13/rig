"""A stateful ``DesktopPort`` for restore to run against: desktops, windows, launches and time.

``FakeDesktop`` implements ``DesktopPort``. It holds the virtual desktops and the
windows on them, and every write changes that state the way Windows would: a launch
starts a process whose window shows up ``startup_seconds`` later, placing a window
puts it on its planned screen, closing a desktop's windows removes them. Time only
moves when restore sleeps or waits, so a test runs instantly and deterministically.

Tests assert on the end state (``windows``, ``desktop_names``, ``current``) and on
``launches``, not on which port methods were called.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import PureWindowsPath

from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    LiveWindow,
    PlacementOutcome,
    PlacementSpec,
    RestoreRunLogger,
    WindowMatch,
    WindowSnapshot,
    WorkspacePlan,
)
from desktop_manager.restore_fallbacks import RestoreFailureFallbacks
from desktop_manager.window_reuse import WindowMove


@dataclass
class FakeWindow:
    hwnd: int
    title: str
    process_name: str
    desktop: int
    placement: PlacementSpec | None = None
    pid: int | None = None
    visible_at: float = 0.0

    @property
    def screen(self) -> int | None:
        return self.placement.screen if self.placement is not None else None

    def live(self) -> LiveWindow:
        return LiveWindow(
            hwnd=self.hwnd,
            title=self.title,
            pid=self.pid,
            process_name=self.process_name,
            desktop_number=self.desktop,
            screen_number=self.screen,
            inferred_placement=self.placement,
        )

    def snapshot(self) -> WindowSnapshot:
        return WindowSnapshot(
            title=self.title,
            pid=self.pid,
            process_name=self.process_name,
            screen_number=self.screen,
            inferred_placement=self.placement,
        )


def _process_of(entry: LaunchEntry) -> str:
    if entry.window_process_name:
        return entry.window_process_name
    return PureWindowsPath(entry.path or entry.name).name


def _same_process(window: FakeWindow, process_name: str) -> bool:
    return window.process_name.casefold() == process_name.casefold()


@dataclass
class FakeDesktop:
    """The machine restore sees. Configure it, run restore, then read its state."""

    desktop_names: list[str] = field(default_factory=lambda: ["Desktop 1"])
    current: int = 1
    windows: list[FakeWindow] = field(default_factory=list[FakeWindow])
    #: Handles of the window driving the restore (the invoking terminal).
    invoking_hwnds: set[int] = field(default_factory=set[int])
    #: Seconds between a launch and its window appearing.
    startup_seconds: float = 0.0
    #: Entry name -> how many of its next launches start nothing at all.
    windowless_launches: dict[str, int] = field(default_factory=dict[str, int])
    #: Entry names whose launch starts a process that runs but never shows a window.
    hung_launches: set[str] = field(default_factory=set[str])
    #: Handles that vanish when restore tries to move them.
    vanishing: set[int] = field(default_factory=set[int])
    now: float = 0.0
    launches: list[str] = field(default_factory=list[str])
    _next_hwnd: int = 1000

    # ----------------------------------------------------------------- setup

    def add_window(
        self,
        title: str,
        process_name: str,
        *,
        desktop: int = 1,
        placement: PlacementSpec | None = None,
        hwnd: int | None = None,
    ) -> FakeWindow:
        window = FakeWindow(hwnd or self._new_hwnd(), title, process_name, desktop, placement)
        self.windows.append(window)
        return window

    def window(self, hwnd: int) -> FakeWindow | None:
        return next((window for window in self.windows if window.hwnd == hwnd), None)

    def _new_hwnd(self) -> int:
        self._next_hwnd += 1
        return self._next_hwnd

    def _visible(self) -> list[FakeWindow]:
        return [window for window in self.windows if window.visible_at <= self.now]

    def _wait_for(self, find: Callable[[], FakeWindow | None], timeout_seconds: float) -> FakeWindow | None:
        found = find()
        if found is None:
            self.now += max(timeout_seconds, self.startup_seconds)
            found = find()
        return found

    def _newest_window_for(self, entry: LaunchEntry, excluded: set[int] | None) -> FakeWindow | None:
        process_name = _process_of(entry)
        candidates = [
            window
            for window in self._visible()
            if _same_process(window, process_name) and window.hwnd not in (excluded or set())
        ]
        return candidates[-1] if candidates else None

    # ----------------------------------------------------------------- reads

    def current_workspace_status(self, desktop_count: int | None = None) -> WorkspacePlan:
        count = desktop_count or len(self.desktop_names)
        return self.current_workspace_status_for_desktop_indexes(list(range(1, count + 1)))

    def current_workspace_status_for_desktop_indexes(self, desktop_indexes: list[int]) -> WorkspacePlan:
        return WorkspacePlan(desktops=[self._desktop_status(index) for index in desktop_indexes])

    def current_desktop_status(self, *, desktop_name: str | None = None) -> DesktopPlan:
        status = self._desktop_status(self.current)
        return DesktopPlan(name=desktop_name or status.name, captured_windows=status.captured_windows)

    def _desktop_status(self, index: int) -> DesktopPlan:
        name = self.desktop_names[index - 1] if index <= len(self.desktop_names) else f"Desktop {index}"
        return DesktopPlan(
            name=name,
            captured_windows=[window.snapshot() for window in self._visible() if window.desktop == index],
        )

    def current_window_handles_to_preserve(self) -> set[int]:
        return set(self.invoking_hwnds)

    def get_current_desktop_number(self) -> int | None:
        return self.current

    def inventory_reusable_windows(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        return [window.live() for window in self._visible() if window.hwnd not in excluded_hwnds]

    def collect_windows_matching_process(self, process_name: str) -> set[int]:
        return {window.hwnd for window in self._visible() if _same_process(window, process_name)}

    def window_titles_for_process(self, process_name: str) -> dict[int, str]:
        return {window.hwnd: window.title for window in self._visible() if _same_process(window, process_name)}

    def is_process_running(self, process_name: str) -> bool:
        # A launched process runs before its window shows, so count windows still starting.
        return any(_same_process(window, process_name) for window in self.windows)

    def is_window_target_confirmed(self, hwnd: int, desktop_index: int, entry: LaunchEntry) -> bool:
        window = self.window(hwnd)
        return window is not None and window.desktop == desktop_index and window.placement == entry.placement

    def is_entry_target_confirmed(self, entry: LaunchEntry, desktop_index: int, *, timeout_seconds: float) -> bool:
        return any(
            self.is_window_target_confirmed(window.hwnd, desktop_index, entry)
            for window in self._visible()
            if _same_process(window, _process_of(entry))
        )

    def wait_for_window_for_entry(
        self,
        entry: LaunchEntry,
        *,
        timeout_seconds: float,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        allow_process_fallback_for_terminals: bool = False,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> WindowMatch:
        window = self._wait_for(lambda: self._newest_window_for(entry, excluded_hwnds), timeout_seconds)
        if window is None:
            return WindowMatch(None)
        moved = window.desktop != target_desktop_number
        window.desktop = target_desktop_number
        return WindowMatch(window.hwnd, moved_to_target_desktop=moved)

    def wait_for_window_title_change(
        self,
        hwnd: int,
        title: str,
        *,
        timeout_seconds: float,
        diagnostic_callback: Callable[[str], None] | None = None,
    ) -> str | None:
        window = self.window(hwnd)
        return window.title if window is not None else None

    # ------------------------------------------------------------------ time

    def clock(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds

    # ---------------------------------------------------------------- writes

    def ensure_windows(self) -> None:
        return None

    def settle_app_updates(self, plan: WorkspacePlan, *, logger: RestoreRunLogger | None) -> bool:
        return False

    def start_terminal_command_upgrades(self, plan: WorkspacePlan) -> None:
        return None

    def ensure_desktop_count(self, count: int, settle_seconds: float) -> None:
        while len(self.desktop_names) < count:
            self.desktop_names.append(f"Desktop {len(self.desktop_names) + 1}")

    def ensure_current_desktop_number(self, desktop_index: int, *, settle_seconds: float) -> bool:
        if not 1 <= desktop_index <= len(self.desktop_names):
            return False
        self.current = desktop_index
        return True

    def rename_desktop(self, desktop_index: int, name: str) -> bool:
        self.desktop_names[desktop_index - 1] = name
        return True

    def retire_switched_off_desktops(self, keep_count: int, retired_names: Sequence[str]) -> list[str]:
        wanted = {name.casefold() for name in retired_names}
        removed: list[str] = []
        for index in range(len(self.desktop_names), 0, -1):
            name = self.desktop_names[index - 1]
            if name.casefold() not in wanted or len(self.desktop_names) == 1:
                continue
            del self.desktop_names[index - 1]
            removed.insert(0, name)
            fallback = min(keep_count, len(self.desktop_names))
            for window in self.windows:
                if window.desktop == index:
                    window.desktop = fallback
                elif window.desktop > index:
                    window.desktop -= 1
            if self.current == index:
                self.current = fallback
            elif self.current > index:
                self.current -= 1
        return removed

    def move_right(self, steps: int) -> None:
        self.current = min(self.current + steps, len(self.desktop_names))

    def switch_leftmost(self, steps: int) -> None:
        self.current = max(self.current - steps, 1)

    def close_current_desktop_windows(self, *, preserved_hwnds: set[int]) -> int:
        closing = [
            window
            for window in self._visible()
            if window.desktop == self.current and window.hwnd not in preserved_hwnds
        ]
        self.windows = [window for window in self.windows if window not in closing]
        return len(closing)

    def move_existing_window_to_entry(self, window: LiveWindow, desktop_index: int, entry: LaunchEntry) -> WindowMove:
        target = self.window(window.hwnd)
        if target is None or window.hwnd in self.vanishing:
            self.windows = [item for item in self.windows if item.hwnd != window.hwnd]
            raise RuntimeError(f"window {window.hwnd} vanished before it could be moved")
        moved_to_desktop = target.desktop != desktop_index
        moved_to_placement = entry.placement is not None and target.placement != entry.placement
        target.desktop = desktop_index
        if entry.placement is not None:
            target.placement = entry.placement
        return WindowMove(moved_to_desktop, moved_to_placement)

    def launch_entry(
        self, entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None
    ) -> int | None:
        self.launches.append(entry.name)
        pid = 5000 + len(self.launches)
        if self.windowless_launches.get(entry.name, 0) > 0:
            self.windowless_launches[entry.name] -= 1
            return pid
        # A hung process is running, so restore can see it, but its window never shows.
        visible_at = float("inf") if entry.name in self.hung_launches else self.now + self.startup_seconds
        self.windows.append(
            FakeWindow(
                self._new_hwnd(),
                entry.window_title or entry.name,
                _process_of(entry),
                self.current,
                pid=pid,
                visible_at=visible_at,
            )
        )
        return pid

    def apply_window_placement(
        self,
        entry: LaunchEntry,
        *,
        hwnd: int | None = None,
        target_desktop_number: int,
        excluded_hwnds: set[int] | None = None,
        diagnostic_callback: Callable[[str], None] | None = None,
        timeout_seconds: float = 10.0,
    ) -> PlacementOutcome:
        window = self.window(hwnd) if hwnd is not None else None
        if window is None:
            window = self._wait_for(lambda: self._newest_window_for(entry, excluded_hwnds), timeout_seconds)
        if window is None:
            return PlacementOutcome(placed=False)
        moved = window.desktop != target_desktop_number
        window.desktop = target_desktop_number
        window.placement = entry.placement
        return PlacementOutcome(placed=True, moved_to_target_desktop=moved)

    def raise_windows_for_processes(self, process_names: set[str]) -> int:
        wanted = {name.casefold() for name in process_names}
        return sum(1 for window in self._visible() if window.process_name.casefold() in wanted)

    def open_missing_path_fallbacks(
        self,
        fallback_state: RestoreFailureFallbacks,
        *,
        logger: RestoreRunLogger | None,
    ) -> list[str]:
        return []

    def apply_second_pass_placements(
        self,
        plan: WorkspacePlan,
        *,
        logger: RestoreRunLogger | None,
        entries_by_desktop: dict[int, list[LaunchEntry]],
    ) -> list[str]:
        actions: list[str] = []
        for desktop_index, entries in entries_by_desktop.items():
            actions.append(f"Desktop {desktop_index}: second-pass placement")
            for entry in entries:
                if entry.placement is None or entry.type == "terminal":
                    continue
                window = self._newest_window_for(entry, None)
                if window is not None:
                    window.desktop = desktop_index
                    window.placement = entry.placement
        return actions
