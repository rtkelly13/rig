from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager import restore
from desktop_manager.win32 import geometry, inventory
from desktop_manager.windows_workspace import (
    DesktopPlan,
    LaunchEntry,
    PlacementLayout,
    PlacementSpec,
    WorkspacePlan,
    find_launch_entries_by_name,
    restart_app_entries,
)

pytestmark = pytest.mark.usefixtures("as_windows")


def _fork_entry(name: str = "Fork") -> LaunchEntry:
    return LaunchEntry(
        name=name,
        type="app",
        path=r"C:\Fork\Fork.exe",
        window_process_name="Fork.exe",
        placement=PlacementSpec(screen=2, layout=PlacementLayout(mode="full", slot=1)),
    )


def _plan() -> WorkspacePlan:
    return WorkspacePlan(
        desktops=[
            DesktopPlan(name="Work", launch=[_fork_entry(), LaunchEntry(name="Terminal 1", type="terminal")]),
            DesktopPlan(name="Review", launch=[_fork_entry()]),
        ]
    )


@pytest.mark.ci
def test_find_launch_entries_by_name_is_case_insensitive_and_spans_desktops():
    matches = find_launch_entries_by_name(_plan(), "fork")
    assert [(match.desktop.name, match.entry.name) for match in matches] == [
        ("Work", "Fork"),
        ("Review", "Fork"),
    ]


@pytest.mark.ci
def test_find_launch_entries_by_name_scopes_to_desktop():
    matches = find_launch_entries_by_name(_plan(), "Fork", target_desktop="Review")
    assert len(matches) == 1
    assert matches[0].desktop.name == "Review"


@pytest.mark.ci
def test_restart_app_entries_raises_when_no_match():
    with pytest.raises(ValueError, match="No launch entries named `Nope`"):
        restart_app_entries(_plan(), "Nope")


@pytest.mark.ci
def test_restart_app_entries_dry_run_does_not_kill_or_relaunch(monkeypatch):
    monkeypatch.setattr(inventory, "process_ids_for_process_name", lambda _name, current_desktop_only=False: {111})

    def fail(*_args, **_kwargs):
        raise AssertionError("dry-run must not act")

    monkeypatch.setattr(inventory, "terminate_process_id", fail)
    monkeypatch.setattr(restore, "launch_single_entry", fail)

    actions = restart_app_entries(_plan(), "Fork", dry_run=True)

    joined = "\n".join(actions)
    assert "would close all `Fork.exe` windows" in joined
    assert "would relaunch `Fork` on Work" in joined
    assert "would relaunch `Fork` on Review" in joined


@pytest.mark.ci
def test_restart_app_entries_force_kills_then_relaunches(monkeypatch):
    killed: list[int] = []
    relaunched: list[tuple[str, str]] = []

    monkeypatch.setattr(restore.time, "sleep", lambda _seconds: None)
    monkeypatch.setattr(inventory, "process_ids_for_process_name", lambda _name, current_desktop_only=False: {111, 222})
    monkeypatch.setattr(inventory, "terminate_process_id", lambda pid, exit_code=1: killed.append(pid) or True)
    monkeypatch.setattr(inventory, "wait_for_process_exit", lambda _pid, timeout_seconds=5.0: True)

    def fake_launch(plan, desktop_name, entry_name, *, dry_run=False, logger=None):
        _ = (plan, dry_run, logger)
        relaunched.append((desktop_name, entry_name))
        return [f"Desktop: {desktop_name}", f"  - {entry_name}"]

    monkeypatch.setattr(restore, "launch_single_entry", fake_launch)

    actions = restart_app_entries(_plan(), "Fork")

    assert sorted(killed) == [111, 222]
    assert relaunched == [("Work", "Fork"), ("Review", "Fork")]
    assert any("closed 2 process(es) (force-kill)" in line for line in actions)


@pytest.mark.ci
def test_restart_app_entries_graceful_force_kills_only_survivors(monkeypatch):
    closed_windows: list[int] = []
    killed: list[int] = []
    calls = {"count": 0}

    monkeypatch.setattr(restore.time, "sleep", lambda _seconds: None)
    monkeypatch.setattr(
        inventory,
        "collect_windows_matching_process",
        lambda _name, current_desktop_only=False: [10, 20],
    )
    monkeypatch.setattr(geometry, "request_close_window", lambda hwnd: closed_windows.append(hwnd))

    def fake_pids(_name, current_desktop_only=False):
        calls["count"] += 1
        # first call: initial inventory; second call: survivors after polite close
        return {111, 222} if calls["count"] == 1 else {222}

    monkeypatch.setattr(inventory, "process_ids_for_process_name", fake_pids)
    monkeypatch.setattr(inventory, "terminate_process_id", lambda pid, exit_code=1: killed.append(pid) or True)
    monkeypatch.setattr(inventory, "wait_for_process_exit", lambda _pid, timeout_seconds=5.0: True)
    monkeypatch.setattr(restore, "launch_single_entry", lambda *a, **k: ["relaunched"])

    actions = restart_app_entries(_plan(), "Fork", target_desktop="Review", graceful=True)

    assert closed_windows == [10, 20]
    assert killed == [222]
    assert any("graceful, then force-kill" in line for line in actions)


@pytest.mark.ci
def test_restart_app_command_reports_missing_config(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    result = runner.invoke(
        app,
        ["workspace", "restart-app", "--name", "Fork", "--config", str(tmp_path / "missing.json")],
    )

    assert result.exit_code == 1
    assert "Config not found" in result.stdout


@pytest.mark.ci
def test_restart_app_command_invokes_orchestrator(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    captured: dict[str, object] = {}

    def fake_restart(plan, name, *, target_desktop=None, graceful=False, dry_run=False, logger=None):
        _ = (plan, logger)
        captured.update(name=name, target_desktop=target_desktop, graceful=graceful, dry_run=dry_run)
        return [f"Restart app `{name}`", "  - closed 1 process(es) (force-kill)"]

    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")

    monkeypatch.setattr("desktop_manager.windows_workspace.set_virtual_desktop_event_callback", lambda _cb: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.clear_virtual_desktop_event_callback", lambda: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: WorkspacePlan(desktops=[]))
    monkeypatch.setattr("desktop_manager.windows_workspace.restart_app_entries", fake_restart)

    runner = CliRunner()
    result = runner.invoke(
        app,
        ["workspace", "restart-app", "--name", "Fork", "--desktop", "Work", "--config", str(config), "--dry-run"],
    )

    assert result.exit_code == 0
    assert captured == {"name": "Fork", "target_desktop": "Work", "graceful": False, "dry_run": True}
    assert "Restart app `Fork`" in result.stdout
