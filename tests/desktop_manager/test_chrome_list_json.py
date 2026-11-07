from __future__ import annotations

import json

from desktop_manager.chrome.cli import window_json
from desktop_manager.chrome.tabs import ChromeTab, ChromeWindow, ScreenPoint, WindowRect


def _window(*, rect: WindowRect | None, monitor: ScreenPoint | None) -> ChromeWindow:
    tab = ChromeTab(
        runtime_id=(42, 1),
        window_hwnd=1,
        index=0,
        name="Inbox",
        title="Inbox",
        selected=True,
        pinned=False,
        loaded=True,
        audible=False,
    )
    return ChromeWindow(
        hwnd=1,
        title="Inbox - Google Chrome",
        profile_dir="Default",
        profile_name="Ryan",
        age_rank=0,
        minimized=False,
        desktop_number=1,
        tabs=(tab,),
        rect=rect,
        monitor=monitor,
    )


def test_list_json_keeps_geometry_as_arrays():
    window = _window(rect=WindowRect(left=0, top=10, right=800, bottom=600), monitor=ScreenPoint(-1920, 0))

    printed = json.loads(json.dumps(window_json(window, window.tabs)))

    assert printed["rect"] == [0, 10, 800, 600]
    assert printed["monitor"] == [-1920, 0]
    assert printed["tabs"][0]["title"] == "Inbox"


def test_list_json_prints_missing_geometry_as_null():
    window = _window(rect=None, monitor=None)

    printed = json.loads(json.dumps(window_json(window, [])))

    assert printed["rect"] is None
    assert printed["monitor"] is None
    assert printed["tabs"] == []
