"""One observation of a pull request, and the pure decision of whether it can still land.

No I/O: `github` observes, this module decides, `wait` polls and `cli` renders.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from enum import StrEnum

#: At most this many names are spelled out in a message; the rest are counted.
_NAMES_SHOWN = 5

#: Check-run conclusions and commit-status states that mean the check will not pass without a rerun or a push.
FAILED_RESULTS = frozenset({"FAILURE", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR"})
#: Commit-status states that are still running. A check run is pending whenever its status is not COMPLETED.
PENDING_STATES = frozenset({"PENDING", "EXPECTED"})
#: Merge states in which GitHub would merge (or enqueue) the PR itself once auto-merge is on.
_READY_STATES = frozenset({"CLEAN", "UNSTABLE", "HAS_HOOKS"})


class CheckState(StrEnum):
    PENDING = "pending"
    PASSED = "passed"
    FAILED = "failed"


class Kind(StrEnum):
    """What a verdict means for the wait."""

    MERGED = "merged"
    #: Still moving: keep polling.
    WAITING = "waiting"
    #: Cannot land without someone acting: stop now.
    STUCK = "stuck"
    #: Looks stuck, but GitHub may only be lagging: stop if it still looks this way after the settle time.
    UNSETTLED = "unsettled"


class Reason(StrEnum):
    MERGED = "merged"
    # waiting
    CHECKS_PENDING = "checks_pending"
    IN_MERGE_QUEUE = "in_merge_queue"
    MERGE_STATE_UNKNOWN = "merge_state_unknown"
    READY = "ready"
    # stuck
    CLOSED = "closed"
    DRAFT = "draft"
    CHECK_FAILED = "check_failed"
    CHANGES_REQUESTED = "changes_requested"
    REVIEW_REQUIRED = "review_required"
    UNRESOLVED_THREADS = "unresolved_threads"
    CONFLICTS = "conflicts"
    # stuck once settled
    REMOVED_FROM_MERGE_QUEUE = "removed_from_merge_queue"
    AUTO_MERGE_OFF = "auto_merge_off"
    BEHIND = "behind"
    BLOCKED = "blocked"


@dataclass(frozen=True, slots=True)
class Check:
    """One check run or commit status on the PR's head commit. ``result`` is GitHub's own word for it."""

    name: str
    state: CheckState
    required: bool
    result: str


@dataclass(frozen=True, slots=True)
class QueueEntry:
    """The PR's place in the merge queue. ``position`` counts from 1 at the front."""

    position: int
    state: str


@dataclass(frozen=True, slots=True)
class Snapshot:
    """The pull request as one GraphQL read sees it."""

    state: str
    draft: bool
    mergeable: str
    merge_state: str
    review_decision: str
    auto_merge: bool
    queue: QueueEntry | None
    checks: tuple[Check, ...]
    base: str
    url: str = ""
    title: str = ""
    #: Classic branch protection on the base branch requires resolved conversations.
    classic_thread_resolution: bool = False
    #: Check names classic branch protection requires.
    classic_required_checks: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class Rules:
    """What the base branch's rulesets demand. ``thread_resolution`` is None when they could not be read."""

    thread_resolution: bool | None
    required_checks: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class ThreadRef:
    """An unresolved review thread, as much of it as a blocked report needs."""

    id: str
    location: str
    author: str
    summary: str
    url: str


@dataclass(frozen=True, slots=True)
class Observation:
    """Everything one poll saw. ``threads`` holds the unresolved review threads only."""

    snapshot: Snapshot
    rules: Rules
    threads: tuple[ThreadRef, ...] = ()

    @property
    def threads_required(self) -> bool | None:
        """Whether unresolved threads block the merge; None when the rulesets could not be read."""
        if self.snapshot.classic_thread_resolution:
            return True
        return self.rules.thread_resolution

    @property
    def missing_checks(self) -> tuple[str, ...]:
        """Required checks that have not reported on the head commit yet, so the rollup does not list them."""
        reported = {check.name for check in self.snapshot.checks}
        required = dict.fromkeys((*self.rules.required_checks, *self.snapshot.classic_required_checks))
        return tuple(name for name in required if name not in reported)

    @property
    def pending_checks(self) -> tuple[str, ...]:
        running = tuple(check.name for check in self.snapshot.checks if check.state is CheckState.PENDING)
        return (*running, *self.missing_checks)

    @property
    def failed_checks(self) -> tuple[str, ...]:
        """Required checks that failed or were cancelled."""
        return tuple(c.name for c in self.snapshot.checks if c.required and c.state is CheckState.FAILED)


@dataclass(frozen=True, slots=True)
class Verdict:
    kind: Kind
    reason: Reason
    message: str


def _names(names: Sequence[str]) -> str:
    shown = ", ".join(names[:_NAMES_SHOWN])
    return f"{shown} and {len(names) - _NAMES_SHOWN} more" if len(names) > _NAMES_SHOWN else shown


def _stuck(reason: Reason, message: str) -> Verdict:
    return Verdict(Kind.STUCK, reason, message)


def _waiting(reason: Reason, message: str) -> Verdict:
    return Verdict(Kind.WAITING, reason, message)


def _unsettled(reason: Reason, message: str) -> Verdict:
    return Verdict(Kind.UNSETTLED, reason, message)


def assess(seen: Observation, *, was_queued: bool = False) -> Verdict:
    """Decide whether the PR has landed, is still moving, or cannot land without someone acting.

    A cause GitHub names outright (a failed required check, an open thread the ruleset wants resolved,
    a requested change, a conflict) is `Kind.STUCK` at once. A state that is stuck only if it lasts
    (BLOCKED or BEHIND with nothing pending, auto-merge gone) is `Kind.UNSETTLED`, because GitHub takes
    a moment between the last check passing and merging or enqueuing the PR. *was_queued* says an
    earlier poll saw the PR in the merge queue, which is how leaving the queue is told from never
    having joined it.
    """
    pr = seen.snapshot
    if pr.state == "MERGED":
        return Verdict(Kind.MERGED, Reason.MERGED, "merged")
    if pr.state != "OPEN":
        return _stuck(Reason.CLOSED, "the pull request was closed without merging")
    named = _named_blocker(seen)
    if named is not None:
        return named
    if pr.queue is not None:
        return _waiting(Reason.IN_MERGE_QUEUE, f"in the merge queue at position {pr.queue.position} ({pr.queue.state})")
    if was_queued:
        return _unsettled(
            Reason.REMOVED_FROM_MERGE_QUEUE,
            "removed from the merge queue (its merge-group checks failed, or someone dequeued it); requeue it",
        )
    if not pr.auto_merge:
        ready = " although it is ready to merge" if pr.merge_state in _READY_STATES else ""
        return _unsettled(Reason.AUTO_MERGE_OFF, f"auto-merge is off{ready}, so nothing will merge it")
    pending = seen.pending_checks
    if pending:
        return _waiting(Reason.CHECKS_PENDING, f"{len(pending)} check(s) pending: {_names(pending)}")
    return _settled_state(seen)


def _named_blocker(seen: Observation) -> Verdict | None:
    """A cause that stops the PR whatever else is still running."""
    pr = seen.snapshot
    if pr.draft:
        return _stuck(Reason.DRAFT, "the pull request is a draft; mark it ready for review")
    failed = seen.failed_checks
    if failed:
        return _stuck(
            Reason.CHECK_FAILED, f"{len(failed)} required check(s) failed or were cancelled: {_names(failed)}"
        )
    if pr.review_decision == "CHANGES_REQUESTED":
        return _stuck(Reason.CHANGES_REQUESTED, "a reviewer requested changes")
    if seen.threads and seen.threads_required:
        return _stuck(
            Reason.UNRESOLVED_THREADS,
            f"{len(seen.threads)} unresolved review thread(s), and the ruleset requires every one resolved",
        )
    if pr.mergeable == "CONFLICTING" or pr.merge_state == "DIRTY":
        return _stuck(Reason.CONFLICTS, f"merge conflicts with {pr.base}; rebase the branch")
    if pr.review_decision == "REVIEW_REQUIRED":
        return _stuck(Reason.REVIEW_REQUIRED, "an approving review is required and has not been given")
    return None


def _settled_state(seen: Observation) -> Verdict:
    """The verdict once auto-merge is on and every check has finished: only the merge state is left to read."""
    pr = seen.snapshot
    if pr.merge_state == "BLOCKED" and seen.threads and seen.threads_required is None:
        return _stuck(
            Reason.UNRESOLVED_THREADS,
            f"blocked with {len(seen.threads)} unresolved review thread(s) and nothing pending "
            "(the base branch's rules could not be read, so this is the likely cause)",
        )
    if pr.merge_state == "BEHIND":
        return _unsettled(Reason.BEHIND, f"the branch is behind {pr.base} and no check is pending; update it")
    if pr.merge_state == "BLOCKED":
        return _unsettled(
            Reason.BLOCKED,
            "GitHub reports the merge BLOCKED with no check pending and no cause this command can name; "
            "open the PR to see which requirement is unmet",
        )
    if pr.merge_state in _READY_STATES:
        return _waiting(Reason.READY, "every requirement is met; waiting for GitHub to merge or enqueue it")
    return _waiting(Reason.MERGE_STATE_UNKNOWN, "GitHub is still working out whether the PR can merge")
