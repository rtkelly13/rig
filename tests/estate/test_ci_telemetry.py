from __future__ import annotations

from rig.estate.ci_telemetry import (
    RawJobRecord,
    RawRunRecord,
    infer_runner_os,
    parse_job_telemetry,
    parse_run_telemetry,
)


def test_infer_runner_os():
    assert infer_runner_os("lint") == "UBUNTU"
    assert infer_runner_os("test") == "UBUNTU"
    assert infer_runner_os("test-windows") == "WINDOWS"
    assert infer_runner_os("build-macos") == "MACOS"


def test_parse_job_telemetry_applies_multipliers():
    raw_ubuntu = RawJobRecord(
        id=101,
        name="test",
        started_at="2026-10-10T12:00:00Z",
        completed_at="2026-10-10T12:02:00Z",  # 120s
    )
    job_u = parse_job_telemetry(raw_ubuntu)
    assert job_u.duration_seconds == 120.0
    assert job_u.billable_multiplier == 1.0
    assert job_u.billable_seconds == 120.0

    raw_win = RawJobRecord(
        id=102,
        name="test-windows",
        started_at="2026-10-10T12:00:00Z",
        completed_at="2026-10-10T12:02:00Z",  # 120s
    )
    job_w = parse_job_telemetry(raw_win)
    assert job_w.duration_seconds == 120.0
    assert job_w.billable_multiplier == 2.0
    assert job_w.billable_seconds == 240.0


def test_parse_run_telemetry_aggregates_billable_time():
    raw_run = RawRunRecord(
        id=1001,
        name="CI",
        event="pull_request",
        head_branch="feat/my-pr",
        conclusion="success",
        created_at="2026-10-10T12:00:00Z",
        run_started_at="2026-10-10T12:00:00Z",
        updated_at="2026-10-10T12:03:00Z",  # 180s wall clock
    )
    raw_jobs = [
        RawJobRecord(
            id=101,
            name="test",
            started_at="2026-10-10T12:00:00Z",
            completed_at="2026-10-10T12:02:00Z",  # 120s * 1 = 120s
        ),
        RawJobRecord(
            id=102,
            name="test-windows",
            started_at="2026-10-10T12:00:00Z",
            completed_at="2026-10-10T12:02:30Z",  # 150s * 2 = 300s
        ),
    ]

    telemetry = parse_run_telemetry("rtkelly13/shared-utilities", raw_run, raw_jobs)
    assert telemetry.run_id == 1001
    assert telemetry.run_duration_seconds == 180.0
    assert telemetry.total_billable_seconds == 420.0  # 120 + 300
    assert len(telemetry.jobs) == 2
