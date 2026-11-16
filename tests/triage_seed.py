"""Database seed builders for the triage browser tests.

The browser tests drive the C# host (``Rig.Web.TriageRoutes``) over the shared rig database, so they need
snapshots and interaction events in the schema C# owns. These builders write the same snake-case payloads
``Rig.Core.ReviewQueue.QueueJson`` decodes. This is test scaffolding only; no production code imports it.
"""

from __future__ import annotations

import json
import sqlite3
from contextlib import closing
from dataclasses import asdict, dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Literal

from rig.sqlite import connect as connect_sqlite
from rig.sqlite import ensure_schema
from rig.typed_json import decode_json

Kind = Literal["pr", "issue"]
Action = Literal[
    "opened",
    "unread",
    "archived",
    "unarchived",
    "review_requested",
    "reviewed",
    "changes_requested",
    "deferred",
    "resumed",
    "note",
]


@dataclass(frozen=True, slots=True)
class Check:
    name: str
    state: str
    url: str


@dataclass(frozen=True, slots=True)
class Related:
    repo: str
    number: int
    title: str
    state: str
    kind: str


@dataclass(frozen=True, slots=True)
class StackMember:
    number: int
    title: str
    state: str
    position: int


@dataclass(frozen=True, slots=True)
class Stack:
    number: int
    size: int
    position: int
    base: str
    members: list[StackMember] = field(default_factory=list[StackMember])


@dataclass(frozen=True, slots=True)
class Facts:
    author: str = ""
    viewer_authored: bool = False
    assignees: list[str] = field(default_factory=list[str])
    labels: list[str] = field(default_factory=list[str])
    milestone: str = ""
    comments: int = 0
    review_decision: str = ""
    review_requested: bool = False
    reviewers: list[str] = field(default_factory=list[str])
    draft: bool = False
    mergeable: str = "UNKNOWN"
    base: str = ""
    head: str = ""
    cross_repository: bool = False
    checks_state: str = "UNKNOWN"
    checks: list[Check] = field(default_factory=list[Check])
    checks_total: int | None = None
    unresolved_threads: int = 0
    threads_total: int = 0
    state_reason: str = ""
    related: list[Related] = field(default_factory=list[Related])
    stack: Stack | None = None
    partial: list[str] = field(default_factory=list[str])


@dataclass(frozen=True, slots=True)
class Item:
    repo: str
    number: int
    kind: Kind
    title: str
    state: str
    created: str
    updated: str
    head: str = ""
    priority: int = 4
    readiness: int = 3
    lines: int = 0
    in_scope: bool = True
    body: str | None = None
    facts: Facts | None = None


@dataclass(frozen=True, slots=True)
class Interaction:
    repo: str
    number: int
    action: Action
    head: str
    note: str = ""


@dataclass(frozen=True, slots=True)
class Event:
    id: int
    at: str
    action: str
    head: str
    note: str
    actor: str


@dataclass(frozen=True, slots=True)
class Activity:
    key: str
    at: str
    actor: str
    action: str
    head: str
    note: str


@dataclass(frozen=True, slots=True)
class Entry:
    item: Item
    events: tuple[Event, ...]

    @property
    def archived(self) -> bool:
        """Archive is local; a new head or human GitHub discussion brings it back."""
        decisions = [event for event in self.events if event.action in ("archived", "unarchived")]
        if not decisions or decisions[-1].action != "archived" or decisions[-1].head != self.item.head:
            return False
        archived_at = datetime.fromisoformat(decisions[-1].at)
        return not any(
            event.action.startswith("github:")
            and "[bot]" not in event.actor
            and datetime.fromisoformat(event.at) > archived_at
            for event in self.events
        )


def entries(conn: sqlite3.Connection) -> list[Entry]:
    """Read every row the C# inbox will read, in the same order."""
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


def save(conn: sqlite3.Connection, items: list[Item]) -> None:
    """Write snapshots and observation events; the caller owns the transaction."""
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
    """Append a local decision, rejecting a stale head exactly as the C# writer does."""
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
    """Import remote discussion events, deduplicated by their source key."""
    for event in activity:
        conn.execute(
            "INSERT OR IGNORE INTO triage_events(repo, number, at, action, head, note, actor, source_key) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (item.repo, item.number, event.at, event.action, event.head, event.note, event.actor, event.key),
        )


def connect(path: Path) -> sqlite3.Connection:
    """Open the shared rig database and ensure the C#-owned triage tables exist."""
    conn = connect_sqlite(path)
    ensure_schema(conn)
    return conn


def read_entries(path: Path) -> list[Entry]:
    """Convenience read for a test that holds a database path."""
    with closing(connect(path)) as conn:
        return entries(conn)
