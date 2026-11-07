"""Integration tests for workspace restore orchestration across virtual desktops."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

from desktop_manager.models import DesktopPlan, PlacementOutcome, WindowMatch
from desktop_manager.window_reuse import WindowMove
from desktop_manager.windows_workspace import (
    LiveWindow,
    WindowSnapshot,
    build_sample_plan,
    create_restore_run_logger,
    restore_workspace_with_options,
)


def test_restore_workspace_dry_run_reports_actions(tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    monkeypatch = pytest.MonkeyPatch()
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    try:
        actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=True, logger=logger)
    finally:
        monkeypatch.undo()

    assert actions[0] == "Desktop 1: Work"
    assert any(action.startswith("  - terminal `Workspace Terminal 1`") for action in actions)
    assert any("Obsidian.exe" in action for action in actions)
    assert logger.run_log_path.exists()
    assert "Starting restore dry_run=True" in logger.run_log_path.read_text(encoding="utf-8")


def test_restore_workspace_logs_post_entry_state(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work", captured_windows=[]),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    restore_workspace_with_options(plan, dry_run=False, diff_existing=False, logger=logger, target_desktop="Work")

    log_text = logger.run_log_path.read_text(encoding="utf-8")
    assert "Post-entry status for Workspace Terminal 1" in log_text
    assert "STATE Work" in log_text


def test_restore_names_desktops_and_retires_switched_off_ones(monkeypatch, tmp_path: Path, fake_desktop_navigation):
    from desktop_manager.plan import apply_feature_flags

    plan = build_sample_plan(tmp_path, terminal_count=1)
    plan.features = {"personal-desktop": False}
    plan.desktops.append(DesktopPlan(name="Personal", feature="personal-desktop"))
    plan = apply_feature_flags(plan)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work", captured_windows=[]),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )

    restore_workspace_with_options(plan, dry_run=False, diff_existing=False, logger=logger)

    assert fake_desktop_navigation["rename_desktop"] == [(1, "Work"), (2, "Admin")]
    assert fake_desktop_navigation["retire_desktops"] == [(2, ["Personal"])]


def test_restore_workspace_reanchors_before_post_entry_status_when_desktop_drifts(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    current_desktop = {"value": 1}
    status_desktops: list[int | None] = []
    ensure_calls: list[int] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current_desktop["value"])
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)

    def fake_ensure_current_desktop_number(desktop_index, **_kwargs):
        ensure_calls.append(desktop_index)
        current_desktop["value"] = desktop_index
        return True

    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_current_desktop_number", fake_ensure_current_desktop_number
    )

    def fake_current_desktop_status(desktop_name=None):
        status_desktops.append(current_desktop["value"])
        return DesktopPlan(name=desktop_name or "Work", captured_windows=[])

    monkeypatch.setattr("desktop_manager.restore.current_desktop_status", fake_current_desktop_status)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )

    def fake_apply_window_placement(*_args, **_kwargs):
        current_desktop["value"] = 2
        return PlacementOutcome(placed=True)

    monkeypatch.setattr("desktop_manager.win32.entry_windows.apply_window_placement", fake_apply_window_placement)
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    restore_workspace_with_options(plan, dry_run=False, diff_existing=False, logger=logger, target_desktop="Work")

    assert status_desktops
    assert all(item == 1 for item in status_desktops)
    assert ensure_calls[-1] == 1
    log_text = logger.run_log_path.read_text(encoding="utf-8")
    assert "Desktop drift detected before post-entry status for Workspace Terminal 1" in log_text


def test_restore_workspace_retries_desktop_when_completion_check_fails(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)
    logger = create_restore_run_logger(tmp_path / "workspace.json")

    current_desktop = {"value": 1}
    launches: list[str] = []
    status_calls = {"count": 0}

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: current_desktop["value"])
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", lambda _steps: None)
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_current_desktop_number",
        lambda desktop_index, **_kwargs: current_desktop.__setitem__("value", desktop_index) or True,
    )

    def fake_inventory_reusable_windows(**_kwargs):
        if launches:
            return [
                LiveWindow(
                    hwnd=101,
                    title="OpenCode",
                    process_name="WindowsTerminal.exe",
                    desktop_number=1,
                    screen_number=1,
                    inferred_placement=plan.desktops[0].launch[0].placement,
                )
            ]
        return []

    def fake_move_vanished_window(*_args: object, **_kwargs: object) -> None:
        raise RuntimeError("window 101 vanished before it could be moved")

    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", fake_inventory_reusable_windows)
    monkeypatch.setattr("desktop_manager.window_reuse.move_existing_window_to_entry", fake_move_vanished_window)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr(
        "desktop_manager.launching.launch_entry", lambda entry, *_args, **_kwargs: launches.append(entry.name)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )

    def fake_current_desktop_status(desktop_name=None):
        status_calls["count"] += 1
        if status_calls["count"] <= 3:
            return DesktopPlan(name=desktop_name or "Work", captured_windows=[])
        return DesktopPlan(
            name=desktop_name or "Work",
            captured_windows=[WindowSnapshot(title="OpenCode", process_name="WindowsTerminal.exe", screen_number=2)],
        )

    monkeypatch.setattr("desktop_manager.restore.current_desktop_status", fake_current_desktop_status)
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(
        plan, dry_run=False, diff_existing=True, logger=logger, target_desktop="Work"
    )

    assert any(action.startswith("  - terminal `Workspace Terminal 1`") for action in actions)
    assert launches == ["Workspace Terminal 1", "Workspace Terminal 1"]
    log_text = logger.run_log_path.read_text(encoding="utf-8")
    assert "Desktop 1 completion check failed on attempt 1" in log_text
    assert "Retrying desktop 1: attempt 2 of 3" in log_text
    assert "Desktop 1 completion check passed on attempt 2" in log_text
    assert "window 101 vanished before it could be moved" in log_text


@pytest.mark.windows
def test_restore_workspace_with_close_dry_run_reports_close_action(tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    actions = restore_workspace_with_options(plan, dry_run=True, close_existing=True)

    assert actions[0] == "Desktop 1: Work"
    assert actions[1] == "  - close existing windows on this desktop"


@pytest.mark.windows
def test_restore_workspace_with_diff_dry_run_reports_reuse(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows",
        lambda **_kwargs: [
            LiveWindow(
                hwnd=101,
                title="Workspace Terminal 1",
                process_name="WindowsTerminal.exe",
                desktop_number=3,
            )
        ],
    )

    actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=True)

    assert actions[0] == "Desktop 1: Work"
    assert actions[1].startswith("  - terminal `Workspace Terminal 1`")


def test_restore_workspace_diff_dry_run_refreshes_inventory_per_desktop(monkeypatch, tmp_path: Path):
    from desktop_manager.windows_workspace import (
        DesktopPlan,
        LaunchEntry,
        PlacementLayout,
        PlacementSpec,
        WorkspacePlan,
    )

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Workspace Terminal 1",
                        type="terminal",
                        command="opencode",
                        cwd="{workspace}",
                        shell="wt-pwsh",
                        window_title="Workspace Terminal 1",
                        window_process_name="WindowsTerminal.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
                    )
                ],
            ),
            DesktopPlan(
                name="Personal",
                launch=[
                    LaunchEntry(
                        name="Personal Terminal 1",
                        type="terminal",
                        command="opencode",
                        cwd="{personal-workspace}",
                        shell="wt-pwsh",
                        window_title="Personal Terminal 1",
                        window_process_name="WindowsTerminal.exe",
                        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
                    )
                ],
            ),
        ],
        workspace=str(tmp_path),
        personal_workspace=str(tmp_path),
    )

    inventory_calls = {"count": 0}

    def fake_inventory_reusable_windows(**_kwargs):
        inventory_calls["count"] += 1
        if inventory_calls["count"] == 1:
            return [
                LiveWindow(
                    hwnd=101,
                    title="OpenCode",
                    process_name="WindowsTerminal.exe",
                    desktop_number=1,
                    screen_number=1,
                    inferred_placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="thirds", slot=1)),
                )
            ]
        return [
            LiveWindow(
                hwnd=202,
                title="OpenCode",
                process_name="WindowsTerminal.exe",
                desktop_number=2,
                screen_number=2,
                inferred_placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
            )
        ]

    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", fake_inventory_reusable_windows)

    actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=True)

    assert inventory_calls["count"] == 2
    assert any(action.startswith("  - reuse window `Workspace Terminal 1`") for action in actions)
    assert any(action.startswith("  - reuse window `Personal Terminal 1`") for action in actions)


@pytest.mark.windows
def test_restore_workspace_diff_dry_run_reports_orphan_open_windows(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows",
        lambda **_kwargs: [
            LiveWindow(
                hwnd=777,
                title="Unrelated Notes",
                process_name="Obsidian.exe",
                desktop_number=1,
                screen_number=3,
            )
        ],
    )

    actions = restore_workspace_with_options(plan, dry_run=True, diff_existing=True)

    assert any(action.startswith("  - orphan open window `Unrelated Notes [Obsidian.exe]`") for action in actions)


def test_restore_workspace_opens_deduplicated_missing_app_fallback(monkeypatch, tmp_path: Path):
    from desktop_manager.windows_workspace import (
        DesktopPlan,
        LaunchEntry,
        PlacementLayout,
        PlacementSpec,
        WorkspacePlan,
    )

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Missing App 1",
                        type="app",
                        path=r"C:\missing\app.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                    LaunchEntry(
                        name="Missing App 2",
                        type="app",
                        path=r"C:\missing\app.exe",
                        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
                    ),
                ],
            )
        ]
    )

    launched: list[tuple[str, tuple[str, ...]]] = []
    placed: list[tuple[str, int | None]] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work"),
    )

    def fake_launch_entry(entry, _subs):
        launched.append((entry.path or "", tuple(entry.args)))
        if entry.path == r"C:\missing\app.exe":
            raise ValueError(f"Launch entry `{entry.name}` resolved path does not exist: C:\\missing\\app.exe")

    monkeypatch.setattr("desktop_manager.launching.launch_entry", fake_launch_entry)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda entry, **kwargs: (
            placed.append((entry.name, kwargs.get("target_desktop_number"))) or PlacementOutcome(placed=True)
        ),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=False)

    assert sum(1 for path, _args in launched if path == "notepad.exe") == 1
    assert any(action.startswith("Fallback note `Restore Error - Missing App 1`") for action in actions)
    assert ("Restore Error - Missing App 1", 1) in placed
    note_args = next(args for path, args in launched if path == "notepad.exe")
    note_text = Path(note_args[0]).read_text(encoding="utf-8")
    assert "Affected windows:" in note_text
    assert "- Missing App 1" in note_text
    assert "- Missing App 2" in note_text


def test_restore_workspace_missing_arg_path_does_not_open_fallback(monkeypatch, tmp_path: Path):
    from desktop_manager.windows_workspace import DesktopPlan, LaunchEntry, WorkspacePlan

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Personal",
                launch=[
                    LaunchEntry(
                        name="Obsidian Vault",
                        type="app",
                        path=r"C:\Users\someone\AppData\Local\Programs\Obsidian\Obsidian.exe",
                        args=[r"C:\code\personal\obsidian-vault\Vault"],
                    )
                ],
            )
        ]
    )

    launched: list[str] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Personal"),
    )

    def fake_launch_entry(entry, _subs):
        launched.append(entry.path or "")
        raise ValueError(
            "Launch entry `Obsidian Vault` resolved arg does not exist: C:\\code\\personal\\obsidian-vault\\Vault"
        )

    monkeypatch.setattr("desktop_manager.launching.launch_entry", fake_launch_entry)
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=False)

    assert launched == [r"C:\Users\someone\AppData\Local\Programs\Obsidian\Obsidian.exe"]
    assert not any(action.startswith("Fallback note `") for action in actions)


@pytest.mark.skipif(sys.platform != "win32", reason="requires win32 desktop APIs")
def test_restore_workspace_returns_to_starting_desktop(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    desktop_calls: list[int] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 3)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr(
        "desktop_manager.win32.desktops.ensure_current_desktop_number",
        lambda desktop_index, **_kwargs: desktop_calls.append(desktop_index) or True,
    )
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", lambda _steps: None)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None, **_kwargs: DesktopPlan(name=desktop_name or "Work", captured_windows=[]),
    )
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=True)

    assert desktop_calls[-1] == 3
    assert actions[-1] == "Return to starting desktop 3"


@pytest.mark.skipif(sys.platform != "win32", reason="requires win32gui for window handle preservation")
def test_restore_workspace_falls_back_to_launch_when_reuse_move_fails(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)
    first_entry = plan.desktops[0].launch[0]

    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows",
        lambda **_kwargs: [
            LiveWindow(
                hwnd=101,
                title="Workspace Terminal 1",
                process_name="WindowsTerminal.exe",
                desktop_number=1,
            )
        ],
    )
    monkeypatch.setattr(
        "desktop_manager.window_reuse.match_reusable_window",
        lambda entry, _windows, _substitutions, **_kwargs: LiveWindow(
            hwnd=101,
            title=entry.window_title or entry.name,
            process_name=entry.window_process_name,
            desktop_number=1,
        ),
    )
    monkeypatch.setattr(
        "desktop_manager.window_reuse.move_existing_window_to_entry",
        lambda _live_window, _desktop_index, entry: (_ for _ in ()).throw(RuntimeError(f"failed to move {entry.name}")),
    )
    launched: list[str] = []
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda entry, _subs: launched.append(entry.name))
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: first_entry.name
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=True)

    assert any(first_entry.name in action for action in actions)
    assert launched[0] == "Workspace Terminal 1"


def test_restore_workspace_retains_invoking_window(monkeypatch, tmp_path: Path):
    # The invoking window is the live Claude Code session that launched the
    # restore.  It must be retained in place — not claimed as a workspace
    # terminal slot and relocated — so the running session survives.  We model
    # an idle WindowsTerminal that *looks* like a terminal entry but is the
    # invoking session, and assert it is excluded from the reusable inventory
    # and never moved, with the slot launched fresh instead.
    plan = build_sample_plan(tmp_path, terminal_count=1)

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 3)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", lambda: {918290})

    inventory_excluded: list[set[int]] = []

    def fake_inventory_reusable_windows(*, excluded_hwnds=None):
        excluded = set(excluded_hwnds or set())
        inventory_excluded.append(excluded)
        windows = [
            LiveWindow(
                hwnd=918290,
                title="Workspace Terminal 1",
                process_name="WindowsTerminal.exe",
                desktop_number=1,
                screen_number=1,
            )
        ]
        return [window for window in windows if window.hwnd not in excluded]

    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", fake_inventory_reusable_windows)
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.move_right", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(
            name=desktop_name or "Work",
            captured_windows=[
                WindowSnapshot(title="Workspace Terminal 1", process_name="WindowsTerminal.exe", screen_number=2),
            ],
        ),
    )

    moved: list[int] = []

    monkeypatch.setattr(
        "desktop_manager.window_reuse.move_existing_window_to_entry",
        lambda live_window, *_args, **_kwargs: moved.append(live_window.hwnd) or WindowMove(False, False),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: [918290]
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.raise_window_to_front", lambda *_args, **_kwargs: None)

    launched: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.launching.launch_entry", lambda entry, *_args, **_kwargs: launched.append(entry.name)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "Claude"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=True, target_desktop="Work")

    # The invoking window's handle is excluded from the reusable inventory...
    assert inventory_excluded == [{918290}]
    # ...so it is never claimed/moved, and the slot is launched fresh instead.
    assert moved == []
    assert launched == ["Workspace Terminal 1"]
    assert not any(action.startswith("  - reuse window `Workspace Terminal 1`") for action in actions)


def test_restore_workspace_polls_for_wt_terminal_readiness(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    readiness_calls: list[tuple[str, int | None, tuple[int, ...], bool | None]] = []
    placement_calls: list[int | None] = []

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [])
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work"),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process",
        lambda *_args, **_kwargs: [91],
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.raise_window_to_front", lambda *_args, **_kwargs: None)
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry",
        lambda entry, **kwargs: (
            readiness_calls.append(
                (
                    entry.name,
                    kwargs.get("target_desktop_number"),
                    tuple(sorted(kwargs.get("excluded_hwnds") or set())),
                    kwargs.get("allow_process_fallback_for_terminals"),
                )
            )
            or WindowMatch(hwnd=123)
        ),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **kwargs: placement_calls.append(kwargs.get("hwnd")) or PlacementOutcome(placed=True),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    restore_workspace_with_options(plan, dry_run=False, diff_existing=False, target_desktop="Work")

    assert readiness_calls
    assert all(call == ("Workspace Terminal 1", 1, (91,), True) for call in readiness_calls)
    assert placement_calls == [123] * len(readiness_calls)


def test_restore_workspace_reuses_wt_terminals(monkeypatch, tmp_path: Path):
    plan = build_sample_plan(tmp_path, terminal_count=1)

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.inventory_reusable_windows",
        lambda **_kwargs: [
            LiveWindow(
                hwnd=101,
                title="Workspace Terminal 1",
                process_name="WindowsTerminal.exe",
                desktop_number=1,
            )
        ],
    )
    monkeypatch.setattr("desktop_manager.win32.desktops.switch_leftmost", lambda _steps: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.win32.inventory.collect_windows_matching_process", lambda *_args, **_kwargs: []
    )
    monkeypatch.setattr("desktop_manager.win32.geometry.raise_window_to_front", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work"),
    )

    moved: list[tuple[int, int, str]] = []

    monkeypatch.setattr(
        "desktop_manager.window_reuse.move_existing_window_to_entry",
        lambda live_window, desktop_index, entry: (
            moved.append((live_window.hwnd, desktop_index, entry.name)) or WindowMove(False, True)
        ),
    )
    # Reuse means *not* launching. Left unpatched this really started the plan's terminal:
    # a Warp window per suite run, which cleanup cannot reap because Warp hands the window
    # to its running instance and the shell inside stays busy.
    launched: list[str] = []
    monkeypatch.setattr(
        "desktop_manager.launching.launch_entry",
        lambda entry, _subs, **_kwargs: launched.append(entry.name),
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=True, target_desktop="Work")

    assert any(action.startswith("  - reuse window `Workspace Terminal 1`") for action in actions)
    assert moved
    assert all(call == (101, 1, "Workspace Terminal 1") for call in moved)
    assert launched == []


@pytest.mark.skipif(sys.platform != "win32", reason="requires win32gui for window handle preservation")
def test_restore_workspace_does_not_retry_failed_reuse_candidate(monkeypatch, tmp_path: Path):
    # Arrange
    plan = build_sample_plan(tmp_path, terminal_count=2)

    candidate = LiveWindow(
        hwnd=101,
        title="opencode - shared-utilities",
        process_name="WindowsTerminal.exe",
        desktop_number=1,
    )

    monkeypatch.setattr("desktop_manager.win32.ensure_windows", lambda: None)
    monkeypatch.setattr("desktop_manager.win32.desktops.get_current_desktop_number", lambda: 1)
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_desktop_count", lambda _count, _delay: None)
    monkeypatch.setattr("desktop_manager.win32.geometry.current_window_handles_to_preserve", set)
    monkeypatch.setattr("desktop_manager.win32.inventory.inventory_reusable_windows", lambda **_kwargs: [candidate])
    monkeypatch.setattr("desktop_manager.win32.desktops.ensure_current_desktop_number", lambda *_args, **_kwargs: True)
    monkeypatch.setattr(
        "desktop_manager.restore.current_desktop_status",
        lambda desktop_name=None: DesktopPlan(name=desktop_name or "Work"),
    )
    monkeypatch.setattr(
        "desktop_manager.window_reuse.move_existing_window_to_entry",
        lambda _live_window, _desktop_index, entry: (_ for _ in ()).throw(RuntimeError(f"failed to move {entry.name}")),
    )

    launched: list[str] = []
    monkeypatch.setattr("desktop_manager.launching.launch_entry", lambda entry, _subs: launched.append(entry.name))
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.apply_window_placement",
        lambda *_args, **_kwargs: PlacementOutcome(placed=True),
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_for_entry", lambda *_args, **_kwargs: WindowMatch(hwnd=123)
    )
    monkeypatch.setattr(
        "desktop_manager.win32.entry_windows.wait_for_window_title_change", lambda *_args, **_kwargs: "OpenCode"
    )
    monkeypatch.setattr("time.sleep", lambda _seconds: None)

    # Act
    actions = restore_workspace_with_options(plan, dry_run=False, diff_existing=True)

    # Assert
    failed_reuses = [action for action in actions if action.startswith("  - reuse window `Workspace Terminal")]
    assert failed_reuses == ["  - reuse window `Workspace Terminal 1` from desktop 1"]
    assert launched[:2] == ["Workspace Terminal 1", "Workspace Terminal 2"]


def test_restore_wait_for_success_filters_actual_state_to_requested_screen(monkeypatch):
    from desktop_manager.cli import _wait_for_restore_success
    from desktop_manager.models import WindowSnapshot
    from desktop_manager.windows_workspace import LaunchEntry, PlacementLayout, PlacementSpec, WorkspacePlan

    class FakeConsole:
        def __init__(self):
            self.lines: list[str] = []

        def print(self, value=""):
            self.lines.append(str(value))

    fake_console = FakeConsole()
    monkeypatch.setattr("desktop_manager.cli.console", fake_console)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status_for_desktop_indexes",
        lambda desktop_indexes: WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Work",
                    captured_windows=[
                        WindowSnapshot(title="Target Window", process_name="tool.exe", screen_number=1),
                        WindowSnapshot(title="Other Screen Window", process_name="noise.exe", screen_number=2),
                    ],
                )
            ]
        ),
    )

    plan = WorkspacePlan(
        desktops=[
            DesktopPlan(
                name="Work",
                launch=[
                    LaunchEntry(
                        name="Target Window",
                        type="app",
                        path=r"C:\tool.exe",
                        window_process_name="tool.exe",
                        placement=PlacementSpec(screen=1, layout=PlacementLayout(mode="full", slot=1)),
                    )
                ],
            )
        ]
    )

    _wait_for_restore_success(plan, desktop="Work", screen=1, logger=None, poll_seconds=0.0, max_checks=1)

    output = "\n".join(fake_console.lines)
    assert "Check 1: success" in output
    assert "Other Screen Window" not in output
