"""Tests for the projects CLI commands and workspace project management commands.

Covers:
- ``projects list``
- ``projects discover``
- ``projects link``
- ``projects link-all``
- ``projects clone``
- ``projects unlink``
- ``workspace projects``
- ``workspace sync-projects``
- ``workspace clone-project``
- ``workspace detect-app``
"""

from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

from desktop_manager.projects import ClonedProject

pytestmark = pytest.mark.usefixtures("as_windows")

# ---------------------------------------------------------------------------
# projects list
# ---------------------------------------------------------------------------


def test_projects_list_shows_linked_projects(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.projects import ProjectInfo

    runner = CliRunner()
    monkeypatch.setattr(
        "desktop_manager.projects.collect_linked_projects",
        lambda: [ProjectInfo(name="tooling", path=tmp_path / "tooling", summary="Useful tools.", is_linked=True)],
    )

    result = runner.invoke(app, ["projects", "list"])

    assert result.exit_code == 0
    assert "tooling" in result.stdout


def test_projects_list_shows_empty_message_when_no_projects(monkeypatch) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.projects.collect_linked_projects", lambda: [])
    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: Path("/fake/projects"))

    result = runner.invoke(app, ["projects", "list"])

    assert result.exit_code == 0
    assert "No projects linked" in result.stdout


# ---------------------------------------------------------------------------
# projects discover
# ---------------------------------------------------------------------------


def test_projects_discover_shows_unlinked_repos(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.projects import ProjectInfo

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr(
        "desktop_manager.projects.discover_unlinked_projects",
        lambda: [ProjectInfo(name="new-repo", path=tmp_path / "new-repo", summary="A new repo.", is_linked=False)],
    )

    result = runner.invoke(app, ["projects", "discover"])

    assert result.exit_code == 0
    assert "new-repo" in result.stdout
    assert "1" in result.stdout


def test_projects_discover_shows_all_linked_message(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr("desktop_manager.projects.discover_unlinked_projects", lambda: [])

    result = runner.invoke(app, ["projects", "discover"])

    assert result.exit_code == 0
    assert "already linked" in result.stdout


# ---------------------------------------------------------------------------
# projects link
# ---------------------------------------------------------------------------


def test_projects_link_creates_link_successfully(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    target = tmp_path / "tooling"
    target.mkdir()

    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr(
        "desktop_manager.projects.link_project",
        lambda path, name=None: tmp_path / "projects" / (name or path.name),
    )

    result = runner.invoke(app, ["projects", "link", "--name", "tooling"])

    assert result.exit_code == 0
    assert "Linked" in result.stdout
    assert "tooling" in result.stdout


def test_projects_link_reports_missing_target(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)

    result = runner.invoke(app, ["projects", "link", "--name", "nonexistent"])

    assert result.exit_code == 1
    assert "Not found" in result.stdout


def test_projects_link_reports_already_exists_error(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    target = tmp_path / "tooling"
    target.mkdir()

    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr(
        "desktop_manager.projects.link_project",
        lambda path, name=None: (_ for _ in ()).throw(FileExistsError("Already exists: /projects/tooling")),
    )

    result = runner.invoke(app, ["projects", "link", "--name", "tooling"])

    assert result.exit_code == 1
    assert "Error" in result.stdout


# ---------------------------------------------------------------------------
# projects link-all
# ---------------------------------------------------------------------------


def test_projects_link_all_links_all_discovered(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.projects import ProjectInfo

    runner = CliRunner()
    linked: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.projects.discover_unlinked_projects",
        lambda: [
            ProjectInfo(name="alpha", path=tmp_path / "alpha", summary="", is_linked=False),
            ProjectInfo(name="beta", path=tmp_path / "beta", summary="", is_linked=False),
        ],
    )

    def fake_link(path: Path, name: str | None = None) -> Path:
        linked.append(name or path.name)
        return tmp_path / "projects" / (name or path.name)

    monkeypatch.setattr("desktop_manager.projects.link_project", fake_link)

    result = runner.invoke(app, ["projects", "link-all"])

    assert result.exit_code == 0
    assert "2" in result.stdout
    assert linked == ["alpha", "beta"]


def test_projects_link_all_fails_when_a_project_is_skipped(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.projects import ProjectInfo

    runner = CliRunner()
    linked: list[str] = []

    monkeypatch.setattr(
        "desktop_manager.projects.discover_unlinked_projects",
        lambda: [
            ProjectInfo(name="alpha", path=tmp_path / "alpha", summary="", is_linked=False),
            ProjectInfo(name="beta", path=tmp_path / "beta", summary="", is_linked=False),
        ],
    )

    def fake_link(path: Path, name: str | None = None) -> Path:
        if name == "alpha":
            raise FileExistsError("projects/alpha already exists")
        linked.append(name or path.name)
        return tmp_path / "projects" / (name or path.name)

    monkeypatch.setattr("desktop_manager.projects.link_project", fake_link)

    result = runner.invoke(app, ["projects", "link-all"])

    assert result.exit_code == 1
    assert "Skipped" in result.stdout
    assert "1 project(s) skipped" in result.stdout
    assert linked == ["beta"]


def test_projects_link_all_shows_none_found_message(monkeypatch) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr("desktop_manager.projects.discover_unlinked_projects", lambda: [])

    result = runner.invoke(app, ["projects", "link-all"])

    assert result.exit_code == 0
    assert "already linked" in result.stdout


# ---------------------------------------------------------------------------
# projects clone
# ---------------------------------------------------------------------------


def test_projects_clone_clones_and_links_repo(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    cloned_to = tmp_path / "personal" / "tooling"
    link_to = tmp_path / "projects" / "tooling"

    monkeypatch.setattr(
        "desktop_manager.projects.clone_project",
        lambda repo, name=None, branch=None, link=True: ClonedProject(cloned_to, link_to),
    )

    result = runner.invoke(app, ["projects", "clone", "--repo", "https://github.com/example/tooling.git"])

    assert result.exit_code == 0
    assert "Cloned" in result.stdout
    assert "Linked" in result.stdout


def test_projects_clone_reports_error_on_failure(monkeypatch, tmp_path: Path) -> None:
    from subprocess import CalledProcessError

    from desktop_manager.cli import app

    runner = CliRunner()

    def fail(*args, **kwargs):
        raise CalledProcessError(128, "git", "fatal: repository not found")

    monkeypatch.setattr("desktop_manager.projects.clone_project", fail)

    result = runner.invoke(app, ["projects", "clone", "--repo", "https://github.com/example/does-not-exist.git"])

    assert result.exit_code == 1
    assert "Error" in result.stdout


# ---------------------------------------------------------------------------
# projects unlink
# ---------------------------------------------------------------------------


def test_projects_unlink_removes_link(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    link_path = tmp_path / "projects" / "tooling"

    monkeypatch.setattr("desktop_manager.projects.unlink_project", lambda name: link_path)

    result = runner.invoke(app, ["projects", "unlink", "--name", "tooling"])

    assert result.exit_code == 0
    assert "Unlinked" in result.stdout
    assert "tooling" in result.stdout


def test_projects_unlink_reports_not_found_error(monkeypatch) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr(
        "desktop_manager.projects.unlink_project",
        lambda name: (_ for _ in ()).throw(FileNotFoundError(f"No linked project named '{name}'")),
    )

    result = runner.invoke(app, ["projects", "unlink", "--name", "missing"])

    assert result.exit_code == 1
    assert "Error" in result.stdout


# ---------------------------------------------------------------------------
# workspace projects
# ---------------------------------------------------------------------------


def test_workspace_projects_shows_status_table(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.projects import ManagedProject, ManagedProjectStatus

    runner = CliRunner()
    project = ManagedProject(name="obsidian-vault", repo="git@github.com:rtkelly13/obsidian-vault.git")
    status = ManagedProjectStatus(
        project=project,
        local_path=tmp_path / "obsidian-vault",
        link_path=tmp_path / "projects" / "obsidian-vault",
        is_cloned=True,
        is_linked=True,
    )

    monkeypatch.setattr("desktop_manager.projects.managed_project_statuses", lambda config_path=None: [status])
    monkeypatch.setattr(
        "desktop_manager.projects.managed_projects_config_path",
        lambda: tmp_path / "config" / "managed-projects.json",
    )

    result = runner.invoke(app, ["workspace", "projects"])

    assert result.exit_code == 0
    assert "obsidian-vault" in result.stdout


# ---------------------------------------------------------------------------
# workspace sync-projects
# ---------------------------------------------------------------------------


def test_workspace_sync_projects_shows_actions(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr(
        "desktop_manager.projects.sync_managed_projects",
        lambda names=None, clone_missing=True, link_missing=True: ["cloned obsidian-vault -> /personal/obsidian-vault"],
    )
    monkeypatch.setattr(
        "desktop_manager.projects.managed_projects_config_path",
        lambda: tmp_path / "config" / "managed-projects.json",
    )

    result = runner.invoke(app, ["workspace", "sync-projects"])

    assert result.exit_code == 0
    assert "obsidian-vault" in result.stdout


def test_workspace_sync_projects_shows_up_to_date_message(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    monkeypatch.setattr(
        "desktop_manager.projects.sync_managed_projects",
        lambda names=None, clone_missing=True, link_missing=True: [],
    )
    monkeypatch.setattr(
        "desktop_manager.projects.managed_projects_config_path",
        lambda: tmp_path / "config" / "managed-projects.json",
    )

    result = runner.invoke(app, ["workspace", "sync-projects"])

    assert result.exit_code == 0
    assert "already match" in result.stdout


# ---------------------------------------------------------------------------
# workspace clone-project
# ---------------------------------------------------------------------------


def test_workspace_clone_project_calls_sync(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()
    calls: list[dict] = []

    def fake_sync(names=None, clone_missing=True, link_missing=True):
        calls.append({"names": names, "clone_missing": clone_missing, "link_missing": link_missing})
        return [f"cloned {names[0]} -> {tmp_path / (names[0] if names else 'unknown')}"]

    monkeypatch.setattr("desktop_manager.projects.sync_managed_projects", fake_sync)

    result = runner.invoke(app, ["workspace", "clone-project", "--name", "obsidian-vault"])

    assert result.exit_code == 0
    assert calls == [{"names": ["obsidian-vault"], "clone_missing": True, "link_missing": True}]
    assert "obsidian-vault" in result.stdout


# ---------------------------------------------------------------------------
# workspace detect-app  (Windows-only; non-Windows path raises Exit(1))
# ---------------------------------------------------------------------------


def test_detect_app_reports_missing_config(tmp_path: Path) -> None:
    from desktop_manager.cli import app

    runner = CliRunner()

    result = runner.invoke(
        app,
        [
            "workspace",
            "detect-app",
            "--config",
            str(tmp_path / "missing.json"),
        ],
    )

    assert result.exit_code == 1
    assert "Config not found" in result.stdout


def test_detect_app_reports_no_matching_entries(monkeypatch, tmp_path: Path) -> None:
    from desktop_manager.cli import app
    from desktop_manager.windows_workspace import WorkspacePlan

    runner = CliRunner()
    config = tmp_path / "workspace.json"
    config.write_text('{"desktops":[{"name":"Work","launch":[]}]}', encoding="utf-8")

    monkeypatch.setattr("desktop_manager.windows_workspace.load_plan", lambda path: WorkspacePlan(desktops=[]))

    result = runner.invoke(app, ["workspace", "detect-app", "--config", str(config)])

    assert result.exit_code == 1
    assert "No matching entries" in result.stdout
