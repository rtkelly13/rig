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
from collections.abc import Mapping
from dataclasses import dataclass
from enum import StrEnum

from rig import proc, tracing
from rig.estate.checks import (
    Finding,
    RepoReport,
    is_dirty,
    list_worktrees,
    orphaned_count,
)
from rig.estate.trim import LONG_PATHS, remove_tree


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
) -> Action:
    """Remove one worktree, on the same two signals a branch gets.

    Deliberately mirrors `_clean_branch`, because the failure mode is identical
    and a first cut had it wrong here after fixing it there: risk is
    `rev-list HEAD --not --remotes`, not "ahead of upstream", and a merged PR
    authorises removal when commits are stranded by a squash.
    """
    path = pathlib.Path(finding.subject)
    registered = {t.path.resolve() for t in list_worktrees(repo) if not t.is_main}
    try:
        resolved = path.resolve()
    except OSError:
        resolved = None
    if resolved not in registered:
        refusal = _guard_path(repo, path)
        if refusal:
            return Action(finding.check, finding.subject, Outcome.REFUSED, refusal)
    if not force and is_dirty(path):
        return Action(finding.check, finding.subject, Outcome.REFUSED, "uncommitted changes")

    orphaned = 0 if force else orphaned_count(path)
    if orphaned:
        branch = next((t.branch for t in list_worktrees(repo) if t.path == path), "")
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


def _clean_temp(repo: pathlib.Path, finding: Finding, apply: bool) -> Action:
    path = pathlib.Path(finding.subject)
    refusal = _guard_path(repo, path)
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
) -> list[Action]:
    """Act on the actionable findings in *report*.

    Safety is per item. Each handler proves its own target holds nothing that
    exists only here — a branch with no local-only commits, a worktree that is
    clean and pushed — so an unrelated dirty file elsewhere in the clone does
    not block work that is provably safe.

    `force` relaxes the worktree refusals only. No flag will delete a branch
    holding the last copy of a commit; that is not a judgement call.
    """
    actions: list[Action] = []
    with tracing.span(
        "estate.clean_repo", scope="rig.estate", **{"estate.clone": str(report.repo), "estate.apply": apply}
    ):
        prunable = [f for f in report.actionable if f.check == "worktree-prunable"]
        if prunable:
            actions.append(_clean_prunable(report.repo, apply))

        for finding in report.actionable:
            with tracing.span(
                f"estate.clean {finding.check}", scope="rig.estate", **{"estate.subject": finding.subject}
            ) as s:
                if finding.check in ("branch-upstream-gone", "branch-merged"):
                    actions.append(_clean_branch(report.repo, finding, apply, merged_heads))
                elif finding.check in ("temp-stale-worktree", "worktree-merged"):
                    actions.append(_clean_worktree(report.repo, finding, apply, force, merged_heads))
                elif finding.check == "temp-stale":
                    actions.append(_clean_temp(report.repo, finding, apply))
                else:
                    continue
                s.set(**{"estate.outcome": str(actions[-1].outcome), "estate.detail": actions[-1].detail[:200]})

    return actions
