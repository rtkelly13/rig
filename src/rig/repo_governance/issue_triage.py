"""Cross-Repo Issue Triage Dashboard Engine.

Scans and buckets open GitHub issues across repositories into actionable categories:
- Needs Triage (0 comments and no type:* label)
- Active Bugs (issues labeled type: bug)
- Community / Good First Issue (issues labeled good first issue or help wanted)
- Stale Waiting on Repro (issues labeled status: needs-repro with no activity > 14 days)
"""

from __future__ import annotations

import datetime
from dataclasses import dataclass
from enum import StrEnum
from typing import Annotated

from pydantic import BaseModel, Field

from rig import proc


class TriageBucket(StrEnum):
    NEEDS_TRIAGE = "needs_triage"
    ACTIVE_BUGS = "active_bugs"
    COMMUNITY = "community"
    STALE_REPRO = "stale_repro"
    OTHER = "other"


class IssueItem(BaseModel):
    repo: str
    number: int
    title: str
    url: str
    author: str = ""
    created_at: datetime.datetime | None = None
    updated_at: datetime.datetime | None = None
    comments_count: int = 0
    labels: list[str] = Field(default_factory=list[str])

    @property
    def short_ref(self) -> str:
        return f"{self.repo}#{self.number}"


class TriageDashboard(BaseModel):
    total_issues: int = 0
    needs_triage: list[IssueItem] = Field(default_factory=list[IssueItem])
    active_bugs: list[IssueItem] = Field(default_factory=list[IssueItem])
    community: list[IssueItem] = Field(default_factory=list[IssueItem])
    stale_repro: list[IssueItem] = Field(default_factory=list[IssueItem])
    other: list[IssueItem] = Field(default_factory=list[IssueItem])


def parse_datetime(val: str | None) -> datetime.datetime | None:
    if not val:
        return None
    try:
        if val.endswith("Z"):
            val = val[:-1] + "+00:00"
        return datetime.datetime.fromisoformat(val)
    except ValueError:
        return None


def categorize_issue(
    issue: IssueItem,
    *,
    now: datetime.datetime | None = None,
    stale_days: int = 14,
) -> TriageBucket:
    """Categorizes an issue into one of the four triage buckets (or other)."""
    current_time = now or datetime.datetime.now(datetime.UTC)
    label_names = {lbl.lower() for lbl in issue.labels}

    # 1. Stale Waiting on Repro: labeled 'status: needs-repro' and no update for > stale_days
    if ("status: needs-repro" in label_names or "needs-repro" in label_names) and issue.updated_at:
        delta = current_time - issue.updated_at
        if delta.days >= stale_days:
            return TriageBucket.STALE_REPRO

    # 2. Active Bugs: labeled 'type: bug' or 'bug'
    if "type: bug" in label_names or "bug" in label_names:
        return TriageBucket.ACTIVE_BUGS

    # 3. Community: labeled 'good first issue' or 'help wanted'
    if "good first issue" in label_names or "help wanted" in label_names:
        return TriageBucket.COMMUNITY

    # 4. Needs Triage: 0 comments and no 'type:*' label
    has_type_label = any(lbl.startswith("type:") or lbl.startswith("type/") for lbl in label_names)
    if issue.comments_count == 0 and not has_type_label:
        return TriageBucket.NEEDS_TRIAGE

    return TriageBucket.OTHER


@dataclass(slots=True, frozen=True)
class _GhLogin:
    login: str = ""


@dataclass(slots=True, frozen=True)
class _GhLabel:
    name: str


@dataclass(slots=True, frozen=True)
class _GhIssue:
    """One row of ``gh issue list --json number,title,url,author,createdAt,updatedAt,comments,labels``."""

    number: int
    title: str
    url: str
    author: _GhLogin | None
    created_at: Annotated[str | None, Field(alias="createdAt")]
    updated_at: Annotated[str | None, Field(alias="updatedAt")]
    comments: list[object]
    labels: list[_GhLabel]


def fetch_repo_issues(repo_name: str, label: str | None = None) -> list[IssueItem]:
    """Fetches open issues for a given repository via the GitHub CLI."""
    cmd = [
        "gh",
        "issue",
        "list",
        "--repo",
        repo_name,
        "--state",
        "open",
        "--limit",
        "100",
        "--json",
        "number,title,url,author,createdAt,updatedAt,comments,labels",
    ]
    if label:
        cmd.extend(["--label", label])

    try:
        rows = proc.gh_json(list[_GhIssue], *cmd[1:])
        if rows is None:
            return []
        return [
            IssueItem(
                repo=repo_name,
                number=row.number,
                title=row.title,
                url=row.url,
                author=row.author.login if row.author else "",
                created_at=parse_datetime(row.created_at),
                updated_at=parse_datetime(row.updated_at),
                comments_count=len(row.comments),
                labels=[lbl.name for lbl in row.labels],
            )
            for row in rows
        ]
    except ValueError:
        return []


def build_triage_dashboard(
    issues: list[IssueItem],
    *,
    now: datetime.datetime | None = None,
    stale_days: int = 14,
) -> TriageDashboard:
    """Groups issues into the TriageDashboard buckets."""
    dashboard = TriageDashboard(total_issues=len(issues))
    for issue in issues:
        bucket = categorize_issue(issue, now=now, stale_days=stale_days)
        if bucket == TriageBucket.NEEDS_TRIAGE:
            dashboard.needs_triage.append(issue)
        elif bucket == TriageBucket.ACTIVE_BUGS:
            dashboard.active_bugs.append(issue)
        elif bucket == TriageBucket.COMMUNITY:
            dashboard.community.append(issue)
        elif bucket == TriageBucket.STALE_REPRO:
            dashboard.stale_repro.append(issue)
        else:
            dashboard.other.append(issue)
    return dashboard
