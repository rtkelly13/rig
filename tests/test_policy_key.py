"""`policy_key`: the key a local checkout's governance policy is looked up by."""

from __future__ import annotations

import pathlib

import pytest

from rig import proc
from rig.repo_governance.policy_key import bare_name, names_match, policy_key, policy_key_from


@pytest.mark.ci
@pytest.mark.parametrize(
    "remote",
    [
        "git@github.com:rtkelly-labs/oxbow.git",
        "https://github.com/rtkelly-labs/oxbow.git",
        "https://github.com/rtkelly-labs/oxbow",
        "ssh://git@github.com/rtkelly-labs/oxbow.git",
    ],
)
def test_key_is_the_remote_slug(remote: str):
    assert policy_key_from(pathlib.Path("/code/rtkelly-labs/oxbow"), remote) == "rtkelly-labs/oxbow"


@pytest.mark.ci
def test_key_takes_the_remote_name_not_a_renamed_folder():
    key = policy_key_from(pathlib.Path("/code/personal/old-name"), "git@github.com:rtkelly13/new-name.git")
    assert key == "rtkelly13/new-name"


@pytest.mark.ci
def test_without_a_github_remote_an_org_folder_supplies_the_owner():
    assert policy_key_from(pathlib.Path("/code/rtkelly-labs/Lake.NET"), None) == "rtkelly-labs/Lake.NET"
    assert policy_key_from(pathlib.Path("/code/RTKELLY-LABS/tool"), "https://gitlab.com/a/b") == "rtkelly-labs/tool"


@pytest.mark.ci
def test_otherwise_the_key_is_the_bare_folder_name():
    assert policy_key_from(pathlib.Path("/code/personal/blog"), None) == "blog"


def _git(cwd: pathlib.Path, *args: str) -> None:
    proc.git(*args, cwd=cwd, check=True)


@pytest.mark.ci
def test_policy_key_reads_origin_from_a_checkout_and_its_worktree(tmp_path: pathlib.Path):
    repo = tmp_path / "personal" / "folder"
    repo.mkdir(parents=True)
    _git(repo, "init", "-q", "-b", "main")
    _git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init")
    _git(repo, "remote", "add", "origin", "git@github.com:rtkelly-labs/oxbow.git")
    worktree = tmp_path / "wt"
    _git(repo, "worktree", "add", "-q", str(worktree), "-b", "side")

    assert policy_key(repo) == "rtkelly-labs/oxbow"
    assert policy_key(worktree) == "rtkelly-labs/oxbow"


@pytest.mark.ci
def test_policy_key_does_not_ask_git_about_a_plain_folder(tmp_path: pathlib.Path):
    plain = tmp_path / "rtkelly-labs" / "not-a-repo"
    plain.mkdir(parents=True)
    assert policy_key(plain) == "rtkelly-labs/not-a-repo"


@pytest.mark.ci
def test_names_match_ignores_case_and_owner():
    assert names_match(["Lake.NET", "oxbow"], "rtkelly-labs/lake.net")
    assert names_match(["Lake.NET"], "LAKE.NET")
    assert not names_match(["Lake.NET"], "rtkelly-labs/lake")
    assert bare_name("rtkelly-labs/Lake.NET") == "lake.net"
