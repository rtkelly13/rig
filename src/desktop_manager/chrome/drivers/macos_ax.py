"""Small, read-only bridge to macOS accessibility, loaded only when requested.

Copied Core Foundation values are released by their owner. Borrowed AX children
are visited while the owning array remains alive. Web content is never visited.

ctypes declares every foreign call as returning `Any`; each function is bound
once to a typed `Callable` attribute, so call sites are typed without casts.
"""

from __future__ import annotations

import ctypes as ct
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import final

_CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"
_APPLICATION_SERVICES = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices"
_UTF8 = 0x08000100
_NO_VALUE = (-25205, -25212)  # kAXErrorAttributeUnsupported, kAXErrorNoValue
#: Subtrees never read: page content, and the menu bar's own large tree.
SKIPPED_ROLES = ("AXWebArea", "AXMenuBar")


@final
class Point(ct.Structure):
    """A Quartz `CGPoint` (or `CGSize`, which has the same layout)."""

    _fields_ = (("x", ct.c_double), ("y", ct.c_double))
    x: float
    y: float

    def __init__(self, x: float = 0.0, y: float = 0.0) -> None:
        super().__init__()
        self.x = x
        self.y = y


type CType = (
    type[ct.c_void_p | ct.c_ulong | ct.c_long | ct.c_int | ct.c_uint | ct.c_ushort | ct.c_bool]
    | type[ct.c_char_p | ct.c_float | ct.c_ulonglong | Point]
)


def bind(library: ct.CDLL, name: str, restype: CType | None, argtypes: Sequence[CType]):
    """The named foreign function, with its result and argument types set."""
    function = library[name]
    function.restype = restype
    function.argtypes = argtypes
    return function


@dataclass(frozen=True)
class AXNode:
    identity: int = 0
    role: str = ""
    subrole: str = ""
    title: str = ""
    description: str = ""
    identifier: str = ""
    help: str = ""
    selected: bool | None = None
    expanded: bool | None = None
    children: tuple[AXNode, ...] = ()

    @property
    def name(self) -> str:
        return self.title or self.description


class AccessibilityUnavailableError(RuntimeError):
    """Accessibility permission, a stale element, or an incomplete tree."""


class NativeAX:
    """Read an application's chrome through AXUIElement, without prompting."""

    def __init__(self) -> None:
        self.cf: ct.CDLL = ct.CDLL(_CORE_FOUNDATION)
        self.ax: ct.CDLL = ct.CDLL(_APPLICATION_SERVICES)
        cf, ax, p = self.cf, self.ax, ct.c_void_p
        self.release: Callable[[int], None] = bind(cf, "CFRelease", None, [p])
        self.type_id: Callable[[int], int] = bind(cf, "CFGetTypeID", ct.c_ulong, [p])
        self.hash: Callable[[int], int] = bind(cf, "CFHash", ct.c_ulong, [p])
        self.string_type: Callable[[], int] = bind(cf, "CFStringGetTypeID", ct.c_ulong, [])
        self.make_string: Callable[[None, bytes, int], int] = bind(
            cf, "CFStringCreateWithCString", p, [p, ct.c_char_p, ct.c_uint]
        )
        self.string_length: Callable[[int], int] = bind(cf, "CFStringGetLength", ct.c_long, [p])
        self.read_string: Callable[[int, ct.Array[ct.c_char], int, int], bool] = bind(
            cf, "CFStringGetCString", ct.c_bool, [p, p, ct.c_long, ct.c_uint]
        )
        self.array_count: Callable[[int], int] = bind(cf, "CFArrayGetCount", ct.c_long, [p])
        self.array_item: Callable[[int, int], int] = bind(cf, "CFArrayGetValueAtIndex", p, [p, ct.c_long])
        self.boolean_type: Callable[[], int] = bind(cf, "CFBooleanGetTypeID", ct.c_ulong, [])
        self.boolean_value: Callable[[int], bool] = bind(cf, "CFBooleanGetValue", ct.c_bool, [p])
        self.number_type: Callable[[], int] = bind(cf, "CFNumberGetTypeID", ct.c_ulong, [])
        self.number_value: Callable[[int, int, object], bool] = bind(
            cf, "CFNumberGetValue", ct.c_bool, [p, ct.c_int, p]
        )
        self.trusted: Callable[[], bool] = bind(ax, "AXIsProcessTrusted", ct.c_bool, [])
        self.application: Callable[[int], int | None] = bind(ax, "AXUIElementCreateApplication", p, [ct.c_int])
        self.set_timeout: Callable[[int, float], int] = bind(
            ax, "AXUIElementSetMessagingTimeout", ct.c_int, [p, ct.c_float]
        )
        self.copy_value: Callable[[int, int, object], int] = bind(
            ax, "AXUIElementCopyAttributeValue", ct.c_int, [p, p, p]
        )
        self._remaining: int = 5000

    def reset_budget(self) -> None:
        """Allow another full tree read: each read may decode at most 5000 elements."""
        self._remaining = 5000

    def cf_string(self, text: str) -> int:
        """A new CFString the caller releases."""
        return self.make_string(None, text.encode(), _UTF8)

    def copy(self, element: int, attribute: str) -> int | None:
        """A copied attribute value the caller releases, or None when it has none."""
        key = self.cf_string(attribute)
        value = ct.c_void_p()
        try:
            error = self.copy_value(element, key, ct.byref(value))
        finally:
            self.release(key)
        if error in _NO_VALUE:
            return None
        if error:
            raise AccessibilityUnavailableError(f"AX read failed ({error})")
        return value.value

    def scalar(self, element: int, attribute: str) -> str | bool | None:
        value = self.copy(element, attribute)
        if value is None:
            return None
        try:
            kind = self.type_id(value)
            if kind == self.string_type():
                buffer = ct.create_string_buffer(self.string_length(value) * 4 + 1)
                if self.read_string(value, buffer, len(buffer), _UTF8):
                    return ct.string_at(buffer).decode("utf-8")
            if kind == self.boolean_type():
                return self.boolean_value(value)
            if kind == self.number_type():
                number = ct.c_longlong()
                if self.number_value(value, 4, ct.byref(number)):  # kCFNumberSInt64Type
                    return bool(number.value)
            return None
        finally:
            self.release(value)

    def text(self, element: int, attribute: str) -> str:
        value = self.scalar(element, attribute)
        return value if isinstance(value, str) else ""

    def _children(self, element: int, attribute: str, depth: int) -> tuple[AXNode, ...]:
        value = self.copy(element, attribute)
        if value is None:
            return ()
        try:
            return tuple(self.node(self.array_item(value, index), depth) for index in range(self.array_count(value)))
        finally:
            self.release(value)

    def _elements(self, element: int, attributes: tuple[str, ...]) -> tuple[AXNode, ...]:
        """Read single-element attributes, once per distinct element."""
        nodes: list[AXNode] = []
        seen: set[int] = set()
        for attribute in attributes:
            value = self.copy(element, attribute)
            if value is None:
                continue
            try:
                identity = self.hash(value)
                if identity not in seen:
                    seen.add(identity)
                    node = self.node(value, 0)
                    if node.role == "AXWindow":
                        nodes.append(node)
            finally:
                self.release(value)
        return tuple(nodes)

    def node(self, element: int, depth: int) -> AXNode:
        self._remaining -= 1
        if depth > 30 or self._remaining < 0:
            raise AccessibilityUnavailableError("AX chrome tree exceeded its read limit")
        role = self.text(element, "AXRole")
        if role in SKIPPED_ROLES:
            return AXNode(role=role)
        selected = self.scalar(element, "AXValue")
        expanded = self.scalar(element, "AXExpanded")
        return AXNode(
            identity=self.hash(element),
            role=role,
            subrole=self.text(element, "AXSubrole"),
            title=self.text(element, "AXTitle"),
            description=self.text(element, "AXDescription"),
            identifier=self.text(element, "AXIdentifier"),
            help=self.text(element, "AXHelp"),
            selected=selected if isinstance(selected, bool) else None,
            expanded=expanded if isinstance(expanded, bool) else None,
            children=self._children(element, "AXChildren", depth + 1),
        )

    def windows(self, pid: int) -> tuple[AXNode, ...]:
        if not self.trusted():
            raise AccessibilityUnavailableError("Grant Accessibility access to the terminal running chrome-tabs")
        application = self.application(pid)
        if not application:
            raise AccessibilityUnavailableError("Chrome accessibility application is unavailable")
        try:
            self.set_timeout(application, 2.0)
            # Chromium enables native AX on demand when an assistive client
            # requests the application role. AXWindows alone need not do so.
            self.text(application, "AXRole")
            deadline = time.monotonic() + 1.0
            while True:
                windows = self._children(application, "AXWindows", 0)
                if not windows:
                    windows = tuple(
                        child for child in self._children(application, "AXChildren", 0) if child.role == "AXWindow"
                    )
                if not windows:
                    # Off the current Space, or behind another app, AXWindows can
                    # be empty while the main and focused windows still resolve.
                    windows = self._elements(application, ("AXMainWindow", "AXFocusedWindow"))
                if windows or time.monotonic() >= deadline:
                    return windows
                time.sleep(0.1)
        finally:
            self.release(application)


def read_windows(pid: int) -> tuple[AXNode, ...]:
    return NativeAX().windows(pid)
