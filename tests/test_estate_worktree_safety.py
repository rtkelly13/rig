"""Worktrees whose usual signals read wrong: unborn HEAD, another clone's, or in use.

Each case comes from the 2026-11-06 estate triage. 56 worktrees lost their
branch refs when an old clone went and read as `worktree-dirty`; 63 registry
entries in `.old` clones pointed at worktrees the new clones owned; and an
`estate tidy --merged` run would have removed 8 worktrees with live terminals.
"""

from __future__ import annotations

import pathlib
import shutil

import pytest

from rig import proc
from rig.estate.checks import Finding, Severity, check_worktrees, inspect_repo
from rig.estate.cleanup import Busy, Outcome, clean_repo
from rig.estate.prs import PullRequest

pytestmark = pytest.mark.ci


def _git(args: list[str], cwd: pathlib.Path) -> str:
    return proc.git(*args, cwd=cwd, check=True).out.strip()


def _clone(parent: pathlib.Path, name: str, origin: pathlib.Path) -> pathlib.Path:
    root = parent / name
    _git(["clone", "-q", str(origin), str(root)], parent)
    _git(["config", "user.email", "t@example.com"], root)
    _git(["config", "user.name", "T"], root)
    return root


@pytest.fixture
def origin(tmp_path: pathlib.Path) -> pathlib.Path:
    """A bare origin holding one commit on `main`."""
    bare = tmp_path / "origin.git"
    _git(["init", "-q", "--bare", "-b", "main", str(bare)], tmp_path)
    seed = tmp_path / "seed"
    seed.mkdir()
    _git(["init", "-q", "-b", "main"], seed)
    _git(["config", "user.email", "t@example.com"], seed)
    _git(["config", "user.name", "T"], seed)
    (seed / "README.md").write_text("hi\n", encoding="utf-8")
    _git(["add", "."], seed)
    _git(["commit", "-qm", "first"], seed)
    _git(["push", "-q", str(bare), "main"], seed)
    return bare


@pytest.fixture
def repo(tmp_path: pathlib.Path, origin: pathlib.Path) -> pathlib.Path:
    return _clone(tmp_path, "clone", origin)


def _spike(repo: pathlib.Path) -> pathlib.Path:
    """A worktree on `spike` with one commit of its own."""
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    (tree / "notes.md").write_text("work\n", encoding="utf-8")
    _git(["add", "."], tree)
    _git(["commit", "-qm", "spike work"], tree)
    return tree


def _orphan_head(repo: pathlib.Path) -> str:
    """Delete `spike`'s ref while its worktree still has it checked out; return the lost tip."""
    tip = _git(["rev-parse", "refs/heads/spike"], repo)
    _git(["update-ref", "-d", "refs/heads/spike"], repo)
    return tip


def _merged(path: pathlib.Path) -> Finding:
    return Finding(check="worktree-merged", severity=Severity.INFO, subject=str(path), message="", actionable=True)


def _checks(findings: list[Finding], subject: pathlib.Path) -> set[str]:
    return {f.check for f in findings if f.subject == str(subject)}


# ---------------------------------------------------------------------------
# unborn HEAD
# ---------------------------------------------------------------------------


def test_an_unborn_worktree_is_reported_as_unborn_not_dirty(repo: pathlib.Path) -> None:
    tree = _spike(repo)
    tip = _orphan_head(repo)

    findings = check_worktrees(repo)

    checks = _checks(findings, tree)
    assert "worktree-unborn-head" in checks
    assert "worktree-dirty" not in checks, "every file reads as staged with no commit behind HEAD; that is not dirt"
    unborn = next(f for f in findings if f.check == "worktree-unborn-head")
    assert unborn.fix == f"cd {repo} && git branch spike {tip}", "the commit is still here, so the fix restores it"


def test_an_unborn_worktree_whose_commit_is_gone_says_the_files_are_the_only_copy(repo: pathlib.Path) -> None:
    tree = _spike(repo)
    _orphan_head(repo)
    admin = pathlib.Path(_git(["rev-parse", "--absolute-git-dir"], tree))
    missing = "1" * 40
    with (admin / "logs" / "HEAD").open("a", encoding="utf-8") as log:
        log.write(f"{'0' * 40} {missing} T <t@example.com> 0 +0000\tcommit: lost\n")

    unborn = next(f for f in check_worktrees(repo) if f.check == "worktree-unborn-head")

    assert missing[:10] in unborn.message
    assert "only copy" in unborn.message
    assert unborn.fix == f"cd {tree} && git status"


def test_a_merged_branch_does_not_make_an_unborn_worktree_finished(repo: pathlib.Path) -> None:
    """With no ref there is no commit to count, so `orphaned_count` reads 0 and nothing else would stop removal."""
    tree = _spike(repo)
    _orphan_head(repo)

    findings = check_worktrees(repo, merged_heads={"spike": PullRequest(7, "MERGED", "spike")})

    assert "worktree-merged" not in _checks(findings, tree)


def test_tidy_refuses_an_unborn_worktree_even_forced(repo: pathlib.Path) -> None:
    tree = _spike(repo)
    _orphan_head(repo)
    report = inspect_repo(repo)
    report.findings.append(_merged(tree))

    actions = clean_repo(report, apply=True, force=True, merged_heads={"spike": PullRequest(7, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.REFUSED
    assert "unborn" in verdict.detail
    assert (tree / "notes.md").is_file()


# ---------------------------------------------------------------------------
# another clone's worktree
# ---------------------------------------------------------------------------


@pytest.fixture
def copied(tmp_path: pathlib.Path, origin: pathlib.Path, repo: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    """A second clone carrying a copy of *repo*'s registry entry, as a copied or moved clone does.

    Returns (the stale clone, the worktree that belongs to *repo*).
    """
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    stale = _clone(tmp_path, "stale", origin)
    shutil.copytree(repo / ".git" / "worktrees" / "spike", stale / ".git" / "worktrees" / "spike")
    return stale, tree


def test_a_worktree_registered_in_another_clone_is_foreign_there(copied: tuple[pathlib.Path, pathlib.Path]) -> None:
    stale, tree = copied

    findings = check_worktrees(stale)

    assert _checks(findings, tree) == {"worktree-foreign-owner"}, "nothing about the other clone's state is reported"


def test_the_owning_clone_does_not_call_its_own_worktree_foreign(
    repo: pathlib.Path, copied: tuple[pathlib.Path, pathlib.Path]
) -> None:
    _stale, tree = copied

    assert "worktree-foreign-owner" not in _checks(check_worktrees(repo), tree)


def test_tidy_never_removes_another_clones_worktree(copied: tuple[pathlib.Path, pathlib.Path]) -> None:
    stale, tree = copied
    report = inspect_repo(stale)
    report.findings.append(_merged(tree))

    actions = clean_repo(report, apply=True, force=True, merged_heads={"spike": PullRequest(7, "MERGED", "spike")})

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.REFUSED
    assert "another clone" in verdict.detail
    assert tree.is_dir()


# ---------------------------------------------------------------------------
# in use
# ---------------------------------------------------------------------------


def test_tidy_never_removes_a_worktree_a_process_is_working_in(repo: pathlib.Path) -> None:
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    report = inspect_repo(repo)
    report.findings.append(_merged(tree))

    in_use = Busy(frozenset({(tree / "src").resolve()}))
    actions = clean_repo(
        report, apply=True, force=True, merged_heads={"spike": PullRequest(7, "MERGED", "spike")}, in_use=in_use
    )

    verdict = next(a for a in actions if a.subject == str(tree))
    assert verdict.outcome is Outcome.REFUSED, "a shell below the worktree counts as well as one at its root"
    assert tree.is_dir()


def test_an_unreadable_snapshot_refuses_unless_forced(repo: pathlib.Path) -> None:
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    report = inspect_repo(repo)
    report.findings.append(_merged(tree))
    unknown = Busy(None)

    cautious = clean_repo(
        report, apply=False, merged_heads={"spike": PullRequest(7, "MERGED", "spike")}, in_use=unknown
    )
    forced = clean_repo(
        report, apply=False, force=True, merged_heads={"spike": PullRequest(7, "MERGED", "spike")}, in_use=unknown
    )

    assert next(a for a in cautious if a.subject == str(tree)).outcome is Outcome.REFUSED
    assert next(a for a in forced if a.subject == str(tree)).outcome is Outcome.WOULD_CLEAN


def test_an_idle_worktree_is_still_removed(repo: pathlib.Path) -> None:
    """The other half: a snapshot naming other directories must not block anything."""
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    report = inspect_repo(repo)
    report.findings.append(_merged(tree))

    actions = clean_repo(
        report,
        apply=True,
        merged_heads={"spike": PullRequest(7, "MERGED", "spike")},
        in_use=Busy(frozenset({repo.resolve() / "docs"})),
    )

    assert next(a for a in actions if a.subject == str(tree)).outcome is Outcome.CLEANED
    assert not tree.exists()


# ---------------------------------------------------------------------------
# scope
# ---------------------------------------------------------------------------


def test_checks_limits_a_run_to_the_named_findings(repo: pathlib.Path) -> None:
    _git(["worktree", "add", "-q", "-b", "spike", "temp/spike"], repo)
    tree = repo / "temp" / "spike"
    report = inspect_repo(repo)
    report.findings.append(_merged(tree))

    skipped = clean_repo(
        report, apply=False, merged_heads={"spike": PullRequest(7, "MERGED", "spike")}, checks={"branch-merged"}
    )
    chosen = clean_repo(
        report, apply=False, merged_heads={"spike": PullRequest(7, "MERGED", "spike")}, checks={"worktree-merged"}
    )

    assert all(a.subject != str(tree) for a in skipped)
    assert {a.check for a in chosen} == {"worktree-merged"}
