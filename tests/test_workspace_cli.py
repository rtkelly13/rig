"""Tests for virtual monorepo estate workspace CLI and engine."""

import json
import pathlib

import pytest
from typer.testing import CliRunner

from rig.workspace.cli import app
from rig.workspace.engine import (
    PROJECTS_TABLE_END_MARKER,
    PROJECTS_TABLE_START_MARKER,
    load_managed_specs,
    sync_estate_code_workspace,
    sync_estate_router,
)
from tests.links import file_symlink_or_skip

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


def test_sync_estate_router_removes_a_claude_md_symlink(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    """A pre-existing symlink is cleaned up, so repeated syncs converge on absent."""
    estate_dir, config_file = mock_estate
    specs = load_managed_specs(config_file)
    sync_estate_router(estate_dir, specs)

    file_symlink_or_skip(estate_dir / "CLAUDE.md", "AGENTS.md")
    sync_estate_router(estate_dir, specs)
    assert not (estate_dir / "CLAUDE.md").is_symlink()


def test_sync_engine_router_and_workspace(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    estate_dir, config_file = mock_estate
    specs = load_managed_specs(config_file)

    agents_path = sync_estate_router(estate_dir, specs)
    assert agents_path.is_file()
    # Claude Code reads AGENTS.md natively; no CLAUDE.md shim is generated.
    assert not (estate_dir / "CLAUDE.md").exists()

    # A real CLAUDE.md is somebody's content - never delete it.
    (estate_dir / "CLAUDE.md").write_text("# hand written\n", encoding="utf-8")
    sync_estate_router(estate_dir, specs)
    assert (estate_dir / "CLAUDE.md").is_file()
    (estate_dir / "CLAUDE.md").unlink()

    content = agents_path.read_text(encoding="utf-8")
    assert "Personal code estate virtual monorepo" in content
    assert PROJECTS_TABLE_START_MARKER in content
    assert PROJECTS_TABLE_END_MARKER in content
    assert "Tech Stack" in content
    assert "Python / uv" in content
    assert "my-managed-app" in content

    ws_file = sync_estate_code_workspace(estate_dir, specs)
    assert ws_file.is_file()
    ws_data = json.loads(ws_file.read_text(encoding="utf-8"))
    assert "folders" in ws_data
    # Only present folders should be included
    folder_names = [f["name"] for f in ws_data["folders"]]
    assert "my-managed-app" in folder_names
    assert "missing-app" not in folder_names


def test_cli_sync(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    estate_dir, config_file = mock_estate
    res = runner.invoke(
        app,
        ["sync", "--root", str(estate_dir), "--config", str(config_file)],
    )
    assert res.exit_code == 0
    assert "Estate orchestration successfully synchronized" in res.stdout
    assert (estate_dir / "AGENTS.md").is_file()
    assert not (estate_dir / "CLAUDE.md").exists()
    assert (estate_dir / "personal.code-workspace").is_file()


@pytest.mark.ci
def test_root_agents_md_carries_the_estate_invariants(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    """The root AGENTS.md is fully regenerated, so every rule must live in the generator.

    Rules 4 and 5 were once hand-appended to the file and would have been silently
    destroyed by the next `workspace sync`.
    """
    estate_dir, config_file = mock_estate
    specs = load_managed_specs(config_file)

    agents_path = sync_estate_router(estate_dir, specs)
    content = agents_path.read_text(encoding="utf-8")

    assert "Brand Asset & Production Cuts Invariant" in content
    assert "production-cuts.csv" in content
    assert "Domain Skills Live in the Vault" in content
    assert "mcp__open-knowledge__skills" in content

    # Regenerating is idempotent: the invariants survive a second sync.
    sync_estate_router(estate_dir, specs)
    assert agents_path.read_text(encoding="utf-8") == content
