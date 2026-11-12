"""The decisions feed as Python reads it back: the C# `estate decisions --write` writes it."""

from __future__ import annotations

import json
import pathlib

import pytest

from desktop_manager.jobs import state
from desktop_manager.jobs.registry import Environment, default_jobs
from rig.estate import decisions
from rig.estate.decisions import Decision

pytestmark = pytest.mark.ci

# Byte for byte what `rig estate decisions --write` writes (DecisionsTests pins the C# side).
FEED = """[
  {
    "kind": "no-pr-worktree",
    "repo": "app",
    "path": "C:\\\\code\\\\app\\\\.worktrees\\\\x+y",
    "reason": "why",
    "size_bytes": 12,
    "suggested_action": "do it",
    "command": "git status"
  }
]
"""


def test_the_feed_csharp_writes_reads_back_as_decisions(tmp_path: pathlib.Path) -> None:
    feed = tmp_path / "decisions.json"
    _ = feed.write_text(FEED, encoding="utf-8")

    assert decisions.read_feed(feed) == [
        Decision("no-pr-worktree", "app", r"C:\code\app\.worktrees\x+y", "why", 12, "do it", "git status")
    ]
    assert json.loads(FEED)[0]["path"] == r"C:\code\app\.worktrees\x+y"


def test_a_missing_or_malformed_feed_reads_as_none(tmp_path: pathlib.Path) -> None:
    broken = tmp_path / "broken.json"
    _ = broken.write_text("{not json", encoding="utf-8")

    assert decisions.read_feed(tmp_path / "missing.json") is None
    assert decisions.read_feed(broken) is None


def test_the_feed_lives_beside_the_jobs_state() -> None:
    assert decisions.default_feed_path() == state.jobs_dir() / decisions.FEED_NAME


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

    assert jobs["estate-prune-caches"].argv[1:] == ("estate", "prune-caches", "--apply")
    assert jobs["estate-decisions"].argv[1:] == (
        "estate",
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
