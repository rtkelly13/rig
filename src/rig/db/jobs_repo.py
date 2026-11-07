"""Repository queries for tracking job runs and executions."""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class JobRunRecord:
    id: int
    job_name: str
    trigger: str
    environment: str
    started_at: str
    finished_at: str | None
    status: str
    exit_code: int | None
    output: str | None


def record_job_run(
    conn: sqlite3.Connection,
    job_name: str,
    trigger: str,
    environment: str,
    started_at: str,
    finished_at: str | None = None,
    status: str = "running",
    exit_code: int | None = None,
    output: str | None = None,
) -> int:
    """Record or start a new job run entry, returning its row ID."""
    cursor = conn.cursor()
    cursor.execute(
        """
        INSERT INTO job_runs (
            job_name, trigger, environment, started_at, finished_at, status, exit_code, output
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (job_name, trigger, environment, started_at, finished_at, status, exit_code, output),
    )
    conn.commit()
    row_id = cursor.lastrowid
    return int(row_id) if row_id is not None else 0


def update_job_run(
    conn: sqlite3.Connection,
    run_id: int,
    finished_at: str,
    status: str,
    exit_code: int | None = None,
    output: str | None = None,
) -> None:
    """Update an existing job run with outcome metrics and exit status."""
    conn.execute(
        """
        UPDATE job_runs
        SET finished_at = ?, status = ?, exit_code = ?, output = ?
        WHERE id = ?
        """,
        (finished_at, status, exit_code, output, run_id),
    )
    conn.commit()


def get_recent_job_runs(conn: sqlite3.Connection, limit: int = 20) -> list[JobRunRecord]:
    """Retrieve the newest job runs in descending order of execution."""
    cursor = conn.cursor()
    cursor.execute(
        """
        SELECT id, job_name, trigger, environment, started_at, finished_at, status, exit_code, output
        FROM job_runs
        ORDER BY started_at DESC
        LIMIT ?
        """,
        (limit,),
    )
    results: list[JobRunRecord] = []
    for row in cursor.fetchall():
        run_id = int(str(row[0]))
        job_name = str(row[1])
        trigger = str(row[2])
        env = str(row[3])
        started = str(row[4])
        finished = str(row[5]) if row[5] is not None else None
        status = str(row[6])
        exit_code = int(str(row[7])) if row[7] is not None else None
        output = str(row[8]) if row[8] is not None else None
        results.append(
            JobRunRecord(
                id=run_id,
                job_name=job_name,
                trigger=trigger,
                environment=env,
                started_at=started,
                finished_at=finished,
                status=status,
                exit_code=exit_code,
                output=output,
            )
        )
    return results
