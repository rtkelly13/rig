"""The Python half of the rig web host: the mounted feature routes and the handlers not yet in C#.

The C# host (``src/Rig.Web``) owns the port and serves status, live reload, the estate, the SPA document, its
static files and the brand images; it forwards every other path here, to this server on a loopback port
(``desktop-manager web upstream``). Each #85 s25 slice moves a route group across.
"""

from __future__ import annotations

import json
import logging
import urllib.parse
from collections.abc import Sequence
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import override

from pydantic import JsonValue

from rig.c4_okf.web import serve_c4
from rig.connectivity.web import handle as handle_connectivity
from rig.triage.web import handle as handle_triage
from rig.web_host.models import BackupResponse
from rig.web_routes import Route, dispatch

logger = logging.getLogger(__name__)


class StudioRequestHandler(BaseHTTPRequestHandler):
    """HTTP request handler routing the forwarded API calls."""

    @property
    def env_db_path(self) -> Path | None:
        return self.server.db_path if isinstance(self.server, StudioServer) else None

    @override
    def log_message(self, format: str, *args: object) -> None:
        """Suppress standard HTTP server access logging to keep terminal output clean."""

    def _send_json(self, data: JsonValue, status: int = 200) -> None:
        payload = json.dumps(data, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "GET"):
            return
        if isinstance(self.server, StudioServer) and handle_triage(
            self, "GET", urllib.parse.urlparse(self.path).path, self.server.db_path
        ):
            return
        parsed_url = urllib.parse.urlparse(self.path)
        path = parsed_url.path

        # serve_c4 owns /c4/ and /api/c4/*, the experimental C4 viewer.
        if serve_c4(self, parsed_url) or handle_connectivity(self, path):
            return

        self.send_error(404, f"Endpoint not found: {path}")

    def do_POST(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "POST"):
            return
        if isinstance(self.server, StudioServer) and handle_triage(
            self, "POST", urllib.parse.urlparse(self.path).path, self.server.db_path
        ):
            return
        path = urllib.parse.urlparse(self.path).path

        # On-demand SQLite database backup
        if path == "/api/db/backup":
            from rig.db.backup import backup_database

            try:
                backup_file = backup_database(
                    db_path=self.env_db_path,
                    backup_dir=self.env_db_path.parent / "backups" if self.env_db_path else None,
                )
                self._send_json(BackupResponse(status="ok", backup_file=str(backup_file)).model_dump(mode="json"))
            except Exception as exc:
                logger.error("DB backup failed", exc_info=True)
                self._send_json({"error": f"Database backup failed: {exc}"}, status=500)
            return

        self.send_error(404, f"Endpoint not found: {path}")


class StudioServer(ThreadingHTTPServer):
    """Threading HTTP server for the rig web host."""

    # The C# host opens a connection per forwarded request, so a page load arrives as a burst of connects. The
    # socketserver default of 5 overflows, and Windows refuses a connect it cannot queue where Linux retries it.
    request_queue_size: int = 128

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8090,
        db_path: Path | None = None,
        routes: Sequence[Route] = (),
    ) -> None:
        self.routes: tuple[Route, ...] = tuple(routes)

        # Initialize and migrate SQLite database
        from rig.db import default_db_path, get_db_connection, run_migrations

        self.db_path: Path = db_path or default_db_path()
        conn = get_db_connection(db_path)
        run_migrations(conn)
        conn.close()

        super().__init__((host, port), StudioRequestHandler)
