"""SQLite persistence, migration engine, and backup management for rig."""

from __future__ import annotations

from rig.db.backup import backup_database, default_backup_dir
from rig.db.connection import default_db_path, get_db_connection
from rig.db.migrations import (
    MigrationRecord,
    current_schema_version,
    get_applied_migrations,
    run_migrations,
)

__all__ = [
    "MigrationRecord",
    "backup_database",
    "current_schema_version",
    "default_backup_dir",
    "default_db_path",
    "get_applied_migrations",
    "get_db_connection",
    "run_migrations",
]
