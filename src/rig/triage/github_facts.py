"""Typed GraphQL metadata shared by bulk snapshots and selected-item observations."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Annotated

from pydantic import Field

from rig.triage.facts import Check, Facts, Related, Stack, StackMember


@dataclass(frozen=True, slots=True)
class Nodes[T]:
    nodes: list[T] = field(default_factory=list[T])
    total_count: Annotated[int, Field(alias="totalCount")] = 0


@dataclass(frozen=True, slots=True)
class Actor:
    login: str = ""


@dataclass(frozen=True, slots=True)
class Name:
    title: str = ""


@dataclass(frozen=True, slots=True)
class Reviewer:
    name: str = ""
    is_viewer: Annotated[bool, Field(alias="isViewer")] = False


@dataclass(frozen=True, slots=True)
class ReviewRequest:
    requested_reviewer: Annotated[Reviewer | None, Field(alias="requestedReviewer")] = None


@dataclass(frozen=True, slots=True)
class Thread:
    is_resolved: Annotated[bool, Field(alias="isResolved")] = False


@dataclass(frozen=True, slots=True)
class CheckNode:
    name: str = ""
    context: str = ""
    state: str = ""
    status: str = ""
    conclusion: str | None = None
    details_url: Annotated[str | None, Field(alias="detailsUrl")] = None
    target_url: Annotated[str | None, Field(alias="targetUrl")] = None


@dataclass(frozen=True, slots=True)
class Rollup:
    state: str = "UNKNOWN"
    contexts: Nodes[CheckNode] | None = None


@dataclass(frozen=True, slots=True)
class Commit:
    status_check_rollup: Annotated[Rollup | None, Field(alias="statusCheckRollup")] = None


@dataclass(frozen=True, slots=True)
class CommitNode:
    commit: Commit


@dataclass(frozen=True, slots=True)
class Repository:
    name_with_owner: Annotated[str, Field(alias="nameWithOwner")]


@dataclass(frozen=True, slots=True)
class RelatedNode:
    number: int
    title: str
    state: str
    repository: Repository
    typename: Annotated[str, Field(alias="__typename")] = "Issue"


@dataclass(frozen=True, slots=True)
class Member:
    number: int
    title: str
    state: str


@dataclass(frozen=True, slots=True)
class StackEntry:
    position: int = 0
    pull_request: Annotated[Member | None, Field(alias="pullRequest")] = None


@dataclass(frozen=True, slots=True)
class StackNode:
    number: int
    size: int
    base_ref_name: Annotated[str, Field(alias="baseRefName")]
    entries: Nodes[StackEntry] | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class Metadata:
    author: Actor | None = None
    viewer_did_author: Annotated[bool, Field(alias="viewerDidAuthor")] = False
    assignees: Nodes[Actor] = field(default_factory=Nodes[Actor])
    milestone: Name | None = None
    comments: Nodes[Actor] = field(default_factory=Nodes[Actor])
    review_decision: Annotated[str | None, Field(alias="reviewDecision")] = None
    review_requests: Annotated[Nodes[ReviewRequest], Field(alias="reviewRequests")] = field(
        default_factory=Nodes[ReviewRequest]
    )
    mergeable: str = "UNKNOWN"
    base_ref_name: Annotated[str, Field(alias="baseRefName")] = ""
    head_ref_name: Annotated[str, Field(alias="headRefName")] = ""
    is_cross_repository: Annotated[bool, Field(alias="isCrossRepository")] = False
    commits: Nodes[CommitNode] = field(default_factory=Nodes[CommitNode])
    review_threads: Annotated[Nodes[Thread], Field(alias="reviewThreads")] = field(default_factory=Nodes[Thread])
    closing_issues: Annotated[Nodes[RelatedNode], Field(alias="closingIssuesReferences")] = field(
        default_factory=Nodes[RelatedNode]
    )
    closing_prs: Annotated[Nodes[RelatedNode], Field(alias="closedByPullRequestsReferences")] = field(
        default_factory=Nodes[RelatedNode]
    )
    stack: StackNode | None = None
    stack_entry: Annotated[StackEntry | None, Field(alias="stackEntry")] = None
    state_reason: Annotated[str | None, Field(alias="stateReason")] = None

    def facts(self, labels: list[str], draft: bool, labels_total: int) -> Facts:
        """Keep partial lists explicit; aggregate check state is independent of sampled job details."""
        rollups = [node.commit.status_check_rollup for node in self.commits.nodes]
        rollup = next((value for value in reversed(rollups) if value is not None), None)
        contexts = rollup.contexts if rollup else None
        related = self.closing_prs if self.closing_prs.total_count else self.closing_issues
        partial = [
            name
            for name, total, size in (
                ("Assignees", self.assignees.total_count, len(self.assignees.nodes)),
                ("Labels", labels_total, len(labels)),
                ("Review requests", self.review_requests.total_count, len(self.review_requests.nodes)),
                ("Review threads", self.review_threads.total_count, len(self.review_threads.nodes)),
                ("Linked items", related.total_count, len(related.nodes)),
            )
            if total > size
        ]
        if contexts and contexts.total_count > len(contexts.nodes):
            partial.append("Checks")
        stack = self.stack
        members = stack.entries.nodes if stack and stack.entries else []
        if stack and len(members) < stack.size:
            partial.append("Stack members")
        return Facts(
            author=self.author.login if self.author else "",
            viewer_authored=self.viewer_did_author,
            assignees=[actor.login for actor in self.assignees.nodes],
            labels=labels,
            milestone=self.milestone.title if self.milestone else "",
            comments=self.comments.total_count,
            review_decision=self.review_decision or "",
            review_requested=any(
                request.requested_reviewer and request.requested_reviewer.is_viewer
                for request in self.review_requests.nodes
            ),
            reviewers=[
                request.requested_reviewer.name
                for request in self.review_requests.nodes
                if request.requested_reviewer is not None
            ],
            draft=draft,
            mergeable=self.mergeable,
            base=self.base_ref_name,
            head=self.head_ref_name,
            cross_repository=self.is_cross_repository,
            checks_state=rollup.state if rollup else "UNKNOWN",
            checks=[
                Check(
                    check.name or check.context,
                    check.state or check.conclusion or check.status,
                    check.details_url or check.target_url or "",
                )
                for check in contexts.nodes
            ]
            if contexts
            else [],
            checks_total=contexts.total_count if contexts else None,
            unresolved_threads=sum(not thread.is_resolved for thread in self.review_threads.nodes),
            threads_total=self.review_threads.total_count,
            state_reason=self.state_reason or "",
            related=[
                Related(
                    node.repository.name_with_owner,
                    node.number,
                    node.title,
                    node.state,
                    "pr" if node.typename == "PullRequest" else "issue",
                )
                for node in related.nodes
            ],
            stack=Stack(
                stack.number,
                stack.size,
                self.stack_entry.position if self.stack_entry else 0,
                stack.base_ref_name,
                [
                    StackMember(
                        member.pull_request.number,
                        member.pull_request.title,
                        member.pull_request.state,
                        member.position,
                    )
                    for member in members
                    if member.pull_request
                ],
            )
            if stack
            else None,
            partial=partial,
        )


COMMON_METADATA = (
    "author { login } viewerDidAuthor assignees(first:20) { totalCount nodes { login } } "
    "milestone { title } comments { totalCount }"
)
RELATED_FIELDS = "__typename number title state repository { nameWithOwner }"


def pr_metadata(*, detailed: bool) -> str:
    """Bulk refresh reads rollups; only the selected item loads individual jobs and full stack entries."""
    contexts = (
        "contexts(first:100) { totalCount nodes { ... on CheckRun { name status conclusion detailsUrl } "
        "... on StatusContext { context state targetUrl } } }"
        if detailed
        else ""
    )
    members = (
        "entries(first:100) { totalCount nodes { position pullRequest { number title state } } }" if detailed else ""
    )
    return (
        "reviewDecision mergeable headRefName baseRefName isCrossRepository "
        "reviewRequests(first:20) { totalCount nodes { requestedReviewer { ... on User { name:login isViewer } "
        "... on Team { name:slug } ... on Bot { name:login } } } } "
        "reviewThreads(first:100) { totalCount nodes { isResolved } } "
        f"commits(last:1) {{ nodes {{ commit {{ statusCheckRollup {{ state {contexts} }} }} }} }} "
        f"closingIssuesReferences(first:25) {{ totalCount nodes {{ {RELATED_FIELDS} }} }} "
        f"stack {{ number size baseRefName {members} }} stackEntry {{ position }}"
    )


ISSUE_METADATA = (
    "stateReason closedByPullRequestsReferences(first:25, includeClosedPrs:true) "
    f"{{ totalCount nodes {{ {RELATED_FIELDS} }} }}"
)
