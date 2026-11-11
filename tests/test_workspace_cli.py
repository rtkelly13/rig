"""Tests for virtual monorepo estate workspace CLI and engine."""

import json
import pathlib

import pytest
from typer.testing import CliRunner

from rig.workspace.engine import load_managed_specs

runner = CliRunner()


@pytest.fixture
def mock_estate(tmp_path: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    """Sets up a temporary estate with managed clones, worktrees, and unmanaged items."""
    estate_dir = tmp_path / "personal"
    estate_dir.mkdir()

    # 1. Managed repo clone
    managed_repo = estate_dir / "my-managed-app"
    managed_repo.mkdir()
    (managed_repo / ".git").mkdir()

    # 2. Linked worktree (where .git is a file)
    worktree = estate_dir / "my-managed-app-golden"
    worktree.mkdir()
    (worktree / ".git").write_text("gitdir: /some/path\n")

    # 3. Known system files
    (estate_dir / "personal.code-workspace").write_text("{}")
    (estate_dir / ".claude").mkdir()

    # 4. Unmanaged repo
    unmanaged_repo = estate_dir / "rogue-repo"
    unmanaged_repo.mkdir()
    (unmanaged_repo / ".git").mkdir()

    # 5. Unmanaged stray dir
    unmanaged_dir = estate_dir / "random-scratch"
    unmanaged_dir.mkdir()

    # 6. Unmanaged stray file
    (estate_dir / "junk.txt").write_text("debris")

    # Managed projects JSON
    config_file = tmp_path / "managed-projects.json"
    config_payload = {
        "projects": [
            {
                "name": "my-managed-app",
                "repo": "git@github.com:rtkelly13/my-managed-app.git",
                "description": "Core app",
                "domain": "Application",
                "stack": "Python / uv",
            },
            {
                "name": "missing-app",
                "repo": "git@github.com:rtkelly13/missing-app.git",
                "description": "Remote only",
                "domain": "Service",
                "stack": ".NET 10 / C#",
            },
        ]
    }
    config_file.write_text(json.dumps(config_payload), encoding="utf-8")

    return estate_dir, config_file


def test_load_managed_specs(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    _, config_file = mock_estate
    specs = load_managed_specs(config_file)
    assert len(specs) == 2
    assert specs[0].name == "my-managed-app"
    assert specs[0].stack == "Python / uv"
    assert specs[1].name == "missing-app"
    assert specs[1].stack == ".NET 10 / C#"
