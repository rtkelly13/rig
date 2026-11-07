"""Delete remote branches whose work has already landed.

`delete_branch_on_merge` handles branches from the moment it is switched on.
It does nothing about branches merged before that, merged outside a PR, or
left behind when the setting was briefly off — so repos accumulate a tail of
dead refs that the GitHub setting will never clear.

**Squash merges are why this needs GitHub, not git.** Under a squash policy a
branch's commits never become ancestors of `main`, so `git branch --merged`
reports nothing: on a repo with 36 merged PRs it found zero. The only reliable
record that a branch landed is the PR that merged it, so merged-ness is read
from `gh`, not from the commit graph.

Three outcomes per branch:

``merged``
    A PR with this head ref was merged. Safe to delete.
``open-pr``
    A PR with this head ref is still open. Never touched.
``no-pr``
    No PR ever referenced it. **Reported, never deleted** — this is where
    genuinely unmerged work lives, and guessing costs someone their branch.

Deleting is opt-in (`apply=True`). Everything here is a dry run otherwise.
"""

from __future__ import annotations

import pathlib
from dataclasses import dataclass
from functools import lru_cache
from typing import Annotated, Literal

from pydantic import Field

from rig import proc
from rig.gh_origin import origin_args
from rig.repo_governance.policy_config import BranchCleanupRules, load_policy_config
from rig.typed_json import try_decode_json

__all__ = [
    "BranchVerdict",
    "CleanupPlan",
    "RepoIdentity",
    "can_prune",
    "classify_branches",
    "delete_remote_branch",
    "list_merged_pr_heads",
    "list_open_pr_heads",
    "list_remote_branches",
    "plan_cleanup",
    "repo_identity",
]

Status = Literal["merged", "open-pr", "no-pr", "protected"]

#: `gh pr list` caps at 1000; merged history on an old repo can exceed the
#: default 30 many times over, and a short page silently understates what is
#: safe to delete.
_PR_PAGE_LIMIT = 500


@dataclass(frozen=True)
class BranchVerdict:
    """What cleanup thinks of one remote branch."""

    branch: str
    status: Status
    #: The PR number that decided it, when there was one.
    pr: int | None = None

    @property
    def deletable(self) -> bool:
        return self.status == "merged"


@dataclass(frozen=True)
class CleanupPlan:
    """The full picture for one repo.

    ``skipped`` is set when the repo must not be pruned at all — currently
    when the remote is not one you can push to. A skipped plan carries no
    verdicts, so a caller that ignores the field still deletes nothing.
    """

    repo: str
    verdicts: tuple[BranchVerdict, ...]
    #: ``owner/name`` of the remote, used to dedupe repeat clones.
    slug: str = ""
    skipped: str = ""

    def of(self, status: Status) -> tuple[BranchVerdict, ...]:
        return tuple(v for v in self.verdicts if v.status == status)

    @property
    def deletable(self) -> tuple[BranchVerdict, ...]:
        return tuple(v for v in self.verdicts if v.deletable)


# ---------------------------------------------------------------------------
# Data gathering
# ---------------------------------------------------------------------------


_TIMEOUT = 120.0


def _run(args: list[str], cwd: pathlib.Path | None = None) -> str:
    """Run a ``git`` or ``gh`` *args* through `rig.proc`, returning stdout; "" on any failure."""
    tool, *rest = args
    run = proc.git if tool == "git" else proc.gh
    res = run(*rest, cwd=cwd, timeout=_TIMEOUT)
    return res.out if res.ok else ""


def list_remote_branches(repo_path: pathlib.Path, *, fetch: bool = True) -> list[str]:
    """Return remote branch names (no ``origin/`` prefix) for *repo_path*.

    Prunes first by default: without it, refs deleted on the server still show
    locally and cleanup reports branches that are already gone.
    """
    if fetch:
        _run(["git", "fetch", "--prune", "--quiet"], cwd=repo_path)
    out = _run(["git", "branch", "-r", "--format=%(refname:short)"], cwd=repo_path)
    branches: list[str] = []
    for line in out.splitlines():
        name = line.strip()
        if not name or "->" in name or name == "origin":
            continue
        branches.append(name.removeprefix("origin/"))
    return sorted(set(branches))


#: Permissions that allow deleting a remote branch.
_PUSH_PERMISSIONS = frozenset({"ADMIN", "MAINTAIN", "WRITE"})


@dataclass(frozen=True)
class RepoIdentity:
    """What ``gh`` says about a checkout's origin."""

    slug: str = ""
    permission: str = ""
    is_fork: bool = False

    @property
    def resolved(self) -> bool:
        return bool(self.slug)


@lru_cache(maxsize=64)
def _repo_identity_cached(repo_path_str: str) -> RepoIdentity:
    """One ``gh repo view`` per checkout per process.

    An ``--all`` sweep asks the same question of each repo more than once —
    the selection filter, then the command itself — and a network round-trip
    per ask makes the sweep noticeably slow for an answer that cannot change
    mid-run.
    """
    return _fetch_repo_identity(pathlib.Path(repo_path_str))


def repo_identity(repo_path: pathlib.Path) -> RepoIdentity:
    """Return the origin identity for *repo_path*, cached per process."""
    return _repo_identity_cached(str(repo_path))


def _fetch_repo_identity(repo_path: pathlib.Path) -> RepoIdentity:
    """Ask ``gh`` for the origin identity.

    Resolved through ``gh`` rather than parsed from the remote URL, because
    the questions that matter — may *you* push to it, is it a fork — are not
    answerable from the URL.
    """
    out = _run(
        ["gh", "repo", "view", *origin_args(repo_path), "--json", "nameWithOwner,viewerPermission,isFork"],
        cwd=repo_path,
    )
    if not out.strip():
        return RepoIdentity()
    view = try_decode_json(_RepoView, out)
    if view is None:
        return RepoIdentity()
    return RepoIdentity(slug=view.name_with_owner, permission=view.viewer_permission, is_fork=view.is_fork)


@dataclass(slots=True, frozen=True)
class _RepoView:
    """The fields of ``gh repo view --json`` that decide whether a repo may be pruned."""

    name_with_owner: Annotated[str, Field(alias="nameWithOwner")]
    viewer_permission: Annotated[str, Field(alias="viewerPermission")]
    is_fork: Annotated[bool, Field(alias="isFork")]


@dataclass(slots=True, frozen=True)
class _PrHead:
    number: int
    head_ref_name: Annotated[str, Field(alias="headRefName")]


def can_prune(permission: str) -> bool:
    """Return True if *permission* allows deleting branches."""
    return permission.upper() in _PUSH_PERMISSIONS


def _pr_heads(repo_path: pathlib.Path, state: str) -> dict[str, int] | None:
    """Return ``{head_ref: pr_number}`` for PRs in *state*, or ``None`` when ``gh`` gave no readable list.

    Unreadable is not the same as empty: reading it as "no PRs" would let a branch with an
    open PR be classified as merged and deleted.
    """
    out = _run(
        [
            "gh",
            "pr",
            "list",
            "--state",
            state,
            "--limit",
            str(_PR_PAGE_LIMIT),
            "--json",
            "number,headRefName",
        ],
        cwd=repo_path,
    )
    if not out.strip():
        return None
    rows = try_decode_json(list[_PrHead], out)
    if rows is None:
        return None
    heads: dict[str, int] = {}
    for row in rows:
        # Keep the highest PR number per head: a branch can be reused, and the
        # most recent PR is the one that describes its current state.
        if row.head_ref_name and row.number > heads.get(row.head_ref_name, 0):
            heads[row.head_ref_name] = row.number
    return heads


def list_merged_pr_heads(repo_path: pathlib.Path) -> dict[str, int] | None:
    """Return ``{head_ref: pr_number}`` for merged PRs, or ``None`` when unreadable."""
    return _pr_heads(repo_path, "merged")


def list_open_pr_heads(repo_path: pathlib.Path) -> dict[str, int] | None:
    """Return ``{head_ref: pr_number}`` for open PRs, or ``None`` when unreadable."""
    return _pr_heads(repo_path, "open")


# --- Classification (pure) -------------------------------------------------


def classify_branches(
    branches: list[str],
    *,
    merged_heads: dict[str, int],
    open_heads: dict[str, int],
    rules: BranchCleanupRules,
) -> tuple[BranchVerdict, ...]:
    """Classify *branches*. Pure — all inputs are supplied.

    Order matters: protection is checked first, then an open PR, then a merged
    one. A branch with both a merged and an open PR has been reused, and the
    open PR is the live fact.
    """
    verdicts: list[BranchVerdict] = []
    for branch in branches:
        if rules.is_protected(branch):
            verdicts.append(BranchVerdict(branch, "protected"))
            continue
        if rules.protect_open_pr_heads and branch in open_heads:
            verdicts.append(BranchVerdict(branch, "open-pr", open_heads[branch]))
            continue
        if branch in merged_heads:
            verdicts.append(BranchVerdict(branch, "merged", merged_heads[branch]))
            continue
        verdicts.append(BranchVerdict(branch, "no-pr"))
    return tuple(verdicts)


# ---------------------------------------------------------------------------
# Plan + apply
# ---------------------------------------------------------------------------


def plan_cleanup(repo_path: pathlib.Path, *, fetch: bool = True) -> CleanupPlan:
    """Build the cleanup plan for *repo_path*. Reads only.

    Returns an empty, ``skipped`` plan when the repo should not be pruned:

    - **Not pushable.** A checkout of someone else's repo has *their* branches
      on its origin. A clone of an upstream you have not forked looks exactly
      like your own repo on disk, and its merged-PR list is full of branches a
      delete would target. Permission is the check that stops that.
    - **Cleanup disabled** for the repo in policy config.
    """
    identity = repo_identity(repo_path)

    if not can_prune(identity.permission):
        reason = f"no push access to {identity.slug}" if identity.resolved else "could not resolve origin"
        return CleanupPlan(repo=repo_path.name, verdicts=(), slug=identity.slug, skipped=reason)

    rules = load_policy_config().branch_cleanup(repo_path.name)
    if not rules.enabled:
        return CleanupPlan(repo=repo_path.name, verdicts=(), slug=identity.slug, skipped="cleanup disabled in policy")

    branches = list_remote_branches(repo_path, fetch=fetch)
    merged_heads = list_merged_pr_heads(repo_path)
    open_heads = list_open_pr_heads(repo_path)
    if merged_heads is None or open_heads is None:
        return CleanupPlan(
            repo=repo_path.name, verdicts=(), slug=identity.slug, skipped="could not read pull requests from gh"
        )
    verdicts = classify_branches(branches, merged_heads=merged_heads, open_heads=open_heads, rules=rules)
    return CleanupPlan(repo=repo_path.name, verdicts=verdicts, slug=identity.slug)


@dataclass(frozen=True, slots=True)
class BranchDeletion:
    """Outcome of deleting one remote branch."""

    branch: str
    status: str
    error: str = ""


def delete_remote_branch(repo_path: pathlib.Path, branch: str) -> BranchDeletion:
    """Delete ``origin/<branch>``. Returns the outcome rather than raising."""
    res = proc.git("push", "origin", "--delete", branch, cwd=repo_path, timeout=_TIMEOUT)
    if not res.ok:
        return BranchDeletion(branch=branch, status="error", error=res.err.strip())
    return BranchDeletion(branch=branch, status="deleted")
