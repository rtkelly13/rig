"""Resolve canonical repository names from GitHub and detect folder/remote mismatches.

Detects repository renames and verifies that local clones in the estate are cloned
into directories matching their canonical upstream GitHub repository names.

Since GitHub's GraphQL schema has no explicit `previousNames` field, rename detection
relies on GitHub's automatic alias/redirect resolution: querying `repository(owner, name)`
with a historical or alias name automatically resolves to the canonical repository
object with its current `name` and `nameWithOwner`.

Results are cached to disk (defaulting to `~/.estate-repo-names.json`) with a configurable
TTL (default 24 hours) to ensure doctor and audit checks run fast without network delays.
"""

from __future__ import annotations

import datetime as dt
import json
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig import proc
from rig.proc import GhRunner, run_gh
from rig.repo_slug import RepoSlug
from rig.typed_json import try_decode_json

DEFAULT_CACHE_FILE = Path.home() / ".estate-repo-names.json"
DEFAULT_TTL = dt.timedelta(hours=24)


@dataclass(frozen=True, slots=True)
class CanonicalRepo:
    """Canonical repository metadata resolved from GitHub."""

    owner: str
    canonical_name: str
    name_with_owner: str
    queried_name: str
    is_archived: bool = False
    url: str = ""

    @property
    def is_renamed(self) -> bool:
        """Whether the queried name differs from the current canonical GitHub name."""
        return self.queried_name.lower() != self.canonical_name.lower()


@dataclass(frozen=True, slots=True)
class RepoFolderCheck:
    """Result of checking a repository folder against its canonical GitHub identity."""

    repo_path: Path
    folder_name: str
    owner: str | None
    remote_repo_name: str | None
    canonical_name: str | None
    is_match: bool
    remote_matches_canonical: bool
    is_renamed: bool
    status: str  # "ok", "folder_renamed", "folder_mismatch", "remote_outdated", "local_only", "unresolved"
    detail: str


@dataclass(frozen=True, slots=True)
class CacheEntry:
    """Cached canonical repository metadata and its timestamp."""

    repo: CanonicalRepo
    fetched_at: dt.datetime


@dataclass(frozen=True, slots=True)
class RepoIdentity:
    """Detected git root path and origin URL."""

    root: Path | None
    origin_url: str | None


@dataclass(frozen=True, slots=True)
class EstateRepoItem:
    """An estate repository path and its owner/name slug."""

    path: Path
    owner: str
    name: str


# -- GraphQL Query & Decoding --------------------------------------------------


@dataclass(frozen=True, slots=True)
class _GqlRepoNode:
    name: str = ""
    name_with_owner: Annotated[str, Field(alias="nameWithOwner")] = ""
    is_archived: Annotated[bool, Field(alias="isArchived")] = False
    url: str = ""


@dataclass(frozen=True, slots=True)
class _GqlResponse:
    data: dict[str, _GqlRepoNode | None] | None = None


def build_batch_query(slugs: Sequence[RepoSlug]) -> str:
    """Builds a single GraphQL query resolving multiple RepoSlug pairs using aliases."""
    lookups = "\n".join(
        f"  r{i}: repository(owner: {json.dumps(slug.owner)}, name: {json.dumps(slug.name)}) {{\n"
        "    name\n"
        "    nameWithOwner\n"
        "    isArchived\n"
        "    url\n"
        "  }"
        for i, slug in enumerate(slugs)
    )
    return f"query {{\n{lookups}\n}}"


def parse_batch_response(stdout: str, slugs: Sequence[RepoSlug]) -> dict[RepoSlug, CanonicalRepo | None]:
    """Parses GraphQL batch query response into CanonicalRepo records."""
    response = try_decode_json(_GqlResponse, stdout)
    if response is None or response.data is None:
        return {slug: None for slug in slugs}

    result: dict[RepoSlug, CanonicalRepo | None] = {}
    for i, slug in enumerate(slugs):
        node = response.data.get(f"r{i}")
        if node is None or not node.name:
            result[slug] = None
        else:
            result[slug] = CanonicalRepo(
                owner=slug.owner,
                canonical_name=node.name,
                name_with_owner=node.name_with_owner or f"{slug.owner}/{node.name}",
                queried_name=slug.name,
                is_archived=node.is_archived,
                url=node.url,
            )
    return result


# -- Cache Persistence ---------------------------------------------------------


@dataclass(frozen=True, slots=True)
class _CachedRepo:
    owner: str
    canonical_name: str
    name_with_owner: str
    queried_name: str
    fetched_at: str
    is_archived: bool = False
    url: str = ""


@dataclass(frozen=True, slots=True)
class _CachePayload:
    repos: dict[str, _CachedRepo] = field(default_factory=dict[str, _CachedRepo])


def _slug_key(owner: str, name: str) -> str:
    return f"{owner.lower()}/{name.lower()}"


def read_cache(cache_file: Path) -> dict[str, CacheEntry]:
    """Reads disk cache returning {slug_key: CacheEntry}."""
    try:
        text = cache_file.read_text(encoding="utf-8")
    except OSError:
        return {}

    payload = try_decode_json(_CachePayload, text)
    if payload is None:
        return {}

    entries: dict[str, CacheEntry] = {}
    for key, item in payload.repos.items():
        try:
            fetched_at = dt.datetime.fromisoformat(item.fetched_at)
        except ValueError:
            continue
        entries[key] = CacheEntry(
            repo=CanonicalRepo(
                owner=item.owner,
                canonical_name=item.canonical_name,
                name_with_owner=item.name_with_owner,
                queried_name=item.queried_name,
                is_archived=item.is_archived,
                url=item.url,
            ),
            fetched_at=fetched_at,
        )
    return entries


def write_cache(cache_file: Path, entries: Mapping[str, CacheEntry]) -> None:
    """Writes canonical repository entries to disk cache."""
    payload = {
        "repos": {
            key: {
                "owner": entry.repo.owner,
                "canonical_name": entry.repo.canonical_name,
                "name_with_owner": entry.repo.name_with_owner,
                "queried_name": entry.repo.queried_name,
                "fetched_at": entry.fetched_at.isoformat(),
                "is_archived": entry.repo.is_archived,
                "url": entry.repo.url,
            }
            for key, entry in entries.items()
        }
    }
    try:
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        cache_file.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    except OSError:
        return


# -- Resolving Logic -----------------------------------------------------------


def resolve_canonical_repos(
    slugs: Sequence[RepoSlug],
    *,
    runner: GhRunner = run_gh,
    cache_file: Path | None = None,
    force_refresh: bool = False,
    ttl: dt.timedelta = DEFAULT_TTL,
    now: dt.datetime | None = None,
) -> dict[RepoSlug, CanonicalRepo | None]:
    """Resolves canonical repository metadata for a sequence of RepoSlugs.

    Utilizes cache when valid, falling back to a single batched GitHub GraphQL request
    for any missing or expired pairs.
    """
    target_cache = cache_file or DEFAULT_CACHE_FILE
    current_time = now or dt.datetime.now(dt.UTC)
    cached_entries = {} if force_refresh else read_cache(target_cache)

    resolved: dict[RepoSlug, CanonicalRepo | None] = {}
    to_fetch: list[RepoSlug] = []

    for slug in slugs:
        key = _slug_key(slug.owner, slug.name)
        hit = cached_entries.get(key)
        if hit is not None and (current_time - hit.fetched_at < ttl):
            resolved[slug] = hit.repo
        else:
            to_fetch.append(slug)

    if to_fetch:
        query = build_batch_query(to_fetch)
        gh_result = runner(query)
        if gh_result.returncode == 0 and gh_result.stdout:
            fetched_map = parse_batch_response(gh_result.stdout, to_fetch)
            updated_cache = dict(cached_entries)
            for slug, canonical in fetched_map.items():
                resolved[slug] = canonical
                if canonical is not None:
                    updated_cache[_slug_key(slug.owner, slug.name)] = CacheEntry(
                        repo=canonical, fetched_at=current_time
                    )
            write_cache(target_cache, updated_cache)
        else:
            for slug in to_fetch:
                resolved[slug] = None

    return resolved


# -- Git & Folder Inspection ----------------------------------------------------


def get_repo_root_and_origin(path: Path) -> RepoIdentity:
    """Retrieves the primary repository root directory and origin URL.

    Handles both standard checkouts and git worktrees safely by discovering
    the git common directory.
    """
    common_raw = proc.git_out("rev-parse", "--git-common-dir", cwd=path, timeout=5)
    if not common_raw:
        return RepoIdentity(root=None, origin_url=None)
    common_path = Path(common_raw)
    if not common_path.is_absolute():
        common_path = (path / common_path).resolve()
    # Primary root is the directory containing .git
    primary_root = common_path.parent
    url = proc.git_out("config", "--get", "remote.origin.url", cwd=primary_root, timeout=5)
    return RepoIdentity(root=primary_root, origin_url=url or None)


def check_repo_folder(
    path: Path,
    *,
    canonical: CanonicalRepo | None = None,
    runner: GhRunner = run_gh,
    cache_file: Path | None = None,
    force_refresh: bool = False,
) -> RepoFolderCheck:
    """Checks whether a repository is cloned into a folder matching its canonical name."""
    identity = get_repo_root_and_origin(path)
    check_path = identity.root or path
    folder_name = check_path.name

    if not identity.origin_url:
        return RepoFolderCheck(
            repo_path=check_path,
            folder_name=folder_name,
            owner=None,
            remote_repo_name=None,
            canonical_name=None,
            is_match=True,
            remote_matches_canonical=True,
            is_renamed=False,
            status="local_only",
            detail="No remote origin URL configured or not a git repository",
        )

    parsed = RepoSlug.from_remote(identity.origin_url)
    if not parsed:
        return RepoFolderCheck(
            repo_path=check_path,
            folder_name=folder_name,
            owner=None,
            remote_repo_name=None,
            canonical_name=None,
            is_match=True,
            remote_matches_canonical=True,
            is_renamed=False,
            status="local_only",
            detail=f"Non-GitHub remote origin ({identity.origin_url})",
        )

    owner = parsed.owner
    remote_name = parsed.name

    # Determine canonical metadata
    resolved_canonical = canonical
    if resolved_canonical is None:
        slug_results = resolve_canonical_repos(
            [parsed],
            runner=runner,
            cache_file=cache_file,
            force_refresh=force_refresh,
        )
        resolved_canonical = slug_results.get(parsed)

    if resolved_canonical is None:
        return RepoFolderCheck(
            repo_path=check_path,
            folder_name=folder_name,
            owner=owner,
            remote_repo_name=remote_name,
            canonical_name=None,
            is_match=False,
            remote_matches_canonical=False,
            is_renamed=False,
            status="unresolved",
            detail=f"Could not resolve repository '{owner}/{remote_name}' on GitHub",
        )

    canonical_name = resolved_canonical.canonical_name
    folder_matches = folder_name.lower() == canonical_name.lower()
    remote_matches = remote_name.lower() == canonical_name.lower()
    is_renamed = resolved_canonical.is_renamed

    if not folder_matches:
        if folder_name.lower() == resolved_canonical.queried_name.lower() and is_renamed:
            status = "folder_renamed"
            detail = (
                f"Folder '{folder_name}' is named after a previous repository name; "
                f"renamed upstream to '{canonical_name}'"
            )
        else:
            status = "folder_mismatch"
            detail = f"Folder '{folder_name}' does not match canonical upstream name '{canonical_name}'"
    elif not remote_matches:
        status = "remote_outdated"
        detail = f"Remote URL points to '{remote_name}', but canonical upstream name is '{canonical_name}'"
    else:
        status = "ok"
        detail = f"Folder and remote match canonical name '{canonical_name}'"

    return RepoFolderCheck(
        repo_path=check_path,
        folder_name=folder_name,
        owner=owner,
        remote_repo_name=remote_name,
        canonical_name=canonical_name,
        is_match=folder_matches,
        remote_matches_canonical=remote_matches,
        is_renamed=is_renamed,
        status=status,
        detail=detail,
    )


def check_estate_repos(
    estate_root: Path,
    *,
    extra_roots: Sequence[Path] = (),
    runner: GhRunner = run_gh,
    cache_file: Path | None = None,
    force_refresh: bool = False,
) -> list[RepoFolderCheck]:
    """Scans and checks all repository clones under estate_root (and extra_roots) in a single batch."""
    if not estate_root.is_dir():
        return []

    repos: list[EstateRepoItem] = []
    slugs_to_resolve: list[RepoSlug] = []
    children = [child for root in (estate_root, *extra_roots) if root.is_dir() for child in root.iterdir()]

    for item in sorted(children, key=lambda p: p.name.lower()):
        if not item.is_dir() or not (item / ".git").exists():
            continue
        identity = get_repo_root_and_origin(item)
        if not identity.origin_url:
            continue
        parsed = RepoSlug.from_remote(identity.origin_url)
        if not parsed:
            continue
        repos.append(EstateRepoItem(path=item, owner=parsed.owner, name=parsed.name))
        slugs_to_resolve.append(parsed)

    resolved_map = resolve_canonical_repos(
        slugs_to_resolve,
        runner=runner,
        cache_file=cache_file,
        force_refresh=force_refresh,
    )

    checks: list[RepoFolderCheck] = []
    for item in repos:
        slug = RepoSlug(owner=item.owner, name=item.name)
        canonical = resolved_map.get(slug)
        check = check_repo_folder(
            item.path,
            canonical=canonical,
            runner=runner,
            cache_file=cache_file,
            force_refresh=force_refresh,
        )
        checks.append(check)

    return checks
