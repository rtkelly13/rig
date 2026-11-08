"""Tests for `estate history`: reading a restart from the new root, readying a clone, sorting old branches."""

from __future__ import annotations

import dataclasses
import pathlib

import pytest

from rig import git_hooks, proc
from rig.estate import history
from rig.repo_slug import RepoSlug

URL = "git@github.com:o/r.git"
MESSAGE = "r: a fresh start from o/r-archive@abc1234\n\nHistory is in o/r-archive.\n"


def _restart() -> history.Restart:
    restart = history.parse_restart(url=URL, root="root", message=MESSAGE, date="2026-11-07")
    assert restart is not None
    return restart


# ---------------------------------------------------------------- reading the restart


@pytest.mark.ci
def test_the_root_commit_names_the_archive_and_old_main() -> None:
    restart = _restart()

    assert (restart.slug, restart.archive_slug) == (RepoSlug("o", "r"), RepoSlug("o", "r-archive"))
    assert (restart.root, restart.old_main) == ("root", "abc1234")
    assert restart.archive_url == "git@github.com:o/r-archive.git"


@pytest.mark.ci
def test_the_archive_url_keeps_the_clone_transport() -> None:
    restart = history.parse_restart(url="https://github.com/O/R", root="x", message=MESSAGE)

    assert restart is not None
    assert restart.archive_url == "https://github.com/o/r-archive"


@pytest.mark.ci
@pytest.mark.parametrize(
    "message",
    ["initial commit", "a fresh start from o/r@abc1234", "a fresh start from nowhere@abc1234"],
    ids=["ordinary root", "names itself", "not a slug"],
)
def test_a_root_that_names_no_archive_is_no_restart(message: str) -> None:
    assert history.parse_restart(url=URL, root="x", message=message) is None


# ---------------------------------------------------------------- readying a clone


def _ready() -> history.CloneFacts:
    return history.CloneFacts(
        remotes={"origin": URL, "archive": "git@github.com:o/r-archive.git"},
        guard_root="root",
        guard_slug="o/r",
        hook_current=True,
        has_old_main=True,
        main_on_new_history=True,
        main_upstream="origin/main",
    )


@pytest.mark.ci
def test_a_ready_clone_needs_nothing() -> None:
    assert history.plan_setup(_restart(), _ready()) == history.SetupPlan()


@pytest.mark.ci
def test_a_clone_from_before_the_restart_gets_the_archive_the_guard_and_the_hook() -> None:
    facts = history.CloneFacts(remotes={"origin": URL})

    plan = history.plan_setup(_restart(), facts)

    assert [step.git for step in plan.steps] == [
        ("remote", "add", "archive", "git@github.com:o/r-archive.git"),
        ("fetch", "archive"),
        ("config", git_hooks.HISTORY_ROOT_KEY, "root"),
        ("config", git_hooks.HISTORY_SLUG_KEY, "o/r"),
        (),
    ]


@pytest.mark.ci
def test_an_old_main_is_advised_never_reset() -> None:
    facts = dataclasses.replace(_ready(), main_on_new_history=False)

    plan = history.plan_setup(_restart(), facts)

    assert plan.steps == ()
    assert "git reset --hard origin/main" in plan.advice[0]


@pytest.mark.ci
def test_an_archive_remote_pointing_elsewhere_is_left_to_a_person() -> None:
    facts = history.CloneFacts(remotes={"origin": URL, "archive": "git@github.com:x/y.git"}, has_old_main=True)

    plan = history.plan_setup(_restart(), facts)

    assert not any(step.git[:2] == ("remote", "add") for step in plan.steps)
    assert "x/y" in plan.advice[0]


# ---------------------------------------------------------------- sorting branches


def _branch(**kw: object) -> history.BranchFacts:
    base: dict[str, object] = {"name": "b", "sha": "s", "on_new_history": False, "ahead_of_old_main": 2}
    return history.BranchFacts(**{**base, **kw})  # pyright: ignore[reportArgumentType]


@pytest.mark.ci
@pytest.mark.parametrize(
    ("branch", "kind"),
    [
        (_branch(on_new_history=True, ahead_of_old_main=0), history.Kind.CURRENT),
        (_branch(ahead_of_old_main=0), history.Kind.RETIRED),
        (_branch(in_old_main=True), history.Kind.RETIRED),
        (_branch(archive_pr=history.ArchivePr(1, "MERGED", "u")), history.Kind.RETIRED),
        (_branch(archive_pr=history.ArchivePr(1, "CLOSED", "u")), history.Kind.RETIRED),
        (_branch(archive_pr=history.ArchivePr(1, "OPEN", "u")), history.Kind.TO_PORT),
        (_branch(), history.Kind.TO_PORT),
    ],
    ids=["new history", "in old main", "squashed in", "pr merged", "pr closed", "pr open", "unmerged"],
)
def test_classify(branch: history.BranchFacts, kind: history.Kind) -> None:
    assert history.classify(branch) is kind


@pytest.mark.ci
def test_port_rebases_the_branch_own_commits_onto_the_new_main(tmp_path: pathlib.Path) -> None:
    plan = history.plan_port(_restart(), _branch(), here=tmp_path, here_dirty=False)

    assert plan.rebase == ("rebase", "--onto", "origin/main", "abc1234", "b")
    assert plan.switches_cwd


@pytest.mark.ci
def test_a_stacked_branch_ports_from_its_parent(tmp_path: pathlib.Path) -> None:
    plan = history.plan_port(_restart(), _branch(), here=tmp_path, here_dirty=False, base="parent")

    assert plan.rebase[3] == "parent"


@pytest.mark.ci
@pytest.mark.parametrize(
    ("branch", "dirty", "reason"),
    [
        (_branch(worktree=pathlib.Path("/elsewhere")), False, "checked out in"),
        (_branch(), True, "uncommitted"),
        (_branch(ahead_of_old_main=0), False, "nothing to port"),
    ],
    ids=["another worktree", "dirty", "retired"],
)
def test_port_refuses(tmp_path: pathlib.Path, branch: history.BranchFacts, dirty: bool, reason: str) -> None:
    plan = history.plan_port(_restart(), branch, here=tmp_path, here_dirty=dirty)

    assert reason in plan.refused
    assert plan.rebase == ()


# ---------------------------------------------------------------- against a real clone


def _git(repo: pathlib.Path, *args: str) -> str:
    ran = proc.git("-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", *args, cwd=repo)
    assert ran.ok, ran.err
    return ran.out.strip()


def _commit(repo: pathlib.Path, name: str, text: str, message: str) -> str:
    _ = (repo / name).write_text(text, encoding="utf-8")
    _ = _git(repo, "add", name)
    _ = _git(repo, "commit", "-q", "-m", message)
    return _git(repo, "rev-parse", "HEAD")


@pytest.mark.ci
def test_a_restarted_clone_is_read_and_sorted(tmp_path: pathlib.Path) -> None:
    repo = tmp_path / "r"
    repo.mkdir()
    _ = _git(repo, "init", "-q", "-b", "main")
    old_main = _commit(repo, "a", "1", "old")
    _ = _git(repo, "branch", "landed")
    _ = _git(repo, "branch", "work")
    _ = _git(repo, "switch", "-q", "landed")
    _ = _commit(repo, "b", "2", "squashed into main later")
    _ = _git(repo, "switch", "-q", "work")
    _ = _commit(repo, "c", "3", "unmerged work")
    _ = _git(repo, "switch", "-q", "main")
    old_main = _commit(repo, "b", "2", "the squash")
    _ = _git(repo, "switch", "-q", "--orphan", "fresh")
    root = _commit(repo, "a", "1", f"r: a fresh start from o/r-archive@{old_main[:8]}")
    _ = _git(repo, "update-ref", "refs/remotes/origin/main", root)
    _ = _git(repo, "remote", "add", "origin", URL)

    restart = history.detect_restart(repo)

    assert restart is not None
    assert (restart.root, restart.old_main, restart.archive_slug.full_name) == (root, old_main[:8], "o/r-archive")
    kinds = {b.name: history.classify(b) for b in history.branch_facts(repo, restart, history.ArchivePrs())}
    assert kinds == {
        "fresh": history.Kind.CURRENT,
        "main": history.Kind.RETIRED,
        "landed": history.Kind.RETIRED,
        "work": history.Kind.TO_PORT,
    }
