"""Org-owned managed projects live in a sibling folder named for the org."""

import json
import pathlib

import pytest

from rig.repo_governance.gitignore_manager import audit_estate_gitignores
from rig.workspace.engine import audit_estate, render_managed_projects_table, sync_estate_code_workspace
from rig.workspace.layout import org_folder, project_dir, project_relpath
from rig.workspace.models import ManagedRepoSpec

PERSONAL = ManagedRepoSpec(name="blog", repo="git@github.com:rtkelly13/blog.git")
LABS = ManagedRepoSpec(name="Diffra", repo="git@github.com:rtkelly-labs/Diffra.git")
RESULTFUL = ManagedRepoSpec(name="Resultful", repo="https://github.com/resultful/Resultful")
FORK = ManagedRepoSpec(name="starter", repo="git@github.com:timlrx/starter.git")


def _clone(path: pathlib.Path) -> None:
    (path / ".git").mkdir(parents=True)


@pytest.mark.ci
@pytest.mark.parametrize(
    ("spec", "folder"),
    [(PERSONAL, None), (LABS, "rtkelly-labs"), (RESULTFUL, "Resultful"), (FORK, None)],
)
def test_org_folder_follows_the_github_owner(spec: ManagedRepoSpec, folder: str | None):
    assert org_folder(spec.repo) == folder


@pytest.mark.ci
def test_project_paths(tmp_path: pathlib.Path):
    estate = tmp_path / "personal"

    assert project_dir(estate, PERSONAL.name, PERSONAL.repo) == estate / "blog"
    assert project_dir(estate, LABS.name, LABS.repo) == tmp_path / "rtkelly-labs" / "Diffra"
    assert project_dir(estate, FORK.name, FORK.repo) == estate / "starter"
    assert project_relpath(RESULTFUL.name, RESULTFUL.repo) == "../Resultful/Resultful"


@pytest.mark.ci
def test_audit_finds_org_repos_and_misses_absent_ones(tmp_path: pathlib.Path):
    estate = tmp_path / "personal"
    _clone(estate / "blog")
    _clone(tmp_path / "rtkelly-labs" / "Diffra")

    result = audit_estate(estate, specs=[PERSONAL, LABS, RESULTFUL])

    assert {item.name: item.path for item in result.managed_repos} == {
        "blog": estate / "blog",
        "Diffra": tmp_path / "rtkelly-labs" / "Diffra",
    }
    assert [spec.name for spec in result.missing_managed_repos] == ["Resultful"]
    assert result.unmanaged_items == []


@pytest.mark.ci
def test_router_table_links_into_org_folders():
    table = render_managed_projects_table([PERSONAL, LABS])

    assert "[`blog`](./blog)" in table
    assert "[`Diffra`](../rtkelly-labs/Diffra)" in table
    assert "(../rtkelly-labs/Diffra/AGENTS.md)" in table


@pytest.mark.ci
def test_code_workspace_points_at_org_folders(tmp_path: pathlib.Path):
    estate = tmp_path / "personal"
    _clone(estate / "blog")
    _clone(tmp_path / "Resultful" / "Resultful")

    workspace = sync_estate_code_workspace(estate, [PERSONAL, RESULTFUL, LABS])
    folders = json.loads(workspace.read_text(encoding="utf-8"))["folders"]

    assert folders == [
        {"name": "blog", "path": "blog"},
        {"name": "Resultful", "path": "../Resultful/Resultful"},
    ]


@pytest.mark.ci
def test_gitignore_sweep_includes_org_folders(tmp_path: pathlib.Path):
    estate = tmp_path / "personal"
    _clone(estate / "blog")
    _clone(tmp_path / "rtkelly-labs" / "Diffra")

    swept = {report.repo_name for report in audit_estate_gitignores(estate)}

    assert swept == {"blog", "Diffra"}
