"""The SQLite connection helpers Python features still use; rig's database itself belongs to C#.

``Rig.Core.Storage.RigDatabase`` owns the schema, its numbered migrations and every maintenance command
(``rig tools db status|migrate|backup``). The Python CLI no longer exposes database commands; this module exists only so
feature flags and the web child's ``StudioServer`` can open the same file until their last slices move to C#. Do not add
new schema or commands here.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

from rig.roots import app_data_dir

# The tables Python still reads and writes. C# owns their migration numbers; IF NOT EXISTS keeps the child usable when
# it runs against a fresh database in a test.
_SCHEMA = """
CREATE TABLE IF NOT EXISTS triage_items (
    repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (repo, number)
);
CREATE TABLE IF NOT EXISTS triage_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT, repo TEXT NOT NULL, number INTEGER NOT NULL,
    at TEXT NOT NULL, action TEXT NOT NULL, head TEXT NOT NULL, note TEXT NOT NULL,
    actor TEXT NOT NULL DEFAULT 'local', source_key TEXT UNIQUE,
    FOREIGN KEY (repo, number) REFERENCES triage_items(repo, number)
);
CREATE INDEX IF NOT EXISTS triage_event_item ON triage_events(repo, number, id);
CREATE TABLE IF NOT EXISTS feature_flags (
    name TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)), updated_at TEXT NOT NULL
);
"""


def default_db_path() -> Path:
    """The shared database C# and the remaining Python features use."""
    return app_data_dir() / "rig.db"


def connect(db_path: Path | None = None) -> sqlite3.Connection:
    """Open the WAL-mode database Python features share with C#."""
    target = db_path or default_db_path()
    target.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(target), timeout=10.0)
    conn.execute("PRAGMA journal_mode = WAL")
    conn.execute("PRAGMA busy_timeout = 10000")
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA synchronous = NORMAL")
    return conn


def ensure_schema(conn: sqlite3.Connection) -> None:
    """Create the tables Python features use when the C# migrator has not run yet (tests, a fresh child)."""
    conn.executescript(_SCHEMA)
    conn.commit()
