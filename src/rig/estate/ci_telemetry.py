"""CI Run telemetry and utilisation auditing across estate repositories."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime

from rig import proc
from rig.typed_json import decode, load_json

OS_MULTIPLIERS = {
    "UBUNTU": 1.0,
    "LINUX": 1.0,
    "WINDOWS": 2.0,
    "MACOS": 10.0,
}


@dataclass(slots=True, frozen=True)
class RawJobRecord:
    id: int = 0
    name: str = "unknown"
    started_at: str | None = None
    completed_at: str | None = None


@dataclass(slots=True, frozen=True)
class RawRunRecord:
    id: int = 0
    name: str = "Unknown"
    event: str = "unknown"
    head_branch: str | None = None
    conclusion: str | None = None
    created_at: str = ""
    run_started_at: str | None = None
    updated_at: str | None = None


@dataclass(slots=True, frozen=True)
class RawRunsResponse:
    workflow_runs: tuple[RawRunRecord, ...] = ()


@dataclass(slots=True, frozen=True)
class RawJobsResponse:
    jobs: tuple[RawJobRecord, ...] = ()


@dataclass(frozen=True)
class JobTelemetry:
    job_id: int
    name: str
    runner_os: str  # UBUNTU, WINDOWS, MACOS, UNKNOWN
    duration_seconds: float
    billable_multiplier: float
    billable_seconds: float


@dataclass(frozen=True)
class RunTelemetry:
    repo: str
    run_id: int
    workflow_name: str
    event: str
    head_branch: str
    conclusion: str  # success, failure, cancelled, etc.
    created_at: str
    run_duration_seconds: float
    total_billable_seconds: float
    jobs: list[JobTelemetry] = field(default_factory=list)


def infer_runner_os(job_name: str) -> str:
    """Infer the runner OS platform from the job name."""
    name_lower = job_name.lower()
    if "windows" in name_lower or "win32" in name_lower:
        return "WINDOWS"
    if "macos" in name_lower or "darwin" in name_lower or "mac" in name_lower:
        return "MACOS"
    return "UBUNTU"


def parse_job_telemetry(raw_job: RawJobRecord) -> JobTelemetry:
    """Parse a single GitHub Actions job record into JobTelemetry."""
    started_at = raw_job.started_at
    completed_at = raw_job.completed_at

    duration_sec = 0.0
    if started_at and completed_at:
        try:
            start_dt = datetime.fromisoformat(started_at.replace("Z", "+00:00"))
            end_dt = datetime.fromisoformat(completed_at.replace("Z", "+00:00"))
            duration_sec = max(0.0, (end_dt - start_dt).total_seconds())
        except (ValueError, TypeError):
            duration_sec = 0.0

    runner_os = infer_runner_os(raw_job.name)
    multiplier = OS_MULTIPLIERS.get(runner_os, 1.0)
    billable_seconds = duration_sec * multiplier

    return JobTelemetry(
        job_id=raw_job.id,
        name=raw_job.name,
        runner_os=runner_os,
        duration_seconds=duration_sec,
        billable_multiplier=multiplier,
        billable_seconds=billable_seconds,
    )


def parse_run_telemetry(
    repo: str, raw_run: RawRunRecord, raw_jobs: tuple[RawJobRecord, ...] | list[RawJobRecord]
) -> RunTelemetry:
    """Parse a workflow run record and its jobs into RunTelemetry."""
    jobs = [parse_job_telemetry(j) for j in raw_jobs]
    total_billable_seconds = sum(j.billable_seconds for j in jobs)

    started_at = raw_run.run_started_at or raw_run.created_at
    updated_at = raw_run.updated_at
    run_duration_sec = 0.0
    if started_at and updated_at:
        try:
            start_dt = datetime.fromisoformat(started_at.replace("Z", "+00:00"))
            end_dt = datetime.fromisoformat(updated_at.replace("Z", "+00:00"))
            run_duration_sec = max(0.0, (end_dt - start_dt).total_seconds())
        except (ValueError, TypeError):
            run_duration_sec = 0.0

    return RunTelemetry(
        repo=repo,
        run_id=raw_run.id,
        workflow_name=raw_run.name,
        event=raw_run.event,
        head_branch=raw_run.head_branch or "",
        conclusion=raw_run.conclusion or "unknown",
        created_at=raw_run.created_at,
        run_duration_seconds=run_duration_sec,
        total_billable_seconds=total_billable_seconds,
        jobs=jobs,
    )


def fetch_repo_runs_via_gh(repo_slug: str, limit: int = 20) -> tuple[RawRunRecord, ...]:
    """Fetch recent workflow runs for a repo using gh api."""
    res = proc.gh("api", f"repos/{repo_slug}/actions/runs?per_page={limit}")
    if not res.ok:
        return ()
    try:
        parsed = load_json(res.out)
        resp = decode(RawRunsResponse, parsed)
        return resp.workflow_runs
    except (ValueError, TypeError):
        return ()


def fetch_run_jobs_via_gh(repo_slug: str, run_id: int) -> tuple[RawJobRecord, ...]:
    """Fetch jobs for a specific workflow run using gh api."""
    res = proc.gh("api", f"repos/{repo_slug}/actions/runs/{run_id}/jobs")
    if not res.ok:
        return ()
    try:
        parsed = load_json(res.out)
        resp = decode(RawJobsResponse, parsed)
        return resp.jobs
    except (ValueError, TypeError):
        return ()
