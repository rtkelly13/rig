"""The Python half of the rig web host: the mounted feature routes and the handlers not yet in C#.

The C# host (``src/Rig.Web``) owns the port and serves status, live reload, the estate, the SPA document, its
static files, the brand images, triage, the estate's ops answers, telemetry and database backups; it forwards every
other path here, to this server on a loopback port (``python -m desktop_manager.web_upstream``). The jobs coordinator,
the C4 viewer and the estate graph are still here until their slices land.
"""

from __future__ import annotations

import urllib.parse
from collections.abc import Sequence
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import override

from rig.c4_okf.web import serve_c4
from rig.web_routes import Route, dispatch


class StudioRequestHandler(BaseHTTPRequestHandler):
    """HTTP request handler routing the forwarded API calls."""

    @override
    def log_message(self, format: str, *args: object) -> None:
        """Suppress standard HTTP server access logging to keep terminal output clean."""

    def do_GET(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "GET"):
            return
        parsed_url = urllib.parse.urlparse(self.path)
        path = parsed_url.path

        # serve_c4 owns /c4/ and /api/c4/*, the experimental C4 viewer.
        if serve_c4(self, parsed_url):
            return

        self.send_error(404, f"Endpoint not found: {path}")

    def do_POST(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "POST"):
            return
        path = urllib.parse.urlparse(self.path).path

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

        from rig.sqlite import connect, default_db_path, ensure_schema

        self.db_path: Path = db_path or default_db_path()
        conn = connect(self.db_path)
        ensure_schema(conn)
        conn.close()

        super().__init__((host, port), StudioRequestHandler)
