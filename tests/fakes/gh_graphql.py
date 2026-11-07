"""A stateful fake of GitHub's GraphQL API for the `rig.gh_threads.github.GraphQL` port.

It models pull requests with their commits and review threads, answers the four operations
`rig gh threads` sends (by operation name), pages its connections, and applies the two mutations
to its own state, so a second run sees what the first one did. TEMPORARY with `rig.gh_threads`.
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass, field

from rig.gh_threads.github import GraphQL
from rig.proc import Ran

CODEX = "chatgpt-codex-connector"
_OPERATION = re.compile(r"\b(?:query|mutation)\s+(\w+)")


@dataclass
class FakeThread:
    id: str
    path: str
    line: int | None
    body: str
    author: str = CODEX
    outdated: bool = False
    resolved: bool = False
    replies: list[str] = field(default_factory=list)


@dataclass
class FakePR:
    head_ref: str
    commits: list[str]
    threads: list[FakeThread] = field(default_factory=list)


@dataclass(frozen=True)
class Write:
    operation: str
    thread: str
    body: str = ""


class FakeGitHub:
    """Answers `GraphQL` calls from in-memory PRs keyed by ``owner/name#number``."""

    def __init__(self, prs: Mapping[str, FakePR], *, page_size: int = 100) -> None:
        self.prs: dict[str, FakePR] = dict(prs)
        self.page_size: int = page_size
        self.calls: list[str] = []
        self.writes: list[Write] = []
        #: Operation name -> GraphQL error message to answer it with, as GitHub does for a refused call.
        self.failures: dict[str, str] = {}

    def __call__(self, query: str, variables: Mapping[str, str | int], /) -> Ran:
        match = _OPERATION.search(query)
        assert match, f"no operation name in {query!r}"
        operation = match.group(1)
        self.calls.append(operation)
        if operation in self.failures:
            message = self.failures[operation]
            return Ran(1, json.dumps({"errors": [{"message": message}]}), f"gh: {message}\n")
        if operation == "ReviewThreads":
            return self._threads(variables)
        if operation == "PullRequestCommits":
            return self._commits(variables)
        thread = self._thread(str(variables["thread"]))
        if operation == "ReplyToThread":
            body = str(variables["body"])
            thread.replies.append(body)
            self.writes.append(Write(operation, thread.id, body))
            url = f"https://github.com/fake/pull/1#discussion_r{len(self.writes)}"
            return _ok({"addPullRequestReviewThreadReply": {"comment": {"url": url}}})
        if operation == "ResolveThread":
            thread.resolved = True
            self.writes.append(Write(operation, thread.id))
            return _ok({"resolveReviewThread": {"thread": {"id": thread.id, "isResolved": True}}})
        raise AssertionError(f"unexpected operation {operation}")

    def _pr(self, variables: Mapping[str, str | int]) -> FakePR | None:
        return self.prs.get(f"{variables['owner']}/{variables['name']}#{variables['number']}")

    def _page[T](self, items: list[T], variables: Mapping[str, str | int]) -> tuple[list[T], dict[str, object]]:
        start = int(variables.get("cursor", 0))
        end = start + self.page_size
        more = end < len(items)
        return items[start:end], {"hasNextPage": more, "endCursor": str(end) if more else None}

    def _threads(self, variables: Mapping[str, str | int]) -> Ran:
        pr = self._pr(variables)
        if pr is None:
            return _ok({"repository": {"pullRequest": None}})
        nodes, info = self._page(pr.threads, variables)
        return _ok(
            {
                "repository": {
                    "pullRequest": {
                        "reviewThreads": {"pageInfo": info, "nodes": [_thread_node(thread) for thread in nodes]}
                    }
                }
            }
        )

    def _commits(self, variables: Mapping[str, str | int]) -> Ran:
        pr = self._pr(variables)
        if pr is None:
            return _ok({"repository": {"pullRequest": None}})
        nodes, info = self._page(pr.commits, variables)
        return _ok(
            {
                "repository": {
                    "pullRequest": {
                        "headRefName": pr.head_ref,
                        "headRefOid": pr.commits[-1],
                        "commits": {"pageInfo": info, "nodes": [{"commit": {"oid": oid}} for oid in nodes]},
                    }
                }
            }
        )

    def _thread(self, thread_id: str) -> FakeThread:
        for pr in self.prs.values():
            for thread in pr.threads:
                if thread.id == thread_id:
                    return thread
        raise AssertionError(f"mutation on unknown thread {thread_id}")


def _thread_node(thread: FakeThread) -> dict[str, object]:
    first = {"author": {"login": thread.author}, "body": thread.body, "url": f"https://github.com/c/{thread.id}"}
    latest = thread.replies[-1] if thread.replies else thread.body
    return {
        "id": thread.id,
        "isResolved": thread.resolved,
        "isOutdated": thread.outdated,
        "path": thread.path,
        "line": None if thread.outdated else thread.line,
        "originalLine": thread.line,
        "first": {"nodes": [first]},
        "latest": {"nodes": [{"body": latest}]},
    }


def _ok(data: dict[str, object]) -> Ran:
    return Ran(0, json.dumps({"data": data}), "")


def as_port(fake: FakeGitHub) -> GraphQL:
    """The fake typed as the port, so a signature change to `GraphQL` breaks the fake visibly."""
    return fake
