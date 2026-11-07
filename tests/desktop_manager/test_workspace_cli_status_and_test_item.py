from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.windows_workspace import DesktopPlan, WindowSnapshot, WorkspacePlan


def test_status_command_rejects_non_windows(monkeypatch):
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.cli.sys.platform", "linux")

    result = runner.invoke(app, ["workspace", "status"])

    assert result.exit_code == 1
    assert "only available on Windows" in result.output


def test_status_command_prints_current_windows(monkeypatch):
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.cli.sys.platform", "win32")
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.get_virtual_desktop_count",
        lambda: 2,
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Desktop 1",
                    captured_windows=[WindowSnapshot(title="Chrome", process_name="chrome.exe", screen_number=2)],
                ),
                DesktopPlan(name="Desktop 2", captured_windows=[]),
            ]
        ),
    )

    result = runner.invoke(app, ["workspace", "status", "--verbose"])

    assert result.exit_code == 0
    assert "Desktop 1" in result.stdout
    assert "Chrome" in result.stdout
    assert "screen 2" in result.stdout
    assert "Desktop 2" in result.stdout
    assert "(no visible windows)" in result.stdout


def test_status_command_replaces_unprintable_window_title_chars(monkeypatch):
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.cli.sys.platform", "win32")
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.get_virtual_desktop_count",
        lambda: 1,
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.current_workspace_status",
        lambda desktop_count=None: WorkspacePlan(
            desktops=[
                DesktopPlan(
                    name="Desktop 1",
                    captured_windows=[
                        WindowSnapshot(
                            title="⠐ Optimize Claude Code token usage and settings",
                            process_name="WindowsTerminal.exe",
                            screen_number=1,
                        )
                    ],
                )
            ]
        ),
    )

    result = runner.invoke(app, ["workspace", "status", "--verbose"])

    assert result.exit_code == 0
    assert "Optimize Claude Code token usage and settings" in result.stdout


@pytest.mark.usefixtures("as_windows")
def test_test_item_command_uses_launch_single_entry(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    def fake_launch_single_entry(plan, desktop, name, dry_run=False, logger=None):
        _ = (plan, dry_run, logger)
        return [f"Desktop 1: {desktop}", f"  - {name}"]

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")

    monkeypatch.setattr("desktop_manager.windows_workspace.set_virtual_desktop_event_callback", lambda _callback: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.clear_virtual_desktop_event_callback", lambda: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: WorkspacePlan(desktops=[]))
    monkeypatch.setattr("desktop_manager.windows_workspace.launch_single_entry", fake_launch_single_entry)

    result = runner.invoke(
        app,
        ["workspace", "test-item", "--desktop", "Work", "--name", "Chrome", "--config", str(config), "--dry-run"],
    )

    assert result.exit_code == 0
    assert "Desktop 1: Work" in result.stdout


@pytest.mark.usefixtures("as_windows")
def test_test_item_command_reports_unknown_desktop(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    def fake_launch_single_entry(plan, desktop, name, dry_run=False, logger=None):
        _ = (plan, name, dry_run, logger)
        raise ValueError(f"Desktop not found: {desktop}")

    cleared: list[bool] = []
    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")

    monkeypatch.setattr("desktop_manager.windows_workspace.set_virtual_desktop_event_callback", lambda _callback: None)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.clear_virtual_desktop_event_callback", lambda: cleared.append(True)
    )
    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path: WorkspacePlan(desktops=[]))
    monkeypatch.setattr("desktop_manager.windows_workspace.launch_single_entry", fake_launch_single_entry)

    result = runner.invoke(
        app,
        ["workspace", "test-item", "--desktop", "Nope", "--name", "Chrome", "--config", str(config), "--dry-run"],
    )

    assert result.exit_code == 1
    assert "Desktop not found: Nope" in result.stdout
    assert result.exception is None or isinstance(result.exception, SystemExit)
    assert cleared == [True]


@pytest.mark.usefixtures("as_windows")
def test_test_item_command_reports_missing_config(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()

    result = runner.invoke(
        app,
        [
            "workspace",
            "test-item",
            "--desktop",
            "Work",
            "--name",
            "Chrome",
            "--config",
            str(tmp_path / "missing.json"),
        ],
    )

    assert result.exit_code == 1
    assert "Config not found" in result.stdout
