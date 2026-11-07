"""Carry out `claude-sessions startup`: plan, log, resume, start fresh, and summarise.

Unattended: it never prompts. Everything it decides and does is appended to
`claude-sessions-startup.log` in the restore logs directory as well as printed.

It looks back a fixed week, so a weekend or a few days off does not age sessions
out of reach; /clear is how a session is marked done. A run that finds the
workstation locked (Windows Update signs in and locks after an overnight restart)
types nothing and exits `EXIT_DEFERRED`, which the jobs coordinator retries on its
ticks until the screen is unlocked.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from desktop_manager.claude_sessions.backends import ResumeOptions, TerminalBackend
from desktop_manager.claude_sessions.discovery import Discovery, SessionRecord
from desktop_manager.claude_sessions.outcomes import FreshOutcome, Outcome
from desktop_manager.claude_sessions.startup import (
    DeferredTerminal,
    LeftOver,
    StartupPlan,
    plan_startup,
    wants_resume_at_logon,
)
from desktop_manager.models import WorkspacePlan

STARTUP_LOG_FILE_NAME = "claude-sessions-startup.log"
#: Some resume or fresh start was attempted and did not come up.
EXIT_FAILED = 1
#: Everything attempted came up, but candidate sessions were left unresumed: follow up by hand.
EXIT_LEFT_OVER = 3
#: The workstation was locked, so nothing was typed; run again once it is unlocked.
EXIT_DEFERRED = 4

_LEFT_ALONE = "left alone"


class StartupLog:
    """Appends timestamped lines to the startup log and echoes each one."""

    def __init__(self, path: Path | None, echo: Callable[[str], None]) -> None:
        self._path: Path | None = path
        self._echo: Callable[[str], None] = echo
        if path is not None:
            path.parent.mkdir(parents=True, exist_ok=True)

    @property
    def path(self) -> Path | None:
        return self._path

    def __call__(self, message: str) -> None:
        self._echo(message)
        if self._path is None:
            return
        stamp = datetime.now().astimezone().isoformat(timespec="seconds")
        with self._path.open("a", encoding="utf-8") as handle:
            handle.write(f"{stamp} {message}\n")


def deferred_terminals(plan: WorkspacePlan) -> list[DeferredTerminal]:
    """The plan's deferred terminals with placeholders resolved; raises ValueError if one cannot resolve."""
    from desktop_manager.placeholders import substitute_placeholders
    from desktop_manager.plan import deferred_logon_terminals

    substitutions = plan.substitutions()
    terminals: list[DeferredTerminal] = []
    for entry in deferred_logon_terminals(plan):
        cwd = substitute_placeholders(entry.cwd, substitutions)
        command = substitute_placeholders(entry.command, substitutions)
        if not cwd or not command:
            raise ValueError(f"terminal `{entry.name}` needs both cwd and command")
        terminals.append(DeferredTerminal(entry.name, entry.window_title, cwd, command))
    return terminals


@dataclass(frozen=True, slots=True)
class StartupSettings:
    include_exited: bool
    max_tabs: int
    max_sessions: int
    verify_timeout: float
    options: ResumeOptions


@dataclass(frozen=True, slots=True)
class StartupSummary:
    resumed: int
    started: int
    #: Sessions not resumed (no room, over the cap, running by the time their turn came)
    #: and planned terminals left alone because they were no longer idle.
    skipped: int
    failed: int
    #: Candidate sessions the plan had no room for; they still need resuming.
    left_over: tuple[LeftOver, ...]
    #: The workstation was locked, so nothing was attempted.
    deferred: bool = False

    @property
    def line(self) -> str:
        if self.deferred:
            return "deferred: the workstation is locked; nothing typed"
        failed = f", failed {self.failed}" if self.failed else ""
        return f"resumed {self.resumed}, started {self.started} fresh, skipped {self.skipped}{failed}"

    @property
    def exit_code(self) -> int:
        if self.deferred:
            return EXIT_DEFERRED
        if self.failed:
            return EXIT_FAILED
        return EXIT_LEFT_OVER if self.left_over else 0


@dataclass(frozen=True, slots=True)
class StartupCandidates:
    #: The sessions startup resumes, newest first.
    wanted: list[SessionRecord]
    #: The ones it passes over by default: closed cleanly after Claude had answered.
    passed: list[LeftOver]


def startup_candidates(found: Discovery, *, include_exited: bool) -> StartupCandidates:
    wanted = [r for r in found.candidates if wants_resume_at_logon(r, include_exited=include_exited)]
    passed = [
        LeftOver(r, "exited cleanly after an answered turn")
        for r in found.candidates
        if not wants_resume_at_logon(r, include_exited=include_exited)
    ]
    return StartupCandidates(wanted, passed)


def _log_plan(plan: StartupPlan, passed: Sequence[LeftOver], found: Discovery, log: StartupLog) -> None:
    log(f"{len(found.live)} session(s) running now, never resumed; {len(passed)} closed cleanly, not resumed")
    for slot in plan.resume:
        log(f"resume {slot.session.session_id[:8]} {slot.session.label[:50]} -> {slot.describe}")
    for target in plan.fresh:
        log(f"fresh  {target.terminal.command!r} -> {target.describe}")
    for item in plan.left_over:
        log(f"left   {item.session.session_id[:8]} {item.session.label[:50]}: {item.reason}")
    for terminal in plan.unmatched:
        log(f"no idle tab for planned terminal {terminal.name}; nothing typed for it")


def _is_skip(detail: str) -> bool:
    return detail.endswith(_LEFT_ALONE)


def run_startup(
    found: Discovery,
    backend: TerminalBackend,
    terminals: Sequence[DeferredTerminal],
    settings: StartupSettings,
    *,
    group_of: Callable[[SessionRecord], str],
    log: StartupLog,
    dry_run: bool,
    prepare: Callable[[], None] = lambda: None,
    locked: Callable[[], bool] = lambda: False,
) -> StartupSummary:
    """Plan and carry out startup; `prepare` runs once before anything is typed (the `claude` update).

    `locked` is asked once, just before typing; while it holds, the run is deferred.
    """
    candidates = startup_candidates(found, include_exited=settings.include_exited)
    plan = plan_startup(
        candidates.wanted,
        backend.list_windows(),
        terminals,
        group_of=group_of,
        max_tabs_per_window=settings.max_tabs,
        max_sessions=settings.max_sessions,
    )
    _log_plan(plan, candidates.passed, found, log)
    left_over = plan.left_over
    if dry_run:
        log("dry run: nothing typed")
        return StartupSummary(len(plan.resume), len(plan.fresh), len(left_over), failed=0, left_over=left_over)
    if plan.is_empty:
        return StartupSummary(0, 0, len(left_over), failed=0, left_over=left_over)
    if locked():
        return StartupSummary(0, 0, 0, failed=0, left_over=left_over, deferred=True)

    prepare()
    outcomes: list[Outcome] = []
    if plan.resume:
        outcomes = backend.resume(list(plan.resume), options=settings.options, on_event=log)
    fresh: list[FreshOutcome] = []
    if plan.fresh:
        fresh = backend.start_fresh(list(plan.fresh), verify_timeout=settings.verify_timeout, on_event=log)

    for outcome in outcomes:
        if not outcome.ok:
            log(f"resume {outcome.slot.session.session_id[:8]}: {outcome.detail}")
    for item in fresh:
        if not item.ok:
            log(f"fresh {item.target.describe}: {item.detail}")
    skipped = len(left_over) + sum(not o.ok and _is_skip(o.detail) for o in outcomes)
    skipped += sum(not f.ok and _is_skip(f.detail) for f in fresh)
    failed = sum(not o.ok and not _is_skip(o.detail) for o in outcomes) + (len(plan.resume) - len(outcomes))
    failed += sum(not f.ok and not _is_skip(f.detail) for f in fresh) + (len(plan.fresh) - len(fresh))
    return StartupSummary(
        resumed=sum(o.ok for o in outcomes),
        started=sum(f.ok for f in fresh),
        skipped=skipped,
        failed=failed,
        left_over=left_over,
    )
