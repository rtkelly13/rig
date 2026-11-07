from __future__ import annotations

import json
import subprocess
from pathlib import Path

from desktop_manager.projects import (
    ManagedProject,
    ProjectInfo,
    clone_project,
    collect_linked_projects,
    discover_unlinked_projects,
    extract_repo_summary,
    generate_vscode_workspace,
    infer_repo_name,
    link_project,
    load_managed_projects,
    managed_project_statuses,
    sync_managed_projects,
)


def test_infer_repo_name_handles_https_and_dot_git() -> None:
    assert infer_repo_name("https://github.com/example/tooling.git") == "tooling"


def test_infer_repo_name_handles_ssh_style_urls() -> None:
    assert infer_repo_name("git@github.com:example/tooling.git") == "tooling"


def test_extract_repo_summary_reads_first_paragraph(tmp_path: Path) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "README.md").write_text(
        "# Repo\n\nUseful utilities for personal automation.\n\n## More\n", encoding="utf-8"
    )

    assert extract_repo_summary(repo) == "Useful utilities for personal automation."


def test_collect_linked_projects_reads_projects_dir(monkeypatch, tmp_path: Path) -> None:
    real_repo = tmp_path / "peer" / "alpha"
    real_repo.mkdir(parents=True)
    (real_repo / ".git").mkdir()

    class FakeEntry:
        name = "alpha"

        def is_dir(self) -> bool:
            return True

        def is_symlink(self) -> bool:
            return False

        def resolve(self) -> Path:
            return real_repo.resolve()

    class FakeProjectsDir:
        def exists(self) -> bool:
            return True

        def iterdir(self) -> list[FakeEntry]:
            return [FakeEntry()]

    fake_projects_dir = FakeProjectsDir()

    def fake_get_projects_dir() -> FakeProjectsDir:
        return fake_projects_dir

    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", fake_get_projects_dir)

    projects = collect_linked_projects()

    assert projects == [ProjectInfo(name="alpha", path=real_repo.resolve(), summary="", is_linked=True)]


def test_discover_unlinked_projects_skips_self_and_linked(monkeypatch, tmp_path: Path) -> None:
    root = tmp_path / "shared-utilities"
    linked_repo = tmp_path / "linked"
    unlinked_repo = tmp_path / "peer-tool"
    root.mkdir()
    linked_repo.mkdir()
    unlinked_repo.mkdir()
    (linked_repo / ".git").mkdir()
    (unlinked_repo / ".git").mkdir()

    monkeypatch.setattr("desktop_manager.projects.get_repo_root", lambda: root)
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr(
        "desktop_manager.projects.collect_linked_projects",
        lambda: [ProjectInfo(name="linked", path=linked_repo.resolve(), summary="", is_linked=True)],
    )

    projects = discover_unlinked_projects()

    assert [(project.name, project.path) for project in projects] == [("peer-tool", unlinked_repo.resolve())]


def test_link_project_creates_symlink(monkeypatch, tmp_path: Path) -> None:
    repo = tmp_path / "peer" / "tool"
    projects_dir = tmp_path / "projects"
    repo.mkdir(parents=True)
    (repo / ".git").mkdir()

    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: projects_dir)

    link_path = link_project(repo)

    assert link_path.exists()
    assert link_path.resolve() == repo.resolve()


def test_clone_project_runs_git_clone_and_links(monkeypatch, tmp_path: Path) -> None:
    peer_dir = tmp_path / "personal"
    peer_dir.mkdir()
    called: list[list[str]] = []

    def fake_run(command: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        called.append(command)
        clone_target = Path(command[-1])
        clone_target.mkdir(parents=True)
        (clone_target / ".git").mkdir()
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: peer_dir)
    monkeypatch.setattr(subprocess, "run", fake_run)
    monkeypatch.setattr(
        "desktop_manager.projects.link_project", lambda path, name=None: tmp_path / "projects" / (name or path.name)
    )

    cloned = clone_project("https://github.com/example/tooling.git")

    assert called == [["git", "clone", "https://github.com/example/tooling.git", str(peer_dir / "tooling")]]
    assert cloned.path == peer_dir / "tooling"
    assert cloned.link_path == tmp_path / "projects" / "tooling"


def test_clone_project_with_skip_lfs_writes_skip_filters_into_the_clone(monkeypatch, tmp_path: Path) -> None:
    peer_dir = tmp_path / "personal"
    peer_dir.mkdir()
    called: list[list[str]] = []

    def fake_run(command: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        called.append(command)
        Path(command[-1]).mkdir(parents=True)
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: peer_dir)
    monkeypatch.setattr(subprocess, "run", fake_run)

    clone_project("git@github.com:example/vault.git", link=False, skip_lfs=True)

    assert called == [
        [
            "git",
            "clone",
            "-c",
            "filter.lfs.smudge=git-lfs smudge --skip -- %f",
            "-c",
            "filter.lfs.process=git-lfs filter-process --skip",
            "git@github.com:example/vault.git",
            str(peer_dir / "vault"),
        ]
    ]


def test_sync_managed_projects_sets_lfs_skip_on_existing_clone(monkeypatch, tmp_path: Path) -> None:
    repo = tmp_path / "vault"
    repo.mkdir()
    subprocess.run(["git", "init", "-q", str(repo)], check=True)
    monkeypatch.setattr(
        "desktop_manager.projects.load_managed_projects",
        lambda _config=None: [ManagedProject(name="vault", repo="git@github.com:example/vault.git", skip_lfs=True)],
    )
    monkeypatch.setattr("desktop_manager.projects.collect_linked_projects", lambda: [])
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: tmp_path / "projects")

    first = sync_managed_projects(link_missing=False)
    second = sync_managed_projects(link_missing=False)

    assert first == ["set LFS skip-smudge on vault"]
    assert second == []
    smudge = subprocess.run(
        ["git", "-C", str(repo), "config", "--local", "--get", "filter.lfs.smudge"],
        check=True,
        capture_output=True,
        text=True,
    )
    assert smudge.stdout.strip() == "git-lfs smudge --skip -- %f"


def test_load_managed_projects_reads_skip_lfs(tmp_path: Path) -> None:
    config_path = tmp_path / "managed-projects.json"
    config_path.write_text('{"projects":[{"name":"vault","repo":"r","skip_lfs":true}]}', encoding="utf-8")

    assert load_managed_projects(config_path)[0].skip_lfs is True


def test_load_managed_projects_reads_config(tmp_path: Path) -> None:
    config_path = tmp_path / "managed-projects.json"
    config_path.write_text(
        '{"projects":[{"name":"obsidian-vault","repo":"git@github.com:rtkelly13/obsidian-vault.git"}]}',
        encoding="utf-8",
    )

    projects = load_managed_projects(config_path)

    assert projects == [ManagedProject(name="obsidian-vault", repo="git@github.com:rtkelly13/obsidian-vault.git")]


def test_managed_project_statuses_reports_clone_and_link(monkeypatch, tmp_path: Path) -> None:
    local_repo = tmp_path / "obsidian-vault"
    local_repo.mkdir()
    (local_repo / ".git").mkdir()
    link_path = tmp_path / "projects" / "obsidian-vault"
    link_path.parent.mkdir()
    link_path.mkdir()

    monkeypatch.setattr(
        "desktop_manager.projects.load_managed_projects",
        lambda _config=None: [
            ManagedProject(name="obsidian-vault", repo="git@github.com:rtkelly13/obsidian-vault.git")
        ],
    )
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path)
    monkeypatch.setattr(
        "desktop_manager.projects.collect_linked_projects",
        lambda: [ProjectInfo(name="obsidian-vault", path=local_repo, summary="", is_linked=True)],
    )
    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: tmp_path / "projects")

    statuses = managed_project_statuses()

    assert len(statuses) == 1
    assert statuses[0].is_cloned is True
    assert statuses[0].is_linked is True


def test_sync_managed_projects_clones_and_links_missing_repo(monkeypatch, tmp_path: Path) -> None:
    peer_dir = tmp_path / "personal"
    projects_dir = tmp_path / "projects"
    peer_dir.mkdir()
    projects_dir.mkdir()
    actions_taken: list[tuple[str, str]] = []

    monkeypatch.setattr(
        "desktop_manager.projects.load_managed_projects",
        lambda _config=None: [
            ManagedProject(name="obsidian-vault", repo="git@github.com:rtkelly13/obsidian-vault.git")
        ],
    )
    monkeypatch.setattr("desktop_manager.projects.collect_linked_projects", lambda: [])
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: peer_dir)
    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: projects_dir)

    def fake_clone(
        repo: str, *, name: str | None = None, branch: str | None = None, link: bool = True, skip_lfs: bool = False
    ):
        repo_path = peer_dir / (name or "obsidian-vault")
        repo_path.mkdir(parents=True)
        (repo_path / ".git").mkdir()
        actions_taken.append(("clone", repo))
        return repo_path, None

    def fake_link(path: Path, *, name: str | None = None) -> Path:
        actions_taken.append(("link", name or path.name))
        link_path = projects_dir / (name or path.name)
        link_path.mkdir(parents=True, exist_ok=True)
        return link_path

    monkeypatch.setattr("desktop_manager.projects.clone_project", fake_clone)
    monkeypatch.setattr("desktop_manager.projects.link_project", fake_link)

    actions = sync_managed_projects(names=["obsidian-vault"])

    assert actions == [
        f"cloned obsidian-vault -> {peer_dir / 'obsidian-vault'}",
        f"linked obsidian-vault -> {projects_dir / 'obsidian-vault'}",
    ]
    assert actions_taken == [
        ("clone", "git@github.com:rtkelly13/obsidian-vault.git"),
        ("link", "obsidian-vault"),
    ]


def test_generate_vscode_workspace_includes_root_and_sibling_repos(monkeypatch, tmp_path: Path) -> None:
    repo_root = tmp_path / "shared-utilities"
    repo_root.mkdir()
    alpha = tmp_path / "alpha"
    beta = tmp_path / "beta"

    monkeypatch.setattr("desktop_manager.projects.get_repo_root", lambda: repo_root)
    monkeypatch.setattr(
        "desktop_manager.projects.collect_linked_projects",
        lambda: [
            ProjectInfo(name="alpha", path=alpha, summary="", is_linked=True),
            ProjectInfo(name="beta", path=beta, summary="", is_linked=True),
        ],
    )

    target = generate_vscode_workspace()

    assert target == repo_root / "rig.code-workspace"
    data = json.loads(target.read_text(encoding="utf-8"))
    assert data["folders"] == [
        {"path": "."},
        {"path": "../alpha", "name": "alpha"},
        {"path": "../beta", "name": "beta"},
    ]
    assert data["settings"] == {}


def test_sync_managed_projects_never_clones_or_links_a_repo_this_machine_skips(monkeypatch, tmp_path: Path) -> None:
    home = tmp_path / "workspace-home"
    home.mkdir()
    (home / "config.json").write_text('{"tools": {"estate": {"skip_repos": ["hearth"]}}}', encoding="utf-8")
    monkeypatch.setenv("WORKSPACE_HOME", str(home))
    monkeypatch.setattr(
        "desktop_manager.projects.load_managed_projects",
        lambda _config=None: [ManagedProject(name="hearth", repo="git@github.com:rtkelly13/hearth.git")],
    )
    monkeypatch.setattr("desktop_manager.projects.collect_linked_projects", lambda: [])
    monkeypatch.setattr("desktop_manager.projects.get_peer_dir", lambda: tmp_path / "personal")
    monkeypatch.setattr("desktop_manager.projects.get_projects_dir", lambda: tmp_path / "projects")

    def refuse(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("a skipped repo must not be cloned or linked")

    monkeypatch.setattr("desktop_manager.projects.clone_project", refuse)
    monkeypatch.setattr("desktop_manager.projects.link_project", refuse)

    actions = sync_managed_projects(names=["hearth"])

    assert actions == ["skipped hearth: listed in tools.estate.skip_repos on this machine"]
