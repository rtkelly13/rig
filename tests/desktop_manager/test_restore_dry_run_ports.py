"""A dry-run restore plans through the injected ports and invokes no command.

Ported from the pytest-bdd scenario `workspace_restore_dry_run.feature`.
"""

from __future__ import annotations

from pathlib import Path

from desktop_manager.commands.workspace_commands import WorkspaceCommands
from desktop_manager.handlers.restore_handler import restore_workspace_with_options
from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow, WorkspacePlan
from desktop_manager.queries.workspace_queries import WorkspaceQueries


class _NoOpQueries(WorkspaceQueries):
    def current_window_handles_to_preserve(self) -> set[int]:
        return set()

    def inventory_reusable_windows(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        return []


class _RecordingCommands(WorkspaceCommands):
    def __init__(self) -> None:
        self.invocations: list[str] = []

    def ensure_windows(self) -> None:
        self.invocations.append("ensure_windows")

    def ensure_desktop_count(self, count: int, settle_seconds: float) -> None:
        self.invocations.append("ensure_desktop_count")


def test_dry_run_renders_planned_actions_without_invoking_commands(tmp_path: Path) -> None:
    editor = LaunchEntry(name="Editor", type="app", path=str(tmp_path / "Code.exe"))
    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[editor])])
    commands = _RecordingCommands()

    actions = restore_workspace_with_options(
        plan, dry_run=True, diff_existing=False, queries=_NoOpQueries(), commands=commands
    )

    assert actions == ["Desktop 1: Work", f"  - app `Editor` -> {editor.path}"]
    assert commands.invocations == []
