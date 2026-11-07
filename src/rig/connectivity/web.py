"""Read-only graph routes backed by a short-lived, local connectivity snapshot."""

from __future__ import annotations

import json
import time
from dataclasses import asdict
from http.server import BaseHTTPRequestHandler

import duckdb

from rig.connectivity.models import Graph
from rig.connectivity.service import build_graph
from rig.typed_json import decode_json
from rig.workspace.engine import config_file_path


def load_graph(*, refresh: bool = False) -> Graph:
    """Reuse the local snapshot for a minute; an explicit rescan always inventories again."""
    path = config_file_path().parent.parent / "temp" / "estate" / "connectivity.json"
    if not refresh and path.is_file() and time.time() - path.stat().st_mtime < 60:
        try:
            return decode_json(Graph, path.read_text(encoding="utf-8"))
        except ValueError:
            pass
    return build_graph()


def handle(handler: BaseHTTPRequestHandler, path: str) -> bool:
    """The existing /api/estate catalogue remains available to the studios."""
    if path not in {"/api/estate/graph", "/api/estate/graph/refresh"}:
        return False
    status = 200
    try:
        payload = json.dumps(asdict(load_graph(refresh=path.endswith("/refresh"))))
    except (OSError, ValueError, RuntimeError, duckdb.Error) as exc:
        status = 503
        payload = json.dumps({"error": str(exc)})
    data = payload.encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(data)))
    handler.send_header("Cache-Control", "no-store")
    handler.end_headers()
    handler.wfile.write(data)
    return True
