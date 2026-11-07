"""Cross-Repo Pull Request Triage and Automation Engine.

Evaluates open pull requests across repositories against required CI checks and merge state:
- READY: Mergeable, mergeStateStatus is CLEAN, all required checks passing.
- BEHIND: Mergeable, mergeStateStatus is BEHIND (or clean but behind main branch tip).
- BLOCKED: Failing checks, merge conflicts (DIRTY), draft, or blocked by ruleset.
- PENDING: CI jobs currently queued or in-flight.
- STALE: No activity / updates in > 14 days.

Automated Batch Actions:
- --update-behind: Calls `gh pr update-branch` on PRs in the BEHIND bucket.
- --merge-ready: Squash-merges PRs in the READY bucket and deletes feature branch.
"""

from __future__ import annotations

import datetime
from dataclasses import dataclass
from enum import StrEnum
from typing import Annotated

from pydantic import BaseModel, Field

from rig import proc
from rig.repo_governance.github_rulesets import get_required_checks_for_repo
from rig.repo_governance.review_threads import inspect_review_threads


class PRBucket(StrEnum):
    READY = "ready"
    BEHIND = "behind"
    BLOCKED = "blocked"
    PENDING = "pending"
    STALE = "stale"
    OTHER = "other"


class PRItem(BaseModel):
    repo: str
    number: int
    title: str
    url: str
    head_branch: str
    base_branch: str = "main"
    author: str = ""
    is_draft: bool = False
    mergeable: str = ""  # MERGEABLE, CONFLICTING, UNKNOWN
    merge_state_status: str = ""  # CLEAN, BEHIND, BLOCKED, DIRTY, DRAFT, UNSTABLE, HAS_HOOKS
    updated_at: datetime.datetime | None = None
    passed_checks: list[str] = Field(default_factory=list[str])
    failed_checks: list[str] = Field(default_factory=list[str])
    pending_checks: list[str] = Field(default_factory=list[str])
    missing_required_checks: list[str] = Field(default_factory=list[str])

    @property
    def short_ref(self) -> str:
        return f"{self.repo}#{self.number}"


class PRTriageDashboard(BaseModel):
    total_prs: int = 0
    ready: list[PRItem] = Field(default_factory=list[PRItem])
    behind: list[PRItem] = Field(default_factory=list[PRItem])
    blocked: list[PRItem] = Field(default_factory=list[PRItem])
    pending: list[PRItem] = Field(default_factory=list[PRItem])
    stale: list[PRItem] = Field(default_factory=list[PRItem])


def parse_datetime(val: str | None) -> datetime.datetime | None:
    if not val:
        return None
    try:
        if val.endswith("Z"):
            val = val[:-1] + "+00:00"
        return datetime.datetime.fromisoformat(val)
    except ValueError:
        return None


def categorize_pr(
    pr: PRItem,
    *,
    now: datetime.datetime | None = None,
    stale_days: int = 14,
) -> PRBucket:
    """Categorizes a pull request into its operational triage bucket."""
    current_time = now or datetime.datetime.now(datetime.UTC)

    # 1. Draft is blocked
    if pr.is_draft:
        return PRBucket.BLOCKED

    # 2. Failing required checks or conflicts
    if pr.failed_checks or pr.missing_required_checks or pr.mergeable == "CONFLICTING":
        return PRBucket.BLOCKED
    if pr.merge_state_status in ("DIRTY", "BLOCKED"):
        return PRBucket.BLOCKED

    # 3. Pending required checks
    if pr.pending_checks:
        return PRBucket.PENDING

    # 4. Behind main
    if pr.merge_state_status == "BEHIND":
        return PRBucket.BEHIND

    # 5. Ready to merge: mergeable and CLEAN
    if pr.merge_state_status == "CLEAN" and not pr.pending_checks and not pr.failed_checks:
        return PRBucket.READY

    # 6. Stale check if not ready/pending
    if pr.updated_at:
        delta = current_time - pr.updated_at
        if delta.days >= stale_days:
            return PRBucket.STALE

    return PRBucket.OTHER


@dataclass(slots=True, frozen=True)
class _GhLogin:
    login: str = ""


@dataclass(slots=True, frozen=True)
class _GhCheck:
    """A ``statusCheckRollup`` entry.

    A check run carries ``name``/``status``/``conclusion``; a status context carries ``context``/``state``.
    """

    name: str | None = None
    context: str | None = None
    status: str | None = None
    conclusion: str | None = None
    state: str | None = None


@dataclass(slots=True, frozen=True)
class _GhPr:
    number: int
    title: str
    url: str
    head_ref_name: Annotated[str, Field(alias="headRefName")]
    base_ref_name: Annotated[str, Field(alias="baseRefName")]
    author: _GhLogin | None
    is_draft: Annotated[bool, Field(alias="isDraft")]
    mergeable: str
    merge_state_status: Annotated[str, Field(alias="mergeStateStatus")]
    updated_at: Annotated[str | None, Field(alias="updatedAt")]
    status_check_rollup: Annotated[list[_GhCheck], Field(alias="statusCheckRollup")]


@dataclass(slots=True, frozen=True)
class PRActionResult:
    ok: bool
    message: str


def fetch_repo_prs(repo_name: str) -> list[PRItem]:
    """Fetches open PRs for a given repository via the GitHub CLI and evaluates check states."""
    cmd = [
        "gh",
        "pr",
        "list",
        "--repo",
        repo_name,
        "--state",
        "open",
        "--limit",
        "50",
        "--json",
        "number,title,url,headRefName,baseRefName,author,isDraft,mergeable,mergeStateStatus,updatedAt,statusCheckRollup",
    ]
    try:
        rows = proc.gh_json(list[_GhPr], *cmd[1:])
        if rows is None:
            return []

        required_checks = set(get_required_checks_for_repo(repo_name))
        prs: list[PRItem] = []
        for row in rows:
            passed_checks: list[str] = []
            failed_checks: list[str] = []
            pending_checks: list[str] = []
            reported_checks: set[str] = set()

            for chk in row.status_check_rollup:
                name = chk.name or chk.context
                if not name:
                    continue
                reported_checks.add(name)

                if chk.conclusion in ("SUCCESS", "NEUTRAL") or chk.state == "SUCCESS":
                    passed_checks.append(name)
                elif chk.conclusion in ("FAILURE", "TIMED_OUT", "ACTION_REQUIRED") or chk.state in ("FAILURE", "ERROR"):
                    failed_checks.append(name)
                elif chk.status in ("IN_PROGRESS", "QUEUED", "WAITING", "PENDING") or chk.state == "PENDING":
                    pending_checks.append(name)

            missing_reqs = [req for req in required_checks if req not in reported_checks]

            prs.append(
                PRItem(
                    repo=repo_name,
                    number=row.number,
                    title=row.title,
                    url=row.url,
                    head_branch=row.head_ref_name,
                    base_branch=row.base_ref_name,
                    author=row.author.login if row.author else "",
                    is_draft=row.is_draft,
                    mergeable=row.mergeable,
                    merge_state_status=row.merge_state_status,
                    updated_at=parse_datetime(row.updated_at),
                    passed_checks=passed_checks,
                    failed_checks=failed_checks,
                    pending_checks=pending_checks,
                    missing_required_checks=missing_reqs,
                )
            )
        return prs
    except ValueError:
        return []


def build_pr_triage_dashboard(
    prs: list[PRItem],
    *,
    now: datetime.datetime | None = None,
    stale_days: int = 14,
) -> PRTriageDashboard:
    """Groups PR items into their respective triage dashboard buckets."""
    dashboard = PRTriageDashboard(total_prs=len(prs))
    for pr in prs:
        bucket = categorize_pr(pr, now=now, stale_days=stale_days)
        if bucket == PRBucket.READY:
            dashboard.ready.append(pr)
        elif bucket == PRBucket.BEHIND:
            dashboard.behind.append(pr)
        elif bucket == PRBucket.BLOCKED:
            dashboard.blocked.append(pr)
        elif bucket == PRBucket.PENDING:
            dashboard.pending.append(pr)
        elif bucket == PRBucket.STALE:
            dashboard.stale.append(pr)
    return dashboard


def update_behind_pr(pr: PRItem) -> PRActionResult:
    """Updates a PR branch with latest main via GitHub CLI."""
    res = proc.gh("pr", "update-branch", str(pr.number), "--repo", pr.repo)
    if res.ok:
        return PRActionResult(ok=True, message=f"Successfully updated branch for {pr.short_ref}")
    return PRActionResult(ok=False, message=f"Failed to update branch for {pr.short_ref}: {res.err.strip()}")


def merge_ready_pr(pr: PRItem) -> PRActionResult:
    """Squash-merge after checking conversations, including on repos without native protection."""
    threads = inspect_review_threads(pr.repo, pr.number)
    if not threads.is_aligned:
        return PRActionResult(ok=False, message=f"Blocked {pr.short_ref}: {threads.message}")
    res = proc.gh("pr", "merge", str(pr.number), "--repo", pr.repo, "--squash", "--delete-branch")
    if res.ok:
        return PRActionResult(ok=True, message=f"Successfully merged {pr.short_ref} and deleted branch")
    return PRActionResult(ok=False, message=f"Failed to merge {pr.short_ref}: {res.err.strip()}")
