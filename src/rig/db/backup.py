"""Automated SQLite online backup and archive management."""

from __future__ import annotations

import sqlite3
from datetime import UTC, datetime
from pathlib import Path

from rig.db.connection import default_db_path
from rig.roots import app_data_dir


def default_backup_dir() -> Path:
    """Directory where rolling database backups are archived."""
    return app_data_dir() / "backups"


def backup_database(
    db_path: Path | None = None,
    backup_dir: Path | None = None,
    max_backups: int = 10,
) -> Path:
    """Perform a consistent online SQLite backup and prune older snapshots.

    Returns the path of the created backup file.
    """
    src_path = db_path or default_db_path()
    if not src_path.is_file():
        raise FileNotFoundError(f"Database file does not exist: {src_path}")

    target_dir = backup_dir or default_backup_dir()
    target_dir.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.now(UTC).strftime("%Y%m%d_%H%M%S")
    backup_file = target_dir / f"rig_{timestamp}.db"

    with sqlite3.connect(str(src_path)) as src, sqlite3.connect(str(backup_file)) as dst:
        src.backup(dst)

    # Prune snapshots older than max_backups
    backups = sorted(target_dir.glob("rig_*.db"), key=lambda p: p.stat().st_mtime)
    if len(backups) > max_backups:
        to_prune = backups[: len(backups) - max_backups]
        for old_file in to_prune:
            old_file.unlink(missing_ok=True)

    return backup_file
