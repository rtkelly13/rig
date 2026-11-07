"""SQLite triage snapshots and append-only interactions; refresh never deletes history."""

from __future__ import annotations

import json
import sqlite3
from contextlib import closing
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

from rig.db.connection import get_db_connection
from rig.db.migrations import run_migrations
from rig.triage.models import Activity, Entry, Event, Interaction, Item, rank
from rig.typed_json import decode_json


def entries(conn: sqlite3.Connection) -> list[Entry]:
    """Read all tracked items, including closed items and their history."""
    result: list[Entry] = []
    for row in conn.execute("SELECT payload FROM triage_items ORDER BY repo, number").fetchall():
        item = decode_json(Item, str(row[0]))
        events = conn.execute(
            "SELECT id, at, action, head, note, actor FROM triage_events WHERE repo = ? AND number = ? ORDER BY id",
            (item.repo, item.number),
        ).fetchall()
        result.append(
            Entry(
                item,
                tuple(Event(int(str(r[0])), str(r[1]), str(r[2]), str(r[3]), str(r[4]), str(r[5])) for r in events),
            )
        )
    return result


def queue(conn: sqlite3.Connection) -> list[Entry]:
    """Open, nondeferred items, PRs before the separate issue queue."""
    active = [
        entry
        for entry in entries(conn)
        if entry.item.in_scope and entry.item.state == "OPEN" and not entry.deferred and not entry.archived
    ]
    prs = sorted((entry for entry in active if entry.item.kind == "pr"), key=lambda entry: rank(entry.item))
    issues = sorted((entry for entry in active if entry.item.kind == "issue"), key=lambda entry: rank(entry.item))
    return [*prs, *issues]


def save(conn: sqlite3.Connection, items: list[Item]) -> None:
    """Caller owns the transaction; record observed changes only when the snapshot changes."""
    for item in items:
        payload = json.dumps(asdict(item), sort_keys=True)
        old = conn.execute(
            "SELECT payload FROM triage_items WHERE repo = ? AND number = ?", (item.repo, item.number)
        ).fetchone()
        if old is not None and str(old[0]) == payload:
            continue
        conn.execute(
            "INSERT INTO triage_items(repo, number, payload) VALUES (?, ?, ?) "
            "ON CONFLICT(repo, number) DO UPDATE SET payload = excluded.payload",
            (item.repo, item.number, payload),
        )
        conn.execute(
            "INSERT INTO triage_events(repo, number, at, action, head, note, actor) VALUES (?, ?, ?, ?, ?, ?, 'rig')",
            (item.repo, item.number, datetime.now(UTC).isoformat(), "observed", item.head, payload),
        )


def interact(conn: sqlite3.Connection, request: Interaction) -> None:
    """Reject decisions for a stale head, so an old review cannot cover new code."""
    row = conn.execute(
        "SELECT payload FROM triage_items WHERE repo = ? AND number = ?", (request.repo, request.number)
    ).fetchone()
    if row is None:
        raise ValueError("Item is not in the triage database; refresh first")
    item = decode_json(Item, str(row[0]))
    if request.head != item.head:
        raise ValueError("The PR head changed; refresh and review the new commit")
    if request.action in ("reviewed", "changes_requested") and item.state != "OPEN":
        raise ValueError("This item is already merged or closed")
    conn.execute(
        "INSERT INTO triage_events(repo, number, at, action, head, note) VALUES (?, ?, ?, ?, ?, ?)",
        (request.repo, request.number, datetime.now(UTC).isoformat(), request.action, request.head, request.note),
    )


def import_activity(conn: sqlite3.Connection, item: Item, activity: list[Activity]) -> None:
    """Deduplicate remote discussions across polls, retaining edits as separate observations."""
    for event in activity:
        conn.execute(
            "INSERT OR IGNORE INTO triage_events(repo, number, at, action, head, note, actor, source_key) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (item.repo, item.number, event.at, event.action, event.head, event.note, event.actor, event.key),
        )


def connect(path: Path) -> sqlite3.Connection:
    """Open the existing rig database and ensure its triage migration is present."""
    conn = get_db_connection(path)
    run_migrations(conn)
    return conn


def read_entries(path: Path) -> list[Entry]:
    """Convenience read for clients that already initialized the database."""
    with closing(connect(path)) as conn:
        return entries(conn)
