"""Gather git facts for every clone in the estate: checkouts, branch refs, dirty and unpushed state.

Per clone, three git calls answer most questions: `git worktree list` (via `estate.checks`);
one `git for-each-ref` returning every local branch with its tip, last commit date and
ahead/behind against its upstream, plus `origin/HEAD` for the default branch; and one
`git rev-list --parents --branches --not --remotes` listing the commits on no remote, from
which each branch's unpushed count is a walk from its tip. Then one `git status` per
checkout for dirty state; that is the slow part, bound by disk rather than threads. All of
it fans out over a thread pool; the parsers are pure.
"""

from __future__ import annotations

import datetime as dt
import re
from collections.abc import Iterable, Mapping
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, replace
from functools import partial
from pathlib import Path

from desktop_manager.work.model import BranchRef, Checkout, RepoFacts
from rig import proc
from rig.estate import checks, discovery
from rig.repo_slug import RepoSlug

DEFAULT_WORKERS = 16
#: Bound on the commits-on-no-remote listing. A clone with no remote would otherwise list its whole history.
UNPUSHED_LIMIT = 5000
_FIELD = "\t"
_REF_FIELDS = 6
_REF_FORMAT = _FIELD.join(
    [
        "%(refname)",
        "%(objectname)",
        "%(committerdate:iso-strict)",
        "%(upstream:short)",
        "%(upstream:track)",
        "%(symref)",
    ]
)
_TRACK_PART = re.compile(r"(ahead|behind) (\d+)")


def _git(args: list[str], cwd: Path, timeout: int = 30) -> str | None:
    ran = proc.git(*args, cwd=cwd, timeout=timeout)
    return ran.out if ran.ok else None


# -- pure parsers ---------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class RefScan:
    branches: tuple[BranchRef, ...]
    #: From `origin/HEAD`; empty when the clone has no such ref.
    default_branch: str


@dataclass(frozen=True, slots=True)
class Track:
    ahead: int | None
    behind: int | None
    gone: bool


def parse_track(upstream: str, track: str) -> Track:
    """`[ahead 2, behind 1]` / `[gone]` / empty. No upstream means ahead/behind are unknown."""
    if not upstream:
        return Track(ahead=None, behind=None, gone=False)
    if track.strip() == "[gone]":
        return Track(ahead=None, behind=None, gone=True)
    ahead = behind = 0
    for match in _TRACK_PART.finditer(track):
        if match.group(1) == "ahead":
            ahead = int(match.group(2))
        else:
            behind = int(match.group(2))
    return Track(ahead=ahead, behind=behind, gone=False)


def _parse_date(text: str) -> dt.datetime | None:
    try:
        return dt.datetime.fromisoformat(text)
    except ValueError:
        return None


def unpushed_counts(rev_list_parents: str, tips: Iterable[str]) -> dict[str, int]:
    """`{tip sha: commits on no remote reachable from it}`.

    *rev_list_parents* is `git rev-list --parents` over the commits on no remote: one line
    per commit, its sha then its parents' shas. A tip outside that set is fully pushed. The
    walk stays inside the set: a parent outside it is on a remote, and so is its history.
    """
    parents: dict[str, list[str]] = {}
    for line in rev_list_parents.splitlines():
        shas = line.split()
        if shas:
            parents[shas[0]] = shas[1:]
    counts: dict[str, int] = {}
    for tip in tips:
        if tip not in parents:
            counts[tip] = 0
            continue
        seen = {tip}
        stack = [tip]
        while stack:
            for parent in parents[stack.pop()]:
                if parent in parents and parent not in seen:
                    seen.add(parent)
                    stack.append(parent)
        counts[tip] = len(seen)
    return counts


def ref_tips(output: str) -> list[str]:
    """The tip sha of every line of `_REF_FORMAT` output."""
    return [parts[1] for line in output.splitlines() if len(parts := line.split(_FIELD)) == _REF_FIELDS]


def parse_refs(output: str, unpushed: Mapping[str, int] | None = None) -> RefScan:
    """Parse `git for-each-ref` output in `_REF_FORMAT` over `refs/heads` and `refs/remotes/origin/HEAD`.

    *unpushed* maps a tip sha to its unpushed count (see `unpushed_counts`); missing means 0.
    """
    counts = unpushed or {}
    branches: list[BranchRef] = []
    default_branch = ""
    for line in output.splitlines():
        parts = line.split(_FIELD)
        if len(parts) != _REF_FIELDS:
            continue
        refname, sha, date, upstream, track, symref = parts
        if refname == "refs/remotes/origin/HEAD":
            default_branch = symref.removeprefix("refs/remotes/origin/")
            continue
        if not refname.startswith("refs/heads/"):
            continue
        tracked = parse_track(upstream, track)
        branches.append(
            BranchRef(
                name=refname.removeprefix("refs/heads/"),
                last_commit=_parse_date(date),
                upstream=upstream,
                upstream_gone=tracked.gone,
                ahead=tracked.ahead,
                behind=tracked.behind,
                unpushed=counts.get(sha, 0),
            )
        )
    return RefScan(branches=tuple(branches), default_branch=default_branch)


def repo_display_name(repo: Path, root: Path) -> str:
    try:
        return repo.relative_to(root).as_posix()
    except ValueError:
        return repo.name


# -- IO -------------------------------------------------------------------------


def _checkouts(repo: Path) -> tuple[Checkout, ...]:
    out: list[Checkout] = []
    for worktree in checks.list_worktrees(repo):
        if worktree.bare or worktree.prunable:
            continue
        out.append(Checkout(path=worktree.path, branch=worktree.branch, is_main_checkout=worktree.is_main))
    if not out:  # git failed; still list the clone itself
        out.append(Checkout(path=repo, branch="", is_main_checkout=True))
    return tuple(out)


def read_repo(repo: Path, root: Path) -> RepoFacts:
    """The cheap facts for one clone: its checkouts and branch refs. No status, no network."""
    refs = _git(["for-each-ref", f"--format={_REF_FORMAT}", "refs/heads", "refs/remotes/origin/HEAD"], repo) or ""
    orphans = _git(["rev-list", "--parents", f"--max-count={UNPUSHED_LIMIT}", "--branches", "--not", "--remotes"], repo)
    scan = parse_refs(refs, unpushed_counts(orphans or "", ref_tips(refs)))
    names = {b.name for b in scan.branches}
    default = scan.default_branch or next((n for n in ("main", "master") if n in names), "")
    return RepoFacts(
        path=repo,
        name=repo_display_name(repo, root),
        default_branch=default,
        checkouts=_checkouts(repo),
        branches=scan.branches,
    )


def read_slug(repo: Path) -> str:
    slug = RepoSlug.from_remote(_git(["remote", "get-url", "origin"], repo, timeout=10))
    return slug.full_name if slug else ""


def read_slugs(repos: Iterable[Path], *, workers: int = DEFAULT_WORKERS) -> dict[Path, str]:
    paths = list(repos)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        return dict(zip(paths, pool.map(read_slug, paths), strict=True))


def _probe(checkout: Checkout) -> Checkout:
    if not checkout.path.is_dir():
        return checkout
    return replace(checkout, dirty=checks.is_dirty(checkout.path))


def probe_checkouts(repos: list[RepoFacts], *, workers: int = DEFAULT_WORKERS) -> list[RepoFacts]:
    """Add dirty state to every checkout, in one pool."""
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [[pool.submit(_probe, checkout) for checkout in repo.checkouts] for repo in repos]
        return [
            replace(repo, checkouts=tuple(f.result() for f in repo_futures))
            for repo, repo_futures in zip(repos, futures, strict=True)
        ]


def read_estate(root: Path, *, refresh: bool = False, workers: int = DEFAULT_WORKERS) -> list[RepoFacts]:
    """Every clone under *root* (cached discovery, see `estate.discovery`), with checkouts and refs."""
    clones = discovery.repos(root, refresh=refresh).paths
    with ThreadPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(partial(read_repo, root=root), clones))
