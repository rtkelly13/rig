"""Unit tests for RestoreExecutionPlan.render_actions().

These tests exercise the pure rendering logic in restore_models.py —
no OS, filesystem, or Windows API dependencies.
"""

from __future__ import annotations

from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow
from desktop_manager.restore_models import PlannedDesktopExecution, PlannedEntryExecution, RestoreExecutionPlan

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _app_entry(name: str = "MyApp", path: str = r"C:\App.exe") -> LaunchEntry:
    return LaunchEntry(name=name, type="app", path=path)


def _desktop(name: str = "Work") -> DesktopPlan:
    return DesktopPlan(name=name)


def _launch_step(entry: LaunchEntry, desktop: DesktopPlan, desktop_index: int = 1) -> PlannedEntryExecution:
    return PlannedEntryExecution(
        desktop=desktop,
        desktop_index=desktop_index,
        entry=entry,
        description=f"app `{entry.name}` -> {entry.path}",
        action_kind="launch",
    )


def _reuse_step(
    entry: LaunchEntry,
    desktop: DesktopPlan,
    window: LiveWindow,
    desktop_index: int = 1,
) -> PlannedEntryExecution:
    return PlannedEntryExecution(
        desktop=desktop,
        desktop_index=desktop_index,
        entry=entry,
        description=f"app `{entry.name}` -> {entry.path}",
        action_kind="reuse",
        reusable_window=window,
    )


# ---------------------------------------------------------------------------
# Empty plan
# ---------------------------------------------------------------------------


class TestRenderActionsEmptyPlan:
    def test_empty_desktops_returns_empty_list(self) -> None:
        plan = RestoreExecutionPlan(desktops=[])
        assert plan.render_actions() == []

    def test_desktop_with_no_entries_renders_header_only(self) -> None:
        desktop = _desktop("Work")
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=False,
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        assert plan.render_actions() == ["Desktop 1: Work"]


# ---------------------------------------------------------------------------
# Desktop header
# ---------------------------------------------------------------------------


class TestRenderActionsDesktopHeader:
    def test_header_uses_desktop_index_and_name(self) -> None:
        desktop = _desktop("Admin")
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=3,
            close_existing=False,
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        assert plan.render_actions()[0] == "Desktop 3: Admin"

    def test_multiple_desktops_each_get_header(self) -> None:
        desktops = [
            PlannedDesktopExecution(desktop=_desktop("Work"), desktop_index=1, close_existing=False),
            PlannedDesktopExecution(desktop=_desktop("Personal"), desktop_index=2, close_existing=False),
        ]
        plan = RestoreExecutionPlan(desktops=desktops)
        actions = plan.render_actions()
        assert actions[0] == "Desktop 1: Work"
        assert actions[1] == "Desktop 2: Personal"


# ---------------------------------------------------------------------------
# Close-existing line
# ---------------------------------------------------------------------------


class TestRenderActionsCloseExisting:
    def test_close_existing_adds_line_after_header(self) -> None:
        desktop = _desktop("Work")
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=True,
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert "  - close existing windows on this desktop" in actions

    def test_no_close_existing_omits_close_line(self) -> None:
        desktop = _desktop("Work")
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=False,
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        assert "  - close existing windows on this desktop" not in plan.render_actions()


# ---------------------------------------------------------------------------
# Launch actions
# ---------------------------------------------------------------------------


class TestRenderActionsLaunchEntry:
    def test_launch_action_uses_description(self) -> None:
        entry = _app_entry("VSCode", r"C:\Code.exe")
        desktop = _desktop()
        step = _launch_step(entry, desktop)
        desktop_step = PlannedDesktopExecution(
            desktop=desktop, desktop_index=1, close_existing=False, entry_steps=[step]
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert r"  - app `VSCode` -> C:\Code.exe" in actions

    def test_multiple_launch_entries_all_rendered(self) -> None:
        desktop = _desktop()
        entry1 = _app_entry("App1", r"C:\App1.exe")
        entry2 = _app_entry("App2", r"C:\App2.exe")
        steps = [_launch_step(entry1, desktop), _launch_step(entry2, desktop)]
        desktop_step = PlannedDesktopExecution(
            desktop=desktop, desktop_index=1, close_existing=False, entry_steps=steps
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert r"  - app `App1` -> C:\App1.exe" in actions
        assert r"  - app `App2` -> C:\App2.exe" in actions


# ---------------------------------------------------------------------------
# Reuse actions
# ---------------------------------------------------------------------------


class TestRenderActionsReuseEntry:
    def test_reuse_without_desktop_number(self) -> None:
        entry = _app_entry("Slack")
        desktop = _desktop()
        window = LiveWindow(hwnd=10, title="Slack", process_name="slack.exe")
        step = _reuse_step(entry, desktop, window)
        desktop_step = PlannedDesktopExecution(
            desktop=desktop, desktop_index=1, close_existing=False, entry_steps=[step]
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert "  - reuse window `Slack`" in actions
        assert "from desktop" not in " ".join(actions)

    def test_reuse_with_desktop_number_appended(self) -> None:
        entry = _app_entry("Slack")
        desktop = _desktop()
        window = LiveWindow(hwnd=10, title="Slack", process_name="slack.exe", desktop_number=3)
        step = _reuse_step(entry, desktop, window)
        desktop_step = PlannedDesktopExecution(
            desktop=desktop, desktop_index=1, close_existing=False, entry_steps=[step]
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert "  - reuse window `Slack` from desktop 3" in actions

    def test_reuse_does_not_include_description(self) -> None:
        entry = _app_entry("Slack", r"C:\Slack.exe")
        desktop = _desktop()
        window = LiveWindow(hwnd=10, title="Slack", process_name="slack.exe", desktop_number=1)
        step = _reuse_step(entry, desktop, window)
        desktop_step = PlannedDesktopExecution(
            desktop=desktop, desktop_index=1, close_existing=False, entry_steps=[step]
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert not any(r"C:\Slack.exe" in a for a in actions)


# ---------------------------------------------------------------------------
# Orphan window actions
# ---------------------------------------------------------------------------


class TestRenderActionsOrphanWindows:
    def _make_plan(self, window: LiveWindow) -> RestoreExecutionPlan:
        desktop = _desktop()
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=False,
            orphan_windows=[window],
        )
        return RestoreExecutionPlan(desktops=[desktop_step])

    def test_orphan_title_only(self) -> None:
        window = LiveWindow(hwnd=99, title="Unknown Window")
        plan = self._make_plan(window)
        actions = plan.render_actions()
        assert "  - orphan open window `Unknown Window`" in actions

    def test_orphan_with_process_name(self) -> None:
        window = LiveWindow(hwnd=99, title="Unknown Window", process_name="foo.exe")
        plan = self._make_plan(window)
        actions = plan.render_actions()
        assert "  - orphan open window `Unknown Window [foo.exe]`" in actions

    def test_orphan_with_desktop_number(self) -> None:
        window = LiveWindow(hwnd=99, title="Orphan", desktop_number=2)
        plan = self._make_plan(window)
        actions = plan.render_actions()
        assert "  - orphan open window `Orphan` on desktop 2" in actions

    def test_orphan_with_screen_number(self) -> None:
        window = LiveWindow(hwnd=99, title="Orphan", screen_number=1)
        plan = self._make_plan(window)
        actions = plan.render_actions()
        assert "  - orphan open window `Orphan` screen 1" in actions

    def test_orphan_with_all_fields(self) -> None:
        window = LiveWindow(hwnd=99, title="Orphan", process_name="bar.exe", desktop_number=3, screen_number=2)
        plan = self._make_plan(window)
        actions = plan.render_actions()
        assert "  - orphan open window `Orphan [bar.exe]` on desktop 3 screen 2" in actions

    def test_multiple_orphans_all_rendered(self) -> None:
        desktop = _desktop()
        orphans = [
            LiveWindow(hwnd=1, title="Window A"),
            LiveWindow(hwnd=2, title="Window B"),
        ]
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=False,
            orphan_windows=orphans,
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert any("Window A" in a for a in actions)
        assert any("Window B" in a for a in actions)


# ---------------------------------------------------------------------------
# Mixed scenarios
# ---------------------------------------------------------------------------


class TestRenderActionsMixedScenarios:
    def test_launch_and_reuse_and_orphan_all_present(self) -> None:
        desktop = _desktop("Work")
        entry_launch = _app_entry("Notepad", r"C:\Notepad.exe")
        entry_reuse = _app_entry("Slack", r"C:\Slack.exe")
        reuse_window = LiveWindow(hwnd=5, title="Slack", process_name="slack.exe", desktop_number=1)
        orphan_window = LiveWindow(hwnd=9, title="Stray Window")

        entry_steps = [
            _launch_step(entry_launch, desktop),
            _reuse_step(entry_reuse, desktop, reuse_window),
        ]
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=False,
            entry_steps=entry_steps,
            orphan_windows=[orphan_window],
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()

        assert "Desktop 1: Work" in actions
        assert r"  - app `Notepad` -> C:\Notepad.exe" in actions
        assert "  - reuse window `Slack` from desktop 1" in actions
        assert "  - orphan open window `Stray Window`" in actions

    def test_close_existing_with_launch_action(self) -> None:
        desktop = _desktop()
        entry = _app_entry("MyApp")
        step = _launch_step(entry, desktop)
        desktop_step = PlannedDesktopExecution(
            desktop=desktop,
            desktop_index=1,
            close_existing=True,
            entry_steps=[step],
        )
        plan = RestoreExecutionPlan(desktops=[desktop_step])
        actions = plan.render_actions()
        assert actions[1] == "  - close existing windows on this desktop"

    def test_two_desktops_with_entries(self) -> None:
        d1 = _desktop("Work")
        d2 = _desktop("Admin")
        e1 = _app_entry("VSCode")
        e2 = _app_entry("Outlook")

        plan = RestoreExecutionPlan(
            desktops=[
                PlannedDesktopExecution(
                    desktop=d1,
                    desktop_index=1,
                    close_existing=False,
                    entry_steps=[_launch_step(e1, d1, 1)],
                ),
                PlannedDesktopExecution(
                    desktop=d2,
                    desktop_index=2,
                    close_existing=False,
                    entry_steps=[_launch_step(e2, d2, 2)],
                ),
            ]
        )
        actions = plan.render_actions()
        assert actions[0] == "Desktop 1: Work"
        assert actions[2] == "Desktop 2: Admin"

    def test_target_desktop_and_index_not_rendered(self) -> None:
        """target_desktop / target_desktop_index are metadata — not in render output."""
        desktop = _desktop("Work")
        plan = RestoreExecutionPlan(
            desktops=[PlannedDesktopExecution(desktop=desktop, desktop_index=1, close_existing=False)],
            target_desktop="Work",
            target_desktop_index=1,
        )
        actions = plan.render_actions()
        assert len(actions) == 1
        assert actions[0] == "Desktop 1: Work"
