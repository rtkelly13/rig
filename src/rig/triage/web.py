"""Small HTTP adapter mounted in the existing rig web server."""

from __future__ import annotations

import json
import sqlite3
import subprocess
from contextlib import closing
from dataclasses import asdict, dataclass
from email.message import Message
from http.server import BaseHTTPRequestHandler
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig.triage import agents, service, store
from rig.triage.agents import AgentSignal
from rig.triage.models import Entry, Event, Interaction, Item, rank
from rig.typed_json import decode_json


@dataclass(frozen=True, slots=True)
class Watch:
    repo: str
    number: Annotated[int, Field(gt=0)]


@dataclass(frozen=True, slots=True)
class PostHeaders:
    headers: Message[str, str]

    def body_length(self) -> int:
        """Validate the standard HTTP header map at its boundary."""
        origin = self.headers.get("Origin")
        if origin and origin != f"http://{self.headers.get('Host')}":
            raise ValueError("Cross-origin triage writes are not allowed")
        if self.headers.get("Content-Type") != "application/json":
            raise ValueError("Expected application/json")
        length = int(self.headers.get("Content-Length", "0"))
        if not 0 < length <= 65536:
            raise ValueError("Invalid request size")
        return length


@dataclass(frozen=True, slots=True)
class Row:
    """One inbox entry as the SPA receives it."""

    item: Item
    events: tuple[Event, ...]
    agent: AgentSignal | None


def with_agents(items: list[Entry]) -> list[Row]:
    """Each entry plus `agent`: the wrap-up verdict of the worktree on its branch, or None.

    A failed scan leaves every `agent` None rather than failing the inbox.
    """
    try:
        found = agents.signals()
    except (OSError, ValueError, subprocess.SubprocessError):
        found = {}
    out: list[Row] = []
    for entry in items:
        facts = entry.item.facts
        branch = facts.head if facts is not None and entry.item.kind == "pr" and not facts.cross_repository else ""
        signal = agents.for_pr(found, entry.item.repo, branch)
        out.append(Row(entry.item, entry.events, signal))
    return out


def handle(handler: BaseHTTPRequestHandler, method: str, path: str, db: Path) -> bool:
    """Return False for routes owned by the rest of the dashboard."""
    if path != "/api/triage" and not path.startswith("/api/triage/"):
        return False
    status = 200
    try:
        if method == "POST":
            # Browser mutations must come from this dashboard, using JSON (no form requests).
            length = PostHeaders(handler.headers).body_length()
            body = handler.rfile.read(length).decode("utf-8")
            if path == "/api/triage/refresh":
                service.refresh(db)
            elif path == "/api/triage/watch":
                request = decode_json(Watch, body)
                service.watch(request.repo, request.number, db)
            elif path == "/api/triage/interact":
                service.record(decode_json(Interaction, body), db)
            else:
                raise ValueError("Unknown triage operation")
        elif method != "GET" or path != "/api/triage":
            raise ValueError("Unknown triage operation")
        with closing(store.connect(db)) as conn:
            items = store.entries(conn)
        items.sort(key=lambda entry: rank(entry.item))
        payload = json.dumps(
            {"entries": [asdict(row) for row in with_agents(items)], "queue_path": service.queue_path().as_posix()}
        )
    except (ValueError, OSError, sqlite3.Error, subprocess.SubprocessError) as error:
        status = 400 if isinstance(error, ValueError) else 503
        payload = json.dumps({"error": str(error)})
    data = payload.encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(data)))
    handler.send_header("Cache-Control", "no-store")
    handler.end_headers()
    handler.wfile.write(data)
    return True
