from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.windows_workspace import WorkspacePlan

pytestmark = pytest.mark.usefixtures("as_windows")


def test_wire_machine_command_saves_generated_plan(monkeypatch, tmp_path: Path):
    from desktop_manager.cli import app

    runner = CliRunner()
    captured: list[tuple[Path, Path | None, Path]] = []

    monkeypatch.setattr(
        "desktop_manager.windows_workspace.build_bespoke_machine_plan",
        lambda workspace, personal_workspace=None: (
            captured.append((workspace, personal_workspace, tmp_path / "workspace.json")) or WorkspacePlan(desktops=[])
        ),
    )
    monkeypatch.setattr(
        "desktop_manager.windows_workspace.save_plan",
        lambda _plan, path: path.write_text("{}", encoding="utf-8"),
    )

    result = runner.invoke(
        app,
        [
            "workspace",
            "wire-machine",
            "--workspace",
            str(tmp_path / "workspace"),
            "--personal-workspace",
            str(tmp_path / "personal"),
            "--config",
            str(tmp_path / "workspace.json"),
        ],
    )

    assert result.exit_code == 0
    assert captured == [(tmp_path / "workspace", tmp_path / "personal", tmp_path / "workspace.json")]
    assert (tmp_path / "workspace.json").exists()
