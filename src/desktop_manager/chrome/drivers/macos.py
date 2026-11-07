"""Drive Chrome on macOS: scripting for identity and closing, accessibility for metadata and menus.

Chrome's scripting dictionary supplies stable window and tab IDs, titles and
URLs, and can select and close a tab without focus. Native accessibility (AX)
supplies what only the browser UI shows. Missing or ambiguous AX evidence makes
the affected tabs pinned and audible, so no planner acts on them. Construction
performs no OS calls.

Lessons from the live browser, each of which once left every tab protected:

- **Ask for the application role first.** Chromium builds its native AX tree on
  demand when an assistive client reads `AXRole`; `AXWindows` alone may not.
- **Chrome off the current Space has no `AXWindows`.** The main and focused
  windows still resolve, so they are the fallback.
- **The AX tree is a graph.** One tab can be reached through several parents;
  identical references collapse, conflicting ones fail closed.
- **Scripting elides window titles; AX does not, and is empty on a New Tab
  page.** Windows pair by full active-tab title, else by an identical strip.
- **Inactive tabs hide their close button.** A missing close button is not
  evidence of pinning; Chrome's "– Pinned" label is.
- **Profiles come from `Google/Chrome/Local State`,** matched against a
  signed-in avatar labelled "<account> (<profile>)".
"""

from __future__ import annotations

import time
from collections.abc import Generator, Iterable, Mapping
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.chrome.desktops import Desktops, NoDesktops
from desktop_manager.chrome.driver import CloseOutcome, GroupOutcome, MoveOutcome, SavedDeleteOutcome
from desktop_manager.chrome.drivers import macos_ax, macos_jxa
from desktop_manager.chrome.drivers.macos_ax import AXNode
from desktop_manager.chrome.drivers.macos_jxa import (
    SELECT_ACTION,
    ChromeMacError,
    JxaWindow,
    ProfileInfo,
    Snapshot,
    action_ids,
    activate_script,
    close_action,
    restore_script,
    status,
    tab_action,
)
from desktop_manager.chrome.drivers.macos_read import (
    METADATA_UNAVAILABLE,
    aligned_slots,
    chrome_tab,
    profile_of,
    window_node,
)
from desktop_manager.chrome.drivers.macos_ui import NativeUI
from desktop_manager.chrome.saved_groups import SavedGroup
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, WindowRect

_POLL_SECONDS = 0.05
_READ_ATTEMPTS = 4


@dataclass(frozen=True, slots=True)
class _Read:
    snapshot: Snapshot
    ax_windows: tuple[AXNode, ...]


def _rect(raw: JxaWindow) -> WindowRect | None:
    bounds = raw.bounds
    if bounds is None:
        return None
    return WindowRect(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height)


class ChromeMac:
    """Chrome's windows and tabs on macOS. Construct once per command."""

    @property
    def desktops(self) -> Desktops:
        return NoDesktops()

    def snapshot(self) -> Snapshot:
        return macos_jxa.snapshot()

    def profiles(self) -> dict[str, ProfileInfo]:
        return macos_jxa.profiles(Path.home())

    def activate(self, hwnd: int, timeout_seconds: float) -> bool:
        """Bring a window to the front and wait until it is Chrome's front window."""
        if status(macos_jxa.run_jxa(activate_script(hwnd))) != "invoked":
            return False
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        while self.foreground_window() != hwnd:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return False
            time.sleep(min(_POLL_SECONDS, remaining))
        return True

    @contextmanager
    def foreground(self, hwnd: int, *, timeout_seconds: float = 2.0) -> Generator[bool]:
        """Bring a window to the front for the duration; then restore the previous focus,
        Chrome's front window, the window's minimised state and the cursor."""
        with NativeUI() as ui:
            before = self.snapshot()
            targets = [window for window in before.windows if window.id == hwnd]
            front = before.front_window()
            if len(targets) != 1 or front is None:
                yield False
                return
            cursor, focus = ui.cursor(), ui.capture_focus(macos_jxa.frontmost_pid)
            try:
                yield self.activate(hwnd, timeout_seconds)
            finally:
                try:
                    macos_jxa.run_jxa(restore_script(hwnd, front.id, targets[0].minimized))
                finally:
                    try:
                        ui.restore_focus(focus)
                    finally:
                        ui.restore_cursor(cursor)

    def foreground_window(self) -> int:
        """Chrome's front window when Chrome is the active app, else 0; failures propagate."""
        current = self.snapshot()
        if not current.frontmost:
            return 0
        front = current.front_window()
        if front is None:
            raise ChromeMacError("Cannot identify Chrome's foreground window")
        return front.id

    def _bracketed_read(self) -> _Read:
        """A snapshot and the AX windows read while it held.

        A second snapshot brackets the AX read: stable IDs and ordering let identical
        titles map positionally without confusing two tabs. A live page (a PR, a
        dashboard) can retitle mid-read, so an unsettled read is retried a few times
        before the AX evidence is dropped and every tab is left protected."""
        current = self.snapshot()
        for _ in range(_READ_ATTEMPTS):
            if not current.windows:
                return _Read(current, ())
            try:
                ax_windows = macos_ax.read_windows(current.pid)
            except (OSError, macos_ax.AccessibilityUnavailableError):
                return _Read(current, ())
            after = self.snapshot()
            if after == current:
                return _Read(current, ax_windows)
            current = after
            time.sleep(_POLL_SECONDS)
        return _Read(current, ())

    def list_windows(self) -> list[ChromeWindow]:
        read = self._bracketed_read()
        current, ax_windows = read.snapshot, read.ax_windows
        if not current.windows:
            return []
        profiles = self.profiles()
        windows: list[ChromeWindow] = []
        seen_windows: set[int] = set()
        seen_tabs: set[int] = set()
        for raw in current.windows:
            if raw.id in seen_windows:
                raise ChromeMacError("Duplicate Chrome window ID")
            seen_windows.add(raw.id)
            if raw.active > len(raw.tabs):
                raise ChromeMacError("Invalid Chrome window state")
            # Duplicate titles or strips cannot identify AX windows safely.
            node = window_node(ax_windows, raw, current.windows)
            # Reordered or hidden tabs make positional matching unsafe.
            slots = aligned_slots(node, raw)
            mapped: list[ChromeTab] = []
            for index, tab in enumerate(raw.tabs):
                if tab.id in seen_tabs:
                    raise ChromeMacError("Duplicate Chrome tab ID")
                seen_tabs.add(tab.id)
                mapped.append(chrome_tab(tab, index, raw, slots[index] if slots else None))
            profile = profile_of(node, profiles)
            unknown = profile is None or raw.mode != "normal"
            windows.append(
                ChromeWindow(
                    hwnd=raw.id,
                    title=raw.title,
                    profile_dir=profile.directory if profile else None,
                    profile_name=profile.name if profile else None,
                    age_rank=raw.id,
                    minimized=raw.minimized,
                    desktop_number=None,
                    tabs=tuple(mapped),
                    rect=_rect(raw),
                    profile_scope=f"(unknown profile: window {raw.id})" if unknown else None,
                )
            )
        return windows

    def close_tab(self, tab: ChromeTab, *, timeout_seconds: float = 3.0) -> CloseOutcome:
        """Revalidate native identity, content and AX safety, then confirm removal.

        The native command repeats the title/URL check immediately before close.
        It never selects a different tab, retries close, or accepts a dialog.
        """
        started = time.perf_counter()

        def outcome(closed: bool, detail: str) -> CloseOutcome:
            return CloseOutcome(closed, detail, time.perf_counter() - started)

        try:
            action_ids(tab)
            if tab.url is None:
                return outcome(False, "URL unavailable; refusing close")
            matches = [
                current
                for window in self.list_windows()
                if window.hwnd == tab.window_hwnd
                for current in window.tabs
                if current.runtime_id == tab.runtime_id
            ]
            if len(matches) != 1:
                return outcome(False, "tab no longer present")
            current = matches[0]
            if current.title != tab.title:
                return outcome(False, f"title changed to {current.title!r}")
            if current.url != tab.url:
                return outcome(False, "URL changed; refusing close")
            if METADATA_UNAVAILABLE in current.alerts:
                return outcome(False, f"{METADATA_UNAVAILABLE}; refusing close")
            if current.pinned or current.audible:
                return outcome(False, "tab is pinned or audible; refusing close")
            reply = status(macos_jxa.run_jxa(tab_action(tab, close_action(tab.title, tab.url))))
            if reply != "invoked":
                return outcome(False, f"close refused: {reply}")
            gone = self._wait_for_removal(tab, timeout_seconds)
            return outcome(
                gone, "closed" if gone else "invoked close but the tab is still present; a prompt may be open"
            )
        except ChromeMacError as exc:
            return outcome(False, f"close failed: {exc}")

    def _wait_for_removal(self, tab: ChromeTab, timeout_seconds: float) -> bool:
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        while True:
            present = any(raw.id == tab.runtime_id[0] for window in self.snapshot().windows for raw in window.tabs)
            if not present:
                return True
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return False
            time.sleep(min(_POLL_SECONDS, remaining))

    def restore_selection(
        self, before: Iterable[ChromeWindow], *, fallbacks: Mapping[int, ChromeTab] | None = None
    ) -> dict[int, ChromeTab | None]:
        """Restore each original tab by ID, or its same-window fallback if absent.

        The script resolves the ID to the current one-based index after closes.
        A failed selection returns None without trying a fallback.
        """
        restored: dict[int, ChromeTab | None] = {}
        for window in before:
            chosen: ChromeTab | None = None
            for candidate in (window.selected_tab, (fallbacks or {}).get(window.hwnd)):
                if candidate is None or candidate.window_hwnd != window.hwnd:
                    continue
                try:
                    reply = status(macos_jxa.run_jxa(tab_action(candidate, SELECT_ACTION)))
                except ChromeMacError:
                    break
                if reply == "selected":
                    chosen = candidate
                    break
                if reply != "missing":
                    break
            restored[window.hwnd] = chosen
        return restored

    def group_tab(
        self, tab: ChromeTab, *, title: str, new: bool, home_hwnd: int | None = None, timeout_seconds: float = 3.0
    ) -> GroupOutcome:
        from desktop_manager.chrome.drivers.macos_actions import group_tab

        return group_tab(self, tab, title=title, new=new, home_hwnd=home_hwnd, timeout_seconds=timeout_seconds)

    def ungroup(self, tab: ChromeTab, *, title: str, timeout_seconds: float = 3.0) -> GroupOutcome:
        from desktop_manager.chrome.drivers.macos_actions import ungroup

        return ungroup(self, tab, title=title, timeout_seconds=timeout_seconds)

    def move_to_window(self, tab: ChromeTab, target_hwnd: int, *, timeout_seconds: float = 3.0) -> MoveOutcome:
        from desktop_manager.chrome.drivers.macos_actions import move_to_window

        return move_to_window(self, tab, target_hwnd, timeout_seconds=timeout_seconds)

    def saved_groups(self, hwnd: int, profile: str) -> list[SavedGroup]:
        """No saved-group reader on macOS yet: report none, so consolidation leaves them alone."""
        del hwnd, profile
        return []

    def delete_saved_group(
        self, hwnd: int, profile: str, title: str, *, timeout_seconds: float = 3.0
    ) -> SavedDeleteOutcome:
        del hwnd, profile, timeout_seconds
        return SavedDeleteOutcome(False, f"saved group {title!r}: not supported on macOS", (), 0.0)
