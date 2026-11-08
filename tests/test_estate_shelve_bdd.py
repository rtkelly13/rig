"""Step definitions for ``features/estate_shelve.feature``.

The feature states the rules `estate shelve` must keep; these steps drive them against
a real clone and a throwaway bare origin, with the shared `clone` fixture and `tests.behaviour.steps.git`.
Mechanics below the level of a rule (trailer parsing, ACL clean-up, prune arithmetic)
stay as example tests there.
"""

from __future__ import annotations

import datetime as dt
import pathlib
from collections.abc import Callable
from dataclasses import dataclass, field

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from rig.estate import shelf_store, shelve
from tests.behaviour.steps.git import add_worktree, commit, make_dirty
from tests.fakes.git import git
from tests.test_estate_shelve import NOW, _tree

pytestmark = pytest.mark.ci

scenarios("features/estate_shelve.feature")

ACTIONS = {"remove": shelve.REMOVE, "push": shelve.PUSH, "snapshot": shelve.SNAPSHOT}
DIRTY_STATUS = [" M README.md", " D gone.txt", "?? new.txt", "?? staged.txt"]


@dataclass
class Shelving:
    """What one scenario has built and observed so far."""

    clone: pathlib.Path
    path: pathlib.Path | None = None
    branch: str = ""
    prs: dict[str, shelve.PrRef] = field(default_factory=dict)
    options: shelve.Options = field(default_factory=lambda: shelve.Options(idle=dt.timedelta(0)))
    now: dt.datetime | None = NOW
    head: str = ""
    pushed_branch: str = ""
    plan: shelve.Plan | None = None
    result: shelve.Result | None = None

    @property
    def worktree(self) -> pathlib.Path:
        assert self.path is not None, "no worktree was created in this scenario"
        return self.path

    @property
    def planned(self) -> shelve.Plan:
        assert self.plan is not None, "the scenario never planned"
        return self.plan

    @property
    def applied(self) -> shelve.Result:
        assert self.result is not None, "the scenario never applied the plan"
        return self.result


# ---------------------------------------------------------------------------
# Given
# ---------------------------------------------------------------------------


@given("a clone whose main branch is pushed to origin", target_fixture="world")
def _a_clone(clone: pathlib.Path) -> Shelving:
    return Shelving(clone=clone)


@given("origin rejects every push")
def _origin_rejects(world: Shelving) -> None:
    hook = world.clone.parent / "origin.git" / "hooks" / "pre-receive"
    hook.write_text("#!/bin/sh\necho rejected by ruleset >&2\nexit 1\n", encoding="utf-8", newline="\n")
    hook.chmod(0o755)


def _clean(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "clean")


def _ahead(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "ahead")
    commit(w.path, "a.txt", "a\n")


def _uncommitted(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "dirty")
    make_dirty(w.path)


def _squash_merged(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "landed")
    commit(w.path, "a.txt", "a\n")
    w.prs["landed"] = shelve.PrRef(number=7, state="MERGED")


def _secret(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "secret")
    (w.path / ".env").write_text("TOKEN=1\n", encoding="utf-8")


def _build_output(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "built")
    (w.path / "node_modules").mkdir()
    (w.path / "node_modules" / "x.js").write_text("x", encoding="utf-8")


def _live(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "live")
    make_dirty(w.path)
    w.options = shelve.Options(idle=dt.timedelta(hours=2))
    w.now = None  # the real clock: the files were written moments ago


def _shelf_clash(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "clash")
    make_dirty(w.path)
    other = git(["rev-parse", "HEAD"], w.clone)
    git(["push", "-q", "origin", f"{other}:refs/heads/shelved/t.user/clash"], w.clone)
    commit(w.path, "x.txt", "x\n")


def _never_pushed_dirty(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "blocked", push=False)
    make_dirty(w.path)


def _local_commit_and_dirty(w: Shelving) -> None:
    w.path = add_worktree(w.clone, "wip")
    w.branch = "wip"
    commit(w.path, "local.txt", "local\n")
    w.head = git(["rev-parse", "HEAD"], w.path)
    w.pushed_branch = git(["rev-parse", "origin/wip"], w.clone)
    make_dirty(w.path)


WORKTREES: dict[str, Callable[[Shelving], None]] = {
    "that is clean and pushed": _clean,
    "with commits that are not on origin": _ahead,
    "with uncommitted changes": _uncommitted,
    "whose local commits were squash-merged": _squash_merged,
    "holding an ignored secret": _secret,
    "holding ignored build output": _build_output,
    "touched recently enough to be a live session": _live,
    "whose shelf on origin holds other content": _shelf_clash,
    "with uncommitted changes that was never pushed": _never_pushed_dirty,
    "with a local commit and uncommitted changes": _local_commit_and_dirty,
}


@given(parsers.parse("a worktree {state}"))
def _a_worktree(world: Shelving, state: str) -> None:
    WORKTREES[state](world)


# ---------------------------------------------------------------------------
# When
# ---------------------------------------------------------------------------


def _plan(world: Shelving) -> None:
    world.plan = shelve.plan_worktree(
        world.clone,
        _tree(world.clone, world.worktree),
        user=shelve.shelf_user(world.clone),
        shelves=shelf_store.remote_shelves(world.clone),
        prs=world.prs,
        options=world.options,
        now=world.now,
    )


@when("I plan shelving it")
def _when_plan(world: Shelving) -> None:
    _plan(world)


@when("I plan shelving it with ignored files included")
def _when_plan_including_ignored(world: Shelving) -> None:
    world.options = shelve.Options(idle=dt.timedelta(0), include_ignored=True)
    _plan(world)


@when("I plan shelving it with ignored files discarded")
def _when_plan_discarding_ignored(world: Shelving) -> None:
    world.options = shelve.Options(idle=dt.timedelta(0), discard_ignored=True)
    _plan(world)


@when("I apply the plan")
def _when_apply(world: Shelving) -> None:
    world.result = shelve.apply_plan(world.planned, now=NOW, ledger=None)


@when("I unshelve it", target_fixture="restored")
def _when_unshelve(world: Shelving) -> shelf_store.Done:
    [shelf] = shelf_store.list_shelves(world.clone)
    return shelf_store.unshelve(shelf)


# ---------------------------------------------------------------------------
# Then
# ---------------------------------------------------------------------------


@then(parsers.parse("the plan is to {action}"))
def _plan_is(world: Shelving, action: str) -> None:
    assert world.planned.action == ACTIONS[action], world.planned.reasons


@then(parsers.parse('the plan gives the reason "{reason}"'))
def _reason(world: Shelving, reason: str) -> None:
    assert reason in world.planned.reasons


@then("no decision is needed")
def _no_decision(world: Shelving) -> None:
    assert world.planned.decision_needed == []


@then(parsers.parse('a decision is needed about "{concern}"'))
def _decision_about(world: Shelving, concern: str) -> None:
    assert any(concern in d for d in world.planned.decision_needed), world.planned.decision_needed


@then(parsers.parse('the snapshot carries "{name}"'))
def _carries(world: Shelving, name: str) -> None:
    assert world.planned.carry_ignored == [name]


@then(parsers.parse('the outcome is "{outcome}"'))
def _outcome(world: Shelving, outcome: str) -> None:
    assert world.applied.outcome == outcome, world.applied.detail


@then("the worktree is still on disk")
def _still_there(world: Shelving) -> None:
    assert world.worktree.is_dir()
    assert _tree(world.clone, world.worktree).path.resolve() == world.worktree.resolve(), "still registered with git"


@then("the worktree's own index is untouched")
def _index_untouched(world: Shelving) -> None:
    assert (world.worktree / "new.txt").read_text(encoding="utf-8") == "untracked\n"
    assert sorted(git(["status", "--porcelain"], world.worktree).splitlines()) == sorted(
        [" M README.md", " D gone.txt", "A  staged.txt", "?? new.txt"]
    )


@then("the worktree is gone from disk")
def _gone(world: Shelving) -> None:
    assert not world.worktree.exists()


@then("origin holds a shelf recording the branch and the commit it was taken on")
def _shelf_on_origin(world: Shelving) -> None:
    sha = world.applied.sha
    assert git(["ls-remote", "origin", f"refs/heads/shelved/t.user/{world.branch}"], world.clone).split("\t")[0] == sha
    assert git(["log", "-1", "--format=%s", sha], world.clone).startswith(shelf_store.PREFIX)
    assert git(["rev-parse", f"{sha}^"], world.clone) == world.head
    [shelf] = shelf_store.list_shelves(world.clone)
    assert (shelf.name, shelf.snapshot, shelf.trailers.branch, shelf.trailers.base) == (
        world.branch,
        True,
        world.branch,
        world.head,
    )


@then("the pull request branch on origin did not move")
def _pr_branch_unmoved(world: Shelving) -> None:
    on_origin = git(["ls-remote", "origin", f"refs/heads/{world.branch}"], world.clone).split("\t")[0]
    assert on_origin == world.pushed_branch


@then("the branch and its commits are back")
def _branch_back(world: Shelving, restored: shelf_store.Done) -> None:
    assert restored.ok, restored.detail
    assert git(["rev-parse", "HEAD"], world.worktree) == world.head
    assert git(["branch", "--show-current"], world.worktree) == world.branch


@then("every uncommitted change is back, unstaged")
def _changes_back(world: Shelving) -> None:
    assert (world.worktree / "README.md").read_text(encoding="utf-8") == "edited\n"
    assert (world.worktree / "new.txt").read_text(encoding="utf-8") == "untracked\n"
    assert not (world.worktree / "gone.txt").exists()
    assert sorted(git(["status", "--porcelain"], world.worktree).splitlines()) == sorted(DIRTY_STATUS)
