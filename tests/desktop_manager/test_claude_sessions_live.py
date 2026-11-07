"""Read-only smoke test of the Windows Terminal UIA adapter against whatever terminal is open.

Never selects, opens or types anything. Skips when no Windows Terminal window is open.
"""

from __future__ import annotations

import pytest

pytestmark = [pytest.mark.windows, pytest.mark.integration]


def test_list_windows_reads_real_tab_strips():
    from desktop_manager.claude_sessions.wt_uia import WindowsTerminalUia, terminal_window_handles

    if not terminal_window_handles():
        pytest.skip("no Windows Terminal window open")
    windows = WindowsTerminalUia().list_windows()
    assert windows
    for window in windows:
        assert window.tabs, f"window {window.hwnd} exposed no tabs"
        assert sum(tab.selected for tab in window.tabs) == 1
        assert len({tab.runtime_id for tab in window.tabs}) == len(window.tabs)
