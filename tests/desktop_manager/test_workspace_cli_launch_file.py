from pathlib import Path

import pytest
from typer.testing import CliRunner

pytestmark = pytest.mark.usefixtures("as_windows")


def test_launch_file_command_uses_launch_path(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "chrome.json"
    entry_file.write_text(
        (
            '{"name":"Chrome Test","type":"app","path":'
            '"C:\\\\Program Files\\\\Google\\\\Chrome\\\\Application\\\\chrome.exe",'
            '"args":["--profile-directory=Default"]}'
        ),
        encoding="utf-8",
    )

    launched: list[tuple[str, list[str], object | None]] = []

    def fake_launch_entry(entry, substitutions):
        launched.append((entry.name, [item for item in entry.args], entry.placement))

    monkeypatch.setattr("desktop_manager.windows_workspace.launch_entry", fake_launch_entry)

    result = runner.invoke(app, ["workspace", "launch-file", "--entry-file", str(entry_file)])

    assert result.exit_code == 0
    assert launched == [("Chrome Test", ["--profile-directory=Default"], None)]
    assert "Chrome Test" in result.stdout


def test_launch_file_command_uses_config_aliases(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "chrome.json"
    entry_file.write_text(
        (
            '{"name":"Chrome Test","type":"app","path":"{chrome-path}",'
            '"args":["--profile-directory={chrome-profile-work}"]}'
        ),
        encoding="utf-8",
    )
    config_file = tmp_path / "plan.json"
    config_file.write_text(
        (
            '{"aliases":{"chrome-path":'
            '"C:\\\\Program Files\\\\Google\\\\Chrome\\\\Application\\\\chrome.exe",'
            '"chrome-profile-work":"Default"}}'
        ),
        encoding="utf-8",
    )

    described: list[str] = []

    def fake_launch_entry(entry, substitutions):
        described.append(f"{substitutions['chrome-path']}|{substitutions['chrome-profile-work']}")

    monkeypatch.setattr("desktop_manager.windows_workspace.launch_entry", fake_launch_entry)

    result = runner.invoke(
        app,
        ["workspace", "launch-file", "--entry-file", str(entry_file), "--config", str(config_file)],
    )

    assert result.exit_code == 0
    assert described == [r"C:\Program Files\Google\Chrome\Application\chrome.exe|Default"]


def test_launch_file_dry_run_does_not_launch(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "chrome.json"
    entry_file.write_text('{"name":"Chrome Test","type":"app","path":"C:\\\\Chrome.exe"}', encoding="utf-8")

    calls: list[str] = []
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.launch_entry",
        lambda entry, _substitutions: calls.append(entry.name),
    )

    result = runner.invoke(app, ["workspace", "launch-file", "--entry-file", str(entry_file), "--dry-run"])

    assert result.exit_code == 0
    assert calls == []


def test_launch_file_drops_placement_to_stay_on_current_screen(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "chrome.json"
    entry_file.write_text(
        (
            '{"name":"Chrome Test","type":"app","path":"C:\\\\Chrome.exe",'
            '"placement":{"screen":3,"layout":{"mode":"full","slot":1}}}'
        ),
        encoding="utf-8",
    )

    placements: list[object | None] = []

    def fake_launch_entry(entry, substitutions):
        placements.append(entry.placement)

    monkeypatch.setattr("desktop_manager.windows_workspace.launch_entry", fake_launch_entry)

    result = runner.invoke(app, ["workspace", "launch-file", "--entry-file", str(entry_file)])

    assert result.exit_code == 0
    assert placements == [None]


def test_launch_file_reports_missing_generated_path(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "terminal.json"
    entry_file.write_text(
        '{"name":"Workspace Terminal 3","type":"terminal","cwd":"{workspace}","command":"opencode","shell":"wt-pwsh"}',
        encoding="utf-8",
    )
    config_file = tmp_path / "plan.json"
    config_file.write_text('{"workspace":"C:\\\\does-not-exist"}', encoding="utf-8")

    result = runner.invoke(
        app,
        ["workspace", "launch-file", "--entry-file", str(entry_file), "--config", str(config_file)],
    )

    assert result.exit_code == 1
    assert "resolved cwd does not exist" in str(result.exception)


def test_launch_file_reports_unresolved_placeholder(tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    entry_file = tmp_path / "terminal.json"
    entry_file.write_text(
        '{"name":"Workspace Terminal 3","type":"terminal","cwd":"{workspace}","command":"opencode","shell":"wt-pwsh"}',
        encoding="utf-8",
    )

    result = runner.invoke(app, ["workspace", "launch-file", "--entry-file", str(entry_file)])

    assert result.exit_code == 1
    assert "Unresolved placeholders" in str(result.exception)
