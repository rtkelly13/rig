"""Integration tests for launching and placing a single workspace entry."""

from __future__ import annotations

from pathlib import Path

from desktop_manager.windows_workspace import (
    find_launch_entry,
    launch_single_entry,
    load_machine_plan_template,
)


def test_find_launch_entry_returns_matching_desktop_and_entry(tmp_path: Path):
    plan = load_machine_plan_template(tmp_path)
    expected_index = next(index for index, desktop in enumerate(plan.desktops, start=1) if desktop.name == "Work")

    location = find_launch_entry(plan, "Work", "Warp Workspace 1")

    assert location.desktop_index == expected_index
    assert location.desktop.name == "Work"
    assert location.entry.name == "Warp Workspace 1"


def test_launch_single_entry_dry_run_reports_action(tmp_path: Path):
    plan = load_machine_plan_template(tmp_path)
    expected_index = next(index for index, desktop in enumerate(plan.desktops, start=1) if desktop.name == "Work")

    actions = launch_single_entry(plan, "Work", "Warp Workspace 1", dry_run=True)

    assert actions[0] == f"Desktop {expected_index}: Work"
    assert any("Warp Workspace 1" in action for action in actions)
