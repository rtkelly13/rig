"""Serialized triage operations shared by the CLI and rig web UI."""

from __future__ import annotations

import sqlite3
from contextlib import closing
from dataclasses import dataclass, replace
from pathlib import Path
from threading import RLock

from rig.db.connection import default_db_path
from rig.triage import github, markdown, store
from rig.triage.models import Activity, Entry, Interaction, Item
from rig.workspace.engine import config_file_path

LOCK = RLock()


@dataclass(frozen=True, slots=True)
class DiscussionImport:
    item: Item
    activity: list[Activity]


def queue_path() -> Path:
    """Regenerable local projection, outside the vault and version control."""
    return config_file_path().parent.parent / "temp" / "triage" / "queue.md"


def _project(conn: sqlite3.Connection, output: Path | None) -> list[Entry]:
    """Serialize CLI and web projections through the shared database's writer lock."""
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        result = store.entries(conn)
        markdown.export(output or queue_path(), result)
    return result


def refresh(db: Path | None = None, output: Path | None = None, config: Path | None = None) -> list[Entry]:
    """Fetch everything before committing. Failure retains all previous snapshots and Markdown."""
    with LOCK, closing(store.connect(db or default_db_path())) as conn:
        repos = github.repositories(config)
        items = [item for repo in repos for kind in ("pr", "issue") for item in github.fetch(repo, kind)]
        found = {item.ref for item in items}
        for entry in store.entries(conn):
            if entry.item.repo not in repos and entry.item.in_scope:
                items.append(replace(entry.item, in_scope=False))
            elif entry.item.repo in repos and entry.item.state == "OPEN" and entry.item.ref not in found:
                items.append(github.observe(entry.item))
        retained = {
            entry.item.ref
            for entry in store.entries(conn)
            if any(event.action in {"archived", "reviewed", "changes_requested"} for event in entry.events)
        }
        discussions = [
            DiscussionImport(item, github.activity(item)) for item in items if item.in_scope and item.ref in retained
        ]
        with conn:
            store.save(conn, items)
            for discussion in discussions:
                store.import_activity(conn, discussion.item, discussion.activity)
        return _project(conn, output)


def watch(repo: str, number: int, db: Path, output: Path | None = None) -> Item:
    """One bounded poll of the selected item; the caller controls the polling lifecycle."""
    with LOCK, closing(store.connect(db)) as conn:
        matches = [
            entry.item for entry in store.entries(conn) if entry.item.repo == repo and entry.item.number == number
        ]
        if not matches:
            raise ValueError("Unknown triage item")
        item = github.observe(matches[0])
        activity = github.activity(item)
        with conn:
            store.save(conn, [item])
            store.import_activity(conn, item, activity)
        _project(conn, output)
        return item


def record(request: Interaction, db: Path, output: Path | None = None) -> None:
    with LOCK, closing(store.connect(db)) as conn:
        if request.action in ("reviewed", "changes_requested"):
            matches = [
                entry.item for entry in store.entries(conn) if entry.item.ref == f"{request.repo}#{request.number}"
            ]
            if not matches:
                raise ValueError("Unknown triage item")
            with conn:
                store.save(conn, [github.observe(matches[0])])
            _project(conn, output)
        with conn:
            store.interact(conn, request)
        _project(conn, output)
