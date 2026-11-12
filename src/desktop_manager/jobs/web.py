"""The jobs coordinator in the rig web server: the registry, each job's runs, and what a run printed.

- `GET /api/ops/jobs`: every registered job with its last outcome from `state.json`.
- `GET /api/ops/runs?job=<name>&limit=1..1000`: job runs from the run history, newest first.
- `GET /api/ops/runs/<job>/<run_id>/output`: what one run printed; 404 for an unknown job or run.
- `POST /api/ops/jobs/run` with `{"job": <name> | null}`: start `rig jobs run --trigger tick` in the background,
  for one job (due or not) or for every due job, and answer at once. The coordinator's lock keeps it from
  overlapping a scheduled run: if one is in progress, the started run skips.

Runs made before the history existed are not lost: a job's last outcome in
`state.json` that the history does not hold is listed too, with its output read
from the run's section of the job log. Path segments never reach the file system
unchecked: the job must be a registry name and the run id must have the
coordinator's `<trigger>-<YYYYmmdd-HHMMSS>` shape, and output files are only
ever read from the paths the history recorded.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from http.server import BaseHTTPRequestHandler
from pathlib import Path

from desktop_manager.jobs import history
from desktop_manager.jobs.history import JobRun
from desktop_manager.jobs.registry import Cadence, Job, current_environment, default_jobs
from desktop_manager.jobs.runner import named_log_path, run_output
from desktop_manager.jobs.state import STATE_NAME, Outcome, jobs_dir, read_state
from rig.tracing import spawn_detached
from rig.typed_json import decode_json, encode_json
from rig.web_routes import Request, RouteNotFoundError, json_body, respond

JOBS_PATH = "/api/ops/jobs"
RUN_PATH = "/api/ops/jobs/run"
RUNS_PATH = "/api/ops/runs"
DEFAULT_LIMIT = 200
MAX_LIMIT = 1000

_CADENCE = {
    Cadence.EVERY_RUN: "every run",
    Cadence.ONCE_PER_BOOT: "once per boot",
    Cadence.DAILY: "daily",
}


@dataclass(frozen=True, slots=True)
class LastRun:
    started: str
    seconds: float
    returncode: int | None
    trigger: str
    error: str | None
    ok: bool


@dataclass(frozen=True, slots=True)
class JobView:
    name: str
    description: str
    cadence: str
    triggers: tuple[str, ...]
    timeout_minutes: int
    last: LastRun | None


@dataclass(frozen=True, slots=True)
class RunView:
    job: str
    run_id: str
    started: str
    seconds: float
    returncode: int | None
    trigger: str
    error: str | None
    ok: bool
    has_output: bool
    #: Empty when the run predates the history, or ran untraced.
    trace_id: str


@dataclass(frozen=True, slots=True)
class RunOutput:
    job: str
    run_id: str
    #: None when the run saved no output, or it has since been pruned.
    output: str | None


def cadence_text(job: Job) -> str:
    """`every run, idle 3 min, flag claude-session-restore`: when the job runs, as the docs' table says it."""
    parts = [_CADENCE[job.cadence]]
    if job.idle_minutes:
        parts.append(f"idle {job.idle_minutes} min")
    if job.feature:
        parts.append(f"flag {job.feature}")
    return ", ".join(parts)


def _last(outcome: Outcome | None) -> LastRun | None:
    if outcome is None:
        return None
    return LastRun(outcome.started, outcome.seconds, outcome.returncode, outcome.trigger, outcome.error, outcome.ok)


def job_views(jobs: Sequence[Job], directory: Path) -> list[JobView]:
    outcomes = read_state(directory / STATE_NAME)
    return [
        JobView(
            name=job.name,
            description=job.description,
            cadence=cadence_text(job),
            triggers=tuple(sorted(str(trigger) for trigger in job.triggers)),
            timeout_minutes=job.timeout_minutes,
            last=_last(outcomes.get(job.name)),
        )
        for job in jobs
    ]


def _run_view(record: JobRun) -> RunView:
    return RunView(
        job=record.job,
        run_id=record.run_id,
        started=record.started,
        seconds=record.seconds,
        returncode=record.returncode,
        trigger=record.trigger,
        error=record.error,
        ok=record.ok,
        has_output=bool(record.output),
        trace_id=record.trace_id,
    )


def _outcome_run_id(outcome: Outcome) -> str:
    """The run that produced `outcome`; outcomes saved before runs had ids get one from their start."""
    return outcome.run_id or f"{outcome.trigger}-{outcome.started_at:%Y%m%d-%H%M%S}"


def _logged_output(directory: Path, job: str, outcome: Outcome) -> str | None:
    """A pre-history run's output: its section of the job log, while the log still holds it."""
    if outcome.run_id is None:
        return None
    lines = run_output(named_log_path(directory / "logs", job), outcome.run_id)
    return "\n".join(lines) + "\n" if lines else None


def _started(view: RunView) -> datetime:
    try:
        stamp = datetime.fromisoformat(view.started)
    except ValueError:
        return datetime.min.replace(tzinfo=UTC)
    return stamp if stamp.tzinfo else stamp.astimezone()


def run_views(directory: Path, *, job: str | None = None, limit: int = DEFAULT_LIMIT) -> list[RunView]:
    """The newest `limit` runs, newest first: the history, plus last outcomes it does not hold."""
    records = history.read_runs(directory, job=job, limit=history.KEEP_RUNS + history.TRIM_SLACK)
    known = {(record.job, record.run_id) for record in records}
    views = [_run_view(record) for record in records]
    for name, outcome in read_state(directory / STATE_NAME).items():
        run_id = _outcome_run_id(outcome)
        if (job is not None and name != job) or (name, run_id) in known:
            continue
        views.append(
            RunView(
                job=name,
                run_id=run_id,
                started=outcome.started,
                seconds=outcome.seconds,
                returncode=outcome.returncode,
                trigger=outcome.trigger,
                error=outcome.error,
                ok=outcome.ok,
                has_output=_logged_output(directory, name, outcome) is not None,
                trace_id="",
            )
        )
    views.sort(key=_started, reverse=True)
    return views[:limit]


def run_output_view(jobs: Sequence[Job], directory: Path, job: str, run_id: str) -> RunOutput:
    """What one run printed; `RouteNotFoundError` for a job the registry lacks or a run nobody recorded."""
    if job not in {item.name for item in jobs}:
        raise RouteNotFoundError(f"No job named {job!r}")
    if not history.is_run_id(run_id):
        raise RouteNotFoundError(f"No run {run_id!r} of {job}")
    record = history.find_run(directory, job, run_id)
    if record is not None:
        return RunOutput(job, run_id, history.read_output(directory, record))
    outcome = read_state(directory / STATE_NAME).get(job)
    if outcome is not None and _outcome_run_id(outcome) == run_id:
        return RunOutput(job, run_id, _logged_output(directory, job, outcome))
    raise RouteNotFoundError(f"No run {run_id!r} of {job}")


def registered_jobs() -> tuple[Job, ...]:
    return default_jobs(current_environment())


@dataclass(frozen=True, slots=True)
class RunRequest:
    #: One job to run whether or not it is due; None runs every job due on a tick.
    job: str | None = None


@dataclass(frozen=True, slots=True)
class RunStarted:
    job: str | None
    pid: int


def run_command(job: str | None) -> list[str]:
    """The coordinator's run, through this environment's `rig`, as a scheduled tick runs it."""
    command = [current_environment().script("rig"), "jobs", "run", "--trigger", "tick"]
    return [*command, "--only", job] if job else command


def start_job(jobs: Sequence[Job], job: str | None, start: Callable[[Sequence[str]], int]) -> RunStarted:
    """Start a coordinator run of *job*, or of every due job; `RouteNotFoundError` for a job the registry lacks."""
    if job is not None and job not in {item.name for item in jobs}:
        raise RouteNotFoundError(f"No job named {job!r}")
    return RunStarted(job=job, pid=start(run_command(job)))


@dataclass(frozen=True, slots=True)
class JobsRoute:
    """The `/api/ops/jobs` and `/api/ops/runs` route; the registry, directory and spawner are injectable for tests."""

    jobs: Callable[[], Sequence[Job]] = registered_jobs
    directory: Callable[[], Path] = jobs_dir
    start: Callable[[Sequence[str]], int] = spawn_detached

    def __call__(self, handler: BaseHTTPRequestHandler, request: Request, /) -> bool:
        path = request.path.rstrip("/")
        owned = (JOBS_PATH, RUNS_PATH)
        if path not in owned and not path.startswith(tuple(f"{prefix}/" for prefix in owned)):
            return False
        if path == RUN_PATH:
            return respond(handler, lambda: self._start(handler, request))
        return respond(handler, lambda: self._answer(request, path))

    def _start(self, handler: BaseHTTPRequestHandler, request: Request) -> str:
        if request.method != "POST":
            raise ValueError(f"{request.method} is not supported here; POST to start a run")
        job = decode_json(RunRequest, json_body(handler)).job
        return encode_json(RunStarted, start_job(self.jobs(), job, self.start))

    def _answer(self, request: Request, path: str) -> str:
        if request.method != "GET":
            raise ValueError(f"{request.method} is not supported here")
        directory = self.directory()
        if path == JOBS_PATH:
            return encode_json(list[JobView], job_views(self.jobs(), directory))
        if path == RUNS_PATH:
            limit = request.int_param("limit", default=DEFAULT_LIMIT, low=1, high=MAX_LIMIT)
            return encode_json(list[RunView], run_views(directory, job=request.param("job"), limit=limit))
        segments = path.removeprefix(f"{RUNS_PATH}/").split("/")
        if path.startswith(f"{RUNS_PATH}/") and len(segments) == 3 and segments[2] == "output":
            view = run_output_view(self.jobs(), directory, job=segments[0], run_id=segments[1])
            return encode_json(RunOutput, view)
        raise RouteNotFoundError(f"Nothing at {request.path}")
