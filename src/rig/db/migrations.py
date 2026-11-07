"""Database migration runner for SQLite schema management."""

from __future__ import annotations

import sqlite3
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime


@dataclass(frozen=True, slots=True)
class MigrationRecord:
    version: int
    name: str
    applied_at: str


@dataclass(frozen=True, slots=True)
class Migration:
    version: int
    name: str
    sql: str


MIGRATIONS: Sequence[Migration] = (
    Migration(
        version=1,
        name="001_initial_schema",
        sql="""
        CREATE TABLE IF NOT EXISTS job_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            job_name TEXT NOT NULL,
            trigger TEXT NOT NULL,
            environment TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL,
            exit_code INTEGER,
            output TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_job_runs_started_at ON job_runs(started_at DESC);

        CREATE TABLE IF NOT EXISTS optical_presets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            project TEXT NOT NULL,
            ior REAL NOT NULL,
            dispersion REAL NOT NULL,
            roughness REAL NOT NULL,
            thickness REAL NOT NULL,
            distortion REAL NOT NULL,
            caustic_color TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        """,
    ),
    Migration(
        version=2,
        name="002_triage",
        sql="""
        CREATE TABLE triage_items (
            repo TEXT NOT NULL,
            number INTEGER NOT NULL,
            payload TEXT NOT NULL,
            PRIMARY KEY (repo, number)
        );
        CREATE TABLE triage_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            repo TEXT NOT NULL,
            number INTEGER NOT NULL,
            at TEXT NOT NULL,
            action TEXT NOT NULL,
            head TEXT NOT NULL,
            note TEXT NOT NULL,
            actor TEXT NOT NULL DEFAULT 'local',
            source_key TEXT UNIQUE,
            FOREIGN KEY (repo, number) REFERENCES triage_items(repo, number)
        );
        CREATE INDEX triage_event_item ON triage_events(repo, number, id);
        """,
    ),
    Migration(
        version=3,
        name="003_feature_flags",
        sql="""
        CREATE TABLE IF NOT EXISTS feature_flags (
            name TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            updated_at TEXT NOT NULL
        );
        """,
    ),
    Migration(
        version=4,
        name="004_drop_optical_presets",
        sql="""
        DROP TABLE IF EXISTS optical_presets;
        """,
    ),
)


class MigrationNumberingError(RuntimeError):
    """The migration list, or a database's applied history, is numbered inconsistently."""


def validate_migrations(migrations: Sequence[Migration]) -> None:
    """Require versions 1..N in order, each named ``NNN_<slug>`` after its version."""
    for expected, migration in enumerate(migrations, start=1):
        if migration.version != expected:
            raise MigrationNumberingError(
                f"migration {migration.name!r} has version {migration.version}; expected {expected}"
            )
        prefix = f"{migration.version:03d}_"
        if not migration.name.startswith(prefix) or len(migration.name) == len(prefix):
            raise MigrationNumberingError(
                f"migration version {migration.version} is named {migration.name!r}; "
                f"its name must start with {prefix!r}"
            )


def _check_applied_history(conn: sqlite3.Connection, migrations: Sequence[Migration]) -> None:
    """Refuse a database whose applied versions carry different migrations than this code.

    Two branches that each claim the same version leave a database that recorded one of
    them; the runner would then skip the other forever.
    """
    known = {migration.version: migration.name for migration in migrations}
    for record in get_applied_migrations(conn):
        expected = known.get(record.version)
        if expected is not None and expected != record.name:
            raise MigrationNumberingError(
                f"database recorded version {record.version} as {record.name!r}, "
                f"but this code numbers {expected!r} as version {record.version}"
            )


def _init_migrations_table(conn: sqlite3.Connection) -> None:
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """
    )


def current_schema_version(conn: sqlite3.Connection) -> int:
    """Return the highest applied schema version, or 0 if uninitialized."""
    _init_migrations_table(conn)
    cursor = conn.cursor()
    cursor.execute("SELECT COALESCE(MAX(version), 0) FROM schema_migrations")
    row = cursor.fetchone()
    if row is None or row[0] is None:
        return 0
    return int(str(row[0]))


def get_applied_migrations(conn: sqlite3.Connection) -> list[MigrationRecord]:
    """Return all applied schema migrations ordered by version."""
    _init_migrations_table(conn)
    cursor = conn.cursor()
    cursor.execute("SELECT version, name, applied_at FROM schema_migrations ORDER BY version ASC")
    results: list[MigrationRecord] = []
    for row in cursor.fetchall():
        results.append(
            MigrationRecord(
                version=int(str(row[0])),
                name=str(row[1]),
                applied_at=str(row[2]),
            )
        )
    return results


def run_migrations(conn: sqlite3.Connection) -> int:
    """Apply any pending schema migrations in an immediate transaction.

    Returns the count of new migrations applied. Raises ``MigrationNumberingError``,
    before changing anything, when the list or the database's history is misnumbered.
    """
    validate_migrations(MIGRATIONS)
    _init_migrations_table(conn)
    _check_applied_history(conn, MIGRATIONS)
    current_ver = current_schema_version(conn)
    applied_count = 0

    for migration in MIGRATIONS:
        if migration.version > current_ver:
            # Execute within transaction
            conn.execute("BEGIN IMMEDIATE")
            try:
                conn.executescript(migration.sql)
                now_str = datetime.now(UTC).isoformat()
                conn.execute(
                    "INSERT INTO schema_migrations (version, name, applied_at) VALUES (?, ?, ?)",
                    (migration.version, migration.name, now_str),
                )
                conn.execute(f"PRAGMA user_version = {migration.version}")
                conn.commit()
                applied_count += 1
            except Exception:
                conn.rollback()
                raise

    return applied_count
