"""Drive Chrome's tab strip through UI Automation, with no extension.

The reusable "PC control" layer for Chrome: list every browser window's tabs
and their groups, select one, close one, add one to a tab group, and put each
window's selection back afterwards. Callers decide *what* to act on; this
module only knows how to do it safely.

Everything except grouping is pure UIA and needs no focus. Grouping is the one
exception: Chrome exposes no UIA pattern for it, so `group_tab` right-clicks the
tab, picks "Add tab to group" from the context menu, and types a new group's
name. It runs only on a foreground window (see `foreground`), checks that the
click point really lands on the tab, and presses Escape on any surprise.

Lessons that shaped it, each learned the hard way against a live browser:

- **Windows come from EnumWindows, not the UIA root.** The root's children omit
  windows cloaked on other virtual desktops. Top-level `Chrome_WidgetWin_1`
  windows are enumerated by handle instead, filtered to `chrome.exe` because
  Slack, Docker Desktop and 1Password use the same window class. Every Chrome
  profile shares one browser process, so this covers them all.
- **Tabs are searched inside the tab strip only.** A descendant search for
  TabItem also finds in-page tabs (GitHub's Write/Preview) once a page's
  accessibility tree is built, and walking the page trees is what makes a
  whole-window search slow. The strip is found breadth-first with every
  Document subtree pruned.
- **Closing is select-then-invoke.** With many tabs Chrome hides the close
  button of every inactive tab, so an unselected tab has no children. Selecting
  it exposes a `TabCloseButton`; the button is invoked only while the tab is
  still selected. This works on minimised windows and on other desktops without
  stealing focus. Injected Ctrl+W never reached Chrome, so there is no keyboard
  fallback.
- **Identity is the UIA RuntimeId**, never the index (it shifts after every
  close) or the selection (selecting to close changes it). Every close
  re-finds its tab by RuntimeId and re-checks the title before acting.
- **Closing a group's last tab asks first.** Chrome shows "Close tab and delete
  group?" in a bubble hosted in a child HWND of the browser window, not under
  the window's root element. `close_tab` finds it through EnumChildWindows and
  confirms "Delete group", because the group would otherwise be left empty.
- **Group membership comes from the raw tree.** A grouped tab's raw parent is a
  `TabGroupView` whose `TabGroupHeaderView` names the group. The tab's own
  "Part of group" name suffix goes stale after a rename, so it is stripped.
- **Clicks are checked with WindowFromPoint, not ElementFromPoint.** Over the
  vertical tab strip, ElementFromPoint returns a `ShadowFrameView` overlay.
- **Saved groups are bookmarks-bar chips, deleted through their menu.** Each
  `SavedTabGroupButton` opens its context menu through UIA's `ShowContextMenu`
  with no mouse, even on a background window. Never Invoke a closed chip: that
  reopens the group with its old tabs. The bar's overflow ("Tab groups") opens
  only on a foreground window, so chips that do not fit are not read.
"""

from __future__ import annotations

import time
from collections.abc import Generator, Iterable, Mapping
from contextlib import contextmanager, suppress
from dataclasses import dataclass
from typing import TYPE_CHECKING

from desktop_manager.chrome.desktops import Desktops, WindowsDesktops
from desktop_manager.chrome.driver import CloseOutcome, GroupOutcome, MoveOutcome, SavedDeleteOutcome
from desktop_manager.chrome.input_windows import bring_forward, foreground_is, poll, press, take_foreground, type_text
from desktop_manager.chrome.saved_groups import SavedGroup, parse_chip_name
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, parse_tab_name
from desktop_manager.chrome.uia_core import UiaCore, element_name
from desktop_manager.win32.errors import WIN32_ERRORS

if TYPE_CHECKING:
    from comtypes.gen.UIAutomationClient import IUIAutomationElement

#: Accessible names Chrome gives a tab's close button. English only; extend for other UI languages.
CLOSE_BUTTON_NAMES = frozenset({"Close"})
_UNGROUP_DIALOG_TITLE = "Ungroup tab group?"
_DIALOG_SETTLE_SECONDS = 0.8
#: A tab-modal bubble (such as the delete-group question) hangs off the window root in its own `RootView`.
_DIALOG_ROOT_CLASS = "RootView"
_DELETE_GROUP_QUESTION = "Close tab and delete group?"
_DELETE_GROUP_BUTTON = "Delete group"
_SAVED_BAR_CLASS = "SavedTabGroupBar"
_SAVED_CHIP_CLASS = "SavedTabGroupButton"
_TABS_IN_GROUP_ITEM = "tabs in group"
#: Chrome takes seconds to drop a deleted saved group's chip from the bar.
_SAVED_DELETE_SECONDS = 15.0


@dataclass(frozen=True, slots=True)
class _SavedChip:
    group: SavedGroup
    element: IUIAutomationElement


class ChromeUia(UiaCore):
    """Chrome's tabs, groups and saved groups, driven through UIA. Windows only; construct once per command."""

    @property
    def desktops(self) -> Desktops:
        return WindowsDesktops()

    # --- closing ---------------------------------------------------------------------------------

    def close_tab(self, tab: ChromeTab, *, timeout_seconds: float = 3.0) -> CloseOutcome:
        """Select the tab, invoke its close button, and wait for it to leave the strip.

        Refuses - without touching anything - when the tab has gone, its title no
        longer matches what the caller saw, or the close button does not appear.
        Selecting a discarded tab makes Chrome start reloading it before it closes.
        """
        started = time.perf_counter()

        def outcome(closed: bool, detail: str) -> CloseOutcome:
            return CloseOutcome(closed=closed, detail=detail, seconds=time.perf_counter() - started)

        element = self.find_tab_element(tab)
        if element is None:
            return outcome(False, "tab no longer present")
        current_title = parse_tab_name(element_name(element)).title
        if current_title != tab.title:
            return outcome(False, f"title changed to {current_title!r}")

        selection = self._selection_pattern(element)
        selection.Select()
        button = poll(lambda: self._close_button(element), timeout_seconds)
        if button is None:
            return outcome(False, "close button did not appear after selecting")
        if not selection.CurrentIsSelected:
            return outcome(False, "tab lost selection before close")
        if button.CurrentName not in CLOSE_BUTTON_NAMES:
            return outcome(False, f"unexpected close button name {button.CurrentName!r}")

        grouped = self._group_of(element) is not None
        self._invoke(button)

        def closed_or_confirmed() -> bool | None:
            if self.find_tab_element(tab) is None:
                return True
            if grouped:
                self._confirm_group_delete(tab.window_hwnd)
            return None

        gone = poll(closed_or_confirmed, timeout_seconds)
        return outcome(bool(gone), "closed" if gone else "invoked close but the tab is still present")

    def _confirm_group_delete(self, hwnd: int) -> None:
        """Closing a group's last tab makes Chrome ask "Close tab and delete group?" in a bubble
        of its own. The group would be left empty, so confirm it."""
        import win32gui

        hosts: list[int] = []
        win32gui.EnumChildWindows(hwnd, lambda child, _: hosts.append(child) or True, 0)
        for host in hosts:
            try:
                element = self._uia.ElementFromHandle(host)
            except WIN32_ERRORS:
                continue
            dialog = element.FindFirst(
                self._u.TreeScope_Children,
                self._uia.CreatePropertyCondition(self._u.UIA_ClassNamePropertyId, _DIALOG_ROOT_CLASS),
            )
            if dialog:
                question = dialog.FindFirst(
                    self._u.TreeScope_Descendants,
                    self._uia.CreatePropertyCondition(self._u.UIA_NamePropertyId, _DELETE_GROUP_QUESTION),
                )
                button = dialog.FindFirst(
                    self._u.TreeScope_Descendants,
                    self._uia.CreateAndCondition(
                        self._uia.CreatePropertyCondition(
                            self._u.UIA_ControlTypePropertyId, self._u.UIA_ButtonControlTypeId
                        ),
                        self._uia.CreatePropertyCondition(self._u.UIA_NamePropertyId, _DELETE_GROUP_BUTTON),
                    ),
                )
                if question and button:
                    self._invoke(button)
                    return

    def _close_button(self, element: IUIAutomationElement) -> IUIAutomationElement | None:
        try:
            button = element.FindFirst(self._u.TreeScope_Children, self._close_condition)
        except WIN32_ERRORS:
            return None
        return button or None

    def restore_selection(
        self, before: Iterable[ChromeWindow], *, fallbacks: Mapping[int, ChromeTab] | None = None
    ) -> dict[int, ChromeTab | None]:
        """Re-select each window's originally selected tab, or its fallback if that tab closed.

        Returns the tab re-selected (or None) per window hwnd. A window whose selected
        tab closed with no fallback keeps whichever neighbour Chrome picked.
        """
        restored: dict[int, ChromeTab | None] = {}
        for window in before:
            selected = window.selected_tab
            candidates = [tab for tab in (selected, (fallbacks or {}).get(window.hwnd)) if tab is not None]
            chosen = next((tab for tab in candidates if self.select_tab(tab)), None)
            restored[window.hwnd] = chosen
        return restored

    # --- saved groups (bookmarks bar; no focus needed) ---------------------------------------------

    def saved_groups(self, hwnd: int, profile: str) -> list[SavedGroup]:
        """The saved-group chips on the window's bookmarks bar, left to right. Empty when the
        bar is hidden; chips pushed into the overflow are not included."""
        return [chip.group for chip in self._saved_chips(hwnd, profile)]

    def _saved_chips(self, hwnd: int, profile: str) -> list[_SavedChip]:
        try:
            bar = self._find_view(self._uia.ElementFromHandle(hwnd), _SAVED_BAR_CLASS)
        except WIN32_ERRORS:
            return []
        if bar is None:
            return []
        found = bar.FindAll(
            self._u.TreeScope_Descendants,
            self._uia.CreatePropertyCondition(self._u.UIA_ClassNamePropertyId, _SAVED_CHIP_CLASS),
        )
        chips: list[_SavedChip] = []
        for index in range(found.Length):
            element = found.GetElement(index)
            parsed = parse_chip_name(element_name(element))
            if parsed is not None:
                chips.append(_SavedChip(SavedGroup(profile, parsed.title, parsed.opened, len(chips)), element))
        return chips

    def delete_saved_group(
        self, hwnd: int, profile: str, title: str, *, timeout_seconds: float = 3.0
    ) -> SavedDeleteOutcome:
        """Delete the rightmost closed saved group titled `title` from the window's bar.

        Refuses, touching nothing, unless the bar still shows another saved copy of the
        title, so the last copy of a group is never deleted. Opens the chip's menu with
        `ShowContextMenu` (never Invoke: that reopens a closed group), records the tabs it
        lists, and picks Delete group; a confirmation, if Chrome asks, is pressed once it
        has settled.
        """
        import win32con
        import win32process

        started = time.perf_counter()
        tabs: tuple[str, ...] = ()

        def outcome(deleted: bool, detail: str) -> SavedDeleteOutcome:
            return SavedDeleteOutcome(deleted=deleted, detail=detail, tabs=tabs, seconds=time.perf_counter() - started)

        def copies() -> list[_SavedChip]:
            return [chip for chip in self._saved_chips(hwnd, profile) if chip.group.title == title]

        before = copies()
        closed = [chip.element for chip in before if not chip.group.opened]
        if not closed:
            return outcome(False, f"no closed saved group called {title!r} on the bar")
        if len(before) < 2:
            return outcome(False, f"the only saved copy of {title!r}; kept")
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        if not self._menus_closed(pid, timeout_seconds):
            return outcome(False, "a menu from the previous step is still open")

        closed[-1].QueryInterface(self._u.IUIAutomationElement3).ShowContextMenu()
        found = poll(lambda: self._menu_item(pid, lambda name: name == "delete group"), timeout_seconds)
        items = [element_name(item).strip() for item in self._popup_elements(pid, self._u.UIA_MenuItemControlTypeId)]
        lowered = [item.lower() for item in items]
        if _TABS_IN_GROUP_ITEM in lowered:
            tabs = tuple(item for item in items[lowered.index(_TABS_IN_GROUP_ITEM) + 1 :] if item)
        if found is None or "open group" not in lowered:
            press(win32con.VK_ESCAPE)
            return outcome(False, "the chip's menu did not offer Delete group")
        self._invoke(found.element)

        # As with ungrouping, a confirm is pressed only once its dialog has settled.
        seen_at: float | None = None
        pressed_at = 0.0

        def gone() -> bool | None:
            nonlocal seen_at, pressed_at
            if len(copies()) < len(before):
                return True
            confirm = self._confirm_button(hwnd, {"delete group", "delete"})
            if confirm is None:
                return None
            now = time.perf_counter()
            seen_at = now if seen_at is None else seen_at
            if now - seen_at >= _DIALOG_SETTLE_SECONDS and now - pressed_at >= _DIALOG_SETTLE_SECONDS:
                pressed_at = now
                with suppress(Exception):
                    self._invoke(confirm)
            return None

        if poll(gone, _SAVED_DELETE_SECONDS) is None:
            if self._popup_elements(pid, self._u.UIA_MenuItemControlTypeId):
                press(win32con.VK_ESCAPE)
            return outcome(False, f"saved group {title!r} is still on the bar")
        return outcome(True, "deleted")

    # --- grouping (mouse and keyboard; foreground only) -------------------------------------------

    def foreground_window(self) -> int:
        """The foreground window's handle, whatever owns it."""
        import win32gui

        return win32gui.GetForegroundWindow()

    @contextmanager
    def foreground(self, hwnd: int, *, timeout_seconds: float = 2.0) -> Generator[bool]:
        """Bring a window to the front for the duration, then put the cursor, the previous
        foreground window and a minimised window's state back. Yields whether it got there."""
        import pywintypes
        import win32api
        import win32con
        import win32gui

        try:
            cursor = win32api.GetCursorPos()
        except pywintypes.error:
            # The lock screen holds the input desktop: no window can come forward.
            yield False
            return
        previous = win32gui.GetForegroundWindow()
        was_minimised = bool(win32gui.IsIconic(hwnd))
        try:
            # A process may only take the foreground right after input; a bare Alt tap counts.
            press(win32con.VK_MENU)
            if was_minimised:
                win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
            arrived = take_foreground(hwnd, timeout_seconds)
            if arrived:
                time.sleep(0.2)  # let a restored window lay out its tab strip before anything clicks it
            yield bool(arrived)
        finally:
            win32api.SetCursorPos(cursor)
            if was_minimised:
                win32gui.ShowWindow(hwnd, win32con.SW_MINIMIZE)
            if previous and previous != hwnd and win32gui.IsWindow(previous):
                with suppress(Exception):
                    win32gui.SetForegroundWindow(previous)

    def group_tab(
        self,
        tab: ChromeTab,
        *,
        title: str,
        new: bool,
        home_hwnd: int | None = None,
        timeout_seconds: float = 3.0,
    ) -> GroupOutcome:
        """Put an ungrouped tab into a new group called `title`, or into the profile's existing one.

        The menu lists every group in the profile, and joining one in another window
        (`home_hwnd`) moves the tab there; the tab then has a new RuntimeId, so arrival
        is checked by a tab with its title turning up in the home window's group.

        Chrome offers grouping only on the tab's context menu, and that menu opens only for a
        real right-click on a foreground window, so this moves the mouse. Run it inside
        `foreground(tab.window_hwnd)`. Refuses, touching nothing, when the tab has gone, its
        title changed, it is already grouped, or a click would not land on it; once a menu is
        open, any surprise closes it with Escape.
        """
        import win32api
        import win32con
        import win32process

        started = time.perf_counter()

        def outcome(grouped: bool, detail: str) -> GroupOutcome:
            return GroupOutcome(grouped=grouped, detail=detail, seconds=time.perf_counter() - started)

        element = self.find_tab_element(tab)
        if element is None:
            return outcome(False, "tab no longer present")
        current_title = parse_tab_name(element_name(element)).title
        if current_title != tab.title:
            return outcome(False, f"title changed to {current_title!r}")
        if self._group_of(element) is not None:
            return outcome(False, "tab is already in a group")
        if not bring_forward(tab.window_hwnd):
            return outcome(False, "window is not in the foreground")

        _, pid = win32process.GetWindowThreadProcessId(tab.window_hwnd)
        home = home_hwnd if home_hwnd is not None and home_hwnd != tab.window_hwnd else None
        before = self._titled_in_group(home, title, tab.title) if home is not None else 0
        if before is None:
            return outcome(False, "home window could not be read to count its group before the move")
        # A right-click made while the previous step's menu is still closing is swallowed.
        if not self._menus_closed(pid, timeout_seconds):
            return outcome(False, "a menu from the previous step is still open")

        scroll = element.GetCurrentPattern(self._u.UIA_ScrollItemPatternId)
        if bool(scroll):
            scroll.QueryInterface(self._u.IUIAutomationScrollItemPattern).ScrollIntoView()
        rect = element.CurrentBoundingRectangle
        x, y = (rect.left + rect.right) // 2, (rect.top + rect.bottom) // 2
        if rect.right <= rect.left or rect.bottom <= rect.top or not self._is_at_point(tab, element, x, y):
            return outcome(False, "tab is not visible where a click would land")

        win32api.SetCursorPos((x, y))
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTDOWN, 0, 0)
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTUP, 0, 0)
        found = poll(lambda: self._menu_item(pid, _is_group_entry), timeout_seconds)
        if found is None:
            press(win32con.VK_ESCAPE)
            return outcome(False, "context menu did not offer a group entry")
        entry, entry_name = found.element, found.name
        if entry_name == "add tab to new group":
            # Chrome offers this flat entry only while the window has no groups at all.
            if not new:
                press(win32con.VK_ESCAPE)
                return outcome(False, f"group {title!r} no longer exists")
            self._invoke(entry)
        else:
            expand = entry.GetCurrentPattern(self._u.UIA_ExpandCollapsePatternId)
            if not bool(expand):
                press(win32con.VK_ESCAPE)
                return outcome(False, f"unexpected menu entry {entry_name!r}")
            expand.QueryInterface(self._u.IUIAutomationExpandCollapsePattern).Expand()
            wanted = "new group" if new else title.lower()
            chosen = poll(lambda: self._menu_items(pid, lambda name: name == wanted) or None, timeout_seconds)
            if chosen is None or len(chosen) > 1:
                press(win32con.VK_ESCAPE)
                press(win32con.VK_ESCAPE)
                if chosen is not None:
                    return outcome(False, f"{len(chosen)} menu entries named {title!r}; cannot tell them apart")
                return outcome(False, "new-group entry not offered" if new else f"group {title!r} not offered")
            self._invoke(chosen[0])

        if new:
            field = poll(lambda: self._title_field(pid), timeout_seconds)
            if field is None:
                return outcome(False, "group created but its title field did not appear; left unnamed")
            if not field.CurrentHasKeyboardFocus or not foreground_is(pid):
                press(win32con.VK_ESCAPE)
                return outcome(False, "group created but its title field lost focus; left unnamed")
            type_text(title)
            press(win32con.VK_RETURN)

        if home is not None:
            arrived = poll(lambda: self._arrived_in_group(home, title, tab.title, before), timeout_seconds)
            if arrived is None:
                return outcome(False, f"tab did not show up in group {title!r} in its home window")
            return outcome(True, "moved")
        joined = poll(lambda: self._joined(tab, title), timeout_seconds)
        if joined is None:
            return outcome(False, f"tab did not show up in group {title!r}")
        return outcome(True, "created" if new else "joined")

    def _arrived_in_group(self, hwnd: int, group_title: str, tab_title: str, before: int) -> bool | None:
        """True once the group holds more `tab_title` tabs than `before`; None (keep polling) otherwise,
        including while the window will not read cleanly."""
        count = self._titled_in_group(hwnd, group_title, tab_title)
        return True if count is not None and count > before else None

    def move_to_window(self, tab: ChromeTab, target_hwnd: int, *, timeout_seconds: float = 3.0) -> MoveOutcome:
        """Move an ungrouped tab into another window through "Move tab to another window".

        That submenu names each window after its active tab (`<title> and N other tabs`,
        long titles cut short with `…`), so the entry must match the target's active tab
        and tab count and be the only one that does. Returns the tab as it now sits in the
        target window, with its new RuntimeId. Run it inside `foreground(tab.window_hwnd)`.
        """
        import win32api
        import win32con
        import win32process

        started = time.perf_counter()

        def outcome(done: bool, detail: str) -> GroupOutcome:
            return GroupOutcome(grouped=done, detail=detail, seconds=time.perf_counter() - started)

        def refused(detail: str) -> MoveOutcome:
            return MoveOutcome(outcome(False, detail))

        element = self.find_tab_element(tab)
        if element is None:
            return refused("tab no longer present")
        current_title = parse_tab_name(element_name(element)).title
        if current_title != tab.title:
            return refused(f"title changed to {current_title!r}")
        if self._group_of(element) is not None:
            return refused("tab is already in a group")
        target = self.read_window(target_hwnd)
        active = next((each for each in target.tabs if each.selected), None) if target is not None else None
        if target is None or active is None:
            return refused("target window has gone or has no active tab")
        if not bring_forward(tab.window_hwnd):
            return refused("window is not in the foreground")

        _, pid = win32process.GetWindowThreadProcessId(tab.window_hwnd)
        before = {each.runtime_id for each in target.tabs}
        if not self._menus_closed(pid, timeout_seconds):
            return refused("a menu from the previous step is still open")
        scroll = element.GetCurrentPattern(self._u.UIA_ScrollItemPatternId)
        if bool(scroll):
            scroll.QueryInterface(self._u.IUIAutomationScrollItemPattern).ScrollIntoView()
        rect = element.CurrentBoundingRectangle
        x, y = (rect.left + rect.right) // 2, (rect.top + rect.bottom) // 2
        if rect.right <= rect.left or rect.bottom <= rect.top or not self._is_at_point(tab, element, x, y):
            return refused("tab is not visible where a click would land")

        win32api.SetCursorPos((x, y))
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTDOWN, 0, 0)
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTUP, 0, 0)
        found = poll(lambda: self._menu_item(pid, lambda name: name == "move tab to another window"), timeout_seconds)
        if found is None:
            press(win32con.VK_ESCAPE)
            return refused("context menu did not offer Move tab to another window")
        expand = found.element.GetCurrentPattern(self._u.UIA_ExpandCollapsePatternId)
        if not bool(expand):
            press(win32con.VK_ESCAPE)
            return refused("Move tab to another window did not open a submenu")
        expand.QueryInterface(self._u.IUIAutomationExpandCollapsePattern).Expand()
        chosen = poll(
            lambda: self._menu_items(pid, lambda name: names_window(name, active.title, len(target.tabs))) or None,
            timeout_seconds,
        )
        if chosen is None or len(chosen) > 1:
            press(win32con.VK_ESCAPE)
            press(win32con.VK_ESCAPE)
            if chosen is not None:
                return refused(f"{len(chosen)} windows are named like the target; cannot tell them apart")
            return refused("target window not offered")
        self._invoke(chosen[0])

        def arrived() -> ChromeTab | None:
            window = self.read_window(target_hwnd)
            if window is None:
                return None
            fresh = [each for each in window.tabs if each.runtime_id not in before and each.title == tab.title]
            return fresh[0] if len(fresh) == 1 and fresh[0].group is None else None

        moved = poll(arrived, timeout_seconds)
        if moved is None:
            return refused("tab did not show up in the target window")
        return MoveOutcome(outcome(True, "moved"), moved)

    def ungroup(self, tab: ChromeTab, *, title: str, timeout_seconds: float = 3.0) -> GroupOutcome:
        """Dissolve the group `tab` sits in, through the editor bubble its header opens on a
        right-click; its tabs stay put.

        A saved group asks first ("Ungroup tab group?", in a dialog inside the window),
        because ungrouping deletes it from the saved groups too; that is confirmed here.

        Run it inside `foreground(tab.window_hwnd)`. Refuses, touching nothing, when the tab
        has gone or its group is no longer called `title`.
        """
        import win32api
        import win32con
        import win32process

        started = time.perf_counter()

        def outcome(done: bool, detail: str) -> GroupOutcome:
            return GroupOutcome(grouped=done, detail=detail, seconds=time.perf_counter() - started)

        element = self.find_tab_element(tab)
        group = self._group_of(element) if element is not None else None
        if element is None or group is None or group.title != title:
            return outcome(False, f"tab is no longer in a group called {title!r}")
        if not bring_forward(tab.window_hwnd):
            return outcome(False, "window is not in the foreground")
        header = self._group_header(element)
        if header is None:
            return outcome(False, "group header not found")
        _, pid = win32process.GetWindowThreadProcessId(tab.window_hwnd)
        if not self._menus_closed(pid, timeout_seconds):
            return outcome(False, "a menu from the previous step is still open")
        rect = header.CurrentBoundingRectangle
        x, y = (rect.left + rect.right) // 2, (rect.top + rect.bottom) // 2
        if rect.right <= rect.left or rect.bottom <= rect.top or not self._is_at_point(tab, header, x, y):
            return outcome(False, "group header is not visible where a click would land")
        win32api.SetCursorPos((x, y))
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTDOWN, 0, 0)
        win32api.mouse_event(win32con.MOUSEEVENTF_RIGHTUP, 0, 0)
        found = poll(lambda: self._bubble_button(pid, "ungroup"), timeout_seconds)
        if found is None:
            press(win32con.VK_ESCAPE)
            return outcome(False, "group editor did not offer Ungroup")
        self._invoke(found)

        # Chrome ignores a dialog's buttons for a moment after it appears, and treats
        # presses closer together than a double-click as accidental, so the confirm is
        # pressed only once the dialog has settled, and retried no faster than that.
        seen_at: float | None = None
        pressed_at = 0.0

        def settled() -> bool | None:
            nonlocal seen_at, pressed_at
            if self._group_of_tab(tab) is None:
                return True
            confirm = self._dialog_button(tab.window_hwnd, _UNGROUP_DIALOG_TITLE, "ungroup")
            now = time.perf_counter()
            if confirm is None:
                return None
            seen_at = now if seen_at is None else seen_at
            if now - seen_at >= _DIALOG_SETTLE_SECONDS and now - pressed_at >= _DIALOG_SETTLE_SECONDS:
                pressed_at = now
                with suppress(Exception):
                    self._invoke(confirm)
            return None

        gone = poll(settled, timeout_seconds + 2 * _DIALOG_SETTLE_SECONDS)
        if gone is None:
            return outcome(False, f"group {title!r} is still there")
        return outcome(True, "ungrouped")


def names_window(entry: str, active_title: str, tab_count: int) -> bool:
    """Whether a "Move tab to another window" entry (lower-cased) names the window whose
    active tab is `active_title` and which holds `tab_count` tabs."""
    others = tab_count - 1
    suffix = "" if others == 0 else f" and {others} other tab{'' if others == 1 else 's'}"
    if suffix and not entry.endswith(suffix):
        return False
    head = entry[: len(entry) - len(suffix)] if suffix else entry
    title = active_title.strip().lower()
    if head.endswith("…"):
        return len(head) > 1 and title.startswith(head[:-1])
    return head == title


def _is_group_entry(name: str) -> bool:
    """ "Add tab to new group" (a window with no groups) or the "Add tab to group" submenu -
    not "Add tab to new split view", which sits right above them."""
    return name in {"add tab to new group", "add tab to group"}
