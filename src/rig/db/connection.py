"""SQLite database connection management for shared estate operations."""

from __future__ import annotations

import sqlite3
from pathlib import Path

from rig.roots import app_data_dir


def default_db_path() -> Path:
    """Location of the shared SQLite database accessed by both production and preview."""
    return app_data_dir() / "rig.db"


def get_db_connection(db_path: Path | None = None) -> sqlite3.Connection:
    """Open or create a WAL-mode SQLite database connection with busy timeout."""
    target_path = db_path or default_db_path()
    target_path.parent.mkdir(parents=True, exist_ok=True)

    conn = sqlite3.connect(str(target_path), timeout=10.0)
    # Enable WAL mode for non-blocking concurrent multi-process readers and writer
    conn.execute("PRAGMA journal_mode = WAL")
    conn.execute("PRAGMA busy_timeout = 10000")
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA synchronous = NORMAL")
    return conn
