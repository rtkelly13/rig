"""Tests for `estate shelve` — against a real clone and a throwaway bare origin."""

from __future__ import annotations

import datetime as dt
import os
import pathlib
import subprocess
import sys
from collections.abc import Mapping

import pytest

from rig.estate import shelf_store, shelve
from rig.estate.checks import Worktree, list_worktrees
from tests.behaviour.steps.git import add_worktree, commit, make_dirty
from tests.fakes.git import git

NOW = dt.datetime(2026, 10, 9, 12, 0, tzinfo=dt.UTC)
NO_IDLE = shelve.Options(idle=dt.timedelta(0))


def _tree(clone: pathlib.Path, path: pathlib.Path) -> Worktree:
    return next(t for t in list_worktrees(clone) if t.path.resolve() == path.resolve())


def _plan(
    clone: pathlib.Path,
    path: pathlib.Path,
    options: shelve.Options = NO_IDLE,
    prs: Mapping[str, shelve.PrRef] | None = None,
) -> shelve.Plan:
    return shelve.plan_worktree(
        clone,
        _tree(clone, path),
        user=shelve.shelf_user(clone),
        shelves=shelf_store.remote_shelves(clone),
        prs=prs or {},
        options=options,
        now=NOW,
    )


# ---------------------------------------------------------------------------
# pure helpers
# ---------------------------------------------------------------------------


@pytest.mark.ci
def test_parse_status_skips_a_renames_source_field() -> None:
    raw = "R  new.py\0old.py\0 M a.txt\0?? b.txt\0"

    assert shelve.parse_status(raw) == shelve.Porcelain(changed=["new.py", "a.txt"], untracked=["b.txt"])


@pytest.mark.ci
def test_message_trailers_round_trip() -> None:
    text = shelve.message(
        repo="data-platform", name="feat", branch="feat", base="abc123", path=".worktrees/feat", pr=42, at=NOW
    )

    assert text.startswith(f"{shelf_store.PREFIX} data-platform/feat (feat)\n")
    assert shelf_store.parse_trailers(text) == shelf_store.Trailers(
        branch="feat",
        base="abc123",
        path=".worktrees/feat",
        pr="#42",
        at="2026-10-09T12:00:00+00:00",
        by="estate shelve",
    )


@pytest.mark.ci
@pytest.mark.parametrize(
    ("relative", "expected"),
    [(".worktrees/PROJ-1", "PROJ-1"), ("temp/x", "temp/x"), (".claude/worktrees/a b", "claude/worktrees/a-b")],
)
def test_shelf_name_drops_the_convention_dir_only(tmp_path: pathlib.Path, relative: str, expected: str) -> None:
    assert shelve.shelf_name(tmp_path, tmp_path / relative) == expected


@pytest.mark.ci
@pytest.mark.parametrize(
    ("path", "regenerable"),
    [
        ("node_modules/", True),
        ("pkg/.venv/", True),
        ("x/__pycache__/", True),
        ("node_modules/a/Secret.js", True),
        ("packages/", True),
        (".DS_Store", True),
        (".env", False),
        ("temp/", False),
        ("packages/api/.env", False),
        ("packages/web/.env.local", False),
        ("build/notes.md", False),
        ("tools/bin/deploy.sh", False),
        ("src/target/x.secret", False),
    ],
)
def test_is_regenerable(path: str, regenerable: bool) -> None:
    assert shelve.is_regenerable(path) is regenerable


# ---------------------------------------------------------------------------
# planning: the rules (least action, decisions needed) live in features/estate_shelve.feature
# ---------------------------------------------------------------------------


# ---------------------------------------------------------------------------
# applying
# ---------------------------------------------------------------------------


@pytest.mark.skipif(sys.platform != "win32", reason="ACLs are Windows-only")
def test_a_delete_git_abandons_part_way_is_finished(clone: pathlib.Path) -> None:
    path = add_worktree(clone, "locked-cache")
    cache = path / ".pytest_cache"
    (cache / "v").mkdir(parents=True)
    _ = (cache / ".gitignore").write_text("*\n", encoding="utf-8")
    for change in (
        ["/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
        ["/remove:g", "*S-1-3-4"],
    ):
        _ = subprocess.run(["icacls", str(cache), *change], capture_output=True, check=True)
    try:
        _ = list(os.scandir(cache))
    except PermissionError:
        pass
    else:
        pytest.skip("an elevated process reads the directory through Administrators")

    result = shelve.apply_plan(_plan(clone, path), now=NOW, ledger=None)

    assert result.outcome == "removed", result.detail
    assert "finished after git stopped part-way" in result.detail
    assert not path.exists()


@pytest.mark.ci
def test_local_commits_are_shelved_and_the_branch_is_recreated(clone: pathlib.Path) -> None:
    path = add_worktree(clone, "never-pushed", push=False)
    commit(path, "a.txt", "a\n")
    head = git(["rev-parse", "HEAD"], path)

    result = shelve.apply_plan(_plan(clone, path), now=NOW, ledger=None)

    assert result.outcome == "shelved", result.detail
    assert "branch never-pushed deleted" in result.detail
    [shelf] = shelf_store.list_shelves(clone)
    assert not shelf.snapshot

    restored = shelf_store.unshelve(shelf)

    assert restored.ok, restored.detail
    assert git(["rev-parse", "never-pushed"], clone) == head
    assert git(["status", "--porcelain"], path) == ""


@pytest.mark.ci
def test_a_rerun_after_a_failed_removal_is_idempotent(clone: pathlib.Path) -> None:
    path = add_worktree(clone, "again")
    make_dirty(path)
    plan = _plan(clone, path)
    sha = shelve._marker(plan, NOW)  # pyright: ignore[reportPrivateUsage]
    git(["push", "-q", "origin", f"{sha}:{plan.ref}"], clone)

    rerun = _plan(clone, path)

    assert not rerun.decision_needed
    assert rerun.existing == sha
    assert shelve.apply_plan(rerun, ledger=None).outcome == "shelved"


@pytest.mark.ci
def test_only_a_marker_without_a_snapshot_is_prunable(clone: pathlib.Path) -> None:
    landed = add_worktree(clone, "landed", push=False)
    commit(landed, "a.txt", "a\n")
    assert shelve.apply_plan(_plan(clone, landed), now=NOW, ledger=None).outcome == "shelved"
    dirty = add_worktree(clone, "dirty")
    make_dirty(dirty)
    assert shelve.apply_plan(_plan(clone, dirty), now=NOW, ledger=None).outcome == "shelved"

    shelves = {s.name: s for s in shelf_store.list_shelves(clone)}

    assert shelf_store.prunable(shelves["landed"], merged_prs={}) == ""
    git(["fetch", "-q", "origin"], clone)
    git(["push", "-q", "origin", f"{shelves['landed'].sha}^:refs/heads/main"], clone)
    git(["fetch", "-q", "origin"], clone)
    assert shelf_store.prunable(shelves["landed"], merged_prs={}) == "base is in main"
    assert shelf_store.prunable(shelves["dirty"], merged_prs={}) == "", "a snapshot is never pruned"

    dropped = shelf_store.drop(shelves["landed"])
    assert dropped.ok, dropped.detail
    assert [s.name for s in shelf_store.list_shelves(clone)] == ["dirty"]


@pytest.mark.ci
def test_a_merged_pr_prunes_a_shelf_only_when_it_merged_the_shelved_commits(clone: pathlib.Path) -> None:
    path = add_worktree(clone, "squashed")
    commit(path, "a.txt", "a\n")
    pushed = git(["rev-parse", "HEAD"], path)
    git(["push", "-q", "origin", "squashed"], path)
    commit(path, "b.txt", "local only\n")
    plan = _plan(clone, path, prs={"squashed": shelve.PrRef(number=5, state="OPEN")})
    assert shelve.apply_plan(plan, now=NOW, ledger=None).outcome == "shelved"
    shelf = next(s for s in shelf_store.list_shelves(clone) if s.name == "squashed")

    assert shelf_store.prunable(shelf, merged_prs={5: pushed}) == "", "the local-only commit was never in #5"
    assert shelf_store.prunable(shelf, merged_prs={5: plan.head}) == "PR #5 merged"


@pytest.mark.ci
def test_a_tree_edited_after_planning_is_kept(clone: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    path = add_worktree(clone, "late-edit")
    make_dirty(path)
    plan = _plan(clone, path)
    on_remote = shelve._on_remote  # pyright: ignore[reportPrivateUsage]

    def edit_then_check(clone: pathlib.Path, ref: str, sha: str) -> bool:
        _ = (path / "late.txt").write_text("late\n", encoding="utf-8")
        return on_remote(clone, ref, sha)

    monkeypatch.setattr(shelve, "_on_remote", edit_then_check)
    result = shelve.apply_plan(plan, now=NOW, ledger=None)

    assert result.outcome == "failed"
    assert "changed since planning" in result.detail
    assert (path / "late.txt").exists()


@pytest.mark.ci
def test_an_ignored_file_written_after_planning_is_kept(clone: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    path = add_worktree(clone, "late-ignored", push=False)
    commit(path, "a.txt", "a\n")
    plan = _plan(clone, path)
    assert plan.action == shelve.PUSH
    on_remote = shelve._on_remote  # pyright: ignore[reportPrivateUsage]

    def write_then_check(clone: pathlib.Path, ref: str, sha: str) -> bool:
        _ = (path / ".env").write_text("TOKEN=1\n", encoding="utf-8")
        return on_remote(clone, ref, sha)

    monkeypatch.setattr(shelve, "_on_remote", write_then_check)
    result = shelve.apply_plan(plan, now=NOW, ledger=None)

    assert result.outcome == "failed"
    assert ".env" in result.detail
    assert (path / ".env").exists()


@pytest.mark.ci
def test_an_unreadable_worktree_list_never_finishes_a_delete(
    clone: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    plan = _plan(clone, add_worktree(clone, "unlisted"))

    def unreadable(_clone: pathlib.Path) -> list[Worktree]:
        return []

    monkeypatch.setattr(shelve, "list_worktrees", unreadable)

    assert not shelve._abandoned_part_way(plan)  # pyright: ignore[reportPrivateUsage]


@pytest.mark.ci
@pytest.mark.parametrize(
    ("path", "regenerable"),
    [
        (".husky/_/", True),
        ("js/pkg/.wireit/", True),
        ("paket-files/", True),
        ("logs/run.log", True),
        ("batch/Specs/Advances.feature.cs", True),
        ("docs/.husky-notes.md", False),
    ],
)
def test_tool_outputs_found_on_the_estate_are_regenerable(path: str, regenerable: bool) -> None:
    assert shelve.is_regenerable(path) is regenerable


@pytest.mark.ci
def test_the_decision_reports_how_much_the_ignored_paths_weigh(clone: pathlib.Path) -> None:
    path = add_worktree(clone, "weighed")
    (path / ".env").write_bytes(b"x" * 3 * 2**20)

    plan = _plan(clone, path)

    assert plan.keep_ignored == [".env"]
    assert plan.keep_ignored_bytes == 3 * 2**20
    assert "3.0 MB" in plan.decision_needed[0]


def _dir_link(link: pathlib.Path, target: pathlib.Path) -> None:
    """A junction on Windows (what pnpm makes, no privilege needed), else a symlink."""
    if sys.platform == "win32":
        import _winapi  # noqa: PLC0415, TID251

        _winapi.CreateJunction(str(target), str(link))
    else:
        link.symlink_to(target, target_is_directory=True)


@pytest.mark.ci
def test_a_link_skeleton_is_swept_without_following_its_links(tmp_path: pathlib.Path) -> None:
    store = tmp_path / "store"
    store.mkdir()
    (store / "keep.js").write_text("x", encoding="utf-8")
    skeleton = tmp_path / "wt"
    (skeleton / "js" / "node_modules" / "@scope").mkdir(parents=True)
    _dir_link(skeleton / "js" / "node_modules" / "@scope" / "pkg", store)

    detail = shelve.sweep_link_skeleton(skeleton)

    assert "1 leftover link(s) cleared" in detail
    assert not skeleton.exists()
    assert (store / "keep.js").read_text(encoding="utf-8") == "x"


@pytest.mark.ci
def test_a_skeleton_holding_a_file_is_left_alone(tmp_path: pathlib.Path) -> None:
    skeleton = tmp_path / "wt"
    (skeleton / "node_modules").mkdir(parents=True)
    (skeleton / "notes.md").write_text("mine", encoding="utf-8")

    assert "still holds files" in shelve.sweep_link_skeleton(skeleton)
    assert (skeleton / "notes.md").exists()


@pytest.mark.ci
def test_a_worktree_holding_another_registered_worktree_is_refused(clone: pathlib.Path) -> None:
    outer = add_worktree(clone, "outer")
    inner = outer / "node_modules" / "inner"
    git(["worktree", "add", "-q", "-b", "inner", str(inner)], clone)

    plan = _plan(clone, outer, shelve.Options(idle=dt.timedelta(0), discard_ignored=True))

    assert any("holds another repository" in d for d in plan.decision_needed)


@pytest.mark.ci
def test_a_branch_with_no_history_in_common_with_origin_is_refused(clone: pathlib.Path) -> None:
    git(["fetch", "-q", "origin"], clone)
    path = add_worktree(clone, "before-the-restart", push=False)
    git(["checkout", "-q", "--orphan", "old-history"], path)
    commit(path, "old.txt", "history origin never had\n")

    plan = _plan(clone, path)

    assert plan.action == shelve.PUSH
    assert any("shares no history" in d for d in plan.decision_needed), plan.decision_needed


@pytest.mark.ci
def test_a_branch_built_on_origin_is_not_refused_for_its_history(clone: pathlib.Path) -> None:
    git(["fetch", "-q", "origin"], clone)
    path = add_worktree(clone, "after-the-restart", push=False)
    commit(path, "new.txt", "new\n")

    plan = _plan(clone, path)

    assert not any("shares no history" in d for d in plan.decision_needed), plan.decision_needed


@pytest.mark.ci
def test_a_worktree_holding_a_nested_clone_is_refused(clone: pathlib.Path) -> None:
    outer = add_worktree(clone, "outer")
    nested = outer / "scratch"
    nested.mkdir()
    git(["init", "-q"], nested)

    plan = _plan(clone, outer)

    assert any("holds another repository" in d for d in plan.decision_needed)
