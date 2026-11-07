"""Unit tests for graceful monitor-shortfall handling.

Cover the pure decision function ``monitor_shortfall`` and the Notepad-note
fallback ``open_monitor_shortfall_note``. Both run on any platform: the Windows
branch is exercised by forcing ``sys.platform`` and stubbing the monitor query, and
the note writer's ``notepad.exe`` launch is stubbed so nothing is spawned.
"""

from __future__ import annotations

from datetime import datetime, timedelta
from pathlib import Path

import pytest

from desktop_manager.models import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WorkspacePlan,
)
from desktop_manager.placement import MonitorShortfall, monitor_shortfall
from desktop_manager.restore_fallbacks import open_monitor_shortfall_note, open_note


def _plan(*screens: int) -> WorkspacePlan:
    launch = [
        LaunchEntry(
            name=f"App{index}",
            type="app",
            path="/usr/bin/app",
            placement=PlacementSpec(screen=screen, layout=PlacementLayout(mode="full", slot=1)),
        )
        for index, screen in enumerate(screens, start=1)
    ]
    return WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=launch)])


@pytest.mark.ci
class TestMonitorShortfall:
    def test_none_on_non_windows(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "linux")
        assert monitor_shortfall(_plan(1, 2, 3)) is None

    def test_shortfall_when_fewer_live_than_required(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "win32")
        monkeypatch.setattr("desktop_manager.win32.geometry.monitor_work_areas", lambda: [(0, 0, 1920, 1080)])
        assert monitor_shortfall(_plan(1, 2, 3)) == MonitorShortfall(required=3, live=1)

    def test_none_when_enough_monitors(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "win32")
        monkeypatch.setattr(
            "desktop_manager.win32.geometry.monitor_work_areas",
            lambda: [(0, 0, 1, 1), (0, 0, 1, 1), (0, 0, 1, 1)],
        )
        assert monitor_shortfall(_plan(1, 2, 3)) is None

    def test_none_for_single_screen_plan(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "win32")
        monkeypatch.setattr("desktop_manager.win32.geometry.monitor_work_areas", lambda: [])
        assert monitor_shortfall(_plan(1)) is None

    def test_none_when_detection_raises(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setattr("sys.platform", "win32")

        def _boom() -> list[tuple[int, int, int, int]]:
            raise OSError("no display")

        monkeypatch.setattr("desktop_manager.win32.geometry.monitor_work_areas", _boom)
        assert monitor_shortfall(_plan(1, 2)) is None


@pytest.mark.ci
class TestMonitorShortfallNote:
    def test_note_written_with_counts_and_launch_suppressed(self, monkeypatch: pytest.MonkeyPatch) -> None:
        calls: list[list[str]] = []
        monkeypatch.setattr(
            "desktop_manager.restore_fallbacks.subprocess.Popen",
            lambda args, **_kwargs: calls.append(args),
        )
        note_path = open_monitor_shortfall_note(required=3, live=1)
        assert isinstance(note_path, Path)
        content = note_path.read_text(encoding="utf-8")
        assert "Workspace restore skipped" in content
        assert "Plan needs 3 screens; only 1 detected." in content
        assert calls and Path(calls[0][0]).name == "notepad.exe"

    def test_note_is_not_reopened_when_already_written_since(self, monkeypatch: pytest.MonkeyPatch) -> None:
        calls: list[list[str]] = []
        monkeypatch.setattr(
            "desktop_manager.restore_fallbacks.subprocess.Popen",
            lambda args, **_kwargs: calls.append(args),
        )
        before = datetime.now().astimezone() - timedelta(seconds=5)
        open_note("quiet-since-test.txt", ["one"], quiet_since=before)
        note_path = open_note("quiet-since-test.txt", ["two"], quiet_since=before)

        assert len(calls) == 1
        assert note_path.read_text(encoding="utf-8") == "two\n"

    def test_note_opens_again_when_written_before_quiet_since(self, monkeypatch: pytest.MonkeyPatch) -> None:
        calls: list[list[str]] = []
        monkeypatch.setattr(
            "desktop_manager.restore_fallbacks.subprocess.Popen",
            lambda args, **_kwargs: calls.append(args),
        )
        open_note("quiet-since-test.txt", ["one"])
        open_note("quiet-since-test.txt", ["two"], quiet_since=datetime.now().astimezone() + timedelta(minutes=1))

        assert len(calls) == 2
