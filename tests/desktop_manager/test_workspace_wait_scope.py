from __future__ import annotations

from pathlib import Path

from desktop_manager.models import RestoreRunLogger
from desktop_manager.visualization import DesktopDiff, ScreenDiff, WorkspaceDiff
from desktop_manager.windows_workspace import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WindowSnapshot,
    WorkspacePlan,
)

OK_WORK = DesktopDiff("Work", (ScreenDiff(1),))
OK_ADMIN = DesktopDiff("Admin", (ScreenDiff(1),))
MISSING_TERMINAL_WORK = DesktopDiff(
    "Work", (ScreenDiff(1, planned=("Workspace Terminal 1",), missing=("Workspace Terminal 1",)),)
)


def test_wait_for_restore_success_tracks_success_through_final_check(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.cli.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
        raising=False,
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.diff_workspace",
        lambda plan, actual: WorkspaceDiff((OK_WORK,)),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work")])
    result = _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=None,
        poll_seconds=0.0,
        max_checks=3,
        checks_after_success=3,
    )

    assert result is True
    assert any("Check 1: success" in line for line in fake_console.lines)
    assert fake_console.lines.count("No Change") == 2


def test_wait_for_restore_success_logs_changed_status_for_repeated_diff(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    logger = RestoreRunLogger(run_log_path=tmp_path / "run.log", latest_log_path=tmp_path / "latest.log")
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.diff_workspace",
        lambda plan, actual: WorkspaceDiff((OK_WORK,)),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work")])
    result = _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=logger,
        poll_seconds=0.0,
        max_checks=2,
        checks_after_success=2,
    )

    assert result is True
    assert any("Wait-for-success check 1: success changed=yes" in line for line in logger.lines)
    assert any("Wait-for-success check 2: success changed=no" in line for line in logger.lines)
    assert sum(1 for line in logger.lines if "WAIT Workspace Diff" in line) == 1


def test_wait_for_restore_success_prints_console_safe_diff(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.diff_workspace",
        lambda plan, actual: WorkspaceDiff(
            (DesktopDiff("Work", (ScreenDiff(1, actual=("🤖 Investigating error source [WindowsTerminal]",)),)),)
        ),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work")])
    _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=None,
        poll_seconds=0.0,
        max_checks=1,
        checks_after_success=1,
    )

    assert any("? Investigating error source" in line for line in fake_console.lines)


def test_wait_for_restore_success_detects_regression_after_initial_success(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    diffs = iter(
        [
            WorkspaceDiff((OK_WORK,)),
            WorkspaceDiff((MISSING_TERMINAL_WORK,)),
        ]
    )
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.diff_workspace",
        lambda plan, actual: next(diffs),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work")])
    result = _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=None,
        poll_seconds=0.0,
        max_checks=2,
        checks_after_success=3,
    )

    assert result is False
    assert any("Regression detected after initial success" in line for line in fake_console.lines)


def test_wait_for_restore_success_prints_only_changed_sections_after_first_check(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    diffs = iter(
        [
            WorkspaceDiff((OK_WORK, OK_ADMIN)),
            WorkspaceDiff((MISSING_TERMINAL_WORK, OK_ADMIN)),
        ]
    )
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work"), DesktopPlan(name="Admin")]),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.diff_workspace",
        lambda plan, actual: next(diffs),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work"), DesktopPlan(name="Admin")])
    _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=None,
        poll_seconds=0.0,
        max_checks=2,
        checks_after_success=3,
    )

    second_diff = fake_console.lines[fake_console.lines.index("Check 2: not yet stable") + 1]
    assert "Admin" in fake_console.lines[fake_console.lines.index("Check 1: success") + 1]
    assert second_diff.splitlines() == [
        "Workspace Diff",
        "",
        "Work",
        "Screen 1",
        "  planned: Workspace Terminal 1",
        "  actual:  -",
        "  missing: Workspace Terminal 1",
    ]


def test_wait_for_restore_success_stops_after_configured_checks_post_success(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    render_calls = {"count": 0}

    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(desktops=[DesktopPlan(name="Work")]),
    )

    def fake_diff_workspace(plan, actual):
        _ = (plan, actual)
        render_calls["count"] += 1
        return WorkspaceDiff((OK_WORK,))

    monkeypatch.setattr("desktop_manager.windows_workspace.diff_workspace", fake_diff_workspace)

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work")])
    result = _wait_for_restore_success(
        plan,
        desktop=None,
        screen=None,
        logger=None,
        poll_seconds=0.0,
        max_checks=10,
        checks_after_success=3,
    )

    assert result is True
    assert render_calls["count"] == 4
    assert sum(1 for line in fake_console.lines if line.startswith("Check ")) == 4
    assert fake_console.lines.count("No Change") == 3


def test_current_workspace_status_for_scope_uses_plan_desktop_index_not_actual_desktop_name(monkeypatch):
    from desktop_manager.cli import _current_workspace_status_for_scope

    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status_for_desktop_indexes",
        lambda desktop_indexes: WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Desktop 1",
                    captured_windows=[
                        WindowSnapshot(title="OpenCode", process_name="WindowsTerminal.exe", screen_number=1)
                    ],
                )
            ]
        ),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work"), DesktopPlan(name="Admin")])
    result = _current_workspace_status_for_scope(plan, desktop="Work", screen=1)

    assert [desktop.name for desktop in result.desktops] == ["Desktop 1"]
    assert [window.title for window in result.desktops[0].captured_windows] == ["OpenCode"]


def test_current_workspace_status_for_scope_uses_targeted_desktop_capture(monkeypatch):
    from desktop_manager.cli import _current_workspace_status_for_scope

    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda **_kwargs: (_ for _ in ()).throw(AssertionError("full workspace status should not be used")),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status_for_desktop_indexes",
        lambda desktop_indexes: WorkspacePlan(
            desktops=[DesktopPlan(name=f"Desktop {index}") for index in desktop_indexes]
        ),
    )

    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work"), DesktopPlan(name="Admin")])
    result = _current_workspace_status_for_scope(plan, desktop="Admin", screen=None)

    assert [desktop.name for desktop in result.desktops] == ["Desktop 2"]


def test_filter_plan_to_screen_filters_launches_and_windows(monkeypatch):
    from desktop_manager.cli import _filter_plan_to_screen

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Screen 1 App",
                        type="app",
                        path=r"C:\tool-1.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                    LaunchEntry(
                        name="Screen 2 App",
                        type="app",
                        path=r"C:\tool-2.exe",
                        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                ],
                captured_windows=[
                    WindowSnapshot(title="One", process_name="tool-1.exe", screen_number=1),
                    WindowSnapshot(title="Two", process_name="tool-2.exe", screen_number=2),
                ],
            ),
            DesktopPlan(
                name="Personal",
                launch=[
                    LaunchEntry(
                        name="Personal App",
                        type="app",
                        path=r"C:\tool-3.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    )
                ],
                captured_windows=[WindowSnapshot(title="Three", process_name="tool-3.exe", screen_number=1)],
            ),
        ]
    )

    filtered = _filter_plan_to_screen(plan, desktop="Work", screen=1)

    assert [desktop.name for desktop in filtered.desktops] == ["Work"]
    assert [entry.name for entry in filtered.desktops[0].launch] == ["Screen 1 App"]
    assert [window.title for window in filtered.desktops[0].captured_windows] == ["One"]
