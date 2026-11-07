"""Tests for repo-governance gitignore CLI command."""

import json
import pathlib

import pytest
from typer.testing import CliRunner

from rig.repo_governance.align_repo_conventions import app

runner = CliRunner()


@pytest.mark.ci
def test_repo_governance_gitignore_audit_and_apply_cli(tmp_path: pathlib.Path):
    repo = tmp_path / "test-repo"
    repo.mkdir()
    (repo / ".git").mkdir()

    # Empty .gitignore -> missing common rules
    gi = repo / ".gitignore"
    gi.write_text("# Initial\n", encoding="utf-8")

    # 1. Audit detects drift (exit code 1)
    res_audit = runner.invoke(app, ["gitignore", str(repo)])
    assert res_audit.exit_code != 0
    assert "MISSING" in res_audit.output

    # 2. JSON output
    res_json = runner.invoke(app, ["gitignore", str(repo), "--json"])
    assert res_json.exit_code != 0  # because drift
    data = json.loads(res_json.output)
    assert len(data) == 1
    assert not data[0]["is_aligned"]

    # 3. Dry-run apply
    res_dry = runner.invoke(app, ["gitignore", str(repo), "--apply", "--dry-run"])
    assert res_dry.exit_code == 0
    assert "aligned" in res_dry.output
    # file not changed
    assert gi.read_text(encoding="utf-8") == "# Initial\n"

    # 4. Apply
    res_apply = runner.invoke(app, ["gitignore", str(repo), "--apply"])
    assert res_apply.exit_code == 0
    content = gi.read_text(encoding="utf-8")
    assert "temp/" in content
    assert "TODO.md" in content
    assert "todo.md" in content

    # 5. Audit after apply exits 0
    res_audit_ok = runner.invoke(app, ["gitignore", str(repo)])
    assert res_audit_ok.exit_code == 0
    assert "CONFIGURED" in res_audit_ok.output


@pytest.mark.ci
def test_repo_governance_gitignore_with_language_option(tmp_path: pathlib.Path):
    repo = tmp_path / "node-repo"
    repo.mkdir()
    (repo / ".git").mkdir()

    gi = repo / ".gitignore"
    gi.write_text("TODO.md\ntodo.md\ntemp/\n", encoding="utf-8")

    # Audit with -l node flags missing node_modules
    res = runner.invoke(app, ["gitignore", str(repo), "-l", "node", "--json"])
    assert res.exit_code != 0
    data = json.loads(res.output)
    assert data[0]["languages"] == ["node"]
    assert "node_modules/" in data[0]["missing_lines"]

    # Apply aligns node_modules/
    res_apply = runner.invoke(app, ["gitignore", str(repo), "-l", "node", "--apply"])
    assert res_apply.exit_code == 0
    assert "node_modules/" in gi.read_text(encoding="utf-8")
