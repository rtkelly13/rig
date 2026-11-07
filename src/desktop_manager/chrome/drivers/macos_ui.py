"""Retained AX elements and Quartz input for foreground browser UI actions.

An AXNode's hash is metadata, never an action handle. This session retains the
actual AXUIElement behind every decoded node and releases it on exit.
"""

from __future__ import annotations

import ctypes as ct
import math
from collections.abc import Callable
from contextlib import suppress
from dataclasses import dataclass
from types import TracebackType
from typing import override

from desktop_manager.chrome.drivers.macos_ax import AccessibilityUnavailableError, AXNode, NativeAX, Point, bind

_CORE_GRAPHICS = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics"
_INVALID_UI_ELEMENT = -25202
_CANNOT_COMPLETE = -25204
_RIGHT_MOUSE = (3, 4)  # kCGEventRightMouseDown, kCGEventRightMouseUp
_ESCAPE_KEY = 53
_HID_EVENT_TAP = 0
_AX_POINT, _AX_SIZE = 1, 2  # kAXValueCGPointType, kAXValueCGSizeType


class InvalidUIElementError(AccessibilityUnavailableError):
    """A retained AX element no longer exists, kAXErrorInvalidUIElement."""


@dataclass(frozen=True, slots=True)
class ScreenPos:
    """A point in global display coordinates."""

    x: float
    y: float


@dataclass(frozen=True, slots=True)
class Focus:
    """The application the user was in, and its focused window if it had one; both retained."""

    app: int
    window: int | None


class NativeUI(NativeAX):
    """Native resources scoped to one context or mutation, never to construction."""

    def __init__(self) -> None:
        super().__init__()
        self.cg: ct.CDLL = ct.CDLL(_CORE_GRAPHICS)
        cf, ax, cg, p = self.cf, self.ax, self.cg, ct.c_void_p
        self.retain: Callable[[int], int] = bind(cf, "CFRetain", p, [p])
        self.equal: Callable[[int, int], bool] = bind(cf, "CFEqual", ct.c_bool, [p, p])
        self.system_wide: Callable[[], int] = bind(ax, "AXUIElementCreateSystemWide", p, [])
        self.element_at: Callable[[int, float, float, object], int] = bind(
            ax, "AXUIElementCopyElementAtPosition", ct.c_int, [p, ct.c_float, ct.c_float, p]
        )
        self.perform: Callable[[int, int], int] = bind(ax, "AXUIElementPerformAction", ct.c_int, [p, p])
        self.set_value: Callable[[int, int, int | None], int] = bind(
            ax, "AXUIElementSetAttributeValue", ct.c_int, [p, p, p]
        )
        self.value_of: Callable[[int, int, object], bool] = bind(ax, "AXValueGetValue", ct.c_bool, [p, ct.c_int, p])
        self.value_type: Callable[[], int] = bind(ax, "AXValueGetTypeID", ct.c_ulong, [])
        self.new_event: Callable[[None], int | None] = bind(cg, "CGEventCreate", p, [p])
        self.location: Callable[[int], Point] = bind(cg, "CGEventGetLocation", Point, [p])
        self.warp: Callable[[Point], int] = bind(cg, "CGWarpMouseCursorPosition", ct.c_int, [Point])
        self.mouse_event: Callable[[None, int, Point, int], int | None] = bind(
            cg, "CGEventCreateMouseEvent", p, [p, ct.c_uint, Point, ct.c_uint]
        )
        self.key_event: Callable[[None, int, bool], int | None] = bind(
            cg, "CGEventCreateKeyboardEvent", p, [p, ct.c_ushort, ct.c_bool]
        )
        self.set_flags: Callable[[int, int], None] = bind(cg, "CGEventSetFlags", None, [p, ct.c_ulonglong])
        self.set_unicode: Callable[[int, int, ct.Array[ct.c_ushort]], None] = bind(
            cg, "CGEventKeyboardSetUnicodeString", None, [p, ct.c_ulong, p]
        )
        self.post: Callable[[int, int], None] = bind(cg, "CGEventPost", None, [ct.c_uint, p])
        self.post_to_pid: Callable[[int, int], None] = bind(cg, "CGEventPostToPid", None, [ct.c_int, p])
        self._handles: dict[int, int] = {}
        self._owned: list[int] = []

    def __enter__(self) -> NativeUI:
        if not self.trusted():
            raise AccessibilityUnavailableError("Grant Accessibility access to the terminal running chrome-tabs")
        return self

    def __exit__(
        self, exc_type: type[BaseException] | None, exc: BaseException | None, traceback: TracebackType | None
    ) -> None:
        for pointer in reversed(self._owned):
            self.release(pointer)
        self._owned.clear()
        self._handles.clear()

    @override
    def node(self, element: int, depth: int) -> AXNode:
        if len(self._owned) >= 100000:
            raise AccessibilityUnavailableError("AX action exceeded its retained reference limit")
        node = super().node(element, depth)
        self.retain(element)
        self._owned.append(element)
        self._handles[id(node)] = element
        return node

    def _handle(self, node: AXNode) -> int:
        pointer = self._handles.get(id(node))
        if pointer is None:
            raise AccessibilityUnavailableError("AX element is not owned by this action session")
        if self.text(pointer, "AXRole") != node.role:
            raise AccessibilityUnavailableError("AX element changed role")
        if (self.text(pointer, "AXTitle") or self.text(pointer, "AXDescription")) != node.name:
            raise AccessibilityUnavailableError("AX element changed name")
        return pointer

    def application_tree(self, pid: int) -> AXNode:
        self.reset_budget()
        app = self.application(pid)
        if not app:
            raise AccessibilityUnavailableError("Chrome AX application is unavailable")
        try:
            self.set_timeout(app, 1.0)
            return self.node(app, 0)
        finally:
            self.release(app)

    def frontmost(self, pid: int) -> bool:
        """Whether this application is frontmost, asked of accessibility (which answers during a menu)."""
        app = self.application(pid)
        if not app:
            return False
        try:
            return self.scalar(app, "AXFrontmost") is True
        finally:
            self.release(app)

    def browser_windows(self, pid: int) -> tuple[AXNode, ...]:
        """Chrome's windows, retained for actions; found even when Chrome is off the current Space."""
        self.reset_budget()
        return self.windows(pid)

    def _action(self, pointer: int, action: str) -> None:
        name = self.cf_string(action)
        try:
            error = self.perform(pointer, name)
            if error == _INVALID_UI_ELEMENT:
                raise InvalidUIElementError(f"AX action {action} failed ({error})")
            if error:
                raise AccessibilityUnavailableError(f"AX action {action} failed ({error})")
        finally:
            self.release(name)

    def press(self, node: AXNode) -> None:
        pointer = self._handle(node)
        if self.scalar(pointer, "AXEnabled") is not True:
            raise AccessibilityUnavailableError("AX control is not enabled")
        self._action(pointer, "AXPress")

    def show_menu(self, node: AXNode) -> None:
        """Open the element's context menu with no mouse.

        A macOS context menu runs a modal loop, so the action does not return until
        it times out: "cannot complete" is the expected reply, not a failure. The
        caller confirms the menu by finding it in the tree.
        """
        pointer = self._handle(node)
        name = self.cf_string("AXShowMenu")
        try:
            self.set_timeout(pointer, 0.5)
            error = self.perform(pointer, name)
            if error == _INVALID_UI_ELEMENT:
                raise InvalidUIElementError(f"AX action AXShowMenu failed ({error})")
            if error not in (0, _CANNOT_COMPLETE):
                raise AccessibilityUnavailableError(f"AX action AXShowMenu failed ({error})")
        finally:
            self.release(name)

    def focused(self, node: AXNode) -> bool:
        return self.scalar(self._handle(node), "AXFocused") is True

    def set_text(self, node: AXNode, text: str, guard: Callable[[], None]) -> None:
        """Type into the empty group editor so Chrome notifies its controller.

        AXSetValue calls Textfield::SetText without ContentsChanged, so it does
        not rename a group. Quartz Unicode input follows the user typing path.
        """
        pointer = self._handle(node)
        if self.text(pointer, "AXValue") != "":
            raise AccessibilityUnavailableError("New group title field is no longer empty")
        for index in range(0, len(text), 8):
            guard()
            if not self.focused(node):
                raise AccessibilityUnavailableError("Group title field lost focus")
            data = text[index : index + 8].encode("utf-16-le")
            characters = (ct.c_ushort * (len(data) // 2)).from_buffer_copy(data)
            for down in (True, False):
                event = self.key_event(None, 0, down)
                if not event:
                    raise AccessibilityUnavailableError("Cannot create Unicode input event")
                self.set_unicode(event, len(characters), characters)
                self._post(event)

    def _point_value(self, pointer: int, attribute: str, kind: int) -> Point:
        value = self.copy(pointer, attribute)
        if value is None:
            raise AccessibilityUnavailableError(f"Missing {attribute}")
        try:
            point = Point()
            if self.type_id(value) != self.value_type() or not self.value_of(value, kind, ct.byref(point)):
                raise AccessibilityUnavailableError(f"Invalid {attribute}")
            return point
        finally:
            self.release(value)

    def click_point(self, node: AXNode) -> ScreenPos:
        """Prove the visible element at the center is this element or its child."""
        pointer = self._handle(node)
        position = self._point_value(pointer, "AXPosition", _AX_POINT)
        size = self._point_value(pointer, "AXSize", _AX_SIZE)
        if size.x <= 0 or size.y <= 0 or not all(math.isfinite(v) for v in (position.x, position.y, size.x, size.y)):
            raise AccessibilityUnavailableError("AX control has no visible rectangle")
        point = ScreenPos(position.x + size.x / 2, position.y + size.y / 2)
        system = self.system_wide()
        hit = ct.c_void_p()
        try:
            error = self.element_at(system, point.x, point.y, ct.byref(hit))
            if error or hit.value is None:
                raise AccessibilityUnavailableError("Cannot identify the control at the click point")
            current: int | None = hit.value
            hit = ct.c_void_p()  # the loop now owns this copied reference
            try:
                for _ in range(12):
                    if current is None:
                        break
                    if self.equal(current, pointer):
                        return point
                    parent = self.copy(current, "AXParent")
                    self.release(current)
                    current = parent
            finally:
                if current:
                    self.release(current)
            raise AccessibilityUnavailableError("A click would land on a different control")
        finally:
            if hit.value:
                self.release(hit.value)
            self.release(system)

    def cursor(self) -> ScreenPos:
        event = self.new_event(None)
        if not event:
            raise AccessibilityUnavailableError("Cannot read cursor position")
        try:
            point = self.location(event)
            return ScreenPos(point.x, point.y)
        finally:
            self.release(event)

    def restore_cursor(self, point: ScreenPos) -> None:
        if self.warp(Point(point.x, point.y)):
            raise AccessibilityUnavailableError("Cannot restore cursor position")

    def right_click(self, point: ScreenPos) -> None:
        for kind in _RIGHT_MOUSE:
            self._post(self.mouse_event(None, kind, Point(point.x, point.y), 1))

    def key(self, code: int) -> None:
        for down in (True, False):
            self._post(self.key_event(None, code, down))

    def escape(self, pid: int) -> None:
        """Dismiss only Chrome's UI when foreground focus has changed."""
        for down in (True, False):
            event = self.key_event(None, _ESCAPE_KEY, down)
            if not event:
                raise AccessibilityUnavailableError("Cannot create Escape event")
            try:
                self.set_flags(event, 0)
                self.post_to_pid(pid, event)
            finally:
                self.release(event)

    def _post(self, event: int | None) -> None:
        if not event:
            raise AccessibilityUnavailableError("Cannot create native input event")
        try:
            self.set_flags(event, 0)
            self.post(_HID_EVENT_TAP, event)
        finally:
            self.release(event)

    def capture_focus(self, frontmost_pid: Callable[[], int | None]) -> Focus:
        """Keep the previous application's actual focused window alive.

        The system-wide query fails (-25204) when the focused app is slow to answer,
        as Electron apps can be; AppKit's frontmost process is the fallback. A
        window that will not say which window has focus is left unrestored.
        """
        app = self._focused_app(frontmost_pid)
        self._owned.append(app)
        try:
            window = self.copy(app, "AXFocusedWindow")
        except AccessibilityUnavailableError:
            window = None
        if window is not None:
            self._owned.append(window)
        return Focus(app, window)

    def _focused_app(self, frontmost_pid: Callable[[], int | None]) -> int:
        system = self.system_wide()
        try:
            app = self.copy(system, "AXFocusedApplication")
        except AccessibilityUnavailableError:
            app = None
        finally:
            self.release(system)
        if app is not None:
            return app
        pid = frontmost_pid()
        app = self.application(pid) if pid is not None else None
        if not app:
            raise AccessibilityUnavailableError("Cannot capture foreground application")
        return app

    def restore_focus(self, focus: Focus) -> None:
        try:
            if focus.window is not None:
                # Moving its last tab may have destroyed the saved window.
                with suppress(InvalidUIElementError):
                    self._action(focus.window, "AXRaise")
        finally:
            self._restore_app(focus.app)

    def _restore_app(self, app: int) -> None:
        key = self.cf_string("AXFrontmost")
        value = ct.c_void_p.in_dll(self.cf, "kCFBooleanTrue")
        try:
            if self.set_value(app, key, value.value):
                raise AccessibilityUnavailableError("Cannot restore foreground application")
        finally:
            self.release(key)
