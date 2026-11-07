from __future__ import annotations

import os
import pathlib
import subprocess
import time
from collections.abc import Mapping

import pytest
from typer.testing import CliRunner

from desktop_manager.jobs import state
from desktop_manager.jobs.registry import Environment, default_jobs
from rig.estate import decisions
from rig.estate.cli import app
from rig.estate.decisions import Decision, Kind
from rig.estate.disksize import footprint
from rig.estate.prs import PullRequest

pytestmark = pytest.mark.ci

DAY = 86400.0
LATER = time.time() + 8 * DAY

_real_run = subprocess.run


def _git(args: list[str], cwd: pathlib.Path) -> str:
    return _real_run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


@pytest.fixture
def clone(tmp_path: pathlib.Path) -> pathlib.Path:
    origin = tmp_path / "origin.git"
    _ = _git(["init", "-q", "--bare", "-b", "main", str(origin)], tmp_path)
    root = tmp_path / "code" / "app"
    root.mkdir(parents=True)
    _ = _git(["init", "-q", "-b", "main"], root)
    _ = _git(["config", "user.email", "t@example.com"], root)
    _ = _git(["config", "user.name", "T"], root)
    _ = (root / ".gitignore").write_text(".worktrees/\ntemp/\n", encoding="utf-8")
    _ = _git(["add", "."], root)
    _ = _git(["commit", "-qm", "first"], root)
    _ = _git(["remote", "add", "origin", str(origin)], root)
    _ = _git(["push", "-q", "-u", "origin", "main"], root)
    return root


def _worktree(clone: pathlib.Path, name: str) -> pathlib.Path:
    path = clone / ".worktrees" / name
    _ = _git(["worktree", "add", "-q", "-b", name, str(path)], clone)
    return path


def _touch(worktree: pathlib.Path, when: float) -> None:
    admin = pathlib.Path(_git(["rev-parse", "--absolute-git-dir"], worktree))
    for name in ("index", "HEAD", "logs/HEAD"):
        if (admin / name).exists():
            os.utime(admin / name, (when, when))


SHELVE = (
    "Preview a shelve, then re-run it with --apply: the work goes to a shelved/ branch on origin, "
    "verified, before the worktree is removed"
)
HAND_WRITTEN = "Some may be hand-written; git cannot restore them"
LIST_FIRST = "List the files, keep anything hand-written, then delete the rest"


def _size(path: pathlib.Path) -> int:
    return footprint(path).bytes_used


def _heads(*pulls: PullRequest) -> Mapping[str, PullRequest]:
    return {p.head_ref: p for p in pulls}


def test_collect_puts_each_idle_worktree_under_one_kind(clone: pathlib.Path) -> None:
    unpushed = _worktree(clone, "unpushed")
    _ = (unpushed / "new.txt").write_text("work\n" * 2000, encoding="utf-8")
    _ = _git(["add", "new.txt"], unpushed)
    _ = _git(["commit", "-qm", "local only"], unpushed)
    dirty = _worktree(clone, "dirty")
    _ = (dirty / "notes.md").write_text("hand-written\n", encoding="utf-8")
    closed = _worktree(clone, "closed")
    no_pr = _worktree(clone, "no-pr")
    _ = _worktree(clone, "merged")
    _ = _worktree(clone, "open")
    locked = _worktree(clone, "locked")
    _ = _git(["worktree", "lock", str(locked)], clone)
    fresh = _worktree(clone, "fresh")
    _touch(fresh, LATER)
    ghost = clone / ".worktrees" / "ghost"
    ghost.mkdir()
    _ = (ghost / "left.bin").write_bytes(b"x" * 4096)
    stale = clone / "temp" / "review-pr-1"
    stale.mkdir(parents=True)
    _ = (stale / ".git").write_text(f"gitdir: {clone / '.git' / 'worktrees' / 'gone'}\n", encoding="utf-8")
    (clone / "temp" / "scratch").mkdir()

    heads = _heads(
        PullRequest(4, "CLOSED", "closed"),
        PullRequest(5, "MERGED", "merged"),
        PullRequest(6, "OPEN", "open"),
    )
    found = decisions.collect(clone.parent, pull_requests=lambda _: heads, now=LATER)

    remove = f"cd {clone} && git worktree remove"
    assert found == [
        Decision(
            kind="unpushed-worktree",
            repo="app",
            path=str(unpushed),
            reason="1 commit(s) on no remote; untouched for 7+ days",
            size_bytes=_size(unpushed),
            suggested_action=SHELVE,
            command=f'estate shelve "{unpushed}"',
        ),
        Decision(
            kind="unpushed-worktree",
            repo="app",
            path=str(dirty),
            reason="uncommitted changes; untouched for 7+ days",
            size_bytes=_size(dirty),
            suggested_action=SHELVE,
            command=f'estate shelve "{dirty}"',
        ),
        Decision(
            kind="closed-pr-worktree",
            repo="app",
            path=str(closed),
            reason="PR #4 for `closed` was closed without merging",
            size_bytes=_size(closed),
            suggested_action="Remove the worktree if the work is abandoned, or reopen the PR",
            command=f"{remove} {closed}",
        ),
        Decision(
            kind="no-pr-worktree",
            repo="app",
            path=str(no_pr),
            reason="`no-pr` has never had a PR; untouched for 7+ days",
            size_bytes=_size(no_pr),
            suggested_action="Open a PR if the work is wanted; otherwise remove the worktree",
            command=f"{remove} {no_pr}",
        ),
        Decision(
            kind="locked-path",
            repo="app",
            path=str(ghost),
            reason=f"git no longer tracks it, but 1 file(s) remain: left.bin. {HAND_WRITTEN}",
            size_bytes=4096,
            suggested_action=LIST_FIRST,
            command=f'cmd /c dir /s /b /a-d "{ghost}"',
        ),
        Decision(
            kind="locked-path",
            repo="app",
            path=str(stale),
            reason=f"git no longer tracks it, but 1 file(s) remain: .git. {HAND_WRITTEN}",
            size_bytes=_size(stale),
            suggested_action=LIST_FIRST,
            command=f'cmd /c dir /s /b /a-d "{stale}"',
        ),
    ]


def test_without_github_only_the_disk_decisions_are_made(clone: pathlib.Path) -> None:
    dirty = _worktree(clone, "dirty")
    _ = (dirty / "notes.md").write_text("hand-written\n", encoding="utf-8")
    _ = _worktree(clone, "no-pr")
    asked: list[pathlib.Path] = []

    def unreachable(repo: pathlib.Path) -> Mapping[str, PullRequest] | None:
        asked.append(repo)
        return None

    found = decisions.collect(clone, pull_requests=unreachable, now=LATER)

    assert [(d.kind, d.path) for d in found] == [(Kind.UNPUSHED_WORKTREE, str(dirty))]
    assert asked == [clone]


def test_github_is_not_asked_when_no_worktree_needs_it(clone: pathlib.Path) -> None:
    _ = _worktree(clone, "busy")
    asked: list[pathlib.Path] = []

    def lookup(repo: pathlib.Path) -> Mapping[str, PullRequest] | None:
        asked.append(repo)
        return {}

    assert decisions.collect(clone, pull_requests=lookup) == []
    assert asked == []


def test_a_clone_git_refuses_to_read_is_one_decision(monkeypatch: pytest.MonkeyPatch, clone: pathlib.Path) -> None:
    def refuse(argv: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
        if argv[:2] == ["git", "rev-parse"] and kwargs.get("cwd") == clone:
            message = f"fatal: detected dubious ownership in repository at '{clone.as_posix()}'\n"
            return subprocess.CompletedProcess(argv, 128, "", message)
        return _real_run(argv, **kwargs)  # pyright: ignore[reportCallIssue, reportArgumentType, reportUnknownVariableType]

    monkeypatch.setattr(subprocess, "run", refuse)

    found = decisions.collect(clone.parent, pull_requests=lambda _: {}, now=LATER)

    assert found == [
        Decision(
            kind="dubious-ownership",
            repo="app",
            path=str(clone),
            reason="git refuses to read it: the directory is owned by another account",
            size_bytes=_size(clone),
            suggested_action=(
                f'Trust it with safe.directory, or take ownership if it is yours: takeown /f "{clone}" /r /d y'
            ),
            command=f"git config --global --add safe.directory {clone.as_posix()}",
        )
    ]


def test_collect_roots_skips_missing_roots_and_reports_a_path_once(clone: pathlib.Path) -> None:
    ghost = clone / ".worktrees" / "ghost"
    ghost.mkdir(parents=True)

    found = decisions.collect_roots(
        [clone.parent, clone, clone.parent / "absent"], pull_requests=lambda _: {}, now=LATER
    )

    assert [(d.kind, d.path) for d in found] == [(Kind.LOCKED_PATH, str(ghost))]


def test_the_feed_round_trips_and_lives_beside_the_jobs_state(tmp_path: pathlib.Path) -> None:
    feed = tmp_path / "jobs" / "decisions.json"
    item = Decision("no-pr-worktree", "app", r"C:\code\app\.worktrees\x", "why", 12, "do it", "git status")

    decisions.write_feed(feed, [item])

    assert decisions.read_feed(feed) == [item]
    assert decisions.read_feed(tmp_path / "missing.json") is None
    assert decisions.default_feed_path() == state.jobs_dir() / decisions.FEED_NAME


def test_the_cli_writes_the_feed_it_prints(clone: pathlib.Path, tmp_path: pathlib.Path) -> None:
    ghost = clone / ".worktrees" / "ghost"
    ghost.mkdir(parents=True)
    feed = tmp_path / "out" / "decisions.json"

    result = CliRunner().invoke(app, ["decisions", "--root", str(clone.parent), "--write", str(feed), "--json"])

    assert result.exit_code == 0, result.output
    assert decisions.read_feed(feed) == [
        Decision(
            kind="locked-path",
            repo="app",
            path=str(ghost),
            reason="git no longer tracks it, and only links and empty directories remain",
            size_bytes=0,
            suggested_action="Delete the skeleton; it holds no files",
            command=f'cmd /c rmdir /s /q "{ghost}"',
        )
    ]


def test_the_cleanup_jobs_pass_explicit_roots_and_paths(tmp_path: pathlib.Path) -> None:
    env = Environment(
        scripts=tmp_path / "Scripts",
        plan=tmp_path / "plan.json",
        workspace_root=tmp_path / "ws",
        personal_root=tmp_path / "personal",
        work_roots=(tmp_path / "acme",),
        decisions_feed=tmp_path / "jobs" / "decisions.json",
        orca_user_data=tmp_path / "orca-personal",
    )
    jobs = {job.name: job for job in default_jobs(env)}

    assert jobs["estate-prune-caches"].argv[1:] == ("prune-caches", "--apply")
    assert jobs["estate-decisions"].argv[1:] == (
        "decisions",
        "--root",
        str(tmp_path),
        "--write",
        str(tmp_path / "jobs" / "decisions.json"),
    )
    assert jobs["worktree-reclaim"].argv == (
        str(env.workspace_python),
        "-m",
        "workspace_cli.main",
        "worktree",
        "reclaim",
        "--all-repos",
        "--apply",
    )
    assert jobs["worktree-reclaim"].requires == (
        env.workspace_python,
        tmp_path / "ws" / "src" / "workspace_cli" / "main.py",
    )


def test_a_leftover_names_a_few_of_its_files_and_counts_the_rest(clone: pathlib.Path) -> None:
    ghost = clone / ".worktrees" / "ghost"
    ghost.mkdir(parents=True)
    for n in range(7):
        _ = (ghost / f"f{n}.txt").write_text("x", encoding="utf-8")

    found = decisions.leftover(ghost)
    [decision] = decisions.collect(clone.parent, pull_requests=lambda _: {}, now=LATER)

    assert (found.files, found.bytes_used, len(found.sample)) == (7, 7, decisions.SAMPLE_FILES)
    assert decision.reason.startswith(
        f"git no longer tracks it, but 7 file(s) remain: {', '.join(found.sample)} and 2 more."
    )
