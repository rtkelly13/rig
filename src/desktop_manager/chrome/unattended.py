"""Unattended runs: leave Chrome alone during calls, and wait for an idle machine before grouping."""

from __future__ import annotations

from collections.abc import Callable, Sequence
from contextlib import suppress
from dataclasses import dataclass, replace
from datetime import datetime
from typing import TYPE_CHECKING

from rich.markup import escape

from desktop_manager.chrome.group_run import run_group_plan
from desktop_manager.chrome.grouping import GroupPlan
from desktop_manager.chrome.presence import Block
from desktop_manager.chrome.tabs import ChromeWindow
from rig import tracing
from rig.tracing import AttributeValue

if TYPE_CHECKING:
    from desktop_manager.chrome.driver import ChromeDriver
    from desktop_manager.chrome.presence import InputWatch

from desktop_manager.chrome.cli_common import (
    console,
    short,
    trace_span,
)


@dataclass(frozen=True)
class GroupingWait:
    """How the wait for an idle, unlocked machine went."""

    deferred: str | None  # why grouping gave up; None when it may go ahead
    outcome: str  # clear_at_once | clear | gave_up | no_wait
    waited_seconds: float = 0.0
    checks: int = 1
    last_block: str | None = None

    def attributes(self) -> dict[str, AttributeValue]:
        return {
            "wait.outcome": self.outcome,
            "wait.seconds": round(self.waited_seconds, 1),
            "wait.checks": self.checks,
            **({"wait.last_block": self.last_block} if self.last_block else {}),
        }


def await_unattended_grouping(
    read_windows: Callable[[], Sequence[ChromeWindow]], idle_minutes: float, wait_minutes: float
) -> GroupingWait:
    """Wait until an unattended run may take the mouse; `deferred` says why it gave up, if it did.

    One span for the whole wait, with a `check` event for every look."""
    import sys

    from desktop_manager.chrome.presence import wait_until_clear

    attrs = {"wait.budget_s": wait_minutes * 60, "wait.idle_minutes": idle_minutes}
    with trace_span("wait_for_idle", **attrs) as span:

        def blocked() -> Block | None:
            block = unattended_group_block(read_windows, idle_minutes)
            span.event("check", **_check_attributes(block))
            return block

        first = blocked()
        if first is None:
            result = GroupingWait(None, "clear_at_once")
        elif wait_minutes <= 0:
            result = GroupingWait(first.reason, "no_wait", last_block=first.reason)
        else:
            console.print(f"Waiting up to {wait_minutes:g} min to group: {escape(first.reason)}")
            sys.stdout.flush()
            tracing.flush()  # a long wait follows: put what has finished so far on disk
            outcome = wait_until_clear(blocked, budget_seconds=wait_minutes * 60)
            checks = outcome.checks + 1
            if outcome.ready:
                console.print(f"Clear to group after {outcome.waited_seconds:.0f}s ({outcome.checks} checks)")
                last = outcome.last_block or first.reason
                result = GroupingWait(None, "clear", outcome.waited_seconds, checks, last)
            else:
                reason = f"still not clear after {wait_minutes:g} min ({outcome.checks} checks): {outcome.last_block}"
                result = GroupingWait(reason, "gave_up", outcome.waited_seconds, checks, outcome.last_block)
        span.set(**result.attributes())
        return result


def _check_attributes(block: Block | None) -> dict[str, AttributeValue]:
    if block is None:
        return {"blocked": False}
    hint = block.clears_in
    return {"blocked": True, "reason": block.reason, **({"hint_s": round(hint, 1)} if hint is not None else {})}


def unattended_group_block(read_windows: Callable[[], Sequence[ChromeWindow]], idle_minutes: float) -> Block | None:
    """Why an unattended run must not take the mouse now, and when it might; None when it may."""
    from desktop_manager.chrome.presence import idle_seconds, session_locked

    if session_locked():
        return Block("the session is locked")
    idle, needed = idle_seconds(), idle_minutes * 60
    if idle < needed:
        return Block(f"the machine was used {idle:.0f}s ago (waits for {idle_minutes:g} min idle)", needed - idle)
    live = live_call(read_windows())
    if live is not None:
        return Block(live)
    return None


def live_call(windows: Sequence[ChromeWindow]) -> str | None:
    """Why the user looks to be on a call or sharing their screen; None when they do not.

    A tab recording or sharing, or an audible call site, gives it away; failing that,
    Chrome holding the microphone or camera does (a muted call, a Meet app window)."""
    from desktop_manager.chrome.presence import devices_in_use
    from desktop_manager.chrome.site_rules import is_call
    from desktop_manager.chrome.tabs import LIVE_ALERTS

    for tab in (tab for window in windows for tab in window.tabs):
        alert = next((alert for alert in tab.alerts if alert in LIVE_ALERTS), None)
        if alert is not None:
            return f"a call is live: {alert.lower()} ({short(tab.title)})"
        if tab.audible and is_call(tab.title):
            return f"a call is live ({short(tab.title)})"
    with suppress(Exception):
        devices = devices_in_use()
        if devices:
            return f"a call is live: Chrome is using the {' and '.join(devices)}"
    return None


def skip_if_live(windows: Sequence[ChromeWindow], run: tracing.Span | None = None) -> bool:
    """For unattended runs: True, having said why, when a call is live and the run must leave Chrome alone."""
    live = live_call(windows)
    if live is None:
        return False
    console.print(f"[yellow]Skipped[/yellow]: {escape(live)}; left everything for the next run")
    if run is not None:
        run.set(**{"run.outcome": "skipped_live", "run.skipped": live})
    return True


#: After input stops grouping, it goes again once the machine has been left alone this long.
RETRY_IDLE_MINUTES = 0.5
#: How long a retry waits for that idle spell before leaving grouping to the next run.
RETRY_WAIT_MINUTES = 5.0
#: Retries a run makes after input stops it, before leaving the rest to the next run.
INPUT_RETRIES = 2
PAUSED_NOTE = "chrome-tidy-paused.txt"


@dataclass(slots=True)
class InputStop:
    """An `interrupted` hook that stops grouping at the first key press or mouse move, and remembers it did.

    Grouping also stops for its own failures; `fired` tells the two apart."""

    watch: InputWatch
    fired: bool = False

    def __call__(self) -> str | None:
        if self.watch.user_moved():
            self.fired = True
            return "keyboard or mouse input"
        self.watch.mark()
        return None


def stop_on_input() -> InputStop:
    from desktop_manager.chrome.presence import InputWatch

    return InputStop(InputWatch())


def show_paused_note(*, quiet_since: datetime) -> None:
    """Open a Notepad note asking the user to leave the machine alone so grouping can finish.

    `quiet_since` is the run's start: a run that is interrupted again does not open a second note."""
    from desktop_manager.restore_fallbacks import open_note

    idle = f"{RETRY_IDLE_MINUTES * 60:.0f} seconds"
    open_note(
        PAUSED_NOTE,
        [
            "Chrome tidy paused",
            "",
            "You used the keyboard or mouse while it was grouping tabs, so it stopped.",
            f"Leave the machine alone for {idle} and it carries on by itself;",
            f"it waits up to {RETRY_WAIT_MINUTES:g} minutes, then leaves the rest to the next hourly run.",
            "",
            "You can close this note.",
        ],
        quiet_since=quiet_since,
    )


@dataclass(frozen=True, slots=True)
class GroupingResult:
    """How grouping went over every attempt: input that stopped it is `by_input`, not a failure."""

    planned: int
    moved: int = 0
    created: bool = False
    stopped: str | None = None
    by_input: bool = False
    retries: int = 0


def group_until_left_alone(
    driver: ChromeDriver,
    plan: GroupPlan,
    *,
    read_windows: Callable[[str], list[ChromeWindow]],
    replan: Callable[[str], GroupPlan],
    unattended: bool,
    run_started: datetime,
) -> GroupingResult:
    """Run the grouping plan; unattended, input that stops it opens a note and it retries once left alone.

    Each retry waits `RETRY_IDLE_MINUTES` of idle, for up to `RETRY_WAIT_MINUTES`, and plans afresh."""
    planned, moved, created, retries = len(plan.moves), 0, False, 0
    while True:
        stop = stop_on_input() if unattended else None
        ran = run_group_plan(driver, plan, interrupted=stop)
        moved += ran.moved
        created = created or (ran.moved > 0 and any(move.new for move in plan.moves))
        by_input = stop is not None and stop.fired
        result = GroupingResult(planned, moved, created, ran.stopped, by_input, retries)
        if ran.stopped is None or not by_input:
            return result
        if retries == INPUT_RETRIES:
            return replace(result, stopped=f"{ran.stopped} again after {retries} retries")
        show_paused_note(quiet_since=run_started)
        console.print(
            f"[yellow]Paused grouping[/yellow]: {escape(ran.stopped)}; "
            f"goes again after {RETRY_IDLE_MINUTES * 60:.0f}s idle (waits up to {RETRY_WAIT_MINUTES:g} min)"
        )
        wait = await_unattended_grouping(lambda: read_windows("wait_check"), RETRY_IDLE_MINUTES, RETRY_WAIT_MINUTES)
        if wait.deferred is not None:
            return replace(result, stopped=f"{ran.stopped}, then {wait.deferred}")
        plan, retries = replan("retry"), retries + 1
        if not plan.moves:
            return replace(result, stopped=None, by_input=False, retries=retries)


def group_outcome(grouped: GroupingResult, *, deferred: str | None) -> str:
    if deferred is not None:
        return "deferred"
    if not grouped.planned:
        return "nothing_to_group"
    if grouped.stopped is None:
        return "grouped"
    return "interrupted" if grouped.by_input else "stopped"
