"""Gather every source and join them into a `WorkReport`. The only module that wires IO to `join`.

Two overlaps keep the wall-clock down. Claude sessions are read while git reads refs, since
neither needs the other. Once refs and sessions are in, `git status` per checkout (the
slowest phase, disk-bound) runs alongside the GitHub lookups, which only need branch names.
"""

from __future__ import annotations

import datetime as dt
import time
from collections.abc import Callable
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass, field
from functools import cache
from pathlib import Path
from threading import Lock

from desktop_manager.work import git_facts, join, prs, sessions
from desktop_manager.work.model import Attribution, PrLookup, RepoFacts, SessionFacts, WorkReport


@dataclass(frozen=True, slots=True)
class CollectOptions:
    root: Path
    session_window: dt.timedelta = sessions.DEFAULT_WINDOW
    stale_after: dt.timedelta = join.DEFAULT_STALE_AFTER
    #: Skip GitHub entirely.
    offline: bool = False
    #: Ignore the PR cache and the clone-list cache.
    refresh: bool = False
    #: Run `git status` per checkout for dirty state.
    probe: bool = True


@dataclass(slots=True)
class Timings:
    """Seconds per phase. Phases can overlap, so they need not add up to the wall clock."""

    phases: dict[str, float] = field(default_factory=dict[str, float])
    _lock: Lock = field(default_factory=Lock)

    def measure[T](self, name: str, work: Callable[[], T]) -> T:
        started = time.perf_counter()
        try:
            return work()
        finally:
            elapsed = time.perf_counter() - started
            with self._lock:
                self.phases[name] = elapsed


@dataclass(frozen=True, slots=True)
class Collected:
    report: WorkReport
    timings: Timings
    #: Wall-clock seconds for the whole collection.
    total: float = 0.0


def pr_branches(repos: list[RepoFacts], extra: dict[Path, set[str]]) -> dict[Path, tuple[str, ...]]:
    """Non-trunk branches per estate repo: those checked out, plus those holding a session."""
    wanted: dict[Path, tuple[str, ...]] = {}
    for repo in repos:
        branches = {c.branch for c in repo.checkouts if c.branch}
        branches |= extra.get(repo.path, set())
        branches.discard(repo.default_branch)
        branches.discard(join.DETACHED_BRANCH)
        if branches:
            wanted[repo.path] = tuple(sorted(branches))
    return wanted


def _lookup_prs(
    repos: list[RepoFacts],
    session_branches: dict[Path, set[str]],
    options: CollectOptions,
    now: dt.datetime,
    timings: Timings,
) -> prs.PrResults:
    wanted = pr_branches(repos, session_branches)
    slugs = timings.measure("pr slugs", lambda: git_facts.read_slugs(wanted))
    requests = [prs.PrRequest(repo_path=path, slug=slugs[path], branches=branches) for path, branches in wanted.items()]
    return timings.measure("github", lambda: prs.fetch_prs(requests, refresh=options.refresh, now=now))


def _is_dir(path: str) -> bool:
    return Path(path).is_dir()


def collect(options: CollectOptions, *, now: dt.datetime | None = None) -> Collected:
    started = time.perf_counter()
    moment = now or dt.datetime.now(dt.UTC)
    timings = Timings()
    with ThreadPoolExecutor(max_workers=2) as pool:

        def _scan_sessions() -> sessions.SessionScan:
            return timings.measure("sessions", lambda: sessions.read_sessions(since=moment - options.session_window))

        scan_future = pool.submit(_scan_sessions)
        repos = timings.measure("git refs", lambda: git_facts.read_estate(options.root, refresh=options.refresh))
        scan = scan_future.result()

        index = join.CheckoutIndex(repos)
        resolve = cache(sessions.resolve_folder)
        exists = cache(_is_dir)

        def _attribute(s: SessionFacts) -> Attribution:
            return join.attribute(s, index, exists=exists, resolve_unknown=resolve)

        attributed = timings.measure(
            "attribute", lambda: [join.AttributedSession(session=s, attribution=_attribute(s)) for s in scan.sessions]
        )
        session_branches: dict[Path, set[str]] = {}
        for a in attributed:
            if a.attribution.repo_path is not None and a.attribution.branch:
                session_branches.setdefault(a.attribution.repo_path, set()).add(a.attribution.branch)

        status_future: Future[list[RepoFacts]] | None = None
        if options.probe:

            def _probe_status() -> list[RepoFacts]:
                return timings.measure("git status", lambda: git_facts.probe_checkouts(repos))

            status_future = pool.submit(_probe_status)
        if options.offline:
            pr_results = prs.PrResults(prs={}, lookups={}, warnings=())
        else:
            pr_results = _lookup_prs(repos, session_branches, options, moment, timings)
        if status_future is not None:
            repos = status_future.result()

    lookups: dict[Path, PrLookup] = dict(pr_results.lookups)
    items = join.build_items(
        join.JoinInputs(repos=repos, sessions=attributed, prs=pr_results.prs, pr_lookups=lookups),
        now=moment,
        stale_after=options.stale_after,
    )
    report = WorkReport(
        generated_at=moment, code_root=options.root, items=tuple(items), warnings=tuple(pr_results.warnings)
    )
    return Collected(report=report, timings=timings, total=time.perf_counter() - started)
