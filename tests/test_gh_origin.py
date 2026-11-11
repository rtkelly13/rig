"""`origin_args` points `gh repo view` at origin, which in a fork clone is not what gh would pick."""

import pathlib

import pytest

from rig import proc
from rig.gh_origin import RepoIdentity, get_repo_root_and_origin, origin_args


def _git(repo: pathlib.Path, *args: str) -> None:
    proc.git(*args, cwd=repo, check=True)


@pytest.mark.ci
def test_a_fork_clone_names_origin_even_with_an_upstream_remote(tmp_path: pathlib.Path):
    _git(tmp_path, "init", "-q")
    _git(tmp_path, "remote", "add", "upstream", "https://github.com/aloneguid/parquet-dotnet.git")
    _git(tmp_path, "remote", "add", "origin", "https://github.com/rtkelly-labs/parquet-dotnet.git")
    assert origin_args(tmp_path) == ["https://github.com/rtkelly-labs/parquet-dotnet.git"]


@pytest.mark.ci
def test_no_origin_leaves_resolution_to_gh(tmp_path: pathlib.Path):
    _git(tmp_path, "init", "-q")
    assert origin_args(tmp_path) == []


@pytest.mark.ci
def test_not_a_repository_leaves_resolution_to_gh(tmp_path: pathlib.Path):
    assert origin_args(tmp_path) == []


@pytest.mark.ci
def test_a_worktree_answers_for_its_main_checkout(tmp_path: pathlib.Path):
    main = tmp_path / "rig"
    main.mkdir()
    _git(main, "init", "-q")
    _git(main, "remote", "add", "origin", "git@github.com:rtkelly13/rig.git")
    _git(
        main,
        "-c",
        "user.name=t",
        "-c",
        "user.email=t@example.invalid",
        "-c",
        "commit.gpgsign=false",
        "commit",
        "-q",
        "--allow-empty",
        "-m",
        "root",
    )
    _git(main, "worktree", "add", "-q", str(tmp_path / "wt"))

    identity = get_repo_root_and_origin(tmp_path / "wt")

    assert identity.root is not None
    assert identity.root.resolve() == main.resolve()
    assert identity.origin_url == "git@github.com:rtkelly13/rig.git"


@pytest.mark.ci
def test_outside_a_repository_there_is_no_identity(tmp_path: pathlib.Path):
    assert get_repo_root_and_origin(tmp_path) == RepoIdentity(root=None, origin_url=None)
