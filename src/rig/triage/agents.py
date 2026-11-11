"""What the agent working on a PR last said about it, from `estate wrapup`'s state files.

Each worktree of each managed repo may hold `temp/.rig/wrapup.json` (docs/estate/wrapup.md#the-state-file).
A PR is matched to it by repo and head branch. The latest event wins: a reply says `done` or `pending`;
an ask with no reply since says `asked`. Nothing here talks to Orca or GitHub: it reads local files only,
so the inbox shows the verdicts even when Orca is closed.
"""

from __future__ import annotations

import datetime as dt
import threading
import time
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

from rig.estate.checks import list_worktrees
from rig.estate.wrapup_state import StateReader, WrapupState, default_store, parse_timestamp
from rig.gh_origin import get_repo_root_and_origin
from rig.repo_slug import RepoSlug
from rig.workspace.engine import config_file_path, estate_root_path, load_managed_specs
from rig.workspace.layout import project_dir

Verdict = Literal["done", "pending", "asked"]

#: Seconds a scan is reused: the inbox polls, and each scan runs `git worktree list` per repo.
TTL_SECONDS = 30.0


@dataclass(frozen=True, slots=True)
class AgentSignal:
    verdict: Verdict
    #: The pending reason, or the facts line rig sent with an unanswered ask.
    detail: str
    #: When the verdict was given (or the ask sent), UTC ISO 8601.
    at: str
    #: Commit sha the worktree was at then; the inbox compares it with the PR's head.
    head: str
    #: Folder name of the worktree, to find the session.
    worktree: str


@dataclass(frozen=True, slots=True)
class Key:
    """A repo (lowercase slug) and a branch in it."""

    repo: str
    branch: str


_EPOCH = dt.datetime.min.replace(tzinfo=dt.UTC)


def _slug(remote: str) -> str:
    identity = RepoSlug.from_remote(remote)
    return f"{identity.owner}/{identity.name}".lower() if identity else remote.lower().removesuffix(".git")


def _when(text: str) -> dt.datetime:
    return parse_timestamp(text) or _EPOCH


def latest(state: WrapupState, worktree: str) -> AgentSignal | None:
    """The newest ask or answer in *state*, across its panes."""
    ask = max(state.asks, key=lambda a: _when(a.at), default=None)
    answer = max(state.answers, key=lambda a: _when(a.at), default=None)
    if answer is not None and (ask is None or _when(answer.at) >= _when(ask.at)):
        verdict: Verdict = "done" if answer.verdict == "done" else "pending"
        return AgentSignal(verdict, answer.detail, answer.at, answer.head, worktree)
    if ask is not None:
        return AgentSignal("asked", ask.facts, ask.at, ask.head, worktree)
    return None


def scan(roots: Mapping[str, Path], read: StateReader) -> dict[Key, AgentSignal]:
    """Each repo's worktrees with a wrap-up record, keyed by (repo slug, branch)."""
    found: dict[Key, AgentSignal] = {}
    for repo, root in roots.items():
        if not root.is_dir():
            continue
        for worktree in list_worktrees(root):
            if not worktree.branch or worktree.bare:
                continue
            state = read(worktree.path)
            signal = latest(state, worktree.path.name) if state else None
            if signal is not None:
                found[Key(repo, worktree.branch)] = signal
    return found


def estate_roots(config: Path | None = None) -> dict[str, Path]:
    """Local checkout of each managed repo, keyed by lowercase slug. The running checkout stands in for its own."""
    cfg = config or config_file_path()
    estate = estate_root_path()
    current = get_repo_root_and_origin(cfg.parent.parent)
    current_slug = _slug(current.origin_url) if current.origin_url else ""
    return {
        _slug(spec.repo): cfg.parent.parent
        if _slug(spec.repo) == current_slug
        else project_dir(estate, spec.name, spec.repo)
        for spec in load_managed_specs(cfg)
    }


class _Cache:
    def __init__(self) -> None:
        self.lock: threading.Lock = threading.Lock()
        self.at: float = float("-inf")
        self.value: dict[Key, AgentSignal] = {}


_cache = _Cache()


def signals(
    *,
    roots: Callable[[], Mapping[str, Path]] = estate_roots,
    clock: Callable[[], float] = time.monotonic,
) -> dict[Key, AgentSignal]:
    """`scan` over the estate, reused for `TTL_SECONDS`."""
    with _cache.lock:
        if clock() - _cache.at >= TTL_SECONDS:
            _cache.value = scan(roots(), default_store().read)
            _cache.at = clock()
        return _cache.value


def for_pr(found: Mapping[Key, AgentSignal], repo: str, branch: str) -> AgentSignal | None:
    return found.get(Key(repo.lower(), branch)) if branch else None
