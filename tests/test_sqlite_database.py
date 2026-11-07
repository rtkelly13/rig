"""Unit tests for shared SQLite database, schema migrations, and online backup."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.db.backup import backup_database
from rig.db.connection import get_db_connection
from rig.db.jobs_repo import get_recent_job_runs, record_job_run, update_job_run
from rig.db.migrations import (
    MIGRATIONS,
    current_schema_version,
    get_applied_migrations,
    run_migrations,
)
from rig.db.presets_repo import get_presets_for_project, save_optical_preset

pytestmark = pytest.mark.ci

LATEST = MIGRATIONS[-1].version


def test_db_connection_and_wal_mode(tmp_path: Path) -> None:
    db_file = tmp_path / "test_rig.db"
    conn = get_db_connection(db_file)
    try:
        cursor = conn.cursor()
        cursor.execute("PRAGMA journal_mode")
        row = cursor.fetchone()
        assert row is not None
        assert str(row[0]).lower() == "wal"
    finally:
        conn.close()


def test_migrations_are_idempotent(tmp_path: Path) -> None:
    db_file = tmp_path / "test_migrations.db"
    conn = get_db_connection(db_file)
    try:
        assert current_schema_version(conn) == 0

        # First run applies all migrations
        applied = run_migrations(conn)
        assert applied >= 1
        assert current_schema_version(conn) == LATEST

        # Check PRAGMA user_version is updated
        cursor = conn.cursor()
        cursor.execute("PRAGMA user_version")
        uv_row = cursor.fetchone()
        assert uv_row is not None and uv_row[0] == LATEST

        # Check get_applied_migrations
        records = get_applied_migrations(conn)
        assert len(records) >= 1
        assert records[0].version == 1
        assert records[0].name == "001_initial_schema"
        assert len(records[0].applied_at) > 0

        # Second run applies zero additional migrations
        applied_again = run_migrations(conn)
        assert applied_again == 0
        assert current_schema_version(conn) == LATEST
    finally:
        conn.close()


def test_job_runs_record_and_query(tmp_path: Path) -> None:
    db_file = tmp_path / "test_jobs.db"
    conn = get_db_connection(db_file)
    try:
        run_migrations(conn)

        run_id = record_job_run(
            conn=conn,
            job_name="tick-maintenance",
            trigger="tick",
            environment="PROD",
            started_at="2026-10-24T14:00:00Z",
            status="running",
        )
        assert run_id > 0

        update_job_run(
            conn=conn,
            run_id=run_id,
            finished_at="2026-10-24T14:00:05Z",
            status="success",
            exit_code=0,
            output="Cleaned up 0 tabs",
        )

        history = get_recent_job_runs(conn, limit=10)
        assert len(history) == 1
        assert history[0].id == run_id
        assert history[0].job_name == "tick-maintenance"
        assert history[0].status == "success"
        assert history[0].exit_code == 0
        assert history[0].environment == "PROD"
    finally:
        conn.close()


def test_database_backup_and_pruning(tmp_path: Path) -> None:
    db_file = tmp_path / "test_backup.db"
    backup_dir = tmp_path / "backups"

    conn = get_db_connection(db_file)
    try:
        run_migrations(conn)
        record_job_run(
            conn=conn,
            job_name="backup-test",
            trigger="manual",
            environment="PREVIEW",
            started_at="2026-10-24T14:10:00Z",
            status="success",
        )
    finally:
        conn.close()

    # Perform online backup
    backup_path = backup_database(db_path=db_file, backup_dir=backup_dir, max_backups=2)
    assert backup_path.is_file()
    assert backup_path.stat().st_size > 0

    # Verify backup contains the migrated schema and recorded data
    backup_conn = get_db_connection(backup_path)
    try:
        assert current_schema_version(backup_conn) == LATEST
        runs = get_recent_job_runs(backup_conn)
        assert len(runs) == 1
        assert runs[0].job_name == "backup-test"
    finally:
        backup_conn.close()


def test_optical_presets_save_and_query(tmp_path: Path) -> None:
    db_file = tmp_path / "test_presets.db"
    conn = get_db_connection(db_file)
    try:
        run_migrations(conn)

        preset_id = save_optical_preset(
            conn=conn,
            name="high-dispersion-flint",
            project="Resultful",
            ior=1.75,
            dispersion=0.08,
            roughness=0.02,
            thickness=1.5,
            distortion=0.1,
            caustic_color="#39ff14",
            created_at="2026-10-24T15:00:00Z",
        )
        assert preset_id > 0

        presets = get_presets_for_project(conn, "Resultful")
        assert len(presets) == 1
        assert presets[0].name == "high-dispersion-flint"
        assert presets[0].ior == 1.75
        assert presets[0].caustic_color == "#39ff14"

        # Check empty for different project
        empty = get_presets_for_project(conn, "Diffra")
        assert len(empty) == 0
    finally:
        conn.close()
