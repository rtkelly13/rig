"""Every job run the coordinator has made, with the output each one printed.

`state.json` keeps only each job's *last* outcome, which is what scheduling
needs. People need more: when did `estate-trim` last fail, and what did it say?
So every job run also appends one `JobRun` line to `runs.ndjson` and saves its
own slice of the job's log as `runs/<job>/<run_id>.log`.

A job runs at most once per coordinator run, so `(job, run_id)` names one job
run, where `run_id` is the coordinator run's `<trigger>-<YYYYmmdd-HHMMSS>`.

- `runs.ndjson` is trimmed back to its newest `KEEP_RUNS` lines once it passes
  `KEEP_RUNS + TRIM_SLACK`, so the rewrite is rare and the file stays small.
- Each job keeps its newest `KEEP_OUTPUTS_PER_JOB` output files. An output is
  capped at `OUTPUT_LIMIT_BYTES`, keeping the end, where the error usually is.
- `trace_id` and `span_id` name the span the coordinator ran the job under, so a
  run links to its trace in a trace viewer.

Unreadable lines are skipped and write failures are swallowed: the history is a
convenience, never a gate on the run.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictFloat, StrictInt, StrictStr

from rig.typed_json import Lenient, encode_json, try_decode_json

HISTORY_NAME = "runs.ndjson"
OUTPUTS_DIR = "runs"
KEEP_RUNS = 2000
TRIM_SLACK = 500
KEEP_OUTPUTS_PER_JOB = 50
OUTPUT_LIMIT_BYTES = 256 * 1024

#: The shape `runner.new_run_id` gives a coordinator run, such as `logon-20261011-080201`.
RUN_ID = re.compile(r"[a-z]+-\d{8}-\d{6}")


@dataclass(frozen=True, slots=True)
class JobRun:
    """One job run: when, how long, how it ended, and where its output went."""

    job: str
    run_id: str
    started: str
    seconds: float
    returncode: int | None
    trigger: str
    error: str | None = None
    #: The run's output file, relative to the jobs directory; empty when nothing was captured.
    output: str = ""
    trace_id: str = ""
    span_id: str = ""

    @property
    def ok(self) -> bool:
        return self.returncode == 0 and self.error is None


def is_run_id(value: str) -> bool:
    return RUN_ID.fullmatch(value) is not None


def history_path(directory: Path) -> Path:
    return directory / HISTORY_NAME


def output_path(directory: Path, job: str, run_id: str) -> Path:
    return directory / OUTPUTS_DIR / job / f"{run_id}.log"


def _stamp(path: Path) -> str:
    """`tick-20261011-080201.log` -> `20261011-080201`, so outputs sort by time whatever their trigger."""
    return path.stem.partition("-")[2]


def save_output(directory: Path, job: str, run_id: str, data: bytes) -> str:
    """Store one run's output, keeping its end, and prune the job's oldest; the path relative to `directory`."""
    if not data or not is_run_id(run_id):
        return ""
    path = output_path(directory, job, run_id)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_bytes(data[-OUTPUT_LIMIT_BYTES:])
        for stale in sorted(path.parent.glob("*.log"), key=_stamp)[:-KEEP_OUTPUTS_PER_JOB]:
            stale.unlink(missing_ok=True)
    except OSError:
        return ""
    return path.relative_to(directory).as_posix()


def append_run(directory: Path, record: JobRun) -> None:
    """Add `record` to the history, trimming the oldest lines once it has grown well past `KEEP_RUNS`."""
    path = history_path(directory)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as handle:
            _ = handle.write(encode_json(JobRun, record) + "\n")
        # A write cut off mid-character must not stop the run being recorded; that line is skipped on read.
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        if len(lines) > KEEP_RUNS + TRIM_SLACK:
            temporary = path.with_suffix(".tmp")
            _ = temporary.write_text("\n".join(lines[-KEEP_RUNS:]) + "\n", encoding="utf-8")
            _ = temporary.replace(path)
    except OSError:
        return


@dataclass(frozen=True, slots=True)
class _StoredRun:
    job: StrictStr
    run_id: StrictStr
    started: StrictStr
    seconds: StrictInt | StrictFloat
    trigger: StrictStr
    returncode: Lenient[StrictInt] = None
    error: Lenient[StrictStr] = None
    output: Lenient[StrictStr] = None
    trace_id: Lenient[StrictStr] = None
    span_id: Lenient[StrictStr] = None


def _record(stored: _StoredRun) -> JobRun:
    return JobRun(
        job=stored.job,
        run_id=stored.run_id,
        started=stored.started,
        seconds=float(stored.seconds),
        returncode=stored.returncode,
        trigger=stored.trigger,
        error=stored.error,
        output=stored.output or "",
        trace_id=stored.trace_id or "",
        span_id=stored.span_id or "",
    )


def read_runs(directory: Path, *, job: str | None = None, limit: int = 200) -> list[JobRun]:
    """The newest `limit` runs, newest first, optionally of one job."""
    try:
        lines = history_path(directory).read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return []
    out: list[JobRun] = []
    for line in reversed(lines):
        stored = try_decode_json(_StoredRun, line) if line.strip() else None
        if stored is None or (job is not None and stored.job != job):
            continue
        out.append(_record(stored))
        if len(out) >= limit:
            break
    return out


def find_run(directory: Path, job: str, run_id: str) -> JobRun | None:
    """The newest recorded run of `job` with `run_id`; None when the history has none."""
    return next(
        (record for record in read_runs(directory, job=job, limit=KEEP_RUNS + TRIM_SLACK) if record.run_id == run_id),
        None,
    )


def read_output(directory: Path, record: JobRun) -> str | None:
    """The output `record` captured; None when it saved none or the file has been pruned."""
    if not record.output:
        return None
    path = (directory / record.output).resolve()
    if not path.is_relative_to((directory / OUTPUTS_DIR).resolve()):
        return None
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
