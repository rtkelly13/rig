"""The UIA session underneath `ChromeUia`: reading windows and tabs, finding elements, menus and dialogs.

`desktop_manager.chrome.uia` builds the actions (close, group, move, delete) on top of it;
its docstring explains the lessons behind both.
"""

from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol

from desktop_manager.chrome.driver import ChromeReadError
from desktop_manager.chrome.input_windows import poll
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, TabGroup, parse_group_header, parse_tab_name
from desktop_manager.chrome.window_info import (
    CHROME_WINDOW_CLASS,
    chrome_window_handles,
    monitor_origin,
    window_profile,
    window_rect,
)
from desktop_manager.win32.errors import WIN32_ERRORS

if TYPE_CHECKING:
    from comtypes.gen.UIAutomationClient import (
        IUIAutomation,
        IUIAutomationCondition,
        IUIAutomationElement,
        IUIAutomationSelectionItemPattern,
        IUIAutomationTreeWalker,
    )
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationElement3 as _Element3,
    )
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationExpandCollapsePattern as _ExpandCollapsePattern,
    )
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationInvokePattern as _InvokePattern,
    )
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationScrollItemPattern as _ScrollItemPattern,
    )
    from comtypes.gen.UIAutomationClient import (
        IUIAutomationSelectionItemPattern as _SelectionItemPattern,
    )

_TAB_STRIP_CLASS = "TabStripView"
_TAB_CLASS = "TabView"
_CLOSE_BUTTON_CLASS = "TabCloseButton"
_ROOT_VIEW_CLASS = "BrowserRootView"
_GROUP_VIEW_CLASS = "TabGroupView"
_BUBBLE_BUTTON_CLASS = "BubbleMenuItemButton"
_GROUP_HEADER_CLASS = "TabGroupHeaderView"
#: The new-group bubble's title box is a `TabGroupEditorBubbleView::TitleField`.
_TITLE_FIELD_CLASS_HINT = "TitleField"
_STRIP_SEARCH_MAX_DEPTH = 12
#: Reads of one window before giving up on it; Chrome rebuilds tab views as tabs load, move or close.
READ_ATTEMPTS = 3
READ_RETRY_SECONDS = 0.25


class UiaElement(Protocol):
    """The two text properties read off every `IUIAutomationElement`; COM hands back None when unset."""

    CurrentName: str | None
    CurrentClassName: str | None


class UiaClient(Protocol):
    """The parts of the generated `comtypes.gen.UIAutomationClient` module that the Chrome UIA code reads."""

    IUIAutomationElement3: type[_Element3]
    IUIAutomationExpandCollapsePattern: type[_ExpandCollapsePattern]
    IUIAutomationInvokePattern: type[_InvokePattern]
    IUIAutomationScrollItemPattern: type[_ScrollItemPattern]
    IUIAutomationSelectionItemPattern: type[_SelectionItemPattern]
    TreeScope_Children: int
    TreeScope_Descendants: int
    TreeScope_Subtree: int
    UIA_ButtonControlTypeId: int
    UIA_ClassNamePropertyId: int
    UIA_ControlTypePropertyId: int
    UIA_DocumentControlTypeId: int
    UIA_EditControlTypeId: int
    UIA_ExpandCollapsePatternId: int
    UIA_InvokePatternId: int
    UIA_MenuItemControlTypeId: int
    UIA_NamePropertyId: int
    UIA_ScrollItemPatternId: int
    UIA_SelectionItemPatternId: int
    UIA_WindowControlTypeId: int


def element_name(element: UiaElement) -> str:
    return element.CurrentName or ""


def element_class(element: UiaElement) -> str:
    return element.CurrentClassName or ""


def read_settled[T](
    read: Callable[[], T],
    *,
    alive: Callable[[], bool],
    on_retry: Callable[[], None],
    attempts: int = READ_ATTEMPTS,
    pause_seconds: float = READ_RETRY_SECONDS,
    sleep: Callable[[float], None] = time.sleep,
) -> T | None:
    """`read()`, retried while a UIA call fails because an element went stale mid-read.

    None when the window itself has gone (`alive` is False), which is an honest answer.
    When the window is still there but no attempt reads cleanly, raises `ChromeReadError`
    rather than returning a guess: every property matters (pinned and grouped tabs are
    protected), so a tab must not be reported with any of them defaulted.
    """
    last: Exception | None = None
    for attempt in range(attempts):
        if attempt:
            sleep(pause_seconds)
        try:
            return read()
        except WIN32_ERRORS as error:
            last = error
            on_retry()
            if not alive():
                return None
    raise ChromeReadError(f"Chrome's tab strip kept changing during the read ({attempts} attempts): {last}") from last


@dataclass(frozen=True, slots=True)
class MenuItem:
    """An open menu item, with its lower-cased name."""

    element: IUIAutomationElement
    name: str


class UiaCore:
    """A UIA session against the running Chrome. Windows only; construct once per command."""

    def __init__(self) -> None:
        from desktop_manager.win32 import ensure_windows

        ensure_windows()
        import comtypes.client

        comtypes.client.GetModule("UIAutomationCore.dll")
        from comtypes.gen import UIAutomationClient

        self._u: UiaClient = UIAutomationClient
        self._uia: IUIAutomation = comtypes.client.CreateObject(
            UIAutomationClient.CUIAutomation, interface=UIAutomationClient.IUIAutomation
        )
        self._walker: IUIAutomationTreeWalker = self._uia.ControlViewWalker
        self._tab_condition: IUIAutomationCondition = self._uia.CreateAndCondition(
            self._uia.CreatePropertyCondition(
                UIAutomationClient.UIA_ControlTypePropertyId, UIAutomationClient.UIA_TabItemControlTypeId
            ),
            self._uia.CreatePropertyCondition(UIAutomationClient.UIA_ClassNamePropertyId, _TAB_CLASS),
        )
        self._close_condition: IUIAutomationCondition = self._uia.CreateAndCondition(
            self._uia.CreatePropertyCondition(
                UIAutomationClient.UIA_ControlTypePropertyId, UIAutomationClient.UIA_ButtonControlTypeId
            ),
            self._uia.CreatePropertyCondition(UIAutomationClient.UIA_ClassNamePropertyId, _CLOSE_BUTTON_CLASS),
        )
        self._strips: dict[int, IUIAutomationElement] = {}

    # --- reading ---------------------------------------------------------------------------------

    def list_windows(self) -> list[ChromeWindow]:
        """Every Chrome browser window with its tabs, on every desktop and profile, oldest first."""
        windows: list[ChromeWindow] = []
        for enum_index, hwnd in enumerate(chrome_window_handles()):
            window = self._read_window(hwnd, enum_index)
            if window is not None:
                windows.append(window)
        windows.sort(key=lambda window: (window.age_rank, window.hwnd))
        return windows

    def _read_window(self, hwnd: int, enum_index: int) -> ChromeWindow | None:
        """One window, read until a pass gets through without an element going stale.

        Raises `ChromeReadError` when the window stays but never reads cleanly."""
        import win32gui

        return read_settled(
            lambda: self._read_window_once(hwnd, enum_index),
            alive=lambda: bool(win32gui.IsWindow(hwnd)),
            on_retry=lambda: self._forget_strip(hwnd),
        )

    def _read_window_once(self, hwnd: int, enum_index: int) -> ChromeWindow | None:
        import win32gui

        from desktop_manager.win32.desktops import get_window_desktop_number

        try:
            root = self._uia.ElementFromHandle(hwnd)
        except WIN32_ERRORS:
            return None
        strip = self._find_tab_strip(hwnd, root)
        if strip is None:  # an app window or popup: no tab strip, nothing to manage
            return None
        profile = window_profile(hwnd)
        return ChromeWindow(
            hwnd=hwnd,
            title=str(win32gui.GetWindowText(hwnd)),
            profile_dir=profile.directory,
            profile_name=profile.name,
            age_rank=self._age_rank(root, enum_index),
            minimized=bool(win32gui.IsIconic(hwnd)),
            desktop_number=get_window_desktop_number(hwnd),
            tabs=tuple(self._read_tabs(hwnd, strip)),
            rect=window_rect(hwnd),
            monitor=monitor_origin(hwnd),
        )

    def _forget_strip(self, hwnd: int) -> None:
        """Drop the cached strip so the next read finds it afresh; a stale one is why reads fail."""
        _ = self._strips.pop(hwnd, None)

    def read_window(self, hwnd: int) -> ChromeWindow | None:
        """One window afresh, as `list_windows` would read it (its age rank aside).

        None when it has gone or will not read cleanly: callers are actions checking their
        own result, and an unreadable window counts as not confirmed."""
        try:
            return self._read_window(hwnd, 0)
        except ChromeReadError:
            return None

    def _read_tabs(self, hwnd: int, strip: IUIAutomationElement) -> list[ChromeTab]:
        tabs: list[ChromeTab] = []
        for index, element in enumerate(self._tab_elements(strip)):
            name = element_name(element)
            parsed = parse_tab_name(name)
            tabs.append(
                ChromeTab(
                    runtime_id=tuple(element.GetRuntimeId()),
                    window_hwnd=hwnd,
                    index=index,
                    name=name,
                    title=parsed.title,
                    selected=self._is_selected(element),
                    pinned=parsed.pinned or self._is_pinned(element, strip),
                    loaded=parsed.loaded,
                    audible=parsed.audible,
                    alerts=parsed.alerts,
                    memory_mb=parsed.memory_mb,
                    group=self._group_of(element),
                )
            )
        return tabs

    def _group_of(self, element: IUIAutomationElement) -> TabGroup | None:
        """The group whose `TabGroupView` holds the tab, titled from the group's header.

        The tab's own "Part of group ..." suffix is not used: Chrome leaves it stale
        after a rename. The group view only shows up in the raw view.
        """
        raw = self._uia.RawViewWalker
        parent = raw.GetParentElement(element)
        if not parent or parent.CurrentClassName != _GROUP_VIEW_CLASS:
            return None
        child = raw.GetFirstChildElement(parent)
        while child:
            if child.CurrentClassName == _GROUP_HEADER_CLASS:
                title = parse_group_header(element_name(child))
                if title is None:
                    return None
                return TabGroup(runtime_id=tuple(parent.GetRuntimeId()), title=title)
            child = raw.GetNextSiblingElement(child)
        return None

    def _tab_elements(self, strip: IUIAutomationElement) -> list[IUIAutomationElement]:
        found = strip.FindAll(self._u.TreeScope_Descendants, self._tab_condition)
        return [found.GetElement(i) for i in range(found.Length)]

    def _find_tab_strip(self, hwnd: int, root: IUIAutomationElement) -> IUIAutomationElement | None:
        """The window's tab strip, cached per window while its element stays live."""
        cached = self._strips.get(hwnd)
        if cached is not None:
            try:
                _ = cached.CurrentClassName  # touch it to prove the element is still live
            except WIN32_ERRORS:
                del self._strips[hwnd]
            else:
                return cached
        strip = self._find_view(root, _TAB_STRIP_CLASS)
        if strip is not None:
            self._strips[hwnd] = strip
        return strip

    def _find_view(self, root: IUIAutomationElement, class_name: str) -> IUIAutomationElement | None:
        """Breadth-first search for a browser view by class that never descends into web content."""
        level: list[IUIAutomationElement] = [root]
        for _ in range(_STRIP_SEARCH_MAX_DEPTH):
            below: list[IUIAutomationElement] = []
            for element in level:
                child = self._walker.GetFirstChildElement(element)
                while child:
                    if child.CurrentClassName == class_name:
                        return child
                    if child.CurrentControlType != self._u.UIA_DocumentControlTypeId:
                        below.append(child)
                    child = self._walker.GetNextSiblingElement(child)
            level = below
        return None

    def _age_rank(self, root: IUIAutomationElement, enum_index: int) -> int:
        """Chrome's accessibility ids are assigned process-wide in creation order, so the
        root view's id orders windows by age. Falls back to z-order behind every real id."""
        child = self._walker.GetFirstChildElement(root)
        while child:
            if child.CurrentClassName == _ROOT_VIEW_CLASS:
                runtime_id = tuple(child.GetRuntimeId())
                if runtime_id:
                    return int(runtime_id[-1])
            child = self._walker.GetNextSiblingElement(child)
        return 1_000_000_000 + enum_index

    def _is_selected(self, element: IUIAutomationElement) -> bool:
        return bool(self._selection_pattern(element).CurrentIsSelected)

    def _selection_pattern(self, element: IUIAutomationElement) -> IUIAutomationSelectionItemPattern:
        pattern = element.GetCurrentPattern(self._u.UIA_SelectionItemPatternId)
        return pattern.QueryInterface(self._u.IUIAutomationSelectionItemPattern)

    def _is_pinned(self, element: IUIAutomationElement, strip: IUIAutomationElement) -> bool:
        """Pinned tabs sit in a `PinnedTabContainerView` (vertical strip) between the tab and the strip."""
        parent = self._walker.GetParentElement(element)
        for _ in range(4):
            if not parent or self._uia.CompareElements(parent, strip):
                return False
            class_name = element_class(parent)
            if "Pinned" in class_name and "Unpinned" not in class_name:
                return True
            parent = self._walker.GetParentElement(parent)
        return False

    # --- finding ---------------------------------------------------------------------------------

    def find_tab_element(self, tab: ChromeTab) -> IUIAutomationElement | None:
        """Re-find a tab's live element by RuntimeId. None if it has gone."""
        try:
            root = self._uia.ElementFromHandle(tab.window_hwnd)
        except WIN32_ERRORS:
            return None
        strip = self._find_tab_strip(tab.window_hwnd, root)
        if strip is None:
            return None
        for element in self._tab_elements(strip):
            if tuple(element.GetRuntimeId()) == tab.runtime_id:
                return element
        return None

    def select_tab(self, tab: ChromeTab) -> bool:
        element = self.find_tab_element(tab)
        if element is None:
            return False
        self._selection_pattern(element).Select()
        return True

    def _group_header(self, element: IUIAutomationElement) -> IUIAutomationElement | None:
        raw = self._uia.RawViewWalker
        parent = raw.GetParentElement(element)
        if not parent or parent.CurrentClassName != _GROUP_VIEW_CLASS:
            return None
        child = raw.GetFirstChildElement(parent)
        while child:
            if child.CurrentClassName == _GROUP_HEADER_CLASS:
                return child
            child = raw.GetNextSiblingElement(child)
        return None

    def _group_of_tab(self, tab: ChromeTab) -> TabGroup | None:
        element = self.find_tab_element(tab)
        return self._group_of(element) if element is not None else None

    def _titled_in_group(self, hwnd: int, group_title: str, tab_title: str) -> int | None:
        """How many tabs titled `tab_title` sit in the window's group called `group_title`.

        None when the window has gone or will not read cleanly: an unknown count, never 0,
        because a move is confirmed by the count rising above a baseline taken from it."""
        window = self.read_window(hwnd)
        if window is None:
            return None
        return sum(
            1
            for tab in window.tabs
            if tab.title == tab_title and tab.group is not None and tab.group.title == group_title
        )

    def _joined(self, tab: ChromeTab, title: str) -> bool | None:
        element = self.find_tab_element(tab)
        group = self._group_of(element) if element is not None else None
        return True if group is not None and group.title == title else None

    def _is_at_point(self, tab: ChromeTab, element: IUIAutomationElement, x: int, y: int) -> bool:
        """Whether a click at (x, y) reaches this tab: the point is in the tab's own window, not
        another window over it, and the tab sits inside the strip's visible area.

        UIA hit-testing cannot say this directly: over a vertical strip it returns a
        `ShadowFrameView` overlay rather than the tab underneath.
        """
        import win32con
        import win32gui

        try:
            under = win32gui.WindowFromPoint((x, y))
        except WIN32_ERRORS:
            return False
        if not under or win32gui.GetAncestor(under, win32con.GA_ROOT) != tab.window_hwnd:
            return False
        try:
            root = self._uia.ElementFromHandle(tab.window_hwnd)
        except WIN32_ERRORS:
            return False
        strip = self._find_tab_strip(tab.window_hwnd, root)
        if strip is None:
            return False
        area = strip.CurrentBoundingRectangle
        inside = area.left <= x < area.right and area.top <= y < area.bottom
        return inside and not element.CurrentIsOffscreen

    # --- menus, bubbles and dialogs ----------------------------------------------------------------

    def _popup_elements(self, pid: int, control_type: int) -> list[IUIAutomationElement]:
        """Elements of one control type in the process's popups (menus, bubbles), not its browser windows."""
        import win32gui
        import win32process

        handles: list[int] = []

        def callback(hwnd: int, lparam: int) -> bool:
            del lparam
            ours = win32gui.IsWindowVisible(hwnd) and win32process.GetWindowThreadProcessId(hwnd)[1] == pid
            browser = win32gui.GetClassName(hwnd) == CHROME_WINDOW_CLASS and win32gui.GetWindowText(hwnd).strip()
            if ours and not browser:
                handles.append(hwnd)
            return True

        win32gui.EnumWindows(callback, 0)
        condition = self._uia.CreatePropertyCondition(self._u.UIA_ControlTypePropertyId, control_type)
        found: list[IUIAutomationElement] = []
        for hwnd in handles:
            try:
                matches = self._uia.ElementFromHandle(hwnd).FindAll(self._u.TreeScope_Subtree, condition)
            except WIN32_ERRORS:
                continue
            found.extend(matches.GetElement(i) for i in range(matches.Length))
        return found

    def _menu_item(self, pid: int, matches: Callable[[str], bool]) -> MenuItem | None:
        """The first open menu item whose lower-cased name matches."""
        for item in self._popup_elements(pid, self._u.UIA_MenuItemControlTypeId):
            name = element_name(item).strip().lower()
            if name and matches(name):
                return MenuItem(item, name)
        return None

    def _menu_items(self, pid: int, matches: Callable[[str], bool]) -> list[IUIAutomationElement]:
        """Every open menu item whose lower-cased name matches."""
        return [
            item
            for item in self._popup_elements(pid, self._u.UIA_MenuItemControlTypeId)
            if matches(element_name(item).strip().lower())
        ]

    def _bubble_button(self, pid: int, name: str) -> IUIAutomationElement | None:
        """The open bubble's menu button named exactly `name` (lower-cased), as in the group editor.

        An exact name, because the same bubble offers Delete group and Close group."""
        for button in self._popup_elements(pid, self._u.UIA_ButtonControlTypeId):
            if element_class(button) == _BUBBLE_BUTTON_CLASS and element_name(button).strip().lower() == name:
                return button
        return None

    def _dialog_button(self, hwnd: int, dialog_title: str, name: str) -> IUIAutomationElement | None:
        """The button named exactly `name` (lower-cased) in the window's dialog titled `dialog_title`."""
        condition = self._uia.CreatePropertyCondition(self._u.UIA_NamePropertyId, dialog_title)
        try:
            dialog = self._uia.ElementFromHandle(hwnd).FindFirst(self._u.TreeScope_Descendants, condition)
        except WIN32_ERRORS:
            return None
        if not dialog:
            return None
        buttons = dialog.FindAll(
            self._u.TreeScope_Descendants,
            self._uia.CreatePropertyCondition(self._u.UIA_ControlTypePropertyId, self._u.UIA_ButtonControlTypeId),
        )
        for index in range(buttons.Length):
            button = buttons.GetElement(index)
            if element_name(button).strip().lower() == name:
                return button
        return None

    def _confirm_button(self, hwnd: int, names: set[str]) -> IUIAutomationElement | None:
        """A button named one of `names` (lower-cased) in a dialog over the window, which Chrome
        hosts in the window's own tree or in a child HWND of it."""
        import win32gui

        hosts: list[int] = [hwnd]
        win32gui.EnumChildWindows(hwnd, lambda child, _: hosts.append(child) or True, 0)
        dialogs = self._uia.CreatePropertyCondition(self._u.UIA_ControlTypePropertyId, self._u.UIA_WindowControlTypeId)
        buttons = self._uia.CreatePropertyCondition(self._u.UIA_ControlTypePropertyId, self._u.UIA_ButtonControlTypeId)
        for host in hosts:
            try:
                dialog = self._uia.ElementFromHandle(host).FindFirst(self._u.TreeScope_Descendants, dialogs)
            except WIN32_ERRORS:
                continue
            if not dialog:
                continue
            found = dialog.FindAll(self._u.TreeScope_Descendants, buttons)
            for index in range(found.Length):
                button = found.GetElement(index)
                if element_name(button).strip().lower() in names:
                    return button
        return None

    def _menus_closed(self, pid: int, timeout_seconds: float) -> bool:
        """Wait until the process shows no menu, as after a pick or an Escape."""
        return bool(
            poll(
                lambda: True if not self._popup_elements(pid, self._u.UIA_MenuItemControlTypeId) else None,
                timeout_seconds,
            )
        )

    def _title_field(self, pid: int) -> IUIAutomationElement | None:
        for field in self._popup_elements(pid, self._u.UIA_EditControlTypeId):
            if _TITLE_FIELD_CLASS_HINT in element_class(field):
                return field
        return None

    def _invoke(self, element: IUIAutomationElement) -> None:
        element.GetCurrentPattern(self._u.UIA_InvokePatternId).QueryInterface(
            self._u.IUIAutomationInvokePattern
        ).Invoke()
