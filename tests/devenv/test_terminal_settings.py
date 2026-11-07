"""Unit tests for Windows Terminal settings management."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from rig.devenv.engine.context import Platform, StepContext
from rig.devenv.steps import terminal


@pytest.fixture
def fake_local_appdata(tmp_path: Path) -> Path:
    """Create a temporary local app data directory structure."""
    return tmp_path / "LocalAppData"


def test_get_windows_terminal_settings_path_none(fake_local_appdata: Path) -> None:
    assert terminal.get_windows_terminal_settings_path(fake_local_appdata) is None


def test_get_windows_terminal_settings_path_packaged(fake_local_appdata: Path) -> None:
    settings = (
        fake_local_appdata / "Packages" / "Microsoft.WindowsTerminal_8wekyb3d8bbwe" / "LocalState" / "settings.json"
    )
    settings.parent.mkdir(parents=True, exist_ok=True)
    settings.write_text("{}", encoding="utf-8")

    found = terminal.get_windows_terminal_settings_path(fake_local_appdata)
    assert found == settings


def test_get_windows_terminal_status_uninstalled(tmp_path: Path) -> None:
    nonexistent = tmp_path / "settings.json"
    status = terminal.get_windows_terminal_status(nonexistent)
    assert not status.installed
    assert status.snap_to_grid is None


def test_get_windows_terminal_status_installed(tmp_path: Path) -> None:
    target = tmp_path / "settings.json"
    data = {
        "snapToGridOnResize": False,
        "profiles": {
            "defaults": {
                "font": {"face": "JetBrainsMono Nerd Font", "size": 10},
                "startingDirectory": "C:\\code",
            }
        },
    }
    target.write_text(json.dumps(data), encoding="utf-8")

    status = terminal.get_windows_terminal_status(target)
    assert status.installed is True
    assert status.snap_to_grid is False
    assert status.font_face == "JetBrainsMono Nerd Font"
    assert status.starting_directory == "C:\\code"


def test_configure_windows_terminal_sets_snap_to_grid_and_defaults(tmp_path: Path) -> None:
    target = tmp_path / "settings.json"
    target.write_text(
        json.dumps(
            {
                "snapToGridOnResize": True,
                "profiles": {"defaults": {}},
            }
        ),
        encoding="utf-8",
    )

    ctx = StepContext(platform=Platform.WINDOWS, dry_run=False)
    ok = terminal.configure_windows_terminal(
        ctx,
        font_name="CascadiaCode Nerd Font",
        starting_directory="C:\\code",
        disable_snap_to_grid=True,
        settings_path=target,
    )
    assert ok is True

    updated = json.loads(target.read_text(encoding="utf-8"))
    assert updated["snapToGridOnResize"] is False
    assert updated["profiles"]["defaults"]["font"]["face"] == "CascadiaCode Nerd Font"
    assert updated["profiles"]["defaults"]["font"]["size"] == 10
    assert updated["profiles"]["defaults"]["startingDirectory"] == "C:\\code"


def test_configure_windows_terminal_idempotent(tmp_path: Path) -> None:
    target = tmp_path / "settings.json"
    initial_content = json.dumps(
        {
            "snapToGridOnResize": False,
            "profiles": {
                "defaults": {
                    "font": {"face": "JetBrainsMono Nerd Font", "size": 10},
                    "startingDirectory": "C:\\code",
                }
            },
        },
        indent=2,
    )
    target.write_text(initial_content, encoding="utf-8")

    ctx = StepContext(platform=Platform.WINDOWS, dry_run=False)
    ok = terminal.configure_windows_terminal(
        ctx,
        font_name="JetBrainsMono Nerd Font",
        starting_directory="C:\\code",
        disable_snap_to_grid=True,
        settings_path=target,
    )
    assert ok is True
    assert target.read_text(encoding="utf-8") == initial_content


def test_configure_windows_terminal_dry_run_does_not_modify(tmp_path: Path) -> None:
    target = tmp_path / "settings.json"
    initial_content = json.dumps({"snapToGridOnResize": True})
    target.write_text(initial_content, encoding="utf-8")

    ctx = StepContext(platform=Platform.WINDOWS, dry_run=True)
    ok = terminal.configure_windows_terminal(
        ctx,
        font_name="JetBrainsMono Nerd Font",
        starting_directory="C:\\code",
        disable_snap_to_grid=True,
        settings_path=target,
    )
    assert ok is True
    assert target.read_text(encoding="utf-8") == initial_content
