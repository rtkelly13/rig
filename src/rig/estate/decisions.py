"""The worktree questions only a human should answer, as one feed.

Everything else the cleanup jobs touch regenerates, so they act on it alone.
What lands here does not, or cannot be judged from the disk:

| kind | what it means |
|---|---|
| `dubious-ownership` | git refuses to read the checkout: its owner is not the current user |
| `unpushed-worktree` | an idle worktree holds uncommitted files or commits on no remote |
| `closed-pr-worktree` | an idle worktree's branch had its newest PR closed without merging |
| `no-pr-worktree` | an idle worktree's branch never had a PR, or it is detached |
| `locked-path` | a worktree directory git no longer tracks: an earlier delete left it behind |

Idle means git's bookkeeping for the worktree has not moved for `idle_days`
(`trim.last_activity`), so live work in progress never shows up. Each path gets
one decision, the first kind that applies in the order above. A merged or open
PR is not a question: `tidy` and `worktree reclaim` finish the merged ones.

PR state comes from GitHub (`prs.pr_heads`) only for idle worktrees that are
clean and pushed. When GitHub cannot be asked, those worktrees are left out
rather than reported as having no PR.

The daily `estate-decisions` job writes the feed to `default_feed_path()`, a
JSON array of `Decision` objects, for the web console and the boot-time Claude
session to read without scanning anything.
"""

from __future__ import annotations

import os
import pathlib
import time
from collections.abc import Callable, Iterable, Mapping
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from enum import StrEnum
from functools import partial

from pydantic import TypeAdapter

from rig import proc
from rig.estate import discovery
from rig.estate.checks import Worktree, is_dirty, list_worktrees, orphaned_count
from rig.estate.disksize import footprint
from rig.estate.prs import PullRequest, origin_slug, pr_heads
from rig.estate.trim import DEFAULT_IDLE_DAYS, last_activity
from rig.typed_json import try_decode_json

FEED_NAME = "decisions.json"
WORKERS = 8


class Kind(StrEnum):
    DUBIOUS_OWNERSHIP = "dubious-ownership"
    UNPUSHED_WORKTREE = "unpushed-worktree"
    CLOSED_PR_WORKTREE = "closed-pr-worktree"
    NO_PR_WORKTREE = "no-pr-worktree"
    LOCKED_PATH = "locked-path"


_ORDER = list(Kind)


@dataclass(frozen=True, slots=True)
class Decision:
    """One thing on this machine a human has to decide about."""

    kind: str
    #: The clone's directory name.
    repo: str
    path: str
    reason: str
    size_bytes: int
    suggested_action: str
    #: One command that carries out the suggested action.
    command: str


#: Newest PR per head ref for a clone, or None when GitHub could not be asked.
type PullRequestLookup = Callable[[pathlib.Path], Mapping[str, PullRequest] | None]


def github_pull_requests(clone: pathlib.Path) -> Mapping[str, PullRequest] | None:
    """The newest PR per branch from `gh`; None when the clone has no GitHub origin `gh` can see."""
    if origin_slug(clone) is None:
        return None
    return pr_heads(clone)


def is_dubious(path: pathlib.Path) -> bool:
    """True when git refuses to read *path* because someone else owns it."""
    ran = proc.git("rev-parse", "--git-dir", cwd=path)
    return not ran.ok and "dubious ownership" in ran.err


def _size(path: pathlib.Path) -> int:
    return footprint(path).bytes_used


def _dubious(repo: str, path: pathlib.Path) -> Decision:
    return Decision(
        kind=Kind.DUBIOUS_OWNERSHIP,
        repo=repo,
        path=str(path),
        reason="git refuses to read it: the directory is owned by another account",
        size_bytes=_size(path),
        suggested_action=(
            f'Trust it with safe.directory, or take ownership if it is yours: takeown /f "{path}" /r /d y'
        ),
        command=f"git config --global --add safe.directory {path.as_posix()}",
    )


def _unpushed(repo: str, tree: Worktree, *, orphaned: int, dirty: bool, idle_days: int) -> Decision:
    parts = [f"{orphaned} commit(s) on no remote"] if orphaned else []
    if dirty:
        parts.append("uncommitted changes")
    # Not `push -u origin <branch>`: that lands half-finished work on a PR branch.
    # The shelve preview pushes nothing and lists anything a person must settle first.
    return Decision(
        kind=Kind.UNPUSHED_WORKTREE,
        repo=repo,
        path=str(tree.path),
        reason=f"{' and '.join(parts)}; untouched for {idle_days}+ days",
        size_bytes=_size(tree.path),
        suggested_action=(
            "Preview a shelve, then re-run it with --apply: the work goes to a shelved/ branch on origin, "
            "verified, before the worktree is removed"
        ),
        command=f'estate shelve "{tree.path}"',
    )


def _pull_request_decision(
    repo: str, clone: pathlib.Path, tree: Worktree, *, heads: Mapping[str, PullRequest], idle_days: int
) -> Decision | None:
    remove = f"cd {clone} && git worktree remove {tree.path}"
    pull = heads.get(tree.branch) if tree.branch else None
    if pull is None:
        reason = (
            f"`{tree.branch}` has never had a PR; untouched for {idle_days}+ days"
            if tree.branch
            else f"detached HEAD, so no PR can match it; untouched for {idle_days}+ days"
        )
        return Decision(
            kind=Kind.NO_PR_WORKTREE,
            repo=repo,
            path=str(tree.path),
            reason=reason,
            size_bytes=_size(tree.path),
            suggested_action="Open a PR if the work is wanted; otherwise remove the worktree",
            command=remove,
        )
    if pull.state == "CLOSED":
        return Decision(
            kind=Kind.CLOSED_PR_WORKTREE,
            repo=repo,
            path=str(tree.path),
            reason=f"PR #{pull.number} for `{tree.branch}` was closed without merging",
            size_bytes=_size(tree.path),
            suggested_action="Remove the worktree if the work is abandoned, or reopen the PR",
            command=remove,
        )
    return None


def _gitdir(marker: pathlib.Path) -> pathlib.Path | None:
    """Where a worktree's `.git` file points, or None when it does not say."""
    try:
        text = marker.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None
    head, found, pointer = text.partition("gitdir:")
    if not found or head.strip():
        return None
    admin = pathlib.Path(pointer.strip())
    return admin if admin.is_absolute() else marker.parent / admin


def _left_behind(path: pathlib.Path, *, needs_marker: bool) -> bool:
    """True when *path* was a worktree git no longer knows about.

    A `.git` directory is a clone of its own, and a `.git` file whose admin dir
    exists is a live worktree of some repo (not necessarily this clone). Inside a
    worktree container a directory with no `.git` at all is what an interrupted
    delete leaves; in plain `temp/` it is ordinary scratch, so a marker is required.
    """
    marker = path / ".git"
    if marker.is_dir():
        return False
    if marker.is_file():
        admin = _gitdir(marker)
        return admin is not None and not admin.exists()
    return not needs_marker


def _subdirs(path: pathlib.Path) -> list[pathlib.Path]:
    try:
        return sorted(child for child in path.iterdir() if child.is_dir() and not child.is_symlink())
    except OSError:
        return []


def left_behind(clone: pathlib.Path) -> list[pathlib.Path]:
    """Worktree directories under *clone* that a previous delete did not finish."""
    containers = [clone / ".worktrees", clone / "temp" / "worktrees"]
    found = [child for c in containers for child in _subdirs(c) if _left_behind(child, needs_marker=False)]
    scratch = [c for c in _subdirs(clone / "temp") if c.name != "worktrees" and _left_behind(c, needs_marker=True)]
    return found + scratch


#: How many leftover files a `locked-path` reason names.
SAMPLE_FILES = 5


@dataclass(frozen=True, slots=True)
class Leftover:
    """What an unfinished delete left: its files' bytes, how many, and a few of their names."""

    bytes_used: int = 0
    files: int = 0
    sample: tuple[str, ...] = ()


def leftover(path: pathlib.Path) -> Leftover:
    """Walk *path* without entering links, counting the regular files still in it."""
    total = 0
    count = 0
    sample: list[str] = []
    stack = [path]
    while stack:
        current = stack.pop()
        try:
            entries = list(os.scandir(current))
        except OSError:
            continue
        for entry in entries:
            try:
                if entry.is_symlink() or entry.is_junction():
                    continue
                if entry.is_dir(follow_symlinks=False):
                    stack.append(pathlib.Path(entry.path))
                elif entry.is_file(follow_symlinks=False):
                    total += entry.stat(follow_symlinks=False).st_size
                    count += 1
                    if len(sample) < SAMPLE_FILES:
                        sample.append(pathlib.Path(entry.path).relative_to(path).as_posix())
            except OSError:
                continue
    return Leftover(total, count, tuple(sample))


def _locked(repo: str, path: pathlib.Path) -> Decision:
    found = leftover(path)
    if not found.files:
        # `rmdir /s` removes a junction without entering it, so with no files
        # left (what pnpm's junctions leave after `git worktree remove`) it can
        # only delete links and empty directories.
        return Decision(
            kind=Kind.LOCKED_PATH,
            repo=repo,
            path=str(path),
            reason="git no longer tracks it, and only links and empty directories remain",
            size_bytes=0,
            suggested_action="Delete the skeleton; it holds no files",
            command=f'cmd /c rmdir /s /q "{path}"',
        )
    more = f" and {found.files - len(found.sample)} more" if found.files > len(found.sample) else ""
    return Decision(
        kind=Kind.LOCKED_PATH,
        repo=repo,
        path=str(path),
        reason=(
            f"git no longer tracks it, but {found.files} file(s) remain: {', '.join(found.sample)}{more}. "
            "Some may be hand-written; git cannot restore them"
        ),
        size_bytes=found.bytes_used,
        suggested_action="List the files, keep anything hand-written, then delete the rest",
        command=f'cmd /c dir /s /b /a-d "{path}"',
    )


@dataclass(frozen=True, slots=True)
class _Verdict:
    """What the disk alone says about one worktree."""

    decision: Decision | None = None
    #: Idle, clean and pushed: only its PR state can make it a decision.
    needs_pull_request: bool = False


def _judge(repo: str, tree: Worktree, *, idle_days: int, now: float) -> _Verdict:
    if is_dubious(tree.path):
        return _Verdict(_dubious(repo, tree.path))
    if now - last_activity(tree.path) < idle_days * 86400:
        return _Verdict()
    orphaned = orphaned_count(tree.path)
    dirty = is_dirty(tree.path)
    if orphaned or dirty:
        return _Verdict(_unpushed(repo, tree, orphaned=orphaned, dirty=dirty, idle_days=idle_days))
    return _Verdict(needs_pull_request=not tree.locked)


def clone_decisions(
    clone: pathlib.Path,
    *,
    pull_requests: PullRequestLookup = github_pull_requests,
    idle_days: int = DEFAULT_IDLE_DAYS,
    now: float | None = None,
) -> list[Decision]:
    """The decisions one clone and its worktrees need.

    Worktrees are judged in parallel: each costs a few git calls and, when it
    becomes a decision, a walk of its files, and a work clone can hold a hundred.
    """
    repo = clone.name
    if is_dubious(clone):
        return [_dubious(repo, clone)]
    moment = time.time() if now is None else now
    trees = [t for t in list_worktrees(clone) if not (t.is_main or t.bare or t.prunable) and t.path.is_dir()]
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        verdicts = list(pool.map(partial(_judge, repo, idle_days=idle_days, now=moment), trees))
        out = [v.decision for v in verdicts if v.decision is not None]
        quiet = [t for t, v in zip(trees, verdicts, strict=True) if v.needs_pull_request]
        heads = pull_requests(clone) if quiet else None
        if heads is not None:
            found = pool.map(partial(_pull_request_decision, repo, clone, heads=heads, idle_days=idle_days), quiet)
            out += [d for d in found if d is not None]
        out += pool.map(partial(_locked, repo), left_behind(clone))
    return out


def collect(
    root: pathlib.Path,
    *,
    pull_requests: PullRequestLookup = github_pull_requests,
    idle_days: int = DEFAULT_IDLE_DAYS,
    now: float | None = None,
) -> list[Decision]:
    """Every decision under one code root, most urgent kind first, then biggest first."""
    out: list[Decision] = []
    for clone in discovery.scan(root):
        out += clone_decisions(clone, pull_requests=pull_requests, idle_days=idle_days, now=now)
    return ordered(out)


def collect_roots(
    roots: Iterable[pathlib.Path],
    *,
    pull_requests: PullRequestLookup = github_pull_requests,
    idle_days: int = DEFAULT_IDLE_DAYS,
    now: float | None = None,
) -> list[Decision]:
    """`collect` over several roots; a root that does not exist contributes nothing."""
    out: list[Decision] = []
    for root in roots:
        if root.is_dir():
            out += collect(root, pull_requests=pull_requests, idle_days=idle_days, now=now)
    unique = {os.path.normcase(d.path): d for d in out}
    return ordered(list(unique.values()))


def ordered(decisions: list[Decision]) -> list[Decision]:
    def rank(decision: Decision) -> int:
        try:
            return _ORDER.index(Kind(decision.kind))
        except ValueError:
            return len(_ORDER)

    return sorted(decisions, key=lambda d: (rank(d), -d.size_bytes, d.path))


def default_feed_path() -> pathlib.Path:
    """`%LOCALAPPDATA%\\shared-utilities\\jobs\\decisions.json`, beside the jobs coordinator's state."""
    base = os.environ.get("LOCALAPPDATA") or str(pathlib.Path.home() / "AppData" / "Local")
    return pathlib.Path(base) / "shared-utilities" / "jobs" / FEED_NAME


_FEED: TypeAdapter[list[Decision]] = TypeAdapter(list[Decision])


def dump_feed(decisions: list[Decision]) -> str:
    """The feed as a JSON array of `Decision` objects."""
    return _FEED.dump_json(decisions, indent=2).decode("utf-8")


def write_feed(path: pathlib.Path, decisions: list[Decision]) -> None:
    """Replace the feed at *path* in one step, so a reader never sees half of it."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    _ = temporary.write_text(dump_feed(decisions) + "\n", encoding="utf-8")
    _ = temporary.replace(path)


def read_feed(path: pathlib.Path) -> list[Decision] | None:
    """The feed at *path*; None when it is missing or unreadable."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    return try_decode_json(list[Decision], text)
