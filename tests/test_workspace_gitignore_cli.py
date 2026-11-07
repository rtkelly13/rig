"""Tests for workspace gitignore and workspace sync CLI commands."""

import json
import pathlib

import pytest
from typer.testing import CliRunner

from rig.workspace.cli import app

runner = CliRunner()


@pytest.mark.ci
def test_workspace_gitignore_audit_and_sync_cli(tmp_path: pathlib.Path):
    estate_root = tmp_path / "estate"
    estate_root.mkdir()

    repo_1 = estate_root / "repo-1"
    repo_2 = estate_root / "repo-2"
    repo_1.mkdir()
    repo_2.mkdir()
    (repo_1 / ".git").mkdir()
    (repo_2 / ".git").mkdir()

    (repo_1 / ".gitignore").write_text("TODO.md\ntodo.md\n/temp/\ntemp/\n", encoding="utf-8")
    (repo_2 / ".gitignore").write_text("TODO.md\n/temp/\ntemp/\n", encoding="utf-8")

    # Config pointing to both
    cfg_file = tmp_path / "managed.json"
    cfg_file.write_text(
        json.dumps(
            {
                "projects": [
                    {"name": "repo-1", "repo": "git@github.com:rtkelly13/repo-1.git"},
                    {"name": "repo-2", "repo": "git@github.com:rtkelly13/repo-2.git"},
                ]
            }
        ),
        encoding="utf-8",
    )

    # 1. Audit fails under --strict because repo-2 is missing todo.md
    res_audit = runner.invoke(
        app,
        ["gitignore", "audit", "--root", str(estate_root), "--config", str(cfg_file), "--strict"],
    )
    assert res_audit.exit_code != 0
    assert "MISSING todo.md" in res_audit.output

    # 2. JSON output includes both repos
    res_json = runner.invoke(
        app,
        ["gitignore", "audit", "--root", str(estate_root), "--config", str(cfg_file), "--json"],
    )
    assert res_json.exit_code == 0
    data = json.loads(res_json.output)
    assert len(data) == 2
    r2 = next(r for r in data if r["repo_name"] == "repo-2")
    assert not r2["is_aligned"]
    assert "todo.md" in r2["missing_lines"]

    # 3. Dry-run sync reports what would change
    res_dry = runner.invoke(
        app,
        ["gitignore", "sync", "--root", str(estate_root), "--config", str(cfg_file), "--dry-run"],
    )
    assert res_dry.exit_code == 0
    assert "Would update" in res_dry.output
    # repo-2 still missing todo.md
    assert "todo.md" not in (repo_2 / ".gitignore").read_text(encoding="utf-8")

    # 4. Sync applies changes
    res_sync = runner.invoke(
        app,
        ["gitignore", "sync", "--root", str(estate_root), "--config", str(cfg_file)],
    )
    assert res_sync.exit_code == 0
    assert "Updated" in res_sync.output
    assert "todo.md" in (repo_2 / ".gitignore").read_text(encoding="utf-8")

    # 5. Re-audit passes with exit code 0
    res_audit_after = runner.invoke(
        app,
        ["gitignore", "audit", "--root", str(estate_root), "--config", str(cfg_file), "--strict"],
    )
    assert res_audit_after.exit_code == 0
    assert "All repositories have compliant common .gitignore entries" in res_audit_after.output


@pytest.mark.ci
def test_workspace_gitignore_cli_with_language_option(tmp_path: pathlib.Path):
    estate_root = tmp_path / "estate"
    estate_root.mkdir()

    repo = estate_root / "py-repo"
    repo.mkdir()
    (repo / ".git").mkdir()
    (repo / ".gitignore").write_text("TODO.md\ntodo.md\ntemp/\n", encoding="utf-8")

    cfg_file = tmp_path / "managed.json"
    cfg_file.write_text(
        json.dumps({"projects": [{"name": "py-repo", "repo": "git@github.com:rtkelly13/py-repo.git"}]}),
        encoding="utf-8",
    )

    # Audit with explicit --language python flags missing python tools
    res = runner.invoke(
        app,
        ["gitignore", "audit", "--root", str(estate_root), "--config", str(cfg_file), "-l", "python", "--json"],
    )
    assert res.exit_code == 0
    data = json.loads(res.output)
    assert len(data) == 1
    assert data[0]["languages"] == ["python"]
    assert not data[0]["is_aligned"]
    assert "__pycache__/" in data[0]["missing_lines"]
