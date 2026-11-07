"""The scheduled tasks' entry point: `python -m desktop_manager.jobs.scheduled logon|tick`.

Task Scheduler starts this under `pythonw.exe`, so nothing flashes up every
`TICK_MINUTES`; pythonw has no stdout, so the run's own narrative goes to
`jobs/coordinator.log` and each job's output to `jobs/logs/<job>.log`. Every
coordinator line names the run's id, which also heads its sections of the job logs.
Its spans, one per job, go to the `desktop-manager-jobs` trace files.

A logon run first waits for Explorer's shell window, the precondition for the
virtual-desktop APIs the restore uses, instead of a blind trigger delay. It also
deletes trace files older than `TRACE_KEEP_DAYS` from the shared traces directory.
"""

from __future__ import annotations

import logging
import sys
import time
from datetime import datetime
from pathlib import Path

from desktop_manager.jobs.registry import Trigger, current_environment, default_jobs
from desktop_manager.jobs.session_windows import boot_time, wait_for_shell
from desktop_manager.jobs.state import jobs_dir

logger = logging.getLogger(__name__)

LOG_NAME = "coordinator.log"
_LOG_LIMIT_BYTES = 512 * 1024
SHELL_READY_TIMEOUT_SECONDS = 120
TRACE_KEEP_DAYS = 14


def main(argv: list[str] | None = None) -> int:
    arguments = sys.argv[1:] if argv is None else argv
    trigger = Trigger(arguments[0]) if arguments else Trigger.TICK
    from desktop_manager.jobs.runner import new_run_id
    from rig import tracing

    # Each job runs in a `job <name>` span; naming the tool also registers the exit flush that writes them.
    tracing.init_tracing("desktop-manager-jobs")
    run_id = new_run_id(trigger, datetime.now().astimezone())
    directory = jobs_dir()
    log_path = directory / LOG_NAME
    log_path.parent.mkdir(parents=True, exist_ok=True)
    if log_path.exists() and log_path.stat().st_size > _LOG_LIMIT_BYTES:
        log_path.replace(log_path.with_suffix(".log.1"))
    with log_path.open("a", encoding="utf-8") as log:
        sys.stdout = sys.stderr = log

        def report(line: str) -> None:
            log.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} [{run_id}] {line}\n")
            log.flush()

        try:
            if trigger is Trigger.LOGON:
                ready = wait_for_shell(SHELL_READY_TIMEOUT_SECONDS)
                report("Explorer shell ready" if ready else "shell probe timed out; continuing")
            from desktop_manager.jobs.runner import run

            environment = current_environment()
            if trigger is Trigger.LOGON:
                from desktop_manager.runtime import traces_dir

                removed = prune_traces(traces_dir(), keep_days=TRACE_KEEP_DAYS, now=time.time())
                if removed:
                    report(f"pruned {removed} trace file(s) older than {TRACE_KEEP_DAYS} days")
            return run(default_jobs(environment), trigger, directory, booted=boot_time(), report=report, run_id=run_id)
        except Exception:
            logger.exception("Jobs coordinator run crashed (trigger %s)", trigger)
            return 1


def prune_traces(directory: Path, *, keep_days: int, now: float) -> int:
    """Delete `*.ndjson` files under `directory` last written over `keep_days` ago; how many went."""
    cutoff = now - keep_days * 86400
    removed = 0
    for path in directory.rglob("*.ndjson") if directory.is_dir() else ():
        try:
            if path.stat().st_mtime < cutoff:
                path.unlink()
                removed += 1
        except OSError:
            continue
    return removed


if __name__ == "__main__":
    raise SystemExit(main())
