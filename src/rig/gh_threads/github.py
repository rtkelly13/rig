"""The GraphQL calls behind `rig gh threads`, through one narrow port.

`GraphQL` runs one query or mutation with its variables. The real adapter, `gh_graphql`, passes them
to ``gh api graphql`` as ``-f``/``-F`` fields through `rig.proc.gh`; tests pass the stateful fake in
``tests/fakes/gh_graphql.py``. Variables travel as fields, never spliced into the query text, so a
reply note can hold any characters.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Annotated, Protocol

from pydantic import Field

from rig import proc
from rig.gh_threads.model import ObservedThread, PullRequest, Thread, summarise
from rig.repo_slug import RepoSlug
from rig.typed_json import try_decode_json

type Variables = Mapping[str, str | int]

#: Stop paginating after this many pages, so a cursor GitHub keeps repeating cannot loop forever.
MAX_PAGES = 50


class GraphQL(Protocol):
    """Runs one GraphQL document with *variables*; strings go as ``-f``, integers as ``-F``."""

    def __call__(self, query: str, variables: Variables, /) -> proc.Ran: ...


def gh_graphql(query: str, variables: Variables, /) -> proc.Ran:
    """The real `GraphQL`: ``gh api graphql`` with each variable as a typed field."""
    args = ["api", "graphql", "-f", f"query={query}"]
    for key, value in variables.items():
        args += ["-F", f"{key}={value}"] if isinstance(value, int) else ["-f", f"{key}={value}"]
    return proc.gh(*args)


class GitHubError(Exception):
    """A GraphQL call failed or answered with something this module cannot read."""


THREADS_QUERY = """
query ReviewThreads($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviewThreads(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          id
          isResolved
          isOutdated
          path
          line
          originalLine
          first: comments(first: 1) { nodes { author { login } body url } }
          latest: comments(last: 1) { nodes { body } }
        }
      }
    }
  }
}
"""

COMMITS_QUERY = """
query PullRequestCommits($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      headRefName
      headRefOid
      commits(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes { commit { oid } }
      }
    }
  }
}
"""

REPLY_MUTATION = """
mutation ReplyToThread($thread: ID!, $body: String!) {
  addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $thread, body: $body}) {
    comment { url }
  }
}
"""

RESOLVE_MUTATION = """
mutation ResolveThread($thread: ID!) {
  resolveReviewThread(input: {threadId: $thread}) {
    thread { id isResolved }
  }
}
"""


# -- wire shapes -----------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _Error:
    message: str = ""


@dataclass(frozen=True, slots=True)
class _PageInfo:
    has_next_page: Annotated[bool, Field(alias="hasNextPage")]
    end_cursor: Annotated[str | None, Field(alias="endCursor")] = None


@dataclass(frozen=True, slots=True)
class _Author:
    login: str = ""


@dataclass(frozen=True, slots=True)
class _Comment:
    author: _Author | None = None
    body: str = ""
    url: str = ""


@dataclass(frozen=True, slots=True)
class _Comments:
    nodes: list[_Comment] = field(default_factory=list[_Comment])


@dataclass(frozen=True, slots=True)
class _ThreadNode:
    id: str
    is_resolved: Annotated[bool, Field(alias="isResolved")]
    is_outdated: Annotated[bool, Field(alias="isOutdated")]
    path: str
    line: int | None = None
    original_line: Annotated[int | None, Field(alias="originalLine")] = None
    first: _Comments = field(default_factory=_Comments)
    latest: _Comments = field(default_factory=_Comments)


@dataclass(frozen=True, slots=True)
class _ThreadPage:
    nodes: list[_ThreadNode]
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")]


@dataclass(frozen=True, slots=True)
class _CommitRef:
    oid: str


@dataclass(frozen=True, slots=True)
class _CommitNode:
    commit: _CommitRef


@dataclass(frozen=True, slots=True)
class _CommitPage:
    nodes: list[_CommitNode]
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")]


@dataclass(frozen=True, slots=True)
class _PullRequest:
    review_threads: Annotated[_ThreadPage | None, Field(alias="reviewThreads")] = None
    head_ref_name: Annotated[str, Field(alias="headRefName")] = ""
    head_ref_oid: Annotated[str, Field(alias="headRefOid")] = ""
    commits: _CommitPage | None = None


@dataclass(frozen=True, slots=True)
class _Repository:
    pull_request: Annotated[_PullRequest | None, Field(alias="pullRequest")] = None


@dataclass(frozen=True, slots=True)
class _ReplyComment:
    url: str = ""


@dataclass(frozen=True, slots=True)
class _ReplyPayload:
    comment: _ReplyComment | None = None


@dataclass(frozen=True, slots=True)
class _ResolvedThread:
    is_resolved: Annotated[bool, Field(alias="isResolved")]


@dataclass(frozen=True, slots=True)
class _ResolvePayload:
    thread: _ResolvedThread | None = None


@dataclass(frozen=True, slots=True)
class _Data:
    repository: _Repository | None = None
    add_reply: Annotated[_ReplyPayload | None, Field(alias="addPullRequestReviewThreadReply")] = None
    resolve: Annotated[_ResolvePayload | None, Field(alias="resolveReviewThread")] = None


@dataclass(frozen=True, slots=True)
class _Response:
    data: _Data | None = None
    errors: list[_Error] = field(default_factory=list[_Error])


# -- calls -----------------------------------------------------------------------


def _call(graphql: GraphQL, what: str, query: str, variables: Variables) -> _Data:
    ran = graphql(query, variables)
    response = try_decode_json(_Response, ran.out) if ran.out.strip() else None
    messages = [error.message for error in response.errors if error.message] if response else []
    if messages:
        raise GitHubError(f"{what}: {'; '.join(messages)}")
    if not ran.ok:
        raise GitHubError(f"{what}: {ran.err.strip() or f'gh exited {ran.code}'}")
    if response is None or response.data is None:
        raise GitHubError(f"{what}: unreadable response from GitHub")
    return response.data


def _pull_request(data: _Data, repo: RepoSlug, number: int) -> _PullRequest:
    if data.repository is None or data.repository.pull_request is None:
        raise GitHubError(f"{repo}#{number}: no such pull request (or no access to it)")
    return data.repository.pull_request


def _pages[T](fetch_page: _PageFetcher[T], what: str) -> list[T]:
    cursor: str | None = None
    seen: set[str] = set()
    items: list[T] = []
    for _ in range(MAX_PAGES):
        page = fetch_page(cursor)
        items.extend(page.items)
        if not page.info.has_next_page:
            return items
        cursor = page.info.end_cursor
        if not cursor or cursor in seen:
            raise GitHubError(f"{what}: GitHub repeated or dropped a page cursor; the list is incomplete")
        seen.add(cursor)
    raise GitHubError(f"{what}: more than {MAX_PAGES} pages; the list is incomplete")


@dataclass(frozen=True, slots=True)
class _Page[T]:
    items: list[T]
    info: _PageInfo


class _PageFetcher[T](Protocol):
    def __call__(self, cursor: str | None, /) -> _Page[T]: ...


def _variables(repo: RepoSlug, number: int, cursor: str | None) -> dict[str, str | int]:
    variables: dict[str, str | int] = {"owner": repo.owner, "name": repo.name, "number": number}
    if cursor is not None:
        variables["cursor"] = cursor
    return variables


def fetch_threads(graphql: GraphQL, repo: RepoSlug, number: int) -> list[ObservedThread]:
    """Every review thread on the PR, resolved or not, across all pages."""
    what = f"review threads of {repo}#{number}"

    def page(cursor: str | None) -> _Page[_ThreadNode]:
        threads = _pull_request(
            _call(graphql, what, THREADS_QUERY, _variables(repo, number, cursor)), repo, number
        ).review_threads
        if threads is None:
            raise GitHubError(f"{what}: response has no reviewThreads")
        return _Page(threads.nodes, threads.page_info)

    return [_observed(node) for node in _pages(page, what)]


def _observed(node: _ThreadNode) -> ObservedThread:
    first = node.first.nodes[0] if node.first.nodes else _Comment()
    latest = node.latest.nodes[0] if node.latest.nodes else first
    thread = Thread(
        id=node.id,
        path=node.path,
        line=node.line if node.line is not None else node.original_line,
        outdated=node.is_outdated,
        resolved=node.is_resolved,
        author=first.author.login if first.author and first.author.login else "ghost",
        summary=summarise(first.body),
        url=first.url,
        body=first.body,
    )
    return ObservedThread(thread=thread, latest_body=latest.body)


def fetch_pull_request(graphql: GraphQL, repo: RepoSlug, number: int) -> PullRequest:
    """The PR's head branch and all of its commits."""
    what = f"commits of {repo}#{number}"
    heads: list[_PullRequest] = []

    def page(cursor: str | None) -> _Page[_CommitNode]:
        pr = _pull_request(_call(graphql, what, COMMITS_QUERY, _variables(repo, number, cursor)), repo, number)
        if pr.commits is None:
            raise GitHubError(f"{what}: response has no commits")
        heads.append(pr)
        return _Page(pr.commits.nodes, pr.commits.page_info)

    commits = tuple(node.commit.oid for node in _pages(page, what))
    head = heads[-1]
    return PullRequest(head_ref=head.head_ref_name, head_oid=head.head_ref_oid, commits=commits)


def reply(graphql: GraphQL, thread_id: str, body: str) -> str:
    """Post *body* as a reply on the thread; returns the new comment's URL."""
    data = _call(graphql, f"reply on {thread_id}", REPLY_MUTATION, {"thread": thread_id, "body": body})
    if data.add_reply is None or data.add_reply.comment is None:
        raise GitHubError(f"reply on {thread_id}: GitHub did not return the new comment")
    return data.add_reply.comment.url


def resolve(graphql: GraphQL, thread_id: str) -> None:
    """Mark the thread resolved."""
    data = _call(graphql, f"resolve {thread_id}", RESOLVE_MUTATION, {"thread": thread_id})
    if data.resolve is None or data.resolve.thread is None or not data.resolve.thread.is_resolved:
        raise GitHubError(f"resolve {thread_id}: GitHub did not report the thread resolved")
