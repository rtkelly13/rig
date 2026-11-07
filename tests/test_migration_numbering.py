"""Migrations stay numbered 1..N, and a database recorded under other numbering is refused."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.db.connection import get_db_connection
from rig.db.migrations import (
    MIGRATIONS,
    Migration,
    MigrationNumberingError,
    get_applied_migrations,
    run_migrations,
    validate_migrations,
)

pytestmark = pytest.mark.ci


def test_the_shipped_migrations_are_numbered_one_to_n() -> None:
    validate_migrations(MIGRATIONS)


@pytest.mark.parametrize(
    ("migrations", "message"),
    [
        pytest.param(
            [Migration(1, "001_a", ""), Migration(1, "001_b", "")],
            "has version 1; expected 2",
            id="duplicate version",
        ),
        pytest.param(
            [Migration(1, "001_a", ""), Migration(3, "003_c", "")],
            "has version 3; expected 2",
            id="gap",
        ),
        pytest.param(
            [Migration(2, "002_b", ""), Migration(1, "001_a", "")],
            "has version 2; expected 1",
            id="out of order",
        ),
        pytest.param(
            [Migration(1, "001_a", ""), Migration(2, "003_b", "")],
            "must start with '002_'",
            id="name disagrees with version",
        ),
        pytest.param([Migration(1, "001_", "")], "must start with '001_'", id="no slug"),
    ],
)
def test_misnumbered_migrations_are_rejected(migrations: list[Migration], message: str) -> None:
    with pytest.raises(MigrationNumberingError, match=message):
        validate_migrations(migrations)


def test_a_database_that_recorded_another_migration_under_a_version_is_refused(tmp_path: Path) -> None:
    conn = get_db_connection(tmp_path / "rig.db")
    try:
        run_migrations(conn)
        conn.execute("UPDATE schema_migrations SET name = '001_from_another_branch' WHERE version = 1")
        conn.commit()

        with pytest.raises(MigrationNumberingError, match="recorded version 1 as '001_from_another_branch'"):
            run_migrations(conn)
    finally:
        conn.close()


def test_a_database_ahead_of_this_code_is_left_alone(tmp_path: Path) -> None:
    conn = get_db_connection(tmp_path / "rig.db")
    try:
        run_migrations(conn)
        future = len(MIGRATIONS) + 1
        conn.execute(
            "INSERT INTO schema_migrations (version, name, applied_at) VALUES (?, ?, 'now')",
            (future, f"{future:03d}_newer"),
        )
        conn.commit()

        assert run_migrations(conn) == 0
        assert [record.version for record in get_applied_migrations(conn)][-1] == future
    finally:
        conn.close()
