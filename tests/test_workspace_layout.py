"""Org-owned managed projects live in a sibling folder named for the org."""

import pathlib

import pytest

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
