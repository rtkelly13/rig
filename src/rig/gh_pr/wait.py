"""The poll loop behind `rig gh pr wait`: observe, assess, back off, until the outcome is known.

Time comes from an injected `Clock`, so tests run the whole loop without sleeping.
"""

from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass
from enum import StrEnum

from rig.gh_pr.model import Kind, Observation, Reason, Verdict, assess
from rig.gh_threads.github import GitHubError

#: `Result.reason` when GitHub could not be read at all.
GITHUB_ERROR = "github_error"
#: An unsettled verdict is rechecked at least this often.
_SETTLE_POLL = 5.0


class Outcome(StrEnum):
    MERGED = "merged"
    BLOCKED = "blocked"
    TIMEOUT = "timeout"
    ERROR = "error"


@dataclass(frozen=True, slots=True)
class Clock:
    """Time, injectable so tests never sleep."""

    monotonic: Callable[[], float] = time.monotonic
    sleep: Callable[[float], None] = time.sleep


@dataclass(frozen=True, slots=True)
class Pace:
    """How long to wait, and how often to look. All in seconds."""

    #: Give up after this long. Zero looks once and reports.
    timeout: float = 3600.0
    #: How long a state that is stuck only if it lasts must last before the wait stops.
    settle: float = 30.0
    #: The gap after a poll that saw a change; each quiet poll multiplies it by `growth`, up to `max_poll`.
    first_poll: float = 5.0
    max_poll: float = 60.0
    growth: float = 1.5
    #: Consecutive failed reads of GitHub tolerated mid-wait. The very first read is never retried.
    max_errors: int = 5


@dataclass(frozen=True, slots=True)
class Tick:
    """What the wait is waiting on, reported whenever it changes."""

    elapsed: float
    message: str
    next_poll: float


@dataclass(frozen=True, slots=True)
class Result:
    outcome: Outcome
    #: A `rig.gh_pr.model.Reason` value, or `GITHUB_ERROR`.
    reason: str
    message: str
    waited: float
    polls: int
    #: The last successful observation; None when GitHub was never read.
    last: Observation | None


type Observer = Callable[[], Observation]
type Reporter = Callable[[Tick], None]


@dataclass(slots=True)
class _Unsettled:
    reason: Reason
    since: float


def wait(observe: Observer, pace: Pace, clock: Clock, report: Reporter) -> Result:
    """Poll until the PR merges, cannot land without someone acting, or *pace.timeout* passes.

    The gap between polls starts at ``first_poll``, grows while nothing changes and resets when
    something does, so a change is seen quickly and a long quiet wait costs few requests.
    """
    start = clock.monotonic()
    delay = pace.first_poll
    polls = 0
    errors = 0
    was_queued = False
    last: Observation | None = None
    verdict: Verdict | None = None
    unsettled: _Unsettled | None = None
    shown = ""
    while True:
        try:
            seen = observe()
        except GitHubError as error:
            errors += 1
            if last is None or errors > pace.max_errors:
                return Result(Outcome.ERROR, GITHUB_ERROR, str(error), clock.monotonic() - start, polls, last)
            message = f"GitHub read failed ({errors} in a row), retrying: {error}"
        else:
            polls += 1
            errors = 0
            last = seen
            verdict = assess(seen, was_queued=was_queued)
            was_queued = was_queued or seen.snapshot.queue is not None
            now = clock.monotonic()
            if verdict.kind is Kind.MERGED:
                return Result(Outcome.MERGED, verdict.reason, verdict.message, now - start, polls, last)
            if verdict.kind is Kind.STUCK:
                return Result(Outcome.BLOCKED, verdict.reason, verdict.message, now - start, polls, last)
            if verdict.kind is Kind.UNSETTLED:
                if unsettled is None or unsettled.reason is not verdict.reason:
                    unsettled = _Unsettled(verdict.reason, now)
                if now - unsettled.since >= pace.settle:
                    return Result(Outcome.BLOCKED, verdict.reason, verdict.message, now - start, polls, last)
                message = f"{verdict.message} (confirming for {pace.settle:.0f}s before giving up)"
            else:
                unsettled = None
                message = verdict.message
        now = clock.monotonic()
        remaining = pace.timeout - (now - start)
        if remaining <= 0:
            if verdict is not None and unsettled is not None:
                # Out of time on a state that only looked stuck: nothing was seen moving, so call it blocked.
                return Result(Outcome.BLOCKED, verdict.reason, verdict.message, now - start, polls, last)
            waiting_on = verdict.message if verdict is not None else "GitHub could not be read"
            reason = verdict.reason if verdict is not None else GITHUB_ERROR
            return Result(Outcome.TIMEOUT, reason, f"timed out; last seen: {waiting_on}", now - start, polls, last)
        if message != shown:
            delay = pace.first_poll
        pause = min(delay, remaining)
        if unsettled is not None:
            pause = min(pause, _SETTLE_POLL)
        if message != shown:
            report(Tick(now - start, message, pause))
            shown = message
        clock.sleep(pause)
        delay = min(delay * pace.growth, pace.max_poll)
