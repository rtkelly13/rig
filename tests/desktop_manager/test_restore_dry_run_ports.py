"""Restore reaches the machine only through the injected ``DesktopPort``.

The dry-run case was ported from the pytest-bdd scenario `workspace_restore_dry_run.feature`.
"""

from __future__ import annotations

from pathlib import Path

from desktop_manager.desktop_port import Win32DesktopPort
from desktop_manager.handlers import restore_steps
from desktop_manager.handlers.restore_handler import restore_workspace_with_options
from desktop_manager.models import DesktopPlan, LaunchEntry, LiveWindow, WorkspacePlan
from desktop_manager.restore_models import PlannedEntryExecution
from desktop_manager.window_reuse import WindowMove


class _RecordingPort(Win32DesktopPort):
    """Answers every read restore makes here, and records every call instead of touching the machine."""

    def __init__(self) -> None:
        self.invocations: list[str] = []

    def current_window_handles_to_preserve(self) -> set[int]:
        return set()

    def inventory_reusable_windows(self, *, excluded_hwnds: set[int]) -> list[LiveWindow]:
        return []

    def ensure_windows(self) -> None:
        self.invocations.append("ensure_windows")

    def ensure_desktop_count(self, count: int, settle_seconds: float) -> None:
        self.invocations.append("ensure_desktop_count")

    def move_existing_window_to_entry(self, window: LiveWindow, desktop_index: int, entry: LaunchEntry) -> WindowMove:
        self.invocations.append(f"move_existing_window_to_entry {window.hwnd}")
        raise RuntimeError("window vanished")

    def collect_windows_matching_process(self, process_name: str) -> set[int]:
        self.invocations.append(f"collect_windows_matching_process {process_name}")
        return {7}

    def launch_entry(
        self, entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None
    ) -> int | None:
        self.invocations.append(f"launch_entry {entry.name}")
        return 4242

    def sleep(self, seconds: float) -> None:
        self.invocations.append(f"sleep {seconds}")


def test_dry_run_renders_planned_actions_without_invoking_commands(tmp_path: Path) -> None:
    editor = LaunchEntry(name="Editor", type="app", path=str(tmp_path / "Code.exe"))
    plan = WorkspacePlan(desktops=[DesktopPlan(name="Work", launch=[editor])])
    port = _RecordingPort()

    actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=False, port=port)

    assert actions == ["Desktop 1: Work", f"  - app `Editor` -> {editor.path}"]
    assert port.invocations == []


def test_failed_reuse_relaunches_through_the_injected_port(tmp_path: Path) -> None:
    """The relaunch after a failed reuse used to build a fresh real-machine query adapter."""
    editor = LaunchEntry(
        name="Editor", type="app", path=str(tmp_path / "Code.exe"), window_process_name="Code.exe", delay_ms=250
    )
    desktop = DesktopPlan(name="Work", launch=[editor])
    plan = WorkspacePlan(desktops=[desktop])
    step = PlannedEntryExecution(
        desktop=desktop,
        desktop_index=1,
        entry=editor,
        description="app `Editor`",
        action_kind="reuse",
        reusable_window=LiveWindow(hwnd=99, title="Editor", process_name="Code.exe", desktop_number=2),
    )
    port = _RecordingPort()

    pending = restore_steps._execute_reuse_step(step, logger=None, port=port, plan=plan)

    assert pending == []
    assert port.invocations == [
        "move_existing_window_to_entry 99",
        "collect_windows_matching_process Code.exe",
        "launch_entry Editor",
        "sleep 0.25",
    ]
