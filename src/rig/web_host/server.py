"""The Python half of the rig web host: the SPA, its static files and the mounted feature routes.

The C# host (``src/Rig.Web``) owns the port and serves status, live reload and the estate; it forwards every other
path here, to this server on a loopback port (``desktop-manager web upstream``). Each #85 s25 slice moves a route group across.
"""

from __future__ import annotations

import json
import logging
import mimetypes
import urllib.parse
from collections.abc import Sequence
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import ClassVar, override

from pydantic import JsonValue

from rig.c4_okf.web import serve_c4
from rig.connectivity.web import handle as handle_connectivity
from rig.triage.web import handle as handle_triage
from rig.web_host.models import BackupResponse
from rig.web_routes import Route, dispatch, serve_app

logger = logging.getLogger(__name__)


class StudioRequestHandler(BaseHTTPRequestHandler):
    """HTTP request handler routing REST API calls and static file delivery."""

    assets_root: ClassVar[Path]
    templates_dir: ClassVar[Path]

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

    def _send_file(self, file_path: Path, content_type: str | None = None) -> None:
        if not file_path.exists() or not file_path.is_file():
            self.send_error(404, f"File Not Found: {file_path.name}")
            return

        ctype = content_type or mimetypes.guess_type(str(file_path))[0] or "application/octet-stream"
        content = file_path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        self.end_headers()
        self.wfile.write(content)

    def _send_under(self, root: Path, rel_path: str, what: str) -> None:
        """Serve `rel_path` from inside `root`, refusing anything that resolves outside it."""
        target = (root / rel_path).resolve()
        if target.is_relative_to(root.resolve()) and target.is_file():
            self._send_file(target)
            return
        self.send_error(404, f"{what} not found: {rel_path}")

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
        if serve_c4(self, parsed_url) or handle_connectivity(self, path) or serve_app(self, path, self.templates_dir):
            return

        if path.startswith("/static/"):
            self._send_under(self.templates_dir, path[len("/static/") :], "Static file")
            return

        # Read-only brand assets, for the estate cards' logos and banners.
        if path.startswith("/media/assets/"):
            self._send_under(self.assets_root, urllib.parse.unquote(path[len("/media/assets/") :]), "Asset")
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

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8090,
        assets_root: Path | None = None,
        templates_dir: Path | None = None,
        db_path: Path | None = None,
        routes: Sequence[Route] = (),
    ) -> None:
        module_dir = Path(__file__).parent
        repo_root = module_dir.parent.parent.parent

        self.assets_root: Path = assets_root or (repo_root / "assets")
        self.templates_dir: Path = templates_dir or (module_dir / "templates")
        self.routes: tuple[Route, ...] = tuple(routes)

        # Initialize and migrate SQLite database
        from rig.db import default_db_path, get_db_connection, run_migrations

        self.db_path: Path = db_path or default_db_path()
        conn = get_db_connection(db_path)
        run_migrations(conn)
        conn.close()

        # Configure RequestHandler class variables
        StudioRequestHandler.assets_root = self.assets_root
        StudioRequestHandler.templates_dir = self.templates_dir

        super().__init__((host, port), StudioRequestHandler)
