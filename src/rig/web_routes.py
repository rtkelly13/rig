"""SPA document routes, mounted API routes and the bounded frontend live-reload inventory.

Features that serve JSON from the rig web server do not import the server. They expose a `Route`, and the composition
root (`desktop_manager.web_cli`) hands the routes to `StudioServer`, which asks each one in turn before its own
handlers. The helpers here give every mounted route the same JSON envelope and the same write guard.
"""

from __future__ import annotations

import json
import urllib.parse
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler
from pathlib import Path
from typing import Protocol

_APP_PATHS = frozenset({"", "/", "/index.html", "/estate", "/studio", "/optical", "/triage", "/ops"})
_APP_PREFIXES = ("/triage/", "/repos/", "/estate/", "/ops/")
_LOOPBACK_HOSTS = frozenset({"127.0.0.1", "localhost", "::1"})
MAX_BODY_BYTES = 65536


class RouteNotFoundError(LookupError):
    """A mounted route owns the path but the thing it names does not exist (answered as 404)."""


@dataclass(frozen=True, slots=True)
class Request:
    """The parts of an HTTP request a mounted route needs, parsed once by the server."""

    method: str
    path: str
    query: dict[str, list[str]] = field(default_factory=dict[str, list[str]])

    @classmethod
    def parse(cls, method: str, target: str) -> Request:
        url = urllib.parse.urlsplit(target)
        return cls(method=method, path=url.path, query=urllib.parse.parse_qs(url.query))

    def param(self, name: str) -> str | None:
        """The last value given for a query parameter, or None when it is absent or empty."""
        values = self.query.get(name)
        return values[-1] if values and values[-1] else None

    def int_param(self, name: str, *, default: int, low: int, high: int) -> int:
        """A bounded integer query parameter; a malformed or out-of-range value is a 400, not a silent clamp."""
        raw = self.param(name)
        if raw is None:
            return default
        try:
            value = int(raw)
        except ValueError:
            raise ValueError(f"`{name}` must be an integer") from None
        if not low <= value <= high:
            raise ValueError(f"`{name}` must be between {low} and {high}")
        return value

    def flag(self, name: str) -> bool:
        return self.param(name) in {"1", "true", "yes"}


class Route(Protocol):
    """A mounted route: answer the request and return True, or return False to let the next handler try."""

    def __call__(self, handler: BaseHTTPRequestHandler, request: Request, /) -> bool: ...


def dispatch(routes: Sequence[Route], handler: BaseHTTPRequestHandler, method: str) -> bool:
    """Offer a request to each mounted route in turn; False means none of them owns it."""
    request = Request.parse(method, handler.path)
    return any(route(handler, request) for route in routes)


def send_json(handler: BaseHTTPRequestHandler, payload: str, status: int = 200) -> None:
    """Write an already-serialised JSON body that browsers and proxies must not cache."""
    data = payload.encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(data)))
    handler.send_header("Cache-Control", "no-store")
    handler.end_headers()
    handler.wfile.write(data)


def respond(
    handler: BaseHTTPRequestHandler,
    produce: Callable[[], str],
    *,
    unavailable: tuple[type[Exception], ...] = (OSError,),
) -> bool:
    """Run `produce` and send its JSON, mapping failures to the shared error envelope `{"error": ...}`.

    A `RouteNotFoundError` is a 404, a `ValueError` (bad input) a 400, and anything in `unavailable` (a store or
    service the route depends on) a 503. Anything else is a bug and propagates.
    """
    try:
        payload, status = produce(), 200
    except RouteNotFoundError as error:
        payload, status = json.dumps({"error": str(error)}), 404
    except ValueError as error:
        payload, status = json.dumps({"error": str(error)}), 400
    except unavailable as error:
        payload, status = json.dumps({"error": str(error)}), 503
    send_json(handler, payload, status)
    return True


def json_body(handler: BaseHTTPRequestHandler) -> str:
    """Read the body of a browser write, refusing anything that did not come from this dashboard.

    The Host must be a loopback name, so a page on another origin cannot reach the server through DNS rebinding;
    the Origin, when sent, must match it; and the body must be JSON, so a plain form post cannot trigger a write.
    Each refusal is a `ValueError`, which `respond` turns into a 400.

    A body of an acceptable size is read before any other check: a reply sent with the body still unread, and the
    socket then closed, is a connection reset on Windows, so the client would see a reset instead of the 400.
    """
    try:
        length = int(handler.headers.get("Content-Length", "0"))
    except ValueError:
        raise ValueError("Invalid request size") from None
    if not 0 < length <= MAX_BODY_BYTES:
        raise ValueError("Invalid request size")
    body = handler.rfile.read(length)
    host = handler.headers.get("Host") or ""
    if urllib.parse.urlsplit(f"//{host}").hostname not in _LOOPBACK_HOSTS:
        raise ValueError("Writes are only accepted on a loopback address")
    origin = handler.headers.get("Origin")
    if origin and origin != f"http://{host}":
        raise ValueError("Cross-origin writes are not allowed")
    if handler.headers.get("Content-Type") != "application/json":
        raise ValueError("Expected application/json")
    return body.decode("utf-8")


def serve_app(handler: BaseHTTPRequestHandler, path: str, templates: Path) -> bool:
    """Serve the same document for app deep links; leave API/static misses as 404s."""
    if path == "/static/triage.html":
        handler.send_response(302)
        handler.send_header("Location", "/triage/inbox")
        handler.send_header("Content-Length", "0")
        handler.end_headers()
        return True
    if path not in _APP_PATHS and not path.startswith(_APP_PREFIXES):
        return False
    document = templates / "index.html"
    if not document.is_file():
        handler.send_error(404, "Application document not found")
        return True
    content = document.read_bytes()
    handler.send_response(200)
    handler.send_header("Content-Type", "text/html; charset=utf-8")
    handler.send_header("Content-Length", str(len(content)))
    handler.send_header("Cache-Control", "no-store")
    handler.end_headers()
    handler.wfile.write(content)
    return True


def frontend_files(templates: Path) -> tuple[Path, ...]:
    """Both the compiled SPA and existing studio files participate in live reload."""
    return tuple(
        templates / name
        for name in (
            "index.html",
            "studio.js",
            "studio.css",
            "design_system/theme.css",
            "design_system/styles.css",
            "gallery.css",
            "app_assets/app.js",
            "app_assets/app.css",
        )
    )
