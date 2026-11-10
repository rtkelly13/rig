"""The jobs run history: every job run appended with its output, bounded, and read back newest first."""

from __future__ import annotations

import os
from dataclasses import replace
from datetime import datetime, timedelta
from pathlib import Path

import pytest

from desktop_manager.jobs import history
from desktop_manager.jobs.history import JobRun, append_run, find_run, read_output, read_runs, save_output
from desktop_manager.jobs.registry import Cadence, Job, Trigger
from desktop_manager.jobs.runner import log_path, run
from desktop_manager.jobs.state import STATE_NAME, read_state
from rig import tracing

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 10, 9, 0).astimezone()
BOOTED = NOW - timedelta(hours=1)


def _record(job: str, run_id: str, *, returncode: int | None = 0, output: str = "") -> JobRun:
    return JobRun(
        job=job,
        run_id=run_id,
        started=NOW.isoformat(timespec="seconds"),
        seconds=1.5,
        returncode=returncode,
        trigger="tick",
        output=output,
    )


def _job(name: str) -> Job:
    return Job(
        name=name,
        description=name,
        argv=("tool", name),
        triggers=frozenset({Trigger.TICK}),
        cadence=Cadence.EVERY_RUN,
        timeout_minutes=1,
    )


def test_runs_read_back_newest_first_and_filter_by_job(tmp_path: Path) -> None:
    append_run(tmp_path, _record("a", "tick-20261010-090000"))
    append_run(tmp_path, _record("b", "tick-20261010-091500", returncode=1))
    append_run(tmp_path, _record("a", "tick-20261010-093000"))

    assert [(r.job, r.run_id) for r in read_runs(tmp_path)] == [
        ("a", "tick-20261010-093000"),
        ("b", "tick-20261010-091500"),
        ("a", "tick-20261010-090000"),
    ]
    assert [r.run_id for r in read_runs(tmp_path, job="a", limit=1)] == ["tick-20261010-093000"]
    failed = find_run(tmp_path, "b", "tick-20261010-091500")
    assert failed is not None
    assert not failed.ok


def test_unreadable_lines_are_skipped(tmp_path: Path) -> None:
    append_run(tmp_path, _record("a", "tick-20261010-090000"))
    with history.history_path(tmp_path).open("a", encoding="utf-8") as handle:
        handle.write("not json\n{}\n\n")
    assert [r.job for r in read_runs(tmp_path)] == ["a"]


def test_a_line_cut_off_mid_character_neither_blocks_recording_nor_reading(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(history, "KEEP_RUNS", 1)
    monkeypatch.setattr(history, "TRIM_SLACK", 0)
    append_run(tmp_path, _record("a", "tick-20261010-090000"))
    with history.history_path(tmp_path).open("ab") as handle:
        handle.write(b'{"job": "caf\xc3\n')

    append_run(tmp_path, _record("a", "tick-20261010-091500"))

    assert [r.run_id for r in read_runs(tmp_path)] == ["tick-20261010-091500"]


def test_the_history_is_trimmed_back_once_well_past_its_bound(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(history, "KEEP_RUNS", 3)
    monkeypatch.setattr(history, "TRIM_SLACK", 2)
    for minute in range(6):
        append_run(tmp_path, _record("a", f"tick-20261010-09{minute:02d}00"))

    lines = history.history_path(tmp_path).read_text(encoding="utf-8").splitlines()
    assert len(lines) == 3
    assert read_runs(tmp_path)[-1].run_id == "tick-20261010-090300"


def test_each_job_keeps_only_its_newest_outputs_whatever_their_trigger(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(history, "KEEP_OUTPUTS_PER_JOB", 2)
    for run_id in ("tick-20261010-090000", "logon-20261010-091500", "tick-20261010-093000"):
        assert save_output(tmp_path, "a", run_id, f"{run_id}\n".encode()) == f"runs/a/{run_id}.log"

    kept = sorted(path.name for path in (tmp_path / "runs" / "a").iterdir())
    assert kept == ["logon-20261010-091500.log", "tick-20261010-093000.log"]


def test_output_is_capped_at_its_end_and_nothing_is_saved_for_an_empty_run(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(history, "OUTPUT_LIMIT_BYTES", 4)
    relative = save_output(tmp_path, "a", "tick-20261010-090000", b"0123456789")
    assert read_output(tmp_path, _record("a", "tick-20261010-090000", output=relative)) == "6789"
    assert save_output(tmp_path, "a", "tick-20261010-091500", b"") == ""
    assert save_output(tmp_path, "a", "../../escape", b"data") == ""


def test_read_output_refuses_a_path_outside_the_runs_directory(tmp_path: Path) -> None:
    (tmp_path / "state.json").write_text("{}", encoding="utf-8")
    assert read_output(tmp_path, _record("a", "tick-20261010-090000", output="state.json")) is None
    assert read_output(tmp_path, _record("a", "tick-20261010-090000", output="runs/a/pruned.log")) is None


def test_a_coordinator_run_records_each_job_with_its_output_and_trace(tmp_path: Path) -> None:
    def execute(job: Job, logs: Path, run_id: str) -> int:
        path = log_path(logs, job)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as log:
            log.write(f"--- 2026-10-10 09:00:00 [{run_id}] tool {job.name}\n{job.name} said hi\n")
        if job.name == "crash":
            raise RuntimeError("boom")
        return 0

    log = log_path(tmp_path / "logs", _job("fine"))
    log.parent.mkdir(parents=True)
    log.write_text("--- earlier run\nold output\n", encoding="utf-8")

    run_id = "tick-20261010-090000"
    code = run(
        [_job("fine"), _job("crash")],
        Trigger.TICK,
        tmp_path,
        booted=BOOTED,
        execute=execute,
        clock=lambda: NOW,
        report=lambda _: None,
        run_id=run_id,
    )

    assert code == 1
    crash, fine = read_runs(tmp_path)
    assert (fine.job, fine.run_id, fine.returncode, fine.ok) == ("fine", run_id, 0, True)
    assert (crash.returncode, crash.error) == (None, "RuntimeError: boom")
    assert fine.trace_id == tracing.current_trace_id()
    assert len(fine.span_id) == 16
    assert fine.span_id != crash.span_id
    output = read_output(tmp_path, fine)
    assert output is not None
    assert "fine said hi" in output
    assert "old output" not in output
    # The state file still holds each job's last outcome, as before.
    assert set(read_state(tmp_path / STATE_NAME)) == {"fine", "crash"}


def test_a_job_that_redirects_its_stdout_saves_this_runs_part_of_its_own_log(tmp_path: Path) -> None:
    own_log = tmp_path / "organise.log"
    own_log.write_text("--- earlier organise\nClosed 3/3\n", encoding="utf-8")
    job = replace(_job("chrome"), detail_log=own_log)

    def execute(job: Job, logs: Path, run_id: str) -> int:
        path = log_path(logs, job)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as log:
            log.write(f"--- 2026-10-10 09:00:00 [{run_id}] tool chrome\n--- exit 1\n")
        with own_log.open("a", encoding="utf-8") as log:
            log.write("--- organise\nNot closed #236: tab is still present\nClosed 0/1\n")
        return 1

    run([job], Trigger.TICK, tmp_path, booted=BOOTED, execute=execute, clock=lambda: NOW, report=lambda _: None)

    [record] = read_runs(tmp_path)
    output = read_output(tmp_path, record)
    assert output is not None
    assert "tool chrome" in output
    assert "Not closed #236: tab is still present" in output
    assert "Closed 3/3" not in output


def test_an_append_that_leaves_the_mtime_unchanged_is_still_this_runs_output(tmp_path: Path) -> None:
    """Windows' clock can stamp an append with the mtime the log already had; the size still moves."""
    own_log = tmp_path / "organise.log"
    own_log.write_text("--- earlier organise\n", encoding="utf-8")
    stamp = own_log.stat().st_mtime_ns
    job = replace(_job("chrome"), detail_log=own_log)

    def execute(job: Job, logs: Path, run_id: str) -> int:
        log_path(logs, job).parent.mkdir(parents=True, exist_ok=True)
        with own_log.open("a", encoding="utf-8") as log:
            log.write("--- organise\nNot closed #236\n")
        os.utime(own_log, ns=(stamp, stamp))
        return 1

    run([job], Trigger.TICK, tmp_path, booted=BOOTED, execute=execute, clock=lambda: NOW, report=lambda _: None)

    [record] = read_runs(tmp_path)
    assert "Not closed #236" in (read_output(tmp_path, record) or "")
