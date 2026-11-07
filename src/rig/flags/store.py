"""Reading and writing flag overrides in the `feature_flags` table."""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from rig.db.connection import get_db_connection
from rig.db.migrations import run_migrations
from rig.flags.registry import FLAGS, Flag, flag_named


@dataclass(frozen=True, slots=True)
class FlagState:
    flag: Flag
    override: bool | None

    @property
    def enabled(self) -> bool:
        return self.flag.default if self.override is None else self.override


def _connect(db_path: Path | None) -> sqlite3.Connection:
    conn = get_db_connection(db_path)
    run_migrations(conn)
    return conn


def flag_states(db_path: Path | None = None) -> list[FlagState]:
    """Every declared flag with its stored override, if any. Rows for undeclared names are ignored."""
    conn = _connect(db_path)
    try:
        rows = conn.execute("SELECT name, enabled FROM feature_flags").fetchall()
    finally:
        conn.close()
    overrides = {str(row[0]): bool(row[1]) for row in rows}
    return [FlagState(flag, overrides.get(flag.name)) for flag in FLAGS]


def effective_flags(db_path: Path | None = None) -> dict[str, bool]:
    """Each declared flag's on/off state: the stored override, else the default."""
    return {state.flag.name: state.enabled for state in flag_states(db_path)}


def is_enabled(name: str, db_path: Path | None = None) -> bool:
    flag_named(name)
    return effective_flags(db_path)[name]


def set_override(name: str, enabled: bool, db_path: Path | None = None) -> None:
    flag_named(name)
    conn = _connect(db_path)
    try:
        conn.execute(
            """
            INSERT INTO feature_flags (name, enabled, updated_at) VALUES (?, ?, ?)
            ON CONFLICT(name) DO UPDATE SET enabled = excluded.enabled, updated_at = excluded.updated_at
            """,
            (name, int(enabled), datetime.now(UTC).isoformat()),
        )
        conn.commit()
    finally:
        conn.close()


def clear_override(name: str, db_path: Path | None = None) -> bool:
    """Remove the stored override; True when there was one."""
    flag_named(name)
    conn = _connect(db_path)
    try:
        cursor = conn.execute("DELETE FROM feature_flags WHERE name = ?", (name,))
        conn.commit()
        return cursor.rowcount > 0
    finally:
        conn.close()
