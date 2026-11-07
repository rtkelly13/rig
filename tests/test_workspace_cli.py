"""Tests for virtual monorepo estate workspace CLI and engine."""

import json
import pathlib

import pytest
from typer.testing import CliRunner

from rig.workspace.cli import app
from rig.workspace.engine import (
    PROJECTS_TABLE_END_MARKER,
    PROJECTS_TABLE_START_MARKER,
    audit_estate,
    clean_estate_item,
    load_managed_specs,
    sync_estate_code_workspace,
    sync_estate_router,
)
from rig.workspace.models import (
    EstateItem,
    EstateItemKind,
    ManagedRepoSpec,
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


def test_audit_estate(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    estate_dir, config_file = mock_estate
    specs = load_managed_specs(config_file)
    result = audit_estate(estate_dir, specs=specs)

    assert not result.is_clean
    assert len(result.managed_repos) == 1
    assert result.managed_repos[0].name == "my-managed-app"
    assert len(result.linked_worktrees) == 1
    assert result.linked_worktrees[0].name == "my-managed-app-golden"
    assert len(result.known_system_items) == 2
    assert len(result.missing_managed_repos) == 1
    assert result.missing_managed_repos[0].name == "missing-app"

    unmanaged_names = {item.name for item in result.unmanaged_items}
    assert unmanaged_names == {"rogue-repo", "random-scratch", "junk.txt"}


def test_audit_estate_never_reports_a_repo_this_machine_skips_as_missing(
    mock_estate: tuple[pathlib.Path, pathlib.Path], monkeypatch: pytest.MonkeyPatch
):
    estate_dir, config_file = mock_estate
    home = estate_dir.parent / "workspace-home"
    home.mkdir()
    _ = (home / "config.json").write_text('{"tools": {"estate": {"skip_repos": ["missing-app"]}}}', encoding="utf-8")
    monkeypatch.setenv("WORKSPACE_HOME", str(home))

    result = audit_estate(estate_dir, specs=load_managed_specs(config_file))

    assert result.missing_managed_repos == []


def test_audit_estate_still_inventories_an_existing_org_clone_this_machine_skips(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
):
    estate_dir = tmp_path / "personal"
    estate_dir.mkdir()
    org_clone = tmp_path / "rtkelly-labs" / "Diffra"
    (org_clone / ".git").mkdir(parents=True)
    config_file = tmp_path / "managed-projects.json"
    _ = config_file.write_text(
        json.dumps({"projects": [{"name": "Diffra", "repo": "git@github.com:rtkelly-labs/Diffra.git"}]}),
        encoding="utf-8",
    )
    home = tmp_path / "workspace-home"
    home.mkdir()
    _ = (home / "config.json").write_text('{"tools": {"estate": {"skip_repos": ["Diffra"]}}}', encoding="utf-8")
    monkeypatch.setenv("WORKSPACE_HOME", str(home))

    result = audit_estate(estate_dir, specs=load_managed_specs(config_file))

    assert [item.path for item in result.managed_repos] == [org_clone]
    assert result.missing_managed_repos == []


def test_clean_estate_item(tmp_path: pathlib.Path):
    # 1. Unmanaged file
    stray_file = tmp_path / "stray.log"
    stray_file.write_text("temp")
    item_file = EstateItem(
        name="stray.log",
        path=stray_file,
        kind=EstateItemKind.UNMANAGED_FILE,
        is_dir=False,
    )
    outcome = clean_estate_item(item_file, dry_run=True)
    assert outcome.ok
    assert "[dry-run]" in outcome.message
    assert stray_file.exists()

    outcome = clean_estate_item(item_file, dry_run=False)
    assert outcome.ok
    assert not stray_file.exists()

    # 2. Refusal on managed item
    managed_item = EstateItem(
        name="managed",
        path=tmp_path,
        kind=EstateItemKind.MANAGED_REPO,
        is_dir=True,
    )
    outcome = clean_estate_item(managed_item, dry_run=False)
    assert not outcome.ok
    assert "Refused" in outcome.message


def test_cli_audit_and_json(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    estate_dir, config_file = mock_estate
    res = runner.invoke(
        app,
        ["audit", "--root", str(estate_dir), "--config", str(config_file), "--json"],
    )
    assert res.exit_code == 0
    data = json.loads(res.stdout)
    assert not data["is_clean"]
    assert len(data["managed_repos"]) == 1
    assert len(data["unmanaged_items"]) == 3


def test_cli_audit_strict(mock_estate: tuple[pathlib.Path, pathlib.Path]):
    estate_dir, config_file = mock_estate
    res = runner.invoke(
        app,
        ["audit", "--root", str(estate_dir), "--config", str(config_file), "--strict"],
    )
    assert res.exit_code == 1


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


@pytest.mark.ci
def test_known_system_entries_recognizes_pnpm_store_and_temp(tmp_path: pathlib.Path):
    estate_dir = tmp_path / "personal"
    estate_dir.mkdir()
    (estate_dir / ".pnpm-store").mkdir()
    (estate_dir / "temp").mkdir()

    result = audit_estate(estate_dir, specs=[])
    known_names = {item.name for item in result.known_system_items}
    assert ".pnpm-store" in known_names
    assert "temp" in known_names
    assert len(result.unmanaged_items) == 0


@pytest.mark.ci
def test_retained_archives_recognized_and_protected_from_clean(tmp_path: pathlib.Path):
    """Archived repositories marked enabled=false must not be treated as unmanaged nor deleted by clean."""
    estate_dir = tmp_path / "personal"
    estate_dir.mkdir()

    archive_repo = estate_dir / "notes-old-clone"
    archive_repo.mkdir()
    (archive_repo / ".git").mkdir()

    archive_backup = estate_dir / "obsidian-backup-clean"
    archive_backup.mkdir()

    specs = [
        ManagedRepoSpec(
            name="notes-old-clone",
            repo="git@github.com:rtkelly13/notes.git",
            description="Legacy notes repository clone preserved for migration reference.",
            enabled=False,
        ),
        ManagedRepoSpec(
            name="obsidian-backup-clean",
            repo="local/obsidian-backup-clean",
            description="Clean snapshot backup of Obsidian vault notes.",
            enabled=False,
        ),
    ]

    result = audit_estate(estate_dir, specs=specs)
    retained_names = {item.name for item in result.retained_archives}
    assert "notes-old-clone" in retained_names
    assert "obsidian-backup-clean" in retained_names
    assert len(result.unmanaged_items) == 0
    assert result.is_clean

    # Ensure clean refuses to remove retained archives
    for item in result.retained_archives:
        outcome = clean_estate_item(item, dry_run=False)
        assert not outcome.ok
        assert "Refused" in outcome.message
        assert item.path.exists()


@pytest.mark.ci
def test_projects_in_the_archive_file_stay_retained_not_unmanaged(tmp_path: pathlib.Path):
    # Arrange
    config = tmp_path / "managed-projects.json"
    _ = config.write_text(json.dumps({"projects": [{"name": "live", "repo": "git@github.com:o/live.git"}]}))
    retired = {"projects": [{"name": "old", "repo": "git@github.com:o/old.git", "status": "archived"}]}
    _ = (tmp_path / "archived-projects.json").write_text(json.dumps(retired))
    estate_dir = tmp_path / "personal"
    (estate_dir / "old" / ".git").mkdir(parents=True)

    # Act
    result = audit_estate(estate_dir, specs=load_managed_specs(config))

    # Assert
    assert [item.name for item in result.retained_archives] == ["old"], (
        "a clone of a retired project must be a retained archive, never unmanaged and offered for deletion"
    )
