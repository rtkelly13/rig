import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.windows_workspace import WorkspacePlan

pytestmark = pytest.mark.usefixtures("as_windows")


def test_restore_command_reports_missing_config(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    missing = tmp_path / "missing.json"

    result = runner.invoke(app, ["workspace", "restore", "--config", str(missing)])

    assert result.exit_code == 1
    assert "Config not found" in result.stdout


def test_restore_command_fails_strict_when_structure_issues(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    monkeypatch.setattr("desktop_manager.cli.config_option", lambda: config)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.load_plan",
        lambda path, **_kwargs: (_ for _ in ()).throw(
            ValueError(f"Structurally invalid workspace plan {path}: screen 1 empty")
        ),
    )

    result = runner.invoke(app, ["workspace", "restore", "--strict", "--dry-run"])

    assert result.exit_code == 1
    assert isinstance(result.exception, SystemExit)


def test_restore_command_prints_final_status_after_run(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    def fake_current_workspace_status(desktop_count=None):
        _ = desktop_count
        return plan

    def fake_render_workspace_diff(plan, actual):
        _ = (plan, actual)
        return "Workspace Diff\nstatus"

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")
    plan = WorkspacePlan(desktops=[])

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path, **_kwargs: plan)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.restore_workspace_with_options",
        lambda *_args, **_kwargs: ["ok"],
    )
    monkeypatch.setattr("desktop_manager.windows_workspace.current_workspace_status", fake_current_workspace_status)
    monkeypatch.setattr("desktop_manager.windows_workspace.render_workspace_diff", fake_render_workspace_diff)
    monkeypatch.setattr("desktop_manager.windows_workspace.create_restore_run_logger", lambda _path: None)
    monkeypatch.setattr("desktop_manager.cli.time.sleep", lambda _seconds: None)

    result = runner.invoke(app, ["workspace", "restore", "--config", str(config)])

    assert result.exit_code == 0
    assert "Final Status" in result.stdout
    assert "Workspace Diff" in result.stdout


def test_restore_command_prints_return_to_starting_desktop(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    def fake_current_workspace_status(desktop_count=None):
        _ = desktop_count
        return plan

    def fake_render_workspace_diff(plan, actual):
        _ = (plan, actual)
        return "Workspace Diff\nstatus"

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")
    plan = WorkspacePlan(desktops=[])

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path, **_kwargs: plan)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.restore_workspace_with_options",
        lambda *_args, **_kwargs: ["Desktop 1: Work", "Return to starting desktop 2"],
    )
    monkeypatch.setattr("desktop_manager.windows_workspace.current_workspace_status", fake_current_workspace_status)
    monkeypatch.setattr("desktop_manager.windows_workspace.render_workspace_diff", fake_render_workspace_diff)
    monkeypatch.setattr("desktop_manager.windows_workspace.create_restore_run_logger", lambda _path: None)
    monkeypatch.setattr("desktop_manager.cli.time.sleep", lambda _seconds: None)

    result = runner.invoke(app, ["workspace", "restore", "--config", str(config)])

    assert result.exit_code == 0
    assert "Return to starting desktop 2" in result.stdout


def test_restore_command_fails_when_wait_for_success_detects_regression(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")
    plan = WorkspacePlan(desktops=[])

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path, **_kwargs: plan)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.restore_workspace_with_options",
        lambda *_args, **_kwargs: ["ok"],
    )
    monkeypatch.setattr("desktop_manager.windows_workspace.create_restore_run_logger", lambda _path: None)
    monkeypatch.setattr("desktop_manager.cli._wait_for_restore_success", lambda *_args, **_kwargs: False)

    result = runner.invoke(app, ["workspace", "restore", "--config", str(config), "--wait-for-success"])

    assert result.exit_code == 1


def test_audit_command_reports_clean_state(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")
    plan = WorkspacePlan(desktops=[])

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path, **_kwargs: plan)
    monkeypatch.setattr("desktop_manager.windows_workspace.current_workspace_status", lambda desktop_count=None: plan)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.render_workspace_diff", lambda _plan, _actual: "Workspace Diff\nclean"
    )

    result = runner.invoke(app, ["workspace", "audit", "--config", str(config)])

    assert result.exit_code == 0
    assert "Workspace Audit" in result.stdout
    assert "Audit passed" in result.stdout


def test_audit_command_reports_drift(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")
    plan = WorkspacePlan(desktops=[])

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda _path, **_kwargs: plan)
    monkeypatch.setattr("desktop_manager.windows_workspace.current_workspace_status", lambda desktop_count=None: plan)
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.render_workspace_diff",
        lambda _plan, _actual: "Workspace Diff\nmissing: Terminal",
    )

    result = runner.invoke(app, ["workspace", "audit", "--config", str(config)])

    assert result.exit_code == 1
    assert "Audit found drift" in result.stdout


def test_restore_desktop_scope_keeps_the_absolute_desktop_index(monkeypatch, tmp_path: Path):
    """`--desktop` must not renumber the target desktop.

    The restore handler resolves the name to a virtual-desktop index itself, so the CLI
    has to hand it the whole plan. Filtering the other desktops out first moved `Work`
    from index 2 to index 1, which restored onto the wrong desktop and relaunched every
    entry instead of adopting the windows already sitting on the real one.
    """
    from desktop_manager.cli import app
    from desktop_manager.restore_planner import resolve_target_desktop_index

    config = tmp_path / "workspace.json"
    config.write_text(
        json.dumps(
            {
                "desktops": [
                    {"name": "Review", "launch": [{"name": "Code", "type": "app", "path": "code.exe"}]},
                    {"name": "Work", "launch": [{"name": "WT 1", "type": "app", "path": "wt.exe"}]},
                    {"name": "Admin", "launch": [{"name": "Slack", "type": "app", "path": "slack.exe"}]},
                ]
            }
        ),
        encoding="utf-8",
    )
    captured: dict[str, object] = {}

    def fake_restore(plan, **kwargs):
        captured["plan"] = plan
        captured["target_desktop"] = kwargs.get("target_desktop")
        return ["ok"]

    monkeypatch.setattr("desktop_manager.windows_workspace.restore_workspace_with_options", fake_restore)

    result = CliRunner().invoke(
        app, ["workspace", "restore", "--config", str(config), "--desktop", "Work", "--dry-run"]
    )

    assert result.exit_code == 0
    passed_plan = captured["plan"]
    assert isinstance(passed_plan, WorkspacePlan)
    assert [desktop.name for desktop in passed_plan.desktops] == ["Review", "Work", "Admin"]
    assert captured["target_desktop"] == "Work"
    assert resolve_target_desktop_index(passed_plan, "Work") == 2
