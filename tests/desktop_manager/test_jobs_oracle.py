"""`rig jobs status|last|log` run in C# (Rig.Core.Jobs); the runner, the web page and the MCP still read these files.

`jobs-oracle.json` is what Python makes of a fixed set of inputs: the registry for a synthetic environment, the
exact bytes `write_state` and `write_run` produce, what `read_state` and `read_run` keep from damaged files, which
jobs `plan` takes and why, and the result and last-run cells `jobs status` prints. The C# tests replay the same
file, so the two sides cannot drift while both are live.

Regenerate after a deliberate change, then rerun both suites::

    uv run python -c "import tests.desktop_manager.test_jobs_oracle as o; o.regenerate()"
"""

from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path

import pytest

from desktop_manager.jobs.registry import Cadence, Environment, Job, Trigger, default_jobs
from desktop_manager.jobs.runner import plan
from desktop_manager.jobs.state import Decision, Outcome, RunRecord, read_run, read_state, write_run, write_state

pytestmark = pytest.mark.ci

ORACLE = Path(__file__).parents[1] / "Rig.Cli.Tests" / "Jobs" / "jobs-oracle.json"
LOCAL_APP_DATA = "/local"
ROOT_TOKEN = "{root}"


def _norm(value: str) -> str:
    """POSIX separators and no `.exe`, so the record reads the same on Windows."""
    text = value.replace("\\", "/")
    return text.removesuffix(".exe")


def _environment(*, workspace: bool) -> Environment:
    return Environment(
        scripts=Path("/rig/.venv/bin"),
        plan=Path("/plans/windows-workspace.json"),
        workspace_root=Path("/code/work/workspace") if workspace else None,
        personal_root=Path("/code/personal"),
        work_roots=(Path("/code/work"), Path("/elsewhere/work")),
        decisions_feed=Path("/state/jobs/decisions.json"),
        orca_user_data=Path("/orca/orca-personal"),
    )


def _job_record(job: Job) -> dict[str, object]:
    return {
        "name": job.name,
        "description": job.description,
        "argv": [_norm(arg) for arg in job.argv],
        "triggers": sorted(job.triggers),
        "cadence": str(job.cadence),
        "timeout_minutes": job.timeout_minutes,
        "requires": [_norm(str(path)) for path in job.requires],
        "idle_minutes": job.idle_minutes,
        "retry_exit_codes": sorted(job.retry_exit_codes),
        "detail_log": None if job.detail_log is None else _norm(str(job.detail_log)),
        "feature": job.feature,
    }


def _outcome(**fields: object) -> Outcome:
    base: dict[str, object] = {
        "started": "2026-11-12T09:00:00.123456+00:00",
        "seconds": 3.0,
        "returncode": 0,
        "trigger": "tick",
    }
    return Outcome(**(base | fields))  # pyright: ignore[reportArgumentType]


_STATES: list[dict[str, Outcome]] = [
    {},
    {
        "restore": _outcome(
            seconds=42.5, trigger="logon", run_id="logon-20261112-090000", summary="restore: 12 placed"
        ),
        "align": _outcome(seconds=1e-05, summary="align: 0 moved, 11 in place"),
        "estate-sync": _outcome(
            started="2026-11-11T22:15:03.5+01:00",
            seconds=125.0,
            returncode=1,
            error=None,
            summary="fatal: café <unreachable> & 'gone' +1",
            detail=("line one", 'tab\tand "quote"', "☃ snow"),
        ),
        "chrome-organise": _outcome(seconds=3600.0, returncode=None, error="timed out after 20 min"),
    },
]

_STATE_TEXTS: list[str] = [
    "",
    "not json",
    "[]",
    json.dumps({"ok": {"started": "2026-11-12T09:00:00+00:00", "seconds": 3, "trigger": "tick", "returncode": 0}}),
    json.dumps(
        {
            "bad-started": {"started": "yesterday", "seconds": 1.0, "trigger": "tick"},
            "missing-trigger": {"started": "2026-11-12T09:00:00+00:00", "seconds": 1.0},
            "bool-seconds": {"started": "2026-11-12T09:00:00+00:00", "seconds": True, "trigger": "tick"},
            "string-seconds": {"started": "2026-11-12T09:00:00+00:00", "seconds": "3", "trigger": "tick"},
            "not-an-object": 7,
            "loose-fields": {
                "started": "2026-11-12T09:00:00+00:00",
                "seconds": 2.5,
                "trigger": "logon",
                "returncode": "3",
                "error": 5,
                "run_id": None,
                "summary": ["x"],
                "detail": ["a", 1],
                "extra": "ignored",
            },
            "float-returncode": {"started": "2026-11-12T09:00Z", "seconds": 1e2, "trigger": "tick", "returncode": 3.0},
            "negative-returncode": {"started": "2026-11-12T09", "seconds": 1, "trigger": "tick", "returncode": -1},
            "date-only": {"started": "2026-11-12", "seconds": 0, "trigger": "logon", "error": "", "summary": ""},
            "space-and-compact-offset": {
                "started": "2026-11-12 09:00:00.1234567+0100",
                "seconds": 1.25,
                "trigger": "tick",
            },
            "null-started": {"started": None, "seconds": 1, "trigger": "tick"},
            "kept": {
                "started": "2026-11-12T08:00:00-05:00",
                "seconds": 0.5,
                "trigger": "tick",
                "returncode": True,
                "detail": ["end", "of", "output"],
            },
        }
    ),
]

_RUNS: list[RunRecord] = [
    RunRecord(
        run_id="logon-20261112-090000",
        trigger="logon",
        started="2026-11-12T09:00:00.000001+00:00",
        finished=None,
        decisions=(
            Decision("restore", ran=True, reason="every-run", outcome=_outcome(seconds=61.25, trigger="logon")),
            Decision("align", ran=False, reason="not on logon"),
            Decision("estate-sync", ran=True, reason="daily", outcome=_outcome(returncode=2, detail=("boom",))),
        ),
    ),
    RunRecord("tick-20261112-091500", "tick", "2026-11-12T09:15:00+00:00", "2026-11-12T09:16:00+00:00", ()),
]

_RUN_TEXTS: list[str] = [
    "{}",
    json.dumps(
        {"run_id": "r", "trigger": "tick", "started": "s", "decisions": [{"job": "a", "ran": "yes", "reason": "x"}]}
    ),
    json.dumps(
        {
            "run_id": "r",
            "trigger": "tick",
            "started": "2026-11-12T09:00:00+00:00",
            "finished": 3,
            "decisions": [
                {
                    "job": "a",
                    "ran": True,
                    "reason": "daily",
                    "outcome": {"started": "nope", "seconds": 1, "trigger": "tick"},
                },
                {"job": "b", "ran": True, "reason": "daily", "outcome": {"seconds": 1}},
                {"job": "c", "ran": False, "reason": "not due (daily)", "outcome": None},
            ],
        }
    ),
]


def _plan_jobs(root: Path) -> list[Job]:
    tick = frozenset({Trigger.TICK})
    both = frozenset({Trigger.LOGON, Trigger.TICK})
    return [
        Job("every", "", ("x",), tick, Cadence.EVERY_RUN, 5),
        Job(
            "logon-only", "", ("x",), frozenset({Trigger.LOGON}), Cadence.EVERY_RUN, 5, retry_exit_codes=frozenset({4})
        ),
        Job("boot", "", ("x",), both, Cadence.ONCE_PER_BOOT, 5),
        Job("daily", "", ("x",), both, Cadence.DAILY, 5),
        Job("needs", "", ("x",), tick, Cadence.EVERY_RUN, 5, requires=(root / "present", root / "absent")),
        Job("has", "", ("x",), tick, Cadence.EVERY_RUN, 5, requires=(root / "present",)),
        Job("flagged", "", ("x",), tick, Cadence.EVERY_RUN, 5, feature="claude-session-restore"),
    ]


_PLAN_STATE: dict[str, dict[str, object]] = {
    "logon-only": {"started": "2026-11-12T08:30:00+00:00", "returncode": 4, "trigger": "logon"},
    "boot": {"started": "2026-11-12T08:30:00+00:00", "returncode": 0, "trigger": "logon"},
    "daily": {"started": "2026-11-11T13:00:00+00:00", "returncode": 0, "trigger": "tick"},
}

_PLAN_CASES: list[dict[str, object]] = [
    {"trigger": "tick", "now": "2026-11-12T09:00:00+00:00", "booted": "2026-11-12T08:00:00+00:00", "flags": {}},
    {"trigger": "logon", "now": "2026-11-12T09:00:00+00:00", "booted": "2026-11-12T08:00:00+00:00", "flags": {}},
    {
        "trigger": "tick",
        "now": "2026-11-12T08:59:59+00:00",
        "booted": "2026-11-12T08:45:00+00:00",
        "flags": {"claude-session-restore": True},
    },
    {
        "trigger": "tick",
        "now": "2026-11-12T10:00:00+01:00",
        "booted": "2026-11-12T08:00:00+00:00",
        "flags": {},
        "force": True,
    },
    {
        "trigger": "logon",
        "now": "2026-11-12T09:00:00+00:00",
        "booted": "2026-11-12T08:00:00+00:00",
        "flags": {},
        "only": ["flagged", "daily"],
    },
    {
        "trigger": "tick",
        "now": "2026-11-12T09:00:00+00:00",
        "booted": "2026-11-12T08:00:00+00:00",
        "flags": {},
        "state": "none",
    },
]

_CELLS: list[Outcome] = [
    _outcome(),
    _outcome(seconds=0.5, summary="align: 0 moved"),
    _outcome(seconds=1.5, returncode=3),
    _outcome(seconds=2.5, returncode=None, error="timed out"),
    _outcome(seconds=59.49, error="killed", summary="half way"),
    _outcome(started="2026-11-08T23:59:00-08:00", seconds=1234.5),
    _outcome(seconds=0.0, returncode=None),
    _outcome(seconds=7.0, returncode=0, error="", summary=""),
]


def _stored(outcome: Outcome | None) -> dict[str, object] | None:
    if outcome is None:
        return None
    return {
        "started": outcome.started,
        "seconds": outcome.seconds,
        "returncode": outcome.returncode,
        "trigger": outcome.trigger,
        "error": outcome.error,
        "run_id": outcome.run_id,
        "summary": outcome.summary,
        "detail": list(outcome.detail),
    }


# The result cell, as `desktop-manager jobs status` and `jobs last` printed it before `rig jobs` took them over.
def _result(outcome: Outcome) -> str:
    status = "ok" if outcome.ok else (outcome.error or f"exit {outcome.returncode}")
    return f"{status}: {outcome.summary}" if outcome.summary else status


# The last-run cell, as the same commands printed it.
def _last_run(outcome: Outcome) -> str:
    return f"{outcome.started_at:%a %H:%M} ({outcome.seconds:.0f}s)"


def record(root: Path) -> dict[str, object]:
    """Everything the C# side is held to, computed from Python now. *root* is a scratch folder for `requires`."""
    (root / "present").write_text("", encoding="utf-8")
    state_dir = root / "state"
    written: list[dict[str, object]] = []
    for state in _STATES:
        write_state(state_dir / "state.json", state)
        text = (state_dir / "state.json").read_bytes().decode("utf-8").replace("\r\n", "\n")
        written.append({"state": {name: _stored(outcome) for name, outcome in state.items()}, "text": text})
    reads: list[dict[str, object]] = []
    for text in _STATE_TEXTS:
        (state_dir / "read.json").write_text(text, encoding="utf-8")
        parsed = read_state(state_dir / "read.json")
        reads.append({"text": text, "state": {name: _stored(outcome) for name, outcome in parsed.items()}})
    runs: list[dict[str, object]] = []
    for run in _RUNS:
        write_run(state_dir / "last-run.json", run)
        runs.append({"text": (state_dir / "last-run.json").read_bytes().decode("utf-8").replace("\r\n", "\n")})
    run_reads: list[dict[str, object]] = []
    for text in [*(item["text"] for item in runs), *_RUN_TEXTS]:
        assert isinstance(text, str)
        (state_dir / "read-run.json").write_text(text, encoding="utf-8")
        parsed_run = read_run(state_dir / "read-run.json")
        run_reads.append({"text": text, "run": None if parsed_run is None else _run_record(parsed_run)})
    plans: list[dict[str, object]] = []
    state = {name: _outcome(**fields) for name, fields in _PLAN_STATE.items()}
    for case in _PLAN_CASES:
        planned = plan(
            _plan_jobs(root),
            Trigger(str(case["trigger"])),
            {} if case.get("state") == "none" else state,
            now=datetime.fromisoformat(str(case["now"])),
            booted=datetime.fromisoformat(str(case["booted"])),
            only=frozenset(case.get("only", [])),  # pyright: ignore[reportArgumentType]
            force=bool(case.get("force", False)),
            flags=case["flags"],  # pyright: ignore[reportArgumentType]
        )
        rows = [[item.job.name, item.run, _norm(item.reason.replace(str(root), ROOT_TOKEN))] for item in planned]
        plans.append(case | {"planned": rows})
    return {
        "source": "desktop_manager.jobs (Python), recorded by tests/desktop_manager/test_jobs_oracle.py",
        "local_app_data": LOCAL_APP_DATA,
        "environment": {
            "scripts": "/rig/.venv/bin",
            "plan": "/plans/windows-workspace.json",
            "workspace_root": "/code/work/workspace",
            "personal_root": "/code/personal",
            "work_roots": ["/code/work", "/elsewhere/work"],
            "decisions_feed": "/state/jobs/decisions.json",
            "orca_user_data": "/orca/orca-personal",
        },
        "registry": [_job_record(job) for job in default_jobs(_environment(workspace=True))],
        "registry_without_workspace": [job.name for job in default_jobs(_environment(workspace=False))],
        "state_writes": written,
        "state_reads": reads,
        "run_writes": runs,
        "run_reads": run_reads,
        "plan_state": _PLAN_STATE,
        "plans": plans,
        "cells": [
            {"outcome": _stored(outcome), "result": _result(outcome), "last_run": _last_run(outcome)}
            for outcome in _CELLS
        ],
    }


def _run_record(run: RunRecord) -> dict[str, object]:
    return {
        "run_id": run.run_id,
        "trigger": run.trigger,
        "started": run.started,
        "finished": run.finished,
        "decisions": [
            {"job": item.job, "ran": item.ran, "reason": item.reason, "outcome": _stored(item.outcome)}
            for item in run.decisions
        ],
    }


def regenerate() -> None:
    """Rewrite the oracle from this Python. Only after a deliberate change to the files or the registry."""
    import os
    import tempfile

    os.environ["LOCALAPPDATA"] = LOCAL_APP_DATA
    with tempfile.TemporaryDirectory() as scratch:
        ORACLE.parent.mkdir(parents=True, exist_ok=True)
        ORACLE.write_text(json.dumps(record(Path(scratch)), indent=1) + "\n", encoding="utf-8")


def test_the_csharp_oracle_is_what_python_records(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("LOCALAPPDATA", LOCAL_APP_DATA)

    assert json.loads(ORACLE.read_text(encoding="utf-8")) == record(tmp_path)
