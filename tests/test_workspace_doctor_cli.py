"""Tests for the `workspace doctor` CLI command."""

from __future__ import annotations

import json
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

from rig.workspace.cli import app
from rig.workspace.repo_resolver import RepoFolderCheck

runner = CliRunner()


def test_workspace_doctor_help() -> None:
    result = runner.invoke(app, ["doctor", "--help"])
    assert result.exit_code == 0
    assert "Diagnose repository folder names and remotes" in result.stdout


def test_workspace_doctor_empty_root(tmp_path: Path) -> None:
    result = runner.invoke(app, ["doctor", "--root", str(tmp_path)])
    assert result.exit_code == 0
    assert "All estate repository folders match their canonical upstream names" in result.stdout


def test_workspace_doctor_with_checks(tmp_path: Path) -> None:
    mock_checks = [
        RepoFolderCheck(
            repo_path=tmp_path / "rig",
            folder_name="rig",
            owner="rtkelly13",
            remote_repo_name="rig",
            canonical_name="rig",
            is_match=True,
            remote_matches_canonical=True,
            is_renamed=False,
            status="ok",
            detail="Folder and remote match canonical name 'rig'",
        ),
        RepoFolderCheck(
            repo_path=tmp_path / "shared-utilities",
            folder_name="shared-utilities",
            owner="rtkelly13",
            remote_repo_name="shared-utilities",
            canonical_name="rig",
            is_match=False,
            remote_matches_canonical=False,
            is_renamed=True,
            status="folder_renamed",
            detail="Folder 'shared-utilities' is named after a previous repository name; renamed upstream to 'rig'",
        ),
    ]

    with patch(
        "rig.workspace.cli.check_estate_repos",
        return_value=mock_checks,
    ):
        # Default non-strict run
        res = runner.invoke(app, ["doctor", "--root", str(tmp_path)], env={"COLUMNS": "200"})
        assert res.exit_code == 0
        assert "Estate Repository Doctor" in res.stdout
        assert "rig" in res.stdout
        assert "shared-utilities" in res.stdout
        assert "Renamed (Outdated)" in res.stdout
        assert "Repository folder or remote mismatches detected" in res.stdout

        # Strict run should exit 1 because of folder_renamed
        res_strict = runner.invoke(app, ["doctor", "--root", str(tmp_path), "--strict"])
        assert res_strict.exit_code == 1

        # JSON output
        res_json = runner.invoke(app, ["doctor", "--root", str(tmp_path), "--json"])
        assert res_json.exit_code == 0
        payload: object = json.loads(res_json.stdout)
        assert isinstance(payload, dict)
        assert payload.get("is_clean") is False
        assert payload.get("total_repos") == 2
        checks_data = payload.get("checks")
        assert isinstance(checks_data, list)
        assert len(checks_data) == 2
        item0 = checks_data[0]
        item1 = checks_data[1]
        assert isinstance(item0, dict)
        assert isinstance(item1, dict)
        assert item0.get("folder_name") == "rig"
        assert item0.get("status") == "ok"
        assert item1.get("folder_name") == "shared-utilities"
        assert item1.get("status") == "folder_renamed"
