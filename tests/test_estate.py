"""Tests for `estate` — local clone discovery, inspection and cleanup."""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import subprocess

import pytest

from rig.estate import discovery, risk
from rig.estate.checks import (
    Finding,
    Severity,
    check_branches,
    check_repo_state,
    check_temp,
    check_worktrees,
    inspect_repo,
    parse_branches,
    parse_worktrees,
)
from rig.estate.cleanup import Outcome, clean_repo
from rig.estate.prs import PullRequest
from rig.estate.reclaim import human, scan_repo


def _git(args: list[str], cwd: pathlib.Path) -> None:
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True)


@pytest.fixture
def repo(tmp_path: pathlib.Path) -> pathlib.Path:
    """A real git repo with one commit, pushed to a bare origin.

    The remote matters: risk is measured as commits on *no remote*, so a repo
    without one legitimately reports every commit as local-only. That is the
    unusual case, and `_drop_remote` covers it explicitly.
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


def _drop_remote(repo: pathlib.Path) -> pathlib.Path:
    """Remove the origin, so every commit becomes local-only."""
    _git(["remote", "remove", "origin"], repo)
    return repo


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


def test_parse_branches_reads_gone_upstreams() -> None:
    raw = (
        "refs/heads/main\trefs/remotes/origin/main\t\n"
        "refs/heads/old\trefs/remotes/origin/old\t[gone]\n"
        "refs/heads/local\t\t\n"
    )
    branches = parse_branches(raw, current="main", worktree_branches={"local"})
    by_name = {b.name: b for b in branches}
    assert by_name["main"].is_current
    assert by_name["old"].upstream_gone and by_name["old"].upstream == "origin/old"
    assert not by_name["local"].upstream
    assert by_name["local"].checked_out_elsewhere


def test_human_formats_at_one_decimal() -> None:
    assert human(0) == "0B"
    assert human(1536) == "1.5KB"
    assert human(5 * 1024**3) == "5.0GB"


# ---------------------------------------------------------------------------
# checks against a real repo
# ---------------------------------------------------------------------------


def test_a_clean_repo_reports_nothing(repo: pathlib.Path) -> None:
    assert inspect_repo(repo).findings == []


def test_dirty_main_worktree_is_an_error(repo: pathlib.Path) -> None:
    (repo / "README.md").write_text("changed\n", encoding="utf-8")
    findings = check_worktrees(repo)
    assert [f.check for f in findings] == ["worktree-dirty"]
    assert findings[0].severity is Severity.ERROR


def test_a_worktree_outside_temp_is_flagged(repo: pathlib.Path, tmp_path: pathlib.Path) -> None:
    outside = tmp_path / "elsewhere"
    _git(["worktree", "add", "-q", "-b", "spike", str(outside)], repo)
    checks = {f.check for f in check_worktrees(repo)}
    assert "worktree-off-convention" in checks


def test_a_worktree_under_temp_is_not_flagged(repo: pathlib.Path) -> None:
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    checks = {f.check for f in check_worktrees(repo)}
    assert "worktree-off-convention" not in checks


def test_a_local_only_branch_is_reported_never_actioned(repo: pathlib.Path) -> None:
    """A branch with no upstream and work of its own is reported, never touched.

    It needs a commit that is *not* on the base — a branch merely pointing at
    `origin/main` has genuinely landed, and `branch-merged` is the right answer
    for it.
    """
    _git(["checkout", "-q", "-b", "orphan"], repo)
    (repo / "own.txt").write_text("mine\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "diverged"], repo)
    _git(["checkout", "-q", "main"], repo)

    findings = [f for f in check_branches(repo) if f.subject == "orphan"]
    assert [f.check for f in findings] == ["branch-local-only"]
    assert not findings[0].actionable


def test_temp_not_ignored_is_an_error(repo: pathlib.Path) -> None:
    (repo / "temp").mkdir()
    (repo / "temp" / "scratch.txt").write_text("x", encoding="utf-8")
    findings = [f for f in check_temp(repo) if f.check == "temp-not-ignored"]
    assert findings and findings[0].severity is Severity.ERROR


def test_temp_stale_entries_are_actionable_once_ignored(repo: pathlib.Path) -> None:
    (repo / ".gitignore").write_text("temp/\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "ignore temp"], repo)
    temp = repo / "temp"
    temp.mkdir()
    (temp / "old.log").write_text("x", encoding="utf-8")

    later = dt.datetime.now(dt.UTC) + dt.timedelta(days=30)
    findings = check_temp(repo, now=later)
    assert [f.check for f in findings] == ["temp-stale"]
    assert findings[0].actionable


# ---------------------------------------------------------------------------
# cleanup safety — the part that must never be wrong
# ---------------------------------------------------------------------------


def test_a_branch_holding_local_only_commits_is_never_deleted(repo: pathlib.Path) -> None:
    """The regression that matters. No flag may delete the last copy of a commit."""
    _git(["checkout", "-q", "-b", "work"], repo)
    (repo / "new.txt").write_text("only here\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "local only"], repo)
    _git(["checkout", "-q", "main"], repo)

    report = inspect_repo(repo)
    report.findings.append(
        Finding(
            check="branch-merged",
            severity=Severity.WARN,
            subject="work",
            message="pretend git believes this landed",
            actionable=True,
        )
    )
    actions = clean_repo(report, apply=True, force=True)
    verdicts = {a.subject: a for a in actions}
    assert verdicts["work"].outcome is Outcome.REFUSED
    assert "on no remote" in verdicts["work"].detail
    # and it really is still there
    branches = subprocess.run(
        ["git", "branch", "--format=%(refname:short)"], cwd=repo, capture_output=True, text=True, check=True
    ).stdout
    assert "work" in branches


def _push_as(repo: pathlib.Path, local: str, remote_branch: str, *, remote: str = "origin") -> None:
    """Commit on *local*, push it as *remote_branch*, then delete that remote branch, as a merge does."""
    _git(["checkout", "-q", "-b", local], repo)
    (repo / "new.txt").write_text("landed by squash\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "work"], repo)
    _git(["push", "-q", "-u", remote, f"{local}:{remote_branch}"], repo)
    _git(["checkout", "-q", "main"], repo)
    _git(["push", "-q", remote, "--delete", remote_branch], repo)
    _git(["fetch", "-q", "--prune", remote], repo)


def _merged_at(repo: pathlib.Path, number: int, head_ref: str, tip_of: str) -> dict[str, PullRequest]:
    """GitHub's view: PR *number* merged *head_ref* with *tip_of*'s current commit as its head."""
    tip = subprocess.run(["git", "rev-parse", tip_of], cwd=repo, check=True, capture_output=True, text=True).stdout
    return {head_ref: PullRequest(number, "MERGED", head_ref, head_oid=tip.strip())}


def _commit_more(repo: pathlib.Path, branch: str) -> None:
    _git(["checkout", "-q", branch], repo)
    (repo / "later.txt").write_text("never pushed\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "after the push"], repo)
    _git(["checkout", "-q", "main"], repo)


def test_a_branch_pushed_under_another_name_matches_its_merged_pr(repo: pathlib.Path) -> None:
    """GitHub knows the PR by the remote branch name; the local name differs after a port or rename."""
    _push_as(repo, "port/work", "30-work")
    heads = _merged_at(repo, 68, "30-work", "port/work")

    actions = clean_repo(inspect_repo(repo, merged_heads=heads), apply=False, merged_heads=heads)

    verdict = next(a for a in actions if a.subject == "port/work")
    assert verdict.outcome is Outcome.WOULD_CLEAN
    assert verdict.detail.startswith("squash-merged in #68")


@pytest.mark.parametrize(("local", "remote_branch"), [("work", "work"), ("port/work", "30-work")])
def test_a_commit_after_the_merged_head_keeps_the_branch(repo: pathlib.Path, local: str, remote_branch: str) -> None:
    """The PR merged what was pushed; a later local commit was never in it, so the name match proves nothing."""
    _push_as(repo, local, remote_branch)
    heads = _merged_at(repo, 68, remote_branch, local)
    _commit_more(repo, local)

    actions = clean_repo(inspect_repo(repo, merged_heads=heads), apply=True, merged_heads=heads)

    verdict = next(a for a in actions if a.subject == local)
    assert verdict.outcome is Outcome.REFUSED
    assert "on no remote" in verdict.detail


def test_a_merged_pr_without_a_known_head_never_authorises_deletion(repo: pathlib.Path) -> None:
    _push_as(repo, "work", "work")
    heads = {"work": PullRequest(68, "MERGED", "work")}

    actions = clean_repo(inspect_repo(repo, merged_heads=heads), apply=True, merged_heads=heads)

    assert next(a for a in actions if a.subject == "work").outcome is Outcome.REFUSED


def test_an_upstream_on_another_remote_never_matches_origins_prs(repo: pathlib.Path, tmp_path: pathlib.Path) -> None:
    """The merged-PR list is origin's; a same-named branch on another remote proves nothing."""
    other = tmp_path / "other.git"
    _git(["init", "-q", "--bare", "-b", "main", str(other)], tmp_path)
    _git(["remote", "add", "other", str(other)], repo)
    _push_as(repo, "port/work", "30-work", remote="other")
    heads = _merged_at(repo, 68, "30-work", "port/work")

    actions = clean_repo(inspect_repo(repo, merged_heads=heads), apply=True, merged_heads=heads)

    verdict = next(a for a in actions if a.subject == "port/work")
    assert verdict.outcome is Outcome.REFUSED
    assert "on no remote" in verdict.detail


def test_dry_run_removes_nothing(repo: pathlib.Path) -> None:
    (repo / ".gitignore").write_text("temp/\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "ignore"], repo)
    stale = repo / "temp" / "old"
    stale.mkdir(parents=True)
    (stale / "f.txt").write_text("x", encoding="utf-8")

    later = dt.datetime.now(dt.UTC) + dt.timedelta(days=30)
    report = inspect_repo(repo, now=later)
    actions = clean_repo(report, apply=False)
    assert all(a.outcome is Outcome.WOULD_CLEAN for a in actions)
    assert stale.exists()


def test_a_path_outside_the_clone_is_refused(repo: pathlib.Path, tmp_path: pathlib.Path) -> None:
    outside = tmp_path / "not-mine"
    outside.mkdir()
    report = inspect_repo(repo)
    report.findings.append(
        Finding(
            check="temp-stale",
            severity=Severity.INFO,
            subject=str(outside),
            message="",
            actionable=True,
        )
    )
    actions = clean_repo(report, apply=True)
    assert actions[-1].outcome is Outcome.REFUSED
    assert outside.exists()


# ---------------------------------------------------------------------------
# risk
# ---------------------------------------------------------------------------


def test_local_only_commits_counts_distinct_not_per_branch(repo: pathlib.Path) -> None:
    """Two branches sharing a commit must not double-count it."""
    _drop_remote(repo)
    (repo / "a.txt").write_text("a\n", encoding="utf-8")
    _git(["add", "."], repo)
    _git(["commit", "-qm", "shared"], repo)
    _git(["branch", "one"], repo)
    _git(["branch", "two"], repo)

    # no remote at all, so every commit is local-only — but only two of them
    assert risk.local_only_commits(repo) == 2


def test_exposure_flags_a_repo_with_no_remote(repo: pathlib.Path) -> None:
    exposure = risk.inspect_exposure(_drop_remote(repo))
    assert not exposure.has_remote
    assert exposure.at_risk


def test_find_duplicates_normalises_ssh_and_https(tmp_path: pathlib.Path) -> None:
    a = risk.Exposure(repo=tmp_path / "a", origin="git@github.com:me/thing.git", local_only_commits=3)
    b = risk.Exposure(repo=tmp_path / "b", origin="https://github.com/me/thing", local_only_commits=3)
    c = risk.Exposure(repo=tmp_path / "c", origin="git@github.com:me/other.git")

    dupes = risk.find_duplicates([a, b, c])
    assert len(dupes) == 1
    assert dupes[0].origin == "github.com/me/thing"
    assert dupes[0].exposure == {"a": 3, "b": 3}


# ---------------------------------------------------------------------------
# reclaim
# ---------------------------------------------------------------------------


def test_reclaim_classifies_a_temp_worktree_separately(repo: pathlib.Path) -> None:
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    (repo / "temp" / "scratch").mkdir()

    entries = {e.path.name: e for e in scan_repo(repo, floor=0).entries}
    assert entries["spike"].is_worktree
    assert not entries["spike"].safe_to_rmtree
    assert not entries["scratch"].is_worktree
    assert entries["scratch"].safe_to_rmtree


def test_reclaim_finds_nested_node_modules(repo: pathlib.Path) -> None:
    nested = repo / "frontend" / "node_modules"
    nested.mkdir(parents=True)
    (nested / "big.js").write_text("x" * 2048, encoding="utf-8")
    categories = {e.category for e in scan_repo(repo, floor=0).entries}
    assert "node_modules" in categories


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


def test_a_worktree_outside_the_clone_does_not_crash_rendering(repo: pathlib.Path, tmp_path: pathlib.Path) -> None:
    """`relative_to` raises on exactly the case `worktree-off-convention` reports."""
    from rig.estate.locate import short as _short

    outside = tmp_path / "elsewhere"
    _git(["worktree", "add", "-q", "-b", "spike", str(outside)], repo)
    assert _short(outside, repo) == str(outside)
    assert _short(repo / "temp" / "x", repo) == "temp/x"
    assert _short(repo, repo) == "."


def _worktree_finding(path: pathlib.Path) -> Finding:
    return Finding(
        check="worktree-merged",
        severity=Severity.INFO,
        subject=str(path),
        message="",
        actionable=True,
    )


def test_a_dirty_worktree_is_never_removed(repo: pathlib.Path) -> None:
    """Modified tracked files must stop removal, and leave the tree in place."""
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    (tree / "README.md").write_text("changed\n", encoding="utf-8")

    report = inspect_repo(repo)
    report.findings.append(_worktree_finding(tree))
    actions = clean_repo(report, apply=True, merged_heads={"spike": PullRequest(1, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.REFUSED
    assert verdict.detail == "uncommitted changes"
    assert tree.is_dir(), "the worktree must still be there"


def test_an_untracked_file_also_blocks_removal(repo: pathlib.Path) -> None:
    """`git status --porcelain` reports untracked files, and git refuses on them too.

    Worth pinning separately: a scratch file someone left in a worktree is the
    likeliest form of unsaved work, and it is invisible to any commit-based check.
    """
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    (tree / "scratch.txt").write_text("notes I have not saved\n", encoding="utf-8")

    report = inspect_repo(repo)
    report.findings.append(_worktree_finding(tree))
    actions = clean_repo(report, apply=True, merged_heads={"spike": PullRequest(1, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.REFUSED
    assert (tree / "scratch.txt").is_file()


def test_a_clean_worktree_is_removed(repo: pathlib.Path) -> None:
    """The other half of the contract — the guard must not refuse everything."""
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"

    report = inspect_repo(repo)
    report.findings.append(_worktree_finding(tree))
    actions = clean_repo(report, apply=True, merged_heads={"spike": PullRequest(1, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.CLEANED
    assert not tree.exists()


def test_no_path_through_estate_deletes_a_dirty_worktree(repo: pathlib.Path) -> None:
    """Even with `force`, git refuses — so the outcome is a loud failure, not loss.

    `force` relaxes this tool's own guard and nothing else. `git worktree remove`
    still declines a tree with modified or untracked files unless given its own
    `--force`, which `estate` never passes. So there is no route through here
    that discards uncommitted work.
    """
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    (tree / "README.md").write_text("changed\n", encoding="utf-8")

    report = inspect_repo(repo)
    report.findings.append(_worktree_finding(tree))
    actions = clean_repo(report, apply=True, force=True, merged_heads={"spike": PullRequest(1, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.FAILED
    assert "modified or untracked" in verdict.detail
    assert tree.is_dir(), "the worktree and its changes survive"


def test_check_repo_state_flags_unignored_todo_file(repo: pathlib.Path) -> None:
    (repo / "TODO.md").write_text("# Tasks\n", encoding="utf-8")
    findings = check_repo_state(repo)
    todo_findings = [f for f in findings if f.check == "todo-not-ignored"]
    assert len(todo_findings) == 1
    assert todo_findings[0].subject == "TODO.md"


def test_check_repo_state_passes_when_todo_ignored(repo: pathlib.Path) -> None:
    (repo / "TODO.md").write_text("# Tasks\n", encoding="utf-8")
    (repo / ".gitignore").write_text("TODO.md\n", encoding="utf-8")
    findings = check_repo_state(repo)
    todo_findings = [f for f in findings if f.check == "todo-not-ignored"]
    assert len(todo_findings) == 0
