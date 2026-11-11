"""The newest pull request for each work-item branch: one aliased GraphQL query per repo, cached briefly.

Listing a repo's recent PRs would page through hundreds of heads; a work list only cares
about the handful of branches checked out locally, so it asks for exactly those. One
`gh api graphql` call per repo, with one aliased `pullRequests(headRefName:)` lookup per
branch, returns the newest PR per head with draft state and URL. Transport is
`rig.proc.run_gh`, injectable as a `GhRunner` so tests never touch the network.

Answers are cached in `~/.work-items-prs.json` for `DEFAULT_TTL`. A failed call is never
cached, and never raises: the repo's lookup is reported `unavailable`.
"""

from __future__ import annotations

import datetime as dt
import json
from collections.abc import Mapping, Sequence
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from functools import partial
from pathlib import Path
from typing import Annotated

from pydantic import Field

from desktop_manager.work.model import BranchKey, PrInfo, PrLookup, PrStatus
from rig.proc import GhRunner, run_gh
from rig.typed_json import try_decode_json

CACHE_FILE = Path.home() / ".work-items-prs.json"
DEFAULT_TTL = dt.timedelta(minutes=10)
DEFAULT_WORKERS = 8


@dataclass(frozen=True, slots=True)
class PrRequest:
    repo_path: Path
    #: `owner/name`; empty when the clone has no GitHub origin.
    slug: str
    branches: tuple[str, ...]


# -- query and decode (pure) ----------------------------------------------------


def build_query(slug: str, branches: Sequence[str]) -> str:
    """One query, aliased `b0..bN` in the order of *branches*. Strings are JSON-escaped."""
    owner, _, name = slug.partition("/")
    lookups = "\n".join(
        f"    b{i}: pullRequests(headRefName: {json.dumps(branch)}, first: 1, "
        "orderBy: {field: CREATED_AT, direction: DESC}) { nodes { number state isDraft url updatedAt } }"
        for i, branch in enumerate(branches)
    )
    return f"query {{\n  repository(owner: {json.dumps(owner)}, name: {json.dumps(name)}) {{\n{lookups}\n  }}\n}}"


@dataclass(frozen=True, slots=True)
class _Node:
    number: int
    state: str
    is_draft: Annotated[bool, Field(alias="isDraft")] = False
    url: str = ""
    updated_at: Annotated[str | None, Field(alias="updatedAt")] = None


@dataclass(frozen=True, slots=True)
class _Connection:
    nodes: tuple[_Node | None, ...] = ()


@dataclass(frozen=True, slots=True)
class _Data:
    repository: dict[str, _Connection | None] | None = None


@dataclass(frozen=True, slots=True)
class _Response:
    data: _Data | None = None


def _parse_time(text: str | None) -> dt.datetime | None:
    if not text:
        return None
    try:
        return dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None


def _status(node: _Node) -> PrStatus | None:
    if node.state == "OPEN":
        return PrStatus.DRAFT if node.is_draft else PrStatus.OPEN
    if node.state == "MERGED":
        return PrStatus.MERGED
    if node.state == "CLOSED":
        return PrStatus.CLOSED
    return None


def _info(node: _Node | None) -> PrInfo | None:
    if node is None:
        return None
    status = _status(node)
    if status is None:
        return None
    return PrInfo(number=node.number, status=status, url=node.url, updated_at=_parse_time(node.updated_at))


def parse_response(stdout: str, branches: Sequence[str]) -> dict[str, PrInfo | None] | None:
    """`{branch: newest PR or None}`, or None when the response is unusable."""
    response = try_decode_json(_Response, stdout)
    if response is None or response.data is None or response.data.repository is None:
        return None
    repository = response.data.repository
    out: dict[str, PrInfo | None] = {}
    for i, branch in enumerate(branches):
        connection = repository.get(f"b{i}")
        nodes = connection.nodes if connection is not None else ()
        out[branch] = _info(nodes[0]) if nodes else None
    return out


# -- cache ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _CachedPr:
    number: int
    status: PrStatus
    url: str = ""
    updated_at: str | None = None


@dataclass(frozen=True, slots=True)
class _CacheEntry:
    fetched_at: str
    branches: dict[str, _CachedPr | None] = field(default_factory=dict[str, "_CachedPr | None"])


@dataclass(frozen=True, slots=True)
class CachedRepo:
    fetched_at: dt.datetime
    branches: Mapping[str, PrInfo | None]

    def answers(self, wanted: Sequence[str], *, now: dt.datetime, ttl: dt.timedelta) -> bool:
        return now - self.fetched_at < ttl and all(b in self.branches for b in wanted)


def read_cache(cache_file: Path) -> dict[str, CachedRepo]:
    try:
        text = cache_file.read_text(encoding="utf-8")
    except OSError:
        return {}
    raw = try_decode_json(dict[str, _CacheEntry], text)
    if raw is None:
        return {}
    out: dict[str, CachedRepo] = {}
    for slug, entry in raw.items():
        fetched = _parse_time(entry.fetched_at)
        if fetched is None:
            continue
        out[slug] = CachedRepo(
            fetched_at=fetched,
            branches={
                branch: None
                if pr is None
                else PrInfo(number=pr.number, status=pr.status, url=pr.url, updated_at=_parse_time(pr.updated_at))
                for branch, pr in entry.branches.items()
            },
        )
    return out


def _pr_json(pr: PrInfo | None) -> dict[str, str | int | None] | None:
    if pr is None:
        return None
    return {
        "number": pr.number,
        "status": pr.status.value,
        "url": pr.url,
        "updated_at": pr.updated_at.isoformat() if pr.updated_at else None,
    }


def write_cache(cache_file: Path, entries: Mapping[str, CachedRepo]) -> None:
    payload = {
        slug: {
            "fetched_at": entry.fetched_at.isoformat(),
            "branches": {branch: _pr_json(pr) for branch, pr in entry.branches.items()},
        }
        for slug, entry in entries.items()
    }
    try:
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        cache_file.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    except OSError:
        return


# -- fetch ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class PrResults:
    prs: dict[BranchKey, PrInfo]
    lookups: dict[Path, PrLookup]
    warnings: tuple[str, ...]
    #: Repos answered from the cache rather than the network.
    cached: int = 0
    fetched: int = 0


def _fetch_one(request: PrRequest, runner: GhRunner) -> dict[str, PrInfo | None] | None:
    result = runner(build_query(request.slug, request.branches))
    if result.returncode != 0:
        return None
    return parse_response(result.stdout, request.branches)


def fetch_prs(
    requests: Sequence[PrRequest],
    *,
    runner: GhRunner = run_gh,
    cache_file: Path | None = CACHE_FILE,
    ttl: dt.timedelta = DEFAULT_TTL,
    refresh: bool = False,
    now: dt.datetime | None = None,
    workers: int = DEFAULT_WORKERS,
) -> PrResults:
    """Look up the newest PR for every requested branch; the cache first unless *refresh*."""
    moment = now or dt.datetime.now(dt.UTC)
    cache = read_cache(cache_file) if cache_file is not None else {}
    prs: dict[BranchKey, PrInfo] = {}
    lookups: dict[Path, PrLookup] = {}
    warnings: list[str] = []
    answered: dict[str, CachedRepo] = {}
    to_fetch: list[PrRequest] = []

    for request in requests:
        if not request.slug:
            lookups[request.repo_path] = PrLookup.UNAVAILABLE
            warnings.append(f"{request.repo_path.name}: origin is not a GitHub remote")
            continue
        hit = cache.get(request.slug)
        if not refresh and hit is not None and hit.answers(request.branches, now=moment, ttl=ttl):
            answered[request.slug] = hit
        else:
            to_fetch.append(request)

    with ThreadPoolExecutor(max_workers=workers) as pool:
        fetched = list(pool.map(partial(_fetch_one, runner=runner), to_fetch))
    for request, result in zip(to_fetch, fetched, strict=True):
        if result is None:
            lookups[request.repo_path] = PrLookup.UNAVAILABLE
            warnings.append(f"{request.slug}: gh lookup failed (offline, unauthenticated or no access)")
            continue
        entry = CachedRepo(fetched_at=moment, branches=result)
        answered[request.slug] = entry
        cache[request.slug] = entry

    for request in requests:
        entry = answered.get(request.slug)
        if entry is None:
            continue
        lookups[request.repo_path] = PrLookup.OK
        for branch in request.branches:
            pr = entry.branches.get(branch)
            if pr is not None:
                prs[BranchKey(request.repo_path, branch)] = pr

    if cache_file is not None and to_fetch:
        write_cache(cache_file, cache)
    return PrResults(
        prs=prs,
        lookups=lookups,
        warnings=tuple(warnings),
        cached=len(requests) - len(to_fetch) - sum(1 for r in requests if not r.slug),
        fetched=sum(1 for r in fetched if r is not None),
    )
