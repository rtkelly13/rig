import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

pytestmark = [pytest.mark.ci, pytest.mark.usefixtures("as_windows")]


def _write_plan(tmp_path: Path, *, features: dict[str, object] | None = None) -> Path:
    config = tmp_path / "workspace.json"
    payload: dict[str, object] = {
        "desktops": [
            {
                "name": "Work",
                "launch": [
                    {"name": "WT 1", "type": "app", "path": "wt.exe", "feature": "wt-terminals"},
                    {"name": "Warp 1", "type": "app", "path": "warp.exe", "feature": "warp-terminals"},
                ],
            }
        ]
    }
    if features is not None:
        payload["features"] = features
    config.write_text(json.dumps(payload), encoding="utf-8")
    return config


def test_features_command_lists_state_and_owning_entries(tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path, features={"wt-terminals": True, "warp-terminals": False})

    result = CliRunner().invoke(app, ["workspace", "features", "--config", str(config)])

    assert result.exit_code == 0
    assert "wt-terminals" in result.stdout
    assert "paused" in result.stdout
    assert "WT 1" in result.stdout
    assert "Warp 1" in result.stdout


def test_features_command_lists_rig_flag_defaults_for_a_plan_without_features(tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path)

    result = CliRunner().invoke(app, ["workspace", "features", "--config", str(config)])

    assert result.exit_code == 0
    assert "warp-terminals: off (paused) (default)" in result.stdout
    assert "Warp 1" in result.stdout


def test_features_command_shows_a_stored_override(tmp_path: Path):
    from desktop_manager.cli import app
    from rig.flags import set_override

    config = _write_plan(tmp_path, features={"wt-terminals": False})
    set_override("wt-terminals", True)

    result = CliRunner().invoke(app, ["workspace", "features", "--config", str(config)])

    assert "wt-terminals: on (rig flags override)" in result.stdout


def test_features_command_flags_entries_pointing_at_undeclared_features(tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path)
    config.write_text(config.read_text(encoding="utf-8").replace('"warp-terminals"', '"warp-terminalz"'), "utf-8")

    result = CliRunner().invoke(app, ["workspace", "features", "--config", str(config)])

    assert result.exit_code == 1
    assert "undeclared features" in result.stdout


def test_restore_rejects_an_unknown_feature_name(tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path, features={"wt-terminals": True, "warp-terminals": False})

    result = CliRunner().invoke(
        app,
        ["workspace", "restore", "--config", str(config), "--dry-run", "--enable", "wrap-terminals"],
    )

    assert result.exit_code == 1
    assert "Unknown feature" in result.stdout


def test_restore_dry_run_skips_entries_behind_a_paused_feature(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path, features={"wt-terminals": True, "warp-terminals": False})
    captured: dict[str, object] = {}

    def fake_restore(plan, **_kwargs):
        captured["enabled"] = [
            entry.name for desktop in plan.desktops for entry in desktop.launch if not entry.disabled
        ]
        return ["ok"]

    monkeypatch.setattr("desktop_manager.windows_workspace.restore_workspace_with_options", fake_restore)

    result = CliRunner().invoke(app, ["workspace", "restore", "--config", str(config), "--dry-run"])

    assert result.exit_code == 0
    assert captured["enabled"] == ["WT 1"]
    assert "warp-terminals=off" in result.stdout


def test_restore_enable_flag_swaps_the_active_variant(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    config = _write_plan(tmp_path, features={"wt-terminals": True, "warp-terminals": False})
    captured: dict[str, object] = {}

    def fake_restore(plan, **_kwargs):
        captured["enabled"] = [
            entry.name for desktop in plan.desktops for entry in desktop.launch if not entry.disabled
        ]
        return ["ok"]

    monkeypatch.setattr("desktop_manager.windows_workspace.restore_workspace_with_options", fake_restore)

    result = CliRunner().invoke(
        app,
        [
            "workspace",
            "restore",
            "--config",
            str(config),
            "--dry-run",
            "--enable",
            "warp-terminals",
            "--disable",
            "wt-terminals",
        ],
    )

    assert result.exit_code == 0
    assert captured["enabled"] == ["Warp 1"]
