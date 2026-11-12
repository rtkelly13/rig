"""What `rig gh pr wait` reads from GitHub: the PR's landing state, the base branch's rules, its threads.

Three reads, all through ports so tests never reach GitHub:

- the landing state is one GraphQL query through `rig.gh_threads.github.GraphQL`;
- the active rules of the base branch come from REST (``repos/o/r/rules/branches/<base>``) through
  `rig.proc.GitHubApi`. GraphQL's ``branchProtectionRule`` only sees classic branch protection, and
  rulesets are where the estate sets ``required_review_thread_resolution``;
- the unresolved threads are `rig.gh_threads.github.fetch_threads`, the same read `rig gh threads list` does.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Annotated
from urllib.parse import quote

from pydantic import Field

from rig.gh_pr.model import (
    FAILED_RESULTS,
    PENDING_STATES,
    Check,
    CheckState,
    Observation,
    QueueEntry,
    Rules,
    Snapshot,
    ThreadRef,
)
from rig.gh_threads.github import GitHubError, GraphQL, fetch_threads
from rig.proc import ApiRequest, GitHubApi
from rig.repo_slug import RepoSlug
from rig.typed_json import try_decode_json

#: Stop paging checks after this many pages, so a cursor GitHub keeps repeating cannot loop forever.
MAX_CHECK_PAGES = 20

LANDING_QUERY = """
query PullRequestLanding($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      state
      isDraft
      mergeable
      mergeStateStatus
      reviewDecision
      url
      title
      baseRefName
      autoMergeRequest { enabledAt }
      mergeQueueEntry { position state }
      baseRef { branchProtectionRule { requiresConversationResolution requiredStatusCheckContexts } }
      commits(last: 1) {
        nodes {
          commit {
            statusCheckRollup {
              contexts(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on CheckRun { name status conclusion isRequired(pullRequestNumber: $number) }
                  ... on StatusContext { context state isRequired(pullRequestNumber: $number) }
                }
              }
            }
          }
        }
      }
    }
  }
}
"""


# -- wire shapes -----------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _Error:
    message: str = ""


@dataclass(frozen=True, slots=True)
class _PageInfo:
    has_next_page: Annotated[bool, Field(alias="hasNextPage")] = False
    end_cursor: Annotated[str | None, Field(alias="endCursor")] = None


@dataclass(frozen=True, slots=True)
class _Context:
    """A check run (``name``, ``status``, ``conclusion``) or a commit status (``context``, ``state``)."""

    typename: Annotated[str, Field(alias="__typename")] = ""
    name: str = ""
    status: str = ""
    conclusion: str | None = None
    context: str = ""
    state: str = ""
    is_required: Annotated[bool, Field(alias="isRequired")] = False


@dataclass(frozen=True, slots=True)
class _Contexts:
    nodes: list[_Context] = field(default_factory=list[_Context])
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")] = field(default_factory=_PageInfo)


@dataclass(frozen=True, slots=True)
class _Rollup:
    contexts: _Contexts = field(default_factory=_Contexts)


@dataclass(frozen=True, slots=True)
class _Commit:
    rollup: Annotated[_Rollup | None, Field(alias="statusCheckRollup")] = None


@dataclass(frozen=True, slots=True)
class _CommitNode:
    commit: _Commit = field(default_factory=_Commit)


@dataclass(frozen=True, slots=True)
class _Commits:
    nodes: list[_CommitNode] = field(default_factory=list[_CommitNode])


@dataclass(frozen=True, slots=True)
class _AutoMerge:
    enabled_at: Annotated[str | None, Field(alias="enabledAt")] = None


@dataclass(frozen=True, slots=True)
class _QueueEntry:
    position: int = 0
    state: str = ""


@dataclass(frozen=True, slots=True)
class _Protection:
    conversation_resolution: Annotated[bool, Field(alias="requiresConversationResolution")] = False
    contexts: Annotated[list[str] | None, Field(alias="requiredStatusCheckContexts")] = None


@dataclass(frozen=True, slots=True)
class _BaseRef:
    protection: Annotated[_Protection | None, Field(alias="branchProtectionRule")] = None


@dataclass(frozen=True, slots=True)
class _PullRequest:
    state: str
    merge_state: Annotated[str, Field(alias="mergeStateStatus")]
    base: Annotated[str, Field(alias="baseRefName")]
    draft: Annotated[bool, Field(alias="isDraft")] = False
    mergeable: str = "UNKNOWN"
    review_decision: Annotated[str | None, Field(alias="reviewDecision")] = None
    url: str = ""
    title: str = ""
    auto_merge: Annotated[_AutoMerge | None, Field(alias="autoMergeRequest")] = None
    queue: Annotated[_QueueEntry | None, Field(alias="mergeQueueEntry")] = None
    base_ref: Annotated[_BaseRef | None, Field(alias="baseRef")] = None
    commits: _Commits = field(default_factory=_Commits)


@dataclass(frozen=True, slots=True)
class _Repository:
    pull_request: Annotated[_PullRequest | None, Field(alias="pullRequest")] = None


@dataclass(frozen=True, slots=True)
class _Data:
    repository: _Repository | None = None


@dataclass(frozen=True, slots=True)
class _Response:
    data: _Data | None = None
    errors: list[_Error] = field(default_factory=list[_Error])


@dataclass(frozen=True, slots=True)
class _RequiredCheck:
    context: str = ""


@dataclass(frozen=True, slots=True)
class _RuleParameters:
    thread_resolution: Annotated[bool, Field(alias="required_review_thread_resolution")] = False
    required_status_checks: list[_RequiredCheck] = field(default_factory=list[_RequiredCheck])


@dataclass(frozen=True, slots=True)
class _Rule:
    type: str = ""
    parameters: _RuleParameters | None = None


# -- reads -----------------------------------------------------------------------


def _landing_page(graphql: GraphQL, repo: RepoSlug, number: int, cursor: str | None) -> _PullRequest:
    what = f"landing state of {repo}#{number}"
    variables: dict[str, str | int] = {"owner": repo.owner, "name": repo.name, "number": number}
    if cursor is not None:
        variables["cursor"] = cursor
    ran = graphql(LANDING_QUERY, variables)
    response = try_decode_json(_Response, ran.out) if ran.out.strip() else None
    messages = [error.message for error in response.errors if error.message] if response else []
    if messages:
        raise GitHubError(f"{what}: {'; '.join(messages)}")
    if not ran.ok:
        raise GitHubError(f"{what}: {ran.err.strip() or f'gh exited {ran.code}'}")
    if response is None or response.data is None:
        raise GitHubError(f"{what}: unreadable response from GitHub")
    if response.data.repository is None or response.data.repository.pull_request is None:
        raise GitHubError(f"{repo}#{number}: no such pull request (or no access to it)")
    return response.data.repository.pull_request


def _contexts(pr: _PullRequest) -> _Contexts:
    rollup = pr.commits.nodes[0].commit.rollup if pr.commits.nodes else None
    return rollup.contexts if rollup is not None else _Contexts()


def _check(node: _Context) -> Check:
    if node.typename == "StatusContext":
        result = node.state
        pending = result in PENDING_STATES
        name = node.context
    else:
        result = node.conclusion or node.status
        pending = node.status != "COMPLETED"
        name = node.name
    finished = CheckState.FAILED if result in FAILED_RESULTS else CheckState.PASSED
    return Check(name=name, state=CheckState.PENDING if pending else finished, required=node.is_required, result=result)


def fetch_snapshot(graphql: GraphQL, repo: RepoSlug, number: int) -> Snapshot:
    """The PR's state, merge state, auto-merge, queue entry and every check on its head commit."""
    pr = _landing_page(graphql, repo, number, None)
    contexts = _contexts(pr)
    nodes = list(contexts.nodes)
    seen: set[str] = set()
    while contexts.page_info.has_next_page:
        cursor = contexts.page_info.end_cursor
        if not cursor or cursor in seen or len(seen) >= MAX_CHECK_PAGES:
            raise GitHubError(f"checks of {repo}#{number}: GitHub repeated a page cursor; the list is incomplete")
        seen.add(cursor)
        contexts = _contexts(_landing_page(graphql, repo, number, cursor))
        nodes.extend(contexts.nodes)
    protection = pr.base_ref.protection if pr.base_ref is not None else None
    return Snapshot(
        state=pr.state,
        draft=pr.draft,
        mergeable=pr.mergeable,
        merge_state=pr.merge_state,
        review_decision=pr.review_decision or "",
        auto_merge=pr.auto_merge is not None,
        queue=QueueEntry(pr.queue.position, pr.queue.state) if pr.queue is not None else None,
        checks=tuple(_check(node) for node in nodes),
        base=pr.base,
        url=pr.url,
        title=pr.title,
        classic_thread_resolution=protection is not None and protection.conversation_resolution,
        classic_required_checks=tuple(protection.contexts or ()) if protection is not None else (),
    )


def fetch_rules(api: GitHubApi, repo: RepoSlug, base: str) -> Rules:
    """The active ruleset rules on *base*. Never raises: unreadable rules come back with ``thread_resolution`` None."""
    ran = api(ApiRequest("GET", f"repos/{repo}/rules/branches/{quote(base, safe='/')}?per_page=100"))
    rules = try_decode_json(list[_Rule], ran.out) if ran.ok else None
    if rules is None:
        return Rules(thread_resolution=None)
    parameters = [rule.parameters for rule in rules if rule.parameters is not None]
    return Rules(
        thread_resolution=any(found.thread_resolution for found in parameters),
        required_checks=tuple(check.context for found in parameters for check in found.required_status_checks),
    )


def observe(graphql: GraphQL, api: GitHubApi, repo: RepoSlug, number: int, known: Rules | None) -> Observation:
    """One poll: the snapshot and, while the PR is open and threads could block it, its unresolved threads.

    *known* is the rules an earlier poll read. They are read again only while they are missing or were
    unreadable, and come back on the observation for the caller to pass in next time.
    """
    snapshot = fetch_snapshot(graphql, repo, number)
    rules = known
    if rules is None or rules.thread_resolution is None:
        rules = fetch_rules(api, repo, snapshot.base)
    threads: tuple[ThreadRef, ...] = ()
    required = True if snapshot.classic_thread_resolution else rules.thread_resolution
    if snapshot.state == "OPEN" and required is not False:
        threads = tuple(
            ThreadRef(
                id=item.thread.id,
                location=item.thread.location,
                author=item.thread.author,
                summary=item.thread.summary,
                url=item.thread.url,
            )
            for item in fetch_threads(graphql, repo, number)
            if not item.thread.resolved
        )
    return Observation(snapshot=snapshot, rules=rules, threads=threads)
