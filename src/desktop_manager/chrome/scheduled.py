"""The scheduled task's entry point: `organise --unattended`, with output to a log file.

Task Scheduler starts this under `pythonw.exe`, so no console window flashes up
every hour; pythonw has no stdout, so everything goes to `organise.log` beside the
installed environment instead. The log keeps one previous generation.

A run waits up to `install_location.DEFAULT_WAIT_MINUTES` for the machine to go idle, inside the
task's `TIME_LIMIT_MINUTES`; arguments given to this module are passed on, so the
jobs coordinator can ask for a shorter wait. Shortly before that limit, faulthandler writes every
thread's stack to the log, so a run that hangs and gets killed says where it was.

The run is traced (see `traces`). A moment before that dump, the spans still
running are written out too, marked incomplete, so a killed run still shows in
`chrome-tabs traces runs`. The first run of each day also deletes trace files
older than `traces.KEEP_DAYS`.
"""

from __future__ import annotations

import faulthandler
import logging
import sys
import threading
import time
from collections.abc import Callable
from contextlib import suppress
from typing import TextIO

from desktop_manager.chrome.install_location import LOG_NAME, TIME_LIMIT_MINUTES, install_root

logger = logging.getLogger(__name__)

_LOG_LIMIT_BYTES = 1024 * 1024
COMMAND = ("organise", "--unattended", "--consolidate-saved", "--trigger", "scheduled")
_HANG_DUMP_SECONDS = (TIME_LIMIT_MINUTES - 2) * 60


def _invoke(app: Callable[..., object], command: list[str]) -> object:
    return app(command, standalone_mode=False)


def main(extra: list[str] | None = None) -> int:
    """Run the organise; `extra` (default: this process's arguments) are appended to `COMMAND`."""
    command = [*COMMAND, *(sys.argv[1:] if extra is None else extra)]
    log_path = install_root() / LOG_NAME
    log_path.parent.mkdir(parents=True, exist_ok=True)
    if log_path.exists() and log_path.stat().st_size > _LOG_LIMIT_BYTES:
        log_path.replace(log_path.with_suffix(".log.1"))
    with log_path.open("a", encoding="utf-8") as log:
        sys.stdout = sys.stderr = log
        log.write(f"--- {time.strftime('%Y-%m-%d %H:%M:%S')} {' '.join(command)}\n")
        log.flush()
        faulthandler.dump_traceback_later(_HANG_DUMP_SECONDS, file=log)
        deadline = _start_tracing(log)
        try:
            from desktop_manager.chrome.cli import chrome_tabs_app

            code = _invoke(chrome_tabs_app, command)
            return code if isinstance(code, int) else 0
        except SystemExit as exit_:
            return exit_.code if isinstance(exit_.code, int) else 1
        except Exception:
            logger.exception("Scheduled organise run crashed")
            return 1
        finally:
            faulthandler.cancel_dump_traceback_later()
            if deadline is not None:
                deadline.cancel()
            _stop_tracing()
            log.flush()


def _start_tracing(log: TextIO) -> threading.Timer | None:
    """Trace this run, prune old trace files once a day, and arm the pre-kill flush. Never raises."""
    with suppress(Exception):
        from desktop_manager.chrome import traces
        from rig import tracing

        tracing.init_tracing("chrome-tabs")
        traces.start()
        removed = traces.prune_daily(traces.traces_dir())
        if removed:
            log.write(f"Pruned {len(removed)} trace files older than {traces.KEEP_DAYS} days\n")
        deadline = threading.Timer(_HANG_DUMP_SECONDS - 5, tracing.flush, kwargs={"include_open": True})
        deadline.daemon = True
        deadline.start()
        return deadline
    return None


def _stop_tracing() -> None:
    with suppress(Exception):
        from rig import tracing

        tracing.shutdown_tracing()


if __name__ == "__main__":
    raise SystemExit(main())
