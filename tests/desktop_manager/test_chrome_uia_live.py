"""Read-only smoke test of the UIA adapter against whatever Chrome is running.

Never closes or selects anything. Skips when no Chrome browser window is open.
"""

from __future__ import annotations

import pytest

pytestmark = [pytest.mark.windows, pytest.mark.integration]


def test_list_windows_reads_real_tab_strips():
    from desktop_manager.chrome.uia import ChromeUia
    from desktop_manager.chrome.window_info import chrome_window_handles

    if not chrome_window_handles():
        pytest.skip("no Chrome window open")
    windows = ChromeUia().list_windows()
    assert windows, "Chrome windows exist but none exposed a tab strip"
    for window in windows:
        assert window.tabs
        assert sum(tab.selected for tab in window.tabs) == 1
        assert [tab.index for tab in window.tabs] == list(range(len(window.tabs)))
        assert len({tab.runtime_id for tab in window.tabs}) == len(window.tabs)
