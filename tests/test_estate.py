"""Tests for `estate` — local clone discovery, worktree parsing and the reclaim scan."""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import subprocess

import pytest

from rig.estate import discovery
from rig.estate.checks import parse_worktrees


def _git(args: list[str], cwd: pathlib.Path) -> None:
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True)


@pytest.fixture
def repo(tmp_path: pathlib.Path) -> pathlib.Path:
    """A real git repo with one commit, pushed to a bare origin.

    The remote matters: risk is measured as commits on *no remote*, so a repo
    without one legitimately reports every commit as local-only. That is the
    unusual case.
    """
    origin = tmp_path / "origin.git"
    _git(["init", "-q", "--bare", "-b", "main", str(origin)], tmp_path)

    root = tmp_path / "clone"
    root.mkdir()
    _git(["init", "-q", "-b", "main"], root)
    _git(["config", "user.email", "t@example.com"], root)
    _git(["config", "user.name", "T"], root)
    _git(["remote", "add", "origin", str(origin)], root)
    (root / "README.md").write_text("hi\n", encoding="utf-8")
    _git(["add", "."], root)
    _git(["commit", "-qm", "first"], root)
    _git(["push", "-q", "-u", "origin", "main"], root)
    return root


# ---------------------------------------------------------------------------
# discovery — two levels, stop at the repo, cache
# ---------------------------------------------------------------------------


def test_scan_stops_descending_at_a_repo(tmp_path: pathlib.Path) -> None:
    """The cheapness guarantee: nothing below a checkout is walked."""
    outer = tmp_path / "code" / "proj"
    (outer / ".git").mkdir(parents=True)
    nested = outer / "vendor" / "inner"
    (nested / ".git").mkdir(parents=True)

    found = discovery.scan(tmp_path)

    assert found == [outer], "a repo inside a repo must not be discovered separately"


def test_scan_respects_max_depth(tmp_path: pathlib.Path) -> None:
    deep = tmp_path / "a" / "b" / "c"
    (deep / ".git").mkdir(parents=True)
    assert discovery.scan(tmp_path, max_depth=2) == []
    assert discovery.scan(tmp_path, max_depth=3) == [deep]


def test_scan_skips_noise_and_hidden_dirs(tmp_path: pathlib.Path) -> None:
    for noise in ("node_modules", ".venv", ".hidden"):
        (tmp_path / noise / "pkg" / ".git").mkdir(parents=True)
    assert discovery.scan(tmp_path) == []


def test_scan_returns_the_root_itself_when_it_is_a_repo(tmp_path: pathlib.Path) -> None:
    (tmp_path / ".git").mkdir()
    assert discovery.scan(tmp_path) == [tmp_path.resolve()]


def test_repos_caches_then_reuses(tmp_path: pathlib.Path) -> None:
    root = tmp_path / "code"
    (root / "one" / ".git").mkdir(parents=True)
    cache = tmp_path / "cache.json"

    scan = discovery.repos(root, cache_file=cache)

    first, cached = scan.paths, scan.from_cache
    assert not cached and len(first) == 1

    # a new repo appears, but the cached answer is still fresh
    (root / "two" / ".git").mkdir(parents=True)
    scan = discovery.repos(root, cache_file=cache)
    second, cached = scan.paths, scan.from_cache
    assert cached and len(second) == 1

    scan = discovery.repos(root, cache_file=cache, refresh=True)

    third, cached = scan.paths, scan.from_cache
    assert not cached and len(third) == 2


def test_repos_rescans_once_the_ttl_lapses(tmp_path: pathlib.Path) -> None:
    root = tmp_path / "code"
    (root / "one" / ".git").mkdir(parents=True)
    cache = tmp_path / "cache.json"
    discovery.repos(root, cache_file=cache)

    (root / "two" / ".git").mkdir(parents=True)
    later = dt.datetime.now(dt.UTC) + dt.timedelta(hours=24)
    scan = discovery.repos(root, cache_file=cache, ttl=dt.timedelta(hours=12), now=later)
    found, cached = scan.paths, scan.from_cache
    assert not cached and len(found) == 2


def test_cached_paths_that_vanished_are_dropped_without_a_rescan(tmp_path: pathlib.Path) -> None:
    """A deleted clone must not be reported from cache — it would produce findings for nothing."""
    root = tmp_path / "code"
    (root / "one" / ".git").mkdir(parents=True)
    (root / "two" / ".git").mkdir(parents=True)
    cache = tmp_path / "cache.json"
    discovery.repos(root, cache_file=cache)

    import shutil

    shutil.rmtree(root / "two")
    scan = discovery.repos(root, cache_file=cache)
    found, cached = scan.paths, scan.from_cache
    assert cached and [p.name for p in found] == ["one"]


def test_a_corrupt_cache_is_treated_as_empty(tmp_path: pathlib.Path) -> None:
    cache = tmp_path / "cache.json"
    cache.write_text("{ not json", encoding="utf-8")
    root = tmp_path / "code"
    (root / "one" / ".git").mkdir(parents=True)
    scan = discovery.repos(root, cache_file=cache)
    found, cached = scan.paths, scan.from_cache
    assert not cached and len(found) == 1
    assert json.loads(cache.read_text(encoding="utf-8"))  # rewritten


# ---------------------------------------------------------------------------
# parsers — pure, so they need no git
# ---------------------------------------------------------------------------


def test_parse_worktrees_marks_the_first_as_main() -> None:
    porcelain = (
        "worktree /code/proj\nHEAD abc123\nbranch refs/heads/main\n\n"
        "worktree /code/proj/temp/spike\nHEAD def456\nbranch refs/heads/spike\n\n"
        "worktree /code/proj/temp/gone\nHEAD 000\ndetached\nprunable gitdir file points to non-existent location\n\n"
    )
    trees = parse_worktrees(porcelain)
    assert [t.is_main for t in trees] == [True, False, False]
    assert trees[1].branch == "spike"
    assert trees[2].prunable and trees[2].detached
    assert "non-existent" in trees[2].prune_reason


def test_a_sibling_worktree_is_not_a_clone(tmp_path: pathlib.Path, repo: pathlib.Path) -> None:
    """A worktree outside its clone must not be discovered as a clone of its own.

    `Parquet.SourceGenerator-golden` is a worktree of `Parquet.SourceGenerator`
    placed as its sibling. Accepting a `.git` *file* counted it separately and
    double-counted its 118 local-only commits into the estate total.
    """
    sibling = tmp_path / "clone-golden"
    _git(["worktree", "add", "-q", "-b", "golden", str(sibling)], repo)
    assert (sibling / ".git").is_file(), "a linked worktree carries a .git file"

    found = discovery.scan(tmp_path)

    assert repo in found
    assert sibling not in found
