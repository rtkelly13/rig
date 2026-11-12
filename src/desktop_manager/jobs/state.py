"""What the coordinator remembers between runs: each job's last outcome, and each trigger's last run.

`state.json` maps job name to its last `Outcome`. `last-<trigger>.json` is the
latest run on that trigger as a `RunRecord`: every job it ran or skipped, and why,
so `rig jobs last --trigger logon` answers "what did sign-in do?" from one file.
`Rig.Core.Jobs` reads and writes both files too, so a change to their shape changes
`tests/Rig.Cli.Tests/Jobs/jobs-oracle.json` and the C# with it; an unreadable file
reads as empty, which only makes every job due once.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta
from pathlib import Path

from pydantic import StrictBool, StrictFloat, StrictInt, StrictStr

from desktop_manager.jobs.registry import DAILY_HOURS, Cadence, Job
from rig.typed_json import Lenient, try_decode_json

STATE_NAME = "state.json"
LOCK_NAME = "run.lock"
LAST_RUN_NAME = "last-{trigger}.json"


def jobs_dir() -> Path:
    from desktop_manager.runtime import base_dir

    return base_dir() / "jobs"


def logs_dir() -> Path:
    return jobs_dir() / "logs"


def last_run_path(directory: Path, trigger: str) -> Path:
    return directory / LAST_RUN_NAME.format(trigger=trigger)


@dataclass(frozen=True, slots=True)
class Outcome:
    started: str
    seconds: float
    returncode: int | None
    trigger: str
    error: str | None = None
    #: The coordinator run that started it; its log section is headed with this id.
    run_id: str | None = None
    #: The last line the job printed, such as "align: 0 moved, 11 in place".
    summary: str | None = None
    #: When it failed, the end of its output: the reason, without opening the log.
    detail: tuple[str, ...] = ()

    @property
    def started_at(self) -> datetime:
        return datetime.fromisoformat(self.started)

    @property
    def ok(self) -> bool:
        return self.returncode == 0 and self.error is None


def read_state(path: Path) -> dict[str, Outcome]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return {}
    stored = try_decode_json(dict[str, Lenient[_StoredOutcome]], text) or {}
    parsed = {name: _outcome(entry) for name, entry in stored.items() if entry is not None}
    return {name: outcome for name, outcome in parsed.items() if outcome is not None}


@dataclass(frozen=True, slots=True)
class _StoredOutcome:
    started: StrictStr
    seconds: StrictInt | StrictFloat
    trigger: StrictStr
    returncode: Lenient[StrictInt] = None
    error: Lenient[StrictStr] = None
    run_id: Lenient[StrictStr] = None
    summary: Lenient[StrictStr] = None
    detail: Lenient[tuple[StrictStr, ...]] = None


def _outcome(stored: _StoredOutcome) -> Outcome | None:
    try:
        datetime.fromisoformat(stored.started)
    except ValueError:
        return None
    return Outcome(
        started=stored.started,
        seconds=float(stored.seconds),
        returncode=stored.returncode,
        trigger=stored.trigger,
        error=stored.error,
        run_id=stored.run_id,
        summary=stored.summary,
        detail=stored.detail or (),
    )


def write_state(path: Path, state: dict[str, Outcome]) -> None:
    _write_json(path, {name: asdict(outcome) for name, outcome in sorted(state.items())})


@dataclass(frozen=True, slots=True)
class Decision:
    """One job in one run: whether it ran, why, and how it went."""

    job: str
    ran: bool
    reason: str
    outcome: Outcome | None = None


@dataclass(frozen=True, slots=True)
class RunRecord:
    run_id: str
    trigger: str
    started: str
    #: None while the run is still going, or when it was killed part-way.
    finished: str | None
    decisions: tuple[Decision, ...]

    @property
    def failed(self) -> tuple[Decision, ...]:
        return tuple(item for item in self.decisions if item.outcome is not None and not item.outcome.ok)


@dataclass(frozen=True, slots=True)
class _StoredDecision:
    job: StrictStr
    ran: StrictBool
    reason: StrictStr
    outcome: Lenient[_StoredOutcome] = None


@dataclass(frozen=True, slots=True)
class _StoredRun:
    run_id: StrictStr
    trigger: StrictStr
    started: StrictStr
    decisions: tuple[_StoredDecision, ...]
    finished: Lenient[StrictStr] = None


def read_run(path: Path) -> RunRecord | None:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    stored = try_decode_json(_StoredRun, text)
    if stored is None:
        return None
    decisions = tuple(
        Decision(
            job=item.job,
            ran=item.ran,
            reason=item.reason,
            outcome=_outcome(item.outcome) if item.outcome is not None else None,
        )
        for item in stored.decisions
    )
    return RunRecord(stored.run_id, stored.trigger, stored.started, stored.finished, decisions)


def write_run(path: Path, record: RunRecord) -> None:
    _write_json(path, asdict(record))


def _write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def is_due(job: Job, last: Outcome | None, *, now: datetime, booted: datetime) -> bool:
    """Whether `job`'s cadence wants it now. Triggers are checked by the caller."""
    if job.cadence is Cadence.EVERY_RUN or last is None:
        return True
    if job.cadence is Cadence.ONCE_PER_BOOT:
        return last.started_at < booted
    return now - last.started_at >= timedelta(hours=DAILY_HOURS)
