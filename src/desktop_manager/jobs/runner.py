"""Run the due jobs for a trigger, one at a time, recording each outcome.

Each job runs as a subprocess with its own `logs/<job>.log` (one previous
generation kept), never in-process: a hung or crashing job then costs only its
own timeout, and the jobs after it still run. The state file is saved after
every job, so a run killed part-way keeps what it finished.

Every run has an id (`<trigger>-<YYYYmmdd-HHMMSS>`). It heads the run's section
of each job log, reaches the job as `RUN_ID_ENV`, and names the run in
`last-<trigger>.json`, which lists every job the run took or skipped. A job's
outcome keeps the last line it printed as its summary and, when it failed, the
end of its output, read from `Job.detail_log` when the job logs there instead of
to stdout. `jobs status` and `jobs last` then show why a job failed without
opening a log.

Each job run also goes into the run history (`history`): one `JobRun` line in
`runs.ndjson`, plus that run's slice of the job log as its own file. The job runs
inside a `job <name>` span, whose trace and span ids the history line keeps.

One run at a time holds `run.lock`. A logon run waits for a tick that got there
first; a tick that finds a run in progress skips, because the next tick is soon.
"""

from __future__ import annotations

import logging
import os
import re
import subprocess
import sys
import time
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from desktop_manager.jobs.history import JobRun, append_run, save_output
from desktop_manager.jobs.registry import Job, Trigger
from desktop_manager.jobs.state import (
    LOCK_NAME,
    STATE_NAME,
    Decision,
    Outcome,
    RunRecord,
    is_due,
    last_run_path,
    read_state,
    write_run,
    write_state,
)
from rig import tracing

logger = logging.getLogger(__name__)

_LOG_LIMIT_BYTES = 512 * 1024
_CREATE_NO_WINDOW = 0x08000000
LOGON_LOCK_WAIT_SECONDS = 30 * 60
TIMED_OUT = 124
RUN_ID_ENV = "SHARED_UTILITIES_JOB_RUN"
DETAIL_LINES = 12
_SUMMARY_CHARS = 200
_TAIL_BYTES = 64 * 1024
_SECTION_HEADER = re.compile(r"--- \d")


@dataclass(frozen=True, slots=True)
class Planned:
    job: Job
    run: bool
    reason: str


Execute = Callable[[Job, Path, str], int]
IdleSeconds = Callable[[], float]


def plan(
    jobs: Iterable[Job],
    trigger: Trigger,
    state: dict[str, Outcome],
    *,
    now: datetime,
    booted: datetime,
    only: frozenset[str] = frozenset(),
    force: bool = False,
    flags: Mapping[str, bool] | None = None,
) -> list[Planned]:
    """Which jobs this run takes, and why each one is or is not running.

    ``flags`` is the rig flag state; when None it is read from rig.db, but only
    if some job is behind a flag.
    """
    jobs = list(jobs)
    if flags is None and any(job.feature for job in jobs):
        from rig.flags import effective_flags

        flags = effective_flags()
    planned: list[Planned] = []
    for job in jobs:
        if only and job.name not in only:
            continue
        missing = [path for path in job.requires if not path.exists()]
        if missing:
            planned.append(Planned(job, run=False, reason=f"missing {missing[0]}"))
        elif job.feature and not (flags or {}).get(job.feature, False) and job.name not in only:
            planned.append(Planned(job, run=False, reason=f"flag {job.feature} off"))
        elif force or job.name in only:
            planned.append(Planned(job, run=True, reason="requested"))
        elif retry := _retry(job, trigger, state.get(job.name), booted=booted):
            planned.append(Planned(job, run=True, reason=retry))
        elif trigger not in job.triggers:
            planned.append(Planned(job, run=False, reason=f"not on {trigger}"))
        elif not is_due(job, state.get(job.name), now=now, booted=booted):
            planned.append(Planned(job, run=False, reason=f"not due ({job.cadence})"))
        else:
            planned.append(Planned(job, run=True, reason=str(job.cadence)))
    return planned


def _retry(job: Job, trigger: Trigger, last: Outcome | None, *, booted: datetime) -> str | None:
    """Why a tick reruns a job whose run this boot said it could not run yet; None if it does not."""
    if trigger is not Trigger.TICK or last is None or last.started_at < booted:
        return None
    if last.returncode is None or last.returncode not in job.retry_exit_codes:
        return None
    return f"retry: last run exited {last.returncode}"


def new_run_id(trigger: Trigger, now: datetime) -> str:
    return f"{trigger}-{now:%Y%m%d-%H%M%S}"


def run(
    jobs: Iterable[Job],
    trigger: Trigger,
    directory: Path,
    *,
    booted: datetime,
    only: frozenset[str] = frozenset(),
    force: bool = False,
    execute: Execute | None = None,
    idle_seconds: IdleSeconds | None = None,
    clock: Callable[[], datetime] = lambda: datetime.now().astimezone(),
    report: Callable[[str], None] = print,
    run_id: str | None = None,
) -> int:
    """Run the due jobs; 0 when every job that ran succeeded, else 1.

    A job with `idle_minutes` is checked against the machine's idle time just
    before it would start, after the jobs ahead of it, and skipped without a
    recorded outcome while someone is at the keyboard.
    """
    from desktop_manager.display_guard import hold_lock

    execute = execute or execute_subprocess
    idle_seconds = idle_seconds or machine_idle_seconds
    directory.mkdir(parents=True, exist_ok=True)
    wait = LOGON_LOCK_WAIT_SECONDS if trigger is Trigger.LOGON else 0
    with hold_lock(directory / LOCK_NAME, timeout_seconds=wait, sleep=time.sleep) as acquired:
        if not acquired:
            report(f"another jobs run holds {directory / LOCK_NAME}; skipping this {trigger} run")
            return 0
        started = clock()
        run_id = run_id or new_run_id(trigger, started)
        state_path = directory / STATE_NAME
        record_path = last_run_path(directory, trigger)
        state = read_state(state_path)
        decisions: list[Decision] = []

        def save(finished: datetime | None) -> None:
            stamp = None if finished is None else finished.isoformat(timespec="seconds")
            write_run(
                record_path,
                RunRecord(run_id, str(trigger), started.isoformat(timespec="seconds"), stamp, tuple(decisions)),
            )

        for item in plan(jobs, trigger, state, now=started, booted=booted, only=only, force=force):
            busy = None if not item.run or item.reason == "requested" else _busy(item.job, idle_seconds)
            if not item.run or busy:
                reason = busy or item.reason
                report(f"skip {item.job.name}: {reason}")
                decisions.append(Decision(item.job.name, ran=False, reason=reason))
                continue
            report(f"run  {item.job.name} ({item.reason})")
            # Recorded before the job starts, so a run killed part way still shows in `jobs last`.
            save(None)
            outcome = _run_one(item.job, trigger, directory, execute, clock, run_id)
            state[item.job.name] = outcome
            write_state(state_path, state)
            decisions.append(Decision(item.job.name, ran=True, reason=item.reason, outcome=outcome))
            save(None)
            report(f"     {item.job.name}: {_describe(outcome)}")
            for line in outcome.detail:
                report(f"       | {line}")
        save(clock())
    return 1 if any(item.outcome is not None and not item.outcome.ok for item in decisions) else 0


def _busy(job: Job, idle_seconds: IdleSeconds) -> str | None:
    if not job.idle_minutes:
        return None
    idle = idle_seconds()
    if idle >= job.idle_minutes * 60:
        return None
    return f"in use (idle {idle:.0f}s, needs {job.idle_minutes} min)"


def machine_idle_seconds() -> float:
    """Seconds since the last keyboard or mouse input; unbounded off Windows."""
    if sys.platform != "win32":
        return float("inf")
    from desktop_manager.chrome.presence import idle_seconds

    return idle_seconds()


def _run_one(
    job: Job, trigger: Trigger, directory: Path, execute: Execute, clock: Callable[[], datetime], run_id: str
) -> Outcome:
    """Run `job` in its own span, then record it in the run history; the outcome is for `state.json`."""
    logs = directory / "logs"
    log = log_path(logs, job)
    _rotate(log)
    offset = _size(log)
    started = clock()
    begun = time.monotonic()
    before = _modified(job.detail_log)
    detail_offset = _size(job.detail_log) if job.detail_log is not None else 0
    attributes: dict[str, tracing.AttributeValue] = {"job.trigger": str(trigger), "job.run_id": run_id}
    with tracing.span(f"job {job.name}", scope="desktop_manager.jobs", **attributes) as span:
        try:
            returncode = execute(job, logs, run_id)
            error = None
        except Exception as exc:
            logger.error("Job %s crashed", job.name, exc_info=True)
            returncode = None
            error = f"{type(exc).__name__}: {exc}"
        span.set(**{"job.returncode": -1 if returncode is None else returncode})
        if error or returncode != 0:
            span.error = error or f"exit {returncode}"
    printed = run_output(log, run_id)
    # Its own log's last section explains this run only if this run wrote to it.
    if job.detail_log is not None and not printed and _modified(job.detail_log) != before:
        printed = run_output(job.detail_log)
    failed = returncode != 0 or error is not None
    # A job that redirects its stdout (chrome-organise) leaves only markers in the coordinator log,
    # so the saved output carries what this run added to its own log too.
    detail = b""
    if job.detail_log is not None and _modified(job.detail_log) != before:
        # A log that shrank was rotated or rewritten during the run, so all of it is this run's.
        detail = _read_from(job.detail_log, detail_offset if _size(job.detail_log) >= detail_offset else 0)
    outcome = Outcome(
        started=started.isoformat(timespec="seconds"),
        seconds=round(time.monotonic() - begun, 1),
        returncode=returncode,
        trigger=str(trigger),
        error=error,
        run_id=run_id,
        summary=printed[-1][:_SUMMARY_CHARS] if printed else None,
        detail=tuple(printed[-DETAIL_LINES:]) if failed else (),
    )
    append_run(
        directory,
        JobRun(
            job=job.name,
            run_id=run_id,
            started=outcome.started,
            seconds=outcome.seconds,
            returncode=returncode,
            trigger=outcome.trigger,
            error=error,
            output=save_output(directory, job.name, run_id, _read_from(log, offset) + detail),
            trace_id=tracing.current_trace_id(),
            span_id=span.span_id,
        ),
    )
    return outcome


def _rotate(path: Path) -> None:
    """Start a new generation of a job log once it passes `_LOG_LIMIT_BYTES`, keeping one previous."""
    try:
        if path.stat().st_size > _LOG_LIMIT_BYTES:
            _ = path.replace(path.with_suffix(".log.1"))
    except OSError:
        return


def _size(path: Path) -> int:
    try:
        return path.stat().st_size
    except OSError:
        return 0


def _read_from(path: Path, offset: int) -> bytes:
    """What was appended to the log at `path` after it was `offset` bytes long."""
    try:
        with path.open("rb") as handle:
            _ = handle.seek(offset)
            return handle.read()
    except OSError:
        return b""


def _modified(path: Path | None) -> int | None:
    try:
        return None if path is None else path.stat().st_mtime_ns
    except OSError:
        return None


def run_output(path: Path, run_id: str | None = None) -> list[str]:
    """The non-blank lines of one section of a log headed `--- <timestamp> ...`: the one naming `run_id`, else the last.

    Empty when the log is missing or, given `run_id`, has no section for it.
    """
    try:
        with path.open("rb") as handle:
            size = handle.seek(0, 2)
            _ = handle.seek(max(0, size - _TAIL_BYTES))
            text = handle.read().decode("utf-8", errors="replace")
    except OSError:
        return []
    lines = text.splitlines()
    headers = [index for index, line in enumerate(lines) if _SECTION_HEADER.match(line)]
    chosen = [index for index in headers if run_id is None or f"[{run_id}]" in lines[index]]
    if not chosen:
        return []
    start = chosen[-1] + 1
    end = next((index for index in headers if index > chosen[-1]), len(lines))
    return [line.rstrip() for line in lines[start:end] if line.strip() and not line.startswith("--- ")]


def _describe(outcome: Outcome) -> str:
    if outcome.error:
        return f"error after {outcome.seconds:.0f}s: {outcome.error}"
    described = f"exit {outcome.returncode} after {outcome.seconds:.0f}s"
    return f"{described}: {outcome.summary}" if outcome.summary else described


def log_path(logs: Path, job: Job) -> Path:
    return named_log_path(logs, job.name)


def named_log_path(logs: Path, name: str) -> Path:
    return logs / f"{name}.log"


def execute_subprocess(job: Job, logs: Path, run_id: str) -> int:
    """Run `job` with no console window, appending its output to its log under a header naming `run_id`.

    The coordinator rotates the log before calling this, so the run's output is everything appended here.
    """
    path = log_path(logs, job)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as log:
        log.write(f"--- {time.strftime('%Y-%m-%d %H:%M:%S')} [{run_id}] {' '.join(job.argv)}\n")
        log.flush()
        try:
            completed = subprocess.run(
                job.argv,
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=subprocess.STDOUT,
                timeout=job.timeout_minutes * 60,
                creationflags=_CREATE_NO_WINDOW if sys.platform == "win32" else 0,
                env={**os.environ, "PYTHONIOENCODING": "utf-8", RUN_ID_ENV: run_id},
                check=False,
            )
        except subprocess.TimeoutExpired:
            log.write(f"--- timed out after {job.timeout_minutes} min\n")
            return TIMED_OUT
        log.write(f"--- exit {completed.returncode}\n")
        return completed.returncode
