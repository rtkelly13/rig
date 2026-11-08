"""What GitHub knows about a clone's branches, in as few calls as possible.

`repo_governance.branch_cleanup` asks `gh pr list` once per state, so learning
whether a branch landed costs two round trips per clone and returns the two
states as separate maps that never get compared. That second part is a hazard,
not just a cost: a **reused** branch can carry an old merged PR and a new open
one, and a caller holding only the merged map will happily authorise deleting
live work.

GraphQL returns `number`, `state` and `headRefName` together, so one page of 100
answers both questions at once and the newest PR per head wins with its state
attached. On `rtkelly13/blog` — 143 PRs — that is two calls instead of two, but
with the states reconciled and no silent truncation.

Two passes, because "most recent 200" and "mine" are different windows:

1. **Recently updated, any author.** Catches everything active, which is what a
   cleanup pass is about.
2. **`author:@me`.** On a repo with other contributors the first window can push
   the user's own older PRs out, and those are exactly the branches still
   sitting in their local clone.
"""

from __future__ import annotations

import json
import pathlib
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Annotated

from pydantic import Field

from rig import proc, tracing
from rig.proc import GhRunner, run_gh
from rig.repo_slug import RepoSlug
from rig.typed_json import try_decode_json

#: One GraphQL page. GitHub caps `first` at 100.
PAGE = 100
DEFAULT_LIMIT = 200

_RECENT = """
query {{
  repository(owner: {owner}, name: {name}) {{
    pullRequests(first: {page}{after}, orderBy: {{field: UPDATED_AT, direction: DESC}}) {{
      pageInfo {{ hasNextPage endCursor }}
      nodes {{ number state headRefName headRefOid }}
    }}
  }}
}}
"""

_MINE = """
query {{
  search(query: {q}, type: ISSUE, first: {page}{after}) {{
    pageInfo {{ hasNextPage endCursor }}
    nodes {{ ... on PullRequest {{ number state headRefName headRefOid }} }}
  }}
}}
"""


@dataclass(frozen=True)
class PullRequest:
    """The PR that most recently described a head ref."""

    number: int
    state: str
    head_ref: str
    #: The PR's last head commit; for a merged PR, what was merged.
    head_oid: str = ""

    @property
    def merged(self) -> bool:
        return self.state == "MERGED"

    @property
    def open(self) -> bool:
        return self.state == "OPEN"


@dataclass(slots=True, frozen=True)
class _PageInfo:
    has_next_page: Annotated[bool, Field(alias="hasNextPage")] = False
    end_cursor: Annotated[str | None, Field(alias="endCursor")] = None


@dataclass(slots=True, frozen=True)
class _PullNode:
    """A search hit that is an issue, not a PR, arrives as ``{}`` and decodes to the defaults."""

    number: int | None = None
    state: str | None = None
    head_ref_name: Annotated[str | None, Field(alias="headRefName")] = None
    head_oid: Annotated[str | None, Field(alias="headRefOid")] = None


@dataclass(slots=True, frozen=True)
class _Connection:
    page_info: Annotated[_PageInfo, Field(alias="pageInfo")] = _PageInfo()
    nodes: tuple[_PullNode | None, ...] = ()


@dataclass(slots=True, frozen=True)
class _Repository:
    pull_requests: Annotated[_Connection | None, Field(alias="pullRequests")] = None


@dataclass(slots=True, frozen=True)
class _RecentData:
    repository: _Repository | None = None


@dataclass(slots=True, frozen=True)
class _RecentResponse:
    data: _RecentData | None = None

    @property
    def connection(self) -> _Connection | None:
        if self.data is None or self.data.repository is None:
            return None
        return self.data.repository.pull_requests


@dataclass(slots=True, frozen=True)
class _MineData:
    search: _Connection | None = None


@dataclass(slots=True, frozen=True)
class _MineResponse:
    data: _MineData | None = None

    @property
    def connection(self) -> _Connection | None:
        return self.data.search if self.data else None


type _Build = Callable[[int, str], str]
type _Extract = Callable[[str], _Connection | None]


def _after(cursor: str) -> str:
    return f", after: {json.dumps(cursor)}" if cursor else ""


def _recent_page(text: str) -> _Connection | None:
    response = try_decode_json(_RecentResponse, text)
    return response.connection if response else None


def _mine_page(text: str) -> _Connection | None:
    response = try_decode_json(_MineResponse, text)
    return response.connection if response else None


def _decoder(read: Callable[[str], _Connection | None], runner: GhRunner) -> _Extract:
    def extract(query: str) -> _Connection | None:
        result = runner(query)
        return read(result.stdout) if result.returncode == 0 and result.stdout.strip() else None

    return extract


def _collect(connection: _Connection, into: dict[str, PullRequest]) -> None:
    """Fold one page into *into*.

    The newest PR number wins per head ref, and it brings its own state. That
    ordering is the whole point: a branch reused after a merge has a higher
    open PR number, so the open state is what a caller sees.
    """
    for node in connection.nodes:
        if node is None or not node.head_ref_name or node.number is None:
            continue
        head = node.head_ref_name
        existing = into.get(head)
        if existing is None or node.number > existing.number:
            into[head] = PullRequest(
                number=node.number, state=node.state or "", head_ref=head, head_oid=node.head_oid or ""
            )


def _paged(build: _Build, extract: _Extract, limit: int) -> dict[str, PullRequest]:
    """Page a GraphQL connection until exhausted or *limit* nodes are seen."""
    found: dict[str, PullRequest] = {}
    cursor = ""
    seen = 0
    while seen < limit:
        connection = extract(build(min(PAGE, limit - seen), cursor))
        if connection is None:
            break
        _collect(connection, found)
        seen += PAGE
        cursor = connection.page_info.end_cursor or ""
        if not connection.page_info.has_next_page or not cursor:
            break
    return found


def origin_slug(repo: pathlib.Path) -> RepoSlug | None:
    """Return the clone's origin owner and name, or None when origin is not a GitHub remote."""
    ran = proc.git("remote", "get-url", "origin", cwd=repo, timeout=10)
    return RepoSlug.from_remote(ran.out) if ran.ok else None


def pr_heads(
    repo: pathlib.Path, *, limit: int = DEFAULT_LIMIT, include_mine: bool = True, runner: GhRunner = run_gh
) -> dict[str, PullRequest]:
    """Return ``{head_ref: PullRequest}`` — the newest PR per branch, with state.

    Two passes, merged so that the higher PR number always wins. Set
    *include_mine* to False to skip the author-filtered pass when the repo has a
    single contributor and the recent window is already complete.
    """
    slug = origin_slug(repo)
    if slug is None:
        return {}

    owner, name = json.dumps(slug.owner), json.dumps(slug.name)
    heads = _paged(
        lambda page, cursor: _RECENT.format(owner=owner, name=name, page=page, after=_after(cursor)),
        _decoder(_recent_page, runner),
        limit,
    )

    if include_mine:
        q = json.dumps(f"repo:{slug.full_name} is:pr author:@me")
        mine = _paged(
            lambda page, cursor: _MINE.format(q=q, page=page, after=_after(cursor)),
            _decoder(_mine_page, runner),
            limit,
        )
        for head, pull in mine.items():
            existing = heads.get(head)
            if existing is None or pull.number > existing.number:
                heads[head] = pull

    return heads


def merged_heads(repo: pathlib.Path, *, limit: int = DEFAULT_LIMIT, runner: GhRunner = run_gh) -> dict[str, int]:
    """Return ``{head_ref: pr_number}`` for heads whose **newest** PR merged.

    A head whose newest PR is open is deliberately absent: the branch was reused
    and the open PR is the live fact, so nothing here should authorise deleting
    it. Returning the plain int keeps the shape `estate.cleanup` already takes.
    """
    with tracing.span("estate.merged_heads", scope="rig.estate", **{"estate.clone": str(repo)}) as s:
        heads = {head: pull.number for head, pull in pr_heads(repo, limit=limit, runner=runner).items() if pull.merged}
        s.set(**{"estate.merged_heads": len(heads)})
    return heads


def merged_pr(repo: pathlib.Path, branch: str, merged_heads: Mapping[str, int] | None) -> int | None:
    """The merged PR for *branch*: by its own name, else by the origin branch it pushes to.

    GitHub knows a PR by its head ref, which is the *remote* branch name. A local branch pushed
    under another name (`git push -u origin local:remote`) only matches through its upstream.
    The upstream comes from config, so it still resolves after the remote branch is deleted.
    """
    if merged_heads is None:
        return None
    if (pr := merged_heads.get(branch)) is not None:
        return pr
    fmt = "%(upstream:remotename)\t%(upstream:remoteref)"
    remote, _, ref = proc.git_out("for-each-ref", f"--format={fmt}", f"refs/heads/{branch}", cwd=repo).partition("\t")
    if remote != "origin" or not ref.startswith("refs/heads/"):
        return None
    return merged_heads.get(ref.removeprefix("refs/heads/"))
