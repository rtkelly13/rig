"""Windows are visited in the order a person scans their screens."""

from __future__ import annotations

import pytest

from desktop_manager.chrome.tabs import ChromeWindow, ScreenPoint, WindowRect, screen_order

pytestmark = pytest.mark.ci


def window(
    hwnd: int, *, desktop: int | None = 1, monitor: tuple[int, int] | None = (0, 0), top_left: tuple[int, int] = (0, 0)
) -> ChromeWindow:
    left, top = top_left
    return ChromeWindow(
        hwnd=hwnd,
        title="w",
        profile_dir="Profile 4",
        profile_name=None,
        age_rank=hwnd,
        minimized=False,
        desktop_number=desktop,
        tabs=(),
        rect=WindowRect(left, top, left + 800, top + 600),
        monitor=ScreenPoint(*monitor) if monitor is not None else None,
    )


def test_current_desktop_first_then_monitor_left_to_right_then_top_to_bottom():
    windows = [
        window(1, desktop=2),
        window(2, monitor=(2560, -1080), top_left=(2560, 0)),
        window(3, monitor=(2560, -1080), top_left=(2560, -1080)),
        window(4, monitor=(0, 0), top_left=(900, 0)),
        window(5, desktop=None),
        window(6, monitor=(0, 0), top_left=(0, 0)),
    ]
    assert [w.hwnd for w in screen_order(windows, current_desktop=1)] == [6, 4, 3, 2, 1, 5]


def test_windows_without_a_known_position_go_last_on_their_desktop():
    windows = [window(1, monitor=None), window(2)]
    assert [w.hwnd for w in screen_order(windows, current_desktop=1)] == [2, 1]
