"""Read every PR review thread; unreadable or incomplete responses block merges."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Annotated

from pydantic import Field

from rig import proc
from rig.typed_json import try_decode_json


@dataclass(frozen=True, slots=True)
class _ThreadAuthor:
    login: str = ""


@dataclass(frozen=True, slots=True)
class _ThreadCommentNode:
    author: _ThreadAuthor | None = None
    body: str = ""


@dataclass(frozen=True, slots=True)
class _ThreadComments:
    nodes: list[_ThreadCommentNode] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class _PageInfo:
    has_next_page: Annotated[bool, Field(alias="hasNextPage")]
    end_cursor: Annotated[str | None, Field(alias="endCursor")] = None


@dataclass(frozen=True, slots=True)
class _ReviewThreadNode:
    id: str
    is_resolved: Annotated[bool, Field(alias="isResolved")]
    comments: _ThreadComments = field(default_factory=_ThreadComments)


@dataclass(frozen=True, slots=True)
class _ReviewThreads:
    nodes: list[_ReviewThreadNode]
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")]


@dataclass(frozen=True, slots=True)
class _GqlPullRequest:
    review_threads: Annotated[_ReviewThreads, Field(alias="reviewThreads")]


@dataclass(frozen=True, slots=True)
class _GqlRepository:
    pull_request: Annotated[_GqlPullRequest | None, Field(alias="pullRequest")] = None


@dataclass(frozen=True, slots=True)
class _GqlData:
    repository: _GqlRepository | None = None


@dataclass(frozen=True, slots=True)
class _GqlResponse:
    data: _GqlData | None = None
    errors: list[object] = field(default_factory=list)


_QUERY = """
query($owner: String!, $repo: String!, $pr: Int!, $cursor: String) {
  repository(owner: $owner, name: $repo) {
    pullRequest(number: $pr) {
      reviewThreads(first: 100, after: $cursor) {
        pageInfo {
          hasNextPage
          endCursor
        }
        nodes {
          id
          isResolved
          comments(first: 1) {
            nodes {
              author { login }
              body
            }
          }
        }
      }
    }
  }
}
"""


@dataclass(frozen=True, slots=True)
class ReviewThreadsResult:
    """Whether all pages were read and every conversation was resolved."""

    is_aligned: bool
    message: str


def inspect_review_threads(repo: str, number: int) -> ReviewThreadsResult:
    """Require explicit resolution for human, bot and outdated review threads alike."""
    if repo.count("/") != 1 or not all(repo.split("/")) or number <= 0:
        return ReviewThreadsResult(False, f"Cannot verify review threads: invalid PR {repo}#{number}")
    owner, name = repo.split("/", 1)
    cursor: str | None = None
    seen: set[str] = set()
    unresolved: list[_ReviewThreadNode] = []
    while True:
        args = [
            "api",
            "graphql",
            "-f",
            f"query={_QUERY}",
            "-f",
            f"owner={owner}",
            "-f",
            f"repo={name}",
            "-F",
            f"pr={number}",
        ]
        if cursor is not None:
            args.extend(["-f", f"cursor={cursor}"])
        response = proc.gh(*args)
        if not response.ok:
            return ReviewThreadsResult(False, f"Failed to query review threads: {response.err.strip()}")
        parsed = try_decode_json(_GqlResponse, response.out)
        if (
            parsed is None
            or parsed.errors
            or parsed.data is None
            or parsed.data.repository is None
            or parsed.data.repository.pull_request is None
        ):
            return ReviewThreadsResult(False, f"Failed to parse review threads GraphQL response for {repo}#{number}")
        threads = parsed.data.repository.pull_request.review_threads
        unresolved.extend(t for t in threads.nodes if not t.is_resolved)
        if not threads.page_info.has_next_page:
            break
        cursor = threads.page_info.end_cursor
        if not cursor or cursor in seen:
            return ReviewThreadsResult(False, f"Incomplete review thread pagination for {repo}#{number}")
        seen.add(cursor)
    if unresolved:
        details: list[str] = []
        for thread in unresolved:
            author = thread.comments.nodes[0].author if thread.comments.nodes else None
            details.append(f"Thread {thread.id} by @{author.login if author else 'unknown'} is unresolved")
        return ReviewThreadsResult(
            False,
            f"{len(unresolved)} unresolved conversation thread(s)!\n"
            + "\n".join(details)
            + "\nAll conversation threads must be resolved before merging.",
        )
    return ReviewThreadsResult(True, "All review threads are resolved")
