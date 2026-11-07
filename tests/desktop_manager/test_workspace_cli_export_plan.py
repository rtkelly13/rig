from pathlib import Path

from typer.testing import CliRunner

from desktop_manager.windows_workspace import DesktopPlan, WorkspacePlan


def test_export_plan_command_saves_bundle(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    exported_plan = WorkspacePlan(desktops=[DesktopPlan(name="Desktop 1", launch=[])])

    monkeypatch.setattr("desktop_manager.cli.sys.platform", "win32")
    monkeypatch.setattr("desktop_manager.windows_workspace.set_virtual_desktop_event_callback", lambda _callback: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.clear_virtual_desktop_event_callback", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.capture_workspace_plan",
        lambda **_kwargs: exported_plan,
    )

    result = runner.invoke(
        app,
        ["workspace", "export-plan", "--config", str(tmp_path / "bundle.json"), "--bundle"],
    )

    assert result.exit_code == 0
    assert (tmp_path / "bundle.json").exists()
    assert (tmp_path / "desktops" / "desktop-1.json").exists()
    assert "Mode:" in result.stdout


def test_export_plan_command_can_save_single_file(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    exported_plan = WorkspacePlan(desktops=[DesktopPlan(name="Desktop 1", launch=[])])

    monkeypatch.setattr("desktop_manager.cli.sys.platform", "win32")
    monkeypatch.setattr("desktop_manager.windows_workspace.set_virtual_desktop_event_callback", lambda _callback: None)
    monkeypatch.setattr("desktop_manager.windows_workspace.clear_virtual_desktop_event_callback", lambda: None)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.capture_workspace_plan",
        lambda **_kwargs: exported_plan,
    )

    result = runner.invoke(
        app,
        ["workspace", "export-plan", "--config", str(tmp_path / "workspace.json")],
    )

    assert result.exit_code == 0
    assert (tmp_path / "workspace.json").exists()
    assert "single file" in result.stdout


def test_export_plan_command_rejects_non_windows(monkeypatch):
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.cli.sys.platform", "linux")

    result = runner.invoke(app, ["workspace", "export-plan"])

    assert result.exit_code == 1
    assert "only available on Windows" in result.output
