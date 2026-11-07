"""`origin_args` points `gh repo view` at origin, which in a fork clone is not what gh would pick."""

import pathlib

import pytest

from rig import proc
from rig.gh_origin import origin_args


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
