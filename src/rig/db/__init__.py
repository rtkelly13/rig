"""SQLite persistence, migration engine, and backup management for rig."""

from __future__ import annotations

from rig.db.backup import backup_database, default_backup_dir
from rig.db.connection import default_db_path, get_db_connection
from rig.db.jobs_repo import JobRunRecord, get_recent_job_runs, record_job_run, update_job_run
from rig.db.migrations import (
    MigrationRecord,
    current_schema_version,
    get_applied_migrations,
    run_migrations,
)
from rig.db.presets_repo import (
    OpticalPresetRecord,
    get_presets_for_project,
    save_optical_preset,
)

__all__ = [
    "JobRunRecord",
    "MigrationRecord",
    "OpticalPresetRecord",
    "backup_database",
    "current_schema_version",
    "default_backup_dir",
    "default_db_path",
    "get_applied_migrations",
    "get_db_connection",
    "get_presets_for_project",
    "get_recent_job_runs",
    "record_job_run",
    "run_migrations",
    "save_optical_preset",
    "update_job_run",
]
