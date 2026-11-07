import json
from pathlib import Path

from typer.testing import CliRunner


def test_validate_plan_command_accepts_valid_plan(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    chrome = tmp_path / "chrome.exe"
    chrome.write_text("", encoding="utf-8")
    slack = tmp_path / "slack.exe"
    slack.write_text("", encoding="utf-8")
    config.write_text(
        json.dumps(
            {
                "workspace": str(workspace),
                "desktops": [
                    {
                        "name": "Work",
                        "launch": [
                            {
                                "name": "Terminal",
                                "type": "terminal",
                                "cwd": "{workspace}",
                                "command": "opencode",
                                "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                                "window_title": "Terminal",
                                "window_process_name": "WindowsTerminal.exe",
                            },
                            {
                                "name": "Chrome",
                                "type": "app",
                                "path": str(chrome),
                                "placement": {"screen": 2, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "chrome.exe",
                            },
                            {
                                "name": "Slack",
                                "type": "app",
                                "path": str(slack),
                                "placement": {"screen": 3, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "slack.exe",
                            },
                        ],
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["workspace", "validate-plan", "--config", str(config)])

    assert result.exit_code == 0
    assert "Plan is valid" in result.stdout


def test_validate_plan_command_reports_missing_config(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()

    result = runner.invoke(app, ["workspace", "validate-plan", "--config", str(tmp_path / "missing.json")])

    assert result.exit_code == 1
    assert "Config not found" in result.stdout


def test_validate_plan_command_reports_missing_installed_app(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    # Pin the detected monitor count so structural validation matches the
    # single-screen plan regardless of how many monitors the host has.
    monkeypatch.setattr("desktop_manager.win32.geometry.monitor_work_areas", lambda: [(0, 0, 1920, 1080)])

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text(
        json.dumps(
            {
                "desktops": [
                    {
                        "name": "Admin",
                        "launch": [
                            {
                                "name": "Missing App",
                                "type": "app",
                                "path": r"C:\does-not-exist\missing.exe",
                                "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "missing.exe",
                            }
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["workspace", "validate-plan", "--config", str(config)])

    assert result.exit_code == 1
    assert "app path does not exist" in result.output


def test_validate_plan_command_accepts_bare_executable_on_path(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    discovered = tmp_path / "claude.exe"
    discovered.write_text("", encoding="utf-8")
    config.write_text(
        json.dumps(
            {
                "desktops": [
                    {
                        "name": "Work",
                        "launch": [
                            {
                                "name": "Claude Desktop",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                            {
                                "name": "Screen 2 App",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 2, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                            {
                                "name": "Screen 3 App",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 3, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr("desktop_manager.executable_paths.resolve_executable_on_path", lambda _path: str(discovered))

    result = runner.invoke(app, ["workspace", "validate-plan", "--config", str(config)])

    assert result.exit_code == 0
    assert "Plan is valid" in result.stdout


def test_validate_plan_command_reports_missing_bare_executable(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text(
        json.dumps(
            {
                "desktops": [
                    {
                        "name": "Work",
                        "launch": [
                            {
                                "name": "Claude Desktop",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 1, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                            {
                                "name": "Screen 2 App",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 2, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                            {
                                "name": "Screen 3 App",
                                "type": "app",
                                "path": "claude.exe",
                                "placement": {"screen": 3, "layout": {"mode": "full", "slot": 1}},
                                "window_process_name": "claude.exe",
                            },
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr("desktop_manager.executable_paths.resolve_executable_on_path", lambda _path: None)

    result = runner.invoke(app, ["workspace", "validate-plan", "--config", str(config)])

    assert result.exit_code == 1
    assert "Work/Claude Desktop: app path does not exist: claude.exe" in result.output
