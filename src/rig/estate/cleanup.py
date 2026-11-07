"""The only module that deletes anything.

Everything is a dry run unless `apply=True`, matching
`repo_governance.branch_cleanup`.

**Safety is per item, and that took two goes to get right.** The first cut
refused any clone holding a dirty file or an unpushed worktree. Run across this
estate it refused 9 of 17 clones and hid 23 provably safe deletions behind a
risk that lives at branch granularity — an uncommitted file in `src/` says
nothing about whether a branch whose upstream was deleted can go. Each handler
now proves its own target instead:

- **A branch** is deleted only when nothing is lost — either it has no commits
  outside a remote, or `gh` confirms a merged PR for its head ref. The second
  clause is not optional: a squash merge plus a deleted remote branch leaves
  the originals on no remote permanently, so the local signal alone refuses
  every branch a person means by "already merged".
- **A worktree** is removed only when it is clean and pushed; `force` relaxes
  that, because occasionally you do know the tree is scrap.
- **A branch checked out elsewhere** is refused; git would refuse too, but
  failing with the reason beats a confusing git error.
- **Anything resolving outside the clone** is refused rather than followed.

A registered worktree goes through `git worktree remove`, never `rm -rf`. The
latter leaves the parent's administrative files behind, which is how a repo ends
up with prunable entries pointing at nothing.
"""

from __future__ import annotations

import pathlib
from collections.abc import Collection, Mapping
from dataclasses import dataclass
from enum import StrEnum

from rig import proc, tracing
from rig.estate import busy
from rig.estate.checks import (
    Finding,
    RepoReport,
    foreign_admin_dir,
    is_dirty,
    list_worktrees,
    orphaned_count,
    own_worktrees_dir,
)
from rig.estate.trim import LONG_PATHS, remove_tree

BRANCH_CHECKS = ("branch-upstream-gone", "branch-merged")
WORKTREE_CHECKS = ("temp-stale-worktree", "worktree-merged")
#: Every finding check `clean_repo` acts on; anything else is reported, never touched.
TIDY_CHECKS = ("worktree-prunable", *BRANCH_CHECKS, *WORKTREE_CHECKS, "temp-stale")


class Outcome(StrEnum):
    """What happened to one finding."""

    CLEANED = "cleaned"
    WOULD_CLEAN = "would-clean"
    REFUSED = "refused"
    FAILED = "failed"


@dataclass(frozen=True)
class Action:
    """One cleanup decision and its result."""

    check: str
    subject: str
    outcome: Outcome
    detail: str = ""


@dataclass(frozen=True, slots=True)
class Busy:
    """One snapshot of the directories processes are working in, taken once per run.

    *dirs* is None when it could not be read. That is not "nothing is busy": an
    unchecked path is refused unless the caller forces it.
    """

    dirs: frozenset[pathlib.Path] | None

    @classmethod
    def snapshot(cls) -> Busy:
        return cls(busy.working_dirs())

    def refusal(self, path: pathlib.Path, force: bool) -> str:
        """Why *path* must not be removed while in use; "" when it may be."""
        if self.dirs is None:
            return (
                "" if force else "cannot tell whether a process is working in it (lsof unavailable) — --force to skip"
            )
        # No flag overrides this: removing it pulls the directory out from under a live process.
        return "a process is working in it (a shell, agent or Orca terminal)" if busy.in_use(path, self.dirs) else ""


@dataclass(frozen=True, slots=True)
class _RunResult:
    """Whether a command succeeded, and its combined output."""

    ok: bool
    output: str


def _git(cwd: pathlib.Path, *args: str) -> _RunResult:
    """Run git, returning whether it succeeded and its combined output."""
    ran = proc.git(*args, cwd=cwd, timeout=120)
    return _RunResult(ok=ran.ok, output=(ran.out + ran.err).strip())


def _guard_path(repo: pathlib.Path, path: pathlib.Path) -> str:
    """Return a refusal reason for removing *path*, or "" when safe.

    Only paths inside the repo are ever touched. A `temp` entry resolving
    outside it — a symlink to somewhere real — is refused rather than followed.
    """
    try:
        resolved = path.resolve()
    except OSError as exc:
        return f"cannot resolve: {exc}"
    if repo.resolve() not in resolved.parents:
        return "resolves outside the repo"
    if resolved == repo.resolve():
        return "is the repo itself"
    return ""


def _clean_prunable(repo: pathlib.Path, apply: bool) -> Action:
    if not apply:
        return Action("worktree-prunable", str(repo), Outcome.WOULD_CLEAN, "git worktree prune")
    run = _git(repo, *LONG_PATHS, "worktree", "prune")
    ok, out = run.ok, run.output
    return Action(
        "worktree-prunable",
        str(repo),
        Outcome.CLEANED if ok else Outcome.FAILED,
        out or "pruned",
    )


def _clean_branch(
    repo: pathlib.Path,
    finding: Finding,
    apply: bool,
    merged_heads: Mapping[str, int] | None = None,
) -> Action:
    """Delete one branch, refusing only if it is genuinely the last copy of work.

    Two signals, and needing both is the whole subtlety.

    `rev-list <branch> --not --remotes` counts commits on no remote. On its own
    that is **useless for the case this command exists for**. A squash merge
    replaces a branch's commits with one new commit on `main`; deleting the
    remote branch afterwards leaves the originals on no remote *permanently*.
    Measured on `design-system`: all 12 branches whose upstream was deleted
    reported 1-6 "local-only" commits, and every one of them had a merged PR.
    Gating on that count alone refuses 100% of what a person means by "already
    merged".

    So a merged PR for the branch's head ref, read from `gh`, is what authorises
    deletion when the commits look local-only. *merged_heads* carries that map.
    Without it this falls back to the local signal and refuses — the honest
    answer when there is no network.
    """
    branch = finding.subject
    live = {t.branch for t in list_worktrees(repo) if t.branch and not t.is_main}
    if branch in live:
        return Action(finding.check, branch, Outcome.REFUSED, "checked out in another worktree")

    only_here = _git(repo, "rev-list", "--count", branch, "--not", "--remotes").output
    orphaned = int(only_here) if only_here.isdigit() else 0

    authority = "no commits outside a remote"
    if orphaned:
        pr = (merged_heads or {}).get(branch)
        if pr is None:
            detail = f"{orphaned} commit(s) on no remote"
            if merged_heads is None:
                detail += " — pass --merged to ask GitHub"
            return Action(finding.check, branch, Outcome.REFUSED, detail)
        authority = f"squash-merged in #{pr}"

    # -D rather than -d: under a squash policy git cannot see that the work
    # landed, so -d would refuse a branch just proved safe.
    if not apply:
        return Action(finding.check, branch, Outcome.WOULD_CLEAN, f"{authority} · git branch -D {branch}")
    run = _git(repo, *LONG_PATHS, "branch", "-D", branch)
    ok, out = run.ok, run.output
    return Action(finding.check, branch, Outcome.CLEANED if ok else Outcome.FAILED, out or authority)


def _clean_worktree(
    repo: pathlib.Path,
    finding: Finding,
    apply: bool,
    force: bool = False,
    merged_heads: Mapping[str, int] | None = None,
    in_use: Busy | None = None,
) -> Action:
    """Remove one worktree, on the same two signals a branch gets.

    Deliberately mirrors `_clean_branch`, because the failure mode is identical
    and a first cut had it wrong here after fixing it there: risk is
    `rev-list HEAD --not --remotes`, not "ahead of upstream", and a merged PR
    authorises removal when commits are stranded by a squash.

    Three refusals hold even under `force`, because each one is a case where
    the usual signals read wrong rather than merely cautious:

    - **unborn HEAD**: its branch ref is gone, so there is no commit to count
      and `orphaned_count` reads 0; the files on disk may be the only copy.
    - **another clone's worktree**: `git worktree remove` from here deletes
      that clone's live work.
    - **in use**: a process is working in it.
    """
    path = pathlib.Path(finding.subject)
    trees = list_worktrees(repo)
    try:
        resolved = path.resolve()
    except OSError:
        resolved = None
    tree = next((t for t in trees if not t.is_main and t.path.resolve() == resolved), None)
    if tree is None:
        refusal = _guard_path(repo, path)
        if refusal:
            return Action(finding.check, finding.subject, Outcome.REFUSED, refusal)
    else:
        # Ownership first: a copied registry entry usually reads as unborn too, since the
        # copy lacks the branch, and "another clone's" is the reason that matters.
        own = own_worktrees_dir(repo)
        if own is None:
            return Action(finding.check, finding.subject, Outcome.REFUSED, "cannot read this clone's git dir")
        if foreign_admin_dir(tree.path, own) is not None:
            return Action(
                finding.check, finding.subject, Outcome.REFUSED, "belongs to another clone — see worktree-foreign-owner"
            )
        if tree.unborn:
            return Action(finding.check, finding.subject, Outcome.REFUSED, "unborn HEAD — see worktree-unborn-head")
    if in_use is not None:
        refusal = in_use.refusal(path, force)
        if refusal:
            return Action(finding.check, finding.subject, Outcome.REFUSED, refusal)
    if not force and is_dirty(path):
        return Action(finding.check, finding.subject, Outcome.REFUSED, "uncommitted changes")

    orphaned = 0 if force else orphaned_count(path)
    if orphaned:
        branch = tree.branch if tree is not None else ""
        pr = (merged_heads or {}).get(branch)
        if pr is None:
            detail = f"{orphaned} commit(s) on no remote"
            if merged_heads is None:
                detail += " — pass --merged to ask GitHub"
            return Action(finding.check, finding.subject, Outcome.REFUSED, detail)

    if not apply:
        return Action(finding.check, finding.subject, Outcome.WOULD_CLEAN, f"git worktree remove {path}")
    run = _git(repo, *LONG_PATHS, "worktree", "remove", str(path))
    ok, out = run.ok, run.output
    return Action(finding.check, finding.subject, Outcome.CLEANED if ok else Outcome.FAILED, out or "removed")


def _clean_temp(
    repo: pathlib.Path, finding: Finding, apply: bool, force: bool = False, in_use: Busy | None = None
) -> Action:
    path = pathlib.Path(finding.subject)
    refusal = _guard_path(repo, path) or (in_use.refusal(path, force) if in_use is not None else "")
    if refusal:
        return Action(finding.check, finding.subject, Outcome.REFUSED, refusal)
    if not apply:
        return Action(finding.check, finding.subject, Outcome.WOULD_CLEAN, f"rm -rf {path}")
    try:
        if path.is_dir() and not path.is_symlink():
            remove_tree(path)
        else:
            path.unlink()
    except OSError as exc:
        return Action(finding.check, finding.subject, Outcome.FAILED, str(exc))
    return Action(finding.check, finding.subject, Outcome.CLEANED, "removed")


def clean_repo(
    report: RepoReport,
    *,
    apply: bool = False,
    force: bool = False,
    merged_heads: Mapping[str, int] | None = None,
    in_use: Busy | None = None,
    checks: Collection[str] | None = None,
) -> list[Action]:
    """Act on the actionable findings in *report*.

    Safety is per item. Each handler proves its own target holds nothing that
    exists only here — a branch with no local-only commits, a worktree that is
    clean and pushed — so an unrelated dirty file elsewhere in the clone does
    not block work that is provably safe.

    `force` relaxes the worktree refusals only. No flag will delete a branch
    holding the last copy of a commit; that is not a judgement call.

    *in_use* is the run's snapshot of process working directories; a directory
    one of them sits in is never removed. None skips that check, which only a
    caller that has already ruled it out should do. *checks* limits the run to
    those finding checks.
    """
    actions: list[Action] = []
    actionable = [f for f in report.actionable if checks is None or f.check in checks]
    with tracing.span(
        "estate.clean_repo", scope="rig.estate", **{"estate.clone": str(report.repo), "estate.apply": apply}
    ):
        prunable = [f for f in actionable if f.check == "worktree-prunable"]
        if prunable:
            actions.append(_clean_prunable(report.repo, apply))

        for finding in actionable:
            with tracing.span(
                f"estate.clean {finding.check}", scope="rig.estate", **{"estate.subject": finding.subject}
            ) as s:
                if finding.check in BRANCH_CHECKS:
                    actions.append(_clean_branch(report.repo, finding, apply, merged_heads))
                elif finding.check in WORKTREE_CHECKS:
                    actions.append(_clean_worktree(report.repo, finding, apply, force, merged_heads, in_use))
                elif finding.check == "temp-stale":
                    actions.append(_clean_temp(report.repo, finding, apply, force, in_use))
                else:
                    continue
                s.set(**{"estate.outcome": str(actions[-1].outcome), "estate.detail": actions[-1].detail[:200]})

    return actions
