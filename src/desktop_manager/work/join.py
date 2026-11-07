"""Pure joining logic: attribute sessions, build one item per (repo, branch), derive its state.

No subprocess, no filesystem and no network here. The few facts that need the outside
world (does a directory exist, what repo is an unknown folder in) arrive as callables, so
every rule is testable with fakes.
"""

from __future__ import annotations

import datetime as dt
import os
import re
from collections import defaultdict
from collections.abc import Callable, Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.work.model import (
    RUNNING_STATES,
    Attribution,
    BranchKey,
    BranchRef,
    Checkout,
    PrInfo,
    PrLookup,
    PrStatus,
    RepoFacts,
    SessionFacts,
    SessionState,
    WorkItem,
    WorkState,
)

DEFAULT_STALE_AFTER = dt.timedelta(days=14)
UNATTACHED_KEY = "unattached"
DETACHED_BRANCH = "(detached)"

#: A ticket id as written: PROJ-1234, ABC-935, XY-802.
_TICKET = re.compile(r"(?<![A-Za-z0-9])([A-Z][A-Z0-9]+-\d+)(?![0-9])")
#: Branch names are often lower case (`proj-3583-thing`). Accepted more strictly: a 2-6 letter
#: project key and at least two digits, so `v-1` and `fix-1` do not read as tickets.
_LOWER_TICKET = re.compile(r"(?<![A-Za-z0-9])([A-Za-z]{2,6}-\d{2,})(?![0-9])")
#: Words that prefix a number in branch names without being a project key.
_NOT_A_PROJECT = frozenset(
    {"pr", "clone", "issue", "fix", "step", "part", "rc", "stack", "layer", "phase", "wave", "round", "deploy", "wt"}
)

#: Display order of states, most deserving of attention first.
STATE_ORDER: tuple[WorkState, ...] = (
    WorkState.ACTIVE,
    WorkState.MERGED,
    WorkState.CLOSED,
    WorkState.STALE,
    WorkState.TRUNK,
    WorkState.UNATTACHED,
)


def parse_ticket(branch: str | None) -> str | None:
    """The first ticket id in a branch name, upper-cased: `feat/PROJ-1234-thing` and `proj-1234-thing` -> `PROJ-1234`.

    An upper-case id wins over a lower-case one anywhere in the name. Either way the project
    key must not be a common word such as `pr`, `clone` or `fix` (see `_NOT_A_PROJECT`).
    """
    if not branch:
        return None
    for pattern in (_TICKET, _LOWER_TICKET):
        for match in pattern.finditer(branch):
            ticket = match[1].upper()
            if ticket.partition("-")[0].lower() not in _NOT_A_PROJECT:
                return ticket
    return None


# -- sessions -------------------------------------------------------------------


def classify_session(
    *,
    live_status: str | None,
    is_live: bool,
    cleared: bool,
    exited_cleanly: bool,
    turn_state: str,
) -> SessionState:
    """One session's state, from the live registry, the end ledger and the transcript tail.

    A live registry entry wins over everything: the process is running. Its `status` is
    `busy` or `idle`; idle means it is sitting at the prompt waiting for the user.
    """
    if is_live:
        return SessionState.WAITING_ON_USER if live_status == "idle" else SessionState.LIVE
    if cleared:
        return SessionState.CLEARED
    if exited_cleanly:
        return SessionState.ENDED_CLEAN
    if turn_state in ("mid-tool", "unanswered"):
        return SessionState.DIED_MID_TASK
    return SessionState.DIED_IDLE


def normalise(path: str | Path) -> str:
    """A path in the form used for prefix comparison: absolute-looking, case- and slash-folded on Windows."""
    return os.path.normcase(os.path.normpath(str(path)))


@dataclass(frozen=True, slots=True)
class _Located:
    repo_path: Path
    branch: str
    normalised: str


class CheckoutIndex:
    """Every known checkout path, for longest-prefix lookup of a session's cwd."""

    def __init__(self, repos: Iterable[RepoFacts]) -> None:
        located = [
            _Located(repo.path, checkout.branch or DETACHED_BRANCH, normalise(checkout.path))
            for repo in repos
            for checkout in repo.checkouts
        ]
        # Longest first, so a worktree at <repo>/temp/<name> wins over the clone that contains it.
        self._located: list[_Located] = sorted(located, key=lambda entry: len(entry.normalised), reverse=True)

    def find(self, cwd: str) -> _Located | None:
        target = normalise(cwd)
        for entry in self._located:
            if target == entry.normalised or target.startswith(entry.normalised.rstrip(os.sep) + os.sep):
                return entry
        return None


type FolderResolver = Callable[[str], Attribution | None]


def attribute(
    session: SessionFacts,
    index: CheckoutIndex,
    *,
    exists: Callable[[str], bool],
    resolve_unknown: FolderResolver,
) -> Attribution:
    """Which (repo, branch) a session belongs to, judged by its LAST working directory.

    The branch is the one checked out there *now*: a session that switched branches in
    place is credited to whatever the checkout holds today. A directory that no longer
    exists (a removed worktree) goes to the unattached bucket rather than being credited,
    by prefix, to the clone that used to contain it.
    """
    cwd = session.last_cwd or session.start_cwd
    if not cwd:
        return Attribution(repo_path=None, reason="no working directory recorded")
    if not exists(cwd):
        return Attribution(repo_path=None, reason=f"directory gone: {cwd}")
    found = index.find(cwd)
    if found is not None:
        return Attribution(repo_path=found.repo_path, branch=found.branch)
    resolved = resolve_unknown(cwd)
    if resolved is not None:
        return resolved
    return Attribution(repo_path=None, reason=f"not in a git checkout: {cwd}")


# -- state ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class StateDecision:
    state: WorkState
    reasons: tuple[str, ...]


def _ago(delta: dt.timedelta) -> str:
    days = delta.days
    if days >= 1:
        return f"{days}d"
    hours = int(delta.total_seconds() // 3600)
    return f"{hours}h" if hours >= 1 else f"{int(delta.total_seconds() // 60)}m"


@dataclass(frozen=True, slots=True)
class _Signals:
    """Each fact that keeps a branch active, as a reason string, or None when absent."""

    open_pr: str | None
    running: str | None
    dirty: str | None
    unpushed: str | None

    def all(self) -> tuple[str, ...]:
        return tuple(s for s in (self.open_pr, self.running, self.dirty, self.unpushed) if s)

    def cleanup_blockers(self) -> tuple[str, ...]:
        """What stops a merged branch being tidied. Unpushed commits do not: a squash merge strands them."""
        return tuple(s for s in (self.running, self.dirty) if s)


def _plural(count: int, noun: str) -> str:
    return f"{count} {noun}{'s' if count != 1 else ''}"


def _signals(
    pr: PrInfo | None,
    checkouts: Sequence[Checkout],
    sessions: Sequence[SessionFacts],
    branch_ref: BranchRef | None,
) -> _Signals:
    running = sum(1 for s in sessions if s.state in RUNNING_STATES)
    unpushed = branch_ref.unpushed if branch_ref else 0
    return _Signals(
        open_pr=f"{pr.status.value} PR #{pr.number}" if pr and pr.status in (PrStatus.OPEN, PrStatus.DRAFT) else None,
        running=_plural(running, "running session") if running else None,
        dirty="uncommitted changes" if any(c.dirty for c in checkouts) else None,
        unpushed=_plural(unpushed, "unpushed commit") if unpushed else None,
    )


def derive_state(
    *,
    is_trunk: bool,
    pr: PrInfo | None,
    checkouts: Sequence[Checkout],
    sessions: Sequence[SessionFacts],
    branch_ref: BranchRef | None,
    last_activity: dt.datetime | None,
    now: dt.datetime,
    stale_after: dt.timedelta = DEFAULT_STALE_AFTER,
) -> StateDecision:
    """The lifecycle state, first rule that matches:

    1. **trunk** - the repo's default branch. It is home, never finished or stale.
    2. **merged** - the branch's newest PR merged. A cleanup candidate; the reasons list
       anything that blocks cleanup (uncommitted changes, a running session).
    3. **active** - an open or draft PR, a running session, uncommitted changes, or
       commits on no remote.
    4. **closed** - the newest PR was closed without merging, and nothing above applies.
    5. **active** - any activity (commit, session, PR update) within `stale_after`.
    6. **stale** - otherwise.
    """
    signals = _signals(pr, checkouts, sessions, branch_ref)
    if is_trunk:
        return StateDecision(WorkState.TRUNK, ("default branch", *signals.all()))
    if pr is not None and pr.status is PrStatus.MERGED:
        return StateDecision(WorkState.MERGED, (f"PR #{pr.number} merged", *signals.cleanup_blockers()))
    if signals.all():
        return StateDecision(WorkState.ACTIVE, signals.all())
    gone = ("upstream gone",) if branch_ref is not None and branch_ref.upstream_gone else ()
    if pr is not None and pr.status is PrStatus.CLOSED:
        return StateDecision(WorkState.CLOSED, (f"PR #{pr.number} closed unmerged", *gone))
    if last_activity is not None and now - last_activity <= stale_after:
        return StateDecision(WorkState.ACTIVE, (f"activity {_ago(now - last_activity)} ago", *gone))
    if last_activity is None:
        return StateDecision(WorkState.STALE, ("no activity recorded", *gone))
    return StateDecision(WorkState.STALE, (f"no activity for {_ago(now - last_activity)}", *gone))


# -- building items -------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class AttributedSession:
    session: SessionFacts
    attribution: Attribution


@dataclass(frozen=True, slots=True)
class JoinInputs:
    repos: Sequence[RepoFacts]
    sessions: Sequence[AttributedSession]
    prs: Mapping[BranchKey, PrInfo]
    #: Per repo path. A repo missing here was not asked about.
    pr_lookups: Mapping[Path, PrLookup]


def _latest(*moments: dt.datetime | None) -> dt.datetime | None:
    present = [m for m in moments if m is not None]
    return max(present) if present else None


def _session_sort_key(session: SessionFacts) -> dt.datetime:
    return session.last_activity or dt.datetime.min.replace(tzinfo=dt.UTC)


@dataclass(frozen=True, slots=True)
class _RepoView:
    path: Path
    name: str
    default_branch: str
    checkouts: tuple[Checkout, ...]
    branches: Mapping[str, BranchRef]


def _views(inputs: JoinInputs) -> dict[Path, _RepoView]:
    views = {
        repo.path: _RepoView(
            path=repo.path,
            name=repo.name,
            default_branch=repo.default_branch,
            checkouts=repo.checkouts,
            branches={b.name: b for b in repo.branches},
        )
        for repo in inputs.repos
    }
    # A session can sit in a checkout outside the code root; give its repo a minimal view.
    for attributed in inputs.sessions:
        path = attributed.attribution.repo_path
        if path is not None and path not in views:
            views[path] = _RepoView(path=path, name=path.name, default_branch="", checkouts=(), branches={})
    return views


def _keys(inputs: JoinInputs) -> list[BranchKey]:
    seen: dict[BranchKey, None] = {}
    for repo in inputs.repos:
        for checkout in repo.checkouts:
            seen[BranchKey(repo.path, checkout.branch or DETACHED_BRANCH)] = None
    for attributed in inputs.sessions:
        path = attributed.attribution.repo_path
        if path is not None:
            seen[BranchKey(path, attributed.attribution.branch or DETACHED_BRANCH)] = None
    return list(seen)


def _is_trunk(view: _RepoView, branch: str) -> bool:
    if view.default_branch:
        return branch == view.default_branch
    return branch in ("main", "master")


def build_items(
    inputs: JoinInputs,
    *,
    now: dt.datetime,
    stale_after: dt.timedelta = DEFAULT_STALE_AFTER,
) -> list[WorkItem]:
    """One item per (repo, branch) checked out anywhere or holding a session, plus the unattached bucket."""
    views = _views(inputs)
    by_key: dict[BranchKey, list[SessionFacts]] = defaultdict(list)
    unattached: list[SessionFacts] = []
    for attributed in inputs.sessions:
        path = attributed.attribution.repo_path
        if path is None:
            unattached.append(attributed.session)
        else:
            by_key[BranchKey(path, attributed.attribution.branch or DETACHED_BRANCH)].append(attributed.session)

    items = [_item(key, views[key.repo_path], inputs, by_key.get(key, []), now, stale_after) for key in _keys(inputs)]
    if unattached:
        items.append(_unattached_item(unattached))
    return sort_items(items)


def _item(
    key: BranchKey,
    view: _RepoView,
    inputs: JoinInputs,
    sessions: list[SessionFacts],
    now: dt.datetime,
    stale_after: dt.timedelta,
) -> WorkItem:
    branch = key.branch
    detached = branch == DETACHED_BRANCH
    checkouts = tuple(c for c in view.checkouts if (c.branch or DETACHED_BRANCH) == branch)
    ordered = tuple(sorted(sessions, key=_session_sort_key, reverse=True))
    branch_ref = None if detached else view.branches.get(branch)
    is_trunk = not detached and _is_trunk(view, branch)
    pr = inputs.prs.get(key)
    lookup = PrLookup.SKIPPED if is_trunk or detached else inputs.pr_lookups.get(view.path, PrLookup.SKIPPED)
    last_activity = _latest(
        branch_ref.last_commit if branch_ref else None,
        ordered[0].last_activity if ordered else None,
        pr.updated_at if pr else None,
    )
    decision = derive_state(
        is_trunk=is_trunk,
        pr=pr,
        checkouts=checkouts,
        sessions=ordered,
        branch_ref=branch_ref,
        last_activity=last_activity,
        now=now,
        stale_after=stale_after,
    )
    return WorkItem(
        key=f"{view.name}:{branch}",
        repo=view.name,
        repo_path=view.path,
        branch=branch,
        ticket=parse_ticket(branch),
        is_trunk=is_trunk,
        state=decision.state,
        reasons=decision.reasons,
        checkouts=checkouts,
        branch_ref=branch_ref,
        pr=pr,
        pr_lookup=lookup,
        sessions=ordered,
        last_activity=last_activity,
    )


def _unattached_item(sessions: list[SessionFacts]) -> WorkItem:
    ordered = tuple(sorted(sessions, key=_session_sort_key, reverse=True))
    return WorkItem(
        key=UNATTACHED_KEY,
        repo=None,
        repo_path=None,
        branch=None,
        ticket=None,
        is_trunk=False,
        state=WorkState.UNATTACHED,
        reasons=("sessions outside any known checkout",),
        checkouts=(),
        branch_ref=None,
        pr=None,
        pr_lookup=PrLookup.SKIPPED,
        sessions=ordered,
        last_activity=ordered[0].last_activity if ordered else None,
    )


def sort_items(items: Iterable[WorkItem]) -> list[WorkItem]:
    """State order first, then most recent activity first, then key for stability."""
    epoch = dt.datetime.min.replace(tzinfo=dt.UTC)
    by_recency = sorted(items, key=lambda item: item.key)
    by_recency.sort(key=lambda item: item.last_activity or epoch, reverse=True)
    by_recency.sort(key=lambda item: STATE_ORDER.index(item.state))
    return by_recency


# -- filtering ------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class ItemFilter:
    repo: str | None = None
    states: frozenset[WorkState] = frozenset()
    since: dt.datetime | None = None
    #: Include trunk items with nothing going on: clean, no sessions in the window.
    include_quiet_trunk: bool = False


def is_quiet_trunk(item: WorkItem) -> bool:
    return item.state is WorkState.TRUNK and not item.sessions and not item.dirty


def _repo_matches(item: WorkItem, needle: str) -> bool:
    if item.repo is None:
        return False
    lowered = needle.lower()
    return lowered in item.repo.lower()


def filter_items(items: Iterable[WorkItem], spec: ItemFilter) -> list[WorkItem]:
    kept: list[WorkItem] = []
    for item in items:
        if spec.repo and not _repo_matches(item, spec.repo):
            continue
        if spec.states and item.state not in spec.states:
            continue
        if spec.since is not None and (item.last_activity is None or item.last_activity < spec.since):
            continue
        if not spec.include_quiet_trunk and not spec.states and is_quiet_trunk(item):
            continue
        kept.append(item)
    return kept


def count_states(items: Iterable[WorkItem]) -> dict[WorkState, int]:
    counts = dict.fromkeys(STATE_ORDER, 0)
    for item in items:
        counts[item.state] += 1
    return counts
